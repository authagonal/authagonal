using Authagonal.Core.Models;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Tests;

/// <summary>
/// <see cref="OrganizationOperations.BackfillAsync"/>: the dry run that writes nothing, the real run that
/// creates organisations and memberships from <see cref="AuthUser.OrganizationId"/>, the re-run that
/// creates nothing, the rename that must not defeat it, and the scan bound that reports <c>truncated</c>.
/// </summary>
public sealed class OrganizationBackfillTests
{
    private const string Actor = "admin@example.test";

    private readonly WritableOrganizationStore _orgs = new();
    private readonly WritableOrganizationMembershipStore _members = new();
    private readonly InMemoryUserStore _users = new();
    private readonly RecordingAuditLogger _audit = new();
    private readonly OrganizationOperations _ops;

    public OrganizationBackfillTests()
    {
        _ops = new OrganizationOperations(_orgs, _members, _users, _audit, new FakeTxtResolver(), NullLogger<OrganizationOperations>.Instance);
    }

    private static CancellationToken Ct => CancellationToken.None;

    private Task SeedAsync(string id, string? legacyOrg, int ageSeconds = 0)
    {
        var email = $"{id}@example.test";
        return _users.CreateAsync(new AuthUser
        {
            Id = id,
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            OrganizationId = legacyOrg,
            CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(ageSeconds),
        });
    }

    [Fact]
    public async Task DryRun_ReportsTheCountsAndWritesNothing()
    {
        await SeedAsync("u1", "Acme Corp");
        await SeedAsync("u2", "Acme Corp");
        await SeedAsync("u3", "Globex");
        await SeedAsync("u4", null);

        var result = await _ops.BackfillAsync(true, Actor, Ct);

        Assert.True(result.DryRun);
        Assert.Equal(2, result.OrganizationsCreated);
        Assert.Equal(3, result.MembershipsCreated);
        Assert.False(result.Truncated);
        Assert.Empty(await _orgs.ListAsync());
        Assert.Empty(_audit.Entries);
        Assert.Equal(["acme-corp", "globex"], result.Organizations.Select(o => o.Slug).Order().ToArray());
    }

    [Fact]
    public async Task RealRun_CreatesOrganizationsAndActiveMemberships_AndAuditsOnce()
    {
        await SeedAsync("u1", "Acme Corp");
        await SeedAsync("u2", "Acme Corp");
        await SeedAsync("u3", "Globex");

        var result = await _ops.BackfillAsync(false, Actor, Ct);

        Assert.False(result.DryRun);
        Assert.Equal(2, result.OrganizationsCreated);
        Assert.Equal(3, result.MembershipsCreated);
        var acme = await _orgs.GetBySlugAsync("acme-corp");
        Assert.NotNull(acme);
        Assert.Equal("Acme Corp", acme!.DisplayName);
        var member = await _members.GetAsync(acme.Id, "u1");
        Assert.NotNull(member);
        Assert.Equal(MembershipStatus.Active, member!.Status);
        Assert.NotNull(await _members.GetAsync(acme.Id, "u2"));
        var audit = Assert.Single(_audit.Entries);
        Assert.Equal("organization.backfill", audit.Action);
        Assert.Equal(Actor, audit.Actor);
        Assert.Equal("organizationsCreated=2 membershipsCreated=3", audit.Detail);
    }

    [Fact]
    public async Task SecondRun_CreatesNothing()
    {
        await SeedAsync("u1", "Acme Corp");
        await SeedAsync("u2", "Globex");
        await _ops.BackfillAsync(false, Actor, Ct);

        var again = await _ops.BackfillAsync(false, Actor, Ct);

        Assert.Equal(0, again.OrganizationsCreated);
        Assert.Equal(0, again.MembershipsCreated);
        Assert.Equal(2, again.UsersSkipped);
        Assert.Equal(2, (await _orgs.ListAsync()).Count);
    }

    [Fact]
    public async Task SecondRun_AfterTheOrganizationWasRenamed_StillCreatesNothing()
    {
        await SeedAsync("u1", "Acme Corp");
        await _ops.BackfillAsync(false, Actor, Ct);
        var org = (await _orgs.GetBySlugAsync("acme-corp"))!;
        org.DisplayName = "Acme Corporation Pty Ltd";
        await _orgs.UpsertAsync(org);

        var again = await _ops.BackfillAsync(false, Actor, Ct);

        Assert.Equal(0, again.OrganizationsCreated);
        Assert.Equal(0, again.MembershipsCreated);
        Assert.Single(await _orgs.ListAsync());
    }

    [Fact]
    public async Task SecondRun_AfterTheOrganizationWasUpdatedThroughUpdateAsync_StillCreatesNothing()
    {
        await SeedAsync("u1", "Acme Corp");
        await _ops.BackfillAsync(false, Actor, Ct);
        var org = (await _orgs.GetBySlugAsync("acme-corp"))!;

        var updated = await _ops.UpdateAsync(org.Id, "Acme Corporation", null, null, null, null, null,
            new Dictionary<string, string> { ["tier"] = "gold" }, Actor, Ct);
        Assert.Equal(OperationOutcome.Ok, updated.Outcome);

        var again = await _ops.BackfillAsync(false, Actor, Ct);

        Assert.Equal(0, again.OrganizationsCreated);
        Assert.Equal(0, again.MembershipsCreated);
        Assert.Single(await _orgs.ListAsync());
    }

    [Fact]
    public async Task LegacyValuesThatDeriveTheSameSlug_GetNumberedSlugs()
    {
        await SeedAsync("u1", "Acme Corp");
        await SeedAsync("u2", "acme-corp!");

        var result = await _ops.BackfillAsync(false, Actor, Ct);

        Assert.Equal(2, result.OrganizationsCreated);
        Assert.NotNull(await _orgs.GetBySlugAsync("acme-corp"));
        Assert.NotNull(await _orgs.GetBySlugAsync("acme-corp-2"));
    }

    [Fact]
    public async Task ALegacyValueEqualToAnExistingOrganizationsId_TakesTheNextSuffix_AndTheRunCompletes()
    {
        const string bareGuidId = "0123456789abcdef0123456789abcdef";
        await _orgs.UpsertAsync(new Organization { Id = bareGuidId, Slug = "legacy-slug", DisplayName = "Legacy" });
        await SeedAsync("u1", bareGuidId);

        var result = await _ops.BackfillAsync(false, Actor, Ct);

        Assert.Equal(1, result.OrganizationsCreated);
        Assert.Equal(1, result.MembershipsCreated);
        Assert.NotNull(await _orgs.GetBySlugAsync(bareGuidId + "-2"));
        Assert.True(_audit.Has("organization.backfill"));
    }

    [Fact]
    public async Task ScanStopsAtMaxUsersScanned_AndReportsTruncated()
    {
        var total = OrganizationOperations.MaxUsersScanned + 400;
        for (var i = 0; i < total; i++)
            await SeedAsync($"bulk{i:D6}", i % 2 == 0 ? "Even" : "Odd", i);

        var result = await _ops.BackfillAsync(true, Actor, Ct);

        Assert.True(result.Truncated);
        Assert.True(result.MembershipsCreated >= OrganizationOperations.MaxUsersScanned);
        Assert.True(result.MembershipsCreated < total);
    }

    [Fact]
    public async Task ExactlyMaxUsersScanned_IsNotTruncated()
    {
        for (var i = 0; i < OrganizationOperations.MaxUsersScanned; i++)
            await SeedAsync($"bulk{i:D6}", "Everyone", i);

        var result = await _ops.BackfillAsync(true, Actor, Ct);

        Assert.False(result.Truncated);
        Assert.Equal(OrganizationOperations.MaxUsersScanned, result.MembershipsCreated);
    }
}
