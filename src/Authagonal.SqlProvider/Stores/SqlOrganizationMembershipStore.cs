using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.SqlProvider.Sql;

namespace Authagonal.SqlProvider.Stores;

/// <summary>
/// SQL <see cref="IOrganizationMembershipStore"/>. Dual index, the same shape as
/// <see cref="SqlScimTokenStore"/>'s forward/reverse rows: <c>OrganizationMembers</c>
/// (pk = "org|{organizationId}", sk = userId) answers "who is in this organization" and backs the
/// single-row <see cref="GetAsync"/>; <c>UserMemberships</c> (pk = "user|{userId}", sk = organizationId)
/// answers "which organizations may this person authenticate as". Both rows carry the full membership
/// document and are kept in sync on every write.
/// </summary>
public sealed class SqlOrganizationMembershipStore(
    SqlTable members, SqlTable userMemberships, EnvPartitioner partitioner, IChangeWriter? tombstones = null)
    : IOrganizationMembershipStore
{
    private static string OrgPk(EnvPartitioner p, string organizationId) => p.PK($"org|{organizationId}");
    private static string UserPk(EnvPartitioner p, string userId) => p.PK($"user|{userId}");

    public async Task<OrganizationMembership?> GetAsync(string organizationId, string userId, CancellationToken ct = default)
    {
        var row = await members.GetAsync(OrgPk(partitioner, organizationId), userId, ct: ct).ConfigureAwait(false);
        return row is null ? null : Read(row);
    }

    public async Task<IReadOnlyList<OrganizationMembership>> ListByUserAsync(string userId, CancellationToken ct = default)
    {
        var results = new List<OrganizationMembership>();
        await foreach (var row in userMemberships.QueryPartitionAsync(UserPk(partitioner, userId), ct).ConfigureAwait(false))
            results.Add(Read(row));
        return results;
    }

    public async Task<IReadOnlyList<OrganizationMembership>> ListByOrganizationAsync(string organizationId, CancellationToken ct = default)
    {
        var results = new List<OrganizationMembership>();
        await foreach (var row in members.QueryPartitionAsync(OrgPk(partitioner, organizationId), ct).ConfigureAwait(false))
            results.Add(Read(row));
        return results;
    }

    public async Task UpsertAsync(OrganizationMembership membership, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(membership, SqlJsonContext.Default.OrganizationMembership);

        var memberRow = new SqlRow(OrgPk(partitioner, membership.OrganizationId), membership.UserId) { Data = json };
        var userRow = new SqlRow(UserPk(partitioner, membership.UserId), membership.OrganizationId) { Data = json };

        await members.PutAsync(memberRow, ct).ConfigureAwait(false);
        await userMemberships.PutAsync(userRow, ct).ConfigureAwait(false);

        if (tombstones is not null)
        {
            await tombstones.WriteUpsertAsync("OrganizationMembers", memberRow.Pk, memberRow.Sk, ct).ConfigureAwait(false);
            await tombstones.WriteUpsertAsync("UserMemberships", userRow.Pk, userRow.Sk, ct).ConfigureAwait(false);
        }
    }

    public async Task DeleteAsync(string organizationId, string userId, CancellationToken ct = default)
    {
        var orgPk = OrgPk(partitioner, organizationId);
        var userPk = UserPk(partitioner, userId);

        var removed = await members.DeleteIfExistsReturningAsync(orgPk, userId, ct).ConfigureAwait(false);
        await userMemberships.DeleteAsync(userPk, organizationId, ct).ConfigureAwait(false);

        if (removed is not null && tombstones is not null)
        {
            await tombstones.WriteAsync("OrganizationMembers", orgPk, userId, ct).ConfigureAwait(false);
            await tombstones.WriteAsync("UserMemberships", userPk, organizationId, ct).ConfigureAwait(false);
        }
    }

    private static OrganizationMembership Read(SqlRow row)
        => JsonSerializer.Deserialize(row.DataOrEmpty, SqlJsonContext.Default.OrganizationMembership)!;
}
