using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Authagonal.AwsProvider.Dynamo;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.AwsProvider.Stores;

/// <summary>DynamoDB <see cref="IOrganizationMembershipStore"/>. Dual index: a forward row
/// (pk = "org|{orgId}", sk = userId) answers the point-read <see cref="GetAsync"/> and the
/// <see cref="ListByOrganizationAsync"/> query; a reverse row (pk = "user|{userId}", sk = orgId)
/// answers <see cref="ListByUserAsync"/>. Both rows carry the full membership document and are kept
/// in sync. <see cref="DeleteAsync"/> attempts both sides independently and unconditionally — it does
/// not check either row's existence first and bail out, so a membership left inconsistent by an
/// earlier partial write (one row present, the other missing) is still fully cleaned up rather than
/// left half-orphaned forever — but it tombstones only the row that actually existed, so deleting
/// something already gone (on one side or both) does not fabricate change-log entries for rows that
/// were never there.</summary>
public sealed class DynamoOrganizationMembershipStore(
    DynamoTable orgMembers,
    DynamoTable userMemberships,
    EnvPartitioner partitioner,
    IChangeWriter? tombstones = null) : IOrganizationMembershipStore
{
    private string OrgPk(string organizationId) => partitioner.PK($"org|{organizationId}");
    private string UserPk(string userId) => partitioner.PK($"user|{userId}");

    public async Task<OrganizationMembership?> GetAsync(string organizationId, string userId, CancellationToken ct = default)
    {
        var item = await orgMembers.GetAsync(OrgPk(organizationId), userId, ct).ConfigureAwait(false);
        return item is null ? null : Read(item);
    }

    public async Task<IReadOnlyList<OrganizationMembership>> ListByUserAsync(string userId, CancellationToken ct = default)
    {
        var results = new List<OrganizationMembership>();
        await foreach (var item in userMemberships.QueryAsync(UserPk(userId), ct: ct).ConfigureAwait(false))
            results.Add(Read(item));
        return results;
    }

    public async Task<IReadOnlyList<OrganizationMembership>> ListByOrganizationAsync(string organizationId, CancellationToken ct = default)
    {
        var results = new List<OrganizationMembership>();
        await foreach (var item in orgMembers.QueryAsync(OrgPk(organizationId), ct: ct).ConfigureAwait(false))
            results.Add(Read(item));
        return results;
    }

    public async Task UpsertAsync(OrganizationMembership membership, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(membership, AwsJsonContext.Default.OrganizationMembership);
        var orgPk = OrgPk(membership.OrganizationId);
        var userPk = UserPk(membership.UserId);

        var forward = Dyn.Item(orgPk, membership.UserId);
        forward.PutS("data", json);
        var reverse = Dyn.Item(userPk, membership.OrganizationId);
        reverse.PutS("data", json);

        await orgMembers.PutAsync(forward, ct).ConfigureAwait(false);
        await userMemberships.PutAsync(reverse, ct).ConfigureAwait(false);

        if (tombstones is not null)
        {
            await tombstones.WriteUpsertAsync("OrganizationMembers", orgPk, membership.UserId, ct).ConfigureAwait(false);
            await tombstones.WriteUpsertAsync("UserMemberships", userPk, membership.OrganizationId, ct).ConfigureAwait(false);
        }
    }

    public async Task DeleteAsync(string organizationId, string userId, CancellationToken ct = default)
    {
        var orgPk = OrgPk(organizationId);
        var userPk = UserPk(userId);

        // Conditional delete-returning on each side independently — no existence check that bails out
        // early (checking the forward row first, as the dual-index token store does, would leave a
        // reverse row stranded forever if the two ever fell out of sync), but also no tombstone for a
        // row that was never there: ALL_OLD comes back null when nothing was removed, and that is
        // exactly when this store should stay silent about that row in the change log.
        var forwardRemoved = await orgMembers.DeleteIfExistsReturningAsync(orgPk, userId, ct).ConfigureAwait(false);
        var reverseRemoved = await userMemberships.DeleteIfExistsReturningAsync(userPk, organizationId, ct).ConfigureAwait(false);

        if (tombstones is not null)
        {
            if (forwardRemoved is not null)
                await tombstones.WriteAsync("OrganizationMembers", orgPk, userId, ct).ConfigureAwait(false);
            if (reverseRemoved is not null)
                await tombstones.WriteAsync("UserMemberships", userPk, organizationId, ct).ConfigureAwait(false);
        }
    }

    private static OrganizationMembership Read(Dictionary<string, AttributeValue> item)
        => JsonSerializer.Deserialize(item.GetStr("data"), AwsJsonContext.Default.OrganizationMembership)!;
}
