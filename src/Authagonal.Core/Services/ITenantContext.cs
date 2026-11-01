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
    /// Whether <see cref="OrganizationId"/> is a pin the host insists on, rather than a default the
    /// request may override. <c>false</c> (the default) keeps <see cref="OrganizationId"/> the
    /// lowest-precedence source it has always been.
    /// </summary>
    /// <remarks>
    /// A pin is authoritative only when this is <c>true</c> AND <see cref="OrganizationId"/> is
    /// non-empty. An authoritative pin governs <c>/connect/authorize</c> and <c>POST /connect/par</c>: a
    /// request naming no organisation has the pinned one added to it, and a request naming any other
    /// organisation (by slug, by id, or through the <c>org_slug</c> and <c>org_id</c> aliases), or one
    /// that does not exist, is refused with <c>400 access_denied</c> and is never redirected. A pin
    /// naming an organisation the store no longer holds is no pin at all, so a host outliving the
    /// organisation it was pinned to keeps working as a tenant-level host.
    /// <para>
    /// A default interface member for the same reason as <see cref="OrganizationId"/>: an existing
    /// implementation keeps compiling untouched and behaves exactly as before.
    /// </para>
    /// </remarks>
    bool OrganizationIdIsAuthoritative => false;

    /// <summary>
    /// Per-tenant answer to whether anonymous dynamic client registration (RFC 7591) is open —
    /// what an MCP connector needs before it can start its OAuth flow. <c>null</c> (the default)
    /// means the tenant has no say and the host-wide <c>Auth:DynamicClientRegistrationEnabled</c>
    /// decides; multi-tenant hosts override from tenant settings so one tenant opting in does not
    /// open anonymous registration on every other.
    /// </summary>
    bool? DynamicClientRegistrationEnabled => null;
}
