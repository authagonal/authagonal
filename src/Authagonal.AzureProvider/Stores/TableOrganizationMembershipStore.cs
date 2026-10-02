using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Core.Services;
using Authagonal.AzureProvider.Entities;

namespace Authagonal.AzureProvider.Stores;

public sealed class TableOrganizationMembershipStore(
    TableClient organizationMembersTable,
    TableClient userMembershipsTable,
    EnvPartitioner partitioner,
    IChangeWriter? changeWriter = null) : IOrganizationMembershipStore
{
    public async Task<OrganizationMembership?> GetAsync(string organizationId, string userId, CancellationToken ct = default)
    {
        try
        {
            var response = await organizationMembersTable.GetEntityAsync<OrganizationMembershipEntity>(
                partitioner.PK(OrganizationMembershipEntity.OrgPartition(organizationId)), userId, cancellationToken: ct);
            return response.Value.ToModel();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<OrganizationMembership>> ListByUserAsync(string userId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(OrganizationMembershipEntity.UserPartition(userId));
        var results = new List<OrganizationMembership>();
        await foreach (var entity in userMembershipsTable.QueryAsync<OrganizationMembershipEntity>(
            e => e.PartitionKey == pk, cancellationToken: ct))
        {
            results.Add(entity.ToModel());
        }
        return results;
    }

    public async Task<IReadOnlyList<OrganizationMembership>> ListByOrganizationAsync(string organizationId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(OrganizationMembershipEntity.OrgPartition(organizationId));
        var results = new List<OrganizationMembership>();
        await foreach (var entity in organizationMembersTable.QueryAsync<OrganizationMembershipEntity>(
            e => e.PartitionKey == pk, cancellationToken: ct))
        {
            results.Add(entity.ToModel());
        }
        return results;
    }

    /// <summary>
    /// Server-side keyset page over the org-partitioned table: <c>PartitionKey eq pk and RowKey gt
    /// after</c> (RowKey = user id), reading at most one row past the page.
    /// </summary>
    public async Task<OrganizationMembershipPage> ListByOrganizationPageAsync(
        string organizationId, string? cursor, int limit, CancellationToken ct = default)
    {
        var after = KeysetCursor.Decode(cursor);
        var take = KeysetCursor.ClampLimit(limit);
        var pk = partitioner.PK(OrganizationMembershipEntity.OrgPartition(organizationId));

        var filter = after is null
            ? TableClient.CreateQueryFilter($"PartitionKey eq {pk}")
            : TableClient.CreateQueryFilter($"PartitionKey eq {pk} and RowKey gt {after}");

        var window = new List<OrganizationMembership>(take + 1);
        await foreach (var entity in organizationMembersTable.QueryAsync<OrganizationMembershipEntity>(
            filter, maxPerPage: take + 1, cancellationToken: ct))
        {
            window.Add(entity.ToModel());
            if (window.Count > take) break;
        }

        var (items, next) = KeysetCursor.Page(window, m => m.UserId, take);
        return new OrganizationMembershipPage(items, next);
    }

    public async Task UpsertAsync(OrganizationMembership membership, CancellationToken ct = default)
    {
        var orgRow = OrganizationMembershipEntity.FromModelForOrganization(membership);
        orgRow.PartitionKey = partitioner.PK(orgRow.PartitionKey);
        await organizationMembersTable.UpsertEntityAsync(orgRow, TableUpdateMode.Replace, ct);
        if (changeWriter is not null)
            await changeWriter.WriteUpsertAsync("OrganizationMembers", orgRow.PartitionKey, orgRow.RowKey, ct);

        var userRow = OrganizationMembershipEntity.FromModelForUser(membership);
        userRow.PartitionKey = partitioner.PK(userRow.PartitionKey);
        await userMembershipsTable.UpsertEntityAsync(userRow, TableUpdateMode.Replace, ct);
        if (changeWriter is not null)
            await changeWriter.WriteUpsertAsync("UserMemberships", userRow.PartitionKey, userRow.RowKey, ct);
    }

    public async Task DeleteAsync(string organizationId, string userId, CancellationToken ct = default)
    {
        var orgPk = partitioner.PK(OrganizationMembershipEntity.OrgPartition(organizationId));
        var userPk = partitioner.PK(OrganizationMembershipEntity.UserPartition(userId));

        // Each row is handled independently of the other's presence (F18) — an orphan left by an
        // earlier partial write must still be cleaned up on its own side — but R9: a row that never
        // existed gets neither a delete attempt nor a tombstone. That decision has to be made from an
        // existence check made BEFORE the tombstone, not from the delete's own 404, or a row that
        // does exist would have its data removed before the crash-safe (F24e) tombstone-first record
        // of that removal.
        await DeleteRowIfPresentAsync(organizationMembersTable, "OrganizationMembers", orgPk, userId, ct);
        await DeleteRowIfPresentAsync(userMembershipsTable, "UserMemberships", userPk, organizationId, ct);
    }

    private async Task DeleteRowIfPresentAsync(TableClient table, string logicalTable, string pk, string rk, CancellationToken ct)
    {
        try
        {
            await table.GetEntityAsync<OrganizationMembershipEntity>(pk, rk, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return; // Nothing there — nothing to tombstone or delete.
        }

        // Tombstone-first (F24e): record the delete before removing the row.
        if (changeWriter is not null)
            await changeWriter.WriteAsync(logicalTable, pk, rk, ct);

        try
        {
            await table.DeleteEntityAsync(pk, rk, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }
}
