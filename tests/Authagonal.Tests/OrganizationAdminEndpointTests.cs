using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Authagonal.Tests;

/// <summary>
/// The organisation and membership admin API over HTTP: authorisation, status codes, headers, the exact
/// JSON member names, the error shape, the audit trail, and the <c>AdminApi:Enabled</c> switch.
/// </summary>
public sealed class OrganizationAdminEndpointTests : IAsyncLifetime
{
    private const string LimitedClientId = "limited-client";
    private const string LimitedClientSecret = "limited-secret-789";

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

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static HttpRequestMessage Build(HttpMethod method, string url, string? token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }

    private Task<HttpResponseMessage> AdminAsync(HttpMethod method, string url, object? body = null) =>
        _client.SendAsync(Build(method, url, _adminToken, body));

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private async Task<JsonElement> CreateOrgAsync(string slug, string? name = null, object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["slug"] = slug, ["name"] = name ?? $"Name {slug}" };
        var response = await AdminAsync(HttpMethod.Post, "/api/v1/organizations", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await JsonAsync(response);
    }

    private static string[] Keys(JsonElement element) =>
        [.. element.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal)];

    /// <summary>A bearer token for a client that holds NO admin scope, so the 403 path is the policy and
    /// not a missing token.</summary>
    private async Task<string> GetLimitedTokenAsync()
    {
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();
        await _factory.ClientStore.UpsertAsync(new OAuthClient
        {
            ClientId = LimitedClientId,
            ClientName = "Limited Client",
            RequireClientSecret = true,
            RequirePkce = false,
            ClientSecretHashes = [hasher.HashPassword(LimitedClientSecret)],
            AllowedGrantTypes = ["client_credentials"],
            AllowedScopes = ["openid"],
            AccessTokenLifetimeSeconds = 3600,
        });

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = "openid",
        });
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{LimitedClientId}:{LimitedClientSecret}"));
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await JsonAsync(response);
        var token = json.GetProperty("access_token").GetString()!;
        // Guard the premise: the token must really lack the admin scope.
        Assert.DoesNotContain(AuthagonalTestFactory.AdminScope, json.GetProperty("scope").GetString()!.Split(' '));
        return token;
    }

    /// <summary>The ten routes: method and path, with throwaway ids.</summary>
    public static TheoryData<string, string> AllRoutes => new()
    {
        { "GET", "/api/v1/organizations" },
        { "POST", "/api/v1/organizations" },
        { "GET", "/api/v1/organizations/some-org" },
        { "PUT", "/api/v1/organizations/some-org" },
        { "DELETE", "/api/v1/organizations/some-org" },
        { "GET", "/api/v1/organizations/some-org/members" },
        { "POST", "/api/v1/organizations/some-org/members" },
        { "PUT", "/api/v1/organizations/some-org/members/some-user" },
        { "DELETE", "/api/v1/organizations/some-org/members/some-user" },
        { "GET", "/api/v1/profile/some-user/organizations" },
    };

    // -----------------------------------------------------------------------
    // Authorisation
    // -----------------------------------------------------------------------

    [Fact]
    public void TheRouteTableCoversTenRoutes()
    {
        Assert.Equal(10, AllRoutes.Count);
    }

    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task EveryRoute_WithNoToken_Answers401(string method, string path)
    {
        var response = await _client.SendAsync(Build(new HttpMethod(method), path, null, new { }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task EveryRoute_WithATokenLackingTheAdminScope_Answers403(string method, string path)
    {
        var token = await GetLimitedTokenAsync();

        var response = await _client.SendAsync(Build(new HttpMethod(method), path, token, new { }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ALimitedToken_ChangesNothing()
    {
        var token = await GetLimitedTokenAsync();

        var response = await _client.SendAsync(Build(HttpMethod.Post, "/api/v1/organizations", token,
            new { slug = "sneaky", name = "Sneaky" }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await _factory.OrganizationStore.ListAsync());
        Assert.Empty(_factory.AuditLog.Entries);
    }

    // -----------------------------------------------------------------------
    // Organisations
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Create_Answers201WithLocation_AndTheExactCamelCaseShape()
    {
        var response = await AdminAsync(HttpMethod.Post, "/api/v1/organizations",
            new { slug = "acme", name = "Acme Inc" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await JsonAsync(response);
        var id = json.GetProperty("id").GetString()!;
        Assert.StartsWith("org_", id);
        Assert.Equal($"/api/v1/organizations/{id}", response.Headers.Location!.OriginalString);

        Assert.Equal(
            new[]
            {
                "allowAutoMembership", "brandingJson", "createdAt", "domains", "enabled", "id", "metadata", "name",
                "requireMembershipForTokens", "slug", "updatedAt",
            },
            Keys(json));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("brandingJson").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("updatedAt").ValueKind);
        Assert.Equal("acme", json.GetProperty("slug").GetString());
        Assert.Equal("Acme Inc", json.GetProperty("name").GetString());
        Assert.True(json.GetProperty("enabled").GetBoolean());
        Assert.True(json.GetProperty("requireMembershipForTokens").GetBoolean());
        Assert.False(json.GetProperty("allowAutoMembership").GetBoolean());
        Assert.Equal(JsonValueKind.Object, json.GetProperty("metadata").ValueKind);
        Assert.Equal(JsonValueKind.Array, json.GetProperty("domains").ValueKind);
        Assert.Equal(JsonValueKind.String, json.GetProperty("createdAt").ValueKind);
    }

    [Fact]
    public async Task Create_HonoursTheOptionalFields()
    {
        var response = await AdminAsync(HttpMethod.Post, "/api/v1/organizations", new
        {
            slug = "opt",
            name = "Opt",
            brandingJson = "{\"logo\":\"x\"}",
            enabled = false,
            requireMembershipForTokens = false,
            allowAutoMembership = true,
            metadata = new { tier = "gold" },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await JsonAsync(response);
        Assert.Equal("{\"logo\":\"x\"}", json.GetProperty("brandingJson").GetString());
        Assert.False(json.GetProperty("enabled").GetBoolean());
        Assert.False(json.GetProperty("requireMembershipForTokens").GetBoolean());
        Assert.True(json.GetProperty("allowAutoMembership").GetBoolean());
        Assert.Equal("gold", json.GetProperty("metadata").GetProperty("tier").GetString());
    }

    [Fact]
    public async Task Create_MissingSlug_Answers400SlugRequired_WithADescription()
    {
        var response = await AdminAsync(HttpMethod.Post, "/api/v1/organizations", new { name = "No Slug" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await JsonAsync(response);
        Assert.Equal("slug_required", json.GetProperty("error").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("error_description").GetString()));
        Assert.False(json.TryGetProperty("fields", out _), "'fields' is omitted when the failure names none");
    }

    [Fact]
    public async Task Create_DuplicateSlug_Answers409SlugTaken()
    {
        await CreateOrgAsync("dup");

        var response = await AdminAsync(HttpMethod.Post, "/api/v1/organizations", new { slug = "dup", name = "Again" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("slug_taken", (await JsonAsync(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Get_ByIdAndBySlug_Answer200_AndUnknownAnswers404OrganizationNotFound()
    {
        var created = await CreateOrgAsync("findme");
        var id = created.GetProperty("id").GetString()!;

        var byId = await AdminAsync(HttpMethod.Get, $"/api/v1/organizations/{id}");
        var bySlug = await AdminAsync(HttpMethod.Get, "/api/v1/organizations/findme");
        var missing = await AdminAsync(HttpMethod.Get, "/api/v1/organizations/no-such-org");

        Assert.Equal(HttpStatusCode.OK, byId.StatusCode);
        Assert.Equal(id, (await JsonAsync(byId)).GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.OK, bySlug.StatusCode);
        Assert.Equal(id, (await JsonAsync(bySlug)).GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var error = await JsonAsync(missing);
        Assert.Equal("organization_not_found", error.GetProperty("error").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("error_description").GetString()));
    }

    [Fact]
    public async Task List_WithAMalformedCursor_Answers400InvalidCursor()
    {
        var response = await AdminAsync(HttpMethod.Get, "/api/v1/organizations?cursor=zzz");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await JsonAsync(response);
        Assert.Equal("invalid_cursor", json.GetProperty("error").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("error_description").GetString()));
    }

    [Fact]
    public async Task List_PagedByLimit_FollowsNextCursorToNull_SeeingEveryOrganisation()
    {
        var created = new List<string>();
        for (var i = 0; i < 5; i++)
            created.Add((await CreateOrgAsync($"paged-{i}")).GetProperty("id").GetString()!);

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = "/api/v1/organizations?limit=2" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var response = await AdminAsync(HttpMethod.Get, url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await JsonAsync(response);
            Assert.Equal(["items", "nextCursor"], Keys(json));
            var items = json.GetProperty("items").EnumerateArray().ToList();
            Assert.True(items.Count <= 2);
            seen.AddRange(items.Select(i => i.GetProperty("id").GetString()!));

            var next = json.GetProperty("nextCursor");
            if (pages == 0)
                Assert.Equal(JsonValueKind.String, next.ValueKind);
            cursor = next.ValueKind == JsonValueKind.Null ? null : next.GetString();
            pages++;
            Assert.True(pages <= 10, "paging did not terminate");
        } while (cursor is not null);

        Assert.Equal(3, pages);
        Assert.Equal(created.OrderBy(x => x, StringComparer.Ordinal), seen);
    }

    [Fact]
    public async Task Update_ChangesTheOrganisation_AndRefusesARename()
    {
        var created = await CreateOrgAsync("upd", "Before");
        var id = created.GetProperty("id").GetString()!;

        var ok = await AdminAsync(HttpMethod.Put, $"/api/v1/organizations/{id}",
            new { name = "After", slug = "upd", allowAutoMembership = true });
        var rename = await AdminAsync(HttpMethod.Put, $"/api/v1/organizations/{id}",
            new { name = "After", slug = "different" });
        var missing = await AdminAsync(HttpMethod.Put, "/api/v1/organizations/org_missing", new { name = "X" });

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var updated = await JsonAsync(ok);
        Assert.Equal("After", updated.GetProperty("name").GetString());
        Assert.True(updated.GetProperty("allowAutoMembership").GetBoolean());
        Assert.Equal(JsonValueKind.String, updated.GetProperty("updatedAt").ValueKind);

        Assert.Equal(HttpStatusCode.BadRequest, rename.StatusCode);
        Assert.Equal("slug_immutable", (await JsonAsync(rename)).GetProperty("error").GetString());

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("organization_not_found", (await JsonAsync(missing)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Delete_Answers204_ThenTheOrganisationIs404()
    {
        var id = (await CreateOrgAsync("doomed")).GetProperty("id").GetString()!;

        var delete = await AdminAsync(HttpMethod.Delete, $"/api/v1/organizations/{id}");
        var get = await AdminAsync(HttpMethod.Get, $"/api/v1/organizations/{id}");
        var deleteAgain = await AdminAsync(HttpMethod.Delete, $"/api/v1/organizations/{id}");

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(0, delete.Content.Headers.ContentLength ?? 0);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deleteAgain.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Members
    // -----------------------------------------------------------------------

    [Fact]
    public async Task AddMember_Answers201WithLocation_AndTheExactShape_ThenConflictsOnRepeat()
    {
        var orgId = (await CreateOrgAsync("mem")).GetProperty("id").GetString()!;
        var user = await _factory.SeedTestUserAsync("member@example.test");

        var response = await AdminAsync(HttpMethod.Post, $"/api/v1/organizations/{orgId}/members",
            new { userId = user.Id, roles = new[] { "billing" } });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/api/v1/organizations/{orgId}/members/{user.Id}", response.Headers.Location!.OriginalString);
        var json = await JsonAsync(response);
        Assert.Equal(
            new[] { "createdAt", "email", "name", "organizationId", "roles", "status", "updatedAt", "userId" },
            Keys(json));
        Assert.Equal(orgId, json.GetProperty("organizationId").GetString());
        Assert.Equal(user.Id, json.GetProperty("userId").GetString());
        Assert.Equal("member@example.test", json.GetProperty("email").GetString());
        Assert.Equal("Test User", json.GetProperty("name").GetString());
        Assert.Equal("active", json.GetProperty("status").GetString());
        Assert.Equal(["billing"], json.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("updatedAt").ValueKind);

        var again = await AdminAsync(HttpMethod.Post, $"/api/v1/organizations/{orgId}/members", new { userId = user.Id });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("membership_exists", (await JsonAsync(again)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task AddMember_ByEmail_ResolvesTheUser()
    {
        var orgId = (await CreateOrgAsync("mem-email")).GetProperty("id").GetString()!;
        var user = await _factory.SeedTestUserAsync("byemail@example.test");

        var response = await AdminAsync(HttpMethod.Post, $"/api/v1/organizations/{orgId}/members",
            new { email = "byemail@example.test" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(user.Id, (await JsonAsync(response)).GetProperty("userId").GetString());
    }

    [Fact]
    public async Task AddMember_Refusals_AnswerTheirStatusAndCode()
    {
        var orgId = (await CreateOrgAsync("mem-bad")).GetProperty("id").GetString()!;
        var user = await _factory.SeedTestUserAsync("bad@example.test");
        var url = $"/api/v1/organizations/{orgId}/members";

        var unknownEmail = await AdminAsync(HttpMethod.Post, url, new { email = "nobody@example.test" });
        var reserved = await AdminAsync(HttpMethod.Post, url, new { userId = user.Id, roles = new[] { "Tenant:admin" } });
        var bogusStatus = await AdminAsync(HttpMethod.Post, url, new { userId = user.Id, status = "bogus" });
        var missingOrg = await AdminAsync(HttpMethod.Post, "/api/v1/organizations/org_missing/members", new { userId = user.Id });

        Assert.Equal(HttpStatusCode.NotFound, unknownEmail.StatusCode);
        Assert.Equal("user_not_found", (await JsonAsync(unknownEmail)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, reserved.StatusCode);
        Assert.Equal("invalid_role", (await JsonAsync(reserved)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, bogusStatus.StatusCode);
        Assert.Equal("invalid_status", (await JsonAsync(bogusStatus)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.NotFound, missingOrg.StatusCode);
        Assert.Equal("organization_not_found", (await JsonAsync(missingOrg)).GetProperty("error").GetString());
        Assert.Empty(await _factory.OrganizationMembershipStore.ListByOrganizationAsync(orgId));
    }

    [Fact]
    public async Task UpdateMember_Answers200_AndUnknownMemberAnswers404MembershipNotFound()
    {
        var orgId = (await CreateOrgAsync("mem-upd")).GetProperty("id").GetString()!;
        var user = await _factory.SeedTestUserAsync("upd@example.test");
        await AdminAsync(HttpMethod.Post, $"/api/v1/organizations/{orgId}/members",
            new { userId = user.Id, status = "invited" });

        var ok = await AdminAsync(HttpMethod.Put, $"/api/v1/organizations/{orgId}/members/{user.Id}",
            new { status = "active", roles = new[] { "owner" } });
        var missing = await AdminAsync(HttpMethod.Put, $"/api/v1/organizations/{orgId}/members/nobody",
            new { status = "active" });

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var json = await JsonAsync(ok);
        Assert.Equal("active", json.GetProperty("status").GetString());
        Assert.Equal(["owner"], json.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal(JsonValueKind.String, json.GetProperty("updatedAt").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("membership_not_found", (await JsonAsync(missing)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task ListMembers_IsPaged_AndMalformedCursorAnswers400()
    {
        var orgId = (await CreateOrgAsync("mem-list")).GetProperty("id").GetString()!;
        var expected = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var user = await _factory.SeedTestUserAsync($"list{i}@example.test");
            expected.Add(user.Id);
            var add = await AdminAsync(HttpMethod.Post, $"/api/v1/organizations/{orgId}/members", new { userId = user.Id });
            Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        }

        var first = await AdminAsync(HttpMethod.Get, $"/api/v1/organizations/{orgId}/members?limit=2");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstJson = await JsonAsync(first);
        Assert.Equal(2, firstJson.GetProperty("items").GetArrayLength());
        var cursor = firstJson.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);

        var second = await AdminAsync(HttpMethod.Get,
            $"/api/v1/organizations/{orgId}/members?limit=2&cursor={Uri.EscapeDataString(cursor!)}");
        var secondJson = await JsonAsync(second);
        Assert.Equal(1, secondJson.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, secondJson.GetProperty("nextCursor").ValueKind);

        var seen = firstJson.GetProperty("items").EnumerateArray()
            .Concat(secondJson.GetProperty("items").EnumerateArray())
            .Select(m => m.GetProperty("userId").GetString()!);
        Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal), seen);

        var bad = await AdminAsync(HttpMethod.Get, $"/api/v1/organizations/{orgId}/members?cursor=zzz");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("invalid_cursor", (await JsonAsync(bad)).GetProperty("error").GetString());

        var missingOrg = await AdminAsync(HttpMethod.Get, "/api/v1/organizations/org_missing/members");
        Assert.Equal(HttpStatusCode.NotFound, missingOrg.StatusCode);
    }

    [Fact]
    public async Task UserOrganizations_ListsTheUsersMemberships()
    {
        var orgId = (await CreateOrgAsync("uo", "UO Corp")).GetProperty("id").GetString()!;
        var user = await _factory.SeedTestUserAsync("uo@example.test");
        await AdminAsync(HttpMethod.Post, $"/api/v1/organizations/{orgId}/members",
            new { userId = user.Id, roles = new[] { "billing" } });

        var response = await AdminAsync(HttpMethod.Get, $"/api/v1/profile/{user.Id}/organizations");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await JsonAsync(response);
        Assert.Equal(["items"], Keys(json));
        var item = Assert.Single(json.GetProperty("items").EnumerateArray().ToList());
        Assert.Equal(
            new[] { "name", "organizationId", "roles", "slug", "status" },
            Keys(item));
        Assert.Equal(orgId, item.GetProperty("organizationId").GetString());
        Assert.Equal("uo", item.GetProperty("slug").GetString());
        Assert.Equal("UO Corp", item.GetProperty("name").GetString());
        Assert.Equal("active", item.GetProperty("status").GetString());
        Assert.Equal(["billing"], item.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));

        var none = await AdminAsync(HttpMethod.Get, "/api/v1/profile/nobody/organizations");
        Assert.Equal(HttpStatusCode.OK, none.StatusCode);
        Assert.Equal(0, (await JsonAsync(none)).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task RemoveMember_Answers204_ThenTheMembershipIs404()
    {
        var orgId = (await CreateOrgAsync("mem-del")).GetProperty("id").GetString()!;
        var user = await _factory.SeedTestUserAsync("del@example.test");
        await AdminAsync(HttpMethod.Post, $"/api/v1/organizations/{orgId}/members", new { userId = user.Id });

        var delete = await AdminAsync(HttpMethod.Delete, $"/api/v1/organizations/{orgId}/members/{user.Id}");
        var again = await AdminAsync(HttpMethod.Delete, $"/api/v1/organizations/{orgId}/members/{user.Id}");

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal("membership_not_found", (await JsonAsync(again)).GetProperty("error").GetString());
    }

    // -----------------------------------------------------------------------
    // Audit
    // -----------------------------------------------------------------------

    [Fact]
    public async Task EveryWrite_LeavesAnAttributedAuditRow()
    {
        var created = await CreateOrgAsync("audit");
        var orgId = created.GetProperty("id").GetString()!;
        var user = await _factory.SeedTestUserAsync("audit@example.test");
        await AdminAsync(HttpMethod.Put, $"/api/v1/organizations/{orgId}", new { name = "Renamed" });
        await AdminAsync(HttpMethod.Post, $"/api/v1/organizations/{orgId}/members", new { userId = user.Id });
        await AdminAsync(HttpMethod.Put, $"/api/v1/organizations/{orgId}/members/{user.Id}", new { roles = new[] { "x" } });
        await AdminAsync(HttpMethod.Delete, $"/api/v1/organizations/{orgId}/members/{user.Id}");
        await AdminAsync(HttpMethod.Delete, $"/api/v1/organizations/{orgId}");

        var entries = _factory.AuditLog.Entries;
        foreach (var action in new[]
                 {
                     "organization.created", "organization.updated", "organization.member.added",
                     "organization.member.updated", "organization.member.removed", "organization.deleted",
                 })
        {
            var row = Assert.Single(entries, e => e.Action == action);
            Assert.False(string.IsNullOrWhiteSpace(row.Actor), $"{action} has no actor");
            Assert.Equal("organization", row.EntityType);
            Assert.Equal(orgId, row.EntityId);
        }

        Assert.Equal("audit", entries.Single(e => e.Action == "organization.created").Detail);
        Assert.Equal($"user={user.Id}", entries.Single(e => e.Action == "organization.member.added").Detail);
    }

    [Fact]
    public async Task FailedWrites_LeaveNoAuditRow()
    {
        await AdminAsync(HttpMethod.Post, "/api/v1/organizations", new { name = "no slug" });
        await AdminAsync(HttpMethod.Put, "/api/v1/organizations/org_missing", new { name = "X" });
        await AdminAsync(HttpMethod.Delete, "/api/v1/organizations/org_missing");

        Assert.Empty(_factory.AuditLog.Entries);
    }

    // -----------------------------------------------------------------------
    // AdminApi:Enabled = false
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WithTheAdminApiDisabled_TheOrganisationRoutesAreNotMapped_ButTheTokenEndpointWorks()
    {
        await using var factory = new AuthagonalTestFactory();
        factory.Configuration["AdminApi:Enabled"] = "false";
        var client = factory.CreateClient();
        await factory.SeedTestDataAsync();

        // The token endpoint is not an admin route: it still answers.
        var token = await factory.GetAdminTokenAsync(client);
        Assert.False(string.IsNullOrEmpty(token));

        // Premise: the switch really is off for this host (a mapped admin route would answer 200/401/403).
        var roles = await client.SendAsync(Build(HttpMethod.Get, "/api/v1/roles", token));
        Assert.Equal(HttpStatusCode.NotFound, roles.StatusCode);

        foreach (var (method, path) in new[]
                 {
                     ("GET", "/api/v1/organizations"),
                     ("POST", "/api/v1/organizations"),
                     ("GET", "/api/v1/organizations/some-org"),
                     ("PUT", "/api/v1/organizations/some-org"),
                     ("DELETE", "/api/v1/organizations/some-org"),
                     ("GET", "/api/v1/organizations/some-org/members"),
                     ("POST", "/api/v1/organizations/some-org/members"),
                     ("PUT", "/api/v1/organizations/some-org/members/some-user"),
                     ("DELETE", "/api/v1/organizations/some-org/members/some-user"),
                     ("GET", "/api/v1/profile/some-user/organizations"),
                 })
        {
            // With a valid admin token, so the 404 can only be "no such route", never an auth refusal.
            var response = await client.SendAsync(Build(new HttpMethod(method), path, token, new { slug = "a", name = "A" }));
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{method} {path} answered {(int)response.StatusCode}");
        }

        Assert.Empty(await factory.OrganizationStore.ListAsync());
    }
}
