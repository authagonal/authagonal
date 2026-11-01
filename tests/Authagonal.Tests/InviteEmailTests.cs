using System.Net;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Authagonal.Tests;

/// <summary>
/// <c>IEmailService.SendUserInviteEmailAsync</c>: the default member an older implementor inherits, and the
/// library sender's invite copy in every shipped locale, branding merge, encoding, and Resend endpoint.
/// </summary>
public sealed class InviteEmailTests
{
    private sealed record Sent(Uri Uri, string Authorization, JsonElement Body, long? ContentLength = null);

    private sealed class Capture : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Requests.Add(new Sent(request.RequestUri!, request.Headers.Authorization!.ToString(), doc.RootElement.Clone(),
                request.Content.Headers.ContentLength));
            return new HttpResponseMessage(Status);
        }
    }

    private sealed class CaptureFactory(Capture handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (IEmailService Email, Capture Capture) Create(
        Dictionary<string, string?>? extra = null, params (string Email, string? Locale)[] users)
    {
        var capture = new Capture();
        var store = new InMemoryUserStore();
        foreach (var (email, locale) in users)
            store.CreateAsync(new AuthUser
            {
                Id = Guid.NewGuid().ToString("N"), Email = email, NormalizedEmail = email.ToUpperInvariant(), Locale = locale,
            }).GetAwaiter().GetResult();

        var config = new Dictionary<string, string?>
        {
            ["Email:ResendApiKey"] = "test-key",
            ["Email:SenderEmail"] = "noreply@acme.test",
        };
        foreach (var (key, value) in extra ?? []) config[key] = value;

        var services = new ServiceCollection();
        services.AddLocalization();
        services.AddLogging();
        services.AddSingleton<IUserStore>(store);
        services.AddSingleton<IHttpClientFactory>(new CaptureFactory(capture));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config).Build());
        services.AddSingleton<IEmailService, Authagonal.Server.Services.EmailService>();
        return (services.BuildServiceProvider().GetRequiredService<IEmailService>(), capture);
    }

    private static Organization Org(string? brandingJson, string name = "Acme Inc") =>
        new() { Id = "org_acme", Slug = "acme", DisplayName = name, BrandingJson = brandingJson };

    private static string Subject(Capture c) => c.Requests.Last().Body.GetProperty("subject").GetString()!;

    private static string Html(Capture c) => c.Requests.Last().Body.GetProperty("html").GetString()!;

    // -----------------------------------------------------------------------
    // The default member
    // -----------------------------------------------------------------------

    [Fact]
    public async Task AnImplementorThatNeverHeardOfInvites_SendsThePasswordResetEmailWithTheAcceptLink()
    {
        IEmailService old = new TestEmailService();

        await old.SendUserInviteEmailAsync("a@invite.test", "Tenant", "https://auth.test/accept-invite?p=tok", "fr", default, Org(null));

        var sent = Assert.Single(((TestEmailService)old).SentEmails);
        Assert.Equal(("a@invite.test", "https://auth.test/accept-invite?p=tok", "password_reset"), sent);
    }

    [Fact]
    public void TheInterfaceMemberIsADefaultMember_WithCloudsSignature()
    {
        var method = typeof(IEmailService).GetMethod(nameof(IEmailService.SendUserInviteEmailAsync))!;

        Assert.False(method.IsAbstract);
        Assert.Equal(
            ["toEmail:String", "tenantName:String", "acceptUrl:String", "locale:String", "ct:CancellationToken", "organization:Organization"],
            method.GetParameters().Select(p => $"{p.Name}:{p.ParameterType.Name}"));
        Assert.All(method.GetParameters().Skip(3), p => Assert.True(p.IsOptional));
    }

    // -----------------------------------------------------------------------
    // Resend endpoint and envelope
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WithNoSetting_TheInviteGoesToResend()
    {
        var (email, capture) = Create();

        await email.SendUserInviteEmailAsync("a@invite.test", "Tenant", "https://auth.test/accept-invite?p=t", "en");

        var sent = Assert.Single(capture.Requests);
        Assert.Equal("https://api.resend.com/emails", sent.Uri.ToString());
        Assert.Equal("Bearer test-key", sent.Authorization);
    }

    [Fact]
    public async Task WithEmailResendApiUrl_TheInviteGoesThere()
    {
        var (email, capture) = Create(new() { ["Email:ResendApiUrl"] = "https://relay.acme.test/v1/emails" });

        await email.SendUserInviteEmailAsync("a@invite.test", "Tenant", "https://auth.test/accept-invite?p=t", "en");

        Assert.Equal("https://relay.acme.test/v1/emails", Assert.Single(capture.Requests).Uri.ToString());
    }

    [Fact]
    public async Task TheRequestCarriesAContentLength_SoARelayNeverSeesAChunkedBody()
    {
        var (email, capture) = Create();

        await email.SendUserInviteEmailAsync("a@invite.test", "Tenant", "https://auth.test/accept-invite?p=t", "en");

        Assert.True(Assert.Single(capture.Requests).ContentLength > 0);
    }

    [Fact]
    public async Task ABlankResendApiUrl_FallsBackToResend()
    {
        var (email, capture) = Create(new() { ["Email:ResendApiUrl"] = "  " });

        await email.SendUserInviteEmailAsync("a@invite.test", "Tenant", "https://auth.test/accept-invite?p=t", "en");

        Assert.Equal("https://api.resend.com/emails", Assert.Single(capture.Requests).Uri.ToString());
    }

    [Fact]
    public async Task TheRequestCarriesFromToSubjectAndHtml_FromTheConfiguredSender()
    {
        var (email, capture) = Create(new() { ["Email:SenderName"] = "Acme Auth" });

        await email.SendUserInviteEmailAsync("a@invite.test", "Tenant", "https://auth.test/accept-invite?p=t", "en");

        var body = Assert.Single(capture.Requests).Body;
        Assert.Equal("Acme Auth <noreply@acme.test>", body.GetProperty("from").GetString());
        Assert.Equal(["a@invite.test"], body.GetProperty("to").EnumerateArray().Select(e => e.GetString()));
        Assert.True(body.TryGetProperty("subject", out _));
        Assert.True(body.TryGetProperty("html", out _));
    }

    [Fact]
    public async Task AFailingProvider_Throws_SoResendCanReportIt()
    {
        var (email, capture) = Create();
        capture.Status = HttpStatusCode.InternalServerError;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            email.SendUserInviteEmailAsync("a@invite.test", "Tenant", "https://auth.test/accept-invite?p=t", "en"));
    }

    [Fact]
    public async Task AnExampleDotComAddress_IsNotSent()
    {
        var (email, capture) = Create();

        await email.SendUserInviteEmailAsync("test@example.com", "Tenant", "https://auth.test/accept-invite?p=t", "en");

        Assert.Empty(capture.Requests);
    }

    // -----------------------------------------------------------------------
    // Copy and locales
    // -----------------------------------------------------------------------

    public static TheoryData<string, string, string> Locales => new()
    {
        { "en", "You're invited", "lang=\"en\" dir=\"ltr\"" },
        { "af", "Jy is uitgenooi", "lang=\"af\" dir=\"ltr\"" },
        { "ar", "تمت دعوتك", "lang=\"ar\" dir=\"rtl\"" },
        { "de", "Sie wurden eingeladen", "lang=\"de\" dir=\"ltr\"" },
        { "es", "Has sido invitado", "lang=\"es\" dir=\"ltr\"" },
        { "fr", "Vous êtes invité", "lang=\"fr\" dir=\"ltr\"" },
        { "hi", "आपको आमंत्रित किया गया है", "lang=\"hi\" dir=\"ltr\"" },
        { "ja", "招待されました", "lang=\"ja\" dir=\"ltr\"" },
        { "pt", "Você foi convidado", "lang=\"pt\" dir=\"ltr\"" },
        { "vi", "Bạn đã được mời", "lang=\"vi\" dir=\"ltr\"" },
        { "zh-Hans", "您已受邀", "lang=\"zh-Hans\" dir=\"ltr\"" },
    };

    [Theory]
    [MemberData(nameof(Locales))]
    public async Task TheInviteIsWrittenInTheInvitersLanguage(string locale, string title, string langAttributes)
    {
        var (email, capture) = Create();

        await email.SendUserInviteEmailAsync("a@invite.test", "Tenant", "https://auth.test/accept-invite?p=t", locale);

        Assert.Equal($"{title} — Authagonal", Subject(capture));
        Assert.Contains(langAttributes, Html(capture), StringComparison.Ordinal);
        Assert.Contains(title, WebUtility.HtmlDecode(Html(capture)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryLocalesInviteIsDistinct_AndNoneIsTheEnglishCopy()
    {
        var (email, capture) = Create();
        var bodies = new Dictionary<string, string>();
        foreach (var locale in Authagonal.Server.Services.SupportedLocales.All)
        {
            await email.SendUserInviteEmailAsync("a@invite.test", "Tenant", "https://auth.test/accept-invite?p=t", locale);
            bodies[locale] = Subject(capture) + Html(capture);
        }

        Assert.Equal(11, bodies.Values.Distinct().Count());
        var english = bodies["en"];
        Assert.All(bodies.Where(b => b.Key != "en"), b => Assert.NotEqual(english, b.Value));
        Assert.All(bodies.Where(b => b.Key != "en"), b => Assert.DoesNotContain("Set your password", b.Value));
    }

    [Fact]
    public async Task TheInvitedToNameFillsTheBody_TheTenantForAPlainInvite_TheOrganisationForAnOrganisationInvite()
    {
        var (email, capture) = Create();

        await email.SendUserInviteEmailAsync("a@invite.test", "Tenant Co", "https://auth.test/accept-invite?p=t", "en");
        Assert.Contains("invited to Tenant Co.", Html(capture));

        await email.SendUserInviteEmailAsync("a@invite.test", "Tenant Co", "https://auth.test/accept-invite?p=t", "en",
            default, Org(null, "Acme Inc"));
        Assert.Contains("invited to Acme Inc.", Html(capture));
        Assert.DoesNotContain("Tenant Co", Html(capture));
    }

    [Fact]
    public async Task ABlankOrganisationName_FallsBackToTheTenant()
    {
        var (email, capture) = Create();

        await email.SendUserInviteEmailAsync("a@invite.test", "Tenant Co", "https://auth.test/accept-invite?p=t", "en",
            default, Org(null, " "));

        Assert.Contains("invited to Tenant Co.", Html(capture));
    }

    [Fact]
    public async Task WithNoInviterLocale_TheInviteesStoredLocaleDecides()
    {
        var (email, capture) = Create(null, ("a@invite.test", "de"));

        await email.SendUserInviteEmailAsync("a@invite.test", "Tenant", "https://auth.test/accept-invite?p=t");

        Assert.StartsWith("Sie wurden eingeladen", Subject(capture));
    }

    [Fact]
    public async Task TheInvitersLocale_BeatsTheInviteesStoredOne()
    {
        var (email, capture) = Create(null, ("a@invite.test", "de"));

        await email.SendUserInviteEmailAsync("a@invite.test", "Tenant", "https://auth.test/accept-invite?p=t", "ja");

        Assert.StartsWith("招待されました", Subject(capture));
    }

    [Fact]
    public async Task TheLink_IsInTheButton_HtmlEncoded()
    {
        var (email, capture) = Create();

        await email.SendUserInviteEmailAsync("a@invite.test", "Tenant", "https://auth.test/accept-invite?p=tok&organization=acme", "en");

        Assert.Contains("href=\"https://auth.test/accept-invite?p=tok&amp;organization=acme\"", Html(capture), StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Branding
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WithNoOrganisation_TheDefaultsApply()
    {
        var (email, capture) = Create();

        await email.SendUserInviteEmailAsync("a@invite.test", "Tenant", "https://auth.test/accept-invite?p=t", "en");

        Assert.Equal("You're invited — Authagonal", Subject(capture));
        Assert.Contains("background: #2563eb", Html(capture), StringComparison.Ordinal);
        Assert.DoesNotContain("<img", Html(capture), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrganisationBranding_SetsTheNameColourAndLogo()
    {
        var (email, capture) = Create();

        await email.SendUserInviteEmailAsync("a@invite.test", "Tenant", "https://auth.test/accept-invite?p=t", "en", default,
            Org("""{"appName":"QA Invite Org","primaryColor":"#0a7c3e","logoUrl":"https://cdn.acme.test/logo.png"}"""));

        Assert.Equal("You're invited — QA Invite Org", Subject(capture));
        Assert.Contains("background: #0a7c3e", Html(capture), StringComparison.Ordinal);
        Assert.Contains("<img src=\"https://cdn.acme.test/logo.png\" alt=\"QA Invite Org\"", Html(capture), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"primaryColor":"#112233"}""", "You're invited — Authagonal", "background: #112233", false)]
    [InlineData("""{"appName":"Only Name"}""", "You're invited — Only Name", "background: #2563eb", false)]
    [InlineData("""{"logoUrl":"https://cdn.acme.test/l.png"}""", "You're invited — Authagonal", "background: #2563eb", true)]
    public async Task OrganisationBranding_IsMergedFieldByField_OverTheDefaults(
        string branding, string subject, string colour, bool hasLogo)
    {
        var (email, capture) = Create();

        await email.SendUserInviteEmailAsync("a@invite.test", "Tenant", "https://auth.test/accept-invite?p=t", "en", default, Org(branding));

        Assert.Equal(subject, Subject(capture));
        Assert.Contains(colour, Html(capture), StringComparison.Ordinal);
        Assert.Equal(hasLogo, Html(capture).Contains("<img", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"primaryColor":"red;background:url(//evil.test/x)"}""")]
    [InlineData("""{"primaryColor":"\"><script>"}""")]
    [InlineData("""{"primaryColor":""}""")]
    [InlineData("""{"primaryColor":7}""")]
    [InlineData("not json at all")]
    [InlineData("""["an","array"]""")]
    public async Task AnUnusableBrandingValue_FallsBackToTheDefault(string branding)
    {
        var (email, capture) = Create();

        await email.SendUserInviteEmailAsync("a@invite.test", "Tenant", "https://auth.test/accept-invite?p=t", "en", default, Org(branding));

        Assert.Contains("background: #2563eb", Html(capture), StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", Html(capture), StringComparison.Ordinal);
        Assert.DoesNotContain("evil.test", Html(capture), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://cdn.acme.test/logo.png")]
    [InlineData("javascript:alert(1)")]
    [InlineData("//cdn.acme.test/logo.png")]
    [InlineData("data:image/png;base64,AAAA")]
    public async Task ALogoThatIsNotAnHttpsUrl_IsDropped(string logo)
    {
        var (email, capture) = Create();

        await email.SendUserInviteEmailAsync("a@invite.test", "Tenant", "https://auth.test/accept-invite?p=t", "en", default,
            Org($$"""{"logoUrl":"{{logo}}"}"""));

        Assert.DoesNotContain("<img", Html(capture), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllTextIsHtmlEncoded_TheOrganisationNameTheAppNameAndTheLogo()
    {
        var (email, capture) = Create();

        await email.SendUserInviteEmailAsync("a@invite.test", "Tenant", "https://auth.test/accept-invite?p=t", "en", default,
            Org("""{"appName":"<b>Evil</b> & Co","logoUrl":"https://cdn.acme.test/l.png?a=1&b=\"2"}""", "<i>Org</i>"));

        var html = Html(capture);
        Assert.DoesNotContain("<b>Evil", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<i>Org", html, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;Evil&lt;/b&gt; &amp; Co", html, StringComparison.Ordinal);
        Assert.Contains("&lt;i&gt;Org&lt;/i&gt;", html, StringComparison.Ordinal);
        // The subject is plain text, not HTML.
        Assert.Equal("You're invited — <b>Evil</b> & Co", Subject(capture));
    }
}
