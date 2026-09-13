using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Web;
using Authagonal.AzureProvider.Entities;
using Authagonal.AzureProvider.Stores;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server;
using Authagonal.Tests.Infrastructure;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Authagonal.Tests;

/// <summary>
/// Wiring defects: a client field that never reached storage, provider stores DI could not reach, and
/// an organisation the embedded host parsed and then discarded.
/// </summary>
public sealed class OrganizationWiringTests
{
    private static readonly EnvPartitioner Live = new("live");


    // -----------------------------------------------------------------------
    // F1 — RestrictedToOrganizationIds survives a round trip through Table Storage
    // -----------------------------------------------------------------------

    /// A row written before the column existed has no such property in storage, so the entity's own
    /// default has to mean "unrestricted" — never null, which every caller would then have to guard.
    [Fact]
    public void ClientEntity_MissingColumnReadsAsUnrestricted()
    {
        var entity = ClientEntity.FromModel(new OAuthClient { ClientId = "c1", ClientName = "C1" });
        entity.PartitionKey = "c1";

        // What Azure hands back for a row stored before the column existed: the property is simply
        // absent from the wire, so the C# initializer stands.
        Assert.Equal("[]", entity.RestrictedToOrganizationIdsJson);

        var model = entity.ToModel(Live);
        Assert.NotNull(model.RestrictedToOrganizationIds);
        Assert.Empty(model.RestrictedToOrganizationIds);
    }

    /// The admin API binds the raw model, so the field is accepted and returned — asserted rather than
    /// assumed, because "it is already on the model" is exactly the reasoning that missed the storage
    /// layer.
    [Fact]
    public async Task AdminApi_AcceptsAndReturnsTheOrganizationRestriction()
    {
        await using var factory = new AuthagonalTestFactory();
        var client = factory.CreateClient();
        await factory.SeedTestDataAsync();
        var token = await factory.GetAdminTokenAsync(client);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var created = await client.PostAsJsonAsync("/api/v1/clients", new
        {
            clientId = "org-restricted-client",
            clientName = "Org Restricted",
            restrictedToOrganizationIds = new[] { "org-a" },
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());

        var read = await client.GetFromJsonAsync<JsonElement>("/api/v1/clients/org-restricted-client");
        var ids = read.GetProperty("restrictedToOrganizationIds").EnumerateArray()
            .Select(e => e.GetString()).ToList();

        Assert.Equal(["org-a"], ids);
    }

    /// R4. The model's `= []` initializer does not survive JSON binding: an explicit null leaves the
    /// property null, and the token paths read `.Count` on it — so one malformed admin call turned every
    /// later authorize and exchange for that client into a 500. Normalised at the endpoint AND guarded
    /// at the point of use, so neither half has to trust the other.
    [Fact]
    public async Task AdminApi_NullRestrictionIsStoredAsEmpty()
    {
        await using var factory = new AuthagonalTestFactory();
        var client = factory.CreateClient();
        await factory.SeedTestDataAsync();
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", await factory.GetAdminTokenAsync(client));

        var created = await client.PostAsJsonAsync("/api/v1/clients", new
        {
            clientId = "null-restriction-client",
            clientName = "Null Restriction",
            restrictedToOrganizationIds = (string[]?)null,
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());

        var stored = await factory.ClientStore.GetAsync("null-restriction-client");
        Assert.NotNull(stored!.RestrictedToOrganizationIds);
        Assert.Empty(stored.RestrictedToOrganizationIds);
    }

    /// An id outside the organization charset is an id no `organization` parameter can ever send, so a
    /// restriction listing one would match nothing — and a restriction that matches nothing refuses
    /// every request. That is a lockout written by a typo, so it is a 400 instead.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("a b")]
    public async Task AdminApi_RejectsAMalformedRestrictionEntry(string entry)
    {
        await using var factory = new AuthagonalTestFactory();
        var client = factory.CreateClient();
        await factory.SeedTestDataAsync();
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", await factory.GetAdminTokenAsync(client));

        var created = await client.PostAsJsonAsync("/api/v1/clients", new
        {
            clientId = $"bad-restriction-{Guid.NewGuid():N}",
            clientName = "Bad Restriction",
            restrictedToOrganizationIds = new[] { entry },
        });

        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", body.GetProperty("error").GetString());
    }

    /// The same normalisation on the update path, which merges onto the stored record and can therefore
    /// produce a null from a body that never mentioned the field.
    [Fact]
    public async Task AdminApi_UpdateRejectsAMalformedRestrictionEntry()
    {
        await using var factory = new AuthagonalTestFactory();
        var client = factory.CreateClient();
        await factory.SeedTestDataAsync();
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", await factory.GetAdminTokenAsync(client));

        var updated = await client.PutAsJsonAsync($"/api/v1/clients/{AuthagonalTestFactory.TestClientId}", new
        {
            restrictedToOrganizationIds = new[] { "a#b" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, updated.StatusCode);
    }

    [Theory]
    [InlineData("org_7f3a", true)]
    [InlineData("acme", true)]
    [InlineData("A.b~c_d-e", true)]
    [InlineData("a/b", false)]
    [InlineData("a b", false)]
    [InlineData("", false)]
    public void OrganizationIdentifier_Classify(string id, bool valid) =>
        Assert.Equal(valid, OrganizationIdentifier.IsValid(id));

    /// R10's advice, mechanised: an `org_`-prefixed id can never be a slug, because `_` is not
    /// slug-legal. A bare lowercase id can be, which is why it is only a SHOULD.
    [Theory]
    [InlineData("org_7f3a", false)]
    [InlineData("AbC123", false)]
    [InlineData("acme", true)]
    public void OrganizationIdentifier_ReportsSlugCollisionRisk(string id, bool couldCollide) =>
        Assert.Equal(couldCollide, OrganizationIdentifier.CouldCollideWithASlug(id));

    // -----------------------------------------------------------------------
    // F2 — the provider's stores must win over the in-memory fallbacks
    // -----------------------------------------------------------------------

    /// The fallbacks are still there for a host that wires no provider at all: the demotion is
    /// conditional on a real registration existing, not unconditional.
    [Fact]
    public void AddAuthagonalCore_AloneStillResolvesTheFallbacks()
    {
        using var provider = BuildCoreOnlyHost();

        Assert.IsType<Authagonal.Server.Services.InMemoryOrganizationStore>(
            provider.GetRequiredService<IOrganizationStore>());
        Assert.IsType<Authagonal.Server.Services.InMemoryOrganizationMembershipStore>(
            provider.GetRequiredService<IOrganizationMembershipStore>());
        Assert.IsType<Authagonal.Server.Services.InMemoryScimGroupRoleMappingStore>(
            provider.GetRequiredService<IScimGroupRoleMappingStore>());
    }

    // -----------------------------------------------------------------------
    // F7 — the Protocol-only host honours the organization parameter
    // -----------------------------------------------------------------------

    /// The embedded host shares AuthorizeRequest.Read, so it parsed `organization` and refused a
    /// malformed one — then built a three-argument resolution context and dropped the value, issuing a
    /// token for whatever organization the subject already carried. The host's resolver echoes
    /// context.RequestedOrganization onto the subject, so org_id proves the value arrived.
    [Fact]
    public async Task ProtocolHost_PassesTheOrganizationToTheResolver()
    {
        await using var host = new ProtocolTestHost();
        var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/test-login")).StatusCode);

        var (verifier, challenge) = NewPkcePair();
        var url = $"/connect/authorize?client_id={ProtocolTestHost.SpaClientId}" +
            $"&response_type=code&redirect_uri={Uri.EscapeDataString(ProtocolTestHost.SpaRedirectUri)}" +
            $"&scope=openid%20profile&state=xyz&organization=acme" +
            $"&code_challenge={challenge}&code_challenge_method=S256";

        var authorize = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.Found, authorize.StatusCode);
        var code = HttpUtility.ParseQueryString(authorize.Headers.Location!.Query)["code"];
        Assert.NotNull(code);

        var token = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code!,
                ["redirect_uri"] = ProtocolTestHost.SpaRedirectUri,
                ["client_id"] = ProtocolTestHost.SpaClientId,
                ["code_verifier"] = verifier,
            }));
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);

        var body = await token.Content.ReadFromJsonAsync<JsonElement>();
        var claims = new JsonWebToken(body.GetProperty("access_token").GetString()!).Claims
            .GroupBy(c => c.Type).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);

        Assert.Equal("acme", claims["org_id"]);
    }

    /// F9 on this host too: the shared validator refuses a malformed value before anything resolves.
    [Fact]
    public async Task ProtocolHost_RefusesAMalformedOrganization()
    {
        await using var host = new ProtocolTestHost();
        var client = host.CreateClient();
        await client.GetAsync("/test-login");

        var (_, challenge) = NewPkcePair();
        var url = $"/connect/authorize?client_id={ProtocolTestHost.SpaClientId}" +
            $"&response_type=code&redirect_uri={Uri.EscapeDataString(ProtocolTestHost.SpaRedirectUri)}" +
            $"&scope=openid&state=xyz&organization={Uri.EscapeDataString("a/b")}" +
            $"&code_challenge={challenge}&code_challenge_method=S256";

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("invalid_request", HttpUtility.ParseQueryString(response.Headers.Location!.Query)["error"]);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static ServiceProvider BuildCoreOnlyHost()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:Issuer"] = "https://wiring.test",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddAuthagonalCore(configuration);

        return services.BuildServiceProvider();
    }

    private static (string Verifier, string Challenge) NewPkcePair()
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(
                SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (verifier, challenge);
    }
}

/// <summary>
/// The one wiring assertion that needs a real table: that <c>RestrictedToOrganizationIds</c> survives a
/// round trip through Azure Table Storage. Split out of <see cref="OrganizationWiringTests"/> so the
/// rest of that file — DI composition, admin-API validation, pure shape checks — does not take a Docker
/// dependency to assert things that have nothing to do with storage.
/// </summary>
[Collection("Azurite")]
public sealed class OrganizationClientEntityStorageTests(AzuriteFixture azurite)
{
    private static readonly EnvPartitioner Live = new("live");

    /// The field was on the model and honoured by the selector, so it looked wired — but the Azure
    /// client entity had no column for it, so an operator who restricted a client saw the restriction
    /// vanish on the very next read, silently. SQL and Dynamo serialise the whole record and were fine,
    /// which is how it survived review of those two.
    [Fact]
    public async Task ClientStore_RoundTripsTheOrganizationRestriction()
    {
        var store = await NewClientStoreAsync();
        var clientId = $"c-{Guid.NewGuid():N}";

        await store.UpsertAsync(new OAuthClient
        {
            ClientId = clientId,
            ClientName = "Restricted",
            RestrictedToOrganizationIds = ["org-a", "org-b"],
        });

        var read = await store.GetAsync(clientId);

        Assert.NotNull(read);
        Assert.Equal(["org-a", "org-b"], read!.RestrictedToOrganizationIds);
    }

    /// AddAuthagonalCore TryAdds the empty in-memory fallbacks and AddTableStorage TryAdds the durable
    /// ones, in that order — so the fallbacks won and the batteries-included host could never create an
    /// organization or read a group→role mapping, with nothing logged. Built through the real
    /// AddAuthagonal rather than a hand-assembled collection, because the registration ORDER is the
    /// defect and a hand-assembled one would not reproduce it.
    [Fact]
    public void AddAuthagonal_ResolvesTheProviderStores_NotTheInMemoryFallbacks()
    {
        using var provider = BuildFullHost();

        Assert.IsType<TableOrganizationStore>(provider.GetRequiredService<IOrganizationStore>());
        Assert.IsType<TableOrganizationMembershipStore>(provider.GetRequiredService<IOrganizationMembershipStore>());
        Assert.IsType<TableScimGroupRoleMappingStore>(provider.GetRequiredService<IScimGroupRoleMappingStore>());
    }

    /// <summary>Composes the real batteries-included host against a live Azurite, because
    /// AddTableStorage creates its tables eagerly — so this cannot be asserted without one.</summary>
    private ServiceProvider BuildFullHost()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:ConnectionString"] = azurite.ConnectionString,
                ["Auth:Issuer"] = "https://wiring.test",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddAuthagonal(configuration);

        return services.BuildServiceProvider();
    }

    private async Task<TableClientStore> NewClientStoreAsync()
    {
        var table = new TableServiceClient(azurite.ConnectionString)
            .GetTableClient($"Clients{Guid.NewGuid():N}");
        await table.CreateIfNotExistsAsync();
        return new TableClientStore(table, Live);
    }
}
