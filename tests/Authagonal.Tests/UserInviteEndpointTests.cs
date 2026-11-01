using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Authagonal.Tests;

/// <summary>
/// <c>POST /api/v1/profile/invite</c> and <c>POST /api/v1/profile/{userId}/resend-invite</c> over HTTP:
/// authorisation, the exact response shapes, every error code and status, the link the email carries, and the
/// whole invite-to-accept journey through the existing <c>/api/auth/reset-password</c>.
/// </summary>
public sealed class UserInviteEndpointTests : IAsyncLifetime
{
    private const string LimitedClientId = "limited-client";
    private const string LimitedClientSecret = "limited-secret-789";
    private const string Password = "NewPass1234!";

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

    private static HttpRequestMessage Build(HttpMethod method, string url, string? token, object? body = null,
        string? acceptLanguage = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (acceptLanguage is not null)
            request.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }

    private static Task<HttpResponseMessage> AdminAsync(HttpClient client, string token, HttpMethod method, string url,
        object? body = null, string? acceptLanguage = null) =>
        client.SendAsync(Build(method, url, token, body, acceptLanguage));

    private Task<HttpResponseMessage> AdminAsync(HttpMethod method, string url, object? body = null, string? acceptLanguage = null) =>
        AdminAsync(_client, _adminToken, method, url, body, acceptLanguage);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private async Task<Organization> SeedOrgAsync(string slug, string name = "Acme Inc", string? branding = null)
    {
        var org = new Organization { Id = $"org_{slug}", Slug = slug, DisplayName = name, BrandingJson = branding };
        await _factory.OrganizationStore.UpsertAsync(org);
        return org;
    }

    private static string TokenOf(string url) => System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["p"]!;

    private async Task<string> LimitedTokenAsync()
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
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials", ["scope"] = "openid",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{LimitedClientId}:{LimitedClientSecret}")));
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("access_token").GetString()!;
    }

    private async Task<(string UserId, string Email, string Token)> InviteAsync(string email, object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["email"] = email };
        if (extra is not null)
            foreach (var p in extra.GetType().GetProperties()) body[p.Name] = p.GetValue(extra);
        var response = await AdminAsync(HttpMethod.Post, "/api/v1/profile/invite", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var userId = (await JsonAsync(response)).GetProperty("userId").GetString()!;
        return (userId, email, TokenOf(_factory.EmailService.SentEmails.Last().CallbackUrl));
    }

    private Task<HttpResponseMessage> RedeemAsync(string token, string password = Password) =>
        _client.PostAsJsonAsync("/api/auth/reset-password", new { token, newPassword = password });

    // -----------------------------------------------------------------------
    // Authorisation
    // -----------------------------------------------------------------------

    public static TheoryData<string, string> Routes => new()
    {
        { "POST", "/api/v1/profile/invite" },
        { "POST", "/api/v1/profile/some-user/resend-invite" },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task WithoutAToken_Is401(string method, string path)
    {
        var response = await _client.SendAsync(Build(new HttpMethod(method), path, null, new { email = "a@invite.test" }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_factory.EmailService.SentEmails);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task WithoutTheAdminScope_Is403(string method, string path)
    {
        var token = await LimitedTokenAsync();

        var response = await _client.SendAsync(Build(new HttpMethod(method), path, token, new { email = "a@invite.test" }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_factory.EmailService.SentEmails);
        Assert.Null(await _factory.UserStore.FindByEmailAsync("a@invite.test"));
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task WithTheAdminApiDisabled_TheRoutesAreNotMapped(string method, string path)
    {
        await using var factory = new AuthagonalTestFactory();
        factory.Configuration["AdminApi:Enabled"] = "false";
        var client = factory.CreateClient();
        await factory.SeedTestDataAsync();
        var token = await factory.GetAdminTokenAsync(client);

        var response = await client.SendAsync(Build(new HttpMethod(method), path, token, new { email = "a@invite.test" }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Invite
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Invite_Answers200_WithExactlyUserIdAndEmail()
    {
        var response = await AdminAsync(HttpMethod.Post, "/api/v1/profile/invite",
            new { email = "New.Person@invite.test", firstName = "New", lastName = "Person", locale = "en" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await JsonAsync(response);
        Assert.Equal(["email", "userId"], json.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("new.person@invite.test", json.GetProperty("email").GetString());
        var user = (await _factory.UserStore.GetAsync(json.GetProperty("userId").GetString()!))!;
        Assert.False(user.EmailConfirmed);
        Assert.Equal("New", user.FirstName);
    }

    [Fact]
    public async Task Invite_SendsTheAcceptLink_ToAnImplementorThatNeverHeardOfInvites()
    {
        await AdminAsync(HttpMethod.Post, "/api/v1/profile/invite", new { email = "plain@invite.test" });

        var (to, url, kind) = Assert.Single(_factory.EmailService.SentEmails);
        Assert.Equal("password_reset", kind);
        Assert.Equal("plain@invite.test", to);
        var uri = new Uri(url);
        Assert.Equal(AuthagonalTestFactory.TestIssuer, uri.GetLeftPart(UriPartial.Authority));
        Assert.Equal("/accept-invite", uri.AbsolutePath);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        Assert.True(query["p"]!.Length > 20);
        Assert.Null(query["organization"]);
    }

    [Fact]
    public async Task InviteIntoAnOrganisation_NamesItsSlugInTheLink_AndWritesAnInvitedMembership()
    {
        var org = await SeedOrgAsync("acme");

        var (userId, _, _) = await InviteAsync("m@invite.test", new { organizationId = org.Id, organizationRoles = new[] { "billing" } });

        var query = System.Web.HttpUtility.ParseQueryString(new Uri(_factory.EmailService.SentEmails.Last().CallbackUrl).Query);
        Assert.Equal("acme", query["organization"]);
        var membership = (await _factory.OrganizationMembershipStore.GetAsync(org.Id, userId))!;
        Assert.Equal(MembershipStatus.Invited, membership.Status);
        Assert.Equal(["billing"], membership.Roles);
        Assert.False(string.IsNullOrWhiteSpace(membership.InvitedByUserId));
        Assert.NotEqual("unknown", membership.InvitedByUserId);
        Assert.NotNull(membership.InvitedAt);
    }

    [Fact]
    public async Task Invite_WithABodyThatNamesNoEmail_Is400_email_required()
    {
        foreach (var body in new object?[] { new { organizationId = "x" }, new { email = "  " }, null })
        {
            var response = await AdminAsync(HttpMethod.Post, "/api/v1/profile/invite", body);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var json = await JsonAsync(response);
            Assert.Equal("email_required", json.GetProperty("error").GetString());
            Assert.Equal("email_required", json.GetProperty("error_description").GetString());
        }
    }

    [Fact]
    public async Task Invite_IntoAMissingOrganisation_Is404_organization_not_found()
    {
        var response = await AdminAsync(HttpMethod.Post, "/api/v1/profile/invite",
            new { email = "x@invite.test", organizationId = "org_missing" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("organization_not_found", (await JsonAsync(response)).GetProperty("error").GetString());
        Assert.Null(await _factory.UserStore.FindByEmailAsync("x@invite.test"));
    }

    [Fact]
    public async Task Invite_WithAReservedRole_Is400_invalid_role()
    {
        var org = await SeedOrgAsync("roles");

        var response = await AdminAsync(HttpMethod.Post, "/api/v1/profile/invite",
            new { email = "y@invite.test", organizationId = org.Id, organizationRoles = new[] { "platform:x" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_role", (await JsonAsync(response)).GetProperty("error").GetString());
        Assert.Null(await _factory.UserStore.FindByEmailAsync("y@invite.test"));
    }

    [Fact]
    public async Task Invite_OfAnExistingAddress_Is409_user_already_exists()
    {
        await InviteAsync("dup@invite.test");

        var response = await AdminAsync(HttpMethod.Post, "/api/v1/profile/invite", new { email = "dup@invite.test" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("user_already_exists", (await JsonAsync(response)).GetProperty("error").GetString());
        Assert.Single(_factory.EmailService.SentEmails);
    }

    [Fact]
    public async Task Invite_AuditsTheMembershipAndTheUser_ByTheAdminsActor()
    {
        var org = await SeedOrgAsync("audit");

        var (userId, _, _) = await InviteAsync("a@invite.test", new { organizationId = org.Id });

        var invited = Assert.Single(_factory.AuditLog.Entries, e => e.Action == "organization.member.invited");
        Assert.Equal(("organization", org.Id, $"user={userId}"), (invited.EntityType, invited.EntityId, invited.Detail));
        var user = Assert.Single(_factory.AuditLog.Entries, e => e.Action == "user.invited");
        Assert.Equal(("user", userId, "***@invite.test"), (user.EntityType, user.EntityId, user.Detail));
        Assert.All(_factory.AuditLog.Entries, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Actor));
            Assert.NotEqual("unknown", e.Actor);
        });
    }

    [Fact]
    public async Task FailedInvites_LeaveNoAuditRow()
    {
        await AdminAsync(HttpMethod.Post, "/api/v1/profile/invite", new { organizationId = "x" });
        await AdminAsync(HttpMethod.Post, "/api/v1/profile/invite", new { email = "x@invite.test", organizationId = "org_missing" });

        Assert.Empty(_factory.AuditLog.Entries);
    }

    [Fact]
    public async Task Invite_WhenTheMailFails_StillAnswers200()
    {
        _factory.EmailService.FailPasswordResetWith = new InvalidOperationException("mail down");

        var response = await AdminAsync(HttpMethod.Post, "/api/v1/profile/invite", new { email = "down@invite.test" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(await _factory.UserStore.FindByEmailAsync("down@invite.test"));
    }

    // -----------------------------------------------------------------------
    // The journey: invite, accept through the existing reset route, become active
    // -----------------------------------------------------------------------

    [Fact]
    public async Task AcceptingTheInvite_SetsThePassword_ConfirmsTheAddress_AndActivatesTheMembership()
    {
        var org = await SeedOrgAsync("journey");
        var (userId, email, token) = await InviteAsync("journey@invite.test", new { organizationId = org.Id, organizationRoles = new[] { "billing" } });
        _factory.AuditLog.Entries.Clear();

        var weak = await RedeemAsync(token, "weak");
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Equal("weak_password", (await JsonAsync(weak)).GetProperty("error").GetString());
        Assert.Equal(MembershipStatus.Invited, (await _factory.OrganizationMembershipStore.GetAsync(org.Id, userId))!.Status);

        var redeemed = await RedeemAsync(token);
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);

        var user = (await _factory.UserStore.GetAsync(userId))!;
        Assert.True(user.EmailConfirmed);
        var membership = (await _factory.OrganizationMembershipStore.GetAsync(org.Id, userId))!;
        Assert.Equal(MembershipStatus.Active, membership.Status);
        Assert.Equal(["billing"], membership.Roles);
        Assert.NotNull(membership.JoinedAt);
        Assert.NotNull(membership.InvitedAt);
        var joined = Assert.Single(_factory.AuditLog.Entries, e => e.Action == "organization.member.joined");
        Assert.Equal((userId, "organization", org.Id, $"user={userId}"), (joined.Actor, joined.EntityType, joined.EntityId, joined.Detail));

        // Single use.
        var again = await RedeemAsync(token);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains((await JsonAsync(again)).GetProperty("error").GetString(), new[] { "token_expired", "invalid_token" });

        // And the new password signs in.
        var login = await _client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task AcceptingTheInvite_ActivatesOnlyInvitedMemberships()
    {
        var invitedOrg = await SeedOrgAsync("one");
        var addedOrg = await SeedOrgAsync("two");
        var suspendedOrg = await SeedOrgAsync("three");
        var (userId, _, token) = await InviteAsync("act@invite.test", new { organizationId = invitedOrg.Id });
        _factory.OrganizationMembershipStore.With(addedOrg.Id, userId, MembershipStatus.Invited);
        _factory.OrganizationMembershipStore.With(suspendedOrg.Id, userId, MembershipStatus.Suspended);

        Assert.Equal(HttpStatusCode.OK, (await RedeemAsync(token)).StatusCode);

        Assert.Equal(MembershipStatus.Active, (await _factory.OrganizationMembershipStore.GetAsync(invitedOrg.Id, userId))!.Status);
        Assert.Equal(MembershipStatus.Active, (await _factory.OrganizationMembershipStore.GetAsync(addedOrg.Id, userId))!.Status);
        Assert.Equal(MembershipStatus.Suspended, (await _factory.OrganizationMembershipStore.GetAsync(suspendedOrg.Id, userId))!.Status);
        Assert.Equal(2, _factory.AuditLog.Entries.Count(e => e.Action == "organization.member.joined"));
    }

    [Fact]
    public async Task TheHostsOwnHookStillRuns_AndOnlyOneHookWasAddedByTheLibrary()
    {
        var hooks = _factory.Services.GetServices<Authagonal.Core.Services.IAuthHook>().ToList();

        Assert.Contains(hooks, h => ReferenceEquals(h, _factory.AuthHook));
        Assert.Single(hooks, h => h is OrganizationInviteActivationHook);
        Assert.DoesNotContain(hooks, h => h is NullAuthHook);
    }

    // -----------------------------------------------------------------------
    // Resend
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Resend_Answers200_WithSentTrue_AndMintsAFreshLink()
    {
        var (userId, _, first) = await InviteAsync("again@invite.test");

        var response = await AdminAsync(HttpMethod.Post, $"/api/v1/profile/{userId}/resend-invite");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await JsonAsync(response);
        Assert.Equal(["sent"], json.EnumerateObject().Select(p => p.Name));
        Assert.True(json.GetProperty("sent").GetBoolean());
        Assert.Equal(2, _factory.EmailService.SentEmails.Count);
        var second = TokenOf(_factory.EmailService.SentEmails.Last().CallbackUrl);
        Assert.NotEqual(first, second);
        Assert.Equal(HttpStatusCode.OK, (await RedeemAsync(second)).StatusCode);
    }

    [Fact]
    public async Task Resend_ToAnOrganisationInvite_KeepsTheOrganisationInTheLink_AndAudits()
    {
        var org = await SeedOrgAsync("resent");
        var (userId, _, _) = await InviteAsync("res@invite.test", new { organizationId = org.Id });
        _factory.AuditLog.Entries.Clear();

        await AdminAsync(HttpMethod.Post, $"/api/v1/profile/{userId}/resend-invite");

        var query = System.Web.HttpUtility.ParseQueryString(new Uri(_factory.EmailService.SentEmails.Last().CallbackUrl).Query);
        Assert.Equal("resent", query["organization"]);
        var row = Assert.Single(_factory.AuditLog.Entries);
        Assert.Equal(("user.invite_resent", "user", userId, "***@invite.test"), (row.Action, row.EntityType, row.EntityId, row.Detail));
    }

    [Fact]
    public async Task Resend_ForAMissingUser_Is404_user_not_found()
    {
        var response = await AdminAsync(HttpMethod.Post, "/api/v1/profile/nobody/resend-invite");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("user_not_found", (await JsonAsync(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Resend_AfterTheInviteWasAccepted_Is400_already_accepted()
    {
        var (userId, _, token) = await InviteAsync("done@invite.test");
        await RedeemAsync(token);
        var sent = _factory.EmailService.SentEmails.Count;

        var response = await AdminAsync(HttpMethod.Post, $"/api/v1/profile/{userId}/resend-invite");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("already_accepted", (await JsonAsync(response)).GetProperty("error").GetString());
        Assert.Equal(sent, _factory.EmailService.SentEmails.Count);
    }

    [Fact]
    public async Task Resend_WhenTheMailFails_Is502_invite_email_failed()
    {
        var (userId, _, _) = await InviteAsync("fail@invite.test");
        _factory.AuditLog.Entries.Clear();
        _factory.EmailService.FailPasswordResetWith = new InvalidOperationException("mail down");

        var response = await AdminAsync(HttpMethod.Post, $"/api/v1/profile/{userId}/resend-invite");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("invite_email_failed", (await JsonAsync(response)).GetProperty("error").GetString());
        Assert.Empty(_factory.AuditLog.Entries);
    }

    // -----------------------------------------------------------------------
    // The real sender, against a stand-in Resend
    // -----------------------------------------------------------------------

    private sealed class ResendStandIn : HttpMessageHandler
    {
        public List<(Uri Uri, JsonElement Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Requests.Add((request.RequestUri!, doc.RootElement.Clone()));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static async Task<(AuthagonalTestFactory Factory, HttpClient Client, string Token, ResendStandIn Resend)> RealSenderHostAsync()
    {
        var resend = new ResendStandIn();
        var factory = new AuthagonalTestFactory { ResendHttpHandler = resend };
        factory.Configuration["Email:ResendApiUrl"] = "https://relay.invite.test/emails";
        var client = factory.CreateClient();
        await factory.SeedTestDataAsync();
        return (factory, client, await factory.GetAdminTokenAsync(client), resend);
    }

    [Fact]
    public async Task WithEmailResendApiUrl_TheRealSenderDeliversTheInviteThere_WithTheOrganisationsBranding()
    {
        var (factory, client, token, resend) = await RealSenderHostAsync();
        await using var _ = factory;
        await factory.OrganizationStore.UpsertAsync(new Organization
        {
            Id = "org_brand", Slug = "brand", DisplayName = "Brand Org",
            BrandingJson = """{"appName":"QA Invite Org","primaryColor":"#0a7c3e"}""",
        });

        var response = await AdminAsync(client, token, HttpMethod.Post, "/api/v1/profile/invite",
            new { email = "brand@invite.test", organizationId = "org_brand" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (uri, body) = Assert.Single(resend.Requests);
        Assert.Equal("https://relay.invite.test/emails", uri.ToString());
        Assert.Equal(["brand@invite.test"], body.GetProperty("to").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("QA Invite Org", body.GetProperty("subject").GetString());
        var html = body.GetProperty("html").GetString()!;
        Assert.Contains("#0a7c3e", html);
        Assert.Contains("Brand Org", html);
        Assert.Contains("/accept-invite?p=", html);
        Assert.Contains("organization=brand", html);
    }

    [Fact]
    public async Task TheInviteIsWrittenInTheCallersAcceptLanguage_ElseTheBodyLocale()
    {
        var (factory, client, token, resend) = await RealSenderHostAsync();
        await using var _ = factory;

        async Task<string> SubjectAsync(string email, string? locale, string? acceptLanguage)
        {
            var response = await AdminAsync(client, token, HttpMethod.Post, "/api/v1/profile/invite",
                new { email, locale }, acceptLanguage);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return resend.Requests.Last().Body.GetProperty("subject").GetString()!;
        }

        // The caller's own preference, best quality first, among the languages shipped.
        Assert.StartsWith("Sie wurden eingeladen", await SubjectAsync("a1@invite.test", "ja", "xx;q=1.0, de;q=0.9, fr;q=0.5"));
        // Nothing shipped in the header: the locale the body names.
        Assert.StartsWith("招待されました", await SubjectAsync("a2@invite.test", "ja", "xx"));
        // No header at all.
        Assert.StartsWith("Vous êtes invité", await SubjectAsync("a3@invite.test", "fr", null));
    }

    [Fact]
    public async Task ResendUsesTheCallersAcceptLanguage()
    {
        var (factory, client, token, resend) = await RealSenderHostAsync();
        await using var _ = factory;
        var userId = (await JsonAsync(await AdminAsync(client, token, HttpMethod.Post, "/api/v1/profile/invite",
            new { email = "rl@invite.test" }))).GetProperty("userId").GetString()!;

        var response = await AdminAsync(client, token, HttpMethod.Post, $"/api/v1/profile/{userId}/resend-invite", null, "es");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("Has sido invitado", resend.Requests.Last().Body.GetProperty("subject").GetString());
    }
}
