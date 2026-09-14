using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Microsoft.Extensions.Logging;

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
/// </remarks>
public sealed class PreAuthOrganizationResolver(
    IOrganizationStore organizations,
    ITenantContext tenantContext,
    ILogger<PreAuthOrganizationResolver> logger)
{
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
