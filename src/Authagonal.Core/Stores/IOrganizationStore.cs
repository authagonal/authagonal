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

    /// <summary>Every organisation in this tenant, unpaged. Prefer <see cref="ListPageAsync"/> for an
    /// admin listing: a tenant serving many customers can hold enough organisations that loading them
    /// all for one screen is the slow path.</summary>
    Task<IReadOnlyList<Organization>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// One page of organisations in ascending ordinal <see cref="Organization.Id"/> order, starting
    /// strictly after the key <paramref name="cursor"/> names (from the start when it is null).
    /// </summary>
    /// <param name="cursor">A <see cref="OrganizationPage.NextCursor"/> from a previous page, or null.</param>
    /// <param name="limit">Page size; clamped to 1..200 (<see cref="KeysetCursor"/>). The default is the caller's choice.</param>
    /// <exception cref="ArgumentException">The cursor is malformed. Callers map this to a 400.</exception>
    /// <remarks>
    /// The default implementation sorts and slices <see cref="ListAsync"/>, so it is correct for every
    /// store but still reads every row. Providers with an ordered key range override it with a
    /// server-side query that reads one page.
    /// </remarks>
    async Task<OrganizationPage> ListPageAsync(string? cursor, int limit, CancellationToken ct = default)
    {
        // Validate the cursor before paying for the listing.
        KeysetCursor.Decode(cursor);
        var all = await ListAsync(ct).ConfigureAwait(false);
        var (items, next) = KeysetCursor.Slice(all, o => o.Id, cursor, limit);
        return new OrganizationPage(items, next);
    }

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
