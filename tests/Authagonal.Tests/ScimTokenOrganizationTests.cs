using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// A SCIM credential can be bound to an organization, and every user it provisions is tagged with it.
/// </summary>
/// <remarks>
/// SCIM cannot say which customer a sync belongs to: core RFC 7643 has no organization attribute and the
/// enterprise extension is not implemented here. Without a binding, the only way to tell one customer's
/// synced users from another's was to give each customer its own OAuth client, which multiplies client
/// registrations for what is a property of the credential.
/// <para>
/// The binding is on the TOKEN, not the client, so one client can hand out a credential per customer. It is
/// NOT an isolation boundary: every ownership check still keys on the client id, so two tokens on one client
/// still see each other's users. This decides how users are tagged, not who may touch them.
/// </para>
/// </remarks>
public sealed class ScimTokenOrganizationTests : IAsyncDisposable
{
    private readonly AuthagonalTestFactory _factory = new();

    private HttpClient Authorized(string rawToken)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);
        return client;
    }

    private static async Task<string> ProvisionUserAsync(HttpClient client, string userName)
    {
        var response = await client.PostAsJsonAsync("/scim/v2/Users", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
            userName,
            active = true,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("id").GetString()!;
    }

    private async Task<string?> StoredOrganizationAsync(string userId)
    {
        var user = await _factory.UserStore.GetAsync(userId);
        Assert.NotNull(user);
        return user!.OrganizationId;
    }

    [Fact]
    public async Task AUserProvisionedThroughABoundTokenCarriesItsOrganization()
    {
        await _factory.SeedTestDataAsync();
        var (_, token) = await _factory.SeedScimClientAsync("scim-acme", orgId: "org_acme");

        var userId = await ProvisionUserAsync(Authorized(token), "ada@acme.example");

        Assert.Equal("org_acme", await StoredOrganizationAsync(userId));
    }

    /// <summary>
    /// The point of binding the token rather than the client: one connector registration, one credential per
    /// customer, and the synced users come out tagged for the right customer.
    /// </summary>
    [Fact]
    public async Task TwoTokensOnOneClientTagTheirUsersForDifferentOrganizations()
    {
        await _factory.SeedTestDataAsync();
        var (_, acmeToken) = await _factory.SeedScimClientAsync("shared-scim-client", orgId: "org_acme");
        var (_, globexToken) = await _factory.SeedScimClientAsync("shared-scim-client", orgId: "org_globex");

        var acmeUser = await ProvisionUserAsync(Authorized(acmeToken), "ada@acme.example");
        var globexUser = await ProvisionUserAsync(Authorized(globexToken), "grace@globex.example");

        Assert.Equal("org_acme", await StoredOrganizationAsync(acmeUser));
        Assert.Equal("org_globex", await StoredOrganizationAsync(globexUser));
    }

    [Fact]
    public async Task AnUnboundTokenLeavesUsersUntagged()
    {
        await _factory.SeedTestDataAsync();
        var (_, token) = await _factory.SeedScimClientAsync("scim-plain");

        var userId = await ProvisionUserAsync(Authorized(token), "linus@example.test");

        Assert.True(string.IsNullOrEmpty(await StoredOrganizationAsync(userId)),
            "a token with no organization binding must leave the user untagged, as every existing token does");
    }

    /// <summary>
    /// Re-tagging a live account is an administrative act. A routine incremental sync must not do it
    /// silently, so the binding applies at creation only.
    /// </summary>
    [Fact]
    public async Task AnUpdateThroughADifferentlyBoundTokenDoesNotRetagTheUser()
    {
        await _factory.SeedTestDataAsync();
        var (_, acmeToken) = await _factory.SeedScimClientAsync("shared-scim-client", orgId: "org_acme");
        var (_, globexToken) = await _factory.SeedScimClientAsync("shared-scim-client", orgId: "org_globex");

        var userId = await ProvisionUserAsync(Authorized(acmeToken), "ada@acme.example");

        // Same client, so the second credential really can write this user; only the tag must hold.
        var patch = await Authorized(globexToken).PatchAsync($"/scim/v2/Users/{userId}",
            new StringContent(JsonSerializer.Serialize(new
            {
                schemas = new[] { "urn:ietf:params:scim:PatchOp" },
                Operations = new[] { new { op = "replace", path = "displayName", value = "Ada L" } },
            }), Encoding.UTF8, "application/scim+json"));
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        Assert.Equal("org_acme", await StoredOrganizationAsync(userId));
    }

    /// <summary>
    /// The organization reaches the create path as a claim on the authenticated principal, so a token with
    /// no binding must not fall back to anything. Deriving it from the client id would put an internal
    /// authorization identifier onto customer tokens as an organization.
    /// </summary>
    [Fact]
    public async Task NoOrganizationIsEverDerivedFromTheClientId()
    {
        await _factory.SeedTestDataAsync();
        var (clientId, token) = await _factory.SeedScimClientAsync("scim-plain");

        var userId = await ProvisionUserAsync(Authorized(token), "linus@example.test");

        var stored = await StoredOrganizationAsync(userId);
        Assert.NotEqual(clientId, stored);
        Assert.True(string.IsNullOrEmpty(stored));
    }

    /// <summary>
    /// A binding that names a real organisation makes the synced person a MEMBER of it. The tag alone left
    /// them outside it everywhere memberships are read: missing from its member list, and refused a token by
    /// any relying party that names the organisation.
    /// </summary>
    [Fact]
    public async Task ATokenBoundToAnOrganizationThatExistsMakesTheUserAnActiveMember()
    {
        await _factory.SeedTestDataAsync();
        _factory.OrganizationStore.With("org_acme", "acme", "Acme");
        var (_, token) = await _factory.SeedScimClientAsync("scim-acme", orgId: "org_acme");

        var userId = await ProvisionUserAsync(Authorized(token), "ada@acme.example");

        var membership = await _factory.OrganizationMembershipStore.GetAsync("org_acme", userId);
        Assert.NotNull(membership);
        Assert.Equal(Authagonal.Core.Models.MembershipStatus.Active, membership!.Status);
        Assert.NotNull(membership.JoinedAt);
        Assert.Equal("org_acme", await StoredOrganizationAsync(userId));
    }

    /// <summary>
    /// A binding that names no organisation record is the bare tag it has always been: the account carries
    /// the string, and no membership is invented for an organisation that does not exist.
    /// </summary>
    [Fact]
    public async Task ATokenBoundToAnIdThatNamesNoOrganizationOnlyTagsTheUser()
    {
        await _factory.SeedTestDataAsync();
        var (_, token) = await _factory.SeedScimClientAsync("scim-legacy", orgId: "customer-42");

        var userId = await ProvisionUserAsync(Authorized(token), "ada@legacy.example");

        Assert.Equal("customer-42", await StoredOrganizationAsync(userId));
        Assert.Empty(await _factory.OrganizationMembershipStore.ListByUserAsync(userId));
    }

    [Fact]
    public async Task AnUnboundTokenCreatesNoMembership()
    {
        await _factory.SeedTestDataAsync();
        _factory.OrganizationStore.With("org_acme", "acme", "Acme");
        var (_, token) = await _factory.SeedScimClientAsync("scim-plain");

        var userId = await ProvisionUserAsync(Authorized(token), "linus@example.test");

        Assert.Empty(await _factory.OrganizationMembershipStore.ListByUserAsync(userId));
    }

    /// <summary>
    /// The membership a sync created goes when the sync deletes the person, so a deprovisioned employee is
    /// not left listed in the customer's organisation.
    /// </summary>
    [Fact]
    public async Task DeletingASyncedUserRemovesTheirMemberships()
    {
        await _factory.SeedTestDataAsync();
        _factory.OrganizationStore.With("org_acme", "acme", "Acme");
        var (_, token) = await _factory.SeedScimClientAsync("scim-acme", orgId: "org_acme");
        var client = Authorized(token);
        var userId = await ProvisionUserAsync(client, "ada@acme.example");

        var delete = await client.DeleteAsync($"/scim/v2/Users/{userId}");

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Empty(await _factory.OrganizationMembershipStore.ListByUserAsync(userId));
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();
}
