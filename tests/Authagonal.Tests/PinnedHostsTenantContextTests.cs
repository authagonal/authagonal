using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Authagonal.Tests;

/// <summary>
/// <see cref="DefaultTenantContext"/> pins hosts to organisations from the <c>Organizations:PinnedHosts</c>
/// configuration section, in either form an operator can write it, and a pin is authoritative only when
/// the context says so.
/// </summary>
public sealed class PinnedHostsTenantContextTests
{
    private const string Host = "login.acme.example";

    private static DefaultTenantContext ContextFor(IConfiguration configuration, string hostHeader)
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        accessor.HttpContext.Request.Host = new HostString(hostHeader);
        return new DefaultTenantContext(configuration, accessor);
    }

    private static IConfiguration Memory(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new[] { ("Issuer", (string?)"https://t.test") }
                .Concat(values)
                .Select(v => new KeyValuePair<string, string?>(v.Item1, v.Item2)))
            .Build();

    [Fact]
    public void CommandLineForm_PinsTheHost()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Issuer"] = "https://t.test" })
            .AddCommandLine([$"--Organizations:PinnedHosts:{Host}=Org_Cli"])
            .Build();

        var tenant = ContextFor(configuration, Host);

        Assert.Equal("Org_Cli", tenant.OrganizationId);
        Assert.True(tenant.OrganizationIdIsAuthoritative);
    }

    [Fact]
    public void EnvironmentForm_PinsTheHost()
    {
        // The variable name is unique to this test so a parallel test cannot see it.
        var host = $"env-{Guid.NewGuid():N}.acme.example";
        var variable = $"Organizations__PinnedHosts__{host}";
        Environment.SetEnvironmentVariable(variable, "Org_Env");
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Issuer"] = "https://t.test" })
                .AddEnvironmentVariables()
                .Build();

            var tenant = ContextFor(configuration, host);

            Assert.Equal("Org_Env", tenant.OrganizationId);
            Assert.True(tenant.OrganizationIdIsAuthoritative);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Theory]
    [InlineData("login.acme.example")]
    [InlineData("LOGIN.ACME.EXAMPLE")]
    [InlineData("Login.Acme.Example:8443")]
    [InlineData("login.acme.example:443")]
    public void HostMatch_IgnoresCaseAndPort(string hostHeader)
    {
        var tenant = ContextFor(Memory(($"Organizations:PinnedHosts:{Host}", "Org_A")), hostHeader);

        Assert.Equal("Org_A", tenant.OrganizationId);
        Assert.True(tenant.OrganizationIdIsAuthoritative);
    }

    [Fact]
    public void ConfiguredKeyCase_DoesNotMatter()
    {
        var tenant = ContextFor(Memory(("ORGANIZATIONS:PINNEDHOSTS:LOGIN.ACME.EXAMPLE", "Org_A")), Host);

        Assert.Equal("Org_A", tenant.OrganizationId);
    }

    [Fact]
    public void ADifferentHost_IsNotPinned()
    {
        var tenant = ContextFor(Memory(($"Organizations:PinnedHosts:{Host}", "Org_A")), "other.acme.example");

        Assert.Null(tenant.OrganizationId);
        Assert.False(tenant.OrganizationIdIsAuthoritative);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyValue_IsNoPin(string value)
    {
        var tenant = ContextFor(Memory(($"Organizations:PinnedHosts:{Host}", value)), Host);

        Assert.Null(tenant.OrganizationId);
        Assert.False(tenant.OrganizationIdIsAuthoritative);
    }

    [Fact]
    public void NoSection_IsTheBaseBehaviour()
    {
        var tenant = ContextFor(Memory(), Host);

        Assert.Equal("default", tenant.TenantId);
        Assert.Equal("https://t.test", tenant.Issuer);
        Assert.Null(tenant.OrganizationId);
        Assert.False(tenant.OrganizationIdIsAuthoritative);
    }

    [Fact]
    public void NoRequest_IsNoPin()
    {
        var tenant = new DefaultTenantContext(
            Memory(($"Organizations:PinnedHosts:{Host}", "Org_A")), new HttpContextAccessor());

        Assert.Null(tenant.OrganizationId);
        Assert.False(tenant.OrganizationIdIsAuthoritative);
    }

    [Fact]
    public void AMapEditedAfterConstruction_IsReadOnTheNextRequest()
    {
        var source = new Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource
        {
            InitialData = new Dictionary<string, string?> { ["Issuer"] = "https://t.test" },
        };
        var configuration = new ConfigurationBuilder().Add(source).Build();
        var tenant = ContextFor(configuration, Host);
        Assert.Null(tenant.OrganizationId);

        configuration[$"Organizations:PinnedHosts:{Host}"] = "Org_Late";

        Assert.Equal("Org_Late", tenant.OrganizationId);
    }

    [Fact]
    public void TheInterfaceDefault_IsNotAuthoritative()
    {
        ITenantContext tenant = new DefaultOnlyTenantContext();

        Assert.Equal("Org_X", tenant.OrganizationId);
        Assert.False(tenant.OrganizationIdIsAuthoritative);
    }

    private sealed class DefaultOnlyTenantContext : ITenantContext
    {
        public string TenantId => "t";
        public string Issuer => "https://t.test";
        public string? OrganizationId => "Org_X";
    }

    /// <summary>
    /// A context that reports an organisation without saying it is authoritative — a host's own context,
    /// written before the pin existed — is not enforced: the request is left exactly as it was.
    /// </summary>
    [Fact]
    public async Task ANonAuthoritativeOrganization_IsNotEnforcedOnAuthorize()
    {
        await using var factory = new AuthagonalTestFactory();
        await factory.SeedTestDataAsync();
        factory.OrganizationStore.With("Org_7fA2", "acme-eu", "Org");
        factory.OrganizationStore.With("Org_9zB4", "acme-us", "Org");
        factory.TenantContext.OrganizationId = "Org_7fA2";
        factory.TenantContext.OrganizationIdIsAuthoritative = false;
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync(
            "/connect/authorize?client_id=test-client&response_type=code&scope=openid&state=s" +
            "&redirect_uri=https%3A%2F%2Fapp.test%2Fcallback&organization=acme-us");

        Assert.NotEqual(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var location = response.Headers.Location?.OriginalString ?? "";
        Assert.DoesNotContain("not%20available", location);
        Assert.DoesNotContain("Org_7fA2", location);
    }

    /// <summary>The standalone server registers the partitioner its Table-storage services take from the container.</summary>
    [Fact]
    public void AddAuthagonal_RegistersTheLivePartitioner()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IUserStore>(new InMemoryUserStore());
        services.AddSingleton<IClientStore>(new InMemoryClientStore());
        services.AddSingleton<IGrantStore>(new InMemoryGrantStore());
        services.AddSingleton<ISigningKeyStore>(new InMemorySigningKeyStore());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Issuer"] = "https://t.test" })
            .Build();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddAuthagonal(configuration);

        using var provider = services.BuildServiceProvider();

        Assert.Equal(EnvPartitioner.Live, provider.GetRequiredService<EnvPartitioner>());
    }

    [Fact]
    public void AnEnvPartitionerTheHostRegisteredFirst_IsKept()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var custom = EnvPartitioner.Live;
        services.AddSingleton(custom);
        services.AddSingleton<IUserStore>(new InMemoryUserStore());
        services.AddSingleton<IClientStore>(new InMemoryClientStore());
        services.AddSingleton<IGrantStore>(new InMemoryGrantStore());
        services.AddSingleton<ISigningKeyStore>(new InMemorySigningKeyStore());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Issuer"] = "https://t.test" })
            .Build();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddAuthagonal(configuration);

        Assert.Single(services, d => d.ServiceType == typeof(EnvPartitioner));
    }
}
