using Amazon.DynamoDBv2;
using Authagonal.AwsProvider.Dynamo;
using Authagonal.AwsProvider.Stores;
using Authagonal.AzureProvider.Stores;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.SqlProvider.Sql;
using Authagonal.SqlProvider.Stores;
using Authagonal.Tests.Infrastructure;
using Azure.Data.Tables;

namespace Authagonal.Tests;

/// <summary>
/// <see cref="SamlProviderConfig.OrganizationId"/> survives a store round trip, and
/// <c>ListByOrganizationAsync</c> answers with that organisation's connections and nothing else — on
/// every backend that ships.
/// </summary>
/// <remarks>
/// Three backends because they persist it three different ways: the Azure store maps an explicit
/// entity column, while the SQL and Dynamo stores serialise the whole model into one blob. A field
/// added to the model is therefore automatically carried by two of the three and silently dropped by
/// the third — which is exactly the drift <c>EntityRoundtripTests</c> was written for, and this suite
/// is its behavioural half: it also proves the FILTER, which no round-trip test can.
/// <para>
/// The filter matters beyond tidiness. It is what the admin API enforces per-organisation domain
/// uniqueness with and what home-realm discovery lists an organisation's IdPs from, so a backend whose
/// filter over-matched would offer one customer's IdP to another's login screen.
/// </para>
/// </remarks>
public abstract class OrganizationScopedConnectionStoreTestsBase
{
    protected abstract Task<(ISamlProviderStore Saml, IOidcProviderStore Oidc)> NewStoresAsync();

    [Fact]
    public async Task TheOrganizationSurvivesARoundTripAndListingFiltersToIt()
    {
        var (saml, oidc) = await NewStoresAsync();

        await saml.UpsertAsync(new SamlProviderConfig
        {
            ConnectionId = "s-acme",
            ConnectionName = "Acme ADFS",
            EntityId = "urn:acme",
            OrganizationId = "org-acme",
            AllowedDomains = ["acme.test"],
        });
        await saml.UpsertAsync(new SamlProviderConfig
        {
            ConnectionId = "s-tenant",
            ConnectionName = "Tenant-wide",
            EntityId = "urn:tenant",
        });
        await oidc.UpsertAsync(new OidcProviderConfig
        {
            ConnectionId = "o-acme",
            ConnectionName = "Acme Entra",
            MetadataLocation = "https://entra.test/.well-known/openid-configuration",
            ClientId = "c",
            ClientSecret = "s",
            OrganizationId = "org-acme",
        });
        await oidc.UpsertAsync(new OidcProviderConfig
        {
            ConnectionId = "o-beta",
            ConnectionName = "Beta Okta",
            MetadataLocation = "https://okta.test/.well-known/openid-configuration",
            ClientId = "c",
            ClientSecret = "s",
            OrganizationId = "org-beta",
        });

        // The value itself round-trips, and a tenant-level connection reads back as null rather than
        // as an empty string — the two are not the same to anything that tests `is null`.
        Assert.Equal("org-acme", (await saml.GetAsync("s-acme"))!.OrganizationId);
        Assert.Null((await saml.GetAsync("s-tenant"))!.OrganizationId);
        Assert.Equal("org-acme", (await oidc.GetAsync("o-acme"))!.OrganizationId);

        Assert.Equal("s-acme", Assert.Single(await saml.ListByOrganizationAsync("org-acme")).ConnectionId);
        Assert.Equal("o-acme", Assert.Single(await oidc.ListByOrganizationAsync("org-acme")).ConnectionId);
        Assert.Equal("o-beta", Assert.Single(await oidc.ListByOrganizationAsync("org-beta")).ConnectionId);

        // A tenant-level connection belongs to NO organisation — it must never be listed under one,
        // which is what would make it offered on an organisation's login screen.
        Assert.Empty(await saml.ListByOrganizationAsync("org-beta"));
        Assert.Empty(await saml.ListByOrganizationAsync("org-nonexistent"));
        Assert.Empty(await oidc.ListByOrganizationAsync("org-nonexistent"));

        // Ordinal, like every other identifier comparison in the product: a differently-cased id is a
        // different organisation, not a sloppy spelling of this one.
        Assert.Empty(await saml.ListByOrganizationAsync("ORG-ACME"));
    }

    [Fact]
    public async Task MovingAConnectionToTenantLevelRemovesItFromTheOrganizationListing()
    {
        var (saml, _) = await NewStoresAsync();

        await saml.UpsertAsync(new SamlProviderConfig
        {
            ConnectionId = "s1", ConnectionName = "Acme", EntityId = "urn:acme", OrganizationId = "org-acme",
        });
        Assert.Single(await saml.ListByOrganizationAsync("org-acme"));

        var read = await saml.GetAsync("s1");
        read!.OrganizationId = null;
        await saml.UpsertAsync(read);

        Assert.Empty(await saml.ListByOrganizationAsync("org-acme"));
        Assert.Null((await saml.GetAsync("s1"))!.OrganizationId);
    }
}

/// <summary>Azure Table Storage — the backend that maps an explicit entity column.</summary>
[Collection("Azurite")]
public sealed class TableOrganizationScopedConnectionStoreTests(AzuriteFixture azurite)
    : OrganizationScopedConnectionStoreTestsBase
{
    private readonly TableServiceClient _svc = new(azurite.ConnectionString);

    protected override Task<(ISamlProviderStore, IOidcProviderStore)> NewStoresAsync()
    {
        var prefix = $"orgconn{Guid.NewGuid():N}";
        var samlTable = _svc.GetTableClient($"{prefix}SamlProviders");
        var oidcTable = _svc.GetTableClient($"{prefix}OidcProviders");
        samlTable.CreateIfNotExists();
        oidcTable.CreateIfNotExists();
        return Task.FromResult<(ISamlProviderStore, IOidcProviderStore)>((
            new TableSamlProviderStore(samlTable, EnvPartitioner.Live),
            new TableOidcProviderStore(oidcTable, EnvPartitioner.Live)));
    }
}

/// <summary>DynamoDB — a whole-model JSON blob under one attribute.</summary>
[Collection("Dynamo")]
public sealed class DynamoOrganizationScopedConnectionStoreTests(DynamoFixture dynamo)
    : OrganizationScopedConnectionStoreTestsBase
{
    private readonly IAmazonDynamoDB _db = dynamo.CreateClient();

    protected override async Task<(ISamlProviderStore, IOidcProviderStore)> NewStoresAsync()
    {
        var prefix = $"orgconn{Guid.NewGuid():N}";
        await DynamoTableProvisioner.EnsureTableAsync(_db, $"{prefix}SamlProviders");
        await DynamoTableProvisioner.EnsureTableAsync(_db, $"{prefix}OidcProviders");
        return (
            new DynamoSamlProviderStore(new DynamoTable(_db, $"{prefix}SamlProviders"), EnvPartitioner.Live),
            new DynamoOidcProviderStore(new DynamoTable(_db, $"{prefix}OidcProviders"), EnvPartitioner.Live));
    }
}

/// <summary>
/// The SQL provider, over SQLite. Same serialisation path as PostgreSQL — both stores write the model
/// through <c>SqlJsonContext</c> into one <c>Data</c> column, so the dialect cannot change the answer.
/// </summary>
public sealed class SqlOrganizationScopedConnectionStoreTests : OrganizationScopedConnectionStoreTestsBase
{
    protected override async Task<(ISamlProviderStore, IOidcProviderStore)> NewStoresAsync()
    {
        var source = SqlTestSource.Sqlite();
        await source.EnsureTableAsync("SamlProviders");
        await source.EnsureTableAsync("OidcProviders");
        return (
            new SqlSamlProviderStore(new SqlTable(source, "SamlProviders"), EnvPartitioner.Live),
            new SqlOidcProviderStore(new SqlTable(source, "OidcProviders"), EnvPartitioner.Live));
    }
}
