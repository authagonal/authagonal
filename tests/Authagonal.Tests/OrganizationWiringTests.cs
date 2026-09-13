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
[Collection("Azurite")]
public sealed class OrganizationWiringTests(AzuriteFixture azurite)
{
    private static readonly EnvPartitioner Live = new("live");

    // -----------------------------------------------------------------------
    // F1 — RestrictedToOrganizationIds survives a round trip through Table Storage
    // -----------------------------------------------------------------------

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

    // -----------------------------------------------------------------------
    // F2 — the provider's stores must win over the in-memory fallbacks
    // -----------------------------------------------------------------------

    /// AddAuthagonalCore TryAdds the empty in-memory fallbacks and AddTableStorage TryAdds the durable
    /// ones, in that order — so the fallbacks won and the batteries-included host could never create an
    /// organization or read a group→role mapping, with nothing logged. Built through the real
    /// AddAuthagonal rather than a hand-assembled collection, because the registration ORDER is the
    /// defect and a hand-assembled one would not reproduce it.
    [Fact]
    public void AddAuthagonal_ResolvesTheProviderStores_NotTheInMemoryFallbacks()
    {
        using var provider = BuildHost(core: false);

        Assert.IsType<TableOrganizationStore>(provider.GetRequiredService<IOrganizationStore>());
        Assert.IsType<TableOrganizationMembershipStore>(provider.GetRequiredService<IOrganizationMembershipStore>());
        Assert.IsType<TableScimGroupRoleMappingStore>(provider.GetRequiredService<IScimGroupRoleMappingStore>());
    }

    /// The fallbacks are still there for a host that wires no provider at all: the demotion is
    /// conditional on a real registration existing, not unconditional.
    [Fact]
    public void AddAuthagonalCore_AloneStillResolvesTheFallbacks()
    {
        using var provider = BuildHost(core: true);

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

    private ServiceProvider BuildHost(bool core)
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
        if (core) services.AddAuthagonalCore(configuration);
        else services.AddAuthagonal(configuration);

        return services.BuildServiceProvider();
    }

    private async Task<TableClientStore> NewClientStoreAsync()
    {
        var table = new TableServiceClient(azurite.ConnectionString)
            .GetTableClient($"Clients{Guid.NewGuid():N}");
        await table.CreateIfNotExistsAsync();
        return new TableClientStore(table, Live);
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
