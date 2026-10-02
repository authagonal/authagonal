namespace Authagonal.Core.Models;

/// <summary>One page of <see cref="Organization"/> records, in ascending ordinal <see cref="Organization.Id"/> order.</summary>
/// <param name="Items">The organisations on this page.</param>
/// <param name="NextCursor">
/// Opaque cursor for the page after this one, or null when there is none. Pass it back verbatim; its
/// contents are not part of the contract.
/// </param>
public sealed record OrganizationPage(IReadOnlyList<Organization> Items, string? NextCursor);

/// <summary>One page of an organisation's <see cref="OrganizationMembership"/> rows, in ascending ordinal
/// <see cref="OrganizationMembership.UserId"/> order.</summary>
/// <param name="Items">The memberships on this page.</param>
/// <param name="NextCursor">
/// Opaque cursor for the page after this one, or null when there is none. Pass it back verbatim; its
/// contents are not part of the contract.
/// </param>
public sealed record OrganizationMembershipPage(IReadOnlyList<OrganizationMembership> Items, string? NextCursor);
