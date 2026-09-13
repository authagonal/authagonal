using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.SqlProvider.Sql;

namespace Authagonal.SqlProvider.Stores;

/// <summary>
/// SQL <see cref="IOrganizationStore"/>. Two tables: all organizations share one partition ("org"),
/// sk = <see cref="Organization.Id"/>, carrying the full document; the slug index shares its own
/// partition ("orgslug"), sk = <see cref="Organization.Slug"/>, carrying only the <c>organizationId</c>
/// attribute the point lookup needs to redirect into the first table. Same two-table shape as
/// <see cref="SqlRoleStore"/>/<see cref="SqlScopeStore"/> for the primary row, plus a slug index the
/// same way <see cref="SqlSsoDomainStore"/> indexes by domain.
/// </summary>
public sealed class SqlOrganizationStore(
    SqlTable organizations, SqlTable slugs, EnvPartitioner partitioner, IChangeWriter? tombstones = null)
    : IOrganizationStore
{
    private const string OrgPartition = "org";
    private const string SlugPartition = "orgslug";

    public async Task<Organization?> GetAsync(string organizationId, CancellationToken ct = default)
    {
        var row = await organizations.GetAsync(partitioner.PK(OrgPartition), organizationId, ct: ct).ConfigureAwait(false);
        return row is null ? null : Read(row);
    }

    public async Task<Organization?> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        var slugRow = await slugs.GetAsync(partitioner.PK(SlugPartition), slug, ct: ct).ConfigureAwait(false);
        if (slugRow is null) return null;

        var row = await organizations.GetAsync(partitioner.PK(OrgPartition), slugRow.GetStr("organizationId"), ct: ct)
            .ConfigureAwait(false);
        return row is null ? null : Read(row);
    }

    public async Task<IReadOnlyList<Organization>> ListAsync(CancellationToken ct = default)
    {
        var results = new List<Organization>();
        await foreach (var row in organizations.QueryPartitionAsync(partitioner.PK(OrgPartition), ct).ConfigureAwait(false))
            results.Add(Read(row));
        return results;
    }

    public async Task UpsertAsync(Organization organization, CancellationToken ct = default)
    {
        var orgPk = partitioner.PK(OrgPartition);
        var slugPk = partitioner.PK(SlugPartition);

        // Reject a slug already held by a DIFFERENT organization — two rows answering one slug would
        // make the `organization` authorize parameter ambiguous. Re-saving the same organization under
        // the same slug is not a clash.
        var clash = await slugs.GetAsync(slugPk, organization.Slug, ct: ct).ConfigureAwait(false);
        if (clash is not null && !string.Equals(clash.GetStr("organizationId"), organization.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Slug '{organization.Slug}' is already held by organization '{clash.GetStr("organizationId")}'.");
        }

        // Read the pre-existing row (if any) BEFORE overwriting it — it is the only place the previous
        // slug is recorded, and we need it to know whether a stale slug row must be cleaned up.
        var existing = await organizations.GetAsync(orgPk, organization.Id, ct: ct).ConfigureAwait(false);
        var staleSlug = existing is not null ? Read(existing).Slug : null;

        var orgRow = new SqlRow(orgPk, organization.Id)
        {
            Data = JsonSerializer.Serialize(organization, SqlJsonContext.Default.Organization),
        };
        await organizations.PutAsync(orgRow, ct).ConfigureAwait(false);
        if (tombstones is not null)
            await tombstones.WriteUpsertAsync("Organizations", orgPk, organization.Id, ct).ConfigureAwait(false);

        var slugRow = new SqlRow(slugPk, organization.Slug);
        slugRow.PutS("organizationId", organization.Id);
        await slugs.PutAsync(slugRow, ct).ConfigureAwait(false);

        if (tombstones is not null)
            await tombstones.WriteUpsertAsync("OrganizationSlugs", slugPk, organization.Slug, ct).ConfigureAwait(false);

        // The new slug row is written before the stale one is removed, so a crash between the two
        // leaves the organization reachable by its (new) slug rather than briefly unreachable by
        // either.
        if (staleSlug is not null && !string.Equals(staleSlug, organization.Slug, StringComparison.Ordinal))
        {
            var removed = await slugs.DeleteIfExistsReturningAsync(slugPk, staleSlug, ct).ConfigureAwait(false);
            if (removed is not null && tombstones is not null)
                await tombstones.WriteAsync("OrganizationSlugs", slugPk, staleSlug, ct).ConfigureAwait(false);
        }
    }

    public async Task DeleteAsync(string organizationId, CancellationToken ct = default)
    {
        // Memberships are NOT cascaded here — the caller owns that ordering (see the interface remarks).
        var orgPk = partitioner.PK(OrgPartition);
        var old = await organizations.DeleteIfExistsReturningAsync(orgPk, organizationId, ct).ConfigureAwait(false);
        if (old is null) return;

        if (tombstones is not null)
            await tombstones.WriteAsync("Organizations", orgPk, organizationId, ct).ConfigureAwait(false);

        var slugPk = partitioner.PK(SlugPartition);
        var slug = Read(old).Slug;
        var removedSlug = await slugs.DeleteIfExistsReturningAsync(slugPk, slug, ct).ConfigureAwait(false);
        if (removedSlug is not null && tombstones is not null)
            await tombstones.WriteAsync("OrganizationSlugs", slugPk, slug, ct).ConfigureAwait(false);
    }

    private static Organization Read(SqlRow row)
        => JsonSerializer.Deserialize(row.DataOrEmpty, SqlJsonContext.Default.Organization)!;
}
