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

    /// <summary>
    /// Server-side keyset page: <c>PartitionKey eq pk and RowKey gt after</c>, reading at most one row
    /// past the page (which only proves a next page exists). Rows come back in RowKey order within the
    /// single partition, so no client-side sort is needed and a large tenant is never loaded whole.
    /// </summary>
    public async Task<OrganizationPage> ListPageAsync(string? cursor, int limit, CancellationToken ct = default)
    {
        var after = KeysetCursor.Decode(cursor);
        var take = KeysetCursor.ClampLimit(limit);
        var pk = partitioner.PK(OrganizationEntity.OrganizationsPartition);

        var filter = after is null
            ? TableClient.CreateQueryFilter($"PartitionKey eq {pk}")
            : TableClient.CreateQueryFilter($"PartitionKey eq {pk} and RowKey gt {after}");

        var window = new List<Organization>(take + 1);
        await foreach (var entity in organizationsTable.QueryAsync<OrganizationEntity>(
            filter, maxPerPage: take + 1, cancellationToken: ct))
        {
            window.Add(entity.ToModel());
            if (window.Count > take) break;
        }

        var (items, next) = KeysetCursor.Page(window, o => o.Id, take);
        return new OrganizationPage(items, next);
    }

    public async Task UpsertAsync(Organization organization, CancellationToken ct = default)
    {
        // R7: the pattern lives once, in Core, so every provider and the in-memory reference enforce
        // the identical rule rather than each keeping its own copy in sync by hand.
        OrganizationSlug.Validate(organization.Slug);

        var orgsPk = partitioner.PK(OrganizationEntity.OrganizationsPartition);
        var slugsPk = partitioner.PK(OrganizationSlugEntity.SlugsPartition);

        // F11: the slug is immutable once set. A rename attempt is refused before anything else is
        // even looked at, let alone written — the whole point is that a relying party can hard-code
        // it.
        OrganizationEntity? existingOrg = null;
        try
        {
            existingOrg = (await organizationsTable.GetEntityAsync<OrganizationEntity>(
                orgsPk, organization.Id, cancellationToken: ct)).Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
        }

        if (existingOrg is not null && !string.Equals(existingOrg.Slug, organization.Slug, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Organization '{organization.Id}' slug is immutable: cannot change '{existingOrg.Slug}' to '{organization.Slug}'.");

        // Cross-namespace: an id and a slug must never resolve to two different organizations, or
        // which one an authorize request means depends on which of the two lookups the caller used.
        try
        {
            var idOwner = await organizationsTable.GetEntityAsync<OrganizationEntity>(
                orgsPk, organization.Slug, cancellationToken: ct);
            if (!string.Equals(idOwner.Value.RowKey, organization.Id, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Slug '{organization.Slug}' collides with organization id '{idOwner.Value.RowKey}'.");
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
        }

        try
        {
            var slugOwner = await organizationSlugsTable.GetEntityAsync<OrganizationSlugEntity>(
                slugsPk, organization.Id, cancellationToken: ct);
            if (!string.Equals(slugOwner.Value.OrganizationId, organization.Id, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Organization id '{organization.Id}' collides with slug '{organization.Id}', held by organization '{slugOwner.Value.OrganizationId}'.");
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
        }

        if (existingOrg is null)
        {
            // F4: uniqueness is enforced by the storage layer's own insert-only primitive rather than
            // a read-then-write check, so two concurrent creates racing on the same new slug can never
            // both win — the loser's AddEntityAsync gets the 409, not a check that ran too early.
            var slugEntity = OrganizationEntity.CreateSlugIndex(organization);
            slugEntity.PartitionKey = partitioner.PK(slugEntity.PartitionKey);
            try
            {
                await organizationSlugsTable.AddEntityAsync(slugEntity, ct);
                if (changeWriter is not null)
                    await changeWriter.WriteUpsertAsync("OrganizationSlugs", slugEntity.PartitionKey, slugEntity.RowKey, ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                // R2: the slug row can already exist and point at OUR id — a retry of this exact
                // create after a crash between this insert and the org-row write below. Self-heal by
                // falling through to (re)write the org row, rather than reporting the organization as
                // already held by an organization that, from the org table's perspective, was never
                // created. Only a DIFFERENT owner is a genuine conflict.
                OrganizationSlugEntity? winner = null;
                try
                {
                    winner = (await organizationSlugsTable.GetEntityAsync<OrganizationSlugEntity>(
                        slugsPk, organization.Slug, cancellationToken: ct)).Value;
                }
                catch (RequestFailedException)
                {
                }

                if (winner is null || !string.Equals(winner.OrganizationId, organization.Id, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Slug '{organization.Slug}' is already held by organization '{winner?.OrganizationId ?? "another organization"}'.");
            }
        }
        // Existing organization: the slug is unchanged (enforced above), so its slug row already
        // points here and is left as is — nothing to write on this table for an update.

        var entity = OrganizationEntity.FromModel(organization);
        entity.PartitionKey = partitioner.PK(entity.PartitionKey);
        await organizationsTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
        if (changeWriter is not null)
            await changeWriter.WriteUpsertAsync("Organizations", entity.PartitionKey, entity.RowKey, ct);
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
