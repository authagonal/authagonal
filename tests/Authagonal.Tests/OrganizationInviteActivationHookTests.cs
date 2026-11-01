using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Tests;

/// <summary>
/// <see cref="OrganizationInviteActivationHook"/>: accepting an invite (the address becoming confirmed)
/// turns the user's <c>invited</c> memberships active and nothing else.
/// </summary>
public sealed class OrganizationInviteActivationHookTests
{
    private const string UserId = "user-1";

    private readonly WritableOrganizationMembershipStore _members = new();
    private readonly RecordingAuditLogger _audit = new();

    private ServiceProvider Provider(IOrganizationMembershipStore? store = null) =>
        new ServiceCollection()
            .AddSingleton(store ?? _members)
            .AddSingleton<IAuditLogger>(_audit)
            .BuildServiceProvider();

    /// <summary>A hook with no request behind it, so it must open a scope of its own.</summary>
    private OrganizationInviteActivationHook ScopeHook(ServiceProvider provider) =>
        new(new HttpContextAccessor(), provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OrganizationInviteActivationHook>.Instance);

    private static OrganizationMembership Row(string org, string status, DateTimeOffset? joinedAt = null) => new()
    {
        OrganizationId = org,
        UserId = UserId,
        Status = status,
        Roles = ["billing"],
        InvitedByUserId = "inviter",
        InvitedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        CreatedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        JoinedAt = joinedAt,
    };

    [Fact]
    public async Task AnInvitedMembership_BecomesActive_KeepingRolesInviterAndInvitationTime()
    {
        await _members.UpsertAsync(Row("org_a", MembershipStatus.Invited));
        var before = DateTimeOffset.UtcNow;

        await ScopeHook(Provider()).OnEmailConfirmedAsync(UserId, "u@invite.test");

        var row = (await _members.GetAsync("org_a", UserId))!;
        Assert.Equal(MembershipStatus.Active, row.Status);
        Assert.Equal(["billing"], row.Roles);
        Assert.Equal("inviter", row.InvitedByUserId);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), row.InvitedAt);
        Assert.InRange(row.JoinedAt!.Value, before, DateTimeOffset.UtcNow);
        Assert.InRange(row.UpdatedAt!.Value, before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task EachActivationIsAuditedOnce_WithTheConfirmingUserAsActor()
    {
        await _members.UpsertAsync(Row("org_a", MembershipStatus.Invited));
        await _members.UpsertAsync(Row("org_b", MembershipStatus.Invited));

        await ScopeHook(Provider()).OnEmailConfirmedAsync(UserId, "u@invite.test");

        Assert.Equal(2, _audit.Entries.Count);
        Assert.All(_audit.Entries, e =>
        {
            Assert.Equal(UserId, e.Actor);
            Assert.Equal("organization.member.joined", e.Action);
            Assert.Equal("organization", e.EntityType);
            Assert.Equal($"user={UserId}", e.Detail);
        });
        Assert.Equal(["org_a", "org_b"], _audit.Entries.Select(e => e.EntityId).Order());
    }

    [Fact]
    public async Task SuspendedAndActiveMembershipsAreUntouched()
    {
        var joined = new DateTimeOffset(2025, 5, 5, 5, 5, 5, TimeSpan.Zero);
        await _members.UpsertAsync(Row("org_suspended", MembershipStatus.Suspended));
        await _members.UpsertAsync(Row("org_active", MembershipStatus.Active, joined));

        await ScopeHook(Provider()).OnEmailConfirmedAsync(UserId, "u@invite.test");

        Assert.Equal(MembershipStatus.Suspended, (await _members.GetAsync("org_suspended", UserId))!.Status);
        var active = (await _members.GetAsync("org_active", UserId))!;
        Assert.Equal(joined, active.JoinedAt);
        Assert.Null(active.UpdatedAt);
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task AnExistingJoinedAt_IsKept()
    {
        var joined = new DateTimeOffset(2025, 5, 5, 5, 5, 5, TimeSpan.Zero);
        await _members.UpsertAsync(Row("org_a", MembershipStatus.Invited, joined));

        await ScopeHook(Provider()).OnEmailConfirmedAsync(UserId, "u@invite.test");

        Assert.Equal(joined, (await _members.GetAsync("org_a", UserId))!.JoinedAt);
    }

    [Fact]
    public async Task ItIsIdempotent_ASecondRunFindsNothingInvited()
    {
        await _members.UpsertAsync(Row("org_a", MembershipStatus.Invited));
        var hook = ScopeHook(Provider());

        await hook.OnEmailConfirmedAsync(UserId, "u@invite.test");
        await hook.OnEmailConfirmedAsync(UserId, "u@invite.test");

        Assert.Single(_audit.Entries);
    }

    [Fact]
    public async Task AnotherUsersMembershipsAreUntouched()
    {
        await _members.UpsertAsync(new OrganizationMembership
        {
            OrganizationId = "org_a", UserId = "someone-else", Status = MembershipStatus.Invited,
        });

        await ScopeHook(Provider()).OnEmailConfirmedAsync(UserId, "u@invite.test");

        Assert.Equal(MembershipStatus.Invited, (await _members.GetAsync("org_a", "someone-else"))!.Status);
    }

    [Fact]
    public async Task WithARequest_ItUsesTheRequestsOwnServices()
    {
        await _members.UpsertAsync(Row("org_a", MembershipStatus.Invited));
        using var provider = Provider();
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { RequestServices = provider } };
        // A scope factory that would fail the test if it were used.
        var hook = new OrganizationInviteActivationHook(accessor, new ThrowingScopeFactory(), NullLogger<OrganizationInviteActivationHook>.Instance);

        await hook.OnEmailConfirmedAsync(UserId, "u@invite.test");

        Assert.Equal(MembershipStatus.Active, (await _members.GetAsync("org_a", UserId))!.Status);
    }

    [Fact]
    public async Task AStoreFailure_IsSwallowed_SoConfirmationNeverFails()
    {
        using var provider = Provider(new ThrowingMembershipStore(new InvalidOperationException("table down")));

        await ScopeHook(provider).OnEmailConfirmedAsync(UserId, "u@invite.test");

        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task ACancellation_IsNotSwallowed()
    {
        using var provider = Provider(new ThrowingMembershipStore(new OperationCanceledException()));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => ScopeHook(provider).OnEmailConfirmedAsync(UserId, "u@invite.test"));
    }

    [Fact]
    public async Task WithNoMembershipStoreRegistered_ItDoesNothing()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        await ScopeHook(provider).OnEmailConfirmedAsync(UserId, "u@invite.test");
    }

    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("a scope was opened despite a request");
    }

    private sealed class ThrowingMembershipStore(Exception failure) : IOrganizationMembershipStore
    {
        public Task<OrganizationMembership?> GetAsync(string organizationId, string userId, CancellationToken ct = default) => throw failure;
        public Task<IReadOnlyList<OrganizationMembership>> ListByUserAsync(string userId, CancellationToken ct = default) => throw failure;
        public Task<IReadOnlyList<OrganizationMembership>> ListByOrganizationAsync(string organizationId, CancellationToken ct = default) => throw failure;
        public Task UpsertAsync(OrganizationMembership membership, CancellationToken ct = default) => throw failure;
        public Task DeleteAsync(string organizationId, string userId, CancellationToken ct = default) => throw failure;
    }
}
