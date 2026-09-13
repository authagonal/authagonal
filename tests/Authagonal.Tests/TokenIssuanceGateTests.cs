using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Web;
using Authagonal.Core.Constants;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// The per-(user, client, request) issuance veto: <see cref="IAuthHook.OnTokenIssuingAsync"/> now
/// fires from the three interactive mints — authorization_code, refresh_token and device_code —
/// with a resolved subject, and throwing from it refuses the issuance with <c>access_denied</c>.
/// </summary>
/// <remarks>
/// Before this, a host could not refuse a token for a PERSON on any interactive grant.
/// <c>OnTokenIssuedAsync</c> is documented "Throw to reject" but the token endpoint calls it with a
/// null subject, by design, because it runs before the grant is redeemed; and
/// <c>OnTokenIssuingAsync</c>, which does carry the subject, fired from exactly two agentic mints.
/// So the one question an authorization server is asked most often — may this person have a token
/// for this application right now — had nowhere to be answered.
/// </remarks>
public sealed class TokenIssuanceGateTests : IAsyncLifetime
{
    private const string RedirectUri = "https://app.test/callback";

    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;
    private AuthUser _user = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
        _user = await _factory.SeedTestUserAsync();

        // The seeded client allows authorization_code and refresh_token only; the device grant is the
        // third interactive path this gate now covers, so it has to be allowed here.
        var client = await _factory.ClientStore.GetAsync(AuthagonalTestFactory.TestClientId);
        client!.AllowedGrantTypes = [.. client.AllowedGrantTypes, GrantTypes.DeviceCode];
        await _factory.ClientStore.UpsertAsync(client);

        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    // -----------------------------------------------------------------------
    // authorization_code
    // -----------------------------------------------------------------------

    [Fact]
    public async Task AuthorizationCode_HookSeesTheResolvedSubject()
    {
        var (code, verifier) = await AuthorizeAsync();
        var response = await RedeemCodeAsync(code, verifier);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var call = Assert.Single(
            _factory.AuthHook.IssuanceGateCalls, c => c.GrantType == GrantTypes.AuthorizationCode);
        Assert.Equal(_user.Id, call.SubjectId);
        Assert.Equal(AuthagonalTestFactory.TestClientId, call.ClientId);
        Assert.Contains("openid", call.Scopes);
    }

    [Fact]
    public async Task AuthorizationCode_VetoReturnsAccessDenied()
    {
        var (code, verifier) = await AuthorizeAsync();
        _factory.AuthHook.RefuseIssuance = _ => "not right now";

        var response = await RedeemCodeAsync(code, verifier);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("access_denied", json.GetProperty("error").GetString());
        Assert.Equal("not right now", json.GetProperty("error_description").GetString());
    }

    /// A hook that names its own OAuth error keeps it — a refusal that is really "this client may not"
    /// should not be flattened into access_denied.
    [Fact]
    public async Task AuthorizationCode_HookMayNameItsOwnError()
    {
        var (code, verifier) = await AuthorizeAsync();
        _factory.AuthHook.RefuseIssuance = null;
        _factory.AuthHook.ThrowProtocolError = ("unauthorized_client", "this client is suspended");

        var response = await RedeemCodeAsync(code, verifier);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unauthorized_client", json.GetProperty("error").GetString());
    }

    // -----------------------------------------------------------------------
    // refresh_token
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Refresh_VetoReturnsAccessDenied_AndLeavesTheTokenUnconsumed()
    {
        var refreshToken = await GetRefreshTokenAsync();

        _factory.AuthHook.RefuseIssuance = c => c.GrantType == GrantTypes.RefreshToken ? "no" : null;
        var refused = await RefreshAsync(refreshToken);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var json = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("access_denied", json.GetProperty("error").GetString());

        // The gate runs before the rotation, so refusing THIS issuance must not have consumed the
        // token or killed the family — a host saying "not now" is not a host ending the session.
        _factory.AuthHook.RefuseIssuance = null;
        var allowed = await RefreshAsync(refreshToken);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task Refresh_HookSeesTheResolvedSubject()
    {
        var refreshToken = await GetRefreshTokenAsync();
        _factory.AuthHook.IssuanceGateCalls.Clear();

        await RefreshAsync(refreshToken);

        var call = Assert.Single(_factory.AuthHook.IssuanceGateCalls);
        Assert.Equal(GrantTypes.RefreshToken, call.GrantType);
        Assert.Equal(_user.Id, call.SubjectId);
    }

    // -----------------------------------------------------------------------
    // device_code
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DeviceCode_VetoReturnsAccessDenied()
    {
        var deviceCode = await ApprovedDeviceCodeAsync();
        _factory.AuthHook.RefuseIssuance = c => c.GrantType == GrantTypes.DeviceCode ? "device refused" : null;

        var response = await PollDeviceAsync(deviceCode);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("access_denied", json.GetProperty("error").GetString());
        Assert.Equal("device refused", json.GetProperty("error_description").GetString());
    }

    [Fact]
    public async Task DeviceCode_HookSeesTheResolvedSubject()
    {
        var deviceCode = await ApprovedDeviceCodeAsync();
        _factory.AuthHook.IssuanceGateCalls.Clear();

        var response = await PollDeviceAsync(deviceCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var call = Assert.Single(_factory.AuthHook.IssuanceGateCalls);
        Assert.Equal(GrantTypes.DeviceCode, call.GrantType);
        Assert.Equal(_user.Id, call.SubjectId);
    }

    // -----------------------------------------------------------------------
    // No hook opinion = no change
    // -----------------------------------------------------------------------

    /// The default for every existing implementor. OnTokenIssuingAsync is a default interface member
    /// and none of them override it, so firing it from three more places must be invisible.
    [Fact]
    public async Task NoVeto_IsANoOp()
    {
        var (code, verifier) = await AuthorizeAsync();
        var response = await RedeemCodeAsync(code, verifier);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(json.GetProperty("access_token").GetString()));
    }

    /// client_credentials without an agent profile is one of the two agentic sites' conditions, and it
    /// must keep its current behaviour: the gate does NOT fire there.
    [Fact]
    public async Task ClientCredentials_WithoutAnAgentProfile_DoesNotFireTheGate()
    {
        _factory.AuthHook.IssuanceGateCalls.Clear();

        var response = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = GrantTypes.ClientCredentials,
                ["client_id"] = AuthagonalTestFactory.AdminClientId,
                ["client_secret"] = AuthagonalTestFactory.AdminClientSecret,
                ["scope"] = AuthagonalTestFactory.AdminScope,
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_factory.AuthHook.IssuanceGateCalls);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private async Task<(string Code, string Verifier)> AuthorizeAsync(string scope = "openid profile")
    {
        var (verifier, challenge) = Pkce();
        var url = $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}" +
            $"&response_type=code&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            $"&scope={Uri.EscapeDataString(scope)}&state=xyz" +
            $"&code_challenge={challenge}&code_challenge_method=S256";

        var response = await _client.GetAsync(url);
        var code = HttpUtility.ParseQueryString(new Uri(response.Headers.Location!.ToString()).Query)["code"];
        Assert.NotNull(code);
        return (code!, verifier);
    }

    private Task<HttpResponseMessage> RedeemCodeAsync(string code, string verifier) =>
        _client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = GrantTypes.AuthorizationCode,
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = AuthagonalTestFactory.TestClientId,
            ["code_verifier"] = verifier,
        }));

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken) =>
        _client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = GrantTypes.RefreshToken,
            ["refresh_token"] = refreshToken,
            ["client_id"] = AuthagonalTestFactory.TestClientId,
        }));

    private async Task<string> GetRefreshTokenAsync()
    {
        var (code, verifier) = await AuthorizeAsync("openid profile offline_access");
        var response = await RedeemCodeAsync(code, verifier);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("refresh_token").GetString()!;
    }

    private async Task<string> ApprovedDeviceCodeAsync()
    {
        var start = await _client.PostAsync("/connect/deviceauthorization", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["client_id"] = AuthagonalTestFactory.TestClientId,
                ["scope"] = "openid profile",
            }));
        start.EnsureSuccessStatusCode();
        var json = await start.Content.ReadFromJsonAsync<JsonElement>();
        var deviceCode = json.GetProperty("device_code").GetString()!;
        var userCode = json.GetProperty("user_code").GetString()!;

        var approve = await _client.PostAsync("/api/auth/device/approve", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["user_code"] = userCode }));
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        return deviceCode;
    }

    private Task<HttpResponseMessage> PollDeviceAsync(string deviceCode) =>
        _client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = GrantTypes.DeviceCode,
            ["device_code"] = deviceCode,
            ["client_id"] = AuthagonalTestFactory.TestClientId,
        }));

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (verifier, challenge);
    }
}
