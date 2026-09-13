using Authagonal.Core.Models;

namespace Authagonal.Core.Stores;

/// <summary>
/// Per-tenant store of <see cref="Organization"/> records. Read on the token-issuance path to
/// resolve the organisation an authorization request selected, and managed through the host's admin
/// surface.
/// </summary>
/// <remarks>
/// An empty store is not an error: it is a deployment that has not adopted organisations, where
/// every account behaves exactly as it did before they existed. Lookups therefore return null rather
/// than throwing for an unknown id or slug, and the issuance path treats "no such organisation" as
/// "this request selected none" unless the request named one explicitly — in which case naming one
/// that does not exist is a refusal, not a silent fall-through.
/// </remarks>
public interface IOrganizationStore
{
    /// <summary>The organisation with this <see cref="Organization.Id"/>, or null.</summary>
    Task<Organization?> GetAsync(string organizationId, CancellationToken ct = default);

    /// <summary>
    /// The organisation with this <see cref="Organization.Slug"/>, or null. Separate from
    /// <see cref="GetAsync"/> because the slug is what a relying party sends in the
    /// <c>organization</c> authorize parameter, and resolving it must not cost a scan.
    /// </summary>
    Task<Organization?> GetBySlugAsync(string slug, CancellationToken ct = default);

    /// <summary>Every organisation in this tenant, for admin listing. Tenants hold tens to
    /// thousands, not millions, so this is deliberately unpaged.</summary>
    Task<IReadOnlyList<Organization>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Create or replace an organisation. Implementations maintain the slug lookup used by
    /// <see cref="GetBySlugAsync"/>, and reject a slug already held by a different organisation —
    /// two rows answering one slug would make the <c>organization</c> parameter ambiguous.
    /// </summary>
    Task UpsertAsync(Organization organization, CancellationToken ct = default);

    /// <summary>
    /// Delete an organisation and its slug lookup. Memberships are NOT cascaded here — the caller
    /// owns that ordering, because a half-deleted organisation that still has members is recoverable
    /// while orphaned memberships pointing at nothing are not.
    /// </summary>
    Task DeleteAsync(string organizationId, CancellationToken ct = default);
}
