using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Authagonal.AwsProvider.Dynamo;
using Authagonal.AwsProvider.Stores;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// DynamoOrganizationStore / DynamoOrganizationMembershipStore behavior against real DynamoDB
/// semantics (DynamoDB Local): the organization document round-trip, the immutable-slug contract
/// (rejecting a change, a cross-namespace collision with an existing id/slug, and an invalid slug
/// format), the insert-only atomicity that lets exactly one concurrent creator of a brand-new slug
/// win, the membership dual-index kept in sync through upsert/delete (including an orphaned-row
/// delete), both membership listings, change-log tombstones on upsert AND delete, and env-partitioned
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

    private static async Task<bool> SawOp(DynamoTable log, string table, string op)
    {
        var saw = false;
        await foreach (var item in log.QueryAsync(table))
            saw |= item["op"].S == op;
        return saw;
    }

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

        // Upsert replaces in place (slug unchanged).
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

        // The original organization and the rejected id are both untouched by the refused write.
        Assert.Equal("Acme", (await store.GetAsync("org-1"))?.DisplayName);
        Assert.Null(await store.GetAsync("org-2"));

        // Re-upserting org-1 itself under its own (unchanged) slug is not a conflict.
        await store.UpsertAsync(new Organization { Id = "org-1", Slug = "acme", DisplayName = "Acme Renamed", CreatedAt = DateTimeOffset.UtcNow });
        Assert.Equal("Acme Renamed", (await store.GetAsync("org-1"))?.DisplayName);
    }

    /// F11: the slug is immutable once an organization exists. A rename is refused outright, and the
    /// refusal changes nothing — no new slug row is claimed and the original document is untouched.
    [Fact]
    public async Task OrganizationStore_UpsertRejects_SlugChange_ForExistingOrganization()
    {
        var store = await NewOrgStoreAsync("immutable");
        var org = new Organization { Id = "org-1", Slug = "acme", DisplayName = "Acme", CreatedAt = DateTimeOffset.UtcNow };
        await store.UpsertAsync(org);

        org.Slug = "acme-corp";
        org.DisplayName = "Acme Renamed"; // paired with an otherwise-valid field change
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpsertAsync(org));
        Assert.Contains("acme", ex.Message, StringComparison.Ordinal);
        Assert.Contains("acme-corp", ex.Message, StringComparison.Ordinal);

        var unchanged = await store.GetAsync("org-1");
        Assert.Equal("acme", unchanged?.Slug);
        Assert.Equal("Acme", unchanged?.DisplayName); // the paired field change did not apply either
        Assert.Equal("org-1", (await store.GetBySlugAsync("acme"))?.Id);
        Assert.Null(await store.GetBySlugAsync("acme-corp")); // never claimed
    }

    /// F4: the slug row for a brand-new organization is claimed with an insert-only conditional
    /// write, not a read-then-write — so of several concurrent creators of the same new slug, exactly
    /// one may win, against real DynamoDB semantics.
    [Fact]
    public async Task OrganizationStore_ConcurrentUpsert_SameNewSlug_ExactlyOneSucceeds()
    {
        var store = await NewOrgStoreAsync("race");

        var attempts = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            try
            {
                await store.UpsertAsync(new Organization
                {
                    Id = $"org-{i}", Slug = "acme", DisplayName = $"Org {i}", CreatedAt = DateTimeOffset.UtcNow,
                });
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }));

        var results = await Task.WhenAll(attempts);
        Assert.Equal(1, results.Count(r => r));
        Assert.Single(await store.ListAsync());
        Assert.NotNull(await store.GetBySlugAsync("acme"));
    }

    /// Cross-namespace rule, direction 1: a NEW organization's slug must not equal another
    /// organization's id.
    [Fact]
    public async Task OrganizationStore_UpsertRejects_SlugEqualToAnExistingOrganizationsId()
    {
        var store = await NewOrgStoreAsync("xns1");
        await store.UpsertAsync(new Organization { Id = "acme", Slug = "acme-inc", DisplayName = "Acme", CreatedAt = DateTimeOffset.UtcNow });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.UpsertAsync(new Organization { Id = "org-2", Slug = "acme", DisplayName = "Impostor", CreatedAt = DateTimeOffset.UtcNow }));
        Assert.Contains("acme", ex.Message, StringComparison.Ordinal);

        Assert.Null(await store.GetAsync("org-2"));
        Assert.Null(await store.GetBySlugAsync("acme")); // the slug was never claimed
    }

    /// Cross-namespace rule, direction 2: a NEW organization's id must not equal another
    /// organization's slug.
    [Fact]
    public async Task OrganizationStore_UpsertRejects_IdEqualToAnExistingOrganizationsSlug()
    {
        var store = await NewOrgStoreAsync("xns2");
        await store.UpsertAsync(new Organization { Id = "org-1", Slug = "acme", DisplayName = "Acme", CreatedAt = DateTimeOffset.UtcNow });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.UpsertAsync(new Organization { Id = "acme", Slug = "impostor", DisplayName = "Impostor", CreatedAt = DateTimeOffset.UtcNow }));
        Assert.Contains("acme", ex.Message, StringComparison.Ordinal);

        Assert.Null(await store.GetAsync("acme"));
        Assert.Null(await store.GetBySlugAsync("impostor")); // never claimed
    }

    [Theory]
    [InlineData("fmt1", "")]
    [InlineData("fmt2", "Acme")]        // uppercase
    [InlineData("fmt3", "-acme")]       // leading hyphen
    [InlineData("fmt4", "acme-")]       // trailing hyphen
    [InlineData("fmt5", "ac me")]       // space
    [InlineData("fmt6", "acme_corp")]   // underscore
    public async Task OrganizationStore_UpsertRejects_InvalidSlugFormat(string prefix, string invalidSlug)
    {
        var store = await NewOrgStoreAsync(prefix);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.UpsertAsync(new Organization { Id = "org-1", Slug = invalidSlug, DisplayName = "Acme", CreatedAt = DateTimeOffset.UtcNow }));

        Assert.Null(await store.GetAsync("org-1")); // nothing written
    }

    [Fact]
    public async Task OrganizationStore_UpsertAccepts_ValidBoundarySlugs()
    {
        var store = await NewOrgStoreAsync("fmtok");
        await store.UpsertAsync(new Organization { Id = "org-1", Slug = "a", DisplayName = "Single char", CreatedAt = DateTimeOffset.UtcNow });
        await store.UpsertAsync(new Organization { Id = "org-2", Slug = "acme-corp-2", DisplayName = "Hyphenated", CreatedAt = DateTimeOffset.UtcNow });

        Assert.Equal("org-1", (await store.GetBySlugAsync("a"))?.Id);
        Assert.Equal("org-2", (await store.GetBySlugAsync("acme-corp-2"))?.Id);
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

        Assert.True(await SawOp(log, "Organizations", "D"));
        Assert.True(await SawOp(log, "OrganizationSlugs", "D"));
    }

    /// F12: upsert records a change for both the organization row and the slug row on creation, and
    /// keeps recording one for the organization row on every subsequent update.
    [Fact]
    public async Task OrganizationStore_Upsert_RecordsChangeForBothRows()
    {
        var log = await T("orgUpsertTombstones");
        var writer = new DynamoChangeWriter(log);
        var store = await NewOrgStoreAsync("upsertTomb", tombstones: writer);

        await store.UpsertAsync(new Organization { Id = "org-1", Slug = "acme", DisplayName = "Acme", CreatedAt = DateTimeOffset.UtcNow });
        Assert.True(await SawOp(log, "Organizations", "U"));
        Assert.True(await SawOp(log, "OrganizationSlugs", "U"));

        // The change log holds one entry per row (keyed by table + pk + sk), so a same-row upsert
        // overwrites rather than appends — delete flips the entry to "D", and a fresh upsert must
        // flip it back to "U", which only happens if UpsertAsync itself writes the change record
        // (rather than the first "U" being a leftover that a plain update never touches again).
        await store.DeleteAsync("org-1");
        Assert.True(await SawOp(log, "Organizations", "D"));
        Assert.True(await SawOp(log, "OrganizationSlugs", "D"));

        await store.UpsertAsync(new Organization { Id = "org-1", Slug = "acme", DisplayName = "Acme reborn", CreatedAt = DateTimeOffset.UtcNow });
        Assert.True(await SawOp(log, "Organizations", "U"));
        Assert.True(await SawOp(log, "OrganizationSlugs", "U"));
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

    /// F18: delete must not early-return just because the forward row happens to be missing — it
    /// still removes (and tombstones) whichever row IS present, so a membership left inconsistent by
    /// an earlier partial write is fully cleaned up rather than left half-orphaned forever.
    [Fact]
    public async Task MembershipStore_Delete_RemovesBothRows_EvenWhenForwardItemIsMissing()
    {
        var orgMembersTable = await T("orphanOrgMembers");
        var userMembershipsTable = await T("orphanUserMemberships");
        var log = await T("orphanTombstones");
        var writer = new DynamoChangeWriter(log);
        var store = new DynamoOrganizationMembershipStore(orgMembersTable, userMembershipsTable, EnvPartitioner.Live, writer);

        // Simulate a partially-written membership: only the reverse (user) row exists, as if a crash
        // landed between the two Puts inside UpsertAsync. The forward (org) row was never written.
        var membershipJson = JsonSerializer.Serialize(
            new OrganizationMembership { OrganizationId = "org-1", UserId = "u1" },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var reverseItem = new Dictionary<string, AttributeValue>
        {
            ["pk"] = new AttributeValue { S = EnvPartitioner.Live.PK("user|u1") },
            ["sk"] = new AttributeValue { S = "org-1" },
            ["data"] = new AttributeValue { S = membershipJson },
        };
        await userMembershipsTable.PutAsync(reverseItem);

        Assert.Null(await store.GetAsync("org-1", "u1")); // forward row absent — GetAsync is a point read on it
        Assert.Single(await store.ListByUserAsync("u1")); // the reverse row is there

        await store.DeleteAsync("org-1", "u1"); // must not early-return because the forward row is missing

        Assert.Empty(await store.ListByUserAsync("u1")); // the orphaned reverse row is now gone
        Assert.True(await SawOp(log, "OrganizationMembers", "D"));
        Assert.True(await SawOp(log, "UserMemberships", "D"));
    }

    [Fact]
    public async Task MembershipStore_Delete_RecordsTombstonesForBothRows()
    {
        var log = await T("membershipTombstones");
        var writer = new DynamoChangeWriter(log);
        var store = await NewMembershipStoreAsync("tomb", tombstones: writer);

        await store.UpsertAsync(new OrganizationMembership { OrganizationId = "org-1", UserId = "u1" });
        await store.DeleteAsync("org-1", "u1");

        Assert.True(await SawOp(log, "OrganizationMembers", "D"));
        Assert.True(await SawOp(log, "UserMemberships", "D"));
    }

    /// F12: upsert records a change for both the forward and reverse rows.
    [Fact]
    public async Task MembershipStore_Upsert_RecordsChangeForBothRows()
    {
        var log = await T("membershipUpsertTombstones");
        var writer = new DynamoChangeWriter(log);
        var store = await NewMembershipStoreAsync("upsertTomb", tombstones: writer);

        await store.UpsertAsync(new OrganizationMembership { OrganizationId = "org-1", UserId = "u1" });

        Assert.True(await SawOp(log, "OrganizationMembers", "U"));
        Assert.True(await SawOp(log, "UserMemberships", "U"));
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
