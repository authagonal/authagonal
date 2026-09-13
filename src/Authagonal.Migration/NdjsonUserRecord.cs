namespace Authagonal.Migration;

/// <summary>
/// One line of the generic NDJSON user-import format documented in <c>docs/migration.md</c> ("NDJSON
/// user import"). Deserialized via the <see cref="MigrationJsonContext"/> source-generated context
/// (Web camelCase: <c>email</c>, <c>givenName</c>, <c>organizationId</c>, ...).
/// </summary>
/// <remarks>
/// Every property is optional at the type level — <see cref="NdjsonUserImportReader"/> is what enforces
/// which ones are actually required (only <see cref="Email"/>) and what "valid" means for the rest
/// (email format, ISO-8601 <see cref="CreatedAt"/>, non-empty <see cref="PasswordHash"/>), so every
/// rejection surfaces as one human-readable reason per line rather than a JSON deserialization
/// exception. <see cref="Username"/> and <see cref="DisplayName"/> have no dedicated column on
/// <c>AuthUser</c> — <see cref="NdjsonUserMapper"/> folds them into <c>CustomAttributes</c> under the
/// reserved keys <c>"username"</c> / <c>"displayName"</c>, the same bag <see cref="Attributes"/> lands
/// in (documented in <c>docs/migration.md</c>).
/// </remarks>
public sealed class NdjsonUserRecord
{
    public string? Email { get; set; }
    public string? Username { get; set; }
    public string? GivenName { get; set; }
    public string? FamilyName { get; set; }
    public string? DisplayName { get; set; }
    public bool? EmailVerified { get; set; }

    /// <summary>
    /// Stored verbatim as <see cref="Authagonal.Core.Models.AuthUser.PasswordHash"/>. Any legacy format
    /// <c>PasswordHasher.VerifyPassword</c> recognises (bcrypt <c>$2a$/$2b$/$2x$/$2y$</c>, ASP.NET
    /// Identity V3, scrypt <c>$s2$</c>) verifies unchanged on the user's first login and upgrades to
    /// native PBKDF2 from there — the same lazy-rehash path the Duende importer relies on. Not
    /// inspected here beyond non-empty; a malformed hash simply fails to verify at login, which is the
    /// same outcome a hand-authored bad hash would produce outside migration.
    /// </summary>
    public string? PasswordHash { get; set; }
    public List<string>? Roles { get; set; }
    public string? OrganizationId { get; set; }
    public Dictionary<string, string>? Attributes { get; set; }
    public string? PhoneNumber { get; set; }
    public bool? Disabled { get; set; }

    /// <summary>
    /// ISO 8601. Kept as a raw string rather than <c>DateTimeOffset?</c> so a malformed value reports
    /// "createdAt is not a valid ISO 8601 date" through <see cref="NdjsonUserImportReader"/> instead of
    /// an opaque JSON deserialization exception.
    /// </summary>
    public string? CreatedAt { get; set; }

    /// <summary>
    /// Stored on <see cref="Authagonal.Core.Models.AuthUser.ExternalId"/> — the same field the Duende
    /// importer occupies with the source database's user id (<c>AspNetUsers.Id</c>).
    /// </summary>
    public string? ExternalId { get; set; }
}
