using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Protocol.Models;
using Authagonal.Protocol.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Authagonal.Server.Services;

/// <summary>
/// Which <see cref="Organization"/> a request is for BEFORE anyone has authenticated — the question
/// home-realm discovery, the login page's provider list and <c>/sso-check</c> all have to answer, and
/// the one <see cref="OrganizationSelector"/> cannot: that runs after sign-in, against a user.
/// </summary>
/// <remarks>
/// Precedence, highest first:
/// <list type="number">
///   <item>The <c>organization</c> authorize parameter (accepted as a slug or an id, slug first —
///   see <see cref="OrganizationSelector.ResolveBySlugThenIdAsync"/>).</item>
///   <item><see cref="OAuthClient.RestrictedToOrganizationIds"/> when it holds exactly ONE entry. Two
///   or more is not a selection: the client serves several and the request named none, which
///   <see cref="OrganizationSelector"/> already answers with <c>account_selection_required</c> once
///   the user is known.</item>
///   <item><see cref="ITenantContext.OrganizationId"/> — a host that pins one per request (a
///   per-organisation hostname or custom domain). Null in every single-tenant deployment.</item>
/// </list>
/// <para>
/// It only ever NARROWS what a request may reach. The organisation must exist and be
/// <see cref="Organization.Enabled"/>, and the client's restriction must permit it; anything else
/// resolves to null, which means "no organisation" and leaves every caller on the tenant-wide path it
/// took before organisations existed. In particular a parameter naming an organisation this client is
/// restricted away from resolves to null here rather than erroring: refusing pre-authentication would
/// move the refusal in front of the login screen and change which requests can be told apart, while
/// falling through leaves it exactly where it already is — <see cref="OrganizationSelector"/>, which
/// raises <c>access_denied</c> once there is a user to refuse.
/// </para>
/// <para>
/// A second entry point, <see cref="ResolveForLoginPageAsync"/>, answers the question for the login
/// document itself, which has no authorize parameters of its own and so reads them back out of the
/// request it stands in for. It has its own precedence and its own, looser tests; see its remarks.
/// </para>
/// </remarks>
/// <param name="grantStore">
/// Where a pushed authorization request is read from by <see cref="ResolveForLoginPageAsync"/>. Null
/// switches that leg of the login-page resolution off. Optional so a resolver built by hand with the
/// three original arguments keeps working.
/// </param>
/// <param name="clients">
/// Where the client of a login page's authorize request is read from, for the single-entry client
/// restriction, by <see cref="ResolveForLoginPageAsync"/>. Null switches that leg off.
/// </param>
public sealed class PreAuthOrganizationResolver(
    IOrganizationStore organizations,
    ITenantContext tenantContext,
    ILogger<PreAuthOrganizationResolver> logger,
    IGrantStore? grantStore = null,
    IClientStore? clients = null)
{
    /// <summary>
    /// The authorize-request parameter names that carry an organisation, in the order they are read.
    /// </summary>
    private static readonly string[] OrganizationParameters = ["organization", "org_slug", "org_id"];

    private static readonly JsonSerializerOptions PushedRequestDeserializeOptions =
        new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// The organisation this request is for, or null when it is for none.
    /// </summary>
    /// <param name="requestedOrganization">
    /// The <c>organization</c> authorize parameter (or the equivalent query parameter on the login
    /// app's own endpoints). Caller-supplied, so it resolves as a slug first and then as an id.
    /// </param>
    /// <param name="client">
    /// The client the request names, when one has been resolved. Null on the endpoints that have no
    /// client (<c>/api/auth/providers</c>, <c>/api/auth/sso-check</c>), where rule 2 simply does not
    /// apply and no restriction can be checked.
    /// </param>
    public async Task<Organization?> ResolveAsync(
        string? requestedOrganization,
        OAuthClient? client = null,
        CancellationToken ct = default)
    {
        var restricted = client?.RestrictedToOrganizationIds ?? [];

        Organization? organization;
        if (!string.IsNullOrWhiteSpace(requestedOrganization))
        {
            organization = await OrganizationSelector.ResolveBySlugThenIdAsync(
                organizations, requestedOrganization, ct);
            if (organization is null)
            {
                // Not an error here. The value is caller-supplied and may be nonsense, and this stage
                // only decides whether to OFFER an organisation's connections — refusing is the job of
                // the post-authentication selector, which already refuses exactly this case.
                logger.LogDebug(
                    "No organization matches the requested value {RequestedOrganization}; continuing tenant-wide",
                    requestedOrganization);
                return null;
            }
        }
        else if (restricted.Count == 1)
        {
            // A client's restricted list holds ids, so it is read as one — never slug-first, or an
            // organisation later taking that value as its slug would capture the client.
            organization = await organizations.GetAsync(restricted[0], ct);
        }
        else if (!string.IsNullOrWhiteSpace(tenantContext.OrganizationId))
        {
            organization = await organizations.GetAsync(tenantContext.OrganizationId, ct);
        }
        else
        {
            return null;
        }

        if (organization is null) return null;

        if (!organization.Enabled)
        {
            logger.LogDebug(
                "Organization {OrganizationId} is disabled; continuing tenant-wide", organization.Id);
            return null;
        }

        // The client restriction bites however the organisation was chosen, including when it came from
        // the tenant context: a host pinning an organisation by hostname must not be able to hand a
        // client an organisation its registration forbids.
        if (restricted.Count > 0 && !restricted.Contains(organization.Id, StringComparer.Ordinal))
        {
            logger.LogDebug(
                "Client {ClientId} is not permitted for organization {OrganizationId}; continuing tenant-wide",
                client?.ClientId, organization.Id);
            return null;
        }

        return organization;
    }

    /// <summary>
    /// The organisation the LOGIN PAGE is being rendered for, or null: the question the page's branding
    /// and its boot payload ask, answered in the order a multi-tenant host has always answered it.
    /// </summary>
    /// <remarks>
    /// Highest first:
    /// <list type="number">
    ///   <item>An authoritative pin (<see cref="ITenantContext.OrganizationIdIsAuthoritative"/>). One
    ///   naming an organisation the store no longer holds is no pin, and resolution carries on.</item>
    ///   <item>On <c>/accept-invite</c> and nowhere else, its <c>organization</c> query parameter. The
    ///   accept page mints no token, so the parameter only chooses branding; on any other page it would
    ///   let the page and the token disagree about the organisation.</item>
    ///   <item>The <c>organization</c>, <c>org_slug</c> or <c>org_id</c> of the authorize request this
    ///   page stands in for, read from the query string of <c>returnUrl</c>. When that names a
    ///   <c>request_uri</c>, the pushed request it points to is read instead (see below).</item>
    ///   <item>The single entry of an enabled client's <see cref="OAuthClient.RestrictedToOrganizationIds"/>,
    ///   for the client the request names.</item>
    ///   <item>A pin that is not authoritative.</item>
    /// </list>
    /// <para>
    /// A pushed authorization request puts none of its parameters on the query string: only
    /// <c>client_id</c> and the opaque <c>request_uri</c> ride along on <c>returnUrl</c>. So once a
    /// record is found (right prefix, right grant type, not expired, stored for the same client) its
    /// stored parameters and its client replace the outer ones for both rule 3 and rule 4. Anything else
    /// about the record falls through silently. It is only ever read: nothing is extended, consumed or
    /// removed, so a page render cannot prolong or destroy a request another leg of the flow owns.
    /// </para>
    /// <para>
    /// The tests applied are the login page's own and none of <see cref="ResolveAsync"/>'s: an
    /// organisation is not tested for being <see cref="Organization.Enabled"/> and its client's
    /// restriction is not checked against it, since a page that renders unstyled is a better failure
    /// than none and the refusals belong to the authorize endpoint. A client's restriction counts only
    /// when the client is enabled. Parameter names are read without regard to case, and each value is
    /// resolved by <see cref="OrganizationSelector.ResolveBySlugThenIdAsync"/>.
    /// </para>
    /// <para>
    /// Any exception, a store that throws included, resolves to none. This never throws.
    /// </para>
    /// </remarks>
    public async Task<Organization?> ResolveForLoginPageAsync(HttpRequest request, CancellationToken ct = default)
    {
        try
        {
            var pinned = tenantContext.OrganizationId;
            var hasPin = !string.IsNullOrWhiteSpace(pinned);
            var pinIsAuthoritative = hasPin && tenantContext.OrganizationIdIsAuthoritative;

            Organization? organization = null;
            if (pinIsAuthoritative)
                organization = await organizations.GetAsync(pinned!, ct);

            organization ??= await ResolveFromInviteLinkAsync(request, ct);
            organization ??= await ResolveFromReturnUrlAsync(request, ct);

            if (organization is null && hasPin && !pinIsAuthoritative)
                organization = await organizations.GetAsync(pinned!, ct);

            return organization;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not resolve the organization for a login page on {Host}; resolving none",
                request.Host.Host);
            return null;
        }
    }

    private async Task<Organization?> ResolveFromInviteLinkAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.Path.Equals("/accept-invite", StringComparison.OrdinalIgnoreCase)) return null;
        if (request.Query["organization"].FirstOrDefault() is not { } value) return null;

        return await ResolveNamedAsync(
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["organization"] = [value] }, ct);
    }

    private async Task<Organization?> ResolveFromReturnUrlAsync(HttpRequest request, CancellationToken ct)
    {
        var returnUrl = request.Query["returnUrl"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(returnUrl)) return null;

        var queryStart = returnUrl.IndexOf('?');
        if (queryStart < 0) return null;

        var parameters = ToCaseInsensitive(QueryHelpers.ParseQuery(returnUrl[(queryStart + 1)..])
            .Select(p => KeyValuePair.Create(p.Key, (string[])p.Value.ToArray()!)));
        var clientId = FirstValue(parameters, "client_id");

        if (grantStore is not null
            && !string.IsNullOrWhiteSpace(clientId)
            && FirstValue(parameters, "request_uri") is { } requestUri
            && await PeekPushedAuthorizationAsync(grantStore, requestUri, clientId, ct) is { } pushed)
        {
            parameters = ToCaseInsensitive(pushed.Parameters);
            clientId = pushed.ClientId;
        }

        if (await ResolveNamedAsync(parameters, ct) is { } named)
            return named;

        return !string.IsNullOrWhiteSpace(clientId) && clients is not null
            ? await ResolveFromRestrictedClientAsync(clients, clientId, ct)
            : null;
    }

    /// <summary>
    /// The organisation the first of <see cref="OrganizationParameters"/> that is present names, or null.
    /// Only the first present one is read: a later alias is not a fallback for one that did not resolve.
    /// </summary>
    private async Task<Organization?> ResolveNamedAsync(
        IReadOnlyDictionary<string, string[]> parameters, CancellationToken ct)
    {
        foreach (var name in OrganizationParameters)
        {
            if (FirstValue(parameters, name) is not { } value) continue;
            return await OrganizationSelector.ResolveBySlugThenIdAsync(organizations, value.Trim(), ct);
        }

        return null;
    }

    private async Task<Organization?> ResolveFromRestrictedClientAsync(
        IClientStore clientStore, string clientId, CancellationToken ct)
    {
        var client = await clientStore.GetAsync(clientId, ct);
        if (client is not { Enabled: true }) return null;
        if (client.RestrictedToOrganizationIds.Count != 1) return null;

        // A client's restricted list holds ids, so it is read as one.
        return await organizations.GetAsync(client.RestrictedToOrganizationIds[0], ct);
    }

    /// <summary>
    /// Reads a pushed authorization request for display only, without either side effect
    /// <see cref="ProtocolPushedAuthorizationService.LoadAsync"/> carries: it never extends the record's
    /// expiry and never deletes an expired or malformed one.
    /// </summary>
    private static async Task<PushedAuthorizationRequest?> PeekPushedAuthorizationAsync(
        IGrantStore grants, string requestUri, string clientId, CancellationToken ct)
    {
        if (!requestUri.StartsWith(ProtocolPushedAuthorizationService.RequestUriPrefix, StringComparison.Ordinal))
            return null;

        var grant = await grants.GetAsync(requestUri, ct);
        if (grant is null || grant.Type != ProtocolPushedAuthorizationService.GrantType) return null;
        if (grant.ExpiresAt <= DateTimeOffset.UtcNow) return null;
        if (!string.Equals(grant.ClientId, clientId, StringComparison.Ordinal)) return null;

        try
        {
            var record = JsonSerializer.Deserialize<PushedAuthorizationRequest>(grant.Data, PushedRequestDeserializeOptions);
            return record?.Parameters is null ? null : record;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, string[]> ToCaseInsensitive(IEnumerable<KeyValuePair<string, string[]>> source)
    {
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, values) in source)
            result.TryAdd(key, values ?? []);
        return result;
    }

    /// <summary>The first value named <paramref name="name"/>, or null if absent or blank.</summary>
    private static string? FirstValue(IReadOnlyDictionary<string, string[]> parameters, string name)
    {
        if (!parameters.TryGetValue(name, out var values)) return null;
        var value = values.FirstOrDefault();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Every connection belonging to <paramref name="organizationId"/>, both protocols, in one shape.
    /// </summary>
    /// <remarks>
    /// The two reads hit independent tables and both sit on interactive paths (home-realm discovery
    /// runs before the login page renders), so they run concurrently — the same reason
    /// <c>BuildProvidersResponseAsync</c> overlaps its own pair.
    /// </remarks>
    public static async Task<IReadOnlyList<OrganizationConnection>> ListConnectionsAsync(
        ISamlProviderStore samlStore,
        IOidcProviderStore oidcStore,
        string organizationId,
        CancellationToken ct = default)
    {
        var samlTask = samlStore.ListByOrganizationAsync(organizationId, ct);
        var oidcTask = oidcStore.ListByOrganizationAsync(organizationId, ct);
        var saml = await samlTask;
        var oidc = await oidcTask;

        var result = new List<OrganizationConnection>(saml.Count + oidc.Count);
        foreach (var c in oidc)
            result.Add(new OrganizationConnection(c.ConnectionId, c.ConnectionName, "oidc", c.AllowedDomains, c.IconUrl, c.ShowOnLogin, c.InteractionPath));
        foreach (var c in saml)
            result.Add(new OrganizationConnection(c.ConnectionId, c.ConnectionName, "saml", c.AllowedDomains, c.IconUrl, ShowOnLogin: true, InteractionPath: null));
        return result;
    }
}

/// <summary>
/// One organisation-scoped connection, protocol-agnostic, as the pre-authentication paths need it.
/// </summary>
/// <param name="ConnectionId">The connection's id — what <c>idp_hint</c> names and what the login URL carries.</param>
/// <param name="ConnectionName">Display name, for the "Continue with {name}" button.</param>
/// <param name="Type"><c>oidc</c> or <c>saml</c>.</param>
/// <param name="AllowedDomains">Email domains this connection claims WITHIN its organisation. Empty = it claims none and is offered directly.</param>
/// <param name="IconUrl">Optional branding icon.</param>
/// <param name="ShowOnLogin">Whether it is advertised as a button. Always true for SAML, which has no such flag.</param>
/// <param name="InteractionPath">Login-app page to render before federating (OIDC only; null otherwise).</param>
public sealed record OrganizationConnection(
    string ConnectionId,
    string ConnectionName,
    string Type,
    IReadOnlyList<string> AllowedDomains,
    string? IconUrl,
    bool ShowOnLogin,
    string? InteractionPath)
{
    /// <summary>The SP-initiated login URL for this connection.</summary>
    public string LoginUrl => Type == "oidc"
        ? $"/oidc/{ConnectionId}/login"
        : $"/saml/{ConnectionId}/login";

    /// <summary>True when this connection claims <paramref name="domain"/> within its organisation.</summary>
    public bool ClaimsDomain(string? domain) =>
        !string.IsNullOrEmpty(domain)
        && AllowedDomains.Any(d => string.Equals(d.Trim(), domain, StringComparison.OrdinalIgnoreCase));
}
