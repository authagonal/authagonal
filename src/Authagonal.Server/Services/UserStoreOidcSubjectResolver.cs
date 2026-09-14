using System.Collections.Generic;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Protocol;
using Authagonal.Server.Services.Oidc;
using Microsoft.Extensions.Logging;

namespace Authagonal.Server.Services;

/// <summary>
/// Server's <see cref="IOidcSubjectResolver"/>: maps an authenticated
/// <see cref="ClaimsPrincipal"/> to an <see cref="OidcSubject"/> by looking up the
/// corresponding <see cref="AuthUser"/> in the user store and inflating groups from
/// the SCIM group store. On refresh, re-reads the user to pick up deactivation,
/// role changes, and fresh group membership — the token endpoint then mints against
/// this fresh subject, so nothing survives deactivation across a refresh.
/// </summary>
public sealed class UserStoreOidcSubjectResolver(
    IUserStore userStore,
    IScimGroupStore scimGroupStore,
    IScimGroupRoleMappingStore groupRoleMappingStore,
    IClientStore clientStore,
    IOidcProviderStore oidcProviderStore,
    OidcDiscoveryClient discoveryClient,
    ISecretProvider secretProvider,
    IHttpClientFactory httpClientFactory,
    ILogger<UserStoreOidcSubjectResolver> logger,
    IUpstreamRefreshTokenStore? upstreamTokenStore = null,
    // Trailing and optional so every existing construction — the DI registration, the device grant,
    // the test helpers — keeps compiling. Null means organizations are not wired at all, and the
    // subject's OrganizationId is read straight off the account exactly as it was before they
    // existed. AddAuthagonalCore registers one, so on a real host this is never null.
    OrganizationSelector? organizationSelector = null) : IOidcSubjectResolver
{
    public async Task<OidcSubjectResult> ResolveAsync(
        ClaimsPrincipal authenticatedPrincipal,
        OidcSubjectResolutionContext context,
        CancellationToken ct = default)
    {
        var subjectId = authenticatedPrincipal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? authenticatedPrincipal.FindFirstValue("sub");

        if (string.IsNullOrWhiteSpace(subjectId))
            return OidcSubjectResult.Reject(OidcRejection.LoginRequired, "No subject claim on principal");

        var user = await userStore.GetAsync(subjectId, ct);
        if (user is null || !user.IsActive)
            return OidcSubjectResult.Reject(OidcRejection.AccessDenied, "User not found or inactive");

        var client = await clientStore.GetAsync(context.ClientId, ct);

        // Propagate the upstream-federation cap captured by the cookie (session_max_exp).
        // This is set at sign-in time when an IdP asserts a session lifetime.
        DateTimeOffset? sessionMaxExpiresAt = null;
        var sessionMaxExpClaim = authenticatedPrincipal.FindFirstValue("session_max_exp");
        if (!string.IsNullOrEmpty(sessionMaxExpClaim) &&
            long.TryParse(sessionMaxExpClaim, out var sessionMaxExpSeconds))
        {
            sessionMaxExpiresAt = DateTimeOffset.FromUnixTimeSeconds(sessionMaxExpSeconds);
        }

        var sessionId = authenticatedPrincipal.FindFirstValue("sid");

        // When this session was actually authenticated. Minted on every sign-in since 0.11.0 but never
        // carried past the cookie, so no token could ever emit auth_time and max_age had nothing to
        // compare against — while claims_supported advertised auth_time regardless.
        DateTimeOffset? authTime = null;
        var authTimeClaim = authenticatedPrincipal.FindFirstValue(CookieSignInHelper.AuthTimeClaim);
        if (!string.IsNullOrEmpty(authTimeClaim) && long.TryParse(authTimeClaim, out var authTimeSeconds))
            authTime = DateTimeOffset.FromUnixTimeSeconds(authTimeSeconds);

        // Federation claims captured at the OIDC callback ride on the cookie as
        // `federated:<name>` claims. Pass them through OidcSubject.FederationClaims
        // so ProtocolTokenService's scope-gated emission re-releases them on the
        // Authagonal-issued token, and so they survive refresh rotations distinct
        // from per-user CustomAttributes (which we re-read fresh on refresh).
        var federationClaims = ExtractFederationClaims(authenticatedPrincipal);

        // Upstream-federated refresh: the token rode the cookie from the federation callback and is also
        // seeded into IUpstreamRefreshTokenStore keyed by (user, connection, sid). Prefer the STORE — it
        // holds the latest rotated token shared by every RP grant for this session, so a new authorize
        // doesn't seed a grant from a cookie copy the upstream already rotated to death. The cookie is the
        // fallback (first authorize before any refresh, or no store registered). Non-emitted.
        var upstreamRefreshToken = authenticatedPrincipal.FindFirstValue("upstream_refresh_token");
        var upstreamConnectionId = authenticatedPrincipal.FindFirstValue("upstream_connection_id");
        if (upstreamTokenStore is not null && !string.IsNullOrEmpty(upstreamConnectionId) && !string.IsNullOrEmpty(sessionId))
        {
            var stored = await upstreamTokenStore.GetAsync(subjectId, upstreamConnectionId, sessionId, ct);
            if (!string.IsNullOrEmpty(stored))
                upstreamRefreshToken = stored;
        }

        // The organisation an ORG-SCOPED SSO connection authenticated this session for. Proven, not
        // asserted by the caller, so it outranks the `organization` parameter and the client's
        // restriction inside the selector — see OrganizationSelector's precedence list.
        var connectionOrganizationId =
            authenticatedPrincipal.FindFirstValue(CookieSignInHelper.ConnectionOrganizationClaim);

        try
        {
            var subject = await BuildSubjectAsync(
                user, client, sessionMaxExpiresAt, sessionId, federationClaims,
                upstreamRefreshToken, upstreamConnectionId, authTime, ct,
                requestedOrganization: context.RequestedOrganization,
                connectionOrganizationId: connectionOrganizationId);
            return OidcSubjectResult.Allow(subject);
        }
        catch (OrganizationAccessDeniedException ex)
        {
            // Refused rather than downgraded to a token with no organization: the relying party asked
            // for one, and a token that silently names none would be read as "this user belongs
            // nowhere" rather than "you may not have this".
            // The selector has already logged the detail (Debug carries the caller-supplied value,
            // Information does not), and ex.Message is written by the selector rather than echoed from
            // the request — so it is safe to return, and safe to log here at Debug only.
            logger.LogDebug(
                "Refusing authorization for {SubjectId} on client {ClientId}: {Reason}",
                subjectId, context.ClientId, ex.Message);
            return OidcSubjectResult.Reject(ex.Rejection, ex.Message);
        }
    }

    private const string FederationClaimPrefix = "federated:";

    private static Dictionary<string, string> ExtractFederationClaims(ClaimsPrincipal principal)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var claim in principal.Claims)
        {
            if (!claim.Type.StartsWith(FederationClaimPrefix, StringComparison.Ordinal))
                continue;
            var name = claim.Type[FederationClaimPrefix.Length..];
            if (string.IsNullOrEmpty(name)) continue;
            result[name] = claim.Value;
        }
        return result;
    }

    public async Task<OidcSubjectResult> ResolveRefreshAsync(
        OidcSubject priorSubject,
        OidcSubjectResolutionContext context,
        CancellationToken ct = default)
    {
        var user = await userStore.GetAsync(priorSubject.SubjectId, ct);
        if (user is null || !user.IsActive)
            return OidcSubjectResult.Reject(OidcRejection.AccessDenied, "User not found or inactive");

        var client = await clientStore.GetAsync(context.ClientId, ct);

        // Upstream-federated refresh (Option A): the point of this path. Redeem the stored upstream
        // refresh token against its IdP. If the upstream refuses (invalid_grant — the federated
        // credential, e.g. a share link, was revoked or expired), reject the local refresh too so the
        // session dies; on success, carry the rotated token forward into the successor grant. A transient
        // failure leaves the session alive (the SessionMaxExpiresAt cap still bounds it).
        // Read the latest upstream token from the shared store (rotated by whichever RP refreshed last),
        // falling back to the copy pinned on this grant. Redeem it; on rotation, write the new token back
        // to the store so sibling RP grants see it; on revocation, drop it.
        var upstreamRefreshToken = priorSubject.UpstreamRefreshToken;
        if (upstreamTokenStore is not null && !string.IsNullOrEmpty(priorSubject.UpstreamConnectionId) && !string.IsNullOrEmpty(priorSubject.SessionId))
        {
            var stored = await upstreamTokenStore.GetAsync(priorSubject.SubjectId, priorSubject.UpstreamConnectionId, priorSubject.SessionId, ct);
            if (!string.IsNullOrEmpty(stored))
                upstreamRefreshToken = stored;
        }
        // Said out loud when the control cannot run at all.
        //
        // RevalidateOnRefresh is opt-in, and an operator who turns it on has decided that upstream revocation
        // must reach this session. With no upstream refresh token there is nothing to redeem, so the control
        // silently becomes a no-op — at no log level — and the deployment believes offboarding is enforced when
        // it is not. The usual cause is an app registration that never consented offline_access, or an upstream
        // that declines it for this flow, so it is a configuration fault with a fix rather than a transient.
        // The adjacent SessionExpClaim control already warns for exactly the same shape of failure.
        if (!string.IsNullOrEmpty(priorSubject.UpstreamConnectionId)
            && string.IsNullOrEmpty(upstreamRefreshToken))
        {
            // The connection is only loaded in this degraded branch, so the extra read costs nothing on a
            // healthy refresh — and nothing at all for a non-federated one, which has no connection id.
            // Since the connection id rides every OIDC-federated cookie (not only the RevalidateOnRefresh
            // ones), this is one point read per refresh of a federated session that holds no upstream
            // token; the warning below still fires only for a connection that asked to revalidate.
            OidcProviderConfig? degradedConfig = null;
            try { degradedConfig = await oidcProviderStore.GetAsync(priorSubject.UpstreamConnectionId, ct); }
            catch (Exception ex)
            {
                logger.LogDebug(ex,
                    "Could not load connection {ConnectionId} to report upstream-revalidation state",
                    priorSubject.UpstreamConnectionId);
            }

            if (degradedConfig?.RevalidateOnRefresh == true)
                logger.LogWarning(
                    "RevalidateOnRefresh is enabled for connection {ConnectionId} but no upstream refresh token "
                    + "is held for subject {SubjectId}, so the upstream was NOT re-checked on this refresh. "
                    + "Upstream revocation will not reach this session. The usual cause is an app registration "
                    + "that never received offline_access from the identity provider.",
                    priorSubject.UpstreamConnectionId, priorSubject.SubjectId);
        }

        if (!string.IsNullOrEmpty(upstreamRefreshToken) && !string.IsNullOrEmpty(priorSubject.UpstreamConnectionId))
        {
            var (outcome, rotated) = await RedeemUpstreamRefreshAsync(
                priorSubject.UpstreamConnectionId, upstreamRefreshToken, ct);
            if (outcome == UpstreamRefreshOutcome.Revoked)
            {
                if (upstreamTokenStore is not null && !string.IsNullOrEmpty(priorSubject.SessionId))
                    await upstreamTokenStore.RemoveAsync(priorSubject.SubjectId, priorSubject.UpstreamConnectionId, priorSubject.SessionId, ct);
                return OidcSubjectResult.Reject(
                    OidcRejection.AccessDenied,
                    "Upstream session ended (the federated credential was revoked or has expired).");
            }
            if (outcome == UpstreamRefreshOutcome.Valid)
            {
                upstreamRefreshToken = rotated;
                if (upstreamTokenStore is not null && !string.IsNullOrEmpty(priorSubject.SessionId) && !string.IsNullOrEmpty(rotated))
                    await upstreamTokenStore.SetAsync(
                        priorSubject.SubjectId, priorSubject.UpstreamConnectionId!, priorSubject.SessionId!, rotated!,
                        priorSubject.SessionMaxExpiresAt ?? DateTimeOffset.UtcNow.AddDays(7), ct);
            }
        }

        // Preserve the federation cap, session id, and federation claims across rotations
        // — the resolver can't re-read any of them from the cookie at refresh time, and
        // they must survive rotations so the cap can't be lifted, back-channel logouts can
        // correlate, and federation-derived claims keep flowing onto refreshed tokens.
        //
        // auth_time rides along for the same reason and one of its own: refreshing is not
        // authenticating, so the value must NOT advance. Bumping it here would let any client hold a
        // session open past every max_age its RPs demand simply by refreshing.
        // The organization the grant was issued for, carried across the rotation — and the reason it
        // has to be carried at all is that everything else here is rebuilt from the user store. Without
        // it, a session that selected organization A through the `organization` parameter would
        // silently revert to the account's own default on its FIRST refresh, roughly one access-token
        // lifetime after login, and hand the relying party another customer's org_id with no error
        // anywhere.
        //
        // Carried only for a selection the REQUEST made. One the account merely supplied is re-derived
        // every rotation, which is what this resolver has always done and is what keeps an operator
        // re-tagging an account taking effect instead of being pinned by a month-old refresh chain.
        // The distinction cannot be recovered by comparing against the account's current value —
        // after a re-tag the carried and current values differ in BOTH cases — so it rides the grant.
        var carriedOrganization = priorSubject.OrganizationExplicitlySelected
            ? priorSubject.OrganizationId
            : null;

        try
        {
            var subject = await BuildSubjectAsync(
                user, client,
                priorSubject.SessionMaxExpiresAt,
                priorSubject.SessionId,
                priorSubject.FederationClaims,
                upstreamRefreshToken,
                priorSubject.UpstreamConnectionId,
                priorSubject.AuthTime,
                ct,
                carriedOrganizationId: carriedOrganization);
            return OidcSubjectResult.Allow(subject);
        }
        catch (OrganizationAccessDeniedException ex)
        {
            // This is where revoking a membership, disabling an organization or narrowing a client's
            // allowed organizations actually takes effect: rejecting here revokes the refresh chain,
            // so the grant dies rather than re-minting for up to the absolute refresh lifetime.
            logger.LogInformation(
                "Refusing refresh for {SubjectId} on client {ClientId}: {Reason}",
                priorSubject.SubjectId, context.ClientId, ex.Message);
            return OidcSubjectResult.Reject(ex.Rejection, ex.Message);
        }
    }

    private enum UpstreamRefreshOutcome { Valid, Revoked, Transient }

    /// <summary>
    /// Redeems the upstream refresh token at its connection's token endpoint. <see cref="UpstreamRefreshOutcome.Valid"/>
    /// (with the rotated token, or the same token if the upstream didn't rotate) on success;
    /// <see cref="UpstreamRefreshOutcome.Revoked"/> on a 4xx (invalid_grant — credential gone); and
    /// <see cref="UpstreamRefreshOutcome.Transient"/> on any transport/5xx/config error, which keeps the
    /// session alive (bounded by SessionMaxExpiresAt) rather than killing it over a blip.
    /// </summary>
    private async Task<(UpstreamRefreshOutcome Outcome, string? RotatedToken)> RedeemUpstreamRefreshAsync(
        string connectionId, string refreshToken, CancellationToken ct)
    {
        OidcProviderConfig? config;
        try
        {
            config = await oidcProviderStore.GetAsync(connectionId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Upstream-refresh: could not load connection {ConnectionId}; treating as transient", connectionId);
            return (UpstreamRefreshOutcome.Transient, null);
        }
        if (config is null)
        {
            logger.LogWarning("Upstream-refresh: connection {ConnectionId} no longer exists; treating as transient", connectionId);
            return (UpstreamRefreshOutcome.Transient, null);
        }

        string tokenEndpoint;
        string clientSecret;
        try
        {
            var discovery = await discoveryClient.GetDiscoveryAsync(config.MetadataLocation, ct);
            tokenEndpoint = discovery.TokenEndpoint;
            clientSecret = await secretProvider.ResolveAsync(config.ClientSecret, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Upstream-refresh: discovery/secret failed for {ConnectionId}; treating as transient", connectionId);
            return (UpstreamRefreshOutcome.Transient, null);
        }

        HttpResponseMessage response;
        string body;
        try
        {
            var client = httpClientFactory.CreateClient("OidcDiscovery");
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = config.ClientId,
                ["client_secret"] = clientSecret,
            });
            response = await client.PostAsync(tokenEndpoint, content, ct);
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Upstream-refresh: token request failed for {ConnectionId}; treating as transient", connectionId);
            return (UpstreamRefreshOutcome.Transient, null);
        }

        if (response.IsSuccessStatusCode)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var rotated = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
                // No rotation → keep redeeming the same token next time.
                return (UpstreamRefreshOutcome.Valid, string.IsNullOrEmpty(rotated) ? refreshToken : rotated);
            }
            catch
            {
                return (UpstreamRefreshOutcome.Valid, refreshToken);
            }
        }

        // Only error=invalid_grant means the refresh token itself is gone/revoked — fail closed so
        // revocation propagates. Any OTHER 4xx is an operator/config fault, NOT proof the user's
        // session ended: invalid_client (a rotated or misconfigured client secret), invalid_request,
        // unauthorized_client, a 429, etc. Treating those as revocation would mass-terminate EVERY
        // federated session on this connection at once. Keep the session (bounded by the absolute
        // session cap) and surface the fault in logs. 5xx is transient too. An unparseable/absent
        // error body is treated as transient (fail open) rather than revoking on ambiguity.
        if ((int)response.StatusCode is >= 400 and < 500)
        {
            var error = TryReadOAuthError(body);
            if (string.Equals(error, "invalid_grant", StringComparison.Ordinal))
            {
                logger.LogInformation("Upstream-refresh: connection {ConnectionId} returned invalid_grant; ending the local session", connectionId);
                return (UpstreamRefreshOutcome.Revoked, null);
            }

            logger.LogWarning("Upstream-refresh: connection {ConnectionId} returned {Status} (error={Error}); treating as transient, session kept", connectionId, (int)response.StatusCode, error ?? "none");
            return (UpstreamRefreshOutcome.Transient, null);
        }

        logger.LogWarning("Upstream-refresh: connection {ConnectionId} returned {Status}; treating as transient", connectionId, (int)response.StatusCode);
        return (UpstreamRefreshOutcome.Transient, null);
    }

    // Extract the RFC 6749 `error` code from an OAuth token-endpoint error response. Returns null if
    // the body is empty or not the expected JSON object, in which case the caller treats it as transient.
    private static string? TryReadOAuthError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var e)
                    ? e.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds an <see cref="OidcSubject"/> from an <see cref="AuthUser"/>. Exposed for
    /// device-code and admin token paths that already know the subject and don't go
    /// through the authorize endpoint.
    /// </summary>
    /// <param name="requestedOrganization">
    /// The <c>organization</c> authorize parameter, when the request carried one. Caller-supplied, so
    /// it resolves as a slug first and then as an id. Null on the device and admin-mint paths, which
    /// name none and fall through to the client restriction and then the account's own default.
    /// Trailing and after <paramref name="ct"/> deliberately, so every existing positional caller keeps
    /// compiling — the same reason <c>ProtocolTokenService.MintAccessTokenAsync</c> puts its
    /// forced-claims argument there.
    /// </param>
    /// <param name="carriedOrganizationId">
    /// The organisation a prior grant was issued for, on the refresh path. An id this server minted,
    /// so it resolves by id only — never as a slug, or a later organisation taking that value as its
    /// slug would capture the grant.
    /// </param>
    /// <param name="connectionOrganizationId">
    /// The organisation an org-scoped SSO connection authenticated this session for, off the cookie.
    /// Trailing for the same reason as the two above — every existing positional caller keeps
    /// compiling. Null on the device and admin-mint paths, which have no federation cookie to read.
    /// </param>
    /// <exception cref="OrganizationAccessDeniedException">
    /// The organisation does not exist, is disabled, is not permitted for this client, or the user is
    /// not an active member of it. Both resolver entry points turn this into an
    /// <see cref="OidcSubjectResult.Rejected"/>; the device grant turns it into an OAuth error.
    /// </exception>
    public async Task<OidcSubject> BuildSubjectAsync(
        AuthUser user,
        OAuthClient? client,
        DateTimeOffset? sessionMaxExpiresAt = null,
        string? sessionId = null,
        IReadOnlyDictionary<string, string>? federationClaims = null,
        string? upstreamRefreshToken = null,
        string? upstreamConnectionId = null,
        DateTimeOffset? authTime = null,
        CancellationToken ct = default,
        string? requestedOrganization = null,
        string? carriedOrganizationId = null,
        string? connectionOrganizationId = null)
    {
        // SCIM group → role mappings (empty store = no-op). Fetch the user's groups once,
        // used for both the optional groups claim and effective-role resolution.
        var mappings = await groupRoleMappingStore.GetAllAsync(ct);
        IReadOnlyList<ScimGroup>? scimGroups = null;
        if (mappings.Count > 0 || client is { IncludeGroupsInTokens: true })
            scimGroups = await scimGroupStore.GetGroupsByUserIdAsync(user.Id, ct);

        IReadOnlyList<string>? groups = null;
        if (client is { IncludeGroupsInTokens: true } && scimGroups is { Count: > 0 })
            groups = scimGroups.Select(g => g.DisplayName).ToList();

        // Effective roles = directly-assigned ∪ roles granted by the user's group memberships.
        var roles = new HashSet<string>(user.Roles, StringComparer.Ordinal);
        if (mappings.Count > 0 && scimGroups is { Count: > 0 })
        {
            var memberGroupIds = scimGroups.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var m in mappings)
                if (memberGroupIds.Contains(m.GroupId))
                    roles.Add(m.Role);
        }

        // Which organization this token is for. With no selector wired — a host constructing this
        // resolver by hand — the account's own field is the answer, which is what it has always been.
        var organization = organizationSelector is null
            ? new OrganizationSelection { OrganizationId = user.OrganizationId }
            : await organizationSelector.SelectAsync(
                user, client, requestedOrganization, carriedOrganizationId, ct, connectionOrganizationId);

        // …and the roles that organization grants, unioned in last. Only an EXPLICITLY selected
        // organization with an ACTIVE membership contributes any (see OrganizationSelector), so a
        // request that named no organization produces exactly the set it produced before organizations
        // existed — the union is over an empty list and the HashSet is untouched.
        if (organization.MembershipRoles is { Count: > 0 } organizationRoles)
        {
            foreach (var role in organizationRoles)
                roles.Add(role);
        }

        return new OidcSubject
        {
            SubjectId = user.Id,
            Email = user.Email,
            EmailVerified = user.EmailConfirmed,
            GivenName = user.FirstName,
            FamilyName = user.LastName,
            Phone = user.Phone,
            Locale = user.Locale,
            OrganizationId = organization.OrganizationId,
            OrganizationSlug = organization.Slug,
            OrganizationName = organization.DisplayName,
            OrganizationExplicitlySelected = organization.ExplicitlySelected,
            Roles = roles.Count > 0 ? roles.ToList() : null,
            Groups = groups,
            CustomAttributes = user.CustomAttributes.Count > 0
                ? user.CustomAttributes.ToDictionary(kv => kv.Key, kv => kv.Value)
                : null,
            FederationClaims = federationClaims is { Count: > 0 } ? federationClaims : null,
            SessionMaxExpiresAt = sessionMaxExpiresAt,
            SessionId = sessionId,
            AuthTime = authTime,
            UpstreamRefreshToken = upstreamRefreshToken,
            UpstreamConnectionId = upstreamConnectionId,
        };
    }
}
