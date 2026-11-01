using System.Text.Json;
using Authagonal.Core.Constants;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.Server.Services;

/// <summary>
/// Organisation and membership administration as operations rather than HTTP handlers, so the library's
/// admin endpoints and any host surface doing the same job (a portal route, a tool) run the same code
/// and write the same audit rows.
/// </summary>
/// <remarks>
/// Each method returns an <see cref="OperationResult{T}"/> carrying the outcome kind and a stable error
/// code, never an HTTP type; the caller maps the kind to its own status. Every successful write records
/// one row through <see cref="IAuditLogger"/>, attributed to the <c>actor</c> the caller names.
/// </remarks>
public sealed partial class OrganizationOperations(
    IOrganizationStore orgStore,
    IOrganizationMembershipStore membershipStore,
    IUserStore userStore,
    IAuditLogger audit)
{
    /// <summary>Page size when the caller names none.</summary>
    public const int DefaultPageSize = 50;

    private const string InvalidCursorMessage =
        "The cursor is not one this listing issued. Start again without a cursor, then pass each page's nextCursor unchanged.";

    /// <summary>
    /// One page of the organisations, in id order. <paramref name="cursor"/> is the <c>nextCursor</c> of the
    /// previous page (null for the first); the last page answers a null <c>nextCursor</c>. The store clamps
    /// <paramref name="limit"/> to 1..200.
    /// </summary>
    public async Task<OperationResult<ResultPage<OrganizationDto>>> ListPageAsync(
        string? cursor, int? limit, CancellationToken ct)
    {
        OrganizationPage page;
        try
        {
            page = await orgStore.ListPageAsync(NullIfEmpty(cursor), limit ?? DefaultPageSize, ct);
        }
        catch (ArgumentException)
        {
            // The store's documented signal for a cursor it did not mint (IOrganizationStore.ListPageAsync).
            return OperationResult<ResultPage<OrganizationDto>>.Invalid("invalid_cursor", InvalidCursorMessage);
        }

        return OperationResult<ResultPage<OrganizationDto>>.Ok(new ResultPage<OrganizationDto>
        {
            Items = page.Items.Select(OrganizationDto.From).ToList(),
            NextCursor = page.NextCursor,
        });
    }

    /// <summary>
    /// Creates an organisation. <paramref name="slug"/> must satisfy <see cref="OrganizationSlug.IsValid"/>
    /// and must not already be held by another organisation, as a slug or as an id. The policy and metadata
    /// parameters are each optional: null leaves the field at <see cref="Organization"/>'s own default.
    /// </summary>
    public async Task<OperationResult<OrganizationDto>> CreateAsync(
        string? slug, string? name, string? brandingJson, bool? enabled, bool? requireMembershipForTokens,
        bool? allowAutoMembership, Dictionary<string, string>? metadata, string actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return OperationResult<OrganizationDto>.Invalid("slug_required");
        if (!OrganizationSlug.IsValid(slug))
            return OperationResult<OrganizationDto>.Invalid("slug_invalid");
        if (string.IsNullOrWhiteSpace(name))
            return OperationResult<OrganizationDto>.Invalid("name_required");
        if (brandingJson is not null && !IsJsonObject(brandingJson))
            return OperationResult<OrganizationDto>.Invalid("invalid_branding_json");

        // Ids and slugs share one lookup namespace (GetAsync by id, then by slug): a slug equal to an
        // existing organisation's id collides just as a slug already held does, and rows whose id is a bare
        // Guid "N" (itself slug-shaped) make that a real case.
        if (await orgStore.GetBySlugAsync(slug, ct) is not null)
            return OperationResult<OrganizationDto>.Conflict("slug_taken");
        if (await orgStore.GetAsync(slug, ct) is not null)
            return OperationResult<OrganizationDto>.Conflict("slug_taken");

        var org = new Organization
        {
            // '_' is not slug-legal, so an org_-prefixed id can never collide with a slug.
            Id = "org_" + Guid.NewGuid().ToString("N"),
            Slug = slug,
            DisplayName = name,
            BrandingJson = brandingJson,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        if (enabled.HasValue) org.Enabled = enabled.Value;
        if (requireMembershipForTokens.HasValue) org.RequireMembershipForTokens = requireMembershipForTokens.Value;
        if (allowAutoMembership.HasValue) org.AllowAutoMembership = allowAutoMembership.Value;
        if (metadata is not null) org.Metadata = metadata;

        try
        {
            await orgStore.UpsertAsync(org, ct);
        }
        catch (InvalidOperationException)
        {
            // The checks above are read-then-write and cannot close a race: a concurrent create of the same
            // slug surfaces here as the store's own conflict guard, which every durable organisation store
            // raises as InvalidOperationException for a slug or id collision.
            return OperationResult<OrganizationDto>.Conflict("slug_taken");
        }

        await audit.LogAsync(actor, "organization.created", "organization", org.Id, org.Slug, ct);

        return OperationResult<OrganizationDto>.Ok(OrganizationDto.From(org));
    }

    /// <summary>Looks up an organisation by id first, then by slug.</summary>
    public async Task<OperationResult<OrganizationDto>> GetAsync(string idOrSlug, CancellationToken ct)
    {
        var org = await orgStore.GetAsync(idOrSlug, ct) ?? await orgStore.GetBySlugAsync(idOrSlug, ct);
        return org is null
            ? OperationResult<OrganizationDto>.NotFound("organization_not_found")
            : OperationResult<OrganizationDto>.Ok(OrganizationDto.From(org));
    }

    /// <summary>
    /// Updates the name, branding, policy flags and metadata. <paramref name="slug"/> is accepted only to
    /// refuse a rename: a slug is immutable once created, because a relying party hard-codes it. Every
    /// other parameter is optional and touches only its own field; a non-null but empty
    /// <paramref name="metadata"/> is a deliberate clear.
    /// </summary>
    public async Task<OperationResult<OrganizationDto>> UpdateAsync(
        string id, string? name, string? brandingJson, string? slug, bool? enabled,
        bool? requireMembershipForTokens, bool? allowAutoMembership, Dictionary<string, string>? metadata,
        string actor, CancellationToken ct)
    {
        var org = await orgStore.GetAsync(id, ct);
        if (org is null)
            return OperationResult<OrganizationDto>.NotFound("organization_not_found");

        if (!string.IsNullOrWhiteSpace(slug) && !string.Equals(slug, org.Slug, StringComparison.Ordinal))
            return OperationResult<OrganizationDto>.Invalid("slug_immutable");

        if (brandingJson is not null && !IsJsonObject(brandingJson))
            return OperationResult<OrganizationDto>.Invalid("invalid_branding_json");

        if (!string.IsNullOrWhiteSpace(name))
            org.DisplayName = name;
        if (brandingJson is not null)
            org.BrandingJson = brandingJson;
        if (enabled.HasValue)
            org.Enabled = enabled.Value;
        if (requireMembershipForTokens.HasValue)
            org.RequireMembershipForTokens = requireMembershipForTokens.Value;
        if (allowAutoMembership.HasValue)
            org.AllowAutoMembership = allowAutoMembership.Value;
        if (metadata is not null)
            org.Metadata = metadata;
        org.UpdatedAt = DateTimeOffset.UtcNow;

        await orgStore.UpsertAsync(org, ct);
        await audit.LogAsync(actor, "organization.updated", "organization", org.Id, org.Slug, ct);

        return OperationResult<OrganizationDto>.Ok(OrganizationDto.From(org));
    }

    /// <summary>
    /// Deletes an organisation and every membership pointing at it, memberships first: the store does not
    /// cascade, and a half-deleted organisation that still has members is recoverable where orphaned
    /// memberships are not.
    /// </summary>
    public async Task<OperationResult<bool>> DeleteAsync(string id, string actor, CancellationToken ct)
    {
        var org = await orgStore.GetAsync(id, ct);
        if (org is null)
            return OperationResult<bool>.NotFound("organization_not_found");

        foreach (var membership in await membershipStore.ListByOrganizationAsync(id, ct))
            await membershipStore.DeleteAsync(id, membership.UserId, ct);

        await orgStore.DeleteAsync(id, ct);
        await audit.LogAsync(actor, "organization.deleted", "organization", id, org.Slug, ct);

        return OperationResult<bool>.Ok(true);
    }

    /// <summary>
    /// One page of an organisation's memberships, in user-id order, with each member's email and name read
    /// from the user store (null when the user record no longer exists). Same cursor contract as
    /// <see cref="ListPageAsync"/>.
    /// </summary>
    public async Task<OperationResult<ResultPage<MembershipDto>>> ListMembersPageAsync(
        string organizationId, string? cursor, int? limit, CancellationToken ct)
    {
        if (await orgStore.GetAsync(organizationId, ct) is null)
            return OperationResult<ResultPage<MembershipDto>>.NotFound("organization_not_found");

        OrganizationMembershipPage page;
        try
        {
            page = await membershipStore.ListByOrganizationPageAsync(
                organizationId, NullIfEmpty(cursor), limit ?? DefaultPageSize, ct);
        }
        catch (ArgumentException)
        {
            return OperationResult<ResultPage<MembershipDto>>.Invalid("invalid_cursor", InvalidCursorMessage);
        }

        var dtos = new List<MembershipDto>(page.Items.Count);
        foreach (var membership in page.Items)
            dtos.Add(MembershipDto.From(membership, await userStore.GetAsync(membership.UserId, ct)));

        return OperationResult<ResultPage<MembershipDto>>.Ok(
            new ResultPage<MembershipDto> { Items = dtos, NextCursor = page.NextCursor });
    }

    /// <summary>
    /// Grants a membership, resolving the user by id (checked first) or by email. A role carrying a reserved
    /// prefix (<see cref="ReservedRolePrefixes"/>) is refused outright rather than silently stripped: this
    /// is an admin write, and a caller asking for tenant-level authority is told no, not quietly narrowed.
    /// </summary>
    public async Task<OperationResult<MembershipDto>> AddMemberAsync(
        string organizationId, string? userId, string? email, string? status, List<string>? roles,
        string actor, CancellationToken ct)
    {
        if (await orgStore.GetAsync(organizationId, ct) is null)
            return OperationResult<MembershipDto>.NotFound("organization_not_found");

        AuthUser? user = null;
        if (!string.IsNullOrWhiteSpace(userId))
            user = await userStore.GetAsync(userId, ct);
        else if (!string.IsNullOrWhiteSpace(email))
            user = await userStore.FindByEmailAsync(email, ct);

        if (user is null)
            return OperationResult<MembershipDto>.NotFound("user_not_found");

        if (await membershipStore.GetAsync(organizationId, user.Id, ct) is not null)
            return OperationResult<MembershipDto>.Conflict("membership_exists");

        var roleList = roles ?? [];
        if (roleList.Any(ReservedRolePrefixes.IsReserved))
            return OperationResult<MembershipDto>.Invalid("invalid_role");

        var resolvedStatus = string.IsNullOrWhiteSpace(status) ? MembershipStatus.Active : status;
        if (!IsValidStatus(resolvedStatus))
            return OperationResult<MembershipDto>.Invalid("invalid_status");

        var now = DateTimeOffset.UtcNow;
        var membership = new OrganizationMembership
        {
            OrganizationId = organizationId,
            UserId = user.Id,
            Roles = roleList,
            Status = resolvedStatus,
            JoinedAt = resolvedStatus == MembershipStatus.Active ? now : null,
            CreatedAt = now,
        };

        await membershipStore.UpsertAsync(membership, ct);
        await audit.LogAsync(actor, "organization.member.added", "organization", organizationId, $"user={user.Id}", ct);

        return OperationResult<MembershipDto>.Ok(MembershipDto.From(membership, user));
    }

    /// <summary>Updates a member's status and/or roles, with the same reserved-prefix refusal as
    /// <see cref="AddMemberAsync"/>. A move to <c>active</c> stamps <c>JoinedAt</c> if it was never set.</summary>
    public async Task<OperationResult<MembershipDto>> UpdateMemberAsync(
        string organizationId, string userId, string? status, List<string>? roles, string actor,
        CancellationToken ct)
    {
        var membership = await membershipStore.GetAsync(organizationId, userId, ct);
        if (membership is null)
            return OperationResult<MembershipDto>.NotFound("membership_not_found");

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!IsValidStatus(status))
                return OperationResult<MembershipDto>.Invalid("invalid_status");
            if (status == MembershipStatus.Active && membership.Status != MembershipStatus.Active)
                membership.JoinedAt ??= DateTimeOffset.UtcNow;
            membership.Status = status;
        }

        if (roles is not null)
        {
            if (roles.Any(ReservedRolePrefixes.IsReserved))
                return OperationResult<MembershipDto>.Invalid("invalid_role");
            membership.Roles = roles;
        }

        membership.UpdatedAt = DateTimeOffset.UtcNow;
        await membershipStore.UpsertAsync(membership, ct);
        await audit.LogAsync(actor, "organization.member.updated", "organization", organizationId, $"user={userId}", ct);

        return OperationResult<MembershipDto>.Ok(
            MembershipDto.From(membership, await userStore.GetAsync(userId, ct)));
    }

    /// <summary>Removes a membership outright (not a status change).</summary>
    public async Task<OperationResult<bool>> RemoveMemberAsync(
        string organizationId, string userId, string actor, CancellationToken ct)
    {
        if (await membershipStore.GetAsync(organizationId, userId, ct) is null)
            return OperationResult<bool>.NotFound("membership_not_found");

        await membershipStore.DeleteAsync(organizationId, userId, ct);
        await audit.LogAsync(actor, "organization.member.removed", "organization", organizationId, $"user={userId}", ct);

        return OperationResult<bool>.Ok(true);
    }

    /// <summary>
    /// Every organisation a user belongs to. A membership whose organisation no longer exists is skipped
    /// rather than surfaced with blank fields.
    /// </summary>
    public async Task<IReadOnlyList<UserOrganizationDto>> ListUserOrganizationsAsync(
        string userId, CancellationToken ct)
    {
        var results = new List<UserOrganizationDto>();
        foreach (var membership in await membershipStore.ListByUserAsync(userId, ct))
        {
            var org = await orgStore.GetAsync(membership.OrganizationId, ct);
            if (org is null) continue;
            results.Add(new UserOrganizationDto
            {
                OrganizationId = org.Id,
                Slug = org.Slug,
                Name = org.DisplayName,
                Status = membership.Status,
                Roles = membership.Roles,
            });
        }
        return results;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static bool IsValidStatus(string status) =>
        status is MembershipStatus.Invited or MembershipStatus.Active or MembershipStatus.Suspended;

    private static bool IsJsonObject(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
