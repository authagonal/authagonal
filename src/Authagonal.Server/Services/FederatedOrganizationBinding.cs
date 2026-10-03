using System.Security.Claims;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Microsoft.Extensions.Logging;

namespace Authagonal.Server.Services;

/// <summary>
/// What an ORG-SCOPED SSO connection does to the session it establishes: it names the organisation,
/// and it makes the person a member of it.
/// </summary>
/// <remarks>
/// Shared by the SAML ACS and the OIDC callback because the two must agree exactly. They already
/// diverged once over the cookie's IdP bound — one applied it, the other did not — and this is the
/// same shape of thing: a rule that is only correct if both halves of federation implement it
/// identically.
/// </remarks>
public static class FederatedOrganizationBinding
{
    /// <summary>
    /// Ensures this user holds an active membership of <paramref name="organizationId"/>, creating one
    /// if they hold none and accepting a pending invitation, and returns the claim that binds the
    /// session to it.
    /// </summary>
    /// <remarks>
    /// Signing in through a connection that belongs to an organisation IS the assertion of belonging:
    /// the organisation's own administrator configured the IdP, and the IdP vouched for this person. So
    /// the membership is created rather than demanded — otherwise every user of a self-service SSO
    /// connection would authenticate successfully and then be refused a token by
    /// <see cref="Organization.RequireMembershipForTokens"/>, with nothing in the product able to
    /// create the row they are missing.
    /// <para>
    /// An <see cref="MembershipStatus.Invited"/> row is ACCEPTED: the organisation invited this person
    /// and its own IdP has now vouched for them, which is a stronger proof than the emailed link the
    /// invitation was waiting on. Leaving it pending stranded exactly the users an operator meant to
    /// let in: invited into an organisation that signs in through SSO, they never set a password, so
    /// the link that would have activated them is never used, and every sign-in ended in a refused
    /// token. Who invited them, when, and the roles they were offered are kept, the same acceptance
    /// <see cref="OrganizationSelector"/> performs for a verified email domain.
    /// </para>
    /// <para>
    /// A <see cref="MembershipStatus.Suspended"/> row is never modified, and that is the security half.
    /// Suspension is how an administrator revokes access without destroying the record; re-activating
    /// it here would mean anyone suspended could restore their own access simply by signing in again,
    /// which is the one thing suspension has to prevent.
    /// </para>
    /// </remarks>
    public static async Task<Claim> BindAsync(
        IOrganizationMembershipStore memberships,
        string organizationId,
        string userId,
        string connectionId,
        ILogger logger,
        CancellationToken ct = default)
    {
        var existing = await memberships.GetAsync(organizationId, userId, ct);
        if (existing is null)
        {
            var now = DateTimeOffset.UtcNow;
            await memberships.UpsertAsync(new OrganizationMembership
            {
                OrganizationId = organizationId,
                UserId = userId,
                Status = MembershipStatus.Active,
                JoinedAt = now,
                CreatedAt = now,
            }, ct);

            logger.LogInformation(
                "Created an active membership of organization {OrganizationId} for {UserId}, who signed in "
                + "through organization-scoped connection {ConnectionId}",
                organizationId, userId, connectionId);
        }
        else if (string.Equals(existing.Status, MembershipStatus.Invited, StringComparison.Ordinal))
        {
            var now = DateTimeOffset.UtcNow;
            await memberships.UpsertAsync(new OrganizationMembership
            {
                OrganizationId = existing.OrganizationId,
                UserId = existing.UserId,
                Status = MembershipStatus.Active,
                Roles = [.. existing.Roles],
                InvitedByUserId = existing.InvitedByUserId,
                InvitedAt = existing.InvitedAt,
                JoinedAt = now,
                CreatedAt = existing.CreatedAt,
                UpdatedAt = now,
            }, ct);

            logger.LogInformation(
                "Accepted {UserId}'s invitation to organization {OrganizationId}: they signed in through "
                + "organization-scoped connection {ConnectionId}",
                userId, organizationId, connectionId);
        }
        else if (!string.Equals(existing.Status, MembershipStatus.Active, StringComparison.Ordinal))
        {
            // Left exactly as it is — see the remarks. Logged because the sign-in succeeds and the
            // token mint then refuses, which is otherwise an unexplained access_denied.
            logger.LogInformation(
                "{UserId} signed in through organization-scoped connection {ConnectionId} but their "
                + "membership of organization {OrganizationId} is '{Status}', which authorises no token",
                userId, connectionId, organizationId, existing.Status);
        }

        return new Claim(CookieSignInHelper.ConnectionOrganizationClaim, organizationId);
    }
}
