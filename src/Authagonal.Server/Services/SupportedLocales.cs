using System.Globalization;

namespace Authagonal.Server.Services;

/// <summary>
/// The UI languages the server ships resources for. One list, used by request localization and by anything
/// that must turn a stored or browser-supplied tag into one of them, so the two cannot drift.
/// </summary>
/// <remarks>
/// Mirrors the login app's <c>LANGUAGES</c> registry (<c>login-app/src/i18n/index.ts</c>).
/// </remarks>
public static class SupportedLocales
{
    public static readonly string[] All = ["en", "zh-Hans", "de", "fr", "es", "vi", "pt", "ja", "ar", "af", "hi"];

    /// <summary>
    /// Resolves any tag (a stored <c>AuthUser.Locale</c>, a browser language, a region variant) to a
    /// supported culture, or null when none matches. The rules are the login app's <c>toLangOption</c>: an
    /// exact match, any <c>zh*</c> tag to <c>zh-Hans</c>, then the base language ("de-DE" to "de").
    /// </summary>
    public static CultureInfo? Resolve(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();

        var match = All.FirstOrDefault(c => c.Equals(tag, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            var language = tag.Split('-', '_')[0];
            match = language.Equals("zh", StringComparison.OrdinalIgnoreCase)
                ? "zh-Hans"
                : All.FirstOrDefault(c => c.Equals(language, StringComparison.OrdinalIgnoreCase));
        }

        if (match is null) return null;
        try { return CultureInfo.GetCultureInfo(match); }
        catch (CultureNotFoundException) { return null; }
    }
}
