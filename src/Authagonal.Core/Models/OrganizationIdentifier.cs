using System.Text.RegularExpressions;

namespace Authagonal.Core.Models;

/// <summary>
/// The shape a value naming an organisation must have — an <see cref="Organization.Id"/>, an entry in
/// <see cref="OAuthClient.RestrictedToOrganizationIds"/>, or the <c>organization</c> authorize
/// parameter.
/// </summary>
/// <remarks>
/// One definition because the three have to agree. An id that cannot be sent as the
/// <c>organization</c> parameter is an id no request can ever select, and a restriction listing one
/// silently matches nothing — which refuses every request, so a typo becomes a lockout. The charset is
/// the RFC 3986 unreserved set, which every one of those values travels through a URL query as.
/// <para>
/// Wider than <see cref="OrganizationSlug"/> on purpose: a slug is a public, human-chosen name and is
/// held to a stricter shape, while an id is opaque and only has to be unambiguous and transportable.
/// The extra characters an id may use (uppercase, <c>.</c>, <c>_</c>, <c>~</c>) are exactly the ones a
/// slug may not, which is what lets an id be minted so it can never collide with the slug namespace.
/// </para>
/// </remarks>
public static partial class OrganizationIdentifier
{
    /// <summary>Maximum length, inclusive.</summary>
    public const int MaxLength = 200;

    /// <summary><c>^[A-Za-z0-9._~-]{1,200}$</c> — the RFC 3986 unreserved set.</summary>
    [GeneratedRegex("^[A-Za-z0-9._~-]{1,200}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    /// <summary>True when <paramref name="value"/> is a well-formed organisation identifier.</summary>
    public static bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value) && Pattern().IsMatch(value);

    /// <summary>
    /// True when <paramref name="id"/> could also be a legal <see cref="OrganizationSlug"/>, and so
    /// could one day collide with one.
    /// </summary>
    /// <remarks>
    /// Advisory, not enforced: stores refuse an actual collision, so a colliding id is caught at the
    /// moment it would matter rather than forbidden up front. Minting ids that answer false here — by
    /// including an uppercase letter, a <c>.</c>, a <c>_</c> or a <c>~</c> — means the two namespaces
    /// can never meet, and the <c>organization</c> parameter can resolve a mixed-case id by id alone.
    /// </remarks>
    public static bool CouldCollideWithASlug(string? id) => OrganizationSlug.IsValid(id);
}
