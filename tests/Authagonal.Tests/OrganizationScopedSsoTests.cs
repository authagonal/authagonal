using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Web;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// Organisation-scoped SSO connections end to end: what the admin API stores and refuses, what the
/// tenant-wide domain index is and is not allowed to hold, what the login payload and
/// <c>/sso-check</c> offer, and where <c>/connect/authorize</c> sends an unauthenticated browser.
/// </summary>
/// <remarks>
/// The three surfaces are tested together on purpose. An org-scoped connection is deliberately absent
/// from the <c>SsoDomain</c> index, so every one of them has to find it a different way — and if any
/// one of them disagrees, a user is offered one IdP by the card and sent to another by the authorize
/// endpoint, which is a login that cannot complete.
/// </remarks>
public sealed class OrganizationScopedSsoTests : IAsyncLifetime
{
    private const string RedirectUri = "https://app.test/callback";

    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;
    private string _adminToken = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
        _adminToken = await _factory.GetAdminTokenAsync(_client);
        _factory.OrganizationStore
            .With("org-acme", "acme", "Acme Corporation")
            .With("org-beta", "beta", "Beta Industries");
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    // -----------------------------------------------------------------------
    // Admin API — storage, the domain index, and scoped uniqueness
    // -----------------------------------------------------------------------

    [Fact]
    public async Task AnOrgScopedConnectionStoresItsOrganizationAndReturnsIt()
    {
        var created = await CreateSamlAsync("Acme ADFS", organizationId: "org-acme", domains: ["acme.test"]);

        Assert.Equal("org-acme", created.GetProperty("organizationId").GetString());

        var read = await ReadJsonAsync(HttpMethod.Get,
            $"/api/v1/saml/connections/{created.GetProperty("connectionId").GetString()}");
        Assert.Equal("org-acme", read.GetProperty("organizationId").GetString());
    }

    /// The index is the tenant-wide answer to "which IdP owns this domain". An org-scoped connection
    /// has no business in it: one customer's connection would then route every other customer's users.
    [Fact]
    public async Task AnOrgScopedConnectionIsNotWrittenToTheTenantWideDomainIndex()
    {
        await CreateSamlAsync("Acme ADFS", organizationId: "org-acme", domains: ["acme.test"]);

        Assert.Null(await _factory.SsoDomainStore.GetAsync("acme.test"));
        Assert.Empty(await _factory.SsoDomainStore.GetAllAsync());
    }

    [Fact]
    public async Task ATenantLevelConnectionStillClaimsItsDomainsTenantWide()
    {
        await CreateSamlAsync("Tenant IdP", organizationId: null, domains: ["shared.test"]);

        var indexed = await _factory.SsoDomainStore.GetAsync("shared.test");
        Assert.Equal("saml", indexed?.ProviderType);
    }

    /// The whole point of scoping: one domain, one tenant-level claim, and one claim per organisation.
    [Fact]
    public async Task OneDomainMayBeClaimedAtTenantLevelAndOncePerOrganization()
    {
        await CreateSamlAsync("Tenant IdP", organizationId: null, domains: ["shared.test"]);
        await CreateSamlAsync("Acme ADFS", organizationId: "org-acme", domains: ["shared.test"]);
        await CreateOidcAsync("Beta Okta", organizationId: "org-beta", domains: ["shared.test"]);

        // Only the tenant-level one reached the index.
        Assert.Equal("saml", (await _factory.SsoDomainStore.GetAsync("shared.test"))?.ProviderType);
        Assert.Single(await _factory.SsoDomainStore.GetAllAsync());
    }

    [Fact]
    public async Task ASecondTenantLevelClaimOnTheSameDomainIsStillRefused()
    {
        await CreateSamlAsync("Tenant IdP", organizationId: null, domains: ["shared.test"]);

        var response = await SendAsync(HttpMethod.Post, "/api/v1/saml/connections", new
        {
            connectionName = "Another tenant IdP",
            entityId = "https://other.test",
            metadataLocation = "https://other.test/meta",
            allowedDomains = new[] { "shared.test" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("domain_claimed", (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString());
    }

    /// Within ONE organisation the domain namespace is still single-valued — across both protocols,
    /// because a user's email domain cannot route to two IdPs of the same organisation.
    [Fact]
    public async Task ASecondClaimOnTheSameDomainInsideOneOrganizationIsRefused()
    {
        await CreateSamlAsync("Acme ADFS", organizationId: "org-acme", domains: ["acme.test"]);

        var response = await SendAsync(HttpMethod.Post, "/api/v1/oidc/connections", new
        {
            connectionName = "Acme Entra",
            metadataLocation = "https://entra.test/.well-known/openid-configuration",
            clientId = "c",
            clientSecret = "s",
            organizationId = "org-acme",
            allowedDomains = new[] { "acme.test" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("domain_claimed", error.GetProperty("error").GetString());
        Assert.Contains("organization", error.GetProperty("error_description").GetString());
    }

    /// A connection pinned to an organisation that does not exist can never be reached — pre-auth
    /// resolution validates the organisation before it will offer one — so it is refused at the write
    /// rather than becoming a connection that silently does nothing.
    [Fact]
    public async Task AnUnknownOrganizationIsRefused()
    {
        var response = await SendAsync(HttpMethod.Post, "/api/v1/saml/connections", new
        {
            connectionName = "Ghost",
            entityId = "https://ghost.test",
            metadataLocation = "https://ghost.test/meta",
            organizationId = "org-nonexistent",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("unknown_organization", (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString());
    }

    /// Moving an existing tenant-level connection into an organisation has to take its index rows with
    /// it, or the domain keeps routing tenant-wide to a connection that is no longer tenant-wide.
    [Fact]
    public async Task MovingAConnectionIntoAnOrganizationRemovesItsIndexRows()
    {
        var created = await CreateSamlAsync("Tenant IdP", organizationId: null, domains: ["shared.test"]);
        var id = created.GetProperty("connectionId").GetString()!;
        Assert.NotNull(await _factory.SsoDomainStore.GetAsync("shared.test"));

        var updated = await ReadJsonAsync(HttpMethod.Put, $"/api/v1/saml/connections/{id}",
            new { organizationId = "org-acme" });

        Assert.Equal("org-acme", updated.GetProperty("organizationId").GetString());
        Assert.Null(await _factory.SsoDomainStore.GetAsync("shared.test"));
        // …and the domains themselves are untouched, only their tenant-wide claim.
        Assert.Equal(["shared.test"],
            updated.GetProperty("allowedDomains").EnumerateArray().Select(d => d.GetString()));
    }

    /// …and back again, because an operator who scoped a connection by mistake has to be able to undo it.
    [Fact]
    public async Task MovingAConnectionBackToTenantLevelReRegistersItsIndexRows()
    {
        var created = await CreateSamlAsync("Acme ADFS", organizationId: "org-acme", domains: ["acme.test"]);
        var id = created.GetProperty("connectionId").GetString()!;
        Assert.Null(await _factory.SsoDomainStore.GetAsync("acme.test"));

        var updated = await ReadJsonAsync(HttpMethod.Put, $"/api/v1/saml/connections/{id}",
            new { organizationId = "" });

        Assert.False(updated.TryGetProperty("organizationId", out var org) && org.ValueKind != JsonValueKind.Null);
        Assert.Equal(id, (await _factory.SsoDomainStore.GetAsync("acme.test"))?.ConnectionId);
    }

    /// An update that says nothing about the scope leaves it alone — the same partial-update contract
    /// every other field on this endpoint has.
    [Fact]
    public async Task AnUpdateThatDoesNotMentionTheScopeLeavesIt()
    {
        var created = await CreateSamlAsync("Acme ADFS", organizationId: "org-acme", domains: ["acme.test"]);
        var id = created.GetProperty("connectionId").GetString()!;

        var updated = await ReadJsonAsync(HttpMethod.Put, $"/api/v1/saml/connections/{id}",
            new { jitProvisioningEnabled = true });

        Assert.Equal("org-acme", updated.GetProperty("organizationId").GetString());
        Assert.Null(await _factory.SsoDomainStore.GetAsync("acme.test"));
    }

    // -----------------------------------------------------------------------
    // /api/auth/providers
    // -----------------------------------------------------------------------

    /// Without an organisation the login screen is the tenant's own. An org-scoped connection is not a
    /// tenant-level button and must not appear as one.
    [Fact]
    public async Task WithoutAnOrganization_OrgScopedConnectionsAreNotListed()
    {
        await CreateOidcAsync("Tenant Google", organizationId: null, domains: []);
        await CreateOidcAsync("Acme Entra", organizationId: "org-acme", domains: []);

        var providers = await ProvidersAsync(organization: null);

        Assert.Equal(["Tenant Google"], providers.GetProperty("providers").EnumerateArray()
            .Select(p => p.GetProperty("name").GetString()));
        Assert.False(providers.TryGetProperty("autoChallenge", out _));
    }

    [Fact]
    public async Task WithAnOrganization_ItsConnectionsAreListedFirst()
    {
        await CreateOidcAsync("Tenant Google", organizationId: null, domains: []);
        await CreateOidcAsync("Acme Entra", organizationId: "org-acme", domains: []);
        await CreateSamlAsync("Acme ADFS", organizationId: "org-acme", domains: []);
        await CreateOidcAsync("Beta Okta", organizationId: "org-beta", domains: []);

        var names = (await ProvidersAsync("acme")).GetProperty("providers").EnumerateArray()
            .Select(p => p.GetProperty("name").GetString()).ToList();

        Assert.Equal(3, names.Count);
        Assert.Equal(["Acme Entra", "Acme ADFS"], names.Take(2));
        Assert.Equal("Tenant Google", names[2]);
        Assert.DoesNotContain("Beta Okta", names);
    }

    /// One connection means there is no choice to present, so the login app can skip the card. It
    /// carries the whole provider record because the connection may be domain-routed and therefore
    /// absent from `providers` — see the next test.
    [Fact]
    public async Task WithExactlyOneConnection_AutoChallengeNamesIt()
    {
        var created = await CreateOidcAsync("Acme Entra", organizationId: "org-acme", domains: []);
        var id = created.GetProperty("connectionId").GetString()!;

        var autoChallenge = (await ProvidersAsync("acme")).GetProperty("autoChallenge");

        Assert.Equal(id, autoChallenge.GetProperty("connectionId").GetString());
        Assert.Equal("oidc", autoChallenge.GetProperty("type").GetString());
        Assert.Equal($"/oidc/{id}/login", autoChallenge.GetProperty("loginUrl").GetString());
    }

    [Fact]
    public async Task ADomainRoutedSoleConnectionIsAutoChallengedButNotListedAsAButton()
    {
        await CreateOidcAsync("Acme Entra", organizationId: "org-acme", domains: ["acme.test"]);

        var providers = await ProvidersAsync("acme");

        Assert.Empty(providers.GetProperty("providers").EnumerateArray());
        Assert.Equal("Acme Entra", providers.GetProperty("autoChallenge").GetProperty("name").GetString());
    }

    [Fact]
    public async Task WithSeveralConnections_ThereIsNoAutoChallenge()
    {
        await CreateOidcAsync("Acme Entra", organizationId: "org-acme", domains: []);
        await CreateSamlAsync("Acme ADFS", organizationId: "org-acme", domains: []);

        Assert.False((await ProvidersAsync("acme")).TryGetProperty("autoChallenge", out _));
    }

    /// A host that pins the organisation per request (a custom domain) gets the same payload without
    /// the query parameter.
    [Fact]
    public async Task TheTenantContextPinAloneSelectsTheOrganization()
    {
        _factory.TenantContext.OrganizationId = "org-acme";
        await CreateOidcAsync("Acme Entra", organizationId: "org-acme", domains: []);

        var providers = await ProvidersAsync(organization: null);

        Assert.Equal("Acme Entra", Assert.Single(providers.GetProperty("providers").EnumerateArray())
            .GetProperty("name").GetString());
    }

    // -----------------------------------------------------------------------
    // /api/auth/sso-check
    // -----------------------------------------------------------------------

    /// The precedence that matters: the same domain claimed tenant-wide and by the organisation the
    /// request is for. The organisation's own IdP wins, because that is the one its users have.
    [Fact]
    public async Task SsoCheck_PrefersTheOrganizationsConnectionOverTheTenantIndex()
    {
        var tenant = await CreateSamlAsync("Tenant IdP", organizationId: null, domains: ["shared.test"]);
        var acme = await CreateOidcAsync("Acme Entra", organizationId: "org-acme", domains: ["shared.test"]);

        var scoped = await SsoCheckAsync("someone@shared.test", organization: "acme");
        Assert.True(scoped.GetProperty("ssoRequired").GetBoolean());
        Assert.Equal(acme.GetProperty("connectionId").GetString(), scoped.GetProperty("connectionId").GetString());
        Assert.Equal("oidc", scoped.GetProperty("providerType").GetString());

        // …and with no organisation, the tenant-wide answer is exactly what it always was.
        var unscoped = await SsoCheckAsync("someone@shared.test", organization: null);
        Assert.Equal(tenant.GetProperty("connectionId").GetString(), unscoped.GetProperty("connectionId").GetString());
        Assert.Equal("saml", unscoped.GetProperty("providerType").GetString());
    }

    /// An organisation whose sole connection claims no domain claims the whole organisation — the same
    /// rule that makes it the authorize endpoint's auto-challenge.
    [Fact]
    public async Task SsoCheck_ASoleUndomainedConnectionAnswersForEveryAddress()
    {
        var created = await CreateOidcAsync("Acme Entra", organizationId: "org-acme", domains: []);

        var result = await SsoCheckAsync("anyone@anywhere.test", organization: "acme");

        Assert.True(result.GetProperty("ssoRequired").GetBoolean());
        Assert.Equal(created.GetProperty("connectionId").GetString(), result.GetProperty("connectionId").GetString());
    }

    /// An organisation that claims nothing for this domain falls through to the tenant-wide index
    /// rather than refusing — the user may still be a tenant-level SSO user.
    [Fact]
    public async Task SsoCheck_FallsThroughToTheTenantIndexWhenTheOrganizationClaimsNothing()
    {
        await CreateOidcAsync("Acme Entra", organizationId: "org-acme", domains: ["acme.test"]);
        await CreateOidcAsync("Acme Okta", organizationId: "org-acme", domains: ["acme-eu.test"]);
        var tenant = await CreateSamlAsync("Tenant IdP", organizationId: null, domains: ["elsewhere.test"]);

        var result = await SsoCheckAsync("someone@elsewhere.test", organization: "acme");

        Assert.Equal(tenant.GetProperty("connectionId").GetString(), result.GetProperty("connectionId").GetString());
    }

    [Fact]
    public async Task SsoCheck_UnknownDomainIsStillNotSsoRequired()
    {
        await CreateOidcAsync("Acme Entra", organizationId: "org-acme", domains: ["acme.test"]);
        await CreateOidcAsync("Acme Okta", organizationId: "org-acme", domains: ["acme-eu.test"]);

        var result = await SsoCheckAsync("someone@nowhere.test", organization: "acme");

        Assert.False(result.GetProperty("ssoRequired").GetBoolean());
    }

    // -----------------------------------------------------------------------
    // /connect/authorize — home-realm discovery
    // -----------------------------------------------------------------------

    /// The headline behaviour: an unauthenticated request that names an organisation with one IdP goes
    /// straight to it, with no login card in between.
    [Fact]
    public async Task Authorize_AutoChallengesAnOrganizationsSoleConnection()
    {
        var created = await CreateOidcAsync("Acme Entra", organizationId: "org-acme", domains: []);
        var id = created.GetProperty("connectionId").GetString()!;

        var location = await AuthorizeLocationAsync("organization=acme");

        Assert.StartsWith($"/oidc/{id}/login", location);
        Assert.Contains("returnUrl=", location);
    }

    [Fact]
    public async Task Authorize_AutoChallengesASamlConnectionToItsOwnPath()
    {
        var created = await CreateSamlAsync("Acme ADFS", organizationId: "org-acme", domains: []);

        var location = await AuthorizeLocationAsync("organization=acme");

        Assert.StartsWith($"/saml/{created.GetProperty("connectionId").GetString()}/login", location);
    }

    /// Several connections: the hinted email domain is what tells them apart, matched WITHIN the
    /// organisation because none of them is in the tenant-wide index.
    [Fact]
    public async Task Authorize_WithSeveralConnections_MatchesTheHintedDomain()
    {
        await CreateOidcAsync("Acme Entra", organizationId: "org-acme", domains: ["acme.test"]);
        var eu = await CreateSamlAsync("Acme EU ADFS", organizationId: "org-acme", domains: ["acme-eu.test"]);

        var location = await AuthorizeLocationAsync("organization=acme&login_hint=bob%40acme-eu.test");

        Assert.StartsWith($"/saml/{eu.GetProperty("connectionId").GetString()}/login", location);
        Assert.Contains("loginHint=bob%40acme-eu.test", location);
    }

    /// Several connections and no hint is a genuine choice, so the login card renders — that is what
    /// `autoChallenge` being absent from the providers payload means.
    [Fact]
    public async Task Authorize_WithSeveralConnectionsAndNoHint_RendersTheLoginCard()
    {
        await CreateOidcAsync("Acme Entra", organizationId: "org-acme", domains: ["acme.test"]);
        await CreateSamlAsync("Acme EU ADFS", organizationId: "org-acme", domains: ["acme-eu.test"]);

        var location = await AuthorizeLocationAsync("organization=acme");

        Assert.StartsWith("/login", location);
    }

    /// No match inside the organisation falls through to exactly today's tenant-wide rules.
    [Fact]
    public async Task Authorize_FallsThroughToTheTenantWideDomainIndex()
    {
        await CreateOidcAsync("Acme Entra", organizationId: "org-acme", domains: ["acme.test"]);
        await CreateOidcAsync("Acme Okta", organizationId: "org-acme", domains: ["acme-eu.test"]);
        var tenant = await CreateSamlAsync("Tenant IdP", organizationId: null, domains: ["elsewhere.test"]);

        var location = await AuthorizeLocationAsync("organization=acme&login_hint=bob%40elsewhere.test");

        Assert.StartsWith($"/saml/{tenant.GetProperty("connectionId").GetString()}/login", location);
    }

    /// A hint naming a domain the sole connection explicitly does NOT claim is the one case where the
    /// request contradicts the auto-challenge: the card renders instead of federating them to an IdP
    /// their address does not belong to.
    [Fact]
    public async Task Authorize_ASoleConnectionIsNotAutoChallengedAgainstAContradictingHint()
    {
        await CreateOidcAsync("Acme Entra", organizationId: "org-acme", domains: ["acme.test"]);

        Assert.StartsWith("/login", await AuthorizeLocationAsync("organization=acme&login_hint=bob%40other.test"));
        // …while an address it does claim still goes straight through.
        Assert.StartsWith("/oidc/", await AuthorizeLocationAsync("organization=acme&login_hint=bob%40acme.test"));
    }

    /// An `idp_hint` selects within the organisation, including a SAML connection — which the
    /// tenant-wide hint path, being OIDC-only, cannot reach.
    [Fact]
    public async Task Authorize_IdpHintSelectsWithinTheOrganization()
    {
        await CreateOidcAsync("Acme Entra", organizationId: "org-acme", domains: []);
        var adfs = await CreateSamlAsync("Acme ADFS", organizationId: "org-acme", domains: []);
        var id = adfs.GetProperty("connectionId").GetString()!;

        var location = await AuthorizeLocationAsync($"organization=acme&idp_hint={id}");

        Assert.StartsWith($"/saml/{id}/login", location);
    }

    /// The loop-breaker. This path federates without being asked to, so a federation that failed and
    /// bounced back here must surface the error to the relying party instead of re-federating forever.
    [Fact]
    public async Task Authorize_AFailedFederationReturnsTheErrorInsteadOfLooping()
    {
        await CreateOidcAsync("Acme Entra", organizationId: "org-acme", domains: []);

        var response = await _client.GetAsync(
            Authorize("organization=acme&error=access_denied&error_description=nope"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith(RedirectUri, location);
        var query = HttpUtility.ParseQueryString(new Uri(location).Query);
        Assert.Equal("access_denied", query["error"]);
        Assert.Equal("nope", query["error_description"]);
    }

    /// The security boundary at the authorize endpoint: a client restricted to one organisation cannot
    /// be steered to another's IdP by a query parameter. No organisation resolves, so nothing is
    /// auto-challenged and the ordinary login card renders.
    [Fact]
    public async Task Authorize_DoesNotFederateToAnOrganizationTheClientIsRestrictedAwayFrom()
    {
        await CreateOidcAsync("Beta Okta", organizationId: "org-beta", domains: []);
        var client = await _factory.ClientStore.GetAsync(AuthagonalTestFactory.TestClientId);
        client!.RestrictedToOrganizationIds = ["org-acme"];
        await _factory.ClientStore.UpsertAsync(client);

        Assert.StartsWith("/login", await AuthorizeLocationAsync("organization=beta"));
    }

    /// A disabled organisation mints no tokens, so federating into it would be a round trip that ends
    /// in access_denied.
    [Fact]
    public async Task Authorize_DoesNotFederateIntoADisabledOrganization()
    {
        await CreateOidcAsync("Acme Entra", organizationId: "org-acme", domains: []);
        _factory.OrganizationStore.With("org-acme", "acme", "Acme Corporation", enabled: false);

        Assert.StartsWith("/login", await AuthorizeLocationAsync("organization=acme"));
    }

    /// The upgrade guarantee at the authorize endpoint: with no organisation named and none pinned,
    /// an org-scoped connection is invisible and the request behaves exactly as it did before.
    [Fact]
    public async Task Authorize_WithNoOrganization_IgnoresOrgScopedConnectionsEntirely()
    {
        await CreateOidcAsync("Acme Entra", organizationId: "org-acme", domains: ["acme.test"]);

        Assert.StartsWith("/login", await AuthorizeLocationAsync("login_hint=bob%40acme.test"));
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// RFC 7636 §4.1 puts the verifier at 43-128 characters; the client requires PKCE, so an authorize
    /// without a challenge is refused before any home-realm rule runs.
    private const string Verifier = "verifier-of-sufficient-length-1234567890-abcdefghijklmnop";

    private static string Authorize(string? extra) =>
        $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}" +
        $"&response_type=code&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
        $"&scope=openid&state=xyz" +
        $"&code_challenge={Challenge(Verifier)}&code_challenge_method=S256" +
        (string.IsNullOrEmpty(extra) ? "" : $"&{extra}");

    private static string Challenge(string verifier)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(verifier));
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private async Task<string> AuthorizeLocationAsync(string extra)
    {
        var response = await _client.GetAsync(Authorize(extra));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location!.ToString();
    }

    private async Task<JsonElement> ProvidersAsync(string? organization)
    {
        var url = organization is null
            ? "/api/auth/providers"
            : $"/api/auth/providers?organization={Uri.EscapeDataString(organization)}";
        var response = await _client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> SsoCheckAsync(string email, string? organization)
    {
        var url = $"/api/auth/sso-check?email={Uri.EscapeDataString(email)}"
            + (organization is null ? "" : $"&organization={Uri.EscapeDataString(organization)}");
        var response = await _client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private Task<JsonElement> CreateSamlAsync(string name, string? organizationId, string[] domains) =>
        ReadJsonAsync(HttpMethod.Post, "/api/v1/saml/connections", new
        {
            connectionName = name,
            entityId = $"https://{Guid.NewGuid():N}.test",
            metadataLocation = $"https://{Guid.NewGuid():N}.test/meta",
            organizationId,
            allowedDomains = domains,
        });

    private Task<JsonElement> CreateOidcAsync(string name, string? organizationId, string[] domains) =>
        ReadJsonAsync(HttpMethod.Post, "/api/v1/oidc/connections", new
        {
            connectionName = name,
            metadataLocation = $"https://{Guid.NewGuid():N}.test/.well-known/openid-configuration",
            clientId = "cid",
            clientSecret = "csecret",
            organizationId,
            allowedDomains = domains,
        });

    private async Task<JsonElement> ReadJsonAsync(HttpMethod method, string url, object? body = null)
    {
        var response = await SendAsync(method, url, body);
        Assert.True(response.IsSuccessStatusCode,
            $"{method} {url} → {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return _client.SendAsync(request);
    }
}
