using System.Text.RegularExpressions;

namespace Authagonal.Core.Models;

/// <summary>
/// The shape a tenant-unique <see cref="Organization.Slug"/> must have, enforced by every store.
/// </summary>
/// <remarks>
/// A slug is a public identifier: it is emitted as the <c>org_slug</c> claim, accepted by the
/// <c>organization</c> authorize parameter, and a relying party will compare it against the customer
/// instance it is serving. So it has to be unambiguous under every transport that carries it — a URL
/// query parameter, a JSON claim, a table row key — and comparable by ordinal equality with no
/// normalisation step a caller might skip.
/// <para>
/// Lowercase only, because the authorize parameter is lowercased before the slug lookup: allowing
/// <c>Acme</c> as a stored slug would create a value no request could ever resolve. No leading or
/// trailing hyphen, so a slug cannot be visually confused with a flag or a fragment. 64 characters is
/// generous for a customer name and short enough to stay inside every key length this is stored under.
/// </para>
/// </remarks>
public static partial class OrganizationSlug
{
    /// <summary>Maximum length, inclusive.</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// <c>^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$</c> — lowercase alphanumerics and interior hyphens,
    /// 1 to 64 characters, no leading or trailing hyphen.
    /// </summary>
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    /// <summary>True when <paramref name="slug"/> is a well-formed organisation slug.</summary>
    public static bool IsValid(string? slug) =>
        !string.IsNullOrEmpty(slug) && Pattern().IsMatch(slug);

    /// <summary>
    /// Throws when <paramref name="slug"/> is malformed. Called by every store's upsert, so a row that
    /// exists is a row a request can resolve.
    /// </summary>
    /// <exception cref="ArgumentException">The slug does not match <see cref="Pattern"/>.</exception>
    public static void Validate(string? slug)
    {
        if (IsValid(slug)) return;

        throw new ArgumentException(
            $"Organization slug '{slug}' is not valid. A slug is 1-{MaxLength} characters of lowercase "
            + "letters, digits and interior hyphens, and may not start or end with a hyphen.",
            nameof(slug));
    }
}
