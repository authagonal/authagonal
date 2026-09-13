namespace Authagonal.Core.Models;

/// <summary>
/// One user's membership of one <see cref="Organization"/>. A user may hold many; this row, not
/// <see cref="AuthUser.OrganizationId"/>, is what authorises authenticating AS an organisation.
/// </summary>
/// <remarks>
/// Read on the token-issuance path, so it decides authorization and not merely presentation: an
/// organisation with <see cref="Organization.RequireMembershipForTokens"/> refuses to mint a token
/// for a user who holds no <see cref="MembershipStatus.Active"/> row here, at authorize and at every
/// refresh.
/// </remarks>
public sealed class OrganizationMembership
{
    /// <summary>The organisation joined — <see cref="Organization.Id"/>.</summary>
    public required string OrganizationId { get; set; }

    /// <summary>The member — <see cref="AuthUser.Id"/>.</summary>
    public required string UserId { get; set; }

    /// <summary>
    /// Roles held WITHIN this organisation, by name. Names come from the tenant's existing role
    /// catalogue (<see cref="Role"/>): a role is defined once per tenant and granted per
    /// organisation, so an ISV declares "Auditor" once and every customer grants it to their own
    /// people.
    /// </summary>
    /// <remarks>
    /// Carried on the model from the start so stores persist it, but NOT yet unioned into the
    /// <c>roles</c> claim — org-scoped role resolution is its own change. Until then this is
    /// recorded membership metadata, and effective roles remain
    /// <see cref="AuthUser.Roles"/> ∪ SCIM-group-granted roles exactly as before.
    /// </remarks>
    public List<string> Roles { get; set; } = [];

    /// <summary>
    /// One of <see cref="MembershipStatus"/>. Only <see cref="MembershipStatus.Active"/> authorises
    /// token issuance — an invited-but-unaccepted member and a suspended one are both refused, and
    /// the distinction exists so suspending someone does not destroy the record that they were
    /// invited and by whom.
    /// </summary>
    public string Status { get; set; } = MembershipStatus.Active;

    /// <summary>Who invited this member (<see cref="AuthUser.Id"/>), when the row came from an
    /// invitation rather than a direct grant or a provisioning sync. Null otherwise.</summary>
    public string? InvitedByUserId { get; set; }

    /// <summary>When the invitation was issued. Null for a membership that was never an
    /// invitation.</summary>
    public DateTimeOffset? InvitedAt { get; set; }

    /// <summary>When the membership became active. Null while an invitation is still
    /// outstanding.</summary>
    public DateTimeOffset? JoinedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>
/// The states an <see cref="OrganizationMembership"/> can be in. Strings rather than an enum so a
/// stored row from a newer writer round-trips through an older reader unchanged, which is how every
/// other status field in this library behaves.
/// </summary>
public static class MembershipStatus
{
    /// <summary>Invited but not yet accepted. Does not authorise token issuance.</summary>
    public const string Invited = "invited";

    /// <summary>A member in good standing. The only value that authorises token issuance.</summary>
    public const string Active = "active";

    /// <summary>Membership withdrawn without deleting the record. Does not authorise token
    /// issuance.</summary>
    public const string Suspended = "suspended";
}
