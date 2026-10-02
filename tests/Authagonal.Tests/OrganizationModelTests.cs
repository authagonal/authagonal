using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Server.Services;

namespace Authagonal.Tests;

/// <summary>
/// The <see cref="Organization"/> / <see cref="OrganizationMembership"/> models and the empty
/// in-memory defaults registered when a host wires no durable organization store.
/// </summary>
/// <remarks>
/// The defaults matter more than they look. They are what every existing deployment gets on upgrade,
/// and the property being asserted is that they reproduce the PRE-organizations behaviour rather than
/// a degraded version of it: reads answer "no organizations exist", which makes the selection path
/// and the membership gate both inert, while writes refuse loudly instead of accepting authorization
/// state into a process-local dictionary that no other node would ever see revoked.
/// </remarks>
public sealed class OrganizationModelTests
{
    // -----------------------------------------------------------------------
    // Model defaults
    // -----------------------------------------------------------------------

    /// A new organization is enabled and gates on membership. Both defaults are deliberate: an
    /// organization nobody may authenticate as is useless, and one whose membership is advisory is
    /// not a tenancy boundary.
    [Fact]
    public void Organization_DefaultsToEnabledAndMembershipGated()
    {
        var org = new Organization { Id = "org-1", Slug = "acme", DisplayName = "Acme" };

        Assert.True(org.Enabled);
        Assert.True(org.RequireMembershipForTokens);
        Assert.False(org.AllowAutoMembership);
        Assert.Empty(org.Metadata);
        Assert.Null(org.BrandingJson);
        Assert.Empty(org.Domains);
    }

    /// Domains is never null, so every reader (and the auto-membership check) can enumerate it without
    /// a guard — even after a deserializer or a caller assigns null.
    [Fact]
    public void Organization_Domains_NullAssignsEmpty()
    {
        var org = new Organization { Id = "org-1", Slug = "acme", DisplayName = "Acme", Domains = null! };
        Assert.NotNull(org.Domains);
        Assert.Empty(org.Domains);
    }

    /// A domain is stored in one form whoever wrote it, so an exact comparison against an email's
    /// lowercased domain part is a correct comparison.
    [Theory]
    [InlineData("acme.com", "acme.com")]
    [InlineData("  Acme.COM  ", "acme.com")]
    [InlineData("acme.com.", "acme.com")]
    [InlineData(" EU.Acme.Com. ", "eu.acme.com")]
    public void OrganizationDomain_IsNormalisedOnAssignment(string input, string stored)
    {
        Assert.Equal(stored, new OrganizationDomain { Domain = input }.Domain);
        Assert.Equal(stored, OrganizationDomain.Normalize(input));
    }

    /// Membership defaults to active, so a row written by a provisioning sync that says nothing about
    /// status authorises immediately. Only an invitation flow sets Invited explicitly.
    [Fact]
    public void Membership_DefaultsToActiveWithNoRoles()
    {
        var membership = new OrganizationMembership { OrganizationId = "org-1", UserId = "user-1" };

        Assert.Equal(MembershipStatus.Active, membership.Status);
        Assert.Empty(membership.Roles);
        Assert.Null(membership.InvitedByUserId);
        Assert.Null(membership.JoinedAt);
    }

    /// The status values are the wire format stores round-trip, so they are pinned by a test rather
    /// than left to whatever a refactor renames them to.
    [Fact]
    public void MembershipStatus_ValuesAreStable()
    {
        Assert.Equal("invited", MembershipStatus.Invited);
        Assert.Equal("active", MembershipStatus.Active);
        Assert.Equal("suspended", MembershipStatus.Suspended);
    }

    /// Empty, not null — an unrestricted client is the default and callers iterate the list without
    /// a null check, exactly as they do for AllowedScopes and ProvisioningApps.
    [Fact]
    public void OAuthClient_IsOrganizationUnrestrictedByDefault()
    {
        var client = new OAuthClient { ClientId = "c1", ClientName = "C1" };

        Assert.NotNull(client.RestrictedToOrganizationIds);
        Assert.Empty(client.RestrictedToOrganizationIds);
    }

    // -----------------------------------------------------------------------
    // In-memory defaults — reads
    // -----------------------------------------------------------------------

    [Fact]
    public async Task InMemoryOrganizationStore_ReadsAreEmpty()
    {
        IOrganizationStore store = new InMemoryOrganizationStore();

        Assert.Null(await store.GetAsync("org-1"));
        Assert.Null(await store.GetBySlugAsync("acme"));
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task InMemoryOrganizationMembershipStore_ReadsAreEmpty()
    {
        IOrganizationMembershipStore store = new InMemoryOrganizationMembershipStore();

        Assert.Null(await store.GetAsync("org-1", "user-1"));
        Assert.Empty(await store.ListByUserAsync("user-1"));
        Assert.Empty(await store.ListByOrganizationAsync("org-1"));
    }

    // -----------------------------------------------------------------------
    // In-memory defaults — writes refuse
    // -----------------------------------------------------------------------

    /// A silent in-memory accept would let a host believe it had created an organization that no
    /// other node can see and that no restart survives. Refusing names the missing registration.
    [Fact]
    public async Task InMemoryOrganizationStore_WritesRefuse()
    {
        IOrganizationStore store = new InMemoryOrganizationStore();
        var org = new Organization { Id = "org-1", Slug = "acme", DisplayName = "Acme" };

        var upsert = await Assert.ThrowsAsync<NotSupportedException>(() => store.UpsertAsync(org));
        Assert.Contains("durable IOrganizationStore", upsert.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<NotSupportedException>(() => store.DeleteAsync("org-1"));
    }

    [Fact]
    public async Task InMemoryOrganizationMembershipStore_WritesRefuse()
    {
        IOrganizationMembershipStore store = new InMemoryOrganizationMembershipStore();
        var membership = new OrganizationMembership { OrganizationId = "org-1", UserId = "user-1" };

        var upsert = await Assert.ThrowsAsync<NotSupportedException>(() => store.UpsertAsync(membership));
        Assert.Contains("durable IOrganizationMembershipStore", upsert.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<NotSupportedException>(() => store.DeleteAsync("org-1", "user-1"));
    }
}
