using System.Net.Mail;
using System.Text.Json;

namespace Authagonal.Migration;

/// <summary>
/// Parses and validates one line of the NDJSON user-import format. Kept separate from
/// <see cref="NdjsonUserImportEngine"/> — and side-effect-free — so every rejection case is
/// unit-testable without a store: invalid JSON, a non-object line, an unknown field (unless
/// <c>AllowUnknownFields</c>), a missing/malformed email, a blank <c>passwordHash</c>, or a
/// non-ISO-8601 <c>createdAt</c>.
/// </summary>
public static class NdjsonUserImportReader
{
    /// <summary>The exact top-level JSON property names the schema recognises (see docs/migration.md).</summary>
    public static readonly IReadOnlySet<string> KnownFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "email", "username", "givenName", "familyName", "displayName", "emailVerified",
        "passwordHash", "roles", "organizationId", "attributes", "phoneNumber", "disabled",
        "createdAt", "externalId",
    };

    /// <summary>
    /// Parses and validates one already-trimmed, non-blank NDJSON line. Never throws — every failure
    /// mode is returned as a single human-readable reason via <see cref="NdjsonUserLineResult.Error"/>.
    /// </summary>
    public static NdjsonUserLineResult ParseLine(string line, bool allowUnknownFields)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            return NdjsonUserLineResult.Failure($"invalid JSON: {ex.Message}");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return NdjsonUserLineResult.Failure("line is not a JSON object");

            if (!allowUnknownFields)
            {
                List<string>? unknown = null;
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (!KnownFields.Contains(property.Name))
                        (unknown ??= []).Add(property.Name);
                }

                if (unknown is { Count: > 0 })
                    return NdjsonUserLineResult.Failure($"unknown field(s): {string.Join(", ", unknown)}");
            }

            NdjsonUserRecord? record;
            try
            {
                record = doc.RootElement.Deserialize(MigrationJsonContext.Default.NdjsonUserRecord);
            }
            catch (JsonException ex)
            {
                return NdjsonUserLineResult.Failure($"invalid field value: {ex.Message}");
            }

            if (record is null)
                return NdjsonUserLineResult.Failure("line is not a JSON object");

            if (string.IsNullOrWhiteSpace(record.Email))
                return NdjsonUserLineResult.Failure("email is required");

            if (!IsValidEmail(record.Email))
                return NdjsonUserLineResult.Failure("email is not a valid email address");

            if (record.PasswordHash is not null && record.PasswordHash.Trim().Length == 0)
                return NdjsonUserLineResult.Failure("passwordHash must not be empty when present");

            if (record.CreatedAt is not null && NdjsonUserMapper.ParseCreatedAt(record.CreatedAt) is null)
                return NdjsonUserLineResult.Failure("createdAt is not a valid ISO 8601 date");

            return NdjsonUserLineResult.Success(record);
        }
    }

    /// <summary>
    /// A pragmatic address check (via <see cref="MailAddress"/>), not a full RFC 5321 validator — good
    /// enough to reject the obviously-wrong lines a bad export or hand-authored file produces. Login is
    /// still gated on the real verification-email flow, so this is a data-quality check, not a security
    /// boundary.
    /// </summary>
    internal static bool IsValidEmail(string email)
    {
        var trimmed = email.Trim();
        if (trimmed.Length == 0) return false;
        try
        {
            return new MailAddress(trimmed).Address == trimmed;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>The outcome of <see cref="NdjsonUserImportReader.ParseLine"/> for one line: exactly one of
/// <see cref="Record"/> or <see cref="Error"/> is set.</summary>
public sealed class NdjsonUserLineResult
{
    public NdjsonUserRecord? Record { get; }
    public string? Error { get; }
    public bool IsSuccess => Error is null;

    private NdjsonUserLineResult(NdjsonUserRecord? record, string? error)
    {
        Record = record;
        Error = error;
    }

    public static NdjsonUserLineResult Success(NdjsonUserRecord record) => new(record, null);
    public static NdjsonUserLineResult Failure(string error) => new(null, error);
}
