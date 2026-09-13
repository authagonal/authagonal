using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Web;
using Authagonal.Core.Constants;
using Authagonal.Core.Models;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Authagonal.Tests;

/// <summary>
/// Organisation context reaching the surfaces that describe a token rather than mint it — userinfo,
/// introspection and RFC 8693 token exchange — and the organisation-scoped roles that ride the
/// <c>roles</c> claim alongside it.
/// </summary>
/// <remarks>
/// All three had the same defect in different shapes. Userinfo re-read the user record, so it
/// answered for the account's DEFAULT organisation while the ID token it had just issued named
/// another. Introspection described a token without saying which customer it was for, which is the
/// one thing a relying party serving many of them has to know. Token exchange rebuilt its subject
/// from the subject token's claims and skipped every reserved name — and the organisation claims are
/// reserved — so a downscoped token came out belonging nowhere.
/// </remarks>
public sealed class OrganizationPropagationTests : IAsyncLifetime
{
    private const string RedirectUri = "https://app.test/callback";
    private const string ExchangeClientId = "exchange-client";
    private const string ExchangeClientSecret = "exchange-secret-123456";
    private const string IntrospectClientId = "introspect-client";
    private const string IntrospectClientSecret = "introspect-secret-123456";

    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;
    private AuthUser _user = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
        _user = await _factory.SeedTestUserAsync();

        // The seeded client does not allow `roles`, and half of this file is about what lands in that
        // claim. Widened here rather than in the shared seed so no other test's token gains a scope.
        var testClient = await _factory.ClientStore.GetAsync(AuthagonalTestFactory.TestClientId);
        testClient!.AllowedScopes = [.. testClient.AllowedScopes, StandardScopes.Roles];
        await _factory.ClientStore.UpsertAsync(testClient);

        // Introspection answers `active: false` for a token addressed to an audience the calling
        // client has no part in, so the caller has to share one with the tokens it inspects.
        var hasher = _factory.Services.GetRequiredService<Authagonal.Server.Services.PasswordHasher>();
        await _factory.ClientStore.UpsertAsync(new OAuthClient
        {
            ClientId = IntrospectClientId,
            ClientName = "Introspecting Resource Server",
            RequireClientSecret = true,
            RequirePkce = false,
            ClientSecretHashes = [hasher.HashPassword(IntrospectClientSecret)],
            AllowedGrantTypes = [GrantTypes.ClientCredentials],
            AllowedScopes = ["openid"],
            Audiences = ["https://api.test/v1"],
        });

        _factory.OrganizationStore.With("org-a", "acme", "Acme Corporation");
        _factory.OrganizationStore.With("org-b", "beta", "Beta Industries");
        _factory.OrganizationMembershipStore.With("org-a", _user.Id, MembershipStatus.Active, "Auditor", "Site Manager");
        _factory.OrganizationMembershipStore.With("org-b", _user.Id, MembershipStatus.Active, "Organisation Manager");

        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    // -----------------------------------------------------------------------
    // Userinfo answers from the token, not the user record
    // -----------------------------------------------------------------------

    /// The headline defect. The operator re-tags the account to organisation B AFTER the token for A
    /// was issued; userinfo must still describe A, because that is the grant the caller presented.
    /// Re-reading the record answered B — the same server contradicting its own ID token.
    [Fact]
    public async Task Userinfo_MatchesTheIdToken_AfterAnOperatorRetagsTheUser()
    {
        var tokens = await AuthorizeAndRedeemAsync("organization=acme", "openid profile roles");
        var idClaims = ReadJwt(tokens.IdToken!);
        Assert.Equal("org-a", idClaims["org_id"]);

        // The operator re-tags the account while the token is still live.
        var stored = await _factory.UserStore.GetAsync(_user.Id);
        stored!.OrganizationId = "org-b";
        await _factory.UserStore.UpdateAsync(stored);

        var userinfo = await UserinfoAsync(tokens.AccessToken);

        Assert.Equal("org-a", userinfo.GetProperty("org_id").GetString());
        Assert.Equal("acme", userinfo.GetProperty("org_slug").GetString());
        Assert.Equal("Acme Corporation", userinfo.GetProperty("org_name").GetString());
    }

    [Fact]
    public async Task Userinfo_RolesComeFromTheToken_NotTheUserRecord()
    {
        var tokens = await AuthorizeAndRedeemAsync("organization=acme", "openid profile roles");

        // Grant an unrelated tenant-wide role after issuance. The token does not carry it, so neither
        // may userinfo — the response describes the grant, not the current account.
        var stored = await _factory.UserStore.GetAsync(_user.Id);
        stored!.Roles.Add("Tenant Wide Latecomer");
        await _factory.UserStore.UpdateAsync(stored);

        var userinfo = await UserinfoAsync(tokens.AccessToken);
        var roles = userinfo.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToList();

        Assert.Contains("Auditor", roles);
        Assert.Contains("Site Manager", roles);
        Assert.DoesNotContain("Tenant Wide Latecomer", roles);
    }

    /// A token minted for organisation B must describe B at userinfo, even though the account's own
    /// default is A — the case a single-organisation-per-user model could not express at all.
    [Fact]
    public async Task Userinfo_AnswersForTheSelectedOrganization_NotTheAccountDefault()
    {
        var stored = await _factory.UserStore.GetAsync(_user.Id);
        stored!.OrganizationId = "org-a";
        await _factory.UserStore.UpdateAsync(stored);

        var tokens = await AuthorizeAndRedeemAsync("organization=beta", "openid profile");
        var userinfo = await UserinfoAsync(tokens.AccessToken);

        Assert.Equal("org-b", userinfo.GetProperty("org_id").GetString());
        Assert.Equal("beta", userinfo.GetProperty("org_slug").GetString());
    }

    /// Regression: no organisation anywhere, and userinfo carries none of the three.
    [Fact]
    public async Task Userinfo_WithNoOrganization_CarriesNoOrgClaims()
    {
        var tokens = await AuthorizeAndRedeemAsync(null, "openid profile");
        var userinfo = await UserinfoAsync(tokens.AccessToken);

        Assert.False(userinfo.TryGetProperty("org_id", out _));
        Assert.False(userinfo.TryGetProperty("org_slug", out _));
        Assert.False(userinfo.TryGetProperty("org_name", out _));
    }

    /// Profile fields stay live — those are the subject's current details, which is what userinfo is
    /// for. Only the authorization context moved to the token.
    [Fact]
    public async Task Userinfo_ProfileFieldsAreStillLive()
    {
        var tokens = await AuthorizeAndRedeemAsync("organization=acme", "openid profile");

        var stored = await _factory.UserStore.GetAsync(_user.Id);
        stored!.FirstName = "Renamed";
        await _factory.UserStore.UpdateAsync(stored);

        var userinfo = await UserinfoAsync(tokens.AccessToken);
        Assert.Equal("Renamed", userinfo.GetProperty("given_name").GetString());
    }

    // -----------------------------------------------------------------------
    // Introspection
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Introspection_ReportsTheOrganization()
    {
        var tokens = await AuthorizeAndRedeemAsync("organization=acme", "openid profile");
        var introspected = await IntrospectAsync(tokens.AccessToken);

        Assert.True(introspected.GetProperty("active").GetBoolean());
        Assert.Equal("org-a", introspected.GetProperty("org_id").GetString());
        Assert.Equal("acme", introspected.GetProperty("org_slug").GetString());
    }

    [Fact]
    public async Task Introspection_WithNoOrganization_OmitsTheClaims()
    {
        var tokens = await AuthorizeAndRedeemAsync(null, "openid profile");
        var introspected = await IntrospectAsync(tokens.AccessToken);

        Assert.True(introspected.GetProperty("active").GetBoolean());
        Assert.False(introspected.TryGetProperty("org_id", out _));
        Assert.False(introspected.TryGetProperty("org_slug", out _));
    }

    // -----------------------------------------------------------------------
    // Token exchange
    // -----------------------------------------------------------------------

    /// An exchange is a projection of an existing session. A projection that drops the customer it
    /// was acting for is not narrower, it is unattributed — a resource server gating on org_id would
    /// have read the downscoped token as belonging nowhere.
    [Fact]
    public async Task Exchange_KeepsTheOrganization()
    {
        await SeedExchangeClientAsync();
        var tokens = await AuthorizeAndRedeemAsync("organization=acme", "openid profile");

        var exchanged = await ExchangeAsync(tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, exchanged.StatusCode);

        var body = await exchanged.Content.ReadFromJsonAsync<JsonElement>();
        var claims = ReadJwt(body.GetProperty("access_token").GetString()!);

        Assert.Equal("org-a", claims["org_id"]);
        Assert.Equal("acme", claims["org_slug"]);
        Assert.Equal("Acme Corporation", claims["org_name"]);
    }

    [Fact]
    public async Task Exchange_WithNoOrganization_CarriesNone()
    {
        await SeedExchangeClientAsync();
        var tokens = await AuthorizeAndRedeemAsync(null, "openid profile");

        var exchanged = await ExchangeAsync(tokens.AccessToken);
        var body = await exchanged.Content.ReadFromJsonAsync<JsonElement>();
        var claims = ReadJwt(body.GetProperty("access_token").GetString()!);

        Assert.False(claims.ContainsKey("org_id"));
        Assert.False(claims.ContainsKey("org_slug"));
    }

    /// F6. The exchanging client's own restriction was never consulted on this path: copying org_id
    /// across from the subject token meant a client registered to serve customer A could be handed any
    /// user's token for customer B and exchange it into a token that still said B. RestrictedTo is a
    /// statement about which customers a client may act for, and an exchange is it acting.
    [Fact]
    public async Task Exchange_RefusesAnOrganizationTheExchangingClientMayNotServe()
    {
        await SeedExchangeClientAsync(restrictedTo: ["org-b"]);
        var tokens = await AuthorizeAndRedeemAsync("organization=acme", "openid profile");

        var exchanged = await ExchangeAsync(tokens.AccessToken);

        Assert.Equal(HttpStatusCode.BadRequest, exchanged.StatusCode);
        var body = await exchanged.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_target", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Exchange_AllowsAnOrganizationOnTheClientsList()
    {
        await SeedExchangeClientAsync(restrictedTo: ["org-a"]);
        var tokens = await AuthorizeAndRedeemAsync("organization=acme", "openid profile");

        var exchanged = await ExchangeAsync(tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, exchanged.StatusCode);

        var body = await exchanged.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("org-a", ReadJwt(body.GetProperty("access_token").GetString()!)["org_id"]);
    }

    /// A restricted client cannot launder an unattributed token either: no organization on the subject
    /// token is not the same as permission to act for any.
    [Fact]
    public async Task Exchange_RestrictedClient_RefusesATokenWithNoOrganization()
    {
        await SeedExchangeClientAsync(restrictedTo: ["org-a"]);
        var tokens = await AuthorizeAndRedeemAsync(null, "openid profile");

        var exchanged = await ExchangeAsync(tokens.AccessToken);

        Assert.Equal(HttpStatusCode.BadRequest, exchanged.StatusCode);
        var body = await exchanged.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_target", body.GetProperty("error").GetString());
    }

    // -----------------------------------------------------------------------
    // F9 — the parameter's shape, and slug case
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("a/b")]
    [InlineData("a#b")]
    [InlineData("a b")]
    [InlineData("a\u0001b")]
    [InlineData("../acme")]
    public async Task Organization_MalformedValueIsRefused(string value)
    {
        var response = await _client.GetAsync(AuthorizeUrl($"organization={Uri.EscapeDataString(value)}", "openid"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var query = HttpUtility.ParseQueryString(new Uri(response.Headers.Location!.ToString()).Query);
        Assert.Equal("invalid_request", query["error"]);
    }

    /// Slugs are stored lowercase, so the parameter is lowercased before the slug lookup — otherwise
    /// `Acme` would be a value no stored slug could ever answer.
    [Fact]
    public async Task Organization_IsLowercasedForTheSlugLookup()
    {
        var tokens = await AuthorizeAndRedeemAsync("organization=ACME", "openid profile");
        Assert.Equal("org-a", ReadJwt(tokens.AccessToken)["org_id"]);
    }

    /// Ids stay exact-match: lowercasing an opaque id would resolve a different organisation from the
    /// one named, or none at all.
    [Fact]
    public async Task Organization_IdLookupIsCaseSensitive()
    {
        _factory.OrganizationStore.With("OrgMixedCase", "mixed", "Mixed");
        _factory.OrganizationMembershipStore.With("OrgMixedCase", _user.Id);

        var byExactId = await AuthorizeAndRedeemAsync("organization=OrgMixedCase", "openid profile");
        Assert.Equal("OrgMixedCase", ReadJwt(byExactId.AccessToken)["org_id"]);

        var response = await _client.GetAsync(AuthorizeUrl("organization=orgmixedcase", "openid"));
        var query = HttpUtility.ParseQueryString(new Uri(response.Headers.Location!.ToString()).Query);
        Assert.Equal("access_denied", query["error"]);
    }

    // -----------------------------------------------------------------------
    // F13 — the issuance gate sees the organization
    // -----------------------------------------------------------------------

    [Fact]
    public async Task IssuanceGate_ObservesTheOrganization()
    {
        await AuthorizeAndRedeemAsync("organization=acme", "openid profile");

        var call = Assert.Single(_factory.AuthHook.IssuanceGateCalls);
        Assert.Equal("org-a", call.OrganizationId);
        Assert.Equal("acme", call.OrganizationSlug);
    }

    [Fact]
    public async Task IssuanceGate_WithNoOrganization_SeesNone()
    {
        await AuthorizeAndRedeemAsync(null, "openid profile");

        var call = Assert.Single(_factory.AuthHook.IssuanceGateCalls);
        Assert.Null(call.OrganizationId);
        Assert.Null(call.OrganizationSlug);
    }

    // -----------------------------------------------------------------------
    // Organization-scoped roles on the token
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Roles_UnionMembershipRolesForTheSelectedOrganization()
    {
        var tokens = await AuthorizeAndRedeemAsync("organization=acme", "openid profile roles");
        var claims = ReadJwtMulti(tokens.AccessToken);

        Assert.Contains("Auditor", claims["roles"]);
        Assert.Contains("Site Manager", claims["roles"]);
        // Organisation B's role is held by the same user, in a different organisation, and must not
        // appear on a token issued for A.
        Assert.DoesNotContain("Organisation Manager", claims["roles"]);
    }

    [Fact]
    public async Task Roles_ForTheOtherOrganization_AreTheOtherOrganizationsRoles()
    {
        var tokens = await AuthorizeAndRedeemAsync("organization=beta", "openid profile roles");
        var claims = ReadJwtMulti(tokens.AccessToken);

        Assert.Contains("Organisation Manager", claims["roles"]);
        Assert.DoesNotContain("Auditor", claims["roles"]);
    }

    /// Tenant-wide roles are unioned WITH the organisation's, not replaced by them: tenant:admin is
    /// portal authority and must survive selecting an organisation.
    [Fact]
    public async Task Roles_TenantWideRolesSurviveTheUnion()
    {
        var stored = await _factory.UserStore.GetAsync(_user.Id);
        stored!.Roles.Add("tenant:admin");
        await _factory.UserStore.UpdateAsync(stored);

        var tokens = await AuthorizeAndRedeemAsync("organization=acme", "openid profile roles");
        var claims = ReadJwtMulti(tokens.AccessToken);

        Assert.Contains("tenant:admin", claims["roles"]);
        Assert.Contains("Auditor", claims["roles"]);
    }

    /// THE regression. No organisation selected: the roles claim is exactly the directly-assigned set
    /// it was before organisation roles existed, and the org claims are absent entirely.
    [Fact]
    public async Task Roles_WithNoOrganization_AreByteIdenticalToBefore()
    {
        var stored = await _factory.UserStore.GetAsync(_user.Id);
        stored!.Roles.Add("Plain Tenant Role");
        await _factory.UserStore.UpdateAsync(stored);

        var tokens = await AuthorizeAndRedeemAsync(null, "openid profile roles");
        var claims = ReadJwtMulti(tokens.AccessToken);

        Assert.Equal(["Plain Tenant Role"], claims["roles"]);
        var single = ReadJwt(tokens.AccessToken);
        Assert.False(single.ContainsKey("org_id"));
        Assert.False(single.ContainsKey("org_slug"));
        Assert.False(single.ContainsKey("org_name"));
    }

    /// An organisation inherited from the account is not an explicit selection, so its membership
    /// roles are not this request's authority — the same asymmetry the membership gate has.
    [Fact]
    public async Task Roles_AreNotUnionedForAnInheritedOrganization()
    {
        var stored = await _factory.UserStore.GetAsync(_user.Id);
        stored!.OrganizationId = "org-a";
        stored.Roles.Add("Plain Tenant Role");
        await _factory.UserStore.UpdateAsync(stored);

        var tokens = await AuthorizeAndRedeemAsync(null, "openid profile roles");
        var claims = ReadJwtMulti(tokens.AccessToken);

        Assert.Equal(["Plain Tenant Role"], claims["roles"]);
        Assert.Equal("org-a", ReadJwt(tokens.AccessToken)["org_id"]);
    }

    /// A suspended membership authorises nothing and therefore grants nothing.
    [Fact]
    public async Task Roles_ASuspendedMembershipGrantsNone()
    {
        _factory.OrganizationStore.With("org-c", "gamma", "Gamma", requireMembership: false);
        _factory.OrganizationMembershipStore.With("org-c", _user.Id, MembershipStatus.Suspended, "Ghost Role");

        var tokens = await AuthorizeAndRedeemAsync("organization=gamma", "openid profile roles");
        var claims = ReadJwt(tokens.AccessToken);

        Assert.Equal("org-c", claims["org_id"]);
        Assert.False(claims.ContainsKey("roles"));
    }

    /// Organisation roles ride the same `roles` scope gate as every other role.
    [Fact]
    public async Task Roles_AreGatedOnTheRolesScope()
    {
        var tokens = await AuthorizeAndRedeemAsync("organization=acme", "openid profile");
        var claims = ReadJwt(tokens.AccessToken);

        Assert.False(claims.ContainsKey("roles"));
        Assert.Equal("org-a", claims["org_id"]);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private sealed record Tokens(string AccessToken, string? IdToken);

    private string AuthorizeUrl(string? extra, string scope, string challenge = "")
    {
        if (challenge.Length == 0) challenge = Pkce().Challenge;
        return $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}" +
            $"&response_type=code&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            $"&scope={Uri.EscapeDataString(scope)}&state=xyz" +
            $"&code_challenge={challenge}&code_challenge_method=S256" +
            (string.IsNullOrEmpty(extra) ? "" : $"&{extra}");
    }

    private async Task<Tokens> AuthorizeAndRedeemAsync(string? extra, string scope)
    {
        var (verifier, challenge) = Pkce();
        var url = AuthorizeUrl(extra, scope, challenge);

        var authorize = await _client.GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var code = HttpUtility.ParseQueryString(new Uri(authorize.Headers.Location!.ToString()).Query)["code"];
        Assert.NotNull(code);

        var token = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = GrantTypes.AuthorizationCode,
                ["code"] = code!,
                ["redirect_uri"] = RedirectUri,
                ["client_id"] = AuthagonalTestFactory.TestClientId,
                ["code_verifier"] = verifier,
            }));
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);

        var body = await token.Content.ReadFromJsonAsync<JsonElement>();
        return new Tokens(
            body.GetProperty("access_token").GetString()!,
            body.TryGetProperty("id_token", out var id) ? id.GetString() : null);
    }

    private async Task<JsonElement> UserinfoAsync(string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> IntrospectAsync(string token)
    {
        var response = await _client.PostAsync("/connect/introspect", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["token"] = token,
                ["client_id"] = IntrospectClientId,
                ["client_secret"] = IntrospectClientSecret,
            }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task SeedExchangeClientAsync(string[]? restrictedTo = null)
    {
        var hasher = _factory.Services.GetRequiredService<Authagonal.Server.Services.PasswordHasher>();
        await _factory.ClientStore.UpsertAsync(new OAuthClient
        {
            ClientId = ExchangeClientId,
            ClientName = "Exchange Client",
            RequireClientSecret = true,
            RequirePkce = false,
            ClientSecretHashes = [hasher.HashPassword(ExchangeClientSecret)],
            AllowedGrantTypes = [GrantTypes.TokenExchange],
            AllowedScopes = ["openid", "profile", "roles"],
            Audiences = ["https://api.test/v1"],
            AccessTokenLifetimeSeconds = 3600,
            RestrictedToOrganizationIds = [.. restrictedTo ?? []],
        });
    }

    private Task<HttpResponseMessage> ExchangeAsync(string subjectToken) =>
        _client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = GrantTypes.TokenExchange,
            ["subject_token"] = subjectToken,
            ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
            ["client_id"] = ExchangeClientId,
            ["client_secret"] = ExchangeClientSecret,
        }));

    private static Dictionary<string, string> ReadJwt(string jwt) =>
        new JsonWebToken(jwt).Claims
            .GroupBy(c => c.Type)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);

    private static Dictionary<string, List<string>> ReadJwtMulti(string jwt) =>
        new JsonWebToken(jwt).Claims
            .GroupBy(c => c.Type)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Value).ToList(), StringComparer.Ordinal);

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (verifier, challenge);
    }
}
