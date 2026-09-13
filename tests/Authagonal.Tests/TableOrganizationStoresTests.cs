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
    public async Task Slug_change_removes_the_stale_slug_row()
    {
        var p = Prefix();
        var store = NewOrgStore(p);
        var org = Org("org1", "acme");
        await store.UpsertAsync(org);

        org.Slug = "acme-renamed";
        await store.UpsertAsync(org);

        Assert.Null(await store.GetBySlugAsync("acme"));
        var bySlug = await store.GetBySlugAsync("acme-renamed");
        Assert.NotNull(bySlug);
        Assert.Equal("org1", bySlug!.Id);

        // Exactly one row survives in the slug table for this organization — the stale one is gone,
        // not merely shadowed.
        var slugsTable = T(p, "OrganizationSlugs");
        var rows = new List<TableEntity>();
        await foreach (var e in slugsTable.QueryAsync<TableEntity>()) rows.Add(e);
        Assert.Single(rows);
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
    public async Task Organization_store_logs_the_stale_slug_delete_on_rename()
    {
        var p = Prefix();
        var log = T(p, "Tombstones");
        var store = NewOrgStore(p, changeWriter: new TableChangeWriter(log));
        var org = Org("org1", "acme");
        await store.UpsertAsync(org);

        org.Slug = "acme-renamed";
        await store.UpsertAsync(org);

        var deletes = await ChangeRows(log, "OrganizationSlugs", "D");
        var deletedRow = Assert.Single(deletes);
        Assert.Equal("acme", deletedRow.GetString("OrigRK"));
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
