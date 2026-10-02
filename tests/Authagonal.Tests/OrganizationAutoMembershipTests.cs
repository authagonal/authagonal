using Authagonal.Core.Models;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Tests;

/// <summary>
/// <see cref="Organization.AllowAutoMembership"/> as enforced by <see cref="OrganizationSelector"/>:
/// an explicitly selected organisation admits a user with no active membership when, and only when,
/// it is enabled, the flag is on, the user's email is confirmed, and the email's domain EXACTLY equals
/// one of the organisation's VERIFIED domains. Every refusal branch also asserts no row was written,
/// because a refused-but-recorded membership would admit the user on their next attempt.
/// </summary>
public sealed class OrganizationAutoMembershipTests
{
    private const string OrgId = "org_acme";
    private const string Slug = "acme";
    private static readonly DateTimeOffset Verified = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static AuthUser User(string email = "jane@acme.com", bool confirmed = true, string? organizationId = null) => new()
    {
        Id = "user-1",
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        EmailConfirmed = confirmed,
        IsActive = true,
        OrganizationId = organizationId,
    };

    private static Organization Org(
        bool allowAuto = true, bool enabled = true, string domain = "acme.com", bool domainVerified = true) => new()
    {
        Id = OrgId,
        Slug = Slug,
        DisplayName = "Acme",
        Enabled = enabled,
        AllowAutoMembership = allowAuto,
        RequireMembershipForTokens = true,
        Domains =
        [
            new OrganizationDomain
            {
                Domain = domain,
                VerificationToken = "tok",
                CreatedAt = Verified.AddDays(-1),
                VerifiedAt = domainVerified ? Verified : null,
            },
        ],
    };

    private static async Task<(OrganizationSelector Selector, WritableOrganizationMembershipStore Members)> SetupAsync(Organization org)
    {
        var orgs = new WritableOrganizationStore();
        await orgs.UpsertAsync(org);
        var members = new WritableOrganizationMembershipStore();
        return (new OrganizationSelector(orgs, members, NullLogger<OrganizationSelector>.Instance), members);
    }

    private static OAuthClient Client(params string[] restrictedTo) => new()
    {
        ClientId = "client-1",
        ClientName = "Client One",
        RestrictedToOrganizationIds = [.. restrictedTo],
    };

    private static async Task AssertRefusedAsync(Task<OrganizationSelection> selecting)
    {
        var ex = await Assert.ThrowsAsync<OrganizationAccessDeniedException>(() => selecting);
        Assert.Contains("not an active member", ex.Message);
    }

    // -----------------------------------------------------------------------
    // Joins
    // -----------------------------------------------------------------------

    [Fact]
    public async Task OrganizationParameter_QualifyingUser_JoinsAsActiveMemberWithNoRoles()
    {
        var (selector, members) = await SetupAsync(Org());
        var before = DateTimeOffset.UtcNow;

        var selection = await selector.SelectAsync(User(), Client(), Slug);

        Assert.Equal(OrgId, selection.OrganizationId);
        Assert.True(selection.ExplicitlySelected);
        Assert.Null(selection.MembershipRoles);

        var row = await members.GetAsync(OrgId, "user-1");
        Assert.NotNull(row);
        Assert.Equal(MembershipStatus.Active, row!.Status);
        Assert.Empty(row.Roles);
        Assert.Null(row.InvitedByUserId);
        Assert.NotNull(row.JoinedAt);
        Assert.True(row.JoinedAt >= before);
    }

    [Fact]
    public async Task Refresh_CarriedOrganization_RejoinsAfterTheMembershipWasDeleted()
    {
        var (selector, members) = await SetupAsync(Org());
        await selector.SelectAsync(User(), Client(), Slug);
        await members.DeleteAsync(OrgId, "user-1");

        var selection = await selector.SelectAsync(User(), Client(), requestedOrganization: null, carriedOrganizationId: OrgId);

        Assert.Equal(OrgId, selection.OrganizationId);
        Assert.Equal(MembershipStatus.Active, (await members.GetAsync(OrgId, "user-1"))!.Status);
    }

    [Fact]
    public async Task SingleRestrictedClient_QualifyingUser_Joins()
    {
        var (selector, members) = await SetupAsync(Org());

        var selection = await selector.SelectAsync(User(), Client(OrgId), requestedOrganization: null);

        Assert.Equal(OrgId, selection.OrganizationId);
        Assert.Equal(MembershipStatus.Active, (await members.GetAsync(OrgId, "user-1"))!.Status);
    }

    [Fact]
    public async Task OrgScopedConnection_QualifyingUser_Joins()
    {
        var (selector, members) = await SetupAsync(Org());

        var selection = await selector.SelectAsync(
            User(), Client(), requestedOrganization: null, connectionOrganizationId: OrgId);

        Assert.Equal(OrgId, selection.OrganizationId);
        Assert.Equal(MembershipStatus.Active, (await members.GetAsync(OrgId, "user-1"))!.Status);
    }

    [Theory]
    [InlineData("Jane@ACME.com")]
    [InlineData("\"odd@local\"@acme.com")] // the domain is after the LAST '@'
    public async Task EmailDomain_IsTheLowercasedPartAfterTheLastAt(string email)
    {
        var (selector, members) = await SetupAsync(Org());

        await selector.SelectAsync(User(email), Client(), Slug);

        Assert.NotNull(await members.GetAsync(OrgId, "user-1"));
    }

    [Fact]
    public async Task InvitedMembership_IsActivated_KeepingItsRolesAndInviter()
    {
        var (selector, members) = await SetupAsync(Org());
        var invitedAt = Verified.AddHours(-3);
        await members.UpsertAsync(new OrganizationMembership
        {
            OrganizationId = OrgId,
            UserId = "user-1",
            Status = MembershipStatus.Invited,
            Roles = ["Auditor"],
            InvitedByUserId = "admin-1",
            InvitedAt = invitedAt,
            CreatedAt = invitedAt,
        });

        var selection = await selector.SelectAsync(User(), Client(), Slug);

        Assert.Equal(["Auditor"], selection.MembershipRoles);
        var row = (await members.GetAsync(OrgId, "user-1"))!;
        Assert.Equal(MembershipStatus.Active, row.Status);
        Assert.Equal(["Auditor"], row.Roles);
        Assert.Equal("admin-1", row.InvitedByUserId);
        Assert.Equal(invitedAt, row.InvitedAt);
        Assert.NotNull(row.JoinedAt);
    }

    // -----------------------------------------------------------------------
    // Refusals
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SuspendedMembership_IsNeverReactivated()
    {
        var (selector, members) = await SetupAsync(Org());
        members.With(OrgId, "user-1", MembershipStatus.Suspended, "Auditor");

        await AssertRefusedAsync(selector.SelectAsync(User(), Client(), Slug));

        Assert.Equal(MembershipStatus.Suspended, (await members.GetAsync(OrgId, "user-1"))!.Status);
    }

    [Fact]
    public async Task UnconfirmedEmail_IsRefused()
    {
        var (selector, members) = await SetupAsync(Org());

        await AssertRefusedAsync(selector.SelectAsync(User(confirmed: false), Client(), Slug));

        Assert.Null(await members.GetAsync(OrgId, "user-1"));
    }

    [Fact]
    public async Task UnverifiedDomain_IsRefused()
    {
        var (selector, members) = await SetupAsync(Org(domainVerified: false));

        await AssertRefusedAsync(selector.SelectAsync(User(), Client(), Slug));

        Assert.Null(await members.GetAsync(OrgId, "user-1"));
    }

    [Theory]
    [InlineData("acme.com", "jane@eu.acme.com")]  // a subdomain of a verified domain
    [InlineData("eu.acme.com", "jane@acme.com")]  // the parent of a verified subdomain
    [InlineData("acme.com", "jane@notacme.com")]  // a suffix that is not a label boundary
    [InlineData("acme.com", "jane@acme.com.evil.test")]
    public async Task OnlyAnExactDomainMatches(string verifiedDomain, string email)
    {
        var (selector, members) = await SetupAsync(Org(domain: verifiedDomain));

        await AssertRefusedAsync(selector.SelectAsync(User(email), Client(), Slug));

        Assert.Null(await members.GetAsync(OrgId, "user-1"));
    }

    [Fact]
    public async Task FlagOff_IsRefused()
    {
        var (selector, members) = await SetupAsync(Org(allowAuto: false));

        await AssertRefusedAsync(selector.SelectAsync(User(), Client(), Slug));

        Assert.Null(await members.GetAsync(OrgId, "user-1"));
    }

    [Fact]
    public async Task DisabledOrganization_IsRefusedAndWritesNothing()
    {
        var (selector, members) = await SetupAsync(Org(enabled: false));

        await Assert.ThrowsAsync<OrganizationAccessDeniedException>(
            () => selector.SelectAsync(User(), Client(), Slug));

        Assert.Null(await members.GetAsync(OrgId, "user-1"));
    }

    [Fact]
    public async Task AccountTagFallback_NeverAutoJoins()
    {
        var (selector, members) = await SetupAsync(Org());

        // No parameter, no carried grant, no connection, unrestricted client: the organisation comes
        // from the account's own tag, which is not an explicit selection.
        var selection = await selector.SelectAsync(User(organizationId: OrgId), Client(), requestedOrganization: null);

        Assert.Equal(OrgId, selection.OrganizationId);
        Assert.False(selection.ExplicitlySelected);
        Assert.Null(selection.MembershipRoles);
        Assert.Null(await members.GetAsync(OrgId, "user-1"));
    }
}
