using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Web;
using Authagonal.Core.Models;
using Authagonal.Tests.Infrastructure;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Authagonal.Tests;

/// <summary>
/// The <c>organization</c> authorize parameter end to end: shape validation, the round trip through
/// login, the PAR leg, and the <c>org_id</c> / <c>org_slug</c> / <c>org_name</c> claims that come out
/// the other side.
/// </summary>
/// <remarks>
/// <see cref="NoOrganization_TokenClaimsAreUnchanged"/> is the regression guard for every deployment
/// that will never create an organisation: the claim set a plain authorization produces must be
/// byte-identical to the one it produced before organisations existed, down to the absence of the
/// three new names.
/// </remarks>
public sealed class OrganizationAuthorizeParameterTests : IAsyncLifetime
{
    private const string RedirectUri = "https://app.test/callback";
    /// RFC 7636 §4.1 puts the verifier at 43-128 characters; a shorter one is refused at the token
    /// endpoint, not at authorize, so it fails as an opaque 400 on redemption.
    private const string Verifier = "verifier-of-sufficient-length-1234567890-abcdefghijklmnop";

    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;
    private AuthUser _user = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
        _user = await _factory.SeedTestUserAsync();
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    // -----------------------------------------------------------------------
    // Shape validation — refused at the endpoint, before any interaction
    // -----------------------------------------------------------------------

    /// Two selectors naming two different organisations is a request that means two things. Ranking
    /// them would tell the relying party it got one when it may have got the other.
    [Fact]
    public async Task ConflictingAliases_AreRefused()
    {
        var response = await _client.GetAsync(Authorize("organization=acme&org_slug=beta"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var query = HttpUtility.ParseQueryString(new Uri(response.Headers.Location!.ToString()).Query);
        Assert.Equal("invalid_request", query["error"]);
        Assert.Contains("organization", query["error_description"]);
    }

    /// The same value under two names is not a conflict — a relying party hedging across providers
    /// sends both and means one thing.
    [Fact]
    public async Task AgreeingAliases_AreAccepted()
    {
        _factory.OrganizationStore.With("org-a", "acme", "Acme");
        _factory.OrganizationMembershipStore.With("org-a", _user.Id);
        await LoginAsync();

        var response = await _client.GetAsync(Authorize("organization=acme&org_slug=acme"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(RedirectUri, response.Headers.Location!.ToString());
    }

    /// Delivered DIRECTLY, not reflected to redirect_uri: the duplicate scan runs before redirect_uri
    /// has been validated against the client, and bouncing an error to an unvalidated URI is an open
    /// redirect. The parameter is on the single-valued list precisely so this refusal exists — a
    /// first-wins read would have the server acting on a different organisation from the one the proxy
    /// and the access log in front of it recorded.
    [Fact]
    public async Task RepeatedParameter_IsRefused()
    {
        var response = await _client.GetAsync(Authorize("organization=acme&organization=beta"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", json.GetProperty("error").GetString());
        Assert.Contains("more than once", json.GetProperty("error_description").GetString());
    }

    [Fact]
    public async Task OverlongParameter_IsRefused()
    {
        var response = await _client.GetAsync(Authorize($"organization={new string('a', 201)}"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var query = HttpUtility.ParseQueryString(new Uri(response.Headers.Location!.ToString()).Query);
        Assert.Equal("invalid_request", query["error"]);
    }

    // -----------------------------------------------------------------------
    // Claims
    // -----------------------------------------------------------------------

    /// The upgrade guarantee: nothing selected, nothing emitted. An RP parsing the id_token sees the
    /// exact claim set it saw before organisations shipped.
    [Fact]
    public async Task NoOrganization_TokenClaimsAreUnchanged()
    {
        await LoginAsync();
        var claims = await AuthorizeAndReadIdTokenAsync(null);

        Assert.False(claims.ContainsKey("org_id"));
        Assert.False(claims.ContainsKey("org_slug"));
        Assert.False(claims.ContainsKey("org_name"));
    }

    [Fact]
    public async Task SelectedOrganization_EmitsAllThreeClaims()
    {
        _factory.OrganizationStore.With("org-a", "acme", "Acme Corporation");
        _factory.OrganizationMembershipStore.With("org-a", _user.Id);
        await LoginAsync();

        var claims = await AuthorizeAndReadIdTokenAsync("organization=acme");

        Assert.Equal("org-a", claims["org_id"]);
        Assert.Equal("acme", claims["org_slug"]);
        Assert.Equal("Acme Corporation", claims["org_name"]);
    }

    /// A legacy tag has no record behind it, so there is no slug or name to emit. Absent must read as
    /// "there is none", never as "withheld".
    [Fact]
    public async Task LegacyOrganizationId_EmitsOrgIdOnly()
    {
        var tagged = await _factory.SeedTestUserAsync("legacy@example.com");
        tagged.OrganizationId = "legacy-42";
        await _factory.UserStore.UpdateAsync(tagged);
        await LoginAsync("legacy@example.com");

        var claims = await AuthorizeAndReadIdTokenAsync(null);

        Assert.Equal("legacy-42", claims["org_id"]);
        Assert.False(claims.ContainsKey("org_slug"));
        Assert.False(claims.ContainsKey("org_name"));
    }

    /// The organisation claims ride the profile scope, exactly as org_id always has. Without it the
    /// token carries none of the three.
    [Fact]
    public async Task OrganizationClaims_AreGatedOnTheProfileScope()
    {
        _factory.OrganizationStore.With("org-a", "acme", "Acme");
        _factory.OrganizationMembershipStore.With("org-a", _user.Id);
        await LoginAsync();

        var claims = await AuthorizeAndReadIdTokenAsync("organization=acme", scope: "openid");

        Assert.False(claims.ContainsKey("org_id"));
        Assert.False(claims.ContainsKey("org_slug"));
        Assert.False(claims.ContainsKey("org_name"));
    }

    // -----------------------------------------------------------------------
    // Refusals reach the relying party as OAuth errors
    // -----------------------------------------------------------------------

    [Fact]
    public async Task UnknownOrganization_ReturnsAccessDenied()
    {
        await LoginAsync();
        var response = await _client.GetAsync(Authorize("organization=no-such-org"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var query = HttpUtility.ParseQueryString(new Uri(response.Headers.Location!.ToString()).Query);
        Assert.Equal("access_denied", query["error"]);
    }

    [Fact]
    public async Task NonMember_ReturnsAccessDenied()
    {
        _factory.OrganizationStore.With("org-a", "acme", "Acme");
        await LoginAsync();

        var response = await _client.GetAsync(Authorize("organization=acme"));

        var query = HttpUtility.ParseQueryString(new Uri(response.Headers.Location!.ToString()).Query);
        Assert.Equal("access_denied", query["error"]);
    }

    // -----------------------------------------------------------------------
    // PAR and the login round trip
    // -----------------------------------------------------------------------

    /// PAR carries the parameter in the pushed payload rather than the query, so the authorize leg
    /// never sees it on the URL — and must still act on it. Nothing in the PAR endpoint was taught
    /// about organisations; it copies every form field it is given.
    [Fact]
    public async Task Par_CarriesTheOrganizationThroughToTheToken()
    {
        _factory.OrganizationStore.With("org-a", "acme", "Acme Corporation");
        _factory.OrganizationMembershipStore.With("org-a", _user.Id);
        await LoginAsync();

        var fields = BasePushedFields();
        fields["organization"] = "acme";
        var parResponse = await _client.PostAsync("/connect/par", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.Created, parResponse.StatusCode);
        var requestUri = (await parResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("request_uri").GetString()!;

        var authorize = await _client.GetAsync(
            $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}&request_uri={Uri.EscapeDataString(requestUri)}");
        var code = HttpUtility.ParseQueryString(new Uri(authorize.Headers.Location!.ToString()).Query)["code"];
        Assert.NotNull(code);

        var claims = await RedeemAsync(code!);
        Assert.Equal("org-a", claims["org_id"]);
        Assert.Equal("acme", claims["org_slug"]);
    }

    /// The parameter survives the trip through the login app because the whole authorize URL rides as
    /// returnUrl — nothing in the login redirect had to be taught about it, and this asserts that the
    /// prompt-stripping rebuild does not drop it.
    [Fact]
    public async Task Organization_SurvivesTheLoginRoundTrip()
    {
        _factory.OrganizationStore.With("org-a", "acme", "Acme");
        _factory.OrganizationMembershipStore.With("org-a", _user.Id);

        // Unauthenticated: bounces to login with the authorize URL as returnUrl.
        var bounced = await _client.GetAsync(Authorize("organization=acme&prompt=login"));
        Assert.Equal(HttpStatusCode.Redirect, bounced.StatusCode);
        // The login redirect is relative, so it has to be resolved against a base before Query works.
        var loginLocation = new Uri(new Uri("https://test.local"), bounced.Headers.Location!.ToString());
        var returnUrl = HttpUtility.ParseQueryString(loginLocation.Query)["returnUrl"];
        Assert.NotNull(returnUrl);
        Assert.Contains("organization=acme", returnUrl);
        // prompt is stripped so the fresh session is not re-challenged into a loop; organization is not.
        Assert.DoesNotContain("prompt=", returnUrl);

        await LoginAsync();
        var resumed = await _client.GetAsync(returnUrl!);
        var code = HttpUtility.ParseQueryString(new Uri(resumed.Headers.Location!.ToString()).Query)["code"];
        Assert.NotNull(code);

        var claims = await RedeemAsync(code!);
        Assert.Equal("org-a", claims["org_id"]);
    }

    // -----------------------------------------------------------------------
    // Discovery
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Discovery_AdvertisesTheOrganizationClaims()
    {
        var response = await _client.GetAsync("/.well-known/openid-configuration");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        var claims = json.GetProperty("claims_supported").EnumerateArray()
            .Select(c => c.GetString()).ToList();

        Assert.Contains("org_id", claims);
        Assert.Contains("org_slug", claims);
        Assert.Contains("org_name", claims);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static string Authorize(string? extra, string scope = "openid profile") =>
        $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}" +
        $"&response_type=code&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
        $"&scope={Uri.EscapeDataString(scope)}&state=xyz" +
        $"&code_challenge={Challenge(Verifier)}&code_challenge_method=S256" +
        (string.IsNullOrEmpty(extra) ? "" : $"&{extra}");

    private async Task LoginAsync(string email = "test@example.com")
    {
        var login = await _client.PostAsJsonAsync("/api/auth/login", new { email, password = "Test1234!" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    private async Task<Dictionary<string, string>> AuthorizeAndReadIdTokenAsync(
        string? extra, string scope = "openid profile")
    {
        var response = await _client.GetAsync(Authorize(extra, scope));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var code = HttpUtility.ParseQueryString(new Uri(response.Headers.Location!.ToString()).Query)["code"];
        Assert.NotNull(code);
        return await RedeemAsync(code!);
    }

    private async Task<Dictionary<string, string>> RedeemAsync(string code)
    {
        var token = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = AuthagonalTestFactory.TestClientId,
            ["code_verifier"] = Verifier,
        }));
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);

        var idToken = (await token.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id_token").GetString()!;
        var jwt = new JsonWebToken(idToken);
        return jwt.Claims
            .GroupBy(c => c.Type)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
    }

    private static Dictionary<string, string> BasePushedFields() => new()
    {
        ["client_id"] = AuthagonalTestFactory.TestClientId,
        ["response_type"] = "code",
        ["redirect_uri"] = RedirectUri,
        ["scope"] = "openid profile",
        ["state"] = "xyz",
        ["code_challenge"] = Challenge(Verifier),
        ["code_challenge_method"] = "S256",
    };

    private static string Challenge(string verifier)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(verifier));
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
