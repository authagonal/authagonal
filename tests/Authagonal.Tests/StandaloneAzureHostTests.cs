using Authagonal.Core.Services;
using Authagonal.Server;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Authagonal.Tests;

/// <summary>
/// A standalone host on the Azure Table backend, built through the real <c>AddAuthagonal</c> with nothing
/// else registered. <see cref="GrantReconciliationService"/> needs an <see cref="EnvPartitioner"/> and
/// nothing registered one, so such a host failed to start with "Unable to resolve service for type
/// 'Authagonal.Core.Services.EnvPartitioner' while attempting to activate
/// 'Authagonal.Server.Services.GrantReconciliationService'". Needs Azurite because AddTableStorage creates
/// its tables eagerly.
/// </summary>
[Collection("Azurite")]
public sealed class StandaloneAzureHostTests(AzuriteFixture azurite)
{
    private WebApplication BuildHost(Action<IServiceCollection>? beforeAddAuthagonal = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["Storage:ConnectionString"] = azurite.ConnectionString;
        builder.Configuration["Auth:Issuer"] = "https://standalone.test";
        beforeAddAuthagonal?.Invoke(builder.Services);
        builder.Services.AddAuthagonal(builder.Configuration);
        return builder.Build();
    }

    [Fact]
    public void StandaloneAzureHost_ResolvesItsHostedServices_IncludingGrantReconciliation()
    {
        using var app = BuildHost();

        var hosted = app.Services.GetServices<IHostedService>().ToList();

        Assert.Contains(hosted, s => s is GrantReconciliationService);
        Assert.Same(EnvPartitioner.Live, app.Services.GetRequiredService<EnvPartitioner>());
    }

    [Fact]
    public void AHostThatRegistersItsOwnEnvPartitioner_KeepsIt()
    {
        var own = new EnvPartitioner("sandbox-7");

        using var app = BuildHost(services => services.AddSingleton(own));

        Assert.Same(own, app.Services.GetRequiredService<EnvPartitioner>());
        Assert.NotSame(EnvPartitioner.Live, app.Services.GetRequiredService<EnvPartitioner>());
        Assert.Contains(app.Services.GetServices<IHostedService>(), s => s is GrantReconciliationService);
    }
}
