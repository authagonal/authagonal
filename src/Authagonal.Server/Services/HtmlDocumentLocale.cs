using System.Globalization;
using System.Text.Encodings.Web;

namespace Authagonal.Server.Services;

/// <summary>
/// The <c>lang</c> and <c>dir</c> attributes for the small HTML pages the server renders itself (sign-out
/// interstitials, the email-confirmation page), taken from the culture <c>UseRequestLocalization</c>
/// resolved for the request. Those pages show localized text, so they must also say which language it is
/// and which way it runs, or a right-to-left translation renders with left-to-right layout.
/// </summary>
public static class HtmlDocumentLocale
{
    // <c>TextInfo.IsRightToLeft</c> reads ICU data, and a host running with invariant globalization has
    // none: every culture then reports left-to-right, which would silently put Arabic on an LTR page. The
    // direction of a script does not depend on the data set, so these language subtags are the backstop.
    private static readonly HashSet<string> RightToLeftLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "ar", "he", "fa", "ur", "ps", "sd", "ug", "yi", "dv", "ku", "ckb",
    };

    /// <summary>True when the culture is written right to left.</summary>
    public static bool IsRightToLeft(CultureInfo culture) =>
        culture.TextInfo.IsRightToLeft || RightToLeftLanguages.Contains(culture.TwoLetterISOLanguageName)
        || RightToLeftLanguages.Contains(culture.Name.Split('-')[0]);

    /// <summary>The <c>dir</c> attribute value for the culture: <c>rtl</c> or <c>ltr</c>.</summary>
    public static string Direction(CultureInfo culture) => IsRightToLeft(culture) ? "rtl" : "ltr";

    /// <summary>
    /// The attributes for an <c>&lt;html&gt;</c> element, e.g. <c>lang="ar" dir="rtl"</c> (no leading or
    /// trailing space), for the current request's UI culture. The language tag is HTML-encoded.
    /// </summary>
    public static string HtmlAttributes() => HtmlAttributes(CultureInfo.CurrentUICulture);

    /// <inheritdoc cref="HtmlAttributes()"/>
    public static string HtmlAttributes(CultureInfo culture)
    {
        var lang = string.IsNullOrEmpty(culture.Name) ? "en" : culture.Name;
        return $"lang=\"{HtmlEncoder.Default.Encode(lang)}\" dir=\"{Direction(culture)}\"";
    }
}
