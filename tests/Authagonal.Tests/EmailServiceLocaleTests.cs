using System.Globalization;
using System.Net;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Tests;

/// <summary>
/// The built-in Resend sender writes to the recipient in their own language: the stored
/// <c>AuthUser.Locale</c>, then the request's UI culture, then English, with lang/dir on the HTML.
/// </summary>
public sealed class EmailServiceLocaleTests
{
    private sealed class Capture : HttpMessageHandler
    {
        public List<(string Subject, string Html)> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Sent.Add((doc.RootElement.GetProperty("subject").GetString()!, doc.RootElement.GetProperty("html").GetString()!));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class CaptureFactory(Capture handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static async Task<(IEmailService Email, Capture Sent)> CreateAsync(params (string Email, string? Locale)[] users)
    {
        var capture = new Capture();
        var store = new InMemoryUserStore();
        foreach (var (email, locale) in users)
            await store.CreateAsync(new AuthUser
            {
                Id = Guid.NewGuid().ToString("N"),
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                Locale = locale,
            });

        var services = new ServiceCollection();
        services.AddLocalization();
        services.AddLogging();
        services.AddSingleton<IUserStore>(store);
        services.AddSingleton<IHttpClientFactory>(new CaptureFactory(capture));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Email:ResendApiKey"] = "test-key",
            ["Email:SenderEmail"] = "noreply@acme.test",
        }).Build());
        services.AddSingleton<IEmailService, EmailService>();
        return (services.BuildServiceProvider().GetRequiredService<IEmailService>(), capture);
    }

    private static async Task<T> WithUiCultureAsync<T>(string culture, Func<Task<T>> body)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try { return await body(); }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public async Task AUserWithAJapaneseLocaleGetsAJapaneseEmail()
    {
        var (email, sent) = await CreateAsync(("taro@acme.test", "ja"));

        await email.SendVerificationEmailAsync("taro@acme.test", "https://auth.acme.test/verify?t=1");

        var (subject, html) = Assert.Single(sent.Sent);
        Assert.Equal("メールアドレスの確認", subject);
        Assert.Contains("lang=\"ja\" dir=\"ltr\"", html, StringComparison.Ordinal);
        Assert.Contains("メールアドレスを確認", html, StringComparison.Ordinal);
        Assert.Contains("https://auth.acme.test/verify?t=1", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnArabicLocaleIsRightToLeft()
    {
        var (email, sent) = await CreateAsync(("sara@acme.test", "ar"));

        await email.SendPasswordResetEmailAsync("sara@acme.test", "https://auth.acme.test/reset?t=1");

        var (subject, html) = Assert.Single(sent.Sent);
        Assert.Equal("إعادة تعيين كلمة المرور", subject);
        Assert.Contains("lang=\"ar\" dir=\"rtl\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAccountExistsEmailGoesToTheOwnersLanguage()
    {
        var (email, sent) = await CreateAsync(("owner@acme.test", "de"));

        await email.SendAccountExistsEmailAsync("owner@acme.test", "https://auth.acme.test/login");

        Assert.Equal("Sie haben bereits ein Konto", Assert.Single(sent.Sent).Subject);
    }

    [Theory]
    [InlineData("de-DE", "Passwort zurücksetzen")]
    [InlineData("zh-TW", "重置您的密码")]
    [InlineData("zh-CN", "重置您的密码")]
    [InlineData("ar-SA", "إعادة تعيين كلمة المرور")]
    [InlineData("JA", "パスワードのリセット")]
    public async Task RegionAndChineseTagsMapTheWayTheAccountPageDoes(string stored, string expectedSubject)
    {
        var (email, sent) = await CreateAsync(("u@acme.test", stored));

        await email.SendPasswordResetEmailAsync("u@acme.test", "https://auth.acme.test/reset");

        Assert.Equal(expectedSubject, Assert.Single(sent.Sent).Subject);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tlh")]
    public async Task AnUnsetOrUnsupportedLocaleFallsBackToEnglish(string? stored)
    {
        var (email, sent) = await WithUiCultureAsync("en", () => CreateAsync(("u@acme.test", stored)));

        await email.SendVerificationEmailAsync("u@acme.test", "https://auth.acme.test/verify");

        var (subject, html) = Assert.Single(sent.Sent);
        Assert.Equal("Verify your email address", subject);
        Assert.Contains("lang=\"en\" dir=\"ltr\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoSuchUserFallsBackToEnglish()
    {
        var (email, sent) = await CreateAsync();

        await email.SendVerificationEmailAsync("nobody@acme.test", "https://auth.acme.test/verify");

        Assert.Equal("Verify your email address", Assert.Single(sent.Sent).Subject);
    }

    [Fact]
    public async Task WithNoStoredLocaleTheRequestCultureDecides()
    {
        var (email, sent) = await CreateAsync(("u@acme.test", null));

        await WithUiCultureAsync("fr", async () =>
        {
            await email.SendVerificationEmailAsync("u@acme.test", "https://auth.acme.test/verify");
            return 0;
        });

        Assert.Equal("Vérifiez votre adresse e-mail", Assert.Single(sent.Sent).Subject);
    }

    [Fact]
    public async Task ARecipientsLocaleBeatsTheRequestCulture()
    {
        var (email, sent) = await CreateAsync(("u@acme.test", "ja"));

        await WithUiCultureAsync("fr", async () =>
        {
            await email.SendVerificationEmailAsync("u@acme.test", "https://auth.acme.test/verify");
            return 0;
        });

        Assert.Equal("メールアドレスの確認", Assert.Single(sent.Sent).Subject);
    }

    [Fact]
    public async Task RenderingDoesNotLeakTheRecipientsCultureIntoTheCaller()
    {
        var (email, _) = await CreateAsync(("u@acme.test", "ja"));
        var before = CultureInfo.CurrentUICulture.Name;

        await email.SendVerificationEmailAsync("u@acme.test", "https://auth.acme.test/verify");

        Assert.Equal(before, CultureInfo.CurrentUICulture.Name);
    }

    [Fact]
    public async Task AFailingUserLookupStillSendsTheEmail()
    {
        var capture = new Capture();
        var services = new ServiceCollection();
        services.AddLocalization();
        services.AddLogging();
        services.AddSingleton<IUserStore>(_ => throw new InvalidOperationException("store down"));
        services.AddSingleton<IHttpClientFactory>(new CaptureFactory(capture));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Email:ResendApiKey"] = "k", ["Email:SenderEmail"] = "noreply@acme.test",
        }).Build());
        services.AddSingleton<IEmailService, EmailService>();

        await WithUiCultureAsync("en", async () =>
        {
            await services.BuildServiceProvider().GetRequiredService<IEmailService>()
                .SendVerificationEmailAsync("u@acme.test", "https://auth.acme.test/verify");
            return 0;
        });

        Assert.Single(capture.Sent);
    }

    [Theory]
    [InlineData("de-DE", "de")]
    [InlineData("zh-TW", "zh-Hans")]
    [InlineData("zh", "zh-Hans")]
    [InlineData("pt_BR", "pt")]
    [InlineData("hi-IN", "hi")]
    [InlineData("en-GB", "en")]
    public void SupportedLocalesResolvesRegionVariants(string tag, string expected)
    {
        Assert.Equal(expected, SupportedLocales.Resolve(tag)!.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("tlh")]
    [InlineData("xx-YY")]
    public void SupportedLocalesRejectsWhatItDoesNotShip(string? tag)
    {
        Assert.Null(SupportedLocales.Resolve(tag));
    }
}
