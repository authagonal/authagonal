using Authagonal.Core.Models;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Tests;

/// <summary>
/// <see cref="PreAuthOrganizationResolver"/> — which organisation a request is for BEFORE anyone has
/// signed in, and the ways it must refuse to answer.
/// </summary>
/// <remarks>
/// Its answer decides which IdP an unauthenticated browser is sent to, so "resolves to nothing" is the
/// safe outcome and every refusal here is a redirect that does not happen. The precedence has to match
/// <see cref="OrganizationSelector"/>'s post-authentication order for the same three sources, or a
/// request would be federated to one organisation's IdP and then issued a token for another's.
/// </remarks>
public sealed class PreAuthOrganizationResolverTests
{
    private static PreAuthOrganizationResolver Resolver(
        WritableOrganizationStore organizations, string? tenantOrganizationId = null) =>
        new(organizations,
            new TestTenantContext(AuthagonalTestFactory.TestIssuer) { OrganizationId = tenantOrganizationId },
            NullLogger<PreAuthOrganizationResolver>.Instance);

    private static OAuthClient Client(params string[] restrictedTo) => new()
    {
        ClientId = "client-1",
        ClientName = "Client One",
        RestrictedToOrganizationIds = [.. restrictedTo],
    };

    // -----------------------------------------------------------------------
    // Precedence
    // -----------------------------------------------------------------------

    /// The upgrade guarantee: nothing asks for an organisation, so there is none, and every caller
    /// stays on the tenant-wide path it took before organisations existed.
    [Fact]
    public async Task NothingNamesAnOrganization_ResolvesToNone()
    {
        var organizations = new WritableOrganizationStore().With("org-a", "acme", "Acme");

        Assert.Null(await Resolver(organizations).ResolveAsync(null, Client()));
    }

    /// The parameter is caller-supplied and of unknown shape, so it is accepted as a slug or an id —
    /// the same rule the post-authentication selector applies to the same value.
    [Theory]
    [InlineData("acme")]
    [InlineData("org-a")]
    public async Task TheParameterResolvesAsASlugOrAnId(string value)
    {
        var organizations = new WritableOrganizationStore().With("org-a", "acme", "Acme");

        var resolved = await Resolver(organizations).ResolveAsync(value, Client());

        Assert.Equal("org-a", resolved?.Id);
    }

    /// A per-customer application names its organisation once, in registration, and its relying party
    /// never sends a parameter.
    [Fact]
    public async Task ASingleClientRestrictionSelectsThatOrganization()
    {
        var organizations = new WritableOrganizationStore().With("org-a", "acme", "Acme");

        var resolved = await Resolver(organizations).ResolveAsync(null, Client("org-a"));

        Assert.Equal("org-a", resolved?.Id);
    }

    /// Two or more is not a selection — the client serves several and the request named none. Answering
    /// with one of them would auto-challenge a user to an arbitrary customer's IdP.
    [Fact]
    public async Task SeveralClientRestrictions_ResolveToNone()
    {
        var organizations = new WritableOrganizationStore()
            .With("org-a", "acme", "Acme")
            .With("org-b", "beta", "Beta");

        Assert.Null(await Resolver(organizations).ResolveAsync(null, Client("org-a", "org-b")));
    }

    /// The lowest-precedence source: a host that pins an organisation per request (a custom domain).
    [Fact]
    public async Task TheTenantContextPinIsUsedWhenNothingElseNamesOne()
    {
        var organizations = new WritableOrganizationStore().With("org-a", "acme", "Acme");

        var resolved = await Resolver(organizations, tenantOrganizationId: "org-a").ResolveAsync(null, Client());

        Assert.Equal("org-a", resolved?.Id);
    }

    /// The parameter outranks the pin: an explicit request beats a hostname default.
    [Fact]
    public async Task TheParameterOutranksTheTenantContextPin()
    {
        var organizations = new WritableOrganizationStore()
            .With("org-a", "acme", "Acme")
            .With("org-b", "beta", "Beta");

        var resolved = await Resolver(organizations, tenantOrganizationId: "org-b").ResolveAsync("acme", Client());

        Assert.Equal("org-a", resolved?.Id);
    }

    /// …and a single client restriction sits between them.
    [Fact]
    public async Task TheClientRestrictionOutranksTheTenantContextPin()
    {
        var organizations = new WritableOrganizationStore()
            .With("org-a", "acme", "Acme")
            .With("org-b", "beta", "Beta");

        var resolved = await Resolver(organizations, tenantOrganizationId: "org-b").ResolveAsync(null, Client("org-a"));

        Assert.Equal("org-a", resolved?.Id);
    }

    // -----------------------------------------------------------------------
    // Refusals — each one is an auto-challenge that must not happen
    // -----------------------------------------------------------------------

    [Fact]
    public async Task AnUnknownValueResolvesToNone()
    {
        var organizations = new WritableOrganizationStore().With("org-a", "acme", "Acme");

        Assert.Null(await Resolver(organizations).ResolveAsync("no-such-org", Client()));
    }

    /// A disabled organisation mints no tokens, so offering its IdP would federate a user into a
    /// sign-in that is refused at the end of the round trip.
    [Fact]
    public async Task ADisabledOrganizationResolvesToNone()
    {
        var organizations = new WritableOrganizationStore().With("org-a", "acme", "Acme", enabled: false);

        Assert.Null(await Resolver(organizations).ResolveAsync("acme", Client()));
        Assert.Null(await Resolver(organizations).ResolveAsync(null, Client("org-a")));
        Assert.Null(await Resolver(organizations, tenantOrganizationId: "org-a").ResolveAsync(null, Client()));
    }

    /// The security boundary: a client restricted to one organisation may not be steered to another's
    /// IdP by a query parameter. It falls through rather than erroring — the refusal already exists,
    /// after authentication, where there is a user to refuse.
    [Fact]
    public async Task AParameterNamingAnOrganizationTheClientIsRestrictedAwayFrom_ResolvesToNone()
    {
        var organizations = new WritableOrganizationStore()
            .With("org-a", "acme", "Acme")
            .With("org-b", "beta", "Beta");

        Assert.Null(await Resolver(organizations).ResolveAsync("beta", Client("org-a")));
    }

    /// The same check applies to the hostname pin, which is not a caller-supplied value but is still
    /// not the client's registration speaking. Two restrictions, so rule 2 does not short-circuit and
    /// the pin is actually the value under test.
    [Fact]
    public async Task ATenantPinTheClientIsRestrictedAwayFrom_ResolvesToNone()
    {
        var organizations = new WritableOrganizationStore()
            .With("org-a", "acme", "Acme")
            .With("org-b", "beta", "Beta")
            .With("org-c", "gamma", "Gamma");

        Assert.Null(await Resolver(organizations, tenantOrganizationId: "org-b")
            .ResolveAsync(null, Client("org-a", "org-c")));
    }

    /// The endpoints with no client at all — /api/auth/providers and /api/auth/sso-check. Rule 2
    /// cannot apply, and nothing else changes.
    [Fact]
    public async Task WithNoClient_TheParameterAndThePinStillResolve()
    {
        var organizations = new WritableOrganizationStore().With("org-a", "acme", "Acme");

        Assert.Equal("org-a", (await Resolver(organizations).ResolveAsync("acme"))?.Id);
        Assert.Equal("org-a", (await Resolver(organizations, tenantOrganizationId: "org-a").ResolveAsync(null))?.Id);
    }

    // -----------------------------------------------------------------------
    // Listing an organisation's connections
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ListConnections_ReturnsBothProtocolsAndOnlyThisOrganizations()
    {
        var saml = new InMemorySamlProviderStore();
        var oidc = new InMemoryOidcProviderStore();

        await saml.UpsertAsync(new SamlProviderConfig
        {
            ConnectionId = "s1", ConnectionName = "Acme ADFS", EntityId = "urn:acme",
            OrganizationId = "org-a", AllowedDomains = ["acme.test"],
        });
        await oidc.UpsertAsync(new OidcProviderConfig
        {
            ConnectionId = "o1", ConnectionName = "Acme Entra", OrganizationId = "org-a",
        });
        await oidc.UpsertAsync(new OidcProviderConfig
        {
            ConnectionId = "o2", ConnectionName = "Beta Okta", OrganizationId = "org-b",
        });
        await oidc.UpsertAsync(new OidcProviderConfig { ConnectionId = "o3", ConnectionName = "Tenant-wide" });

        var connections = await PreAuthOrganizationResolver.ListConnectionsAsync(saml, oidc, "org-a");

        Assert.Equal(["o1", "s1"], connections.Select(c => c.ConnectionId).Order());

        var samlConnection = connections.Single(c => c.ConnectionId == "s1");
        Assert.Equal("saml", samlConnection.Type);
        Assert.Equal("/saml/s1/login", samlConnection.LoginUrl);
        Assert.True(samlConnection.ClaimsDomain("acme.test"));
        // Case-insensitively, because an email domain arrives however the user typed it.
        Assert.True(samlConnection.ClaimsDomain("ACME.TEST"));
        Assert.False(samlConnection.ClaimsDomain("beta.test"));

        var oidcConnection = connections.Single(c => c.ConnectionId == "o1");
        Assert.Equal("oidc", oidcConnection.Type);
        Assert.Equal("/oidc/o1/login", oidcConnection.LoginUrl);
        // A connection listing no domains claims none by this test, which is what makes the
        // auto-challenge rule ("one connection, no contradicting hint") the thing that reaches it.
        Assert.False(oidcConnection.ClaimsDomain("anything.test"));
    }
}
