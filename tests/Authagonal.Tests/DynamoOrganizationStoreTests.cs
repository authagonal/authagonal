using Amazon.DynamoDBv2;
using Authagonal.AwsProvider.Dynamo;
using Authagonal.AwsProvider.Stores;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// DynamoOrganizationStore / DynamoOrganizationMembershipStore behavior against real DynamoDB
/// semantics (DynamoDB Local): the organization document round-trip, the slug lookup's point-read
/// shape (including a rename and a same-slug conflict), the membership dual-index kept in sync
/// through upsert/delete, both membership listings, change-log tombstones, and env-partitioned
/// isolation — the same coverage shape as <see cref="DynamoUserStoreTests"/> and
/// <see cref="DynamoStoreSmokeTests"/> for their stores.
/// </summary>
[Collection("Dynamo")]
public class DynamoOrganizationStoreTests(DynamoFixture dynamo)
{
    private readonly IAmazonDynamoDB _db = dynamo.CreateClient();

    private async Task<DynamoTable> T(string name)
    {
        await DynamoTableProvisioner.EnsureTableAsync(_db, name);
        return new DynamoTable(_db, name);
    }

    private async Task<DynamoOrganizationStore> NewOrgStoreAsync(string prefix, EnvPartitioner? partitioner = null, IChangeWriter? tombstones = null) =>
        new(await T($"{prefix}Orgs"), await T($"{prefix}OrgSlugs"), partitioner ?? EnvPartitioner.Live, tombstones);

    private async Task<DynamoOrganizationMembershipStore> NewMembershipStoreAsync(string prefix, EnvPartitioner? partitioner = null, IChangeWriter? tombstones = null) =>
        new(await T($"{prefix}OrgMembers"), await T($"{prefix}UserMemberships"), partitioner ?? EnvPartitioner.Live, tombstones);

    // ----- IOrganizationStore ------------------------------------------------------

    [Fact]
    public async Task OrganizationStore_RoundTrips_EveryField()
    {
        var store = await NewOrgStoreAsync("rt");
        var created = DateTimeOffset.UtcNow;

        var org = new Organization
        {
            Id = "org-1",
            Slug = "acme",
            DisplayName = "Acme Corp",
            Metadata = new Dictionary<string, string> { ["tier"] = "enterprise" },
            BrandingJson = """{"logo":"https://cdn.example.com/acme.svg"}""",
            Enabled = true,
            RequireMembershipForTokens = true,
            AllowAutoMembership = true,
            CreatedAt = created,
        };
        await store.UpsertAsync(org);

        var read = await store.GetAsync("org-1");
        Assert.NotNull(read);
        Assert.Equal("acme", read!.Slug);
        Assert.Equal("Acme Corp", read.DisplayName);
        Assert.Equal("enterprise", read.Metadata["tier"]);
        Assert.Equal("""{"logo":"https://cdn.example.com/acme.svg"}""", read.BrandingJson);
        Assert.True(read.Enabled);
        Assert.True(read.RequireMembershipForTokens);
        Assert.True(read.AllowAutoMembership);
        Assert.Equal(created, read.CreatedAt);
        Assert.Null(read.UpdatedAt);

        // Upsert replaces in place.
        org.DisplayName = "Acme Corp v2";
        org.UpdatedAt = DateTimeOffset.UtcNow;
        await store.UpsertAsync(org);
        var updated = await store.GetAsync("org-1");
        Assert.Equal("Acme Corp v2", updated?.DisplayName);
        Assert.NotNull(updated!.UpdatedAt);
    }

    [Fact]
    public async Task OrganizationStore_GetBySlug_IsAPointLookup_CaseSensitive_AndUnknownIsNull()
    {
        var store = await NewOrgStoreAsync("slug");
        await store.UpsertAsync(new Organization { Id = "org-1", Slug = "acme", DisplayName = "Acme", CreatedAt = DateTimeOffset.UtcNow });

        Assert.Equal("org-1", (await store.GetBySlugAsync("acme"))?.Id);
        Assert.Null(await store.GetBySlugAsync("ACME")); // slugs are not case-folded, unlike SSO domains
        Assert.Null(await store.GetBySlugAsync("nowhere"));
    }

    [Fact]
    public async Task OrganizationStore_UpsertRejects_SlugHeldByADifferentOrganization()
    {
        var store = await NewOrgStoreAsync("conflict");
        await store.UpsertAsync(new Organization { Id = "org-1", Slug = "acme", DisplayName = "Acme", CreatedAt = DateTimeOffset.UtcNow });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.UpsertAsync(new Organization { Id = "org-2", Slug = "acme", DisplayName = "Other Acme", CreatedAt = DateTimeOffset.UtcNow }));
        Assert.Contains("acme", ex.Message, StringComparison.Ordinal);
        Assert.Contains("org-1", ex.Message, StringComparison.Ordinal);

        // The original organization and the rejected id are both untouched by the refused write.
        Assert.Equal("Acme", (await store.GetAsync("org-1"))?.DisplayName);
        Assert.Null(await store.GetAsync("org-2"));

        // Re-upserting org-1 itself under the same slug is not a conflict.
        await store.UpsertAsync(new Organization { Id = "org-1", Slug = "acme", DisplayName = "Acme Renamed", CreatedAt = DateTimeOffset.UtcNow });
        Assert.Equal("Acme Renamed", (await store.GetAsync("org-1"))?.DisplayName);
    }

    [Fact]
    public async Task OrganizationStore_SlugChange_RemovesTheStaleLookupRow_AndFreesItForReuse()
    {
        var store = await NewOrgStoreAsync("rename");
        var org = new Organization { Id = "org-1", Slug = "acme", DisplayName = "Acme", CreatedAt = DateTimeOffset.UtcNow };
        await store.UpsertAsync(org);
        Assert.Equal("org-1", (await store.GetBySlugAsync("acme"))?.Id);

        org.Slug = "acme-corp";
        await store.UpsertAsync(org);

        Assert.Null(await store.GetBySlugAsync("acme")); // stale row dropped
        Assert.Equal("org-1", (await store.GetBySlugAsync("acme-corp"))?.Id); // new row resolves
        Assert.Equal("acme-corp", (await store.GetAsync("org-1"))?.Slug);

        // The freed slug can now be claimed by a different organization.
        await store.UpsertAsync(new Organization { Id = "org-2", Slug = "acme", DisplayName = "New Acme", CreatedAt = DateTimeOffset.UtcNow });
        Assert.Equal("org-2", (await store.GetBySlugAsync("acme"))?.Id);
    }

    [Fact]
    public async Task OrganizationStore_List_IsUnpaged_AndReflectsDeletes()
    {
        var store = await NewOrgStoreAsync("list");
        for (var i = 0; i < 4; i++)
        {
            await store.UpsertAsync(new Organization
            {
                Id = $"org-{i}", Slug = $"slug-{i}", DisplayName = $"Org {i}", CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        Assert.Equal(4, (await store.ListAsync()).Count);

        await store.DeleteAsync("org-0");
        var remaining = await store.ListAsync();
        Assert.Equal(3, remaining.Count);
        Assert.DoesNotContain(remaining, o => o.Id == "org-0");
    }

    [Fact]
    public async Task OrganizationStore_Delete_RemovesOrganizationAndSlugRow_AndIsANoOpWhenMissing()
    {
        var store = await NewOrgStoreAsync("delete");
        await store.UpsertAsync(new Organization { Id = "org-1", Slug = "acme", DisplayName = "Acme", CreatedAt = DateTimeOffset.UtcNow });

        await store.DeleteAsync("org-1");
        Assert.Null(await store.GetAsync("org-1"));
        Assert.Null(await store.GetBySlugAsync("acme"));
        Assert.Empty(await store.ListAsync());

        await store.DeleteAsync("org-1"); // already gone — no-op
        await store.DeleteAsync("never-existed"); // no-op
    }

    [Fact]
    public async Task OrganizationStore_SandboxEnvs_ShareOneTablePair_WithoutLeaking()
    {
        var orgs = await T("envOrgs");
        var slugs = await T("envOrgSlugs");
        var testEnv = new DynamoOrganizationStore(orgs, slugs, new EnvPartitioner("test"));
        var stagingEnv = new DynamoOrganizationStore(orgs, slugs, new EnvPartitioner("staging"));

        // Same id AND same slug in two different envs — must not collide either way.
        await testEnv.UpsertAsync(new Organization { Id = "org-1", Slug = "acme", DisplayName = "test copy", CreatedAt = DateTimeOffset.UtcNow });
        await stagingEnv.UpsertAsync(new Organization { Id = "org-1", Slug = "acme", DisplayName = "staging copy", CreatedAt = DateTimeOffset.UtcNow });

        Assert.Equal("test copy", (await testEnv.GetAsync("org-1"))?.DisplayName);
        Assert.Equal("staging copy", (await stagingEnv.GetAsync("org-1"))?.DisplayName);
        Assert.Equal("test copy", (await testEnv.GetBySlugAsync("acme"))?.DisplayName);
        Assert.Equal("staging copy", (await stagingEnv.GetBySlugAsync("acme"))?.DisplayName);

        Assert.Single(await testEnv.ListAsync());
        Assert.Single(await stagingEnv.ListAsync());

        await testEnv.DeleteAsync("org-1");
        Assert.Null(await testEnv.GetAsync("org-1"));
        Assert.Equal("staging copy", (await stagingEnv.GetAsync("org-1"))?.DisplayName); // untouched
    }

    [Fact]
    public async Task OrganizationStore_Delete_RecordsTombstonesForBothRows()
    {
        var log = await T("orgTombstones");
        var writer = new DynamoChangeWriter(log);
        var store = await NewOrgStoreAsync("tomb", tombstones: writer);

        await store.UpsertAsync(new Organization { Id = "org-1", Slug = "acme", DisplayName = "Acme", CreatedAt = DateTimeOffset.UtcNow });
        await store.DeleteAsync("org-1");

        var sawOrgDelete = false;
        await foreach (var item in log.QueryAsync("Organizations"))
            sawOrgDelete |= item["op"].S == "D";
        Assert.True(sawOrgDelete);

        var sawSlugDelete = false;
        await foreach (var item in log.QueryAsync("OrganizationSlugs"))
            sawSlugDelete |= item["op"].S == "D";
        Assert.True(sawSlugDelete);
    }

    // ----- IOrganizationMembershipStore --------------------------------------------

    [Fact]
    public async Task MembershipStore_RoundTrips_EveryField()
    {
        var store = await NewMembershipStoreAsync("rt");
        var now = DateTimeOffset.UtcNow;

        var membership = new OrganizationMembership
        {
            OrganizationId = "org-1",
            UserId = "u1",
            Roles = ["auditor", "billing"],
            Status = MembershipStatus.Invited,
            InvitedByUserId = "admin-1",
            InvitedAt = now,
            CreatedAt = now,
        };
        await store.UpsertAsync(membership);

        var read = await store.GetAsync("org-1", "u1");
        Assert.NotNull(read);
        Assert.Equal(["auditor", "billing"], read!.Roles);
        Assert.Equal(MembershipStatus.Invited, read.Status);
        Assert.Equal("admin-1", read.InvitedByUserId);
        Assert.Equal(now, read.InvitedAt);
        Assert.Null(read.JoinedAt);

        // Accepting the invitation is an upsert that replaces the row in both indexes.
        membership.Status = MembershipStatus.Active;
        membership.JoinedAt = now.AddMinutes(5);
        await store.UpsertAsync(membership);

        Assert.Equal(MembershipStatus.Active, (await store.GetAsync("org-1", "u1"))?.Status);
        Assert.Equal(MembershipStatus.Active, (await store.ListByUserAsync("u1")).Single().Status);
        Assert.Equal(MembershipStatus.Active, (await store.ListByOrganizationAsync("org-1")).Single().Status);
    }

    [Fact]
    public async Task MembershipStore_GetAsync_IsAPointLookup_AndUnknownIsNull()
    {
        var store = await NewMembershipStoreAsync("point");
        await store.UpsertAsync(new OrganizationMembership { OrganizationId = "org-1", UserId = "u1" });

        Assert.NotNull(await store.GetAsync("org-1", "u1"));
        Assert.Null(await store.GetAsync("org-1", "u-none"));
        Assert.Null(await store.GetAsync("org-none", "u1"));
    }

    [Fact]
    public async Task MembershipStore_ListByUser_AndListByOrganization_AreIndependentIndexes()
    {
        var store = await NewMembershipStoreAsync("list");

        await store.UpsertAsync(new OrganizationMembership { OrganizationId = "org-1", UserId = "u1" });
        await store.UpsertAsync(new OrganizationMembership { OrganizationId = "org-2", UserId = "u1" });
        await store.UpsertAsync(new OrganizationMembership { OrganizationId = "org-1", UserId = "u2" });

        Assert.Equal(["org-1", "org-2"], (await store.ListByUserAsync("u1")).Select(m => m.OrganizationId).OrderBy(x => x));
        Assert.Empty(await store.ListByUserAsync("u-none"));

        Assert.Equal(["u1", "u2"], (await store.ListByOrganizationAsync("org-1")).Select(m => m.UserId).OrderBy(x => x));
        Assert.Equal("u1", Assert.Single(await store.ListByOrganizationAsync("org-2")).UserId);
        Assert.Empty(await store.ListByOrganizationAsync("org-none"));
    }

    [Fact]
    public async Task MembershipStore_Delete_RemovesBothRows_AndIsANoOpWhenMissing()
    {
        var store = await NewMembershipStoreAsync("delete");
        await store.UpsertAsync(new OrganizationMembership { OrganizationId = "org-1", UserId = "u1" });
        await store.UpsertAsync(new OrganizationMembership { OrganizationId = "org-1", UserId = "u2" });

        await store.DeleteAsync("org-1", "u1");
        Assert.Null(await store.GetAsync("org-1", "u1"));
        Assert.Empty(await store.ListByUserAsync("u1"));
        Assert.Equal("u2", Assert.Single(await store.ListByOrganizationAsync("org-1")).UserId);

        await store.DeleteAsync("org-1", "u1"); // already gone — no-op
        await store.DeleteAsync("org-none", "u-none"); // never existed — no-op
    }

    [Fact]
    public async Task MembershipStore_Delete_RecordsTombstonesForBothRows()
    {
        var log = await T("membershipTombstones");
        var writer = new DynamoChangeWriter(log);
        var store = await NewMembershipStoreAsync("tomb", tombstones: writer);

        await store.UpsertAsync(new OrganizationMembership { OrganizationId = "org-1", UserId = "u1" });
        await store.DeleteAsync("org-1", "u1");

        var sawOrgMemberDelete = false;
        await foreach (var item in log.QueryAsync("OrganizationMembers"))
            sawOrgMemberDelete |= item["op"].S == "D";
        Assert.True(sawOrgMemberDelete);

        var sawUserMembershipDelete = false;
        await foreach (var item in log.QueryAsync("UserMemberships"))
            sawUserMembershipDelete |= item["op"].S == "D";
        Assert.True(sawUserMembershipDelete);
    }

    [Fact]
    public async Task MembershipStore_SandboxEnvs_ShareOneTablePair_WithoutLeaking()
    {
        var orgMembers = await T("envOrgMembers");
        var userMemberships = await T("envUserMemberships");
        var testEnv = new DynamoOrganizationMembershipStore(orgMembers, userMemberships, new EnvPartitioner("test"));
        var stagingEnv = new DynamoOrganizationMembershipStore(orgMembers, userMemberships, new EnvPartitioner("staging"));

        await testEnv.UpsertAsync(new OrganizationMembership { OrganizationId = "org-1", UserId = "u1", Status = MembershipStatus.Active });
        await stagingEnv.UpsertAsync(new OrganizationMembership { OrganizationId = "org-1", UserId = "u1", Status = MembershipStatus.Invited });

        Assert.Equal(MembershipStatus.Active, (await testEnv.GetAsync("org-1", "u1"))?.Status);
        Assert.Equal(MembershipStatus.Invited, (await stagingEnv.GetAsync("org-1", "u1"))?.Status);
        Assert.Single(await testEnv.ListByUserAsync("u1"));
        Assert.Single(await stagingEnv.ListByUserAsync("u1"));

        await testEnv.DeleteAsync("org-1", "u1");
        Assert.Null(await testEnv.GetAsync("org-1", "u1"));
        Assert.Equal(MembershipStatus.Invited, (await stagingEnv.GetAsync("org-1", "u1"))?.Status); // untouched
    }
}
