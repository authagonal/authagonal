using System.Globalization;
using System.Security.Cryptography;
using Authagonal.Core.Models;

namespace Authagonal.Migration;

/// <summary>
/// Pure, side-effect-free mapping from a validated <see cref="NdjsonUserRecord"/> onto
/// <see cref="AuthUser"/> — the counterpart of <see cref="DuendeMappings"/>. Kept separate and
/// internal so field mapping is unit-testable without a store.
/// </summary>
internal static class NdjsonUserMapper
{
    /// <summary>Builds a brand-new <see cref="AuthUser"/> (new id, fresh security stamp) for a line
    /// with no existing match.</summary>
    public static AuthUser ToNewAuthUser(NdjsonUserRecord record, DateTimeOffset now)
    {
        var email = record.Email!.Trim();
        var user = new AuthUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            SecurityStamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            CreatedAt = ParseCreatedAt(record.CreatedAt) ?? now,
        };
        ApplyFields(user, record);
        return user;
    }

    /// <summary>
    /// Merges a line's present fields onto an existing user (<c>--on-duplicate update</c>). Only fields
    /// the line actually sets are overwritten — an absent optional field leaves the existing value
    /// alone, so a re-import that only carries <c>displayName</c> cannot blank out an unrelated field.
    /// <see cref="AuthUser.Id"/>, <see cref="AuthUser.SecurityStamp"/>, <see cref="AuthUser.CreatedAt"/>
    /// and the concurrency token are never touched.
    /// </summary>
    public static void ApplyToExisting(AuthUser user, NdjsonUserRecord record, DateTimeOffset now)
    {
        ApplyFields(user, record);
        user.UpdatedAt = now;
    }

    private static void ApplyFields(AuthUser user, NdjsonUserRecord record)
    {
        var email = record.Email!.Trim();
        user.Email = email;
        user.NormalizedEmail = email.ToUpperInvariant();

        // PasswordHash: NdjsonUserImportReader already rejects a present-but-blank value, so any
        // non-null value here is safe to store verbatim.
        if (record.PasswordHash is not null) user.PasswordHash = record.PasswordHash;
        if (record.EmailVerified.HasValue) user.EmailConfirmed = record.EmailVerified.Value;
        if (record.GivenName is not null) user.FirstName = record.GivenName;
        if (record.FamilyName is not null) user.LastName = record.FamilyName;
        if (record.PhoneNumber is not null) user.Phone = record.PhoneNumber;
        if (record.OrganizationId is not null) user.OrganizationId = record.OrganizationId;
        if (record.ExternalId is not null) user.ExternalId = record.ExternalId;
        if (record.Disabled.HasValue) user.IsActive = !record.Disabled.Value;
        if (record.Roles is not null) user.Roles = [..record.Roles];

        // Attributes, then the two reserved keys — so username/displayName always win over a
        // same-named key inside the caller-supplied attributes bag, rather than depending on
        // Dictionary enumeration order.
        if (record.Attributes is not null)
            foreach (var (key, value) in record.Attributes)
                user.CustomAttributes[key] = value;
        if (record.Username is not null) user.CustomAttributes["username"] = record.Username;
        if (record.DisplayName is not null) user.CustomAttributes["displayName"] = record.DisplayName;
    }

    /// <summary>Parses an ISO-8601 <paramref name="value"/>, or null if absent/unparsable. Shared by the
    /// reader's validation and the mapper's actual value so the two never disagree.</summary>
    internal static DateTimeOffset? ParseCreatedAt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }
}
