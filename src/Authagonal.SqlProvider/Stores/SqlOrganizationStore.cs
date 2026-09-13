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
/// <para>
/// The slug is immutable once an organization exists — <see cref="UpsertAsync"/> refuses to change
/// it, since it is the value a relying party hard-codes as the <c>organization</c> authorize
/// parameter, and changing what it resolves to under the same string is a silent redirect to a
/// different customer, not an update. Ids and slugs also share no namespace: a NEW organization is
/// refused when its slug reads as an existing organization's id, or its id reads as an existing
/// organization's slug — otherwise a bare string accepted as either would resolve to different
/// entities depending on which lookup read it. A brand-new slug is claimed with an insert-only
/// conditional write (<see cref="SqlTable.PutIfAbsentAsync"/>) before the organization document
/// itself is written, so two concurrent creates for the same new slug cannot both succeed. That
/// ordering opens one narrow, self-healing window: a crash between the slug insert and the
/// organization write leaves a slug row owned by an id with no organization row behind it yet, and
/// <see cref="UpsertAsync"/> recognises a retry of that same id/slug pair as exactly this case —
/// finishing the write — rather than rejecting it as held by another organization.
/// </para>
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
        // The rule lives once in Core so every store enforces the same shape.
        OrganizationSlug.Validate(organization.Slug);

        var orgPk = partitioner.PK(OrgPartition);
        var slugPk = partitioner.PK(SlugPartition);

        var existingRow = await organizations.GetAsync(orgPk, organization.Id, ct: ct).ConfigureAwait(false);
        if (existingRow is not null)
        {
            // The slug is immutable once set — see the class remarks. Everything else on the
            // document is freely mutable, so this is the only field upsert has to police.
            var existing = Read(existingRow);
            if (!string.Equals(existing.Slug, organization.Slug, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Organization '{organization.Id}' slug is immutable; cannot change '{existing.Slug}' to '{organization.Slug}'.");
            }

            var updatedRow = new SqlRow(orgPk, organization.Id)
            {
                Data = JsonSerializer.Serialize(organization, SqlJsonContext.Default.Organization),
            };
            await organizations.PutAsync(updatedRow, ct).ConfigureAwait(false);
            if (tombstones is not null)
                await tombstones.WriteUpsertAsync("Organizations", orgPk, organization.Id, ct).ConfigureAwait(false);
            return;
        }

        // New organization. Ids and slugs share no namespace: a slug that reads as another
        // organization's id, or an id that reads as another organization's slug, would make "which
        // one did the caller mean" ambiguous wherever a bare string is accepted as either.
        var idClash = await organizations.GetAsync(orgPk, organization.Slug, ct: ct).ConfigureAwait(false);
        if (idClash is not null)
            throw new InvalidOperationException($"Slug '{organization.Slug}' is already in use as another organization's id.");

        var slugClash = await slugs.GetAsync(slugPk, organization.Id, ct: ct).ConfigureAwait(false);
        if (slugClash is not null)
        {
            var owner = slugClash.GetStr("organizationId");
            throw new InvalidOperationException($"Id '{organization.Id}' is already held by organization '{owner}' as its slug.");
        }

        // Insert-only: exactly one concurrent creator of the same brand-new slug wins. This is the
        // real uniqueness guarantee — the read above (slugClash) is a fast, friendlier rejection for
        // the common non-racing case, not a substitute for it, since it and this insert are not one
        // atomic operation.
        var slugRow = new SqlRow(slugPk, organization.Slug);
        slugRow.PutS("organizationId", organization.Id);
        if (await slugs.PutIfAbsentAsync(slugRow, ct).ConfigureAwait(false))
        {
            if (tombstones is not null)
                await tombstones.WriteUpsertAsync("OrganizationSlugs", slugPk, organization.Slug, ct).ConfigureAwait(false);
        }
        else
        {
            // The slug row already exists. That is a genuine conflict UNLESS it is this exact id —
            // slug-first ordering means a crash between claiming the slug and writing the
            // organization document leaves precisely this row behind, and a retry of the same
            // create must self-heal and finish the write rather than read its own earlier claim
            // back as "held by another organization".
            var existingSlugRow = await slugs.GetAsync(slugPk, organization.Slug, ct: ct).ConfigureAwait(false);
            if (!string.Equals(existingSlugRow?.GetStr("organizationId"), organization.Id, StringComparison.Ordinal))
                throw new InvalidOperationException($"Slug '{organization.Slug}' is already held by another organization.");
        }

        var orgRow = new SqlRow(orgPk, organization.Id)
        {
            Data = JsonSerializer.Serialize(organization, SqlJsonContext.Default.Organization),
        };
        await organizations.PutAsync(orgRow, ct).ConfigureAwait(false);
        if (tombstones is not null)
            await tombstones.WriteUpsertAsync("Organizations", orgPk, organization.Id, ct).ConfigureAwait(false);
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
