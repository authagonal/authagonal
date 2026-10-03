using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// The HTML pages the server renders itself (email confirmation, sign-out interstitials) carry localized
/// text, so they must say which language it is and which way it runs. These drive the real pipeline, so
/// they also prove the request culture actually reaches those routes.
/// </summary>
public sealed class ServerRenderedPageLocaleTests : IAsyncLifetime
{
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    // A forged but well-formed token: the page renders it exactly as a valid unconfirmed one (no store
    // lookup can match), which is what lets this exercise the "prompt" page without a real mail.
    private static string Token() => Convert.ToBase64String(Encoding.UTF8.GetBytes(
        $"stamp||nobody@example.com||{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}"));

    private async Task<string> GetAsync(string url, string? acceptLanguage)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (acceptLanguage is not null) request.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The pages HTML-encode non-ASCII text as numeric entities (the default encoder's safe list is
        // Basic Latin); a browser renders them identically, so assert on the decoded text.
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ConfirmEmailPage_InArabic_IsRightToLeftAndArabic()
    {
        var html = await GetAsync($"/api/auth/confirm-email?token={Uri.EscapeDataString(Token())}", "ar");

        Assert.Contains("<html lang=\"ar\" dir=\"rtl\">", html, StringComparison.Ordinal);
        Assert.Contains("أكّد بريدك الإلكتروني", html, StringComparison.Ordinal);
        Assert.Contains("تأكيد بريدي الإلكتروني", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Confirm my email", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmEmailPage_InEnglish_IsLeftToRight()
    {
        var html = await GetAsync($"/api/auth/confirm-email?token={Uri.EscapeDataString(Token())}", "en");

        Assert.Contains("<html lang=\"en\" dir=\"ltr\">", html, StringComparison.Ordinal);
        Assert.Contains("Confirm my email", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmEmailPage_WithNoLanguageSignal_FallsBackToEnglishLtr()
    {
        var html = await GetAsync("/api/auth/confirm-email", null);

        Assert.Contains("<html lang=\"en\" dir=\"ltr\">", html, StringComparison.Ordinal);
        Assert.Contains("Something is missing", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmEmailPage_ARegionalArabicTagResolvesToTheArabicResource()
    {
        // What a real browser sends: a region variant, with fallbacks and q-values.
        var html = await GetAsync("/api/auth/confirm-email", "ar-SA,ar;q=0.9,en;q=0.5");

        Assert.Contains("dir=\"rtl\"", html, StringComparison.Ordinal);
        Assert.Contains("هناك معلومة ناقصة", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmEmailPage_TheQueryStringProviderOutranksAcceptLanguage()
    {
        var html = await GetAsync("/api/auth/confirm-email?culture=ar&ui-culture=ar", "en");

        Assert.Contains("<html lang=\"ar\" dir=\"rtl\">", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("de", "de", "Es fehlt etwas")]
    [InlineData("ja", "ja", "情報が不足しています")]
    [InlineData("hi", "hi", "कुछ जानकारी अधूरी है")]
    [InlineData("zh-Hans", "zh-Hans", "缺少信息")]
    public async Task ConfirmEmailPage_LeftToRightLocalesStayLtrWithTheirOwnText(string accept, string lang, string text)
    {
        var html = await GetAsync("/api/auth/confirm-email", accept);

        Assert.Contains($"<html lang=\"{lang}\" dir=\"ltr\">", html, StringComparison.Ordinal);
        Assert.Contains(text, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignOutConfirmation_FollowsTheRequestCulture()
    {
        await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        var arabic = await GetAsync("/connect/endsession", "ar");
        Assert.Contains("<html lang=\"ar\" dir=\"rtl\">", arabic, StringComparison.Ordinal);
        Assert.Contains("تسجيل الخروج", arabic, StringComparison.Ordinal);

        var english = await GetAsync("/connect/endsession", "en");
        Assert.Contains("<html lang=\"en\" dir=\"ltr\">", english, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ar", "rtl")]
    [InlineData("ar-SA", "rtl")]
    [InlineData("he", "rtl")]
    [InlineData("fa-IR", "rtl")]
    [InlineData("en", "ltr")]
    [InlineData("zh-Hans", "ltr")]
    [InlineData("ja", "ltr")]
    [InlineData("hi", "ltr")]
    public void Direction_FollowsTheScript(string tag, string expected)
    {
        Assert.Equal(expected, HtmlDocumentLocale.Direction(CultureInfo.GetCultureInfo(tag)));
    }

    [Fact]
    public void HtmlAttributes_EncodesTheLanguageTagAndFallsBackToEnglishForTheInvariantCulture()
    {
        Assert.Equal("lang=\"en\" dir=\"ltr\"", HtmlDocumentLocale.HtmlAttributes(CultureInfo.InvariantCulture));
        Assert.Equal("lang=\"ar\" dir=\"rtl\"", HtmlDocumentLocale.HtmlAttributes(CultureInfo.GetCultureInfo("ar")));
    }

    [Fact]
    public void EveryLocaleResxCarriesExactlyTheBaseKeysAndPlaceholders()
    {
        var dir = Path.Combine(RepositoryRoot(), "src", "Authagonal.Server", "Resources");
        static Dictionary<string, string> Read(string path) =>
            XDocument.Load(path).Root!.Elements("data")
                .ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!);
        static string Placeholders(string v) =>
            string.Join(",", Regex.Matches(v, @"\{\d+\}").Select(m => m.Value).OrderBy(x => x, StringComparer.Ordinal));

        var baseline = Read(Path.Combine(dir, "SharedMessages.resx"));
        var locales = Directory.GetFiles(dir, "SharedMessages.*.resx");
        Assert.Equal(10, locales.Length);

        foreach (var file in locales)
        {
            var entries = Read(file);
            var name = Path.GetFileName(file);
            Assert.True(baseline.Keys.ToHashSet().SetEquals(entries.Keys), $"{name}: key set differs from SharedMessages.resx");
            foreach (var (key, value) in baseline)
            {
                Assert.True(Placeholders(value) == Placeholders(entries[key]), $"{name}: placeholders differ for {key}");
                Assert.DoesNotContain((char)0x2014, entries[key]);
            }
        }
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Authagonal.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
