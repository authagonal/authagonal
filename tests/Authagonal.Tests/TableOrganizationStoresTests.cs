using Authagonal.AzureProvider.Entities;
using Authagonal.AzureProvider.Stores;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Tests.Infrastructure;
using Azure.Data.Tables;

namespace Authagonal.Tests;

/// <summary>
/// Azure Table Storage provider for <see cref="Authagonal.Core.Stores.IOrganizationStore"/> and
/// <see cref="Authagonal.Core.Stores.IOrganizationMembershipStore"/>, against Azurite — the house
/// style <see cref="StoreEnvPrefixTests"/> and <see cref="ChangeLogCoverageAdditionsTests"/> use for
/// the sibling stores this design follows: <c>TableScimTokenStore</c>'s dual full-document
/// forward/reverse rows for the membership store, and <c>TableScimGroupStore</c>'s separate index
/// table for the organization store's slug lookup.
/// </summary>
[Collection("Azurite")]
public class TableOrganizationStoresTests(AzuriteFixture azurite)
{
    private readonly TableServiceClient _svc = new(azurite.ConnectionString);

    private TableClient T(string prefix, string name)
    {
        var c = _svc.GetTableClient($"{prefix}{name}");
        c.CreateIfNotExists();
        return c;
    }

    private static string Prefix() => $"org{Guid.NewGuid():N}"[..20];

    private TableOrganizationStore NewOrgStore(string prefix, EnvPartitioner? partitioner = null, IChangeWriter? changeWriter = null) =>
        new(T(prefix, "Organizations"), T(prefix, "OrganizationSlugs"), partitioner ?? EnvPartitioner.Live, changeWriter);

    private TableOrganizationMembershipStore NewMembershipStore(string prefix, EnvPartitioner? partitioner = null, IChangeWriter? changeWriter = null) =>
        new(T(prefix, "OrganizationMembers"), T(prefix, "UserMemberships"), partitioner ?? EnvPartitioner.Live, changeWriter);

    private static Organization Org(string id, string slug, string name = "Acme") => new()
    {
        Id = id,
        Slug = slug,
        DisplayName = name,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static OrganizationMembership Membership(string orgId, string userId, string status = MembershipStatus.Active, params string[] roles) => new()
    {
        OrganizationId = orgId,
        UserId = userId,
        Status = status,
        Roles = [.. roles],
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private async Task<List<TableEntity>> ChangeRows(TableClient log, string changeTable, string op)
    {
        var rows = new List<TableEntity>();
        await foreach (var e in log.QueryAsync<TableEntity>(e => e.PartitionKey == changeTable))
            if (e.GetString("Op") == op) rows.Add(e);
        return rows;
    }

    // ── IOrganizationStore ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Organization_round_trips_by_id_and_lists()
    {
        var p = Prefix();
        var store = NewOrgStore(p);

        var org = Org("org1", "acme");
        org.Metadata["tier"] = "gold";
        org.BrandingJson = "{\"logo\":\"https://x.test/logo.png\"}";
        org.AllowAutoMembership = true;
        org.RequireMembershipForTokens = false;
        org.Enabled = false;

        await store.UpsertAsync(org);

        var read = await store.GetAsync("org1");
        Assert.NotNull(read);
        Assert.Equal("org1", read!.Id);
        Assert.Equal("acme", read.Slug);
        Assert.Equal("Acme", read.DisplayName);
        Assert.Equal("gold", read.Metadata["tier"]);
        Assert.Equal("{\"logo\":\"https://x.test/logo.png\"}", read.BrandingJson);
        Assert.True(read.AllowAutoMembership);
        Assert.False(read.RequireMembershipForTokens);
        Assert.False(read.Enabled);

        var listed = await store.ListAsync();
        var only = Assert.Single(listed);
        Assert.Equal("org1", only.Id);

        Assert.Null(await store.GetAsync("nope"));
    }

    [Fact]
    public async Task GetBySlugAsync_is_a_point_lookup_to_the_same_organization()
    {
        var p = Prefix();
        var store = NewOrgStore(p);
        await store.UpsertAsync(Org("org1", "acme"));

        var bySlug = await store.GetBySlugAsync("acme");
        Assert.NotNull(bySlug);
        Assert.Equal("org1", bySlug!.Id);

        Assert.Null(await store.GetBySlugAsync("nonexistent"));
    }

    [Fact]
    public async Task UpsertAsync_rejects_a_slug_rename_and_changes_nothing()
    {
        // F11: the slug is immutable. This replaces the old rename/stale-slug-row test — renaming is
        // no longer a supported path at all, it is a refusal.
        var p = Prefix();
        var store = NewOrgStore(p);
        var org = Org("org1", "acme");
        await store.UpsertAsync(org);

        var renamed = Org("org1", "acme-renamed");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpsertAsync(renamed));
        Assert.Contains("acme", ex.Message);
        Assert.Contains("acme-renamed", ex.Message);

        // Nothing changed: the original slug still resolves and the attempted new one does not exist.
        var stillThere = await store.GetAsync("org1");
        Assert.Equal("acme", stillThere!.Slug);
        Assert.Equal("org1", (await store.GetBySlugAsync("acme"))!.Id);
        Assert.Null(await store.GetBySlugAsync("acme-renamed"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Acme")]      // uppercase not allowed
    [InlineData("-acme")]     // cannot start with a hyphen
    [InlineData("acme-")]     // cannot end with a hyphen
    [InlineData("ac me")]     // no whitespace
    [InlineData("ac_me")]     // underscore not allowed
    public async Task UpsertAsync_rejects_a_slug_that_does_not_match_the_slug_pattern(string badSlug)
    {
        var p = Prefix();
        var store = NewOrgStore(p);

        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertAsync(Org("org1", badSlug)));
        Assert.Null(await store.GetAsync("org1"));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("a-b")]
    [InlineData("abc-123-xyz")]
    public async Task UpsertAsync_accepts_slugs_matching_the_slug_pattern(string slug)
    {
        var p = Prefix();
        var store = NewOrgStore(p);

        await store.UpsertAsync(Org("org1", slug));
        Assert.Equal(slug, (await store.GetAsync("org1"))!.Slug);
    }

    [Fact]
    public async Task Concurrent_creates_with_the_same_new_slug_race_to_exactly_one_winner()
    {
        // F4: uniqueness is enforced by the storage layer's insert-only primitive, not a
        // read-then-write check — so this must hold under real concurrency against Azurite, not just
        // sequential calls.
        var p = Prefix();
        var store = NewOrgStore(p);

        var results = await Task.WhenAll(
            TryUpsert(store, Org("orgA", "acme")),
            TryUpsert(store, Org("orgB", "acme")));

        Assert.Single(results, r => r is null);
        Assert.Single(results, r => r is InvalidOperationException);

        var winner = await store.GetBySlugAsync("acme");
        Assert.NotNull(winner);
        Assert.True(winner!.Id is "orgA" or "orgB");

        // The loser wrote nothing at all.
        var loserId = winner.Id == "orgA" ? "orgB" : "orgA";
        Assert.Null(await store.GetAsync(loserId));

        // Exactly one slug row exists for "acme".
        var slugsTable = T(p, "OrganizationSlugs");
        var rows = new List<TableEntity>();
        await foreach (var e in slugsTable.QueryAsync<TableEntity>()) rows.Add(e);
        Assert.Single(rows);
    }

    private static async Task<Exception?> TryUpsert(TableOrganizationStore store, Organization org)
    {
        try
        {
            await store.UpsertAsync(org);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Fact]
    public async Task Organization_create_retry_after_crash_between_slug_and_org_write_succeeds()
    {
        // R2: the slug row is written before the org row (F4), so a crash in that exact window leaves
        // a slug row pointing at an id with no org row behind it yet. Retrying the identical create
        // must self-heal (AddEntityAsync's 409 sees its OWN id as the existing owner) rather than
        // fail as "already held" by an organization that, from the org table, does not exist.
        var p = Prefix();
        var store = NewOrgStore(p);

        var slugsTable = T(p, "OrganizationSlugs");
        var orphanSlugRow = OrganizationEntity.CreateSlugIndex(Org("org1", "acme"));
        orphanSlugRow.PartitionKey = EnvPartitioner.Live.PK(orphanSlugRow.PartitionKey);
        await slugsTable.AddEntityAsync(orphanSlugRow);

        // The org row genuinely does not exist yet — this is the crash window, not a normal update.
        Assert.Null(await store.GetAsync("org1"));

        await store.UpsertAsync(Org("org1", "acme"));

        var read = await store.GetAsync("org1");
        Assert.NotNull(read);
        Assert.Equal("acme", read!.Slug);
        Assert.Equal("org1", (await store.GetBySlugAsync("acme"))!.Id);
    }

    [Fact]
    public async Task UpsertAsync_rejects_a_slug_that_collides_with_an_existing_organizations_id()
    {
        var p = Prefix();
        var store = NewOrgStore(p);
        await store.UpsertAsync(Org("org-a", "org-a-slug"));

        // A new organization whose SLUG equals org-a's ID.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.UpsertAsync(Org("org-b", "org-a")));
        Assert.Contains("org-a", ex.Message);

        Assert.Null(await store.GetAsync("org-b"));
        Assert.Null(await store.GetBySlugAsync("org-a"));
    }

    [Fact]
    public async Task UpsertAsync_rejects_an_id_that_collides_with_an_existing_organizations_slug()
    {
        var p = Prefix();
        var store = NewOrgStore(p);
        await store.UpsertAsync(Org("org-a", "acme"));

        // A new organization whose ID equals org-a's SLUG.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.UpsertAsync(Org("acme", "org-b-slug")));
        Assert.Contains("acme", ex.Message);

        Assert.Null(await store.GetAsync("acme"));
        Assert.Null(await store.GetBySlugAsync("org-b-slug"));
    }

    [Fact]
    public async Task UpsertAsync_rejects_a_slug_already_held_by_a_different_organization()
    {
        var p = Prefix();
        var store = NewOrgStore(p);
        await store.UpsertAsync(Org("org1", "acme"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.UpsertAsync(Org("org2", "acme", "Other Co")));
        Assert.Contains("acme", ex.Message);
        Assert.Contains("org1", ex.Message);

        // Rejected before anything was written for org2.
        Assert.Null(await store.GetAsync("org2"));
    }

    [Fact]
    public async Task UpsertAsync_allows_re_saving_an_organization_under_its_own_unchanged_slug()
    {
        var p = Prefix();
        var store = NewOrgStore(p);
        var org = Org("org1", "acme");
        await store.UpsertAsync(org);

        org.DisplayName = "Acme Renamed";
        await store.UpsertAsync(org); // same slug, same id — must not throw or duplicate the index

        var read = await store.GetAsync("org1");
        Assert.Equal("Acme Renamed", read!.DisplayName);
        Assert.Equal("org1", (await store.GetBySlugAsync("acme"))!.Id);
    }

    [Fact]
    public async Task Organization_delete_removes_the_org_and_slug_row_but_not_memberships()
    {
        var p = Prefix();
        var orgStore = NewOrgStore(p);
        var memberStore = NewMembershipStore(p);
        await orgStore.UpsertAsync(Org("org1", "acme"));
        await memberStore.UpsertAsync(Membership("org1", "user1"));

        await orgStore.DeleteAsync("org1");

        Assert.Null(await orgStore.GetAsync("org1"));
        Assert.Null(await orgStore.GetBySlugAsync("acme"));
        // Not cascaded: the membership row survives the organization's own deletion.
        Assert.NotNull(await memberStore.GetAsync("org1", "user1"));
    }

    [Fact]
    public async Task DeleteAsync_on_an_unknown_organization_is_a_no_op()
    {
        var p = Prefix();
        var store = NewOrgStore(p);
        await store.DeleteAsync("nope"); // must not throw
    }

    // ── IOrganizationMembershipStore ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Membership_upsert_writes_both_rows_and_they_agree()
    {
        var p = Prefix();
        var store = NewMembershipStore(p);
        var membership = Membership("org1", "user1", MembershipStatus.Invited, "auditor", "billing");
        membership.InvitedByUserId = "admin1";
        membership.InvitedAt = DateTimeOffset.UtcNow;

        await store.UpsertAsync(membership);

        var byOrgUser = await store.GetAsync("org1", "user1");
        Assert.NotNull(byOrgUser);
        Assert.Equal("org1", byOrgUser!.OrganizationId);
        Assert.Equal("user1", byOrgUser.UserId);
        Assert.Equal(MembershipStatus.Invited, byOrgUser.Status);
        Assert.Equal("admin1", byOrgUser.InvitedByUserId);
        Assert.Equal(["auditor", "billing"], byOrgUser.Roles);

        var byOrg = await store.ListByOrganizationAsync("org1");
        var byUser = await store.ListByUserAsync("user1");
        var orgRow = Assert.Single(byOrg);
        var userRow = Assert.Single(byUser);
        Assert.Equal(byOrgUser.Status, orgRow.Status);
        Assert.Equal(byOrgUser.Status, userRow.Status);
        Assert.Equal(["auditor", "billing"], orgRow.Roles);
        Assert.Equal(["auditor", "billing"], userRow.Roles);

        Assert.Null(await store.GetAsync("org1", "no-such-user"));
    }

    [Fact]
    public async Task Membership_delete_removes_both_rows()
    {
        var p = Prefix();
        var store = NewMembershipStore(p);
        await store.UpsertAsync(Membership("org1", "user1"));

        await store.DeleteAsync("org1", "user1");

        Assert.Null(await store.GetAsync("org1", "user1"));
        Assert.Empty(await store.ListByOrganizationAsync("org1"));
        Assert.Empty(await store.ListByUserAsync("user1"));
    }

    [Fact]
    public async Task DeleteAsync_on_an_unknown_membership_is_a_no_op()
    {
        var p = Prefix();
        var store = NewMembershipStore(p);
        await store.DeleteAsync("org1", "user1"); // must not throw
    }

    [Fact]
    public async Task ListByOrganizationAsync_and_ListByUserAsync_answer_both_directions()
    {
        var p = Prefix();
        var store = NewMembershipStore(p);
        // org1 has two members; user1 belongs to two organizations.
        await store.UpsertAsync(Membership("org1", "user1"));
        await store.UpsertAsync(Membership("org1", "user2"));
        await store.UpsertAsync(Membership("org2", "user1"));

        var org1Members = await store.ListByOrganizationAsync("org1");
        Assert.Equal(2, org1Members.Count);
        Assert.Contains(org1Members, m => m.UserId == "user1");
        Assert.Contains(org1Members, m => m.UserId == "user2");

        var user1Orgs = await store.ListByUserAsync("user1");
        Assert.Equal(2, user1Orgs.Count);
        Assert.Contains(user1Orgs, m => m.OrganizationId == "org1");
        Assert.Contains(user1Orgs, m => m.OrganizationId == "org2");

        Assert.Empty(await store.ListByOrganizationAsync("org-unknown"));
        Assert.Empty(await store.ListByUserAsync("user-unknown"));
    }

    // ── partition isolation ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Two_envs_sharing_one_table_prefix_do_not_see_each_others_rows()
    {
        var p = Prefix();
        var sandboxA = new EnvPartitioner("sandbox-a");
        var sandboxB = new EnvPartitioner("sandbox-b");
        var orgStoreA = NewOrgStore(p, sandboxA);
        var orgStoreB = NewOrgStore(p, sandboxB);
        var memberStoreA = NewMembershipStore(p, sandboxA);
        var memberStoreB = NewMembershipStore(p, sandboxB);

        await orgStoreA.UpsertAsync(Org("org1", "acme"));
        await memberStoreA.UpsertAsync(Membership("org1", "user1"));

        // Same natural ids, different env — invisible to the other env.
        Assert.Null(await orgStoreB.GetAsync("org1"));
        Assert.Null(await orgStoreB.GetBySlugAsync("acme"));
        Assert.Empty(await orgStoreB.ListAsync());
        Assert.Null(await memberStoreB.GetAsync("org1", "user1"));
        Assert.Empty(await memberStoreB.ListByOrganizationAsync("org1"));
        Assert.Empty(await memberStoreB.ListByUserAsync("user1"));

        // env B can mint the identical slug independently — no cross-env clash.
        await orgStoreB.UpsertAsync(Org("org1", "acme", "Different Co"));
        Assert.Equal("Different Co", (await orgStoreB.GetAsync("org1"))!.DisplayName);

        // env A's data is unaffected.
        Assert.Equal("Acme", (await orgStoreA.GetAsync("org1"))!.DisplayName);
    }

    [Fact]
    public async Task Two_table_prefixes_do_not_see_each_others_rows()
    {
        var p1 = Prefix();
        var p2 = Prefix();
        var store1 = NewOrgStore(p1);
        var store2 = NewOrgStore(p2);

        await store1.UpsertAsync(Org("org1", "acme"));

        Assert.Null(await store2.GetAsync("org1"));
        Assert.Empty(await store2.ListAsync());
    }

    // ── change-writer capture ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Organization_store_logs_upsert_and_delete_for_both_tables()
    {
        var p = Prefix();
        var log = T(p, "Tombstones");
        var store = NewOrgStore(p, changeWriter: new TableChangeWriter(log));

        await store.UpsertAsync(Org("org1", "acme"));

        Assert.Single(await ChangeRows(log, "Organizations", "U"));
        Assert.Single(await ChangeRows(log, "OrganizationSlugs", "U"));

        await store.DeleteAsync("org1");

        Assert.Single(await ChangeRows(log, "Organizations", "D"));
        Assert.Single(await ChangeRows(log, "OrganizationSlugs", "D"));
    }

    [Fact]
    public async Task Membership_delete_repairs_an_orphaned_reverse_row_and_tombstones_only_it()
    {
        // F18: an orphan — only the user-partitioned (reverse) row present, e.g. from a half-completed
        // upsert or an out-of-band repair — must still have its delete attempted. No early return just
        // because the forward row is already missing. R9: the forward row was never there, so it gets
        // neither a delete attempt nor a tombstone — only the row that actually existed does.
        var p = Prefix();
        var log = T(p, "Tombstones");
        var membersTable = T(p, "OrganizationMembers");
        var userMembershipsTable = T(p, "UserMemberships");
        var store = new TableOrganizationMembershipStore(
            membersTable, userMembershipsTable, EnvPartitioner.Live, new TableChangeWriter(log));

        var membership = Membership("org1", "user1");
        var orphanRow = OrganizationMembershipEntity.FromModelForUser(membership);
        orphanRow.PartitionKey = EnvPartitioner.Live.PK(orphanRow.PartitionKey);
        await userMembershipsTable.UpsertEntityAsync(orphanRow, TableUpdateMode.Replace);

        // The forward row genuinely does not exist.
        Assert.Null(await store.GetAsync("org1", "user1"));

        await store.DeleteAsync("org1", "user1");

        // The orphaned reverse row is gone.
        Assert.Empty(await store.ListByUserAsync("user1"));
        // Only the row that actually existed is tombstoned.
        Assert.Empty(await ChangeRows(log, "OrganizationMembers", "D"));
        Assert.Single(await ChangeRows(log, "UserMemberships", "D"));
    }

    [Fact]
    public async Task Membership_delete_of_a_nonexistent_membership_writes_no_tombstones()
    {
        // R9: neither row ever existed, so DeleteAsync must write no tombstone for either — a
        // tombstone means "this key was deleted", not "a delete was requested".
        var p = Prefix();
        var log = T(p, "Tombstones");
        var store = NewMembershipStore(p, changeWriter: new TableChangeWriter(log));

        await store.DeleteAsync("org1", "user1"); // must not throw

        Assert.Empty(await ChangeRows(log, "OrganizationMembers", "D"));
        Assert.Empty(await ChangeRows(log, "UserMemberships", "D"));
    }

    [Fact]
    public async Task Membership_store_logs_upsert_and_delete_for_both_tables()
    {
        var p = Prefix();
        var log = T(p, "Tombstones");
        var store = NewMembershipStore(p, changeWriter: new TableChangeWriter(log));

        await store.UpsertAsync(Membership("org1", "user1"));

        Assert.Single(await ChangeRows(log, "OrganizationMembers", "U"));
        Assert.Single(await ChangeRows(log, "UserMemberships", "U"));

        await store.DeleteAsync("org1", "user1");

        Assert.Single(await ChangeRows(log, "OrganizationMembers", "D"));
        Assert.Single(await ChangeRows(log, "UserMemberships", "D"));
    }
}
