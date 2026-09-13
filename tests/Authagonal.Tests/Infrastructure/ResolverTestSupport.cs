using Authagonal.Core.Stores;
using Authagonal.Server.Services;
using Authagonal.Server.Services.Oidc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Authagonal.Tests.Infrastructure;

/// <summary>
/// Builds a <see cref="UserStoreOidcSubjectResolver"/> for unit tests that exercise only the
/// authorize/claims paths. The upstream-refresh dependencies (provider store, discovery, secret
/// resolution, HTTP) are satisfied with inert fakes — tests covering revalidation supply real
/// doubles themselves.
/// </summary>
public static class ResolverTestSupport
{
    public static UserStoreOidcSubjectResolver NewResolver(
        IUserStore users,
        IScimGroupStore groups,
        IScimGroupRoleMappingStore mappings,
        IClientStore clients,
        IOidcProviderStore? oidcProviders = null,
        IUpstreamRefreshTokenStore? upstreamTokenStore = null,
        // Null leaves the resolver reading AuthUser.OrganizationId directly — the pre-organizations
        // behaviour, and what every test that predates them expects.
        OrganizationSelector? organizationSelector = null) =>
        new(
            users, groups, mappings, clients,
            oidcProviders ?? new InMemoryOidcProviderStore(),
            new OidcDiscoveryClient(
                new InertHttpClientFactory(),
                new MemoryCache(new MemoryCacheOptions()),
                Options.Create(new CacheOptions())),
            new PlaintextSecretProvider(),
            new InertHttpClientFactory(),
            NullLogger<UserStoreOidcSubjectResolver>.Instance,
            upstreamTokenStore,
            organizationSelector);

    private sealed class InertHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
