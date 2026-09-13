using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Authagonal.AwsProvider.Dynamo;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.AwsProvider.Stores;

/// <summary>DynamoDB <see cref="IOrganizationStore"/>. Two tables: the organization document
/// (pk = "org", sk = organizationId, data = the full document) and a slug lookup (pk = "orgslug",
/// sk = slug, data = the owning organization id) so <see cref="GetBySlugAsync"/> is a point read
/// rather than a scan. <see cref="UpsertAsync"/> writes the organization row then the new slug row,
/// dropping the stale slug row last when the slug changed; a slug already held by a different
/// organization is refused.</summary>
public sealed class DynamoOrganizationStore(
    DynamoTable organizations,
    DynamoTable slugs,
    EnvPartitioner partitioner,
    IChangeWriter? tombstones = null) : IOrganizationStore
{
    private const string OrgPartition = "org";
    private const string SlugPartition = "orgslug";

    public async Task<Organization?> GetAsync(string organizationId, CancellationToken ct = default)
    {
        var item = await organizations.GetAsync(partitioner.PK(OrgPartition), organizationId, ct).ConfigureAwait(false);
        return item is null ? null : Read(item);
    }

    public async Task<Organization?> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        var lookup = await slugs.GetAsync(partitioner.PK(SlugPartition), slug, ct).ConfigureAwait(false);
        return lookup is null ? null : await GetAsync(lookup.GetStr("data"), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Organization>> ListAsync(CancellationToken ct = default)
    {
        var results = new List<Organization>();
        await foreach (var item in organizations.QueryAsync(partitioner.PK(OrgPartition), ct: ct).ConfigureAwait(false))
            results.Add(Read(item));
        return results;
    }

    public async Task UpsertAsync(Organization organization, CancellationToken ct = default)
    {
        var orgPk = partitioner.PK(OrgPartition);
        var slugPk = partitioner.PK(SlugPartition);

        // A slug already answering for a different organization would make the `organization`
        // authorize parameter ambiguous — refuse rather than let the new row shadow it.
        var clash = await slugs.GetAsync(slugPk, organization.Slug, ct).ConfigureAwait(false);
        if (clash is not null && !string.Equals(clash.GetStr("data"), organization.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Slug '{organization.Slug}' is already held by organization '{clash.GetStr("data")}'.");
        }

        var existing = await organizations.GetAsync(orgPk, organization.Id, ct).ConfigureAwait(false);
        var previousSlug = existing is null ? null : Read(existing).Slug;

        var orgItem = Dyn.Item(orgPk, organization.Id);
        orgItem.PutS("data", JsonSerializer.Serialize(organization, AwsJsonContext.Default.Organization));
        await organizations.PutAsync(orgItem, ct).ConfigureAwait(false);

        var slugItem = Dyn.Item(slugPk, organization.Slug);
        slugItem.PutS("data", organization.Id);
        await slugs.PutAsync(slugItem, ct).ConfigureAwait(false);

        // Slug change: the new row above already resolves lookups for the new slug, so only now is
        // it safe to drop the stale one.
        if (previousSlug is not null && !string.Equals(previousSlug, organization.Slug, StringComparison.Ordinal))
        {
            await slugs.DeleteAsync(slugPk, previousSlug, ct).ConfigureAwait(false);
            if (tombstones is not null)
                await tombstones.WriteAsync("OrganizationSlugs", slugPk, previousSlug, ct).ConfigureAwait(false);
        }
    }

    public async Task DeleteAsync(string organizationId, CancellationToken ct = default)
    {
        var orgPk = partitioner.PK(OrgPartition);
        var old = await organizations.DeleteIfExistsReturningAsync(orgPk, organizationId, ct).ConfigureAwait(false);
        if (old is null) return; // already gone — no-op, matching every other store's delete semantics

        if (tombstones is not null)
            await tombstones.WriteAsync("Organizations", orgPk, organizationId, ct).ConfigureAwait(false);

        var slugPk = partitioner.PK(SlugPartition);
        var slug = Read(old).Slug;
        await slugs.DeleteAsync(slugPk, slug, ct).ConfigureAwait(false);
        if (tombstones is not null)
            await tombstones.WriteAsync("OrganizationSlugs", slugPk, slug, ct).ConfigureAwait(false);
    }

    private static Organization Read(Dictionary<string, AttributeValue> item)
        => JsonSerializer.Deserialize(item.GetStr("data"), AwsJsonContext.Default.Organization)!;
}
