namespace Authagonal.Core.Services;

/// <summary>
/// Provides per-request tenant context. In single-tenant deployments, returns
/// a fixed configuration. In multi-tenant (Cloud) deployments, resolved from
/// the Host header by TenantResolutionMiddleware.
/// </summary>
public interface ITenantContext
{
    string TenantId { get; }
    string Issuer { get; }

    /// <summary>
    /// Sub-tenant environment (e.g. <c>live</c>, <c>test1</c>, <c>staging</c>).
    /// Live data lives in unprefixed tables (<c>{slug}-Users</c>); non-live envs
    /// share a per-tenant sandbox table set (<c>{slug}-sandbox-Users</c>) with
    /// the env discriminating rows via PartitionKey prefix. Defaults to <c>live</c>.
    /// </summary>
    string Env => LiveEnv;

    /// <summary>Canonical name for the live (production) environment.</summary>
    public const string LiveEnv = "live";

    /// <summary>
    /// Whether the login endpoint rejects users whose email is not yet confirmed.
    /// Defaults to <c>true</c> — an unconfirmed user cannot sign in. Multi-tenant
    /// deployments override this to <c>false</c> for control-plane (admin) tenants,
    /// where a freshly provisioned owner must be able to sign in and verify from
    /// inside the portal (the in-app verify banner, not a login wall).
    /// </summary>
    bool RequireConfirmedEmailForLogin => true;

    /// <summary>
    /// The organisation this request is for, when the host resolves one BEFORE authentication — a
    /// custom-domain or per-organisation hostname pin, for example. <c>null</c> (the default) means
    /// the host resolves none, which is what every single-tenant deployment does and what this
    /// library did everywhere before organisation-scoped SSO connections existed.
    /// </summary>
    /// <remarks>
    /// A default interface member rather than a new required property, so an existing
    /// <see cref="ITenantContext"/> implementation in another host keeps compiling untouched.
    /// <para>
    /// It is the LOWEST-precedence pre-authentication source: the <c>organization</c> authorize
    /// parameter and a client restricted to exactly one organisation both outrank it (see
    /// <c>PreAuthOrganizationResolver</c>). It never widens access on its own — the organisation it
    /// names is still checked for existence, for <see cref="Models.Organization.Enabled"/>, and
    /// against the client's <c>RestrictedToOrganizationIds</c>.
    /// </para>
    /// </remarks>
    string? OrganizationId => null;

    /// <summary>
    /// Per-tenant answer to whether anonymous dynamic client registration (RFC 7591) is open —
    /// what an MCP connector needs before it can start its OAuth flow. <c>null</c> (the default)
    /// means the tenant has no say and the host-wide <c>Auth:DynamicClientRegistrationEnabled</c>
    /// decides; multi-tenant hosts override from tenant settings so one tenant opting in does not
    /// open anonymous registration on every other.
    /// </summary>
    bool? DynamicClientRegistrationEnabled => null;
}
