using System.Text.Json;
using System.Text.Json.Nodes;
using Authagonal.Core.Stores;
using Authagonal.Server.Endpoints;
using Microsoft.Extensions.Logging;

namespace Authagonal.Server.Services;

/// <summary>
/// What a login document carries inline so the login app can paint without its own round trips: the
/// branding for the organisation the request is for, the provider list, and that organisation.
/// </summary>
/// <remarks>
/// Serialised with <see cref="JsonSerializerDefaults.Web"/> it is
/// <c>{ "branding": {...}, "providers": {...}, "organization": { "id", "slug", "name" } | null }</c>,
/// the three members the login app reads off the <c>authagonal-boot</c> element
/// (<c>login-app/src/branding.ts</c>, <c>getBoot</c>). <see cref="LoginBootPayloadBuilder.InjectIntoShell"/>
/// does that serialisation.
/// </remarks>
/// <param name="Branding">The tenant's branding with the organisation's merged over it, or the tenant's alone.</param>
/// <param name="Providers">The <c>/api/auth/providers</c> payload for the same organisation.</param>
/// <param name="Organization">The organisation the login page is for, or null when it is for none.</param>
public sealed record LoginBootPayload(
    JsonObject Branding,
    SsoProviderListResponse Providers,
    LoginBootOrganization? Organization);

/// <summary>The organisation a login page is for, as the login app shows it.</summary>
/// <param name="Id">The organisation's id.</param>
/// <param name="Slug">The organisation's slug.</param>
/// <param name="Name">The organisation's display name.</param>
public sealed record LoginBootOrganization(string Id, string Slug, string Name);

/// <summary>
/// Builds the login boot payload for one request, and inlines it into the login document.
/// </summary>
/// <remarks>
/// A host that serves the login document itself calls <see cref="BuildAsync"/> and then
/// <see cref="InjectIntoShell"/>, and serves the plain document when either fails. The standalone
/// server does so in <c>MapAuthagonalLoginFallback</c>; a host with a different login document adds its
/// own headers and its own per-request values (a Turnstile site key, say) around the same two calls.
/// <para>
/// One organisation resolution (<see cref="PreAuthOrganizationResolver.ResolveForLoginPageAsync"/>)
/// serves all three members, so the branding, the provider list with its <c>autoChallenge</c>, and the
/// organisation block always agree on which organisation the page is for.
/// </para>
/// </remarks>
public sealed class LoginBootPayloadBuilder(
    PreAuthOrganizationResolver organizations,
    IOidcProviderStore oidcStore,
    ISamlProviderStore samlStore,
    ILogger<LoginBootPayloadBuilder> logger)
{
    /// <summary>The id of the element <see cref="InjectIntoShell"/> inserts, which the login app reads.</summary>
    public const string ElementId = "authagonal-boot";

    /// <summary>
    /// Makes the payload for <paramref name="request"/>: one login-page resolution, then the branding,
    /// the provider list and the organisation block from it.
    /// </summary>
    /// <param name="request">The request for the login document.</param>
    /// <param name="tenantBranding">
    /// The tenant's branding: the operator's <c>branding.json</c> on the standalone server. Never changed.
    /// </param>
    /// <param name="turnstileSiteKey">The Turnstile site key the provider list carries, or null for none.</param>
    /// <param name="cancellationToken">Cancels the store reads.</param>
    /// <remarks>
    /// The resolver never throws: a store that cannot answer resolves to no organisation. The provider
    /// list reads the stores directly, so a store that is down throws from here; the caller serves the
    /// plain document.
    /// </remarks>
    public async Task<LoginBootPayload> BuildAsync(
        HttpRequest request, JsonObject tenantBranding, string? turnstileSiteKey, CancellationToken cancellationToken)
    {
        var organization = await organizations.ResolveForLoginPageAsync(request, cancellationToken);

        var branding = organization is null
            ? tenantBranding
            : OrganizationBranding.Merge(tenantBranding, organization, logger);

        var providers = await AuthEndpoints.BuildProvidersResponseAsync(
            oidcStore, samlStore, turnstileSiteKey, cancellationToken, organization?.Id);

        return new LoginBootPayload(
            branding,
            providers,
            organization is null
                ? null
                : new LoginBootOrganization(organization.Id, organization.Slug, organization.DisplayName));
    }

    /// <summary>
    /// Inserts <paramref name="payload"/> into <paramref name="indexHtml"/> as a
    /// <c>&lt;script type="application/json" id="authagonal-boot"&gt;</c> element just before
    /// <c>&lt;/head&gt;</c>. Returns null when the document has no <c>&lt;/head&gt;</c>, so the caller
    /// serves it as it is.
    /// </summary>
    /// <remarks>
    /// A non-executable JSON block, not an assignment: <c>script-src</c> does not govern it, so the
    /// server's strict CSP (no <c>unsafe-inline</c>, no nonce) serves it. The payload is serialised with
    /// <see cref="JsonSerializerDefaults.Web"/> and the default encoder, which escapes <c>&lt;</c> and
    /// <c>&gt;</c> as <c><</c> and <c>></c>, so a branding string can never form a
    /// <c>&lt;/script&gt;</c> and break out of the element. The rest of the document is unchanged.
    /// </remarks>
    public static string? InjectIntoShell(string indexHtml, LoginBootPayload payload)
    {
        var headEnd = indexHtml.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (headEnd < 0)
            return null;

        // Built as nodes rather than by serialising the record: the reflection serialiser the record
        // would need is not trim-safe, and the providers member has a source-generated type already.
        var root = new JsonObject
        {
            ["branding"] = payload.Branding.DeepClone(),
            ["providers"] = JsonSerializer.SerializeToNode(
                payload.Providers, AuthagonalJsonContext.Default.SsoProviderListResponse),
            ["organization"] = payload.Organization is { } organization
                ? new JsonObject
                {
                    ["id"] = organization.Id,
                    ["slug"] = organization.Slug,
                    ["name"] = organization.Name,
                }
                : null,
        };

        // The default encoder (not UnsafeRelaxedJsonEscaping), which is what escapes '<' and '>'.
        var json = root.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return indexHtml.Insert(headEnd, $"<script type=\"application/json\" id=\"{ElementId}\">{json}</script>");
    }
}
