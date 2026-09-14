using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Web;
using System.Xml;
using Authagonal.Core.Models;
using Authagonal.Tests.Infrastructure;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Authagonal.Tests;

/// <summary>
/// What a completed federation through an ORG-SCOPED connection leaves behind: the organisation on the
/// session, an active membership, and an <c>org_id</c> claim that outranks anything the relying party
/// asks for.
/// </summary>
/// <remarks>
/// Both halves of federation are exercised because they are two implementations of one rule. They have
/// diverged before — the OIDC callback went a release without applying the IdP's session bound to the
/// cookie while the SAML ACS did — and a rule that only one of them honours is a rule that holds for
/// half of a tenant's customers.
/// <para>
/// The membership creation is what makes the feature usable at all: <c>RequireMembershipForTokens</c>
/// defaults to on, so without it every user of a self-service SSO connection would authenticate
/// successfully and then be refused a token, with nothing in the product able to create the row they
/// are missing.
/// </para>
/// </remarks>
[Collection("Azurite")]
public sealed class OrganizationScopedFederationTests : IAsyncLifetime
{
    private const string RedirectUri = "https://app.test/callback";
    private const string Verifier = "verifier-of-sufficient-length-1234567890-abcdefghijklmnop";
    private const string SpEntityId = "https://sp.test/org-scoped";

    private readonly OidcMockHandler _oidcMock = new();
    private readonly AuthagonalTestFactory _factory;
    private HttpClient _client = null!;
    private string _adminToken = null!;

    public OrganizationScopedFederationTests(AzuriteFixture azurite)
    {
        _factory = new AuthagonalTestFactory
        {
            OidcHttpHandler = _oidcMock,
            AzuriteConnectionString = azurite.ConnectionString,
        };
    }

    public async Task InitializeAsync()
    {
        _oidcMock.Email = "federated@acme.test";
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
        _adminToken = await _factory.GetAdminTokenAsync(_client);
        _factory.OrganizationStore
            .With("org-acme", "acme", "Acme Corporation")
            .With("org-beta", "beta", "Beta Industries");
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    // -----------------------------------------------------------------------
    // SAML
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Saml_AnOrgScopedConnectionCreatesTheMembershipAndStampsOrgId()
    {
        var connectionId = await CreateSamlConnectionAsync("org-acme");
        await CompleteSamlLoginAsync(connectionId, "someone@acme.test");

        var user = await _factory.UserStore.FindByEmailAsync("someone@acme.test");
        Assert.NotNull(user);

        var membership = await _factory.OrganizationMembershipStore.GetAsync("org-acme", user!.Id);
        Assert.Equal(MembershipStatus.Active, membership?.Status);
        Assert.NotNull(membership!.JoinedAt);

        var claims = await AuthorizeAndReadIdTokenAsync(null);
        Assert.Equal("org-acme", claims["org_id"]);
        Assert.Equal("acme", claims["org_slug"]);
        Assert.Equal("Acme Corporation", claims["org_name"]);
    }

    /// A tenant-level connection changes nothing: no membership, no organisation, exactly the session
    /// SAML federation established before any of this existed.
    [Fact]
    public async Task Saml_ATenantLevelConnectionCreatesNoMembershipAndNoOrganization()
    {
        var connectionId = await CreateSamlConnectionAsync(organizationId: null);
        await CompleteSamlLoginAsync(connectionId, "someone@acme.test");

        var user = await _factory.UserStore.FindByEmailAsync("someone@acme.test");
        Assert.Empty(await _factory.OrganizationMembershipStore.ListByUserAsync(user!.Id));
        Assert.False((await AuthorizeAndReadIdTokenAsync(null)).ContainsKey("org_id"));
    }

    /// The connection's organisation is an assertion — the user proved their identity at that
    /// organisation's IdP — while the account tag is a downstream provisioning artefact. The assertion
    /// wins, or a user tagged by one customer's provisioner would be issued tokens for it while signed
    /// in through another's IdP.
    [Fact]
    public async Task Saml_TheConnectionsOrganizationOverridesALegacyAccountTag()
    {
        var user = await _factory.SeedTestUserAsync(email: "someone@acme.test");
        user.OrganizationId = "legacy-tag";
        await _factory.UserStore.UpdateAsync(user);

        var connectionId = await CreateSamlConnectionAsync("org-acme");
        await CompleteSamlLoginAsync(connectionId, "someone@acme.test");

        Assert.Equal("org-acme", (await AuthorizeAndReadIdTokenAsync(null))["org_id"]);
    }

    /// The precedence that the whole claim exists for: a relying party naming a different organisation
    /// is refused, not quietly handed the one it did not ask for.
    [Fact]
    public async Task Saml_ARequestNamingAnotherOrganizationIsRefused()
    {
        var connectionId = await CreateSamlConnectionAsync("org-acme");
        await CompleteSamlLoginAsync(connectionId, "someone@acme.test");

        var response = await _client.GetAsync(Authorize("organization=beta"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var query = HttpUtility.ParseQueryString(new Uri(response.Headers.Location!.ToString()).Query);
        Assert.Equal("access_denied", query["error"]);
    }

    /// Naming the SAME organisation is not a conflict — a relying party that knows which customer it
    /// is serving sends the parameter, and it agrees.
    [Fact]
    public async Task Saml_ARequestNamingTheSameOrganizationIsAllowed()
    {
        var connectionId = await CreateSamlConnectionAsync("org-acme");
        await CompleteSamlLoginAsync(connectionId, "someone@acme.test");

        Assert.Equal("org-acme", (await AuthorizeAndReadIdTokenAsync("organization=acme"))["org_id"]);
    }

    /// Suspension is how an administrator revokes access without destroying the record. Signing in
    /// again must not restore it — which is the only reason an existing row is never touched.
    [Fact]
    public async Task Saml_ASuspendedMembershipIsNotReactivatedBySigningInAgain()
    {
        var user = await _factory.SeedTestUserAsync(email: "someone@acme.test");
        _factory.OrganizationMembershipStore.With("org-acme", user.Id, MembershipStatus.Suspended);

        var connectionId = await CreateSamlConnectionAsync("org-acme");
        await CompleteSamlLoginAsync(connectionId, "someone@acme.test");

        Assert.Equal(MembershipStatus.Suspended,
            (await _factory.OrganizationMembershipStore.GetAsync("org-acme", user.Id))!.Status);

        // …and the token mint refuses, because a suspended membership authorises nothing.
        var response = await _client.GetAsync(Authorize(null));
        var query = HttpUtility.ParseQueryString(new Uri(response.Headers.Location!.ToString()).Query);
        Assert.Equal("access_denied", query["error"]);
    }

    // -----------------------------------------------------------------------
    // OIDC
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Oidc_AnOrgScopedConnectionCreatesTheMembershipAndStampsOrgId()
    {
        var connectionId = await CreateOidcConnectionAsync("org-acme");
        await CompleteOidcLoginAsync(connectionId);

        var user = await _factory.UserStore.FindByEmailAsync(_oidcMock.Email);
        Assert.NotNull(user);

        var membership = await _factory.OrganizationMembershipStore.GetAsync("org-acme", user!.Id);
        Assert.Equal(MembershipStatus.Active, membership?.Status);

        var claims = await AuthorizeAndReadIdTokenAsync(null);
        Assert.Equal("org-acme", claims["org_id"]);
        Assert.Equal("acme", claims["org_slug"]);
    }

    [Fact]
    public async Task Oidc_ATenantLevelConnectionCreatesNoMembershipAndNoOrganization()
    {
        var connectionId = await CreateOidcConnectionAsync(organizationId: null);
        await CompleteOidcLoginAsync(connectionId);

        var user = await _factory.UserStore.FindByEmailAsync(_oidcMock.Email);
        Assert.Empty(await _factory.OrganizationMembershipStore.ListByUserAsync(user!.Id));
        Assert.False((await AuthorizeAndReadIdTokenAsync(null)).ContainsKey("org_id"));
    }

    [Fact]
    public async Task Oidc_ARequestNamingAnotherOrganizationIsRefused()
    {
        var connectionId = await CreateOidcConnectionAsync("org-acme");
        await CompleteOidcLoginAsync(connectionId);

        var response = await _client.GetAsync(Authorize("organization=beta"));

        var query = HttpUtility.ParseQueryString(new Uri(response.Headers.Location!.ToString()).Query);
        Assert.Equal("access_denied", query["error"]);
    }

    /// The organisation survives a refresh rotation. Everything else on the subject is rebuilt from the
    /// user store, so an organisation that lived only on the cookie would revert to the account default
    /// on the first rotation — about one access-token lifetime after login, silently.
    [Fact]
    public async Task Oidc_TheOrganizationSurvivesARefreshRotation()
    {
        var connectionId = await CreateOidcConnectionAsync("org-acme");
        await CompleteOidcLoginAsync(connectionId);

        var tokens = await AuthorizeAndRedeemAsync(null);
        var refreshed = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!,
            ["client_id"] = AuthagonalTestFactory.TestClientId,
        }));
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);

        var idToken = (await refreshed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id_token").GetString()!;
        Assert.Equal("org-acme", new JsonWebToken(idToken).Claims.First(c => c.Type == "org_id").Value);
    }

    // -----------------------------------------------------------------------
    // Helpers — SAML
    // -----------------------------------------------------------------------

    private async Task<string> CreateSamlConnectionAsync(string? organizationId)
    {
        var created = await PostAsync("/api/v1/saml/connections", new
        {
            connectionName = "Acme ADFS",
            entityId = SpEntityId,
            metadataXml = SamlTestHelper.BuildIdpMetadata(),
            organizationId,
            allowedDomains = new[] { "acme.test" },
            jitProvisioningEnabled = true,
        });
        return created.GetProperty("connectionId").GetString()!;
    }

    private async Task CompleteSamlLoginAsync(string connectionId, string subject)
    {
        var login = await _client.GetAsync($"/saml/{connectionId}/login");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var requestId = ExtractRequestId(login.Headers.Location!.ToString());

        var response = SamlTestHelper.BuildSignedResponse(
            $"{AuthagonalTestFactory.TestIssuer}/saml/{connectionId}/acs",
            SpEntityId,
            subject,
            inResponseTo: requestId,
            email: subject);

        var acs = await _client.PostAsync($"/saml/{connectionId}/acs",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = response }));

        Assert.Equal(HttpStatusCode.Redirect, acs.StatusCode);
        Assert.DoesNotContain("error=", acs.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Pulls the AuthnRequest id out of the deflated SAMLRequest on the login redirect.</summary>
    private static string ExtractRequestId(string redirectUrl)
    {
        var query = HttpUtility.ParseQueryString(new Uri(redirectUrl).Query);
        using var input = new MemoryStream(Convert.FromBase64String(query["SAMLRequest"]!));
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(deflate, Encoding.UTF8);
        var doc = new XmlDocument();
        doc.LoadXml(reader.ReadToEnd());
        return doc.DocumentElement!.Attributes["ID"]!.Value;
    }

    // -----------------------------------------------------------------------
    // Helpers — OIDC
    // -----------------------------------------------------------------------

    private async Task<string> CreateOidcConnectionAsync(string? organizationId)
    {
        var created = await PostAsync("/api/v1/oidc/connections", new
        {
            connectionName = "Acme Entra",
            metadataLocation = $"{_oidcMock.Issuer}/.well-known/openid-configuration",
            clientId = "test-oidc-client",
            clientSecret = "test-oidc-secret",
            organizationId,
            allowedDomains = new[] { "acme.test" },
            jitProvisioningEnabled = true,
        });
        return created.GetProperty("connectionId").GetString()!;
    }

    private async Task CompleteOidcLoginAsync(string connectionId)
    {
        var login = await _client.GetAsync($"/oidc/{connectionId}/login?returnUrl=/");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        var query = HttpUtility.ParseQueryString(new Uri(login.Headers.Location!.ToString()).Query);
        _oidcMock.Nonce = query["nonce"]!;

        var callback = await _client.GetAsync(
            $"/oidc/callback?code=test-auth-code&state={Uri.EscapeDataString(query["state"]!)}");

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.DoesNotContain("error=", callback.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Helpers — authorize / token
    // -----------------------------------------------------------------------

    private static string Authorize(string? extra) =>
        $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}" +
        $"&response_type=code&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
        $"&scope={Uri.EscapeDataString("openid profile offline_access")}&state=xyz" +
        $"&code_challenge={Challenge(Verifier)}&code_challenge_method=S256" +
        (string.IsNullOrEmpty(extra) ? "" : $"&{extra}");

    private async Task<JsonElement> AuthorizeAndRedeemAsync(string? extra)
    {
        var response = await _client.GetAsync(Authorize(extra));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith(RedirectUri, location);

        var code = HttpUtility.ParseQueryString(new Uri(location).Query)["code"];
        Assert.NotNull(code);

        var token = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code!,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = AuthagonalTestFactory.TestClientId,
            ["code_verifier"] = Verifier,
        }));
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        return await token.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<Dictionary<string, string>> AuthorizeAndReadIdTokenAsync(string? extra)
    {
        var tokens = await AuthorizeAndRedeemAsync(extra);
        return new JsonWebToken(tokens.GetProperty("id_token").GetString()!).Claims
            .GroupBy(c => c.Type)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
    }

    private static string Challenge(string verifier)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(verifier));
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private async Task<JsonElement> PostAsync(string url, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);
        var response = await _client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode,
            $"POST {url} → {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
