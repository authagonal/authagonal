using System.Globalization;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
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
    private const string ResendApiUrl = "https://api.resend.com/emails";

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

    private async Task SendAsync(string toEmail, string subject, string html, CancellationToken ct)
    {
        var apiKey = configuration[$"{ConfigSection}:ResendApiKey"]
            ?? throw new InvalidOperationException("Email:ResendApiKey is not configured");

        var senderEmail = configuration[$"{ConfigSection}:SenderEmail"]
            ?? throw new InvalidOperationException("Email:SenderEmail is not configured");

        var senderName = configuration[$"{ConfigSection}:SenderName"] ?? "Authagonal";

        var client = httpClientFactory.CreateClient("Resend");

        using var request = new HttpRequestMessage(HttpMethod.Post, ResendApiUrl);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(new ResendEmailRequest
        {
            From = $"{senderName} <{senderEmail}>",
            To = [toEmail],
            Subject = subject,
            Html = html
        }, AuthagonalJsonContext.Default.ResendEmailRequest);

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
