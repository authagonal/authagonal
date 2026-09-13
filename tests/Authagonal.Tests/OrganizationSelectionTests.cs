using System.Security.Claims;
using Authagonal.Core.Models;
using Authagonal.Protocol;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Tests;

/// <summary>
/// Organisation selection and enforcement in <see cref="OrganizationSelector"/> and
/// <see cref="UserStoreOidcSubjectResolver"/> — which organisation a request is for, whether the
/// user and client may have it, and that the answer survives a refresh rotation unchanged.
/// </summary>
/// <remarks>
/// The carry-forward tests are the load-bearing ones. Every other field on the subject is rebuilt
/// from the user store on refresh, so an organisation that lives only on the subject reverts to the
/// account default on the first rotation — silently, roughly one access-token lifetime after login,
/// handing the relying party another customer's <c>org_id</c> with no error anywhere. That is the
/// defect <see cref="Refresh_CarriesTheSelectedOrganization_NotTheAccountDefault"/> exists to catch.
/// </remarks>
public sealed class OrganizationSelectionTests
{
    private const string ClientId = "client-1";

    // -----------------------------------------------------------------------
    // Fixtures
    // -----------------------------------------------------------------------

    private static AuthUser User(string id = "user-1", string? organizationId = null) => new()
    {
        Id = id,
        Email = $"{id}@example.com",
        NormalizedEmail = $"{id.ToUpperInvariant()}@EXAMPLE.COM",
        EmailConfirmed = true,
        IsActive = true,
        OrganizationId = organizationId,
    };

    private static OAuthClient Client(params string[] restrictedTo) => new()
    {
        ClientId = ClientId,
        ClientName = "Client One",
        RestrictedToOrganizationIds = [.. restrictedTo],
    };

    private static ClaimsPrincipal Principal(string subjectId)
    {
        var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, subjectId));
        identity.AddClaim(new Claim("sub", subjectId));
        return new ClaimsPrincipal(identity);
    }

    private static UserStoreOidcSubjectResolver Resolver(
        AuthUser user,
        OAuthClient? client = null,
        WritableOrganizationStore? organizations = null,
        WritableOrganizationMembershipStore? memberships = null)
    {
        var users = new InMemoryUserStore();
        users.CreateAsync(user).GetAwaiter().GetResult();

        var clients = new InMemoryClientStore();
        if (client is not null) clients.UpsertAsync(client).GetAwaiter().GetResult();

        return ResolverTestSupport.NewResolver(
            users, new InMemoryScimGroupStore(), new WritableScimGroupRoleMappingStore(), clients,
            organizationSelector: new OrganizationSelector(
                organizations ?? new WritableOrganizationStore(),
                memberships ?? new WritableOrganizationMembershipStore(),
                NullLogger<OrganizationSelector>.Instance));
    }

    private static Task<OidcSubjectResult> ResolveAsync(
        UserStoreOidcSubjectResolver resolver, AuthUser user, string? organization = null) =>
        resolver.ResolveAsync(
            Principal(user.Id),
            new OidcSubjectResolutionContext(ClientId, ["openid", "profile"], [], organization));

    private static OidcSubject Allowed(OidcSubjectResult result) =>
        Assert.IsType<OidcSubjectResult.Allowed>(result).Subject;

    private static OidcSubjectResult.Rejected Rejected(OidcSubjectResult result) =>
        Assert.IsType<OidcSubjectResult.Rejected>(result);

    // -----------------------------------------------------------------------
    // Regression: a deployment with no organizations behaves exactly as before
    // -----------------------------------------------------------------------

    /// The upgrade guarantee. No organization records, no parameter, no client restriction: the
    /// subject carries precisely what it carried before organisations existed — the account's own
    /// string as org_id, and nothing else.
    [Fact]
    public async Task NoOrganizationsAnywhere_SubjectIsUnchanged()
    {
        var user = User();
        var subject = Allowed(await ResolveAsync(Resolver(user, Client()), user));

        Assert.Null(subject.OrganizationId);
        Assert.Null(subject.OrganizationSlug);
        Assert.Null(subject.OrganizationName);
    }

    /// The other half of the upgrade guarantee, and the case that actually exists in the field: an
    /// account tagged by downstream provisioning or a SCIM token with an id that has no organization
    /// record behind it. It keeps emitting org_id verbatim, gains no slug or name it cannot back, and
    /// is gated by nothing.
    [Fact]
    public async Task LegacyOrganizationId_PassesThroughWithNoSlugAndNoGate()
    {
        var user = User(organizationId: "legacy-org-42");
        var subject = Allowed(await ResolveAsync(Resolver(user, Client()), user));

        Assert.Equal("legacy-org-42", subject.OrganizationId);
        Assert.Null(subject.OrganizationSlug);
        Assert.Null(subject.OrganizationName);
    }

    /// A host that constructs the resolver without a selector — the seam every pre-existing caller
    /// uses — reads the account field directly, with no store lookup at all.
    [Fact]
    public async Task NoSelectorWired_ReadsTheAccountFieldDirectly()
    {
        var user = User(organizationId: "org-from-account");
        var users = new InMemoryUserStore();
        await users.CreateAsync(user);
        var resolver = ResolverTestSupport.NewResolver(
            users, new InMemoryScimGroupStore(), new WritableScimGroupRoleMappingStore(), new InMemoryClientStore());

        var subject = Allowed(await ResolveAsync(resolver, user));

        Assert.Equal("org-from-account", subject.OrganizationId);
        Assert.Null(subject.OrganizationSlug);
    }

    // -----------------------------------------------------------------------
    // Precedence
    // -----------------------------------------------------------------------

    /// Rule 1 beats rule 3: the parameter wins over the account's own default.
    [Fact]
    public async Task Parameter_BeatsTheAccountDefault()
    {
        var orgs = new WritableOrganizationStore().With("org-a", "acme", "Acme").With("org-b", "beta", "Beta");
        var members = new WritableOrganizationMembershipStore().With("org-a", "user-1").With("org-b", "user-1");
        var user = User(organizationId: "org-b");

        var subject = Allowed(await ResolveAsync(Resolver(user, Client(), orgs, members), user, organization: "acme"));

        Assert.Equal("org-a", subject.OrganizationId);
        Assert.Equal("acme", subject.OrganizationSlug);
        Assert.Equal("Acme", subject.OrganizationName);
    }

    /// The parameter takes a slug or an id, because a relying party migrating from another provider
    /// may hold either and sending the wrong one silently should not be possible.
    [Fact]
    public async Task Parameter_ResolvesBySlugOrById()
    {
        var orgs = new WritableOrganizationStore().With("org-a", "acme", "Acme");
        var members = new WritableOrganizationMembershipStore().With("org-a", "user-1");
        var user = User();

        var bySlug = Allowed(await ResolveAsync(Resolver(user, Client(), orgs, members), user, "acme"));
        var byId = Allowed(await ResolveAsync(Resolver(user, Client(), orgs, members), user, "org-a"));

        Assert.Equal("org-a", bySlug.OrganizationId);
        Assert.Equal("org-a", byId.OrganizationId);
    }

    /// Rule 2: a client restricted to exactly one organisation names it for every request, so a
    /// per-customer application needs no parameter at all. This is the Mobiom shape.
    [Fact]
    public async Task SingleClientRestriction_SelectsWithoutAParameter()
    {
        var orgs = new WritableOrganizationStore().With("org-a", "acme", "Acme");
        var members = new WritableOrganizationMembershipStore().With("org-a", "user-1");
        var user = User();

        var subject = Allowed(await ResolveAsync(Resolver(user, Client("org-a"), orgs, members), user));

        Assert.Equal("org-a", subject.OrganizationId);
        Assert.Equal("acme", subject.OrganizationSlug);
    }

    /// Rule 3: the account's own organisation, when it resolves to a real record, brings the slug and
    /// name with it.
    [Fact]
    public async Task AccountDefault_ResolvesToTheRecordWhenOneExists()
    {
        var orgs = new WritableOrganizationStore().With("org-a", "acme", "Acme");
        var user = User(organizationId: "org-a");

        var subject = Allowed(await ResolveAsync(Resolver(user, Client(), orgs), user));

        Assert.Equal("org-a", subject.OrganizationId);
        Assert.Equal("acme", subject.OrganizationSlug);
        Assert.Equal("Acme", subject.OrganizationName);
    }

    /// Membership is demanded for an explicit selection but NOT for the account's own default —
    /// otherwise creating an organization whose id matches an existing tag would lock out every user
    /// carrying it, which is precisely what the backfill has not run yet at that moment.
    [Fact]
    public async Task AccountDefault_DoesNotDemandMembership()
    {
        var orgs = new WritableOrganizationStore().With("org-a", "acme", "Acme", requireMembership: true);
        var user = User(organizationId: "org-a");

        var subject = Allowed(await ResolveAsync(Resolver(user, Client(), orgs), user));

        Assert.Equal("org-a", subject.OrganizationId);
    }

    // -----------------------------------------------------------------------
    // Refusals
    // -----------------------------------------------------------------------

    [Fact]
    public async Task UnknownOrganization_IsRefusedRatherThanIgnored()
    {
        var user = User(organizationId: "org-a");
        var orgs = new WritableOrganizationStore().With("org-a", "acme", "Acme");

        var rejected = Rejected(await ResolveAsync(Resolver(user, Client(), orgs), user, "no-such-org"));

        Assert.Equal(OidcRejection.AccessDenied, rejected.Reason);
        // The description reaches the relying party as error_description — into its logs and often
        // onto a screen — so it must NOT echo the caller-supplied value back. The detail lives on the
        // server's Debug log instead.
        Assert.DoesNotContain("no-such-org", rejected.Description);
        Assert.Contains("does not exist", rejected.Description);
    }

    [Fact]
    public async Task DisabledOrganization_IsRefused()
    {
        var orgs = new WritableOrganizationStore().With("org-a", "acme", "Acme", enabled: false);
        var members = new WritableOrganizationMembershipStore().With("org-a", "user-1");
        var user = User();

        var rejected = Rejected(await ResolveAsync(Resolver(user, Client(), orgs, members), user, "acme"));

        Assert.Equal(OidcRejection.AccessDenied, rejected.Reason);
        Assert.Contains("disabled", rejected.Description);
    }

    [Fact]
    public async Task NonMember_IsRefusedForAnExplicitSelection()
    {
        var orgs = new WritableOrganizationStore().With("org-a", "acme", "Acme");
        var user = User();

        var rejected = Rejected(await ResolveAsync(Resolver(user, Client(), orgs), user, "acme"));

        Assert.Equal(OidcRejection.AccessDenied, rejected.Reason);
        Assert.Contains("not an active member", rejected.Description);
    }

    /// Invited-but-unaccepted and suspended are both memberships, and neither authorises. Only
    /// `active` does.
    [Theory]
    [InlineData(MembershipStatus.Invited)]
    [InlineData(MembershipStatus.Suspended)]
    public async Task InactiveMembership_DoesNotAuthorise(string status)
    {
        var orgs = new WritableOrganizationStore().With("org-a", "acme", "Acme");
        var members = new WritableOrganizationMembershipStore().With("org-a", "user-1", status);
        var user = User();

        var rejected = Rejected(await ResolveAsync(Resolver(user, Client(), orgs, members), user, "acme"));

        Assert.Equal(OidcRejection.AccessDenied, rejected.Reason);
    }

    /// A membership-gated organisation with the gate turned off issues to anyone who can name it —
    /// the opt-out for a deployment using organisations for branding and routing rather than access.
    [Fact]
    public async Task MembershipGateOff_IssuesToANonMember()
    {
        var orgs = new WritableOrganizationStore().With("org-a", "acme", "Acme", requireMembership: false);
        var user = User();

        var subject = Allowed(await ResolveAsync(Resolver(user, Client(), orgs), user, "acme"));

        Assert.Equal("org-a", subject.OrganizationId);
    }

    [Fact]
    public async Task ClientRestriction_RefusesAnOrganizationNotOnItsList()
    {
        var orgs = new WritableOrganizationStore().With("org-a", "acme", "Acme").With("org-b", "beta", "Beta");
        var members = new WritableOrganizationMembershipStore().With("org-b", "user-1");
        var user = User();

        var rejected = Rejected(await ResolveAsync(Resolver(user, Client("org-a"), orgs, members), user, "beta"));

        Assert.Equal(OidcRejection.AccessDenied, rejected.Reason);
        Assert.Contains("not permitted", rejected.Description);
    }

    /// A client restricted to several organisations cannot be told which by silence, and guessing
    /// would be guessing at whose data the token may reach. account_selection_required is actionable:
    /// the relying party retries with a parameter.
    [Fact]
    public async Task SeveralRestrictedOrganizationsAndNoSelection_AsksForOne()
    {
        var orgs = new WritableOrganizationStore().With("org-a", "acme", "Acme").With("org-b", "beta", "Beta");
        var user = User();

        var rejected = Rejected(await ResolveAsync(Resolver(user, Client("org-a", "org-b"), orgs), user));

        Assert.Equal(OidcRejection.AccountSelectionRequired, rejected.Reason);
    }

    /// A restricted client also refuses a legacy account tag that is not on its list — the client
    /// declared which customers it serves, and "this one has no organization record" is not an
    /// exemption from that.
    [Fact]
    public async Task ClientRestriction_RefusesAnUnlistedLegacyTag()
    {
        var orgs = new WritableOrganizationStore().With("org-a", "acme", "Acme");
        var user = User(organizationId: "legacy-other");

        var rejected = Rejected(await ResolveAsync(Resolver(user, Client("org-a"), orgs), user));

        Assert.Equal(OidcRejection.AccessDenied, rejected.Reason);
    }

    // -----------------------------------------------------------------------
    // Organization-scoped roles
    // -----------------------------------------------------------------------

    [Fact]
    public async Task MembershipRoles_AreUnionedForAnExplicitSelection()
    {
        var orgs = new WritableOrganizationStore().With("org-a", "acme", "Acme");
        var members = new WritableOrganizationMembershipStore()
            .With("org-a", "user-1", MembershipStatus.Active, "Auditor");
        var user = User();
        user.Roles.Add("tenant-wide");

        var subject = Allowed(await ResolveAsync(Resolver(user, Client(), orgs, members), user, "acme"));

        Assert.Contains("Auditor", subject.Roles!);
        Assert.Contains("tenant-wide", subject.Roles!);
    }

    /// The isolation property. The same user holds a role in B; a token for A must not carry it.
    [Fact]
    public async Task MembershipRoles_DoNotLeakAcrossOrganizations()
    {
        var orgs = new WritableOrganizationStore().With("org-a", "acme", "Acme").With("org-b", "beta", "Beta");
        var members = new WritableOrganizationMembershipStore()
            .With("org-a", "user-1", MembershipStatus.Active, "Auditor")
            .With("org-b", "user-1", MembershipStatus.Active, "Organisation Manager");
        var user = User();

        var subject = Allowed(await ResolveAsync(Resolver(user, Client(), orgs, members), user, "acme"));

        Assert.Contains("Auditor", subject.Roles!);
        Assert.DoesNotContain("Organisation Manager", subject.Roles!);
    }

    /// An organisation that turned the membership gate off can still grant roles to the members it
    /// does have — the gate decides who may in, not what a member holds.
    [Fact]
    public async Task MembershipRoles_AreGrantedEvenWhenTheGateIsOff()
    {
        var orgs = new WritableOrganizationStore().With("org-a", "acme", "Acme", requireMembership: false);
        var members = new WritableOrganizationMembershipStore()
            .With("org-a", "user-1", MembershipStatus.Active, "Auditor");
        var user = User();

        var subject = Allowed(await ResolveAsync(Resolver(user, Client(), orgs, members), user, "acme"));

        Assert.Contains("Auditor", subject.Roles!);
    }

    [Theory]
    [InlineData(MembershipStatus.Invited)]
    [InlineData(MembershipStatus.Suspended)]
    public async Task MembershipRoles_AnInactiveMembershipGrantsNone(string status)
    {
        var orgs = new WritableOrganizationStore().With("org-a", "acme", "Acme", requireMembership: false);
        var members = new WritableOrganizationMembershipStore().With("org-a", "user-1", status, "Ghost");
        var user = User();

        var subject = Allowed(await ResolveAsync(Resolver(user, Client(), orgs, members), user, "acme"));

        Assert.Null(subject.Roles);
    }

    /// An inherited organisation was never proven to be the one this request acts for, so its roles
    /// are not this request's authority — the same asymmetry the membership gate has.
    [Fact]
    public async Task MembershipRoles_AreNotUnionedForAnInheritedOrganization()
    {
        var orgs = new WritableOrganizationStore().With("org-a", "acme", "Acme", requireMembership: false);
        var members = new WritableOrganizationMembershipStore()
            .With("org-a", "user-1", MembershipStatus.Active, "Auditor");
        var user = User(organizationId: "org-a");

        var subject = Allowed(await ResolveAsync(Resolver(user, Client(), orgs, members), user));

        Assert.Equal("org-a", subject.OrganizationId);
        Assert.Null(subject.Roles);
    }

    /// The regression: no organisation, so the role set is untouched.
    [Fact]
    public async Task MembershipRoles_NoOrganization_LeavesRolesUnchanged()
    {
        var user = User();
        user.Roles.Add("tenant-wide");

        var subject = Allowed(await ResolveAsync(Resolver(user, Client()), user));

        Assert.Equal(["tenant-wide"], subject.Roles!);
    }

    /// Roles are carried across the rotation because the selection is, and are re-read from the
    /// membership row each time — so changing a role reaches a live session at the next refresh.
    [Fact]
    public async Task MembershipRoles_AreReReadOnRefresh()
    {
        var orgs = new WritableOrganizationStore().With("org-a", "acme", "Acme");
        var members = new WritableOrganizationMembershipStore()
            .With("org-a", "user-1", MembershipStatus.Active, "Auditor");
        var user = User(organizationId: "org-b");
        var resolver = Resolver(user, Client(), orgs, members);

        var atLogin = Allowed(await ResolveAsync(resolver, user, "acme"));
        Assert.Contains("Auditor", atLogin.Roles!);

        members.With("org-a", "user-1", MembershipStatus.Active, "Site Manager");

        var afterRefresh = Allowed(await resolver.ResolveRefreshAsync(
            atLogin, new OidcSubjectResolutionContext(ClientId, ["openid", "roles"], [])));

        Assert.Contains("Site Manager", afterRefresh.Roles!);
        Assert.DoesNotContain("Auditor", afterRefresh.Roles!);
    }

    // -----------------------------------------------------------------------
    // Refresh carry-forward — the regression this design exists to prevent
    // -----------------------------------------------------------------------

    /// THE test. A session that selected organisation A through the parameter must still be for A
    /// after a rotation, even though the account's own default is B and every other field on the
    /// subject is rebuilt from the user store. Without the carry-forward this returns "org-b".
    [Fact]
    public async Task Refresh_CarriesTheSelectedOrganization_NotTheAccountDefault()
    {
        var orgs = new WritableOrganizationStore().With("org-a", "acme", "Acme").With("org-b", "beta", "Beta");
        var members = new WritableOrganizationMembershipStore().With("org-a", "user-1").With("org-b", "user-1");
        var user = User(organizationId: "org-b");
        var resolver = Resolver(user, Client(), orgs, members);

        var atLogin = Allowed(await ResolveAsync(resolver, user, organization: "acme"));
        Assert.Equal("org-a", atLogin.OrganizationId);

        var afterRefresh = Allowed(await resolver.ResolveRefreshAsync(
            atLogin, new OidcSubjectResolutionContext(ClientId, ["openid", "profile"], [])));

        Assert.Equal("org-a", afterRefresh.OrganizationId);
        Assert.Equal("acme", afterRefresh.OrganizationSlug);
        Assert.Equal("Acme", afterRefresh.OrganizationName);
    }

    /// Revoking a membership has to reach a live session, and the refresh rotation is the only place
    /// it can: rejecting here revokes the chain instead of re-minting for up to the absolute refresh
    /// lifetime.
    [Fact]
    public async Task Refresh_RefusesOnceTheMembershipIsRevoked()
    {
        var orgs = new WritableOrganizationStore().With("org-a", "acme", "Acme");
        var members = new WritableOrganizationMembershipStore().With("org-a", "user-1");
        var user = User(organizationId: "org-b");
        var resolver = Resolver(user, Client(), orgs, members);

        var atLogin = Allowed(await ResolveAsync(resolver, user, organization: "acme"));
        await members.DeleteAsync("org-a", "user-1");

        var rejected = Rejected(await resolver.ResolveRefreshAsync(
            atLogin, new OidcSubjectResolutionContext(ClientId, ["openid", "profile"], [])));

        Assert.Equal(OidcRejection.AccessDenied, rejected.Reason);
    }

    /// Disabling an organisation ends its live sessions at their next rotation, for the same reason.
    [Fact]
    public async Task Refresh_RefusesOnceTheOrganizationIsDisabled()
    {
        var orgs = new WritableOrganizationStore().With("org-a", "acme", "Acme");
        var members = new WritableOrganizationMembershipStore().With("org-a", "user-1");
        var user = User(organizationId: "org-b");
        var resolver = Resolver(user, Client(), orgs, members);

        var atLogin = Allowed(await ResolveAsync(resolver, user, organization: "acme"));
        await orgs.UpsertAsync(new Organization
        {
            Id = "org-a", Slug = "acme", DisplayName = "Acme", Enabled = false,
        });

        var rejected = Rejected(await resolver.ResolveRefreshAsync(
            atLogin, new OidcSubjectResolutionContext(ClientId, ["openid", "profile"], [])));

        Assert.Equal(OidcRejection.AccessDenied, rejected.Reason);
    }

    /// When nothing was explicitly selected, the organisation is re-derived on every rotation — which
    /// is what this resolver has always done, and is what keeps an operator's re-tagging of an account
    /// taking effect rather than being pinned by a months-old refresh chain.
    [Fact]
    public async Task Refresh_ReDerivesWhenNothingWasExplicitlySelected()
    {
        var orgs = new WritableOrganizationStore().With("org-a", "acme", "Acme").With("org-b", "beta", "Beta");
        var user = User(organizationId: "org-a");
        var users = new InMemoryUserStore();
        await users.CreateAsync(user);
        var resolver = ResolverTestSupport.NewResolver(
            users, new InMemoryScimGroupStore(), new WritableScimGroupRoleMappingStore(), new InMemoryClientStore(),
            organizationSelector: new OrganizationSelector(
                orgs, new WritableOrganizationMembershipStore(), NullLogger<OrganizationSelector>.Instance));

        var atLogin = Allowed(await resolver.ResolveAsync(
            Principal(user.Id), new OidcSubjectResolutionContext(ClientId, ["openid"], [])));
        Assert.Equal("org-a", atLogin.OrganizationId);

        // An operator re-tags the account.
        var retagged = await users.GetAsync(user.Id);
        retagged!.OrganizationId = "org-b";
        await users.UpdateAsync(retagged);

        var afterRefresh = Allowed(await resolver.ResolveRefreshAsync(
            atLogin, new OidcSubjectResolutionContext(ClientId, ["openid"], [])));

        Assert.Equal("org-b", afterRefresh.OrganizationId);
    }
}
