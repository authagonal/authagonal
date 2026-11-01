using System.Globalization;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Microsoft.Extensions.Localization;

namespace Authagonal.Server.Services;

/// <summary>
/// The built-in Resend sender. Subject and body are localized: the recipient's stored
/// <c>AuthUser.Locale</c> decides, then the request's UI culture, then English.
/// </summary>
/// <remarks>
/// Registered as a singleton, so it must not hold a store: stores can be scoped (the Cloud registers them
/// per tenant). The user store is resolved from a fresh scope for each lookup instead.
/// </remarks>
public sealed class EmailService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<EmailService> logger,
    IServiceScopeFactory scopeFactory,
    IStringLocalizer<SharedMessages> localizer) : IEmailService
{
    // The default encoder escapes everything outside Basic Latin as numeric entities, which would turn a
    // Japanese or Arabic body into a wall of &#x...; in the message source. Markup characters are still escaped.
    private static readonly HtmlEncoder TextEncoder = HtmlEncoder.Create(UnicodeRanges.All);

    private const string ConfigSection = "Email";

    // Where mail goes when Email:ResendApiUrl is not set. The setting exists so a deployment can send through
    // its own relay or proxy; it comes from the deployment's own configuration only, never from an admin API
    // or a registrant, so nothing here guards it.
    private const string DefaultResendApiUrl = "https://api.resend.com/emails";

    // What an organisation's branding falls back to, field by field.
    private const string DefaultAppName = "Authagonal";
    private const string DefaultPrimaryColor = "#2563eb";

    // A simple, safe CSS colour (hex, rgb[a]/hsl[a], or a plain keyword); anything that could break out of
    // the inline style attribute falls back to the default.
    private static readonly Regex SafeColor = new(
        @"^(#[0-9a-fA-F]{3,8}|rgba?\([\d.,%\s/]+\)|hsla?\([\d.,%\s/]+\)|[a-zA-Z]{1,20})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task SendVerificationEmailAsync(string email, string callbackUrl, CancellationToken ct = default)
    {
        if (IsTestEmail(email))
        {
            logger.LogInformation("Skipping verification email for test address: {Email}", email);
            return;
        }

        var culture = await ResolveCultureAsync(email, ct);
        var (subject, html) = Render(culture, "Email_Verify", callbackUrl);
        await SendAsync(email, subject, html, ct);
    }

    public async Task SendPasswordResetEmailAsync(string email, string callbackUrl, CancellationToken ct = default)
    {
        if (IsTestEmail(email))
        {
            logger.LogInformation("Skipping password reset email for test address: {Email}", email);
            return;
        }

        var culture = await ResolveCultureAsync(email, ct);
        var (subject, html) = Render(culture, "Email_Reset", callbackUrl);
        await SendAsync(email, subject, html, ct);
    }

    public async Task SendUserInviteEmailAsync(string toEmail, string tenantName, string acceptUrl, string? locale = null,
        CancellationToken ct = default, Organization? organization = null)
    {
        if (IsTestEmail(toEmail))
        {
            logger.LogInformation("Skipping user invite email for test address: {Email}", toEmail);
            return;
        }

        // The inviting administrator's language when it names one we ship; the invitee has no stored
        // preference yet unless the invite recorded one, which the lookup then finds.
        var culture = SupportedLocales.Resolve(locale) ?? await ResolveCultureAsync(toEmail, ct);
        var invitedTo = organization is null || string.IsNullOrWhiteSpace(organization.DisplayName)
            ? tenantName
            : organization.DisplayName;
        var (subject, html) = RenderInvite(culture, acceptUrl, invitedTo, ParseBranding(organization?.BrandingJson));
        await SendAsync(toEmail, subject, html, ct);
    }

    public async Task SendAccountExistsEmailAsync(string email, string signInUrl, CancellationToken ct = default)
    {
        if (IsTestEmail(email))
        {
            logger.LogInformation("Skipping account-exists email for test address: {Email}", email);
            return;
        }

        // The recipient is the existing account owner, so their stored locale, not the re-registrant's.
        var culture = await ResolveCultureAsync(email, ct);
        var (subject, html) = Render(culture, "Email_Exists", signInUrl);
        await SendAsync(email, subject, html, ct);
    }

    /// <summary>
    /// The language to write to <paramref name="email"/> in: the stored locale of the account at that
    /// address, else the request's UI culture, else English. A lookup failure never blocks the email.
    /// </summary>
    private async Task<CultureInfo> ResolveCultureAsync(string email, CancellationToken ct)
    {
        string? stored = null;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var users = scope.ServiceProvider.GetService<IUserStore>();
            if (users is not null)
                stored = (await users.FindByEmailAsync(email, ct))?.Locale;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not resolve locale for {Email}; using the request culture", email);
        }

        return SupportedLocales.Resolve(stored)
            ?? SupportedLocales.Resolve(CultureInfo.CurrentUICulture.Name)
            ?? CultureInfo.GetCultureInfo("en");
    }

    /// <summary>
    /// Renders one email in an explicit culture. Synchronous on purpose: it swaps the thread's UI culture
    /// for the duration of the lookups and restores it before returning, so it cannot leak across an await.
    /// </summary>
    private (string Subject, string Html) Render(CultureInfo culture, string prefix, string url)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = culture;
        try
        {
            static string Enc(string v) => TextEncoder.Encode(v);
            var subject = localizer[$"{prefix}_Subject"].Value;
            var html = $"""
                <div {HtmlDocumentLocale.HtmlAttributes(culture)} style="font-family: sans-serif; max-width: 480px; margin: 0 auto;">
                    <h2>{Enc(localizer[$"{prefix}_Heading"].Value)}</h2>
                    <p>{Enc(localizer[$"{prefix}_Body"].Value)}</p>
                    <p><a href="{url}" style="display: inline-block; padding: 12px 24px; background: #2563eb; color: white; text-decoration: none; border-radius: 6px;">{Enc(localizer[$"{prefix}_Button"].Value)}</a></p>
                    <p style="color: #6b7280; font-size: 14px; margin-top: 24px;">{Enc(localizer[$"{prefix}_Footer"].Value)}</p>
                </div>
                """;
            return (subject, html);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    /// <summary>The branding an invite email wears: the three fields an organisation can override.</summary>
    private sealed record InviteBranding(string AppName, string? LogoUrl, string PrimaryColor);

    /// <summary>
    /// The organisation's <c>appName</c>, <c>logoUrl</c> and <c>primaryColor</c> merged field by field over
    /// the defaults (<c>Authagonal</c>, no logo, <c>#2563eb</c>), so an organisation that sets only a logo
    /// keeps the default name and colour. A logo must be an absolute https URL and a colour a simple CSS
    /// colour; a value that is neither is treated as unset. Malformed JSON is ignored: an invite is never
    /// refused for an organisation's branding.
    /// </summary>
    private InviteBranding ParseBranding(string? brandingJson)
    {
        var appName = DefaultAppName;
        string? logoUrl = null;
        var primaryColor = DefaultPrimaryColor;

        if (!string.IsNullOrWhiteSpace(brandingJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(brandingJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in doc.RootElement.EnumerateObject())
                    {
                        if (property.Value.ValueKind != JsonValueKind.String) continue;
                        var value = property.Value.GetString()?.Trim();
                        if (string.IsNullOrEmpty(value)) continue;

                        if (property.Name.Equals("appName", StringComparison.OrdinalIgnoreCase))
                            appName = value;
                        else if (property.Name.Equals("logoUrl", StringComparison.OrdinalIgnoreCase)
                                 && Uri.TryCreate(value, UriKind.Absolute, out var logo) && logo.Scheme == Uri.UriSchemeHttps)
                            logoUrl = logo.AbsoluteUri;
                        else if (property.Name.Equals("primaryColor", StringComparison.OrdinalIgnoreCase)
                                 && SafeColor.IsMatch(value))
                            primaryColor = value;
                    }
                }
            }
            catch (JsonException ex)
            {
                logger.LogDebug(ex, "Ignoring unreadable organisation branding in an invite email");
            }
        }

        return new InviteBranding(appName, logoUrl, primaryColor);
    }

    /// <summary>
    /// Renders the invite in an explicit culture. The subject is the localized title and the app name, as
    /// <c>"{title} — {appName}"</c>; the body names what the person is invited to (<paramref name="invitedTo"/>).
    /// Every piece of text is HTML-encoded, including the branding values.
    /// </summary>
    private (string Subject, string Html) RenderInvite(
        CultureInfo culture, string acceptUrl, string invitedTo, InviteBranding branding)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = culture;
        try
        {
            static string Enc(string v) => TextEncoder.Encode(v);
            var title = localizer["Email_Invite_Subject"].Value;
            var subject = $"{title} — {branding.AppName}";
            var header = branding.LogoUrl is null
                ? $"""<p style="font-size: 20px; font-weight: 700; margin: 0 0 16px;">{Enc(branding.AppName)}</p>"""
                : $"""<p style="margin: 0 0 16px;"><img src="{Enc(branding.LogoUrl)}" alt="{Enc(branding.AppName)}" style="max-height: 54px; max-width: 220px;" /></p>""";
            var html = $"""
                <div {HtmlDocumentLocale.HtmlAttributes(culture)} style="font-family: sans-serif; max-width: 480px; margin: 0 auto;">
                    {header}
                    <h2>{Enc(localizer["Email_Invite_Heading"].Value)}</h2>
                    <p>{Enc(localizer["Email_Invite_Body", invitedTo].Value)}</p>
                    <p><a href="{Enc(acceptUrl)}" style="display: inline-block; padding: 12px 24px; background: {Enc(branding.PrimaryColor)}; color: white; text-decoration: none; border-radius: 6px;">{Enc(localizer["Email_Invite_Button"].Value)}</a></p>
                    <p style="color: #6b7280; font-size: 14px; margin-top: 24px;">{Enc(localizer["Email_Invite_Footer"].Value)}</p>
                </div>
                """;
            return (subject, html);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    private async Task SendAsync(string toEmail, string subject, string html, CancellationToken ct)
    {
        var apiKey = configuration[$"{ConfigSection}:ResendApiKey"]
            ?? throw new InvalidOperationException("Email:ResendApiKey is not configured");

        var senderEmail = configuration[$"{ConfigSection}:SenderEmail"]
            ?? throw new InvalidOperationException("Email:SenderEmail is not configured");

        var senderName = configuration[$"{ConfigSection}:SenderName"] ?? "Authagonal";

        var client = httpClientFactory.CreateClient("Resend");

        var resendApiUrl = configuration[$"{ConfigSection}:ResendApiUrl"];
        if (string.IsNullOrWhiteSpace(resendApiUrl)) resendApiUrl = DefaultResendApiUrl;

        using var request = new HttpRequestMessage(HttpMethod.Post, resendApiUrl);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(new ResendEmailRequest
        {
            From = $"{senderName} <{senderEmail}>",
            To = [toEmail],
            Subject = subject,
            Html = html
        }, AuthagonalJsonContext.Default.ResendEmailRequest);
        // Buffered so the request carries a Content-Length: a relay behind Email:ResendApiUrl may not read a
        // chunked body, and JsonContent alone streams one.
        await request.Content.LoadIntoBufferAsync(ct);

        var response = await client.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Resend returned {StatusCode} when sending to {Email}: {Body}",
                response.StatusCode, toEmail, body);
            throw new InvalidOperationException($"Failed to send email via Resend: {response.StatusCode}");
        }

        logger.LogInformation("Email sent to {Email} via Resend (subject: {Subject})", toEmail, subject);
    }

    private static bool IsTestEmail(string email)
    {
        return email.EndsWith("@example.com", StringComparison.OrdinalIgnoreCase);
    }
}
