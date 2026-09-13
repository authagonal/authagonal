using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.SqlProvider.Sql;
using Authagonal.SqlProvider.Stores;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// Behavioural coverage for <see cref="SqlOrganizationStore"/> and
/// <see cref="SqlOrganizationMembershipStore"/>, run identically against SQLite and PostgreSQL — the
/// same shared-base pattern as <see cref="SqlProviderTestsBase"/>, kept in its own file because these
/// two stores are a self-contained pair with their own slug-uniqueness and dual-row-consistency
/// concerns that don't belong mixed into the general SQL-provider suite.
/// </summary>
public abstract class SqlOrganizationStoreTestsBase : IAsyncLifetime
{
    private SqlDataSource _source = null!;

    protected abstract SqlDataSource CreateSource();

    /// <summary>
    /// A second, independent <see cref="SqlDataSource"/> pointed at the SAME underlying tables as the
    /// one <see cref="CreateSource"/> built — same SQLite shared-cache database, or same PostgreSQL
    /// schema. Simulates a second pod running the provider's startup DDL against tables a first pod
    /// already created.
    /// </summary>
    protected abstract SqlDataSource CreateSecondSourceAtSameLocation();

    public Task InitializeAsync()
    {
        _source = CreateSource();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _source.DisposeAsync();

    private static readonly EnvPartitioner Live = EnvPartitioner.Live;

    private async Task<SqlTable> T(string name)
    {
        await _source.EnsureTableAsync(name);
        return new SqlTable(_source, name);
    }

    private async Task<SqlOrganizationStore> NewOrgStoreAsync(IChangeWriter? tombstones = null)
        => new(await T("Organizations"), await T("OrganizationSlugs"), Live, tombstones);

    private async Task<SqlOrganizationMembershipStore> NewMembershipStoreAsync(IChangeWriter? tombstones = null)
        => new(await T("OrganizationMembers"), await T("UserMemberships"), Live, tombstones);

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

    // ── IOrganizationStore ───────────────────────────────────────────────────────

    [Fact]
    public async Task OrganizationStore_RoundTripsCreateGetAndList()
    {
        var store = await NewOrgStoreAsync();
        var org = Org("o1", "acme");
        await store.UpsertAsync(org);

        var fetched = await store.GetAsync("o1");
        Assert.NotNull(fetched);
        Assert.Equal("acme", fetched!.Slug);
        Assert.Equal("Acme", fetched.DisplayName);

        var listed = await store.ListAsync();
        Assert.Single(listed);
        Assert.Equal("o1", listed[0].Id);

        Assert.Null(await store.GetAsync("no-such-org"));
    }

    [Fact]
    public async Task OrganizationStore_GetBySlug_IsAPointLookup()
    {
        var store = await NewOrgStoreAsync();
        await store.UpsertAsync(Org("o1", "acme"));
        await store.UpsertAsync(Org("o2", "globex"));

        Assert.Equal("o1", (await store.GetBySlugAsync("acme"))?.Id);
        Assert.Equal("o2", (await store.GetBySlugAsync("globex"))?.Id);
        Assert.Null(await store.GetBySlugAsync("no-such-slug"));

        // Slugs are not case-folded — Organization carries no such normalization, unlike SsoDomain.
        Assert.Null(await store.GetBySlugAsync("ACME"));
    }

    [Fact]
    public async Task OrganizationStore_SlugChange_WritesTheNewRowAndRemovesTheStaleOne()
    {
        var store = await NewOrgStoreAsync();
        await store.UpsertAsync(Org("o1", "acme-old"));
        Assert.Equal("o1", (await store.GetBySlugAsync("acme-old"))?.Id);

        await store.UpsertAsync(Org("o1", "acme-new"));

        Assert.Null(await store.GetBySlugAsync("acme-old"));
        Assert.Equal("o1", (await store.GetBySlugAsync("acme-new"))?.Id);
        Assert.Equal("acme-new", (await store.GetAsync("o1"))!.Slug);
        Assert.Single(await store.ListAsync());
    }

    [Fact]
    public async Task OrganizationStore_SlugHeldByAnotherOrganization_ThrowsAndWritesNothing()
    {
        var store = await NewOrgStoreAsync();
        await store.UpsertAsync(Org("o1", "acme"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.UpsertAsync(Org("o2", "acme")));
        Assert.Contains("acme", ex.Message, StringComparison.Ordinal);
        Assert.Contains("o1", ex.Message, StringComparison.Ordinal);

        // The rejected upsert must not have written anything for o2.
        Assert.Null(await store.GetAsync("o2"));
        Assert.Equal("o1", (await store.GetBySlugAsync("acme"))?.Id);
    }

    [Fact]
    public async Task OrganizationStore_ReUpsertingTheSameOrganizationUnderItsOwnSlug_IsNotAClash()
    {
        var store = await NewOrgStoreAsync();
        var org = Org("o1", "acme");
        await store.UpsertAsync(org);

        org.DisplayName = "Acme Renamed";
        await store.UpsertAsync(org); // same id, same slug — must not throw

        Assert.Equal("Acme Renamed", (await store.GetAsync("o1"))!.DisplayName);
        Assert.Equal("o1", (await store.GetBySlugAsync("acme"))?.Id);
    }

    [Fact]
    public async Task OrganizationStore_Delete_RemovesTheOrgAndSlugRow_ButDoesNotCascadeMemberships()
    {
        var orgStore = await NewOrgStoreAsync();
        var membershipStore = await NewMembershipStoreAsync();

        await orgStore.UpsertAsync(Org("o1", "acme"));
        await membershipStore.UpsertAsync(Membership("o1", "u1"));

        await orgStore.DeleteAsync("o1");

        Assert.Null(await orgStore.GetAsync("o1"));
        Assert.Null(await orgStore.GetBySlugAsync("acme"));
        Assert.Empty(await orgStore.ListAsync());

        // Not cascaded: the caller owns that ordering (IOrganizationStore.DeleteAsync remarks).
        Assert.NotNull(await membershipStore.GetAsync("o1", "u1"));
        Assert.Single(await membershipStore.ListByOrganizationAsync("o1"));
    }

    [Fact]
    public async Task OrganizationStore_Delete_OfAnUnknownId_IsANoOp()
    {
        var store = await NewOrgStoreAsync();
        await store.DeleteAsync("no-such-org"); // must not throw
        Assert.Empty(await store.ListAsync());
    }

    // ── IOrganizationMembershipStore ─────────────────────────────────────────────

    [Fact]
    public async Task MembershipStore_RoundTrips_AndAPointGetMatchesBothListings()
    {
        var store = await NewMembershipStoreAsync();
        await store.UpsertAsync(Membership("o1", "u1", MembershipStatus.Active, "admin"));

        var direct = await store.GetAsync("o1", "u1");
        Assert.NotNull(direct);
        Assert.Equal(["admin"], direct!.Roles);

        Assert.Equal("u1", Assert.Single(await store.ListByOrganizationAsync("o1")).UserId);
        Assert.Equal("o1", Assert.Single(await store.ListByUserAsync("u1")).OrganizationId);

        Assert.Null(await store.GetAsync("o1", "no-such-user"));
        Assert.Null(await store.GetAsync("no-such-org", "u1"));
    }

    [Fact]
    public async Task MembershipStore_Upsert_KeepsBothIndexRowsInSyncOnUpdate()
    {
        var store = await NewMembershipStoreAsync();
        await store.UpsertAsync(Membership("o1", "u1", MembershipStatus.Invited, "member"));

        Assert.Equal(MembershipStatus.Invited, (await store.GetAsync("o1", "u1"))!.Status);

        // Re-upsert with different status/roles — both physical rows must reflect the change, not
        // just the one the next read happens to touch.
        await store.UpsertAsync(Membership("o1", "u1", MembershipStatus.Active, "member", "admin"));

        var byOrg = Assert.Single(await store.ListByOrganizationAsync("o1"));
        var byUser = Assert.Single(await store.ListByUserAsync("u1"));

        Assert.Equal(MembershipStatus.Active, byOrg.Status);
        Assert.Equal(MembershipStatus.Active, byUser.Status);
        Assert.Equal(["member", "admin"], byOrg.Roles);
        Assert.Equal(["member", "admin"], byUser.Roles);
    }

    [Fact]
    public async Task MembershipStore_Delete_RemovesBothIndexRows()
    {
        var store = await NewMembershipStoreAsync();
        await store.UpsertAsync(Membership("o1", "u1"));

        await store.DeleteAsync("o1", "u1");

        Assert.Null(await store.GetAsync("o1", "u1"));
        Assert.Empty(await store.ListByOrganizationAsync("o1"));
        Assert.Empty(await store.ListByUserAsync("u1"));
    }

    [Fact]
    public async Task MembershipStore_Delete_OfAnUnknownPair_IsANoOp()
    {
        var store = await NewMembershipStoreAsync();
        await store.DeleteAsync("no-such-org", "no-such-user"); // must not throw
    }

    [Fact]
    public async Task MembershipStore_ListByUserAndListByOrganization_ReturnIndependentSets()
    {
        var store = await NewMembershipStoreAsync();
        // u1 belongs to o1 and o2; u2 belongs only to o1.
        await store.UpsertAsync(Membership("o1", "u1"));
        await store.UpsertAsync(Membership("o2", "u1"));
        await store.UpsertAsync(Membership("o1", "u2"));

        var u1Orgs = (await store.ListByUserAsync("u1")).Select(m => m.OrganizationId).OrderBy(x => x).ToArray();
        Assert.Equal(["o1", "o2"], u1Orgs);

        var o1Users = (await store.ListByOrganizationAsync("o1")).Select(m => m.UserId).OrderBy(x => x).ToArray();
        Assert.Equal(["u1", "u2"], o1Users);

        Assert.Equal(["u1"], (await store.ListByOrganizationAsync("o2")).Select(m => m.UserId));
        Assert.Empty(await store.ListByUserAsync("no-such-user"));
        Assert.Empty(await store.ListByOrganizationAsync("no-such-org"));
    }

    // ── collation ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every lookup in these two stores is exact-key equality (point read or whole-partition scan) —
    /// unlike the range/prefix predicates elsewhere in the provider, nothing here depends on byte
    /// ordering. This pins that down against the ICU-collated PostgreSQL fixture: ids, slugs and user
    /// ids that sort differently under ICU than under a byte-ordinal ("C") collation must still resolve
    /// to exactly the right row, with no accidental case-folding.
    /// </summary>
    [Fact]
    public async Task OrganizationAndMembershipEquality_IsUnaffectedByLinguisticCollation()
    {
        var orgStore = await NewOrgStoreAsync();
        var membershipStore = await NewMembershipStoreAsync();

        await orgStore.UpsertAsync(Org("Org-Zeta", "Zeta-Slug"));
        await orgStore.UpsertAsync(Org("org-zeta-2", "zeta-slug"));

        Assert.Equal("Org-Zeta", (await orgStore.GetAsync("Org-Zeta"))?.Id);
        Assert.Equal("org-zeta-2", (await orgStore.GetAsync("org-zeta-2"))?.Id);
        Assert.Equal("Org-Zeta", (await orgStore.GetBySlugAsync("Zeta-Slug"))?.Id);
        Assert.Equal("org-zeta-2", (await orgStore.GetBySlugAsync("zeta-slug"))?.Id);

        await membershipStore.UpsertAsync(Membership("Org-Zeta", "User-A"));
        await membershipStore.UpsertAsync(Membership("Org-Zeta", "user-a"));

        Assert.NotNull(await membershipStore.GetAsync("Org-Zeta", "User-A"));
        Assert.NotNull(await membershipStore.GetAsync("Org-Zeta", "user-a"));
        Assert.Equal(2, (await membershipStore.ListByOrganizationAsync("Org-Zeta")).Count);
    }

    // ── DDL idempotency ──────────────────────────────────────────────────────────

    /// <summary>
    /// F126-style coverage for the four new tables: a second "pod" running the provider's startup DDL
    /// against tables a first pod already created must be a no-op, not an error, and the tables must
    /// stay fully usable afterwards.
    /// </summary>
    [Fact]
    public async Task Ddl_IsIdempotentAcrossTwoStartups()
    {
        string[] tables = ["Organizations", "OrganizationSlugs", "OrganizationMembers", "UserMemberships"];

        await _source.EnsureTablesAsync(tables); // first startup
        await using var second = CreateSecondSourceAtSameLocation();
        await second.EnsureTablesAsync(tables); // second startup, same underlying tables — must not throw

        var store = new SqlOrganizationStore(
            new SqlTable(second, "Organizations"), new SqlTable(second, "OrganizationSlugs"), Live);
        await store.UpsertAsync(Org("o1", "acme"));
        Assert.Equal("o1", (await store.GetAsync("o1"))?.Id);
        Assert.Equal("o1", (await store.GetBySlugAsync("acme"))?.Id);
    }
}

/// <summary>The suite against SQLite — the zero-dependency single-node backend.</summary>
public sealed class SqliteOrganizationStoreTests : SqlOrganizationStoreTestsBase
{
    // A fixed connection string (not SqlTestSource.Sqlite()'s random one) so the "second startup" test
    // can point a second SqlDataSource at the very same shared-cache database.
    private readonly string _connectionString =
        $"Data Source=authagonal-test-org-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

    protected override SqlDataSource CreateSource() => new(new SqliteDialect(_connectionString));
    protected override SqlDataSource CreateSecondSourceAtSameLocation() => new(new SqliteDialect(_connectionString));
}

/// <summary>
/// The same suite against a real PostgreSQL server, on a database with a linguistic (ICU) collation —
/// see <see cref="PostgresFixture"/> for why that collation is the interesting one.
/// </summary>
[Collection("Postgres")]
public sealed class PostgresOrganizationStoreTests(PostgresFixture postgres) : SqlOrganizationStoreTestsBase
{
    // A fixed schema (not SqlTestSource.Postgres's random one) so the "second startup" test can point a
    // second SqlDataSource at the very same schema.
    private readonly string _schema = $"orgtest_{Guid.NewGuid():N}";

    protected override SqlDataSource CreateSource() => SqlTestSource.Postgres(postgres.ConnectionString, _schema);
    protected override SqlDataSource CreateSecondSourceAtSameLocation() => SqlTestSource.Postgres(postgres.ConnectionString, _schema);
}
