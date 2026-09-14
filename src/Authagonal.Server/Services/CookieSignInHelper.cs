using System.Security.Claims;
using Authagonal.Core.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Authagonal.Server.Services;

public static class CookieSignInHelper
{
    /// <summary>Cookie claim asserting the session completed multi-factor authentication (or was
    /// established via an external IdP that owns authentication). Required at /connect/authorize for
    /// MFA-enrolled users.</summary>
    public const string MfaAuthenticatedClaim = "mfa_authenticated";

    /// <summary>Cookie claim (Unix seconds) recording when this session was established by an actual
    /// authentication. Set on every real sign-in and never bumped by sliding-cookie renewal, so
    /// /connect/authorize can honor prompt=login by requiring a session newer than the request.</summary>
    public const string AuthTimeClaim = "auth_time";

    /// <summary>
    /// Cookie claim naming the organisation an ORG-SCOPED SSO connection authenticated this session
    /// for. Absent on every other sign-in.
    /// </summary>
    /// <remarks>
    /// Distinct from the <c>org_id</c> claim, which this helper also mints and which may equally come
    /// from the account's own <see cref="AuthUser.OrganizationId"/>. The two are not interchangeable:
    /// this one is an ASSERTION — the user proved their identity at an IdP that belongs to exactly one
    /// organisation — and <c>OrganizationSelector</c> lets it outrank the <c>organization</c> parameter
    /// and the client's restriction because of it. A claim that could also be a legacy account tag could
    /// not be given that authority.
    /// <para>
    /// It rides through the MFA park/resume path in <see cref="PendingFederatedSession"/> like every
    /// other federation binding, and it is what <c>org_id</c> is minted from below when present — so a
    /// user with a legacy account tag who signs in through an org-scoped connection gets the
    /// connection's organisation, not the tag's.
    /// </para>
    /// </remarks>
    public const string ConnectionOrganizationClaim = "connection_org_id";

    /// <summary>
    /// Authentication-property key recording when this session began, in Unix seconds.
    /// </summary>
    /// <remarks>
    /// Distinct from <c>Properties.IssuedUtc</c>, which sliding renewal rewrites on every refresh —
    /// making it useless as an absolute-lifetime reference. This value is written once at sign-in and
    /// never touched again, so the 7-day cap can actually fire.
    /// </remarks>
    public const string SessionStartedProperty = "session_started";

    /// <summary>Reads <see cref="SessionStartedProperty"/>, or null when the session predates it.</summary>
    public static DateTimeOffset? SessionStartedAt(AuthenticationProperties? properties)
    {
        var raw = properties?.GetString(SessionStartedProperty);
        return long.TryParse(raw, out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    }

    /// <summary>
    /// Stamps <see cref="SessionStartedProperty"/> with now, unless it is already set.
    /// </summary>
    /// <remarks>
    /// Idempotent because the absolute cap is only a cap while this value is written ONCE. Re-stamping on
    /// a renewal, or on a second sign-in that reuses an existing property bag, would slide the 7-day
    /// deadline forward on every request — which is exactly the defect that made the cap dead code when
    /// it read <c>Properties.IssuedUtc</c>.
    /// </remarks>
    public static void MarkSessionStart(AuthenticationProperties properties)
    {
        if (SessionStartedAt(properties) is null)
            properties.SetString(SessionStartedProperty, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
    }

    /// <param name="extraClaims">
    /// Claims to merge onto the session — the federation bindings of a login that was parked on an MFA
    /// challenge. See <see cref="PendingFederatedSession"/> for what was lost without them.
    /// </param>
    /// <param name="cookieExpiresUtc">
    /// An upstream IdP's stated session bound, so the local cookie cannot outlive the authentication behind it.
    /// </param>
    public static async Task SignInAsync(
        HttpContext httpContext,
        AuthUser user,
        bool mfaAuthenticated = false,
        IEnumerable<Claim>? extraClaims = null,
        DateTimeOffset? cookieExpiresUtc = null,
        string? sessionId = null)
    {
        var name = $"{user.FirstName} {user.LastName}".Trim();

        // An org-scoped connection's organisation OVERRIDES the account's own tag: the session was
        // authenticated at that organisation's IdP, and the tag is a downstream provisioning artefact
        // that may name a different one. Read from the parked claims because this method is also the
        // resume path for a federated login that was held on an MFA challenge.
        var connectionOrganizationId = extraClaims?
            .FirstOrDefault(c => c.Type == ConnectionOrganizationClaim)?.Value;
        var organizationId = string.IsNullOrWhiteSpace(connectionOrganizationId)
            ? user.OrganizationId
            : connectionOrganizationId;

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new("sub", user.Id),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Name, string.IsNullOrWhiteSpace(name) ? user.Email : name),
            new("security_stamp", user.SecurityStamp ?? ""),
            // The callback's sid when a federated login parked one: the upstream refresh token is stored under
            // a per-(user, connection, sid) key at callback time, so minting a fresh one here orphaned it.
            new("sid", sessionId ?? Guid.NewGuid().ToString("N")),
            new(AuthTimeClaim, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString())
        };

        if (mfaAuthenticated)
            claims.Add(new Claim(MfaAuthenticatedClaim, "true"));

        if (!string.IsNullOrWhiteSpace(organizationId))
            claims.Add(new Claim("org_id", organizationId));

        // Merged, not overwritten, and only for types this helper did not already mint — so a parked
        // `sid`/`sub` cannot displace the ones above, while `saml_name_id`, `session_max_exp`,
        // `upstream_refresh_token` and the `federated:*` passthrough all land.
        if (extraClaims is not null)
        {
            var minted = new HashSet<string>(claims.Select(c => c.Type), StringComparer.Ordinal);
            foreach (var extra in extraClaims)
                if (!minted.Contains(extra.Type))
                    claims.Add(extra);
        }

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        var properties = new AuthenticationProperties();
        MarkSessionStart(properties);

        // The IdP's bound, when it stated one. Without this a federated user with MFA got a session that
        // outlived the authentication behind it.
        if (cookieExpiresUtc is { } bound && bound < DateTimeOffset.UtcNow.AddDays(30))
        {
            properties.ExpiresUtc = bound;
            properties.IsPersistent = true;
        }

        await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, properties);
    }

    public static string GetDisplayName(AuthUser user)
    {
        return $"{user.FirstName} {user.LastName}".Trim();
    }
}
