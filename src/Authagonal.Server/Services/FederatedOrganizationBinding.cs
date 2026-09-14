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
    /// Ensures this user holds a membership of <paramref name="organizationId"/>, creating an active
    /// one if they hold none, and returns the claim that binds the session to it.
    /// </summary>
    /// <remarks>
    /// Signing in through a connection that belongs to an organisation IS the assertion of belonging:
    /// the organisation's own administrator configured the IdP, and the IdP vouched for this person. So
    /// the membership is created rather than demanded — otherwise every user of a self-service SSO
    /// connection would authenticate successfully and then be refused a token by
    /// <see cref="Organization.RequireMembershipForTokens"/>, with nothing in the product able to
    /// create the row they are missing.
    /// <para>
    /// An EXISTING row is never modified, and that is the security half. A
    /// <see cref="MembershipStatus.Suspended"/> membership is how an administrator revokes access
    /// without destroying the record; re-activating it here would mean anyone suspended could restore
    /// their own access simply by signing in again, which is the one thing suspension has to prevent.
    /// An <see cref="MembershipStatus.Invited"/> row is likewise left for the invitation flow to
    /// accept, and any roles already on the row are left alone.
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
