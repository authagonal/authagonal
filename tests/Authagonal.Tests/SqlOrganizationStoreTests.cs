using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.SqlProvider;
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

    /// <summary>Same as <see cref="NewOrgStoreAsync"/>, but also hands back the two underlying tables
    /// so a test can seed a row directly, bypassing the store's own upsert.</summary>
    private async Task<(SqlOrganizationStore Store, SqlTable Organizations, SqlTable Slugs)>
        NewOrgStoreWithTablesAsync(IChangeWriter? tombstones = null)
    {
        var organizations = await T("Organizations");
        var slugs = await T("OrganizationSlugs");
        return (new SqlOrganizationStore(organizations, slugs, Live, tombstones), organizations, slugs);
    }

    private async Task<SqlOrganizationMembershipStore> NewMembershipStoreAsync(IChangeWriter? tombstones = null)
        => new(await T("OrganizationMembers"), await T("UserMemberships"), Live, tombstones);

    /// <summary>Same as <see cref="NewMembershipStoreAsync"/>, but also hands back the two underlying
    /// tables so a test can seed a row directly, bypassing the store's own dual-write.</summary>
    private async Task<(SqlOrganizationMembershipStore Store, SqlTable Members, SqlTable UserMemberships)>
        NewMembershipStoreWithTablesAsync(IChangeWriter? tombstones = null)
    {
        var members = await T("OrganizationMembers");
        var userMemberships = await T("UserMemberships");
        return (new SqlOrganizationMembershipStore(members, userMemberships, Live, tombstones), members, userMemberships);
    }

    /// <summary>Records every delete this test run sent through <see cref="IChangeWriter"/>, so a test
    /// can assert exactly which rows were (and were not) tombstoned.</summary>
    private sealed class RecordingChangeWriter : IChangeWriter
    {
        public List<(string TableName, string PartitionKey, string RowKey)> Deletes { get; } = [];

        public Task WriteAsync(string tableName, string partitionKey, string rowKey, CancellationToken ct = default)
        {
            Deletes.Add((tableName, partitionKey, rowKey));
            return Task.CompletedTask;
        }

        public Task WriteBatchAsync(string tableName, IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default)
        {
            foreach (var (pk, rk) in keys) Deletes.Add((tableName, pk, rk));
            return Task.CompletedTask;
        }

        public Task WriteUpsertAsync(string tableName, string partitionKey, string rowKey, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task WriteUpsertBatchAsync(string tableName, IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default)
            => Task.CompletedTask;
    }

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
    public async Task OrganizationStore_RoundTripsDomains()
    {
        var store = await NewOrgStoreAsync();
        var created = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var verified = created.AddHours(2);
        var org = Org("o-dom", "domains");
        org.Domains =
        [
            new OrganizationDomain { Domain = " Acme.COM. ", VerificationToken = "tok-1", CreatedAt = created, VerifiedAt = verified },
            new OrganizationDomain { Domain = "pending.example", VerificationToken = "tok-2", CreatedAt = created },
        ];
        await store.UpsertAsync(org);

        var read = (await store.GetAsync("o-dom"))!;
        Assert.Equal(2, read.Domains.Count);
        Assert.Equal("acme.com", read.Domains[0].Domain);
        Assert.Equal("tok-1", read.Domains[0].VerificationToken);
        Assert.Equal(created, read.Domains[0].CreatedAt);
        Assert.Equal(verified, read.Domains[0].VerifiedAt);
        Assert.Equal("pending.example", read.Domains[1].Domain);
        Assert.Null(read.Domains[1].VerifiedAt);
    }

    [Fact]
    public async Task OrganizationStore_RowWithoutDomains_ReadsBackEmpty()
    {
        var (store, organizations, _) = await NewOrgStoreWithTablesAsync();
        // A document written before Organization.Domains existed: no "domains" key at all.
        await organizations.PutAsync(new SqlRow(Live.PK("org"), "o-legacy")
        {
            Data = """{"id":"o-legacy","slug":"legacy","displayName":"Legacy","enabled":true}""",
        });

        var read = await store.GetAsync("o-legacy");
        Assert.NotNull(read);
        Assert.NotNull(read!.Domains);
        Assert.Empty(read.Domains);
    }

    [Fact]
    public async Task OrganizationStore_PagesThroughTheDefaultImplementation()
    {
        var store = await NewOrgStoreAsync();
        foreach (var i in Enumerable.Range(0, 5))
            await store.UpsertAsync(Org($"o-page-{i}", $"page-{i}"));

        Authagonal.Core.Stores.IOrganizationStore s = store;
        var first = await s.ListPageAsync(null, 3);
        Assert.Equal(["o-page-0", "o-page-1", "o-page-2"], first.Items.Select(o => o.Id));
        Assert.NotNull(first.NextCursor);
        var second = await s.ListPageAsync(first.NextCursor, 3);
        Assert.Equal(["o-page-3", "o-page-4"], second.Items.Select(o => o.Id));
        Assert.Null(second.NextCursor);
    }

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

    /// <summary>F11 — the slug is immutable once an organization exists.</summary>
    [Fact]
    public async Task OrganizationStore_SlugChange_ThrowsAndChangesNothing()
    {
        var store = await NewOrgStoreAsync();
        await store.UpsertAsync(Org("o1", "acme-old"));
        Assert.Equal("o1", (await store.GetBySlugAsync("acme-old"))?.Id);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.UpsertAsync(Org("o1", "acme-new")));
        Assert.Contains("immutable", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Nothing changed: the old slug still resolves, the new one resolves to nothing, and the
        // stored document's slug is untouched.
        Assert.Equal("acme-old", (await store.GetAsync("o1"))!.Slug);
        Assert.Equal("o1", (await store.GetBySlugAsync("acme-old"))?.Id);
        Assert.Null(await store.GetBySlugAsync("acme-new"));
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

        // The rejected upsert must not have written anything for o2.
        Assert.Null(await store.GetAsync("o2"));
        Assert.Equal("o1", (await store.GetBySlugAsync("acme"))?.Id);
    }

    /// <summary>F4 — the slug is claimed with an insert-only write, so of many concurrent creators of
    /// the same brand-new slug, exactly one wins, on both dialects.</summary>
    [Fact]
    public async Task OrganizationStore_ConcurrentUpsertsOfTheSameNewSlug_ExactlyOneSucceeds()
    {
        var store = await NewOrgStoreAsync();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            try
            {
                await store.UpsertAsync(Org($"o{i}", "contested"));
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }));

        Assert.Equal(1, outcomes.Count(won => won));
        Assert.Single(await store.ListAsync());

        var winner = (await store.ListAsync())[0];
        Assert.Equal("contested", winner.Slug);
        Assert.Equal(winner.Id, (await store.GetBySlugAsync("contested"))?.Id);
    }

    /// <summary>R2 — the crash window slug-first ordering opens. If the process died right after
    /// claiming the slug row but before writing the organization document, a retry of the exact same
    /// create (same id, same slug) must self-heal and finish the write rather than read its own
    /// earlier claim back as "held by another organization" — seeded here by writing only the slug
    /// row, bypassing <see cref="SqlOrganizationStore.UpsertAsync"/> entirely.</summary>
    [Fact]
    public async Task OrganizationStore_CreateRetryAfterCrashBetweenSlugAndOrgWrite_Succeeds()
    {
        var (store, _, slugs) = await NewOrgStoreWithTablesAsync();

        var orphanSlug = new SqlRow(Live.PK("orgslug"), "acme");
        orphanSlug.PutS("organizationId", "o1");
        await slugs.PutAsync(orphanSlug);

        // The org row never landed, so nothing resolves yet — this is the exact inconsistency the
        // crash window leaves behind.
        Assert.Null(await store.GetAsync("o1"));
        Assert.Null(await store.GetBySlugAsync("acme"));

        // The retry — same id, same slug — must succeed rather than throw.
        await store.UpsertAsync(Org("o1", "acme"));

        Assert.Equal("o1", (await store.GetAsync("o1"))?.Id);
        Assert.Equal("o1", (await store.GetBySlugAsync("acme"))?.Id);
        Assert.Single(await store.ListAsync());
    }

    /// <summary>F3 — ids and slugs share no namespace: a new organization's slug may not equal an
    /// existing organization's id.</summary>
    [Fact]
    public async Task OrganizationStore_SlugEqualToAnExistingOrganizationsId_ThrowsAndWritesNothing()
    {
        var store = await NewOrgStoreAsync();
        await store.UpsertAsync(Org("acme-id", "acme-slug"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.UpsertAsync(Org("o2", "acme-id")));
        Assert.Contains("acme-id", ex.Message, StringComparison.Ordinal);

        Assert.Null(await store.GetAsync("o2"));
        Assert.Null(await store.GetBySlugAsync("acme-id"));
    }

    /// <summary>F3, the other direction: a new organization's id may not equal an existing
    /// organization's slug.</summary>
    [Fact]
    public async Task OrganizationStore_IdEqualToAnExistingOrganizationsSlug_ThrowsAndWritesNothing()
    {
        var store = await NewOrgStoreAsync();
        await store.UpsertAsync(Org("o1", "acme-slug"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.UpsertAsync(Org("acme-slug", "different-slug")));
        Assert.Contains("acme-slug", ex.Message, StringComparison.Ordinal);

        Assert.Null(await store.GetAsync("acme-slug"));
        Assert.Null(await store.GetBySlugAsync("different-slug"));
        // The pre-existing organization is untouched.
        Assert.Equal("o1", (await store.GetBySlugAsync("acme-slug"))?.Id);
    }

    /// <summary>F4 — slug format is validated at the boundary, not left for some later reader to choke
    /// on.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("Acme")]
    [InlineData("-acme")]
    [InlineData("acme-")]
    [InlineData("ac me")]
    [InlineData("ac_me")]
    public async Task OrganizationStore_InvalidSlugPattern_ThrowsArgumentExceptionAndWritesNothing(string slug)
    {
        var store = await NewOrgStoreAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertAsync(Org("o1", slug)));
        Assert.Null(await store.GetAsync("o1"));
        Assert.Empty(await store.ListAsync());
    }

    [Theory]
    [InlineData("a")]
    [InlineData("a1")]
    [InlineData("acme-corp-2")]
    public async Task OrganizationStore_ValidSlugPattern_IsAccepted(string slug)
    {
        var store = await NewOrgStoreAsync();
        await store.UpsertAsync(Org("o1", slug));
        Assert.Equal(slug, (await store.GetAsync("o1"))?.Slug);
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

    /// <summary>R9 — a tombstone records a row that actually changed. Deleting a membership that never
    /// existed must remove nothing and therefore record nothing on either side.</summary>
    [Fact]
    public async Task MembershipStore_Delete_OfANonExistentMembership_WritesNoTombstones()
    {
        var tombstones = new RecordingChangeWriter();
        var store = await NewMembershipStoreAsync(tombstones);

        await store.DeleteAsync("no-such-org", "no-such-user");

        Assert.Empty(tombstones.Deletes);
    }

    /// <summary>F18 — delete must not let the forward row's absence short-circuit the reverse row's
    /// removal (and tombstone). Seeds ONLY the reverse (<c>UserMemberships</c>) row directly, bypassing
    /// <see cref="SqlOrganizationMembershipStore.UpsertAsync"/>, to simulate a membership an earlier
    /// partial write left half-orphaned.</summary>
    [Fact]
    public async Task MembershipStore_Delete_WithOnlyTheReverseRowPresent_RemovesAndTombstonesIt()
    {
        var tombstones = new RecordingChangeWriter();
        var (store, _, userMemberships) = await NewMembershipStoreWithTablesAsync(tombstones);

        var orphan = new SqlRow(Live.PK("user|u1"), "o1")
        {
            Data = JsonSerializer.Serialize(Membership("o1", "u1"), SqlJsonContext.Default.OrganizationMembership),
        };
        await userMemberships.PutAsync(orphan);

        // The forward row was never written — GetAsync (backed by OrganizationMembers) confirms the
        // orphan is exactly the inconsistency this test means to set up.
        Assert.Null(await store.GetAsync("o1", "u1"));
        Assert.Single(await store.ListByUserAsync("u1"));

        await store.DeleteAsync("o1", "u1");

        Assert.Empty(await store.ListByUserAsync("u1"));
        Assert.Contains(("UserMemberships", Live.PK("user|u1"), "o1"), tombstones.Deletes);
        // No forward row ever existed, so no forward tombstone should have been written.
        Assert.DoesNotContain(("OrganizationMembers", Live.PK("org|o1"), "u1"), tombstones.Deletes);
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

        // Slugs must be lowercase (the F4 pattern), so the case-sensitivity half of this test lives on
        // the ids instead — which carry no such restriction.
        await orgStore.UpsertAsync(Org("Org-Zeta", "zeta-slug"));
        await orgStore.UpsertAsync(Org("org-zeta-2", "zeta-slug-2"));

        Assert.Equal("Org-Zeta", (await orgStore.GetAsync("Org-Zeta"))?.Id);
        Assert.Equal("org-zeta-2", (await orgStore.GetAsync("org-zeta-2"))?.Id);
        Assert.Equal("Org-Zeta", (await orgStore.GetBySlugAsync("zeta-slug"))?.Id);
        Assert.Equal("org-zeta-2", (await orgStore.GetBySlugAsync("zeta-slug-2"))?.Id);

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
