namespace Authagonal.Core.Models;

/// <summary>
/// A customer organisation inside a tenant. The tenant remains the isolation boundary — one table
/// prefix, one signing key, one issuer — and an organisation partitions identity WITHIN it, so one
/// ISV deployment can serve many customer organisations without a tenant each.
/// </summary>
/// <remarks>
/// This is the entity behind the <c>org_id</c> claim. Before it existed, <c>org_id</c> was emitted
/// from <see cref="AuthUser.OrganizationId"/> — a bare string with no record behind it, written by
/// downstream provisioning and by SCIM token tagging. That string keeps working and keeps emitting
/// <c>org_id</c>: an account whose organization id resolves to no <see cref="Organization"/> row
/// behaves exactly as it did before this type shipped. Only the additional claims
/// (<c>org_slug</c>, <c>org_name</c>) and membership enforcement need a real row.
/// </remarks>
public sealed class Organization
{
    /// <summary>
    /// Stable opaque identifier, emitted as the <c>org_id</c> claim. Immutable: relying parties compare
    /// it against the instance they are serving, so a changed value is an outage with no error message.
    /// </summary>
    /// <remarks>
    /// Must match <see cref="OrganizationIdentifier"/> — <c>^[A-Za-z0-9._~-]{1,200}$</c> — so it can
    /// always be sent as the <c>organization</c> authorize parameter. An id outside that shape is an id
    /// no request can select, and a client restricted to one would refuse every request.
    /// <para>
    /// It SHOULD also contain at least one character a slug may not: an uppercase letter, <c>.</c>,
    /// <c>_</c> or <c>~</c>. Ids and slugs share one lookup namespace (stores refuse an actual
    /// collision), and the <c>organization</c> parameter is resolved slug-first for an all-lowercase
    /// value — so an id that is itself slug-shaped is an id that could one day be refused at creation
    /// because someone took that slug, while one carrying a non-slug character never can.
    /// <c>OrganizationIdentifier.CouldCollideWithASlug</c> reports which kind an id is.
    /// </para>
    /// <para>
    /// No convention is enforced, and the library has never enforced one: the values already in the
    /// field come from a downstream app's TCC <c>/try</c> response
    /// (<c>TccProvisioningOrchestrator</c>) or from an operator's SCIM token binding
    /// (<c>ScimToken.OrganizationId</c>, stamped on new users by <c>ScimUserEndpoints</c>), and both are
    /// arbitrary strings. For new ids an <c>org_</c>-prefixed opaque value — <c>org_7f3a9c</c> — is the
    /// recommended shape: the underscore is not slug-legal, so the prefix alone guarantees the id can
    /// never collide with any slug.
    /// </para>
    /// </remarks>
    public required string Id { get; set; }

    /// <summary>
    /// Tenant-unique, URL-safe identifier, emitted as the <c>org_slug</c> claim and accepted by the
    /// <c>organization</c> authorize parameter. Immutable for the same reason as <see cref="Id"/> —
    /// it is the value a relying party hard-codes.
    /// </summary>
    /// <remarks>
    /// There is no tenant foreign key on this type. The tenant is the store the row lives in — the
    /// table prefix in the multi-tenant host, the deployment itself in a single-tenant one — exactly
    /// as for <see cref="Role"/>, <see cref="ScimGroup"/> and every other per-tenant record.
    /// </remarks>
    public required string Slug { get; set; }

    /// <summary>Human-readable name, emitted as the <c>org_name</c> claim. Freely mutable.</summary>
    public required string DisplayName { get; set; }

    /// <summary>
    /// Free-form metadata the tenant controls. Never emitted on a token by itself — a value reaches a
    /// token only if a scope's <c>UserClaims</c> releases a claim of that name, the same gate custom
    /// user attributes go through.
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = [];

    /// <summary>
    /// Branding overriding the tenant's, serialised as JSON because the shape belongs to the host —
    /// the multi-tenant product has a rich branding model this library does not reference. Null means
    /// inherit everything. Hosts that render it merge field by field, so an organisation that sets
    /// only a logo keeps the tenant's colours.
    /// </summary>
    public string? BrandingJson { get; set; }

    /// <summary>
    /// Disabled organisations mint no tokens. Checked at authorize AND on every refresh, so disabling
    /// one stops live sessions at their next rotation rather than only blocking new logins.
    /// </summary>
    /// <remarks>
    /// Each refresh is REFUSED while the organisation is disabled; the grant itself is not revoked and
    /// is left unconsumed, so re-enabling the organisation resumes the session without a fresh
    /// sign-in, and the chain still dies on its own absolute lifetime. Ending a session outright is
    /// grant revocation, which this flag is not.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether a user must hold an active membership of this organisation to be issued a token for
    /// it. On by default: an organisation whose membership is advisory is not a tenancy boundary, and
    /// the whole point of selecting one is that the resulting token names who it may act for.
    /// </summary>
    /// <remarks>
    /// Only bites once an organisation is actually selected. An account carrying a legacy
    /// <see cref="AuthUser.OrganizationId"/> that resolves to no row here is not gated at all, so
    /// enabling organisations cannot retroactively lock out existing users.
    /// </remarks>
    public bool RequireMembershipForTokens { get; set; } = true;

    /// <summary>
    /// Whether a user whose email domain matches one this organisation has verified may become a
    /// member without an invitation. Off by default, mirroring
    /// <see cref="SamlProviderConfig.AllowUninvitedJit"/> — self-service joining is a decision, not a
    /// default. Nothing in this library acts on it yet; it is the host's provisioning surface that
    /// will.
    /// </summary>
    public bool AllowAutoMembership { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }
}
