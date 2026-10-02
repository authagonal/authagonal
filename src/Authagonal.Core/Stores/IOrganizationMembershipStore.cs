using Authagonal.Core.Models;

namespace Authagonal.Core.Stores;

/// <summary>
/// Per-tenant store of <see cref="OrganizationMembership"/> rows — who belongs to which
/// organisation. Read on the token-issuance path by the membership gate.
/// </summary>
/// <remarks>
/// Both directions are hot and neither can be derived cheaply from the other, so both are first-class
/// operations rather than a filter over one listing. <see cref="ListByUserAsync"/> answers "which
/// organisations may this person authenticate as", asked on every interactive authorization;
/// <see cref="ListByOrganizationAsync"/> answers "who is in this organisation", asked by the admin
/// surface. Durable implementations back them with two index rows per membership; the single-row
/// <see cref="GetAsync"/> is what the gate itself uses, because it already knows both halves of the
/// key and a point read beats either listing.
/// </remarks>
public interface IOrganizationMembershipStore
{
    /// <summary>This user's membership of this organisation, or null if they hold none.</summary>
    Task<OrganizationMembership?> GetAsync(string organizationId, string userId, CancellationToken ct = default);

    /// <summary>Every membership this user holds, in any status.</summary>
    Task<IReadOnlyList<OrganizationMembership>> ListByUserAsync(string userId, CancellationToken ct = default);

    /// <summary>Every membership of this organisation, in any status.</summary>
    Task<IReadOnlyList<OrganizationMembership>> ListByOrganizationAsync(string organizationId, CancellationToken ct = default);

    /// <summary>
    /// One page of this organisation's memberships, in any status, in ascending ordinal
    /// <see cref="OrganizationMembership.UserId"/> order, starting strictly after the key
    /// <paramref name="cursor"/> names (from the start when it is null).
    /// </summary>
    /// <param name="organizationId">The organisation whose members to list.</param>
    /// <param name="cursor">A <see cref="OrganizationMembershipPage.NextCursor"/> from a previous page of
    /// the SAME organisation, or null.</param>
    /// <param name="limit">Page size; clamped to 1..200 (<see cref="KeysetCursor"/>). The default is the caller's choice.</param>
    /// <exception cref="ArgumentException">The cursor is malformed. Callers map this to a 400.</exception>
    /// <remarks>
    /// The default implementation sorts and slices <see cref="ListByOrganizationAsync"/>, so it is
    /// correct for every store but still reads every member. Providers with an ordered key range
    /// override it with a server-side query that reads one page.
    /// </remarks>
    async Task<OrganizationMembershipPage> ListByOrganizationPageAsync(
        string organizationId, string? cursor, int limit, CancellationToken ct = default)
    {
        KeysetCursor.Decode(cursor);
        var all = await ListByOrganizationAsync(organizationId, ct).ConfigureAwait(false);
        var (items, next) = KeysetCursor.Slice(all, m => m.UserId, cursor, limit);
        return new OrganizationMembershipPage(items, next);
    }

    /// <summary>Create or replace a membership, maintaining both indexes.</summary>
    Task UpsertAsync(OrganizationMembership membership, CancellationToken ct = default);

    /// <summary>
    /// Remove a membership outright. Revoking access without losing the record is
    /// <see cref="MembershipStatus.Suspended"/> through <see cref="UpsertAsync"/>; this is for
    /// genuine deletion.
    /// </summary>
    Task DeleteAsync(string organizationId, string userId, CancellationToken ct = default);
}
