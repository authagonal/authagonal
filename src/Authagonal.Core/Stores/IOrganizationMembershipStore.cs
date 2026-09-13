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

    /// <summary>Create or replace a membership, maintaining both indexes.</summary>
    Task UpsertAsync(OrganizationMembership membership, CancellationToken ct = default);

    /// <summary>
    /// Remove a membership outright. Revoking access without losing the record is
    /// <see cref="MembershipStatus.Suspended"/> through <see cref="UpsertAsync"/>; this is for
    /// genuine deletion.
    /// </summary>
    Task DeleteAsync(string organizationId, string userId, CancellationToken ct = default);
}
