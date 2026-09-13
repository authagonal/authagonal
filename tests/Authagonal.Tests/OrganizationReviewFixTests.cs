using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Web;
using Authagonal.Core.Constants;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Protocol;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using System.Security.Claims;

namespace Authagonal.Tests;

/// <summary>
/// Defects found reviewing the organizations work, each with the scenario that produced it.
/// </summary>
/// <remarks>
/// The two that matter most are the id/slug namespace confusion and the reserved-role strip. The first
/// silently re-points a live grant at a different customer the moment anyone takes a slug equal to an
/// existing organisation's id; the second is a privilege escalation from customer-scoped data.
/// </remarks>
public sealed class OrganizationReviewFixTests
{
    private const string ClientId = "client-1";

    // -----------------------------------------------------------------------
    // F3 / F5 — ids are ids
    // -----------------------------------------------------------------------

    /// The exact scenario. A grant is issued for the organisation whose ID is "a1". Later, a DIFFERENT
    /// organisation takes the SLUG "a1". A slug-first lookup on the carried id would hand the live
    /// session to the newcomer on its next refresh; an id-only lookup cannot.
    [Fact]
    public async Task CarriedOrganization_ResolvesByIdEvenWhenAnotherOrgTakesThatValueAsItsSlug()
    {
        var orgs = new WritableOrganizationStore();
        await orgs.UpsertAsync(Org("a1", "acme", "Acme"));
        var members = new WritableOrganizationMembershipStore().With("a1", "user-1");
        var user = User(organizationId: "other");
        var resolver = Resolver(user, Client(), orgs, members);

        var atLogin = Allowed(await ResolveAsync(resolver, user, "acme"));
        Assert.Equal("a1", atLogin.OrganizationId);

        // A second organisation is created whose SLUG is the first one's ID. The store refuses it
        // outright (below); force it in to prove the lookup would still be safe if a legacy row existed.
        await orgs.ForceAsync(Org("b2", "a1", "Impostor"));

        var afterRefresh = Allowed(await resolver.ResolveRefreshAsync(
            atLogin, new OidcSubjectResolutionContext(ClientId, ["openid"], [])));

        Assert.Equal("a1", afterRefresh.OrganizationId);
        Assert.Equal("acme", afterRefresh.OrganizationSlug);
    }

    /// The same confusion through the account's stored tag: a legacy tag is an id this server minted,
    /// so it must never re-resolve to whichever organisation later claims it as a slug.
    [Fact]
    public async Task LegacyTag_NeverReResolvesToADifferentOrganization()
    {
        var orgs = new WritableOrganizationStore();
        await orgs.ForceAsync(Org("b2", "legacy-tag", "Impostor"));
        var user = User(organizationId: "legacy-tag");

        var subject = Allowed(await ResolveAsync(Resolver(user, Client(), orgs), user));

        // The tag resolved as an ID, found nothing, and passed through verbatim — it did NOT become
        // organisation b2.
        Assert.Equal("legacy-tag", subject.OrganizationId);
        Assert.Null(subject.OrganizationSlug);
    }

    /// A client's restricted list holds ids too.
    [Fact]
    public async Task ClientRestriction_ResolvesById_NotBySlug()
    {
        var orgs = new WritableOrganizationStore();
        await orgs.UpsertAsync(Org("a1", "acme", "Acme"));
        await orgs.ForceAsync(Org("b2", "a1", "Impostor"));
        var members = new WritableOrganizationMembershipStore().With("a1", "user-1");
        var user = User();

        var subject = Allowed(await ResolveAsync(Resolver(user, Client("a1"), orgs, members), user));

        Assert.Equal("a1", subject.OrganizationId);
        Assert.Equal("acme", subject.OrganizationSlug);
    }

    /// F5 — the reference store refuses to write the ambiguity in the first place, in both directions.
    [Fact]
    public async Task Store_RefusesASlugThatCollidesWithAnotherOrganizationsId()
    {
        var orgs = new WritableOrganizationStore();
        await orgs.UpsertAsync(Org("a1", "acme", "Acme"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => orgs.UpsertAsync(Org("b2", "a1", "Impostor")));
        Assert.Contains("one namespace", ex.Message);
    }

    [Fact]
    public async Task Store_RefusesAnIdThatCollidesWithAnotherOrganizationsSlug()
    {
        var orgs = new WritableOrganizationStore();
        await orgs.UpsertAsync(Org("a1", "acme", "Acme"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => orgs.UpsertAsync(Org("acme", "beta", "Beta")));
    }

    // -----------------------------------------------------------------------
    // F19 — slug shape
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("acme")]
    [InlineData("a")]
    [InlineData("international-sos")]
    [InlineData("a1b2")]
    public void Slug_AcceptsWellFormedValues(string slug) => Assert.True(OrganizationSlug.IsValid(slug));

    [Theory]
    [InlineData("")]
    [InlineData("Acme")]          // uppercase would be unreachable: the parameter is lowercased
    [InlineData("-acme")]
    [InlineData("acme-")]
    [InlineData("ac me")]
    [InlineData("a/b")]
    [InlineData("a#b")]
    [InlineData("acme_corp")]
    public void Slug_RejectsMalformedValues(string slug) => Assert.False(OrganizationSlug.IsValid(slug));

    [Fact]
    public void Slug_RejectsOverlongValues() =>
        Assert.False(OrganizationSlug.IsValid(new string('a', OrganizationSlug.MaxLength + 1)));

    [Fact]
    public async Task Store_RefusesAMalformedSlug()
    {
        var orgs = new WritableOrganizationStore();
        await Assert.ThrowsAsync<ArgumentException>(() => orgs.UpsertAsync(Org("a1", "Not A Slug", "X")));
    }

    // -----------------------------------------------------------------------
    // F8 — a membership may not grant reserved authority
    // -----------------------------------------------------------------------

    [Fact]
    public async Task MembershipRoles_ReservedPrefixesAreStripped()
    {
        var orgs = new WritableOrganizationStore();
        await orgs.UpsertAsync(Org("a1", "acme", "Acme"));
        var members = new WritableOrganizationMembershipStore()
            .With("a1", "user-1", MembershipStatus.Active,
                "Auditor", "tenant:admin", "platform:owner", "TENANT:Owner");
        var user = User();

        var subject = Allowed(await ResolveAsync(Resolver(user, Client(), orgs, members), user, "acme"));

        Assert.Contains("Auditor", subject.Roles!);
        Assert.DoesNotContain("tenant:admin", subject.Roles!);
        Assert.DoesNotContain("platform:owner", subject.Roles!);
        // Case-insensitively, because a filter a different casing walks past is no filter.
        Assert.DoesNotContain("TENANT:Owner", subject.Roles!);
    }

    /// A membership of nothing but reserved roles grants nothing, rather than failing the login.
    [Fact]
    public async Task MembershipRoles_AllReservedYieldsNoRoles()
    {
        var orgs = new WritableOrganizationStore();
        await orgs.UpsertAsync(Org("a1", "acme", "Acme"));
        var members = new WritableOrganizationMembershipStore()
            .With("a1", "user-1", MembershipStatus.Active, "tenant:admin");
        var user = User();

        var subject = Allowed(await ResolveAsync(Resolver(user, Client(), orgs, members), user, "acme"));

        Assert.Null(subject.Roles);
    }

    [Theory]
    [InlineData("tenant:admin", true)]
    [InlineData("platform:sre", true)]
    [InlineData("Tenant:Admin", true)]
    [InlineData("Auditor", false)]
    [InlineData("tenant-manager", false)]
    [InlineData(null, false)]
    public void ReservedRolePrefixes_Classify(string? role, bool reserved) =>
        Assert.Equal(reserved, ReservedRolePrefixes.IsReserved(role));

    // -----------------------------------------------------------------------
    // Fixtures
    // -----------------------------------------------------------------------

    private static Organization Org(string id, string slug, string name) => new()
    {
        Id = id,
        Slug = slug,
        DisplayName = name,
        CreatedAt = DateTimeOffset.UtcNow,
    };

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

    private static UserStoreOidcSubjectResolver Resolver(
        AuthUser user, OAuthClient client,
        WritableOrganizationStore? organizations = null,
        WritableOrganizationMembershipStore? memberships = null)
    {
        var users = new InMemoryUserStore();
        users.CreateAsync(user).GetAwaiter().GetResult();
        var clients = new InMemoryClientStore();
        clients.UpsertAsync(client).GetAwaiter().GetResult();

        return ResolverTestSupport.NewResolver(
            users, new InMemoryScimGroupStore(), new WritableScimGroupRoleMappingStore(), clients,
            organizationSelector: new OrganizationSelector(
                organizations ?? new WritableOrganizationStore(),
                memberships ?? new WritableOrganizationMembershipStore(),
                NullLogger<OrganizationSelector>.Instance));
    }

    private static Task<OidcSubjectResult> ResolveAsync(
        UserStoreOidcSubjectResolver resolver, AuthUser user, string? organization = null)
    {
        var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id));
        identity.AddClaim(new Claim("sub", user.Id));
        return resolver.ResolveAsync(
            new ClaimsPrincipal(identity),
            new OidcSubjectResolutionContext(ClientId, ["openid", "profile", "roles"], [], organization));
    }

    private static OidcSubject Allowed(OidcSubjectResult result) =>
        Assert.IsType<OidcSubjectResult.Allowed>(result).Subject;
}
