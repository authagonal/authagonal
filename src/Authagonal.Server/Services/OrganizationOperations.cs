using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    IAuditLogger audit,
    ITxtResolver txtResolver,
    ILogger<OrganizationOperations> logger)
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

    // ── Email domains ──────────────────────────────────────────────────────────────────────────────
    //
    // A verified domain lets the library auto-join a user whose confirmed email sits under it
    // (OrganizationSelector, gated on AllowAutoMembership). The library only stores the claim; proving it
    // is this class's job, via a TXT record carrying a per-domain random token.

    /// <summary>
    /// Claims <paramref name="domain"/> for an organisation, unverified, minting the token its proof
    /// record must carry. One domain belongs to at most one organisation, verified or not: otherwise a
    /// user's email could qualify them for two organisations at once.
    /// </summary>
    public async Task<OperationResult<OrganizationDomainDto>> AddDomainAsync(
        string organizationId, string? domain, string actor, CancellationToken ct)
    {
        var org = await orgStore.GetAsync(organizationId, ct);
        if (org is null)
            return OperationResult<OrganizationDomainDto>.NotFound("organization_not_found");

        if (!TryNormalizeDomain(domain, out var normalized))
            return OperationResult<OrganizationDomainDto>.Invalid("domain_invalid",
                "Enter a bare domain such as acme.com: no scheme, path, port, wildcard or @.");

        if (org.Domains.Any(d => d.Domain == normalized))
            return OperationResult<OrganizationDomainDto>.Conflict("domain_exists",
                $"{normalized} is already on this organization.");

        if (await FindOtherHolderAsync(org.Id, normalized, ct) is { } holder)
            return OperationResult<OrganizationDomainDto>.Conflict("domain_taken",
                $"{normalized} is already claimed by the organization '{holder.Slug}'. Remove it there first.");

        var claim = new OrganizationDomain
        {
            Domain = normalized,
            // 256 bits, base64url: URL- and DNS-TXT-safe, and unguessable, so seeing one organisation's
            // record tells nobody another's.
            VerificationToken = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        org.Domains.Add(claim);
        org.UpdatedAt = DateTimeOffset.UtcNow;
        await orgStore.UpsertAsync(org, ct);

        await audit.LogAsync(actor, "organization.domain_added", "organization", org.Id, $"domain={normalized}", ct);

        return OperationResult<OrganizationDomainDto>.Ok(OrganizationDomainDto.From(claim));
    }

    /// <summary>
    /// Checks the domain's TXT record and, on a match, stamps <see cref="OrganizationDomain.VerifiedAt"/>.
    /// Idempotent once verified. A failed check is a conflict <c>verification_failed</c>, never a success
    /// carrying <c>verified: false</c>: a caller polling for success must not be able to mistake a miss
    /// for one.
    /// </summary>
    public async Task<OperationResult<OrganizationDomainDto>> VerifyDomainAsync(
        string organizationId, string domain, string actor, CancellationToken ct)
    {
        var org = await orgStore.GetAsync(organizationId, ct);
        if (org is null)
            return OperationResult<OrganizationDomainDto>.NotFound("organization_not_found");

        var normalized = OrganizationDomain.Normalize(domain);
        var claim = org.Domains.FirstOrDefault(d => d.Domain == normalized);
        if (claim is null)
            return OperationResult<OrganizationDomainDto>.NotFound("domain_not_found");

        if (claim.VerifiedAt is not null)
            return OperationResult<OrganizationDomainDto>.Ok(OrganizationDomainDto.From(claim));

        // AddDomainAsync refuses a domain another organisation holds, but that check is read-then-write;
        // re-asking here means a lost race can never end with two organisations VERIFIED on one domain.
        if (await FindOtherHolderAsync(org.Id, normalized, ct) is { } holder)
            return OperationResult<OrganizationDomainDto>.Conflict("domain_taken",
                $"{normalized} is also claimed by the organization '{holder.Slug}'. Remove one of the claims first.");

        var check = await CheckTxtRecordAsync(normalized, claim.VerificationToken, ct);
        if (!check.Verified)
            return OperationResult<OrganizationDomainDto>.Conflict("verification_failed", check.Message);

        // Re-read before writing: the DNS lookup can take seconds, and the row is replaced whole, so a
        // write from the copy read above would undo any edit that landed meanwhile.
        org = await orgStore.GetAsync(organizationId, ct);
        claim = org?.Domains.FirstOrDefault(d => d.Domain == normalized);
        if (org is null || claim is null)
            return OperationResult<OrganizationDomainDto>.NotFound(
                org is null ? "organization_not_found" : "domain_not_found");

        claim.VerifiedAt ??= DateTimeOffset.UtcNow;
        org.UpdatedAt = DateTimeOffset.UtcNow;
        await orgStore.UpsertAsync(org, ct);

        await audit.LogAsync(actor, "organization.domain_verified", "organization", org.Id, $"domain={normalized}", ct);

        return OperationResult<OrganizationDomainDto>.Ok(OrganizationDomainDto.From(claim));
    }

    /// <summary>Drops a domain claim, verified or not. Existing memberships are untouched: removing the
    /// domain stops future auto-joins, it does not evict anyone.</summary>
    public async Task<OperationResult<bool>> RemoveDomainAsync(
        string organizationId, string domain, string actor, CancellationToken ct)
    {
        var org = await orgStore.GetAsync(organizationId, ct);
        if (org is null)
            return OperationResult<bool>.NotFound("organization_not_found");

        var normalized = OrganizationDomain.Normalize(domain);
        if (org.Domains.RemoveAll(d => d.Domain == normalized) == 0)
            return OperationResult<bool>.NotFound("domain_not_found");

        org.UpdatedAt = DateTimeOffset.UtcNow;
        await orgStore.UpsertAsync(org, ct);

        await audit.LogAsync(actor, "organization.domain_removed", "organization", org.Id, $"domain={normalized}", ct);

        return OperationResult<bool>.Ok(true);
    }

    /// <summary>Another organisation holding <paramref name="domain"/>, verified or not. A full listing:
    /// domain claims are not indexed, and this runs only on an operator's add/verify.</summary>
    private async Task<Organization?> FindOtherHolderAsync(string organizationId, string domain, CancellationToken ct)
    {
        foreach (var other in await orgStore.ListAsync(ct))
            if (other.Id != organizationId && other.Domains.Any(d => d.Domain == domain))
                return other;
        return null;
    }

    /// <summary>
    /// A TXT record at <see cref="OrganizationDomainRecord.Name"/> whose value is exactly
    /// <see cref="OrganizationDomainRecord.Value"/>. The record carries a secret token: the binding is to
    /// one organisation among many, so a record naming only the tenant would let any organisation claim a
    /// domain another one proved. The failure message is actionable on its own, since it is the only
    /// place the exact record name and value are spelled out to the operator.
    /// </summary>
    private async Task<(bool Verified, string Message)> CheckTxtRecordAsync(
        string domain, string token, CancellationToken ct)
    {
        var name = OrganizationDomainRecord.Name(domain);
        var expected = OrganizationDomainRecord.Value(token);

        IReadOnlyList<string> found;
        try
        {
            found = await txtResolver.ResolveTxtAsync(name, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "TXT lookup failed for {Name}", name);
            return (false, $"DNS lookup failed for {name}. Add a TXT record at {name} with the value {expected}.");
        }

        // Exact, ordinal: the token is case-sensitive base64url, and a prefix or substring match would let
        // a record carrying some other value plus this one's prefix pass.
        if (found.Any(v => string.Equals(v.Trim(), expected, StringComparison.Ordinal)))
        {
            logger.LogInformation("Organisation domain {Domain} verified via {Record}", domain, name);
            return (true, $"Verified: {name} has the expected TXT value.");
        }

        var seen = found.Count == 0
            ? $"No TXT record was found at {name}."
            : $"Found at {name}: {string.Join(", ", found.Select(v => $"\"{v}\""))}.";
        return (false,
            $"Add a TXT record at {name} with the value {expected}. {seen} "
            + "DNS changes can take a few minutes to become visible.");
    }

    /// <summary>
    /// Normalises with <see cref="OrganizationDomain.Normalize"/>, then requires a plausible DNS name:
    /// at least two labels, each 1..63 of <c>[a-z0-9-]</c> not starting or ending with a hyphen, at most
    /// 253 characters overall, and a last label that is not all digits (so an IPv4 address is refused).
    /// Anything carrying a scheme, path, port, wildcard, <c>@</c> or whitespace fails the label rule.
    /// </summary>
    internal static bool TryNormalizeDomain(string? raw, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        normalized = OrganizationDomain.Normalize(raw);
        return normalized.Length <= 253
               && DomainShape().IsMatch(normalized)
               && !normalized[(normalized.LastIndexOf('.') + 1)..].All(char.IsAsciiDigit);
    }

    [GeneratedRegex("^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\\.)+[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex DomainShape();

    // ── Backfill ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Hard cap on how many users one <see cref="BackfillAsync"/> call scans, so a single HTTP request
    /// against a very large tenant cannot page an unbounded number of times before responding. A tenant
    /// larger than this needs more than one backfill run: <see cref="BackfillResultDto.Truncated"/> says
    /// so, rather than the request silently running long or timing out with nothing to show for it.
    /// </summary>
    public const int MaxUsersScanned = 50_000;

    /// <summary>
    /// Migrates the legacy free-text <see cref="AuthUser.OrganizationId"/> into real
    /// <see cref="Organization"/>/<see cref="OrganizationMembership"/> rows.
    /// </summary>
    /// <remarks>
    /// <see cref="IUserStore"/> has no index of DISTINCT legacy values: the reverse index is keyed BY an
    /// already-known organisation id, which is exactly the thing this backfill has not discovered yet. So
    /// this pages through users via <see cref="IUserStore.ListPageAsync"/> with a null organisation filter
    /// (the full-tenant scan), grouping by the exact legacy string, up to <see cref="MaxUsersScanned"/>.
    /// Only id and legacy value are kept in memory, not the whole decrypted user record.
    /// </remarks>
    public async Task<BackfillResultDto> BackfillAsync(bool dryRun, string actor, CancellationToken ct)
    {
        var usersByLegacyValue = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var scanned = 0;
        var truncated = false;
        string? cursor = null;
        do
        {
            var page = await userStore.ListPageAsync(null, 200, cursor, ct);
            foreach (var user in page.Users)
            {
                scanned++;
                if (string.IsNullOrWhiteSpace(user.OrganizationId)) continue;
                if (!usersByLegacyValue.TryGetValue(user.OrganizationId, out var ids))
                    usersByLegacyValue[user.OrganizationId] = ids = [];
                ids.Add(user.Id);
            }
            cursor = page.ContinuationToken;

            // A single backfill request must not scan an unbounded tenant unbounded: stop requesting
            // further pages once the bound is reached rather than paging to the end no matter how large
            // the population is. Only "past it": a scan that finishes exactly at or under the bound on
            // its last page is NOT truncated, so a tenant with exactly MaxUsersScanned users still gets a
            // complete, non-truncated result.
            if (scanned >= MaxUsersScanned && cursor is not null)
            {
                truncated = true;
                break;
            }
        } while (cursor is not null);

        var result = new BackfillResultDto { DryRun = dryRun, Truncated = truncated };
        var slugsClaimedThisRun = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (legacyValue, userIds) in usersByLegacyValue)
        {
            var baseSlug = DeriveSlug(legacyValue);
            var (slug, org, isNew) = await ResolveOrCreateOrganizationAsync(
                baseSlug, legacyValue, slugsClaimedThisRun, dryRun, ct);
            if (isNew) result.OrganizationsCreated++;

            foreach (var userId in userIds)
            {
                if (await membershipStore.GetAsync(org.Id, userId, ct) is not null)
                {
                    result.UsersSkipped++;
                    continue;
                }

                if (!dryRun)
                {
                    var now = DateTimeOffset.UtcNow;
                    await membershipStore.UpsertAsync(new OrganizationMembership
                    {
                        OrganizationId = org.Id,
                        UserId = userId,
                        Status = MembershipStatus.Active,
                        JoinedAt = now,
                        CreatedAt = now,
                    }, ct);
                }
                result.MembershipsCreated++;
            }

            if (result.Organizations.Count < 50)
                result.Organizations.Add(new BackfillOrganizationEntryDto
                {
                    LegacyValue = legacyValue,
                    Slug = slug,
                    Users = userIds.Count,
                });
        }

        if (!dryRun)
            await audit.LogAsync(actor, "organization.backfill", "organization", null,
                $"organizationsCreated={result.OrganizationsCreated} membershipsCreated={result.MembershipsCreated}", ct);

        return result;
    }

    /// <summary>
    /// The <see cref="Organization.Metadata"/> key the backfill stamps on every organisation it creates,
    /// carrying the raw legacy <see cref="AuthUser.OrganizationId"/> value it was created for.
    /// </summary>
    /// <remarks>
    /// Matching on this (see <see cref="MatchesLegacyValue"/>) rather than on <see
    /// cref="Organization.DisplayName"/> is what keeps a re-run idempotent after an admin renames the
    /// organisation: <c>DisplayName</c> is deliberately editable (<see cref="UpdateAsync"/>), so matching
    /// on it would make a rename look, to the next backfill run, like the legacy value migrating nowhere
    /// yet, creating a duplicate <c>-2</c> organisation and duplicate memberships for every user. The
    /// stamp is set once at creation and never touched by <see cref="UpdateAsync"/>.
    /// </remarks>
    private const string LegacyOrganizationIdMetadataKey = "legacyOrganizationId";

    /// <summary>
    /// True if <paramref name="org"/> is the organisation the backfill already created for
    /// <paramref name="legacyValue"/>. Prefers the <see cref="LegacyOrganizationIdMetadataKey"/> stamp
    /// (immune to a later rename); falls back to comparing <see cref="Organization.DisplayName"/> directly
    /// only for rows created before this stamp existed, which never got one.
    /// </summary>
    private static bool MatchesLegacyValue(Organization org, string legacyValue) =>
        org.Metadata.TryGetValue(LegacyOrganizationIdMetadataKey, out var stamped)
            ? stamped == legacyValue
            : org.DisplayName == legacyValue;

    /// <summary>
    /// Finds (or creates) the organisation for one distinct legacy value. A pre-existing organisation
    /// stamped (see <see cref="MatchesLegacyValue"/>) with this raw legacy value, or, for a pre-stamp row,
    /// whose <see cref="Organization.DisplayName"/> still equals it, is treated as "already migrated" and
    /// reused: this is what makes a re-run idempotent even after the organisation has been renamed. Any
    /// OTHER collision (a different organisation already holding that slug, in the store or claimed
    /// earlier in this same run by a different legacy value) is resolved by appending <c>-2</c>,
    /// <c>-3</c>, ... to the base slug.
    /// </summary>
    private async Task<(string Slug, Organization Org, bool IsNew)> ResolveOrCreateOrganizationAsync(
        string baseSlug, string legacyValue, Dictionary<string, string> slugsClaimedThisRun, bool dryRun,
        CancellationToken ct)
    {
        var candidate = baseSlug;
        for (var suffix = 1; suffix < 1000; suffix++)
        {
            if (!slugsClaimedThisRun.ContainsKey(candidate))
            {
                var existing = await orgStore.GetBySlugAsync(candidate, ct);
                if (existing is null)
                {
                    slugsClaimedThisRun[candidate] = legacyValue;
                    var org = new Organization
                    {
                        // Dry run never writes, so there is no real id to report: a placeholder makes
                        // that visible rather than implying a row exists. It is also, harmlessly, never a
                        // real membership's OrganizationId, so the per-user membership check below
                        // correctly answers "no" for every user under a not-yet-real organisation.
                        //
                        // org_-prefixed for the same reason CreateAsync's ids are: '_' is not slug-legal,
                        // so this id can never later collide with a slug the way a bare Guid "N" could.
                        Id = dryRun ? $"(dry-run:{candidate})" : "org_" + Guid.NewGuid().ToString("N"),
                        Slug = candidate,
                        DisplayName = legacyValue,
                        CreatedAt = DateTimeOffset.UtcNow,
                        Metadata = new Dictionary<string, string> { [LegacyOrganizationIdMetadataKey] = legacyValue },
                    };
                    if (!dryRun) await orgStore.UpsertAsync(org, ct);
                    return (candidate, org, true);
                }

                if (MatchesLegacyValue(existing, legacyValue))
                {
                    slugsClaimedThisRun[candidate] = legacyValue;
                    return (candidate, existing, false);
                }
            }

            var suffixText = $"-{suffix + 1}";
            var truncatedBase = baseSlug.Length + suffixText.Length > OrganizationSlug.MaxLength
                ? baseSlug[..(OrganizationSlug.MaxLength - suffixText.Length)].TrimEnd('-')
                : baseSlug;
            candidate = $"{truncatedBase}{suffixText}";
        }

        throw new InvalidOperationException(
            $"Could not find a free slug for legacy organization value '{legacyValue}' after 1000 attempts.");
    }

    /// <summary>Lowercases, collapses every run of non-<c>[a-z0-9]</c> characters to a single hyphen, trims
    /// leading/trailing hyphens, then truncates to <see cref="OrganizationSlug.MaxLength"/>: the shape
    /// <see cref="OrganizationSlug.IsValid"/> requires. Falls back to <c>"org"</c> for a legacy value with
    /// no alphanumeric character at all (empty after collapsing), which a slug has no shape for.</summary>
    private static string DeriveSlug(string legacyValue)
    {
        var collapsed = NonSlugCharacters().Replace(legacyValue.ToLowerInvariant(), "-").Trim('-');
        if (collapsed.Length > OrganizationSlug.MaxLength)
            collapsed = collapsed[..OrganizationSlug.MaxLength].TrimEnd('-');
        return collapsed.Length == 0 ? "org" : collapsed;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugCharacters();

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
