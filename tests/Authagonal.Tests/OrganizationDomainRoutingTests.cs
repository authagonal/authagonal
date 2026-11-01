using System.Collections.Specialized;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Protocol.Services;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Authagonal.Tests;

/// <summary>
/// An organisation a host is pinned to is authoritative for the authorization requests arriving on it:
/// <c>/connect/authorize</c> and <c>/connect/par</c>.
/// </summary>
/// <remarks>
/// Two halves, and the second is the one that matters. Supplying the organisation the endpoints would
/// otherwise have to be told is a convenience; refusing a request that names a DIFFERENT organisation is
/// the security property, because without it any relying party could send its own <c>organization</c> and
/// a host pinned to organisation A would mint organisation-B tokens.
/// <para>
/// Ported from the cloud's test class of the same name, method for method. These drive the library's real
/// <c>/connect/authorize</c> and <c>/connect/par</c> on the test host (<see cref="AuthagonalTestFactory"/>)
/// with the production <see cref="DefaultTenantContext"/>, so a pin is what an operator sets:
/// <c>Organizations:PinnedHosts</c>. Where a cloud test's premise is cloud-only the test of that name
/// asserts the library's equivalent: a domain record's organisation id is a <c>PinnedHosts</c> entry, a
/// tenant-level custom domain is a host with no entry, a platform subdomain is a host on a server with no
/// <c>Organizations</c> section at all, and an organisation id from another tenant is an id the tenant's
/// store does not hold. What the cloud's stand-in endpoints echoed back is read here off the library's own
/// answer: an unauthenticated authorize redirects to the login page carrying the request it would resume
/// as <c>returnUrl</c>, and a pushed request is read back from the grant store as the endpoint stored it.
/// </para>
/// </remarks>
public sealed class OrganizationDomainRoutingTests : IAsyncLifetime
{
    private const string AppClientId = "pin-app";
    private const string RedirectUri = "https://app.test/callback";

    private readonly AuthagonalTestFactory _factory = new() { UseDefaultTenantContext = true };
    private HttpClient? _client;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _client?.Dispose();
        return _factory.DisposeAsync().AsTask();
    }

    /// <summary>
    /// Starts the real host with these hosts pinned, exactly as an operator pins them. Organisations are
    /// seeded with <see cref="Seed"/> before or after; the pins must be given here, before the host reads
    /// its configuration.
    /// </summary>
    private async Task StartAsync(params (string Host, string OrganizationId)[] pins)
    {
        foreach (var (host, organizationId) in pins)
            _factory.Configuration[$"{DefaultTenantContext.PinnedHostsSection}:{host}"] = organizationId;

        await _factory.ClientStore.UpsertAsync(new OAuthClient
        {
            ClientId = AppClientId,
            ClientName = "Pinned app",
            RequireClientSecret = false,
            RequirePkce = false,
            AllowedGrantTypes = ["authorization_code"],
            AllowedScopes = ["openid"],
            RedirectUris = [RedirectUri],
        });
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
    }

    private void Seed(string id, string slug) => _factory.OrganizationStore.With(id, slug, "Org");

    private static string AuthorizeUrl(string path, string extra = "", bool withClient = true) =>
        $"{path}?{(withClient ? $"client_id={AppClientId}&" : "")}response_type=code&scope=openid&state=s" +
        $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}{extra}";

    private const string PushedForm =
        "client_id=" + AppClientId + "&response_type=code&scope=openid&redirect_uri=https%3A%2F%2Fapp.test%2Fcallback";

    private async Task<HttpResponseMessage> GetAsync(string host, string pathAndQuery)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, pathAndQuery);
        request.Headers.Host = host;
        return await _client!.SendAsync(request);
    }

    private async Task<HttpResponseMessage> PostFormAsync(string host, string path, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        request.Headers.Host = host;
        return await _client!.SendAsync(request);
    }

    /// <summary>
    /// Content whose length cannot be computed, so the request goes out with no <c>Content-Length</c> —
    /// the shape chunked transfer-encoding and HTTP/2 both produce, and the one the pin used to skip.
    /// </summary>
    private sealed class UnmeasuredContent(string body, string contentType) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(body);
            await stream.WriteAsync(bytes);
        }

        internal HttpContent WithContentType()
        {
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            return this;
        }
    }

    private async Task<HttpResponseMessage> PostUnmeasuredAsync(
        string host, string path, string body, string contentType = "application/x-www-form-urlencoded")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new UnmeasuredContent(body, contentType).WithContentType(),
        };
        request.Headers.Host = host;
        request.Headers.TransferEncodingChunked = true;
        return await _client!.SendAsync(request);
    }

    /// <summary>
    /// The query of the authorize request the login page would resume — what the endpoint saw AFTER the
    /// pin ran. An unauthenticated authorize redirects to the login page with exactly that request as its
    /// <c>returnUrl</c>, so an organisation the pin injected, or left alone, is read off it.
    /// </summary>
    private static NameValueCollection ResumedRequest(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        var returnUrl = HttpUtility.ParseQueryString(new Uri("http://login.invalid" + location).Query)["returnUrl"];
        Assert.False(string.IsNullOrEmpty(returnUrl), $"the redirect {location} carries no returnUrl");
        var at = returnUrl.IndexOf('?');
        return HttpUtility.ParseQueryString(at < 0 ? "" : returnUrl[(at + 1)..]);
    }

    /// <summary>The request a PAR 201 stored, as the authorize call would later read it back.</summary>
    private async Task<(string RequestUri, Dictionary<string, string[]> Parameters)> StoredPushedRequestAsync(
        HttpResponseMessage created)
    {
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var requestUri = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("request_uri").GetString()!;

        using var scope = _factory.Services.CreateScope();
        var record = await scope.ServiceProvider.GetRequiredService<ProtocolPushedAuthorizationService>()
            .LoadAsync(requestUri, AppClientId);
        Assert.NotNull(record);
        return (requestUri, record.Parameters);
    }

    /// <summary>What the production tenant context says for a request to <paramref name="hostHeader"/>.</summary>
    private (string? OrganizationId, bool Authoritative) PinFor(string hostHeader)
    {
        var accessor = _factory.Services.GetRequiredService<IHttpContextAccessor>();
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(hostHeader);
        accessor.HttpContext = context;
        try
        {
            var tenant = _factory.Services.GetRequiredService<ITenantContext>();
            Assert.IsType<DefaultTenantContext>(tenant);
            return (tenant.OrganizationId, tenant.OrganizationIdIsAuthoritative);
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? "");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("access_denied", json.GetProperty("error").GetString());
        Assert.Equal(
            "The requested organization is not available on this domain.",
            json.GetProperty("error_description").GetString());
    }

    // ── The pin entry ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A pin is a <c>PinnedHosts</c> entry: the tenant context reads it back for its host, and it rides
    /// all the way to the endpoint rather than stopping at the context.
    /// </summary>
    [Fact]
    public async Task DomainRecord_WithAnOrganizationId_RoundTripsAndReachesDomainRouting()
    {
        Seed("Org_7fA2", "acme-eu");
        await StartAsync(("pinned.example.com", "Org_7fA2"));

        var (organizationId, authoritative) = PinFor("pinned.example.com");
        Assert.Equal("Org_7fA2", organizationId);
        Assert.True(authoritative);

        var resumed = ResumedRequest(await GetAsync("pinned.example.com", AuthorizeUrl("/connect/authorize")));
        Assert.Equal("Org_7fA2", resumed["organization"]);
    }

    /// <summary>
    /// An entry with no organisation — the value the section holds when an operator blanks it out — reads
    /// back as no pin, which is the same answer a host with no entry gives. This is what makes the section
    /// backward compatible with no migration.
    /// </summary>
    [Fact]
    public async Task DomainRecord_WithoutAnOrganizationId_RoundTripsAsNull()
    {
        await StartAsync(("plain.example.com", ""));

        Assert.Equal((null, false), PinFor("plain.example.com"));
        Assert.Equal((null, false), PinFor("unlisted.example.com"));
    }

    // ── Context population ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Middleware_PinnedCustomDomain_SetsTheContextOrganizationFromTheDomain()
    {
        Seed("Org_7fA2", "acme-eu");
        await StartAsync(("eu.acme.com", "Org_7fA2"));

        var tenant = _factory.Services.GetRequiredService<ITenantContext>();
        var (organizationId, authoritative) = PinFor("eu.acme.com");

        Assert.Equal("default", tenant.TenantId);
        Assert.Equal("Org_7fA2", organizationId);
        Assert.True(authoritative);
    }

    [Fact]
    public async Task Middleware_TenantLevelCustomDomain_LeavesTheContextOrganizationNull()
    {
        Seed("Org_7fA2", "acme-eu");
        await StartAsync(("eu.acme.com", "Org_7fA2"));

        // A host with no entry is a tenant-level host: no organisation, nothing authoritative.
        var (organizationId, authoritative) = PinFor("auth.acme.com");

        Assert.Null(organizationId);
        Assert.False(authoritative);
    }

    /// <summary>
    /// A platform subdomain addresses the tenant as a whole. In the library that is a server with no
    /// <c>Organizations</c> section at all: no host is pinned, whatever it is called.
    /// </summary>
    [Fact]
    public async Task Middleware_PlatformSubdomain_NeverCarriesAnOrganization()
    {
        Seed("Org_7fA2", "acme-eu");
        await StartAsync();

        Assert.Equal((null, false), PinFor("acme.authagonal.test"));
        Assert.Equal((null, false), PinFor("eu.acme.com"));
    }

    // ── GET /connect/authorize ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Authorize_OnAPinnedDomainNamingNoOrganization_HasItInjected()
    {
        Seed("Org_7fA2", "acme-eu");
        await StartAsync(("eu.acme.com", "Org_7fA2"));

        var response = await GetAsync("eu.acme.com", AuthorizeUrl("/connect/authorize"));
        var resumed = ResumedRequest(response);

        Assert.Equal(["Org_7fA2"], resumed.GetValues("organization"));
        // The rest of the request is untouched.
        Assert.Equal(AppClientId, resumed["client_id"]);
        Assert.Equal("code", resumed["response_type"]);
        Assert.Equal(RedirectUri, resumed["redirect_uri"]);
    }

    /// <summary>A blank organisation parameter names none, so the pin replaces it rather than sitting beside it.</summary>
    [Theory]
    [InlineData("organization")]
    [InlineData("org_slug")]
    [InlineData("org_id")]
    public async Task Authorize_OnAPinnedDomainCarryingABlankAlias_HasItReplacedByThePin(string alias)
    {
        Seed("Org_7fA2", "acme-eu");
        await StartAsync(("eu.acme.com", "Org_7fA2"));

        var response = await GetAsync("eu.acme.com", AuthorizeUrl("/connect/authorize", $"&{alias}="));
        var resumed = ResumedRequest(response);

        Assert.Equal(["Org_7fA2"], resumed.GetValues("organization"));
        Assert.DoesNotContain("org_slug", resumed.AllKeys!);
        Assert.DoesNotContain("org_id", resumed.AllKeys!);
    }

    [Theory]
    [InlineData("organization")]
    [InlineData("org_slug")]
    [InlineData("org_id")]
    public async Task PushedAuthorization_OnAPinnedDomainCarryingABlankAlias_HasItReplacedByThePin(string alias)
    {
        Seed("Org_7fA2", "acme-eu");
        await StartAsync(("eu.acme.com", "Org_7fA2"));

        var response = await PostFormAsync("eu.acme.com", "/connect/par", PushedForm + $"&{alias}=");
        var (_, stored) = await StoredPushedRequestAsync(response);

        Assert.Equal(["Org_7fA2"], stored["organization"]);
        Assert.False(stored.ContainsKey("org_slug"));
        Assert.False(stored.ContainsKey("org_id"));
    }

    [Fact]
    public async Task Authorize_OnATenantLevelDomain_IsNotTouched()
    {
        Seed("Org_7fA2", "acme-eu");
        await StartAsync(("eu.acme.com", "Org_7fA2"));

        var resumed = ResumedRequest(await GetAsync("auth.acme.com", AuthorizeUrl("/connect/authorize")));

        Assert.Null(resumed["organization"]);
        Assert.DoesNotContain("organization", resumed.AllKeys!);
    }

    /// <summary>
    /// A relying party naming the pinned organisation by its SLUG while the host pins its ID names the
    /// same organisation, so it must pass through. Comparing the raw strings would refuse it.
    /// </summary>
    [Fact]
    public async Task Authorize_NamingThePinnedOrganizationBySlug_PassesThroughUnchanged()
    {
        Seed("Org_7fA2", "acme-eu");
        await StartAsync(("eu.acme.com", "Org_7fA2"));

        var response = await GetAsync("eu.acme.com", AuthorizeUrl("/connect/authorize", "&organization=acme-eu"));
        var resumed = ResumedRequest(response);

        // Not refused, and not injected a second time beside the one the relying party named.
        Assert.Equal(["acme-eu"], resumed.GetValues("organization"));
    }

    /// <summary>
    /// The trailing-slash row is not cosmetic: ASP.NET routing matches "/connect/authorize/" to the
    /// endpoint's MapGet("/connect/authorize"), so a raw path comparison let one extra character carry a
    /// request for another organisation straight to the real authorize endpoint, unpinned and unrefused.
    /// </summary>
    [Theory]
    [InlineData("/connect/authorize")]
    [InlineData("/connect/authorize/")]
    public async Task Authorize_NamingADifferentOrganization_IsRefusedWithAccessDenied(string path)
    {
        Seed("Org_7fA2", "acme-eu");
        Seed("Org_9zB4", "acme-us");
        await StartAsync(("eu.acme.com", "Org_7fA2"));

        var response = await GetAsync("eu.acme.com", AuthorizeUrl(path, "&organization=acme-us"));

        await AssertRefusedAsync(response);
        Assert.Null(response.Headers.Location);
    }

    /// <summary>The injecting half of the same normalisation.</summary>
    [Fact]
    public async Task Authorize_WithATrailingSlash_IsStillPinned()
    {
        Seed("Org_7fA2", "acme-eu");
        await StartAsync(("eu.acme.com", "Org_7fA2"));

        var resumed = ResumedRequest(await GetAsync("eu.acme.com", AuthorizeUrl("/connect/authorize/")));

        Assert.Equal(["Org_7fA2"], resumed.GetValues("organization"));
    }

    [Fact]
    public async Task PushedAuthorization_WithATrailingSlash_NamingADifferentOrganization_IsRefused()
    {
        Seed("Org_7fA2", "acme-eu");
        Seed("Org_9zB4", "acme-us");
        await StartAsync(("eu.acme.com", "Org_7fA2"));

        var response = await PostFormAsync("eu.acme.com", "/connect/par/", PushedForm + "&organization=acme-us");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertRefusedAsync(response);
    }

    /// <summary>
    /// <c>org_slug</c> and <c>org_id</c> are aliases the endpoints accept, so a pin that only inspected
    /// <c>organization</c> would be bypassable by sending one of the other two.
    /// </summary>
    [Theory]
    [InlineData("org_slug")]
    [InlineData("org_id")]
    public async Task Authorize_NamingADifferentOrganizationThroughAnAlias_IsAlsoRefused(string alias)
    {
        Seed("Org_7fA2", "acme-eu");
        Seed("Org_9zB4", "acme-us");
        await StartAsync(("eu.acme.com", "Org_7fA2"));

        var response = await GetAsync("eu.acme.com", AuthorizeUrl("/connect/authorize", $"&{alias}=acme-us"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertRefusedAsync(response);
    }

    /// <summary>
    /// An organisation that does not exist at all is a different organisation from the pinned one, so the
    /// refusal is the same. It is also the case that would otherwise slip through a resolve-then-compare
    /// that treated an unresolvable value as "no organisation named".
    /// </summary>
    [Fact]
    public async Task Authorize_NamingAnOrganizationThatDoesNotExist_IsRefused()
    {
        Seed("Org_7fA2", "acme-eu");
        await StartAsync(("eu.acme.com", "Org_7fA2"));

        var response = await GetAsync("eu.acme.com", AuthorizeUrl("/connect/authorize", "&organization=nope"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertRefusedAsync(response);
    }

    /// <summary>
    /// A pushed request resumes with a <c>request_uri</c> and the endpoint then reads its parameters from
    /// the stored payload, never from this query string. Appending here would be a silent no-op, so it
    /// must not happen — the pin for that leg is applied at /connect/par.
    /// </summary>
    [Fact]
    public async Task Authorize_ResumingAPushedRequest_DoesNotInjectIntoTheQueryString()
    {
        Seed("Org_7fA2", "acme-eu");
        await StartAsync(("eu.acme.com", "Org_7fA2"));

        // Pushed on a host with no pin, so the stored request names no organisation, then resumed on the
        // pinned host: neither injected nor refused.
        var (requestUri, stored) = await StoredPushedRequestAsync(
            await PostFormAsync("auth.acme.com", "/connect/par", PushedForm));
        Assert.DoesNotContain("organization", stored.Keys);

        var response = await GetAsync(
            "eu.acme.com", $"/connect/authorize?client_id={AppClientId}&request_uri={Uri.EscapeDataString(requestUri)}");
        var resumed = ResumedRequest(response);

        Assert.Equal(requestUri, resumed["request_uri"]);
        Assert.Null(resumed["organization"]);
    }

    /// <summary>
    /// Deleting an organisation deliberately does not delete the hosts pinned to it. A dangling pin is
    /// treated as no pin at all: injecting an id the endpoint would refuse would fail every login on the
    /// hostname closed, which is a far worse outcome than serving it as the tenant-level host it was
    /// before.
    /// </summary>
    [Fact]
    public async Task Authorize_OnADomainPinnedToADeletedOrganization_IsServedAsTenantLevel()
    {
        // No organisation seeded: the pin names one that is not there.
        await StartAsync(("eu.acme.com", "Org_Gone"));

        var response = await GetAsync("eu.acme.com", AuthorizeUrl("/connect/authorize"));
        var resumed = ResumedRequest(response);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Null(resumed["organization"]);
    }

    /// <summary>
    /// ...and a dangling pin does not turn into a refusal either: a request that names an organisation on
    /// such a host is an ordinary unpinned request.
    /// </summary>
    [Fact]
    public async Task Authorize_NamingAnOrganizationOnADomainPinnedToADeletedOne_IsNotRefused()
    {
        Seed("Org_9zB4", "acme-us");
        await StartAsync(("eu.acme.com", "Org_Gone"));

        var response = await GetAsync("eu.acme.com", AuthorizeUrl("/connect/authorize", "&organization=acme-us"));
        var resumed = ResumedRequest(response);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(["acme-us"], resumed.GetValues("organization"));
    }

    // ── POST /connect/par ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PushedAuthorization_OnAPinnedDomainNamingNoOrganization_HasItInjectedIntoTheForm()
    {
        Seed("Org_7fA2", "acme-eu");
        await StartAsync(("eu.acme.com", "Org_7fA2"));

        var response = await PostFormAsync("eu.acme.com", "/connect/par", PushedForm);
        var (_, stored) = await StoredPushedRequestAsync(response);

        Assert.Equal(["Org_7fA2"], stored["organization"]);
        // The fields the client pushed are all still there, plus the one that was added. client_id is
        // authenticated by the endpoint and so is not stored.
        Assert.Equal(["code"], stored["response_type"]);
        Assert.Equal(["openid"], stored["scope"]);
        Assert.Equal([RedirectUri], stored["redirect_uri"]);
        Assert.Equal(4, stored.Count);
    }

    [Fact]
    public async Task PushedAuthorization_NamingADifferentOrganization_IsRefusedWithAccessDenied()
    {
        Seed("Org_7fA2", "acme-eu");
        Seed("Org_9zB4", "acme-us");
        await StartAsync(("eu.acme.com", "Org_7fA2"));

        var response = await PostFormAsync("eu.acme.com", "/connect/par", PushedForm + "&organization=acme-us");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertRefusedAsync(response);
    }

    /// <summary>
    /// A pushed request that already names the pinned organisation is handed on with its body intact —
    /// the middleware read that body to check it, so proving the endpoint can still read it is the point
    /// of this one.
    /// </summary>
    [Fact]
    public async Task PushedAuthorization_NamingThePinnedOrganization_ReachesTheEndpointWithItsBodyIntact()
    {
        Seed("Org_7fA2", "acme-eu");
        await StartAsync(("eu.acme.com", "Org_7fA2"));

        var response = await PostFormAsync("eu.acme.com", "/connect/par", PushedForm + "&organization=acme-eu");
        var (_, stored) = await StoredPushedRequestAsync(response);

        Assert.Equal(["acme-eu"], stored["organization"]);
        Assert.Equal(["code"], stored["response_type"]);
        Assert.Equal(["openid"], stored["scope"]);
        Assert.Equal([RedirectUri], stored["redirect_uri"]);
        Assert.Equal(4, stored.Count);
    }

    /// <summary>
    /// Chunked transfer-encoding and HTTP/2 both routinely omit Content-Length, and the pin used to skip
    /// any body it could not measure up front — so this, the most ordinary shape of a pushed request, went
    /// through unpinned.
    /// </summary>
    [Fact]
    public async Task PushedAuthorization_WithNoContentLength_IsStillPinned()
    {
        Seed("Org_7fA2", "acme-eu");
        await StartAsync(("eu.acme.com", "Org_7fA2"));

        var response = await PostUnmeasuredAsync("eu.acme.com", "/connect/par", PushedForm);
        var (_, stored) = await StoredPushedRequestAsync(response);

        Assert.Equal(["Org_7fA2"], stored["organization"]);
        Assert.Equal(["openid"], stored["scope"]);
        Assert.Equal(4, stored.Count);
    }

    /// <summary>...and the half that actually mattered: unmeasured bodies were not refused either.</summary>
    [Fact]
    public async Task PushedAuthorization_WithNoContentLength_NamingADifferentOrganization_IsRefused()
    {
        Seed("Org_7fA2", "acme-eu");
        Seed("Org_9zB4", "acme-us");
        await StartAsync(("eu.acme.com", "Org_7fA2"));

        var response = await PostUnmeasuredAsync(
            "eu.acme.com", "/connect/par", PushedForm + "&organization=acme-us");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertRefusedAsync(response);
    }

    /// <summary>
    /// multipart/form-data satisfies HasFormContentType but its fields are invisible to the query-string
    /// parser the pin uses — so a multipart organisation read as "names none", passed the conflict check,
    /// and was then parsed perfectly well by the endpoint's own form reader.
    /// </summary>
    [Fact]
    public async Task PushedAuthorization_WithAMultipartBody_IsRefused()
    {
        Seed("Org_7fA2", "acme-eu");
        Seed("Org_9zB4", "acme-us");
        await StartAsync(("eu.acme.com", "Org_7fA2"));

        var multipart = new MultipartFormDataContent("boundary1")
        {
            { new StringContent(AppClientId), "client_id" },
            { new StringContent("acme-us"), "organization" },
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/par") { Content = multipart };
        request.Headers.Host = "eu.acme.com";

        var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertRefusedAsync(response);
    }

    /// <summary>
    /// A body too large to inspect is refused rather than forwarded unexamined, and carries the same body
    /// as every other refusal — a caller must not be able to learn which check fired, and so which shape
    /// of request goes uninspected.
    /// </summary>
    [Fact]
    public async Task PushedAuthorization_WithABodyOverTheBound_IsRefused()
    {
        Seed("Org_7fA2", "acme-eu");
        await StartAsync(("eu.acme.com", "Org_7fA2"));

        // Unmeasured, so it cannot be left to the endpoint's Content-Length 413 and must be bounded here.
        var oversized = $"client_id={AppClientId}&padding=" + new string('x', 33 * 1024);
        var response = await PostUnmeasuredAsync("eu.acme.com", "/connect/par", oversized);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertRefusedAsync(response);
    }

    /// <summary>
    /// A DECLARED over-bound length is still left to the endpoint, which answers it with the 413 it
    /// already has — reading it here to decide would be the flood that bound exists to prevent.
    /// </summary>
    [Fact]
    public async Task PushedAuthorization_WithADeclaredOverBoundLength_IsLeftToTheEndpoint()
    {
        Seed("Org_7fA2", "acme-eu");
        await StartAsync(("eu.acme.com", "Org_7fA2"));

        var oversized = $"client_id={AppClientId}&padding=" + new string('x', 33 * 1024);
        var response = await PostFormAsync("eu.acme.com", "/connect/par", oversized);

        // The endpoint's own 413, not the pin's 400 — the request reached it untouched.
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PushedAuthorization_OnATenantLevelDomain_IsNotTouched()
    {
        Seed("Org_7fA2", "acme-eu");
        await StartAsync(("eu.acme.com", "Org_7fA2"));

        var response = await PostFormAsync("auth.acme.com", "/connect/par", PushedForm);
        var (_, stored) = await StoredPushedRequestAsync(response);

        Assert.DoesNotContain("organization", stored.Keys);
        Assert.Equal(3, stored.Count);
    }

    // ── Cross-tenant ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The organisation lookup runs through the tenant's own store, so an organisation id another tenant
    /// holds does not resolve here: in the library, an id the tenant's store does not hold. The same
    /// pinned server answers both hosts in one test, so the difference can only be whether the store
    /// holds the id.
    /// </summary>
    [Fact]
    public async Task Authorize_WithAnOrganizationIdFromAnotherTenant_DoesNotResolveOnThisTenantsHost()
    {
        // The organisation exists in THIS tenant's store only; the other host pins an id that lives in
        // some other tenant's store, which this one does not hold.
        Seed("Org_Shared", "shared");
        await StartAsync(("auth.alpha.com", "Org_Shared"), ("auth.beta.com", "Org_OtherTenants"));

        var onAlpha = ResumedRequest(await GetAsync("auth.alpha.com", AuthorizeUrl("/connect/authorize")));
        var onBeta = ResumedRequest(await GetAsync("auth.beta.com", AuthorizeUrl("/connect/authorize")));

        // The store holds it, so the pin applies.
        Assert.Equal(["Org_Shared"], onAlpha.GetValues("organization"));
        // It does not, so the pin is dangling and nothing is injected — beta can never mint a token for
        // an organisation its tenant does not hold.
        Assert.Null(onBeta["organization"]);
    }
}
