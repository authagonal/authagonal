using Authagonal.AzureProvider.Entities;
using Authagonal.AzureProvider.Stores;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Tests.Infrastructure;
using Azure.Data.Tables;

namespace Authagonal.Tests;

/// <summary>
/// The DEFAULT interface implementations of <see cref="IOrganizationStore.ListPageAsync"/> and
/// <see cref="IOrganizationMembershipStore.ListByOrganizationPageAsync"/>, exercised through the
/// writable in-memory test stores, which do not override them.
/// </summary>
public class OrganizationDefaultPagingTests
{
    private static async Task<IOrganizationStore> OrgsAsync(int count)
    {
        var store = new WritableOrganizationStore();
        // Inserted out of order so the test proves the page is SORTED, not insertion-ordered.
        foreach (var i in Enumerable.Range(0, count).Reverse())
            await store.UpsertAsync(new Organization { Id = $"org_{i:D2}", Slug = $"s{i:D2}", DisplayName = $"O{i}" });
        return store;
    }

    private static async Task<List<string>> DrainAsync(IOrganizationStore store, int limit)
    {
        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await store.ListPageAsync(cursor, limit);
            Assert.True(page.Items.Count <= limit);
            seen.AddRange(page.Items.Select(o => o.Id));
            cursor = page.NextCursor;
            Assert.True(++pages < 100, "paging did not terminate");
        } while (cursor is not null);
        return seen;
    }

    [Fact]
    public async Task Pages_cover_every_row_once_in_ordinal_order()
    {
        var store = await OrgsAsync(7);
        var seen = await DrainAsync(store, 3);
        Assert.Equal(Enumerable.Range(0, 7).Select(i => $"org_{i:D2}"), seen);
    }

    [Fact]
    public async Task Exact_boundary_has_no_next_cursor()
    {
        var store = await OrgsAsync(6);

        var only = await store.ListPageAsync(null, 6);
        Assert.Equal(6, only.Items.Count);
        Assert.Null(only.NextCursor);

        var first = await store.ListPageAsync(null, 3);
        Assert.NotNull(first.NextCursor);
        var second = await store.ListPageAsync(first.NextCursor, 3);
        Assert.Equal(["org_03", "org_04", "org_05"], second.Items.Select(o => o.Id));
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task Empty_store_is_one_empty_page()
    {
        IOrganizationStore empty = new WritableOrganizationStore();
        var page = await empty.ListPageAsync(null, 50);
        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task Cursor_is_keyset_so_a_deleted_row_does_not_shift_the_next_page()
    {
        var store = await OrgsAsync(6);
        var first = await store.ListPageAsync(null, 3);
        // Delete a row already returned: an offset cursor would now skip org_03.
        await store.DeleteAsync("org_01");
        var second = await store.ListPageAsync(first.NextCursor, 3);
        Assert.Equal(["org_03", "org_04", "org_05"], second.Items.Select(o => o.Id));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1000, 200)]
    public async Task Limit_is_clamped(int requested, int expected)
    {
        var store = await OrgsAsync(250);
        var page = await store.ListPageAsync(null, requested);
        Assert.Equal(expected, page.Items.Count);
        Assert.NotNull(page.NextCursor);
    }

    [Theory]
    [InlineData("not base64 !!")]
    [InlineData("Zm9v")] // valid base64url ("foo") but not a cursor this library issued
    [InlineData("azE6")] // "k1:" with an empty key
    public async Task Malformed_cursor_is_an_ArgumentException(string cursor)
    {
        var store = await OrgsAsync(2);
        await Assert.ThrowsAsync<ArgumentException>(() => store.ListPageAsync(cursor, 10));

        IOrganizationMembershipStore members = new WritableOrganizationMembershipStore().With("org_00", "u1");
        await Assert.ThrowsAsync<ArgumentException>(() => members.ListByOrganizationPageAsync("org_00", cursor, 10));
    }

    [Fact]
    public async Task Membership_pages_are_scoped_to_one_organization_and_ordered_by_user()
    {
        var store = new WritableOrganizationMembershipStore();
        foreach (var u in new[] { "u5", "u1", "u3", "u2", "u4" })
            store.With("org-a", u);
        store.With("org-b", "u0");
        IOrganizationMembershipStore s = store;

        var first = await s.ListByOrganizationPageAsync("org-a", null, 2);
        Assert.Equal(["u1", "u2"], first.Items.Select(m => m.UserId));
        var second = await s.ListByOrganizationPageAsync("org-a", first.NextCursor, 2);
        Assert.Equal(["u3", "u4"], second.Items.Select(m => m.UserId));
        var third = await s.ListByOrganizationPageAsync("org-a", second.NextCursor, 2);
        Assert.Equal(["u5"], third.Items.Select(m => m.UserId));
        Assert.Null(third.NextCursor);
    }

    [Fact]
    public void Cursor_round_trips_keys_with_separators_and_unicode()
    {
        foreach (var key in new[] { "a|b", "org/x", "ünïcødé", "k1:nested" })
            Assert.Equal(key, KeysetCursor.Decode(KeysetCursor.Encode(key)));
        Assert.Null(KeysetCursor.Decode(null));
        Assert.Null(KeysetCursor.Decode(""));
    }
}

/// <summary>
/// The Azure Table OVERRIDES — server-side <c>RowKey gt</c> queries — against Azurite, plus the
/// <see cref="Organization.Domains"/> column.
/// </summary>
[Collection("Azurite")]
public class TableOrganizationPagingTests(AzuriteFixture azurite)
{
    private readonly TableServiceClient _svc = new(azurite.ConnectionString);

    private TableClient T(string prefix, string name)
    {
        var c = _svc.GetTableClient($"{prefix}{name}");
        c.CreateIfNotExists();
        return c;
    }

    private static string Prefix() => $"opg{Guid.NewGuid():N}"[..20];

    private TableOrganizationStore Orgs(string prefix, EnvPartitioner p) =>
        new(T(prefix, "Organizations"), T(prefix, "OrganizationSlugs"), p);

    private TableOrganizationMembershipStore Members(string prefix, EnvPartitioner p) =>
        new(T(prefix, "OrganizationMembers"), T(prefix, "UserMemberships"), p);

    [Fact]
    public async Task Organization_pages_walk_the_partition_and_stop_at_the_boundary()
    {
        var prefix = Prefix();
        var live = Orgs(prefix, EnvPartitioner.Live);
        foreach (var i in Enumerable.Range(0, 7).Reverse())
            await live.UpsertAsync(new Organization { Id = $"org_{i:D2}", Slug = $"s{i:D2}", DisplayName = $"O{i}" });

        var seen = new List<string>();
        string? cursor = null;
        var pageSizes = new List<int>();
        do
        {
            var page = await live.ListPageAsync(cursor, 3);
            pageSizes.Add(page.Items.Count);
            seen.AddRange(page.Items.Select(o => o.Id));
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.Equal([3, 3, 1], pageSizes);
        Assert.Equal(Enumerable.Range(0, 7).Select(i => $"org_{i:D2}"), seen);

        var exact = await live.ListPageAsync(null, 7);
        Assert.Equal(7, exact.Items.Count);
        Assert.Null(exact.NextCursor);

        await Assert.ThrowsAsync<ArgumentException>(() => live.ListPageAsync("Zm9v", 3));
    }

    [Fact]
    public async Task Organization_pages_respect_the_env_partition()
    {
        var prefix = Prefix();
        var live = Orgs(prefix, EnvPartitioner.Live);
        var sandbox = Orgs(prefix, new EnvPartitioner("sandbox"));
        await live.UpsertAsync(new Organization { Id = "org_live", Slug = "live", DisplayName = "L" });
        await sandbox.UpsertAsync(new Organization { Id = "org_sbx", Slug = "sbx", DisplayName = "S" });

        Assert.Equal(["org_live"], (await live.ListPageAsync(null, 50)).Items.Select(o => o.Id));
        Assert.Equal(["org_sbx"], (await sandbox.ListPageAsync(null, 50)).Items.Select(o => o.Id));
    }

    [Fact]
    public async Task Membership_pages_are_scoped_to_one_organization()
    {
        var prefix = Prefix();
        var store = Members(prefix, EnvPartitioner.Live);
        foreach (var u in new[] { "u5", "u1", "u3", "u2", "u4" })
            await store.UpsertAsync(new OrganizationMembership { OrganizationId = "org-a", UserId = u });
        await store.UpsertAsync(new OrganizationMembership { OrganizationId = "org-b", UserId = "u0" });
        // A neighbouring partition key that sorts right after "org|org-a" must not leak in.
        await store.UpsertAsync(new OrganizationMembership { OrganizationId = "org-a2", UserId = "u9" });

        var first = await store.ListByOrganizationPageAsync("org-a", null, 2);
        Assert.Equal(["u1", "u2"], first.Items.Select(m => m.UserId));
        var second = await store.ListByOrganizationPageAsync("org-a", first.NextCursor, 2);
        Assert.Equal(["u3", "u4"], second.Items.Select(m => m.UserId));
        var third = await store.ListByOrganizationPageAsync("org-a", second.NextCursor, 2);
        Assert.Equal(["u5"], third.Items.Select(m => m.UserId));
        Assert.Null(third.NextCursor);

        var exact = await store.ListByOrganizationPageAsync("org-a", null, 5);
        Assert.Equal(5, exact.Items.Count);
        Assert.Null(exact.NextCursor);
    }

    [Fact]
    public async Task Domains_round_trip_and_a_row_without_the_column_reads_empty()
    {
        var prefix = Prefix();
        var store = Orgs(prefix, EnvPartitioner.Live);
        var created = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var verified = created.AddHours(2);

        await store.UpsertAsync(new Organization
        {
            Id = "org_dom",
            Slug = "dom",
            DisplayName = "Dom",
            Domains =
            [
                new OrganizationDomain { Domain = " Acme.COM. ", VerificationToken = "tok-1", CreatedAt = created, VerifiedAt = verified },
                new OrganizationDomain { Domain = "pending.example", VerificationToken = "tok-2", CreatedAt = created },
            ],
        });
        var read = (await store.GetAsync("org_dom"))!;
        Assert.Equal(2, read.Domains.Count);
        Assert.Equal("acme.com", read.Domains[0].Domain);
        Assert.Equal("tok-1", read.Domains[0].VerificationToken);
        Assert.Equal(created, read.Domains[0].CreatedAt);
        Assert.Equal(verified, read.Domains[0].VerifiedAt);
        Assert.Null(read.Domains[1].VerifiedAt);

        // No domains: the column is not written at all.
        await store.UpsertAsync(new Organization { Id = "org_none", Slug = "none", DisplayName = "None" });
        var raw = (await T(prefix, "Organizations").GetEntityAsync<TableEntity>(
            EnvPartitioner.Live.PK(OrganizationEntity.OrganizationsPartition), "org_none")).Value;
        Assert.False(raw.ContainsKey(nameof(OrganizationEntity.DomainsJson)));

        // A row written before the property existed — only the columns that existed then.
        await T(prefix, "Organizations").UpsertEntityAsync(new TableEntity(
            EnvPartitioner.Live.PK(OrganizationEntity.OrganizationsPartition), "org_legacy")
        {
            ["Slug"] = "legacy",
            ["DisplayName"] = "Legacy",
            ["Enabled"] = true,
            ["RequireMembershipForTokens"] = true,
        });
        var legacy = (await store.GetAsync("org_legacy"))!;
        Assert.NotNull(legacy.Domains);
        Assert.Empty(legacy.Domains);
        Assert.Contains(legacy.Id, (await store.ListPageAsync(null, 10)).Items.Select(o => o.Id));
    }
}
