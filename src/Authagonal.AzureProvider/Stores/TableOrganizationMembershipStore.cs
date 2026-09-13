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

        // Tombstone-first (F24e): record both deletes before removing anything.
        if (changeWriter is not null)
        {
            await changeWriter.WriteAsync("OrganizationMembers", orgPk, userId, ct);
            await changeWriter.WriteAsync("UserMemberships", userPk, organizationId, ct);
        }

        try
        {
            await organizationMembersTable.DeleteEntityAsync(orgPk, userId, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }

        try
        {
            await userMembershipsTable.DeleteEntityAsync(userPk, organizationId, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }
}
