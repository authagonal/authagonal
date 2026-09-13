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
/// rather than a scan. The slug is immutable once an organization exists — <see cref="UpsertAsync"/>
/// refuses to change it — and ids/slugs share no namespace, so a new organization is also refused
/// when its slug reads as an existing organization's id or its id reads as an existing organization's
/// slug. A brand-new slug is claimed with an insert-only conditional write before the organization
/// document itself is written, so two concurrent creates for the same new slug cannot both succeed;
/// a crash between that write and the organization write is self-healed rather than refused forever,
/// because the lost race and "this is my own half-finished create" look identical except for who owns
/// the slug row already there.</summary>
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
        // The shape a slug must have lives once on the model, not re-declared per store.
        OrganizationSlug.Validate(organization.Slug);

        var orgPk = partitioner.PK(OrgPartition);
        var slugPk = partitioner.PK(SlugPartition);

        var existingItem = await organizations.GetAsync(orgPk, organization.Id, ct).ConfigureAwait(false);
        if (existingItem is not null)
        {
            // The slug is immutable once set: it is the value a relying party hard-codes (as the
            // `organization` authorize parameter), and changing what it resolves to under the same
            // string is a silent redirect to a different customer, not an update.
            var existing = Read(existingItem);
            if (!string.Equals(existing.Slug, organization.Slug, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Organization '{organization.Id}' slug is immutable; cannot change '{existing.Slug}' to '{organization.Slug}'.");
            }

            await organizations.PutAsync(OrgItem(orgPk, organization), ct).ConfigureAwait(false);
            if (tombstones is not null)
                await tombstones.WriteUpsertAsync("Organizations", orgPk, organization.Id, ct).ConfigureAwait(false);
            return;
        }

        // New organization. Ids and slugs share no namespace: a slug that reads as another
        // organization's id, or an id that reads as another organization's slug, would make "which
        // one did the caller mean" ambiguous wherever a bare string is accepted as either.
        var idClash = await organizations.GetAsync(orgPk, organization.Slug, ct).ConfigureAwait(false);
        if (idClash is not null)
            throw new InvalidOperationException($"Slug '{organization.Slug}' is already in use as another organization's id.");

        var slugClash = await slugs.GetAsync(slugPk, organization.Id, ct).ConfigureAwait(false);
        if (slugClash is not null)
        {
            var owner = slugClash.GetStr("data");
            throw new InvalidOperationException($"Id '{organization.Id}' is already held by organization '{owner}' as its slug.");
        }

        // Insert-only: exactly one concurrent creator of the same brand-new slug wins. This is the
        // real uniqueness guarantee — the reads above are a fast, friendlier rejection for the
        // common (non-racing) case, not a substitute for it.
        var slugItem = Dyn.Item(slugPk, organization.Slug);
        slugItem.PutS("data", organization.Id);
        if (await slugs.PutIfAbsentAsync(slugItem, ct).ConfigureAwait(false))
        {
            if (tombstones is not null)
                await tombstones.WriteUpsertAsync("OrganizationSlugs", slugPk, organization.Slug, ct).ConfigureAwait(false);
        }
        else
        {
            // Lost the race — or this IS the winner, retrying after a crash between this write and
            // the organization write below. Both look identical from here except for who the slug
            // row names: the same id means self-heal by proceeding to (re)write the organization
            // item rather than refusing forever; a different id is a genuine conflict.
            var current = await slugs.GetAsync(slugPk, organization.Slug, ct).ConfigureAwait(false);
            if (!string.Equals(current?.GetStr("data"), organization.Id, StringComparison.Ordinal))
                throw new InvalidOperationException($"Slug '{organization.Slug}' is already held by another organization.");
        }

        await organizations.PutAsync(OrgItem(orgPk, organization), ct).ConfigureAwait(false);
        if (tombstones is not null)
            await tombstones.WriteUpsertAsync("Organizations", orgPk, organization.Id, ct).ConfigureAwait(false);
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

    private static Dictionary<string, AttributeValue> OrgItem(string orgPk, Organization organization)
    {
        var item = Dyn.Item(orgPk, organization.Id);
        item.PutS("data", JsonSerializer.Serialize(organization, AwsJsonContext.Default.Organization));
        return item;
    }

    private static Organization Read(Dictionary<string, AttributeValue> item)
        => JsonSerializer.Deserialize(item.GetStr("data"), AwsJsonContext.Default.Organization)!;
}
