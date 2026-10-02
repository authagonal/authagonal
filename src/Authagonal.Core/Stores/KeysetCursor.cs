using System.Buffers.Text;
using System.Text;

namespace Authagonal.Core.Stores;

/// <summary>
/// The opaque keyset cursor the paged organisation listings hand out, and the slice every provider
/// without a server-side range query uses. Shared so a cursor minted by one implementation decodes in
/// another and the clamp is identical everywhere.
/// </summary>
/// <remarks>
/// A cursor names the LAST key returned, and the next page is everything strictly after it. Keyset
/// rather than offset on purpose: a row inserted or deleted between two page reads shifts every offset
/// after it, so an offset cursor silently skips or repeats rows, while "after key K" stays correct.
/// </remarks>
public static class KeysetCursor
{
    /// <summary>The smallest page a caller can ask for.</summary>
    public const int MinLimit = 1;

    /// <summary>The largest page a caller can ask for. Larger requests are clamped, not refused.</summary>
    public const int MaxLimit = 200;

    // Versioned so a future cursor shape can be told apart, and so an arbitrary base64url string a
    // caller made up is refused rather than read as a key.
    private const string Prefix = "k1:";

    /// <summary>Clamp a requested page size into [<see cref="MinLimit"/>, <see cref="MaxLimit"/>].</summary>
    public static int ClampLimit(int limit) => Math.Clamp(limit, MinLimit, MaxLimit);

    /// <summary>Encode the last key of a page as a cursor.</summary>
    public static string Encode(string lastKey) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(Prefix + lastKey));

    /// <summary>
    /// The key a cursor names, or null for a null/empty cursor (the first page).
    /// </summary>
    /// <exception cref="ArgumentException">The cursor is not one this library issued.</exception>
    public static string? Decode(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor)) return null;

        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(Base64Url.DecodeFromChars(cursor));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new ArgumentException("The paging cursor is malformed.", nameof(cursor), ex);
        }

        if (!text.StartsWith(Prefix, StringComparison.Ordinal) || text.Length == Prefix.Length)
            throw new ArgumentException("The paging cursor is malformed.", nameof(cursor));

        return text[Prefix.Length..];
    }

    /// <summary>
    /// Keyset-slice an in-memory listing: sort by key (ordinal), keep keys strictly after the cursor's,
    /// take <paramref name="limit"/> (clamped). <c>nextCursor</c> is null when nothing follows.
    /// </summary>
    /// <exception cref="ArgumentException">The cursor is malformed.</exception>
    public static (IReadOnlyList<T> Items, string? NextCursor) Slice<T>(
        IEnumerable<T> source, Func<T, string> key, string? cursor, int limit)
    {
        var after = Decode(cursor);
        var take = ClampLimit(limit);

        var window = source
            .Where(x => after is null || string.CompareOrdinal(key(x), after) > 0)
            .OrderBy(key, StringComparer.Ordinal)
            .Take(take + 1)
            .ToList();

        return Page(window, key, take);
    }

    /// <summary>
    /// Finish a page from up to <c>take + 1</c> rows already in key order: the extra row only proves a
    /// next page exists and is not returned.
    /// </summary>
    public static (IReadOnlyList<T> Items, string? NextCursor) Page<T>(List<T> window, Func<T, string> key, int take)
    {
        if (window.Count <= take) return (window, null);
        window.RemoveRange(take, window.Count - take);
        return (window, Encode(key(window[^1])));
    }
}
