using Authagonal.Core.Models;

namespace Authagonal.Server.Services;

/// <summary>
/// An <see cref="Organization"/> as the organisation operations return it: every public field of the
/// model, with <see cref="Organization.DisplayName"/> carried as <see cref="Name"/>.
/// </summary>
public sealed class OrganizationDto
{
    /// <summary>See <see cref="Organization.Id"/>.</summary>
    public string Id { get; set; } = "";

    /// <summary>See <see cref="Organization.Slug"/>.</summary>
    public string Slug { get; set; } = "";

    /// <summary>See <see cref="Organization.DisplayName"/>.</summary>
    public string Name { get; set; } = "";

    /// <summary>See <see cref="Organization.BrandingJson"/>.</summary>
    public string? BrandingJson { get; set; }

    /// <summary>See <see cref="Organization.Metadata"/>.</summary>
    public Dictionary<string, string> Metadata { get; set; } = [];

    /// <summary>See <see cref="Organization.Enabled"/>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>See <see cref="Organization.RequireMembershipForTokens"/>.</summary>
    public bool RequireMembershipForTokens { get; set; } = true;

    /// <summary>See <see cref="Organization.AllowAutoMembership"/>.</summary>
    public bool AllowAutoMembership { get; set; }

    /// <summary>See <see cref="Organization.CreatedAt"/>.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>See <see cref="Organization.UpdatedAt"/>.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>See <see cref="Organization.Domains"/>. Read-only through this DTO: domains change only
    /// through the domain operations, never through an organisation update.</summary>
    public List<OrganizationDomainDto> Domains { get; set; } = [];

    /// <summary>The DTO for <paramref name="org"/>.</summary>
    public static OrganizationDto From(Organization org) => new()
    {
        Id = org.Id,
        Slug = org.Slug,
        Name = org.DisplayName,
        BrandingJson = org.BrandingJson,
        Metadata = org.Metadata,
        Enabled = org.Enabled,
        RequireMembershipForTokens = org.RequireMembershipForTokens,
        AllowAutoMembership = org.AllowAutoMembership,
        CreatedAt = org.CreatedAt,
        UpdatedAt = org.UpdatedAt,
        Domains = org.Domains.Select(OrganizationDomainDto.From).ToList(),
    };
}

/// <summary>
/// An <see cref="OrganizationDomain"/> claim, with the DNS TXT record that proves it. The verification
/// token is exposed only inside <see cref="RecordValue"/>.
/// </summary>
public sealed class OrganizationDomainDto
{
    /// <summary>See <see cref="OrganizationDomain.Domain"/> (normalised).</summary>
    public string Domain { get; set; } = "";

    /// <summary>True once <see cref="VerifiedAt"/> is set.</summary>
    public bool Verified { get; set; }

    /// <summary>See <see cref="OrganizationDomain.VerifiedAt"/>.</summary>
    public DateTimeOffset? VerifiedAt { get; set; }

    /// <summary>See <see cref="OrganizationDomain.CreatedAt"/>.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The name of the TXT record to publish (<c>_authagonal-org.{domain}</c>).</summary>
    public string RecordName { get; set; } = "";

    /// <summary>The exact TXT value to publish (<c>authagonal-org-verify={token}</c>).</summary>
    public string RecordValue { get; set; } = "";

    /// <summary>The DTO for <paramref name="d"/>.</summary>
    public static OrganizationDomainDto From(OrganizationDomain d) => new()
    {
        Domain = d.Domain,
        Verified = d.VerifiedAt is not null,
        VerifiedAt = d.VerifiedAt,
        CreatedAt = d.CreatedAt,
        RecordName = OrganizationDomainRecord.Name(d.Domain),
        RecordValue = OrganizationDomainRecord.Value(d.VerificationToken),
    };
}

/// <summary>
/// The DNS TXT record that proves an organisation holds an email domain: published at
/// <c>_authagonal-org.{domain}</c> with the value <c>authagonal-org-verify={token}</c>.
/// </summary>
public static class OrganizationDomainRecord
{
    /// <summary>The label the record is published under, in front of the domain.</summary>
    public const string NamePrefix = "_authagonal-org";

    /// <summary>What the record's value starts with, in front of the claim's token.</summary>
    public const string ValuePrefix = "authagonal-org-verify=";

    /// <summary>The record name for <paramref name="domain"/>.</summary>
    public static string Name(string domain) => $"{NamePrefix}.{domain}";

    /// <summary>The record value for <paramref name="token"/>.</summary>
    public static string Value(string token) => $"{ValuePrefix}{token}";
}

/// <summary>
/// An <see cref="OrganizationMembership"/> as the membership operations return it, with the member's
/// current email and name read from the user store rather than stored on the membership row, which only
/// ever holds the user id.
/// </summary>
public sealed class MembershipDto
{
    /// <summary>See <see cref="OrganizationMembership.OrganizationId"/>.</summary>
    public string OrganizationId { get; set; } = "";

    /// <summary>See <see cref="OrganizationMembership.UserId"/>.</summary>
    public string UserId { get; set; } = "";

    /// <summary>The member's current email. Null when the user record no longer exists.</summary>
    public string? Email { get; set; }

    /// <summary>The member's current <c>"{FirstName} {LastName}"</c>, trimmed. Null when the user record no
    /// longer exists.</summary>
    public string? Name { get; set; }

    /// <summary>See <see cref="OrganizationMembership.Status"/> and <see cref="MembershipStatus"/>.</summary>
    public string Status { get; set; } = "";

    /// <summary>See <see cref="OrganizationMembership.Roles"/>.</summary>
    public List<string> Roles { get; set; } = [];

    /// <summary>See <see cref="OrganizationMembership.CreatedAt"/>.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>See <see cref="OrganizationMembership.UpdatedAt"/>.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>The DTO for <paramref name="membership"/>, with <paramref name="user"/>'s email and name
    /// when the user still exists.</summary>
    public static MembershipDto From(OrganizationMembership membership, AuthUser? user) => new()
    {
        OrganizationId = membership.OrganizationId,
        UserId = membership.UserId,
        Email = user?.Email,
        Name = user is null ? null : $"{user.FirstName} {user.LastName}".Trim(),
        Status = membership.Status,
        Roles = membership.Roles,
        CreatedAt = membership.CreatedAt,
        UpdatedAt = membership.UpdatedAt,
    };
}

/// <summary>One organisation a user belongs to, with that user's status and roles in it.</summary>
public sealed class UserOrganizationDto
{
    /// <summary>See <see cref="Organization.Id"/>.</summary>
    public string OrganizationId { get; set; } = "";

    /// <summary>See <see cref="Organization.Slug"/>.</summary>
    public string Slug { get; set; } = "";

    /// <summary>See <see cref="Organization.DisplayName"/>.</summary>
    public string Name { get; set; } = "";

    /// <summary>See <see cref="OrganizationMembership.Status"/>.</summary>
    public string Status { get; set; } = "";

    /// <summary>See <see cref="OrganizationMembership.Roles"/>: this user's roles within this organisation.</summary>
    public List<string> Roles { get; set; } = [];
}

/// <summary>Result of <see cref="OrganizationOperations.BackfillAsync"/>.</summary>
public sealed class BackfillResultDto
{
    /// <summary>True when nothing was written: the counts below describe what WOULD happen.</summary>
    public bool DryRun { get; set; }

    /// <summary>Count of new <see cref="Organization"/> rows (created, or that would be created on a dry
    /// run) across every distinct legacy value.</summary>
    public int OrganizationsCreated { get; set; }

    /// <summary>Count of new <see cref="OrganizationMembership"/> rows (created, or that would be created
    /// on a dry run) across every affected user.</summary>
    public int MembershipsCreated { get; set; }

    /// <summary>Count of users left untouched because they already held a membership of the resolved
    /// organisation: a re-run is idempotent, these are not counted again.</summary>
    public int UsersSkipped { get; set; }

    /// <summary>The first 50 distinct legacy values processed, and what each resolved to.</summary>
    public List<BackfillOrganizationEntryDto> Organizations { get; set; } = [];

    /// <summary>True when the user scan stopped at <see cref="OrganizationOperations.MaxUsersScanned"/>
    /// before reaching the end of the tenant's user population: the counts above describe only the users
    /// scanned so far, not the whole tenant. A caller that sees this should run the backfill again; the
    /// already-migrated users this run did cover are skipped on the re-run, so re-running is cheap and
    /// safe, not merely idempotent by accident.</summary>
    public bool Truncated { get; set; }
}

/// <summary>One distinct legacy <see cref="AuthUser.OrganizationId"/> value and what it resolved to.</summary>
public sealed class BackfillOrganizationEntryDto
{
    /// <summary>The raw legacy <see cref="AuthUser.OrganizationId"/> string, unmodified.</summary>
    public string LegacyValue { get; set; } = "";

    /// <summary>The <see cref="Organization.Slug"/> it resolved (or would resolve) to.</summary>
    public string Slug { get; set; } = "";

    /// <summary>How many users carry this legacy value: the size of the migration bucket, not merely
    /// the count newly given a membership (some may already have one from an earlier run).</summary>
    public int Users { get; set; }
}
