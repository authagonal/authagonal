using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Core.Services;
using Authagonal.AzureProvider.Entities;

namespace Authagonal.AzureProvider.Stores;

public sealed class TableOrganizationStore(
    TableClient organizationsTable,
    TableClient organizationSlugsTable,
    EnvPartitioner partitioner,
    IChangeWriter? changeWriter = null) : IOrganizationStore
{
    public async Task<Organization?> GetAsync(string organizationId, CancellationToken ct = default)
    {
        try
        {
            var response = await organizationsTable.GetEntityAsync<OrganizationEntity>(
                partitioner.PK(OrganizationEntity.OrganizationsPartition), organizationId, cancellationToken: ct);
            return response.Value.ToModel();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<Organization?> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        try
        {
            var index = await organizationSlugsTable.GetEntityAsync<OrganizationSlugEntity>(
                partitioner.PK(OrganizationSlugEntity.SlugsPartition), slug, cancellationToken: ct);
            return await GetAsync(index.Value.OrganizationId, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<Organization>> ListAsync(CancellationToken ct = default)
    {
        var pk = partitioner.PK(OrganizationEntity.OrganizationsPartition);
        var results = new List<Organization>();
        await foreach (var entity in organizationsTable.QueryAsync<OrganizationEntity>(
            e => e.PartitionKey == pk, cancellationToken: ct))
        {
            results.Add(entity.ToModel());
        }
        return results;
    }

    public async Task UpsertAsync(Organization organization, CancellationToken ct = default)
    {
        var orgsPk = partitioner.PK(OrganizationEntity.OrganizationsPartition);
        var slugsPk = partitioner.PK(OrganizationSlugEntity.SlugsPartition);

        // Reject a slug already held by a different organization — two rows answering one slug would
        // make the `organization` authorize parameter ambiguous.
        try
        {
            var existingSlug = await organizationSlugsTable.GetEntityAsync<OrganizationSlugEntity>(
                slugsPk, organization.Slug, cancellationToken: ct);
            if (!string.Equals(existingSlug.Value.OrganizationId, organization.Id, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Slug '{organization.Slug}' is already held by organization '{existingSlug.Value.OrganizationId}'.");
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
        }

        // Find the previous slug, if this is an update that renames it, so the stale index row can be
        // dropped once the new one is in place.
        string? previousSlug = null;
        try
        {
            var existingOrg = await organizationsTable.GetEntityAsync<OrganizationEntity>(orgsPk, organization.Id, cancellationToken: ct);
            if (!string.Equals(existingOrg.Value.Slug, organization.Slug, StringComparison.Ordinal))
                previousSlug = existingOrg.Value.Slug;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
        }

        var entity = OrganizationEntity.FromModel(organization);
        entity.PartitionKey = partitioner.PK(entity.PartitionKey);
        await organizationsTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
        if (changeWriter is not null)
            await changeWriter.WriteUpsertAsync("Organizations", entity.PartitionKey, entity.RowKey, ct);

        // New slug row before the stale one is dropped: a crash in between leaves a harmless extra
        // pointer, never a missing one.
        var slugEntity = OrganizationEntity.CreateSlugIndex(organization);
        slugEntity.PartitionKey = partitioner.PK(slugEntity.PartitionKey);
        await organizationSlugsTable.UpsertEntityAsync(slugEntity, TableUpdateMode.Replace, ct);
        if (changeWriter is not null)
            await changeWriter.WriteUpsertAsync("OrganizationSlugs", slugEntity.PartitionKey, slugEntity.RowKey, ct);

        if (previousSlug is not null)
        {
            if (changeWriter is not null)
                await changeWriter.WriteAsync("OrganizationSlugs", slugsPk, previousSlug, ct);
            try
            {
                await organizationSlugsTable.DeleteEntityAsync(slugsPk, previousSlug, cancellationToken: ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }
        }
    }

    public async Task DeleteAsync(string organizationId, CancellationToken ct = default)
    {
        var orgsPk = partitioner.PK(OrganizationEntity.OrganizationsPartition);
        try
        {
            var existing = await organizationsTable.GetEntityAsync<OrganizationEntity>(orgsPk, organizationId, cancellationToken: ct);
            var slug = existing.Value.Slug;
            var slugsPk = partitioner.PK(OrganizationSlugEntity.SlugsPartition);

            // Tombstone-first (F24e): record both deletes before removing anything.
            if (changeWriter is not null)
            {
                await changeWriter.WriteAsync("Organizations", orgsPk, organizationId, ct);
                await changeWriter.WriteAsync("OrganizationSlugs", slugsPk, slug, ct);
            }

            try
            {
                await organizationSlugsTable.DeleteEntityAsync(slugsPk, slug, cancellationToken: ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }

            // Memberships are NOT cascaded here — the caller owns that ordering (IOrganizationStore).
            await organizationsTable.DeleteEntityAsync(orgsPk, organizationId, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
        }
    }
}
