using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// The domain and backfill routes of the organisation admin API over HTTP, and the SCIM token endpoint's
/// <c>unknown_organization</c> refusal, against a host whose DNS is a <see cref="FakeTxtResolver"/>.
/// </summary>
public sealed class OrganizationDomainBackfillEndpointTests : IAsyncLifetime
{
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;
    private string _adminToken = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        await _factory.SeedTestDataAsync();
        _adminToken = await _factory.GetAdminTokenAsync(_client);
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    private Task<HttpResponseMessage> AdminAsync(HttpMethod method, string url, object? body = null, string? rawBody = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);
        if (rawBody is not null)
            request.Content = new StringContent(rawBody, Encoding.UTF8, "application/json");
        else if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return _client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private async Task<string> CreateOrgAsync(string slug)
    {
        var response = await AdminAsync(HttpMethod.Post, "/api/v1/organizations", new { slug, name = $"Name {slug}" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> ClaimAsync(string orgId, string domain)
    {
        var response = await AdminAsync(HttpMethod.Post, $"/api/v1/organizations/{orgId}/domains", new { domain });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await JsonAsync(response);
    }

    // ---- domains -----------------------------------------------------------------------------------

    [Fact]
    public async Task AddDomain_Answers201WithLocationAndTheRecordToPublish()
    {
        var id = await CreateOrgAsync("acme");

        var response = await AdminAsync(HttpMethod.Post, $"/api/v1/organizations/{id}/domains", new { domain = "Acme.com" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.EndsWith($"/api/v1/organizations/{id}/domains/acme.com", response.Headers.Location!.ToString());
        var json = await JsonAsync(response);
        Assert.Equal("acme.com", json.GetProperty("domain").GetString());
        Assert.False(json.GetProperty("verified").GetBoolean());
        Assert.Equal("_authagonal-org.acme.com", json.GetProperty("recordName").GetString());
        Assert.StartsWith("authagonal-org-verify=", json.GetProperty("recordValue").GetString());
        Assert.Contains(_factory.AuditLog.Entries, e => e.Action == "organization.domain_added" && e.EntityId == id);
    }

    [Fact]
    public async Task AddDomain_Refusals_CarryTheirCodes()
    {
        var first = await CreateOrgAsync("first");
        var second = await CreateOrgAsync("second");
        await ClaimAsync(first, "acme.com");

        var invalid = await AdminAsync(HttpMethod.Post, $"/api/v1/organizations/{first}/domains", new { domain = "https://acme.com" });
        var exists = await AdminAsync(HttpMethod.Post, $"/api/v1/organizations/{first}/domains", new { domain = "acme.com" });
        var taken = await AdminAsync(HttpMethod.Post, $"/api/v1/organizations/{second}/domains", new { domain = "acme.com" });
        var missing = await AdminAsync(HttpMethod.Post, "/api/v1/organizations/org_missing/domains", new { domain = "acme.com" });

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("domain_invalid", (await JsonAsync(invalid)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Conflict, exists.StatusCode);
        Assert.Equal("domain_exists", (await JsonAsync(exists)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal("domain_taken", (await JsonAsync(taken)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("organization_not_found", (await JsonAsync(missing)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task VerifyDomain_Miss_Is409VerificationFailed_ThenHitIs200Verified_ThenRepeatIs200()
    {
        var id = await CreateOrgAsync("acme");
        var claim = await ClaimAsync(id, "acme.com");

        var miss = await AdminAsync(HttpMethod.Post, $"/api/v1/organizations/{id}/domains/acme.com/verify");
        Assert.Equal(HttpStatusCode.Conflict, miss.StatusCode);
        var error = await JsonAsync(miss);
        Assert.Equal("verification_failed", error.GetProperty("error").GetString());
        Assert.Contains(claim.GetProperty("recordValue").GetString()!, error.GetProperty("error_description").GetString());

        _factory.TxtResolver.Publish(claim.GetProperty("recordName").GetString()!, claim.GetProperty("recordValue").GetString()!);
        var hit = await AdminAsync(HttpMethod.Post, $"/api/v1/organizations/{id}/domains/acme.com/verify");
        Assert.Equal(HttpStatusCode.OK, hit.StatusCode);
        Assert.True((await JsonAsync(hit)).GetProperty("verified").GetBoolean());

        var again = await AdminAsync(HttpMethod.Post, $"/api/v1/organizations/{id}/domains/acme.com/verify");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Single(_factory.AuditLog.Entries, e => e.Action == "organization.domain_verified");
    }

    [Fact]
    public async Task VerifyAndRemove_UnknownDomain_Are404DomainNotFound()
    {
        var id = await CreateOrgAsync("acme");

        var verify = await AdminAsync(HttpMethod.Post, $"/api/v1/organizations/{id}/domains/nothere.com/verify");
        var remove = await AdminAsync(HttpMethod.Delete, $"/api/v1/organizations/{id}/domains/nothere.com");

        Assert.Equal(HttpStatusCode.NotFound, verify.StatusCode);
        Assert.Equal("domain_not_found", (await JsonAsync(verify)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.NotFound, remove.StatusCode);
        Assert.Equal("domain_not_found", (await JsonAsync(remove)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task RemoveDomain_Answers204_AndTheDomainIsGone()
    {
        var id = await CreateOrgAsync("acme");
        await ClaimAsync(id, "acme.com");

        var response = await AdminAsync(HttpMethod.Delete, $"/api/v1/organizations/{id}/domains/acme.com");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty((await _factory.OrganizationStore.GetAsync(id))!.Domains);
        Assert.Contains(_factory.AuditLog.Entries, e => e.Action == "organization.domain_removed" && e.EntityId == id);
    }

    [Fact]
    public async Task DomainRoutes_WithoutAToken_Are401()
    {
        var id = await CreateOrgAsync("acme");

        var response = await _client.PostAsync($"/api/v1/organizations/{id}/domains",
            new StringContent("""{"domain":"acme.com"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- backfill ----------------------------------------------------------------------------------

    private Task SeedLegacyUserAsync(string id, string legacyOrg)
    {
        var email = $"{id}@legacy.test";
        return _factory.UserStore.CreateAsync(new AuthUser
        {
            Id = id,
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            OrganizationId = legacyOrg,
            CreatedAt = DateTimeOffset.UtcNow,
        });
    }

    [Fact]
    public async Task Backfill_WithNoBody_IsADryRun()
    {
        await SeedLegacyUserAsync("legacy-1", "Legacy Co");
        var before = (await _factory.OrganizationStore.ListAsync()).Count;

        var response = await AdminAsync(HttpMethod.Post, "/api/v1/organizations/backfill");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await JsonAsync(response);
        Assert.True(json.GetProperty("dryRun").GetBoolean());
        Assert.Equal(1, json.GetProperty("organizationsCreated").GetInt32());
        Assert.Equal(1, json.GetProperty("membershipsCreated").GetInt32());
        Assert.False(json.GetProperty("truncated").GetBoolean());
        Assert.Equal(before, (await _factory.OrganizationStore.ListAsync()).Count);
        Assert.DoesNotContain(_factory.AuditLog.Entries, e => e.Action == "organization.backfill");
    }

    [Fact]
    public async Task Backfill_DryRunFalse_Writes_AndASecondRunCreatesNothing()
    {
        await SeedLegacyUserAsync("legacy-1", "Legacy Co");

        var first = await AdminAsync(HttpMethod.Post, "/api/v1/organizations/backfill", new { dryRun = false });
        var second = await AdminAsync(HttpMethod.Post, "/api/v1/organizations/backfill", new { dryRun = false });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstJson = await JsonAsync(first);
        Assert.False(firstJson.GetProperty("dryRun").GetBoolean());
        Assert.Equal(1, firstJson.GetProperty("organizationsCreated").GetInt32());
        Assert.NotNull(await _factory.OrganizationStore.GetBySlugAsync("legacy-co"));
        var secondJson = await JsonAsync(second);
        Assert.Equal(0, secondJson.GetProperty("organizationsCreated").GetInt32());
        Assert.Equal(0, secondJson.GetProperty("membershipsCreated").GetInt32());
        Assert.Equal(2, _factory.AuditLog.Entries.Count(e => e.Action == "organization.backfill"));
    }

    // ---- SCIM token creation names an organisation -------------------------------------------------

    [Fact]
    public async Task CreateScimToken_UnknownOrganization_Is400UnknownOrganization_AndMintsNothing()
    {
        await _factory.ClientStore.UpsertAsync(new OAuthClient { ClientId = "scim-org-client", ClientName = "SCIM Org" });

        var response = await AdminAsync(HttpMethod.Post, "/api/v1/scim/tokens",
            new { clientId = "scim-org-client", description = "t", organizationId = "org_nonexistent" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await JsonAsync(response);
        Assert.Equal("unknown_organization", json.GetProperty("error").GetString());
        Assert.Contains("org_nonexistent", json.GetProperty("error_description").GetString());
        var list = await AdminAsync(HttpMethod.Get, "/api/v1/scim/tokens?clientId=scim-org-client");
        Assert.Equal(0, (await JsonAsync(list)).GetProperty("tokens").GetArrayLength());
    }

    [Fact]
    public async Task CreateScimToken_KnownOrganization_StillSucceeds()
    {
        var id = await CreateOrgAsync("scim-acme");
        await _factory.ClientStore.UpsertAsync(new OAuthClient { ClientId = "scim-org-client", ClientName = "SCIM Org" });

        var response = await AdminAsync(HttpMethod.Post, "/api/v1/scim/tokens",
            new { clientId = "scim-org-client", description = "t", organizationId = id });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull((await JsonAsync(response)).GetProperty("token").GetString());
    }
}
