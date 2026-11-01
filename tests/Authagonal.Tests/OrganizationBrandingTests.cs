using System.Text.Json;
using System.Text.Json.Nodes;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Protocol.Models;
using Authagonal.Protocol.Services;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Tests;

/// <summary>
/// The login page renders the branding of the organisation the request is for, merged over the
/// tenant's: <see cref="PreAuthOrganizationResolver.ResolveForLoginPageAsync"/> says which organisation,
/// <see cref="OrganizationBranding.Merge"/> says what its branding does to the tenant's.
/// </summary>
/// <remarks>
/// Ported from the multi-tenant host's <c>OrganizationBrandingTests</c>, each method under its own
/// name. The merge contract is the library's <c>Organization.BrandingJson</c> doc: "Null means inherit
/// everything. Hosts that render it merge field by field, so an organisation that sets only a logo keeps
/// the tenant's colours." Where a premise of the original is that host's alone, the test of that name
/// asserts the library's equivalent: a domain record's organisation is an <c>Organizations:PinnedHosts</c>
/// entry, a tenant-level domain is a host with no entry, branding is the JSON the SPA reads rather than
/// a typed model, and a host with no organisation store is a store that throws.
/// </remarks>
public class OrganizationBrandingTests
{
    private const string Host = "auth.acme.com";

    private static JsonObject TenantBranding() => new()
    {
        ["appName"] = "Acme",
        ["primaryColor"] = "#111111",
        ["logoUrl"] = "https://cdn.acme.com/acme.png",
        ["supportEmail"] = "help@acme.com",
        ["showForgotPassword"] = true,
        ["showRegistration"] = true,
        ["darkMode"] = "auto",
    };

    private static Organization OrganizationWith(string? brandingJson) => new()
    {
        Id = "Org_7fA2",
        Slug = "acme-eu",
        DisplayName = "Acme Europe",
        BrandingJson = brandingJson,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static JsonObject Merge(JsonObject tenant, string? brandingJson) =>
        OrganizationBranding.Merge(tenant, OrganizationWith(brandingJson), NullLogger.Instance);

    private static string? Text(JsonObject branding, string key) => branding[key]?.GetValue<string>();

    // ── Merge ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole contract in one test: what the organisation sets wins, what it leaves out is still the
    /// tenant's.
    /// </summary>
    [Fact]
    public void Merge_WithSomeFieldsSet_OverridesThoseAndInheritsTheRest()
    {
        var merged = Merge(
            TenantBranding(),
            """{"appName":"Acme Europe","logoUrl":"https://cdn.acme.com/eu.png"}""");

        Assert.Equal("Acme Europe", Text(merged, "appName"));
        Assert.Equal("https://cdn.acme.com/eu.png", Text(merged, "logoUrl"));
        // Untouched by the organisation, so still the tenant's.
        Assert.Equal("#111111", Text(merged, "primaryColor"));
        Assert.Equal("help@acme.com", Text(merged, "supportEmail"));
        Assert.Equal("auto", Text(merged, "darkMode"));
    }

    /// <summary>A boolean is a field like any other: its absence must not read as <c>false</c>.</summary>
    [Fact]
    public void Merge_WithABooleanSetToFalse_OverridesIt_AndAnAbsentBooleanIsInherited()
    {
        var merged = Merge(TenantBranding(), """{"showForgotPassword":false}""");

        Assert.False(merged["showForgotPassword"]!.GetValue<bool>());
        // showRegistration was not named, so it keeps the tenant's true rather than defaulting, and it
        // is policy-driven besides (see Merge_WithShowRegistration_...).
        Assert.True(merged["showRegistration"]!.GetValue<bool>());
    }

    [Fact]
    public void Merge_WithNoBrandingJson_ReturnsTheTenantBrandingUnchanged()
    {
        var tenant = TenantBranding();

        var merged = Merge(tenant, null);

        Assert.True(JsonNode.DeepEquals(tenant, merged));
        Assert.NotSame(tenant, merged);
    }

    [Fact]
    public void Merge_WithMalformedJson_LeavesTheTenantBrandingUnchanged()
    {
        var tenant = TenantBranding();

        var merged = Merge(tenant, """{"appName": """);

        Assert.True(JsonNode.DeepEquals(tenant, merged));
        Assert.Equal("Acme", Text(merged, "appName"));
    }

    /// <summary>Valid JSON that is not an object has no fields to merge, so it is the same answer.</summary>
    [Fact]
    public void Merge_WithJsonThatIsNotAnObject_LeavesTheTenantBrandingUnchanged()
    {
        var tenant = TenantBranding();

        var merged = Merge(tenant, """["appName","Acme Europe"]""");

        Assert.True(JsonNode.DeepEquals(tenant, merged));
    }

    /// <summary>
    /// The organisation record is shared with hosts whose branding model is not this one, so a key
    /// this host does not know is data for somebody else, not an error.
    /// </summary>
    [Fact]
    public void Merge_WithKeysThatAreNotBrandingFields_IgnoresThemAndAppliesTheRest()
    {
        var merged = Merge(
            TenantBranding(),
            """{"appName":"Acme Europe","someOtherHostsField":{"a":1},"tagline":"hi"}""");

        Assert.Equal("Acme Europe", Text(merged, "appName"));
        Assert.Equal("#111111", Text(merged, "primaryColor"));
        Assert.False(merged.ContainsKey("someOtherHostsField"));
        Assert.False(merged.ContainsKey("tagline"));
    }

    /// <summary>
    /// An explicit null inherits, exactly as an absent key does. It does NOT clear the tenant's value:
    /// the JSON has no way to say "unset" as distinct from "set to nothing", and clearing is the
    /// destructive reading of an ambiguous one.
    /// </summary>
    [Fact]
    public void Merge_WithAnExplicitNull_KeepsTheTenantValue()
    {
        var merged = Merge(
            TenantBranding(),
            """{"logoUrl":null,"supportEmail":null,"appName":"Acme Europe"}""");

        Assert.Equal("https://cdn.acme.com/acme.png", Text(merged, "logoUrl"));
        Assert.Equal("help@acme.com", Text(merged, "supportEmail"));
        Assert.Equal("Acme Europe", Text(merged, "appName"));
    }

    /// <summary>
    /// One unusable value is one unusable field, not a discarded document: the rest of it is still
    /// what the operator meant.
    /// </summary>
    [Fact]
    public void Merge_WithAValueOfTheWrongType_KeepsThatFieldAndStillAppliesTheOthers()
    {
        var merged = Merge(
            TenantBranding(),
            """{"showRegistration":"yes please","showForgotPassword":"no thanks","darkMode":5,"primaryColor":"#22cc88"}""");

        Assert.True(merged["showRegistration"]!.GetValue<bool>());
        Assert.True(merged["showForgotPassword"]!.GetValue<bool>());
        Assert.Equal("auto", Text(merged, "darkMode"));
        Assert.Equal("#22cc88", Text(merged, "primaryColor"));
    }

    /// <summary>
    /// The tenant's policy decides whether the login page advertises registration (a tenant whose public
    /// sign-up is disabled, and every admin tenant, has it off). An organisation is not the authority for
    /// that decision, so its brandingJson must not be able to put the hosted registration link back:
    /// the API behind it stays shut either way, and advertising a link that is refused is precisely the
    /// inconsistency forcing it off exists to prevent.
    /// </summary>
    [Fact]
    public void Merge_WithShowRegistration_CannotReEnableWhatTheTenantPolicyTurnedOff()
    {
        var tenant = TenantBranding();
        tenant["showRegistration"] = false; // as the host leaves it for a no-public-signup tenant

        var merged = Merge(tenant, """{"showRegistration":true,"appName":"Acme Europe"}""");

        Assert.False(merged["showRegistration"]!.GetValue<bool>());
        // The rest of the document is still applied: one excluded key is not a discarded document.
        Assert.Equal("Acme Europe", Text(merged, "appName"));
    }

    /// <summary>
    /// showForgotPassword is deliberately NOT policy-driven, since nothing in the host forces it, so an
    /// organisation may set it. Asserted so the exclusion list does not quietly grow.
    /// </summary>
    [Fact]
    public void Merge_WithShowForgotPassword_StillOverrides()
    {
        var merged = Merge(TenantBranding(), """{"showForgotPassword":false}""");

        Assert.False(merged["showForgotPassword"]!.GetValue<bool>());
    }

    [Fact]
    public void Merge_WithLanguages_ReplacesTheTenantList()
    {
        var tenant = TenantBranding();
        tenant["languages"] = JsonNode.Parse("""[{"code":"en","label":"English"},{"code":"fr","label":"Français"}]""");

        var merged = Merge(tenant, """{"languages":[{"code":"de","label":"Deutsch"}]}""");

        Assert.Equal(["de"], merged["languages"]!.AsArray().Select(l => l!["code"]!.GetValue<string>()));
    }

    /// <summary>
    /// The tenant's branding is shared by every request for that tenant, so merging onto it directly
    /// would leak one organisation's branding to the whole tenant.
    /// </summary>
    [Fact]
    public void Merge_DoesNotMutateTheTenantBranding()
    {
        var tenant = TenantBranding();
        var before = tenant.ToJsonString();

        var merged = Merge(tenant, """{"appName":"Acme Europe"}""");

        Assert.Equal("Acme Europe", Text(merged, "appName"));
        Assert.Equal("Acme", Text(tenant, "appName"));
        Assert.Equal(before, tenant.ToJsonString());
        Assert.NotSame(tenant, merged);
    }

    // The branding the SPA reads is wider than any one host's model: an operator's branding.json carries
    // keys the library does not know, and the merge must carry them through untouched.
    [Fact]
    public void Merge_WithOperatorKeysTheLibraryDoesNotModel_CarriesThemThrough()
    {
        var tenant = TenantBranding();
        tenant["operatorOnly"] = JsonNode.Parse("""{"nested":[1,2]}""");

        var merged = Merge(tenant, """{"appName":"Acme Europe"}""");

        Assert.True(JsonNode.DeepEquals(tenant["operatorOnly"], merged["operatorOnly"]));
    }

    /// <summary>Names are matched without regard to case and written under the canonical one.</summary>
    [Fact]
    public void Merge_WithKeysInAnotherCase_WritesThemUnderTheCanonicalName()
    {
        var merged = Merge(TenantBranding(), """{"PrimaryColor":"#222222","APPNAME":"Acme Europe"}""");

        Assert.Equal("#222222", Text(merged, "primaryColor"));
        Assert.Equal("Acme Europe", Text(merged, "appName"));
        Assert.False(merged.ContainsKey("PrimaryColor"));
        Assert.False(merged.ContainsKey("APPNAME"));
        Assert.Equal(TenantBranding().Count, merged.Count);
    }

    [Fact]
    public void Merge_WithEmailSurfaceColours_AppliesThem()
    {
        var merged = Merge(
            TenantBranding(),
            """{"emailBg":"#1","emailHeaderColor":"#2","emailCardBg":"#3","emailFooterColor":"#4","emailLogoBg":"#5"}""");

        Assert.Equal(["#1", "#2", "#3", "#4", "#5"],
            new[] { "emailBg", "emailHeaderColor", "emailCardBg", "emailFooterColor", "emailLogoBg" }
                .Select(k => Text(merged, k)));
    }

    /// <summary>A localised string takes a plain string or an object of strings, and nothing else.</summary>
    [Theory]
    [InlineData("\"Welcome\"", true)]
    [InlineData("""{"en":"Welcome","fr":"Bienvenue"}""", true)]
    [InlineData("""{"en":5}""", false)]
    [InlineData("5", false)]
    [InlineData("""["Welcome"]""", false)]
    public void Merge_WithALocalizedString_TakesAStringOrAnAllStringObject(string value, bool accepted)
    {
        var tenant = TenantBranding();
        tenant["welcomeTitle"] = "Tenant welcome";

        var merged = Merge(tenant, $$"""{"welcomeTitle":{{value}}}""");

        Assert.True(JsonNode.DeepEquals(
            accepted ? JsonNode.Parse(value) : JsonValue.Create("Tenant welcome"), merged["welcomeTitle"]));
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("""[{"code":"fr"}]""")]
    [InlineData("""[{"code":1,"label":"F"}]""")]
    [InlineData("\"fr\"")]
    [InlineData("{}")]
    public void Merge_WithALanguagesValueOfTheWrongKind_KeepsTheTenantList(string value)
    {
        var tenant = TenantBranding();
        tenant["languages"] = JsonNode.Parse("""[{"code":"en","label":"English"}]""");

        var merged = Merge(tenant, $$"""{"languages":{{value}}}""");

        Assert.True(JsonNode.DeepEquals(tenant["languages"], merged["languages"]));
    }

    // ── Resolution ─────────────────────────────────────────────────────────────────────────────────

    private sealed class StubOrganizationStore : IOrganizationStore
    {
        private readonly List<Organization> _organizations = [];

        public StubOrganizationStore Add(Organization organization)
        {
            _organizations.Add(organization);
            return this;
        }

        public Task<Organization?> GetAsync(string organizationId, CancellationToken ct = default) =>
            Task.FromResult(_organizations.FirstOrDefault(o => o.Id == organizationId));

        public Task<Organization?> GetBySlugAsync(string slug, CancellationToken ct = default) =>
            Task.FromResult(_organizations.FirstOrDefault(o => o.Slug == slug));

        public Task<IReadOnlyList<Organization>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Organization>>(_organizations);

        public Task UpsertAsync(Organization organization, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string organizationId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// A host with no organisation store the resolver can read: every member throws. The multi-tenant
    /// host this was ported from ran its resolver with no store registered; the library's resolver has a
    /// store or none, and a store that cannot answer is the same page: tenant branding.
    /// </summary>
    private sealed class ThrowingOrganizationStore : IOrganizationStore
    {
        public Task<Organization?> GetAsync(string organizationId, CancellationToken ct = default) =>
            throw new InvalidOperationException("no organisation store");

        public Task<Organization?> GetBySlugAsync(string slug, CancellationToken ct = default) =>
            throw new InvalidOperationException("no organisation store");

        public Task<IReadOnlyList<Organization>> ListAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("no organisation store");

        public Task UpsertAsync(Organization organization, CancellationToken ct = default) =>
            throw new InvalidOperationException("no organisation store");

        public Task DeleteAsync(string organizationId, CancellationToken ct = default) =>
            throw new InvalidOperationException("no organisation store");
    }

    private sealed class StubClientStore : IClientStore
    {
        private readonly List<OAuthClient> _clients = [];

        public StubClientStore Add(OAuthClient client)
        {
            _clients.Add(client);
            return this;
        }

        public Task<OAuthClient?> GetAsync(string clientId, CancellationToken ct = default) =>
            Task.FromResult(_clients.FirstOrDefault(c => c.ClientId == clientId));

        public Task<IReadOnlyList<OAuthClient>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OAuthClient>>(_clients);

        public Task UpsertAsync(OAuthClient client, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string clientId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Backs <see cref="IGrantStore"/> with a plain dictionary keyed on <see cref="PersistedGrant.Key"/>:
    /// enough to seed a pushed authorization request for the resolver to read. Every mutating member
    /// throws, since these tests only ever read a grant seeded via <see cref="Add"/>.
    /// </summary>
    private class StubGrantStore : IGrantStore
    {
        private readonly Dictionary<string, PersistedGrant> _grants = new(StringComparer.Ordinal);

        public StubGrantStore Add(PersistedGrant grant)
        {
            _grants[grant.Key] = grant;
            return this;
        }

        public virtual Task<PersistedGrant?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(_grants.GetValueOrDefault(key));

        public Task StoreAsync(PersistedGrant grant, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task ConsumeAsync(string key, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> TryConsumeAsync(string key, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> TryMarkConsumedAsync(PersistedGrant grant, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> TryUpdateDataIfUnconsumedAsync(PersistedGrant grant, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task RemoveAsync(string key, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task RemoveAllBySubjectAsync(string subjectId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task RemoveAllBySubjectAndClientAsync(string subjectId, string clientId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task RemoveBySubjectAsync(
            string subjectId, IReadOnlyCollection<string> types, string? clientId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<int> RemoveBySessionAsync(
            string subjectId, IReadOnlyCollection<string> types, string sessionId, bool invert = false,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PersistedGrant>> GetBySubjectAsync(string subjectId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task RemoveExpiredAsync(DateTimeOffset cutoff, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingGrantStore : StubGrantStore
    {
        public override Task<PersistedGrant?> GetAsync(string key, CancellationToken ct = default) =>
            throw new InvalidOperationException("grant store down");
    }

    private static HttpRequest LoginRequest(string? returnUrl)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(Host);
        context.Request.Path = "/login";
        if (returnUrl is not null)
            context.Request.QueryString = QueryString.Create("returnUrl", returnUrl);
        return context.Request;
    }

    private static HttpRequest DocumentRequest(string path, string organization)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("acme.authagonal.io");
        context.Request.Path = path;
        context.Request.QueryString = QueryString.Create("organization", organization);
        return context.Request;
    }

    /// <summary>The page's branding and the organisation it was resolved for.</summary>
    private sealed record ResolvedBranding(JsonObject Branding, Organization? Organization);

    /// <summary>
    /// Resolves the way the standalone server does for a login page: the pin is whatever
    /// <c>Organizations:PinnedHosts</c> holds for the request's host (an <see cref="ITenantContext"/>
    /// reading it per request, as <see cref="DefaultTenantContext"/> does), and the branding is the
    /// tenant's with the resolved organisation's merged over it.
    /// </summary>
    private static async Task<ResolvedBranding> ResolveAsync(
        HttpRequest request, IOrganizationStore organizations, string? pinnedOrganizationId = null,
        IGrantStore? grantStore = null, IClientStore? clients = null)
    {
        var configuration = new Dictionary<string, string?> { ["Issuer"] = "https://auth.acme.com" };
        if (pinnedOrganizationId is not null)
            configuration[$"{DefaultTenantContext.PinnedHostsSection}:{request.Host.Host}"] = pinnedOrganizationId;

        var tenant = new DefaultTenantContext(
            new ConfigurationBuilder().AddInMemoryCollection(configuration).Build(),
            new HttpContextAccessor { HttpContext = request.HttpContext });

        return await ResolveAsync(request, tenant, organizations, grantStore, clients);
    }

    private static async Task<ResolvedBranding> ResolveAsync(
        HttpRequest request, ITenantContext tenant, IOrganizationStore organizations,
        IGrantStore? grantStore = null, IClientStore? clients = null)
    {
        var resolver = new PreAuthOrganizationResolver(
            organizations, tenant, NullLogger<PreAuthOrganizationResolver>.Instance, grantStore, clients);

        var organization = await resolver.ResolveForLoginPageAsync(request, CancellationToken.None);

        return new ResolvedBranding(
            organization is null
                ? TenantBranding()
                : OrganizationBranding.Merge(TenantBranding(), organization, NullLogger.Instance),
            organization);
    }

    /// <summary>An organisation invite with no pinned domain links to
    /// <c>/accept-invite?p=…&amp;organization={slug}</c>, and that page wears the organisation's branding.</summary>
    [Fact]
    public async Task Resolve_AcceptInvitePageNamingAnOrganization_UsesItsBranding()
    {
        var store = new StubOrganizationStore().Add(OrganizationWith("""{"appName":"Acme Europe"}"""));

        var resolved = await ResolveAsync(DocumentRequest("/accept-invite", "acme-eu"), store);

        Assert.Equal("Org_7fA2", resolved.Organization!.Id);
        Assert.Equal("Acme Europe", Text(resolved.Branding, "appName"));
    }

    /// <summary>The same bare parameter on the LOGIN document is ignored: that page stands in for an
    /// authorize request, and its organisation must be the one the request will mint a token for.</summary>
    [Fact]
    public async Task Resolve_LoginPageWithABareOrganizationParameter_IgnoresIt()
    {
        var store = new StubOrganizationStore().Add(OrganizationWith("""{"appName":"Acme Europe"}"""));

        var resolved = await ResolveAsync(DocumentRequest("/login", "acme-eu"), store);

        Assert.Null(resolved.Organization);
        Assert.Equal("Acme", Text(resolved.Branding, "appName"));
    }

    [Fact]
    public async Task Resolve_WithNoOrganizationAnywhere_ReturnsTenantBranding()
    {
        var resolved = await ResolveAsync(
            LoginRequest("/connect/authorize?client_id=app"), new StubOrganizationStore());

        Assert.Null(resolved.Organization);
        Assert.Equal("Acme", Text(resolved.Branding, "appName"));
    }

    [Fact]
    public async Task Resolve_WithAPinnedDomain_UsesThePinnedOrganizationsBranding()
    {
        var store = new StubOrganizationStore().Add(OrganizationWith("""{"appName":"Acme Europe"}"""));

        var resolved = await ResolveAsync(LoginRequest(null), store, pinnedOrganizationId: "Org_7fA2");

        Assert.Equal("Org_7fA2", resolved.Organization!.Id);
        Assert.Equal("Acme Europe", Text(resolved.Branding, "appName"));
    }

    /// <summary>
    /// On an unpinned host, the organisation comes from the authorize request this login page is
    /// standing in for, which rides along as returnUrl, because the library redirects to
    /// <c>{LoginAppUrl}?returnUrl={the whole authorize URL}</c>.
    /// </summary>
    [Theory]
    [InlineData("organization", "acme-eu")]
    [InlineData("org_slug", "acme-eu")]
    [InlineData("org_id", "Org_7fA2")]
    public async Task Resolve_WithAnOrganizationOnTheAuthorizeReturnUrl_UsesIt(string parameter, string value)
    {
        var store = new StubOrganizationStore().Add(OrganizationWith("""{"appName":"Acme Europe"}"""));

        var resolved = await ResolveAsync(
            LoginRequest($"/connect/authorize?client_id=app&{parameter}={value}"),
            store);

        Assert.Equal("Org_7fA2", resolved.Organization!.Id);
        Assert.Equal("Acme Europe", Text(resolved.Branding, "appName"));
    }

    /// <summary>
    /// The domain pin wins. The two can only disagree on a request the pin middleware is about to
    /// refuse anyway, so the page must not render the organisation that is not going to be issued.
    /// </summary>
    [Fact]
    public async Task Resolve_WithBothAPinAndAReturnUrlOrganization_PrefersThePin()
    {
        var store = new StubOrganizationStore()
            .Add(OrganizationWith("""{"appName":"Acme Europe"}"""))
            .Add(new Organization
            {
                Id = "Org_9zB4",
                Slug = "acme-us",
                DisplayName = "Acme US",
                BrandingJson = """{"appName":"Acme US"}""",
                CreatedAt = DateTimeOffset.UtcNow,
            });

        var resolved = await ResolveAsync(
            LoginRequest("/connect/authorize?client_id=app&organization=acme-us"),
            store, pinnedOrganizationId: "Org_7fA2");

        Assert.Equal("Org_7fA2", resolved.Organization!.Id);
        Assert.Equal("Acme Europe", Text(resolved.Branding, "appName"));
    }

    /// <summary>
    /// An unstyled login page is a far better failure than no login page, so neither a deleted
    /// organisation nor a host whose organisation store cannot answer is an error.
    /// </summary>
    [Fact]
    public async Task Resolve_WithADanglingDomainPin_ReturnsTenantBranding()
    {
        var resolved = await ResolveAsync(
            LoginRequest(null), new StubOrganizationStore(), pinnedOrganizationId: "Org_Gone");

        Assert.Null(resolved.Organization);
        Assert.Equal("Acme", Text(resolved.Branding, "appName"));
    }

    /// <summary>
    /// The pin middleware treats a pin that resolves to nothing as no pin at all and lets the request's
    /// own organization parameter through unrefused, so a token WILL be minted for that organisation.
    /// Short-circuiting on the configured id here would render the tenant's branding for it, and the page
    /// and the token would name different organisations.
    /// </summary>
    [Fact]
    public async Task Resolve_WithADanglingPinAndAnOrganizationOnTheReturnUrl_UsesTheReturnUrlOrganization()
    {
        var store = new StubOrganizationStore().Add(OrganizationWith("""{"appName":"Acme Europe"}"""));

        var resolved = await ResolveAsync(
            LoginRequest("/connect/authorize?client_id=app&organization=acme-eu"),
            store, pinnedOrganizationId: "Org_Gone");

        Assert.Equal("Org_7fA2", resolved.Organization!.Id);
        Assert.Equal("Acme Europe", Text(resolved.Branding, "appName"));
    }

    [Fact]
    public async Task Resolve_WithNoOrganizationStoreRegistered_ReturnsTenantBranding()
    {
        var resolved = await ResolveAsync(
            LoginRequest(null), new ThrowingOrganizationStore(), pinnedOrganizationId: "Org_7fA2");

        Assert.Null(resolved.Organization);
        Assert.Equal("Acme", Text(resolved.Branding, "appName"));
    }

    [Fact]
    public async Task Resolve_WithAReturnUrlCarryingNoQueryString_ReturnsTenantBranding()
    {
        var store = new StubOrganizationStore().Add(OrganizationWith("""{"appName":"Acme Europe"}"""));

        var resolved = await ResolveAsync(LoginRequest("/account"), store);

        Assert.Null(resolved.Organization);
        Assert.Equal("Acme", Text(resolved.Branding, "appName"));
    }

    // ── Client-restricted organisation (no explicit parameter) ───────────────────────────────────────

    /// <summary>
    /// <c>OrganizationSelector</c>'s own rule, mirrored for the login page: a client restricted to
    /// exactly one organisation is scoped to it even when the request that reaches it names none at
    /// all, a per-customer application that never sends an organisation parameter.
    /// </summary>
    [Fact]
    public async Task Resolve_WithAClientRestrictedToOneOrganization_ResolvesItWithoutAParameter()
    {
        var organizations = new StubOrganizationStore().Add(OrganizationWith("""{"appName":"Acme Europe"}"""));
        var clients = new StubClientStore().Add(new OAuthClient { ClientId = "app", RestrictedToOrganizationIds = ["Org_7fA2"] });

        var resolved = await ResolveAsync(
            LoginRequest("/connect/authorize?client_id=app"), organizations, clients: clients);

        Assert.Equal("Org_7fA2", resolved.Organization!.Id);
        Assert.Equal("Acme Europe", Text(resolved.Branding, "appName"));
    }

    /// <summary>Several entries do not select (the request still has to say which), so this resolves
    /// nothing rather than guessing.</summary>
    [Fact]
    public async Task Resolve_WithAClientRestrictedToSeveralOrganizations_ReturnsTenantBranding()
    {
        var organizations = new StubOrganizationStore()
            .Add(OrganizationWith("""{"appName":"Acme Europe"}"""))
            .Add(new Organization
            {
                Id = "Org_9zB4", Slug = "acme-us", DisplayName = "Acme US",
                BrandingJson = null, CreatedAt = DateTimeOffset.UtcNow,
            });
        var clients = new StubClientStore().Add(
            new OAuthClient { ClientId = "app", RestrictedToOrganizationIds = ["Org_7fA2", "Org_9zB4"] });

        var resolved = await ResolveAsync(
            LoginRequest("/connect/authorize?client_id=app"), organizations, clients: clients);

        Assert.Null(resolved.Organization);
        Assert.Equal("Acme", Text(resolved.Branding, "appName"));
    }

    /// <summary>A disabled client is ignored, not honoured: the same restriction on a client with
    /// <c>Enabled=false</c> resolves nothing.</summary>
    [Fact]
    public async Task Resolve_WithADisabledRestrictedClient_ReturnsTenantBranding()
    {
        var organizations = new StubOrganizationStore().Add(OrganizationWith("""{"appName":"Acme Europe"}"""));
        var clients = new StubClientStore().Add(
            new OAuthClient { ClientId = "app", Enabled = false, RestrictedToOrganizationIds = ["Org_7fA2"] });

        var resolved = await ResolveAsync(
            LoginRequest("/connect/authorize?client_id=app"), organizations, clients: clients);

        Assert.Null(resolved.Organization);
        Assert.Equal("Acme", Text(resolved.Branding, "appName"));
    }

    /// <summary>An unregistered client_id is not an error, just nothing to restrict to.</summary>
    [Fact]
    public async Task Resolve_WithAnUnknownClientId_ReturnsTenantBranding()
    {
        var organizations = new StubOrganizationStore().Add(OrganizationWith("""{"appName":"Acme Europe"}"""));
        var clients = new StubClientStore();

        var resolved = await ResolveAsync(
            LoginRequest("/connect/authorize?client_id=ghost"), organizations, clients: clients);

        Assert.Null(resolved.Organization);
        Assert.Equal("Acme", Text(resolved.Branding, "appName"));
    }

    /// <summary>
    /// The explicit parameter is still the highest-precedence signal: a client restricted to one
    /// organisation does not override a request that names a different one outright. (The library's
    /// own <c>OrganizationSelector</c> would go on to refuse such a request as <c>access_denied</c> at
    /// the token end; this resolver only renders branding, so it shows what was actually requested.)
    /// </summary>
    [Fact]
    public async Task Resolve_WithAnOrganizationParameterAndARestrictedClient_PrefersTheParameter()
    {
        var organizations = new StubOrganizationStore()
            .Add(OrganizationWith("""{"appName":"Acme Europe"}"""))
            .Add(new Organization
            {
                Id = "Org_9zB4", Slug = "acme-us", DisplayName = "Acme US",
                BrandingJson = """{"appName":"Acme US"}""", CreatedAt = DateTimeOffset.UtcNow,
            });
        var clients = new StubClientStore().Add(new OAuthClient { ClientId = "app", RestrictedToOrganizationIds = ["Org_9zB4"] });

        var resolved = await ResolveAsync(
            LoginRequest("/connect/authorize?client_id=app&organization=acme-eu"),
            organizations, clients: clients);

        Assert.Equal("Org_7fA2", resolved.Organization!.Id);
        Assert.Equal("Acme Europe", Text(resolved.Branding, "appName"));
    }

    // ── Pushed authorization requests (RFC 9126) ──────────────────────────────────────────────────────

    private const string PushedRequestUri = ProtocolPushedAuthorizationService.RequestUriPrefix + "e2e-test-abc123";

    private static PersistedGrant PushedAuthorizationGrant(
        string requestUri, string clientId, Dictionary<string, string[]> parameters, DateTimeOffset? expiresAt = null)
    {
        var expiry = expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(1);
        return new PersistedGrant
        {
            Key = requestUri,
            Type = ProtocolPushedAuthorizationService.GrantType,
            ClientId = clientId,
            Data = JsonSerializer.Serialize(new PushedAuthorizationRequest
            {
                RequestUri = requestUri,
                ClientId = clientId,
                Parameters = parameters,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = expiry,
            }),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiry,
        };
    }

    private static HttpRequest PushedLoginRequest() =>
        LoginRequest($"/connect/authorize?client_id=app&request_uri={Uri.EscapeDataString(PushedRequestUri)}");

    /// <summary>
    /// The library stops reading the outer query for an organisation once <c>request_uri</c> is
    /// present, so the parameter has to be read out of the pushed record's own payload instead.
    /// </summary>
    [Fact]
    public async Task Resolve_WithAPushedRequestNamingAnOrganization_ResolvesIt()
    {
        var organizations = new StubOrganizationStore().Add(OrganizationWith("""{"appName":"Acme Europe"}"""));
        var grants = new StubGrantStore().Add(PushedAuthorizationGrant(
            PushedRequestUri, "app", new Dictionary<string, string[]> { ["organization"] = ["acme-eu"] }));

        var resolved = await ResolveAsync(PushedLoginRequest(), organizations, grantStore: grants);

        Assert.Equal("Org_7fA2", resolved.Organization!.Id);
        Assert.Equal("Acme Europe", Text(resolved.Branding, "appName"));
    }

    /// <summary>
    /// A pushed request naming no organisation still lets the restricted-client fallback apply. The
    /// client_id comes off the LOADED record's own <see cref="PushedAuthorizationRequest.ClientId"/>,
    /// not off the outer query: the endpoint that stores the record excludes client_id from the
    /// parameter bag entirely, so it could not be read any other way.
    /// </summary>
    [Fact]
    public async Task Resolve_WithAPushedRequestNamingNoOrganization_FallsBackToTheRestrictedClient()
    {
        var organizations = new StubOrganizationStore().Add(OrganizationWith("""{"appName":"Acme Europe"}"""));
        var clients = new StubClientStore().Add(new OAuthClient { ClientId = "app", RestrictedToOrganizationIds = ["Org_7fA2"] });
        var grants = new StubGrantStore().Add(PushedAuthorizationGrant(
            PushedRequestUri, "app", new Dictionary<string, string[]> { ["redirect_uri"] = ["https://rp.example/cb"] }));

        var resolved = await ResolveAsync(PushedLoginRequest(), organizations, grantStore: grants, clients: clients);

        Assert.Equal("Org_7fA2", resolved.Organization!.Id);
        Assert.Equal("Acme Europe", Text(resolved.Branding, "appName"));
    }

    [Fact]
    public async Task Resolve_WithAMissingPushedRequest_ReturnsTenantBranding()
    {
        var organizations = new StubOrganizationStore().Add(OrganizationWith("""{"appName":"Acme Europe"}"""));
        var grants = new StubGrantStore(); // nothing stored under PushedRequestUri

        var resolved = await ResolveAsync(PushedLoginRequest(), organizations, grantStore: grants);

        Assert.Null(resolved.Organization);
        Assert.Equal("Acme", Text(resolved.Branding, "appName"));
    }

    [Fact]
    public async Task Resolve_WithAnExpiredPushedRequest_ReturnsTenantBranding()
    {
        var organizations = new StubOrganizationStore().Add(OrganizationWith("""{"appName":"Acme Europe"}"""));
        var grants = new StubGrantStore().Add(PushedAuthorizationGrant(
            PushedRequestUri, "app", new Dictionary<string, string[]> { ["organization"] = ["acme-eu"] },
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1)));

        var resolved = await ResolveAsync(PushedLoginRequest(), organizations, grantStore: grants);

        Assert.Null(resolved.Organization);
        Assert.Equal("Acme", Text(resolved.Branding, "appName"));
    }

    /// <summary>Bound to a different client (the same binding check <c>LoadAsync</c> itself applies),
    /// never read as this request's, even though the requestUri matches.</summary>
    [Fact]
    public async Task Resolve_WithAPushedRequestBoundToADifferentClient_ReturnsTenantBranding()
    {
        var organizations = new StubOrganizationStore().Add(OrganizationWith("""{"appName":"Acme Europe"}"""));
        var grants = new StubGrantStore().Add(PushedAuthorizationGrant(
            PushedRequestUri, "someone-elses-client", new Dictionary<string, string[]> { ["organization"] = ["acme-eu"] }));

        var resolved = await ResolveAsync(PushedLoginRequest(), organizations, grantStore: grants);

        Assert.Null(resolved.Organization);
        Assert.Equal("Acme", Text(resolved.Branding, "appName"));
    }

    /// <summary>
    /// Never consumes or mutates the record. <see cref="StubGrantStore"/>'s every write member throws
    /// <see cref="NotSupportedException"/>, so if resolution called <c>StoreAsync</c> (the expiry
    /// extension the library's own <c>ProtocolPushedAuthorizationService.LoadAsync</c> performs on a
    /// first load) or <c>RemoveAsync</c>/<c>ConsumeAsync</c>, the call itself would throw. The resolver
    /// resolves any exception to none, so the organisation being found is what shows no write happened.
    /// </summary>
    [Fact]
    public async Task Resolve_WithAPushedRequest_NeverWritesToTheGrantStore()
    {
        var organizations = new StubOrganizationStore().Add(OrganizationWith("""{"appName":"Acme Europe"}"""));
        var grant = PushedAuthorizationGrant(
            PushedRequestUri, "app", new Dictionary<string, string[]> { ["organization"] = ["acme-eu"] });
        var dataBefore = grant.Data;
        var expiresBefore = grant.ExpiresAt;
        var grants = new StubGrantStore().Add(grant);

        var resolved = await ResolveAsync(PushedLoginRequest(), organizations, grantStore: grants);

        Assert.Equal("Org_7fA2", resolved.Organization!.Id);
        Assert.Equal(dataBefore, grant.Data);
        Assert.Equal(expiresBefore, grant.ExpiresAt);
    }

    // ── The login page's own rules ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Once a pushed record is found, its parameters replace the outer query's rather than adding to it,
    /// exactly as the authorize endpoint stops reading the query when <c>request_uri</c> is present.
    /// </summary>
    [Fact]
    public async Task Resolve_WithAPushedRequest_ItsParametersReplaceTheOuterQuery()
    {
        var organizations = new StubOrganizationStore()
            .Add(OrganizationWith("""{"appName":"Acme Europe"}"""))
            .Add(new Organization
            {
                Id = "Org_9zB4", Slug = "acme-us", DisplayName = "Acme US",
                BrandingJson = """{"appName":"Acme US"}""", CreatedAt = DateTimeOffset.UtcNow,
            });
        var grants = new StubGrantStore().Add(PushedAuthorizationGrant(
            PushedRequestUri, "app", new Dictionary<string, string[]> { ["organization"] = ["acme-eu"] }));

        var resolved = await ResolveAsync(
            LoginRequest($"/connect/authorize?client_id=app&organization=acme-us&request_uri={Uri.EscapeDataString(PushedRequestUri)}"),
            organizations, grantStore: grants);

        Assert.Equal("Org_7fA2", resolved.Organization!.Id);
    }

    /// <summary>Parameter names are read without regard to case, on the return URL and in a pushed record.</summary>
    [Fact]
    public async Task Resolve_ReadsParameterNamesWithoutRegardToCase()
    {
        var organizations = new StubOrganizationStore().Add(OrganizationWith("""{"appName":"Acme Europe"}"""));
        var grants = new StubGrantStore().Add(PushedAuthorizationGrant(
            PushedRequestUri, "app", new Dictionary<string, string[]> { ["ORG_SLUG"] = ["acme-eu"] }));

        var onReturnUrl = await ResolveAsync(
            LoginRequest("/connect/authorize?client_id=app&Organization=acme-eu"), organizations);
        var inPushedRequest = await ResolveAsync(PushedLoginRequest(), organizations, grantStore: grants);

        Assert.Equal("Org_7fA2", onReturnUrl.Organization!.Id);
        Assert.Equal("Org_7fA2", inPushedRequest.Organization!.Id);
    }

    /// <summary>
    /// The login page applies no test for being enabled: a disabled organisation's page shows its
    /// branding, and the endpoints that mint anything refuse it themselves.
    /// </summary>
    [Fact]
    public async Task Resolve_WithADisabledOrganization_StillUsesItsBranding()
    {
        var organization = OrganizationWith("""{"appName":"Acme Europe"}""");
        organization.Enabled = false;
        var store = new StubOrganizationStore().Add(organization);

        var resolved = await ResolveAsync(LoginRequest(null), store, pinnedOrganizationId: "Org_7fA2");

        Assert.Equal("Org_7fA2", resolved.Organization!.Id);
        Assert.Equal("Acme Europe", Text(resolved.Branding, "appName"));
    }

    /// <summary>A pin the host does not insist on ranks last, below everything the request names.</summary>
    [Fact]
    public async Task Resolve_WithANonAuthoritativePin_RanksBelowTheReturnUrlAndTheClientRestriction()
    {
        var organizations = new StubOrganizationStore()
            .Add(OrganizationWith("""{"appName":"Acme Europe"}"""))
            .Add(new Organization
            {
                Id = "Org_9zB4", Slug = "acme-us", DisplayName = "Acme US",
                BrandingJson = """{"appName":"Acme US"}""", CreatedAt = DateTimeOffset.UtcNow,
            });
        var clients = new StubClientStore().Add(new OAuthClient { ClientId = "app", RestrictedToOrganizationIds = ["Org_9zB4"] });
        var tenant = new TestTenantContext(AuthagonalTestFactory.TestIssuer) { OrganizationId = "Org_7fA2" };

        var named = await ResolveAsync(
            LoginRequest("/connect/authorize?client_id=app&organization=acme-us"), tenant, organizations);
        var restricted = await ResolveAsync(
            LoginRequest("/connect/authorize?client_id=app"), tenant, organizations, clients: clients);
        var neither = await ResolveAsync(LoginRequest(null), tenant, organizations);

        Assert.Equal("Org_9zB4", named.Organization!.Id);
        Assert.Equal("Org_9zB4", restricted.Organization!.Id);
        Assert.Equal("Org_7fA2", neither.Organization!.Id);
    }

    /// <summary>An unknown organisation on the accept-invite page names nothing.</summary>
    [Fact]
    public async Task Resolve_AcceptInvitePageNamingAnUnknownOrganization_ResolvesNone()
    {
        var store = new StubOrganizationStore().Add(OrganizationWith("""{"appName":"Acme Europe"}"""));

        var resolved = await ResolveAsync(DocumentRequest("/accept-invite", "nobody"), store);

        Assert.Null(resolved.Organization);
    }

    /// <summary>A store that throws is no organisation, never a failed page.</summary>
    [Fact]
    public async Task Resolve_WithAGrantStoreThatThrows_ResolvesNone()
    {
        var organizations = new StubOrganizationStore().Add(OrganizationWith("""{"appName":"Acme Europe"}"""));

        var resolved = await ResolveAsync(PushedLoginRequest(), organizations, grantStore: new ThrowingGrantStore());

        Assert.Null(resolved.Organization);
        Assert.Equal("Acme", Text(resolved.Branding, "appName"));
    }

    // ── The boot payload ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Backs <see cref="IOidcProviderStore"/>/<see cref="ISamlProviderStore"/> with a plain list: enough
    /// for <c>GetAllAsync</c>, which is all <c>AuthEndpoints.BuildProvidersResponseAsync</c> reads.
    /// </summary>
    private sealed class StubOidcProviderStore : IOidcProviderStore
    {
        private readonly List<OidcProviderConfig> _connections = [];
        public StubOidcProviderStore Add(OidcProviderConfig config) { _connections.Add(config); return this; }
        public Task<OidcProviderConfig?> GetAsync(string connectionId, CancellationToken ct = default) =>
            Task.FromResult(_connections.FirstOrDefault(c => c.ConnectionId == connectionId));
        public Task<IReadOnlyList<OidcProviderConfig>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OidcProviderConfig>>(_connections);
        public Task<IReadOnlyList<OidcProviderConfig>> ListByOrganizationAsync(string organizationId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OidcProviderConfig>>(
                _connections.Where(c => c.OrganizationId == organizationId).ToList());
        public Task UpsertAsync(OidcProviderConfig config, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string connectionId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StubSamlProviderStore : ISamlProviderStore
    {
        private readonly List<SamlProviderConfig> _connections = [];
        public StubSamlProviderStore Add(SamlProviderConfig config) { _connections.Add(config); return this; }
        public Task<SamlProviderConfig?> GetAsync(string connectionId, CancellationToken ct = default) =>
            Task.FromResult(_connections.FirstOrDefault(c => c.ConnectionId == connectionId));
        public Task<IReadOnlyList<SamlProviderConfig>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SamlProviderConfig>>(_connections);
        public Task<IReadOnlyList<SamlProviderConfig>> ListByOrganizationAsync(string organizationId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SamlProviderConfig>>(
                _connections.Where(c => c.OrganizationId == organizationId).ToList());
        public Task UpsertAsync(SamlProviderConfig config, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string connectionId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private const string Shell = "<!doctype html><html><head><title>login</title></head><body></body></html>";

    /// <summary>
    /// Builds the payload the way the standalone server's login document does: the pin is whatever
    /// <c>Organizations:PinnedHosts</c> holds for the request's host, the tenant's branding is
    /// <see cref="TenantBranding"/>, one resolution serves <see cref="LoginBootPayloadBuilder.BuildAsync"/>,
    /// and the result is inlined with <see cref="LoginBootPayloadBuilder.InjectIntoShell"/> and read back
    /// out of the document, so what is asserted is what the login document actually carries.
    /// </summary>
    private static async Task<JsonElement> BootPayloadAsync(
        string host, string pathAndQuery, IOrganizationStore organizations, string? pinnedOrganizationId = null,
        StubOidcProviderStore? oidc = null, StubSamlProviderStore? saml = null,
        IGrantStore? grants = null, IClientStore? clients = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        var split = pathAndQuery.IndexOf('?');
        context.Request.Path = split < 0 ? pathAndQuery : pathAndQuery[..split];
        if (split >= 0)
            context.Request.QueryString = new QueryString(pathAndQuery[split..]);

        var configuration = new Dictionary<string, string?> { ["Issuer"] = $"https://{host}" };
        if (pinnedOrganizationId is not null)
            configuration[$"{DefaultTenantContext.PinnedHostsSection}:{host}"] = pinnedOrganizationId;
        var tenant = new DefaultTenantContext(
            new ConfigurationBuilder().AddInMemoryCollection(configuration).Build(),
            new HttpContextAccessor { HttpContext = context });

        var builder = new LoginBootPayloadBuilder(
            new PreAuthOrganizationResolver(
                organizations, tenant, NullLogger<PreAuthOrganizationResolver>.Instance, grants, clients),
            oidc ?? new StubOidcProviderStore(),
            saml ?? new StubSamlProviderStore(),
            NullLogger<LoginBootPayloadBuilder>.Instance);

        var payload = await builder.BuildAsync(context.Request, TenantBranding(), turnstileSiteKey: null, CancellationToken.None);
        var html = LoginBootPayloadBuilder.InjectIntoShell(Shell, payload);
        Assert.NotNull(html);

        const string open = """<script type="application/json" id="authagonal-boot">""";
        var start = html.IndexOf(open, StringComparison.Ordinal) + open.Length;
        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        return JsonDocument.Parse(html[start..end]).RootElement.Clone();
    }

    private static string AuthorizeReturn(string query) =>
        "/login?returnUrl=" + Uri.EscapeDataString("/connect/authorize?" + query);

    private static Organization SeededOrganization(string? brandingJson, string id = "Org_7fA2", string slug = "acme-eu") => new()
    {
        Id = id,
        Slug = slug,
        DisplayName = $"{slug} display",
        BrandingJson = brandingJson,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task BootPayload_WithAnOrganizationOnTheAuthorizeRequest_CarriesTheMergedBranding()
    {
        var organizations = new StubOrganizationStore().Add(SeededOrganization("""{"appName":"Acme Europe"}"""));

        var boot = await BootPayloadAsync(
            "acme.authagonal.test", AuthorizeReturn("client_id=app&organization=acme-eu"), organizations);

        var branding = boot.GetProperty("branding");
        Assert.Equal("Acme Europe", branding.GetProperty("appName").GetString());
        // Not overridden, so still the tenant's.
        Assert.Equal("#111111", branding.GetProperty("primaryColor").GetString());
    }

    [Fact]
    public async Task BootPayload_WithoutAnOrganization_CarriesTheTenantBranding()
    {
        var organizations = new StubOrganizationStore().Add(SeededOrganization("""{"appName":"Acme Europe"}"""));

        var boot = await BootPayloadAsync(
            "acme.authagonal.test", AuthorizeReturn("client_id=app"), organizations);

        Assert.Equal("Acme", boot.GetProperty("branding").GetProperty("appName").GetString());
        Assert.Equal(JsonValueKind.Null, boot.GetProperty("organization").ValueKind);
    }

    /// <summary>
    /// The organisation block is additive: the SPA parses the boot payload with a bare JSON.parse and
    /// reads only the keys it knows, so an older login-app build ignores it.
    /// </summary>
    [Fact]
    public async Task BootPayload_WithAnOrganization_CarriesItsIdSlugAndName()
    {
        var organizations = new StubOrganizationStore().Add(SeededOrganization(brandingJson: null));

        var boot = await BootPayloadAsync(
            "acme.authagonal.test", AuthorizeReturn("client_id=app&organization=acme-eu"), organizations);

        var organization = boot.GetProperty("organization");
        Assert.Equal("Org_7fA2", organization.GetProperty("id").GetString());
        Assert.Equal("acme-eu", organization.GetProperty("slug").GetString());
        Assert.Equal("acme-eu display", organization.GetProperty("name").GetString());
        // No brandingJson, so nothing is overridden.
        Assert.Equal("Acme", boot.GetProperty("branding").GetProperty("appName").GetString());
    }

    /// <summary>
    /// A host pinned to an organisation (an <c>Organizations:PinnedHosts</c> entry) brands its login page
    /// without the relying party having to name the organisation at all, which is the whole point of
    /// pinning one.
    /// </summary>
    [Fact]
    public async Task BootPayload_OnADomainPinnedToAnOrganization_CarriesThatOrganizationsBranding()
    {
        var organizations = new StubOrganizationStore().Add(
            SeededOrganization("""{"appName":"Acme Europe","primaryColor":"#22cc88"}"""));

        var boot = await BootPayloadAsync("eu.acme.com", "/login", organizations, pinnedOrganizationId: "Org_7fA2");

        Assert.Equal("Acme Europe", boot.GetProperty("branding").GetProperty("appName").GetString());
        Assert.Equal("#22cc88", boot.GetProperty("branding").GetProperty("primaryColor").GetString());
        Assert.Equal("Org_7fA2", boot.GetProperty("organization").GetProperty("id").GetString());
    }

    /// <summary>
    /// The lookup runs through the tenant's own organisation store, so an organisation of tenant A cannot
    /// be reached from tenant B's host: for the library, an id the tenant's store does not hold. The same
    /// id and the same returnUrl are used against both stores, so the difference can only be the tenant.
    /// </summary>
    [Fact]
    public async Task BootPayload_WithAnOrganizationIdFromAnotherTenant_FallsBackToTenantBranding()
    {
        // Exists in ALPHA only.
        var alpha = new StubOrganizationStore().Add(
            SeededOrganization("""{"appName":"Alpha Division"}""", id: "Org_Shared", slug: "shared"));
        var beta = new StubOrganizationStore();

        var returnUrl = AuthorizeReturn("client_id=app&org_id=Org_Shared");

        var onAlpha = await BootPayloadAsync("alpha.authagonal.test", returnUrl, alpha);
        var onBeta = await BootPayloadAsync("beta.authagonal.test", returnUrl, beta);

        Assert.Equal("Alpha Division", onAlpha.GetProperty("branding").GetProperty("appName").GetString());
        Assert.Equal("Org_Shared", onAlpha.GetProperty("organization").GetProperty("id").GetString());

        Assert.Equal("Acme", onBeta.GetProperty("branding").GetProperty("appName").GetString());
        Assert.Equal(JsonValueKind.Null, onBeta.GetProperty("organization").ValueKind);
    }

    // ── Organisation-scoped connections / autoChallenge ───────────────────────────────────────────────

    /// <summary>
    /// The boot's `providers` block is the library's own <c>SsoProviderListResponse</c>, so it always has
    /// `providers`/`turnstileSiteKey`, and `autoChallenge` only when present (it is serialised with
    /// <c>JsonIgnoreCondition.WhenWritingNull</c>).
    /// </summary>
    private static bool HasAutoChallenge(JsonElement boot, out JsonElement autoChallenge) =>
        boot.GetProperty("providers").TryGetProperty("autoChallenge", out autoChallenge);

    /// <summary>(a) Pinned host: the organisation's one connection auto-challenges without the relying
    /// party naming anything at all, the same pin mechanism the branding tests cover, asserted for the
    /// providers/autoChallenge half of the same boot payload.</summary>
    [Fact]
    public async Task BootPayload_OnADomainPinnedToAnOrganization_AutoChallengesItsSoleConnection()
    {
        var organizations = new StubOrganizationStore().Add(SeededOrganization(brandingJson: null));
        var oidc = new StubOidcProviderStore().Add(new OidcProviderConfig
        {
            ConnectionId = "conn-eu-okta",
            ConnectionName = "Acme EU Okta",
            OrganizationId = "Org_7fA2",
        });

        var boot = await BootPayloadAsync(
            "eu.acme.com", "/login", organizations, pinnedOrganizationId: "Org_7fA2", oidc: oidc);

        Assert.True(HasAutoChallenge(boot, out var autoChallenge));
        Assert.Equal("conn-eu-okta", autoChallenge.GetProperty("connectionId").GetString());
        // The organisation's own connection is also the first (only) button offered.
        var providers = boot.GetProperty("providers").GetProperty("providers");
        Assert.Single(providers.EnumerateArray());
    }

    /// <summary>(b) `organization=` on the returnUrl: the relying party names the organisation on the
    /// authorize request that led here, and the login page auto-challenges its sole connection exactly
    /// as the pin does.</summary>
    [Fact]
    public async Task BootPayload_WithAnOrganizationOnTheAuthorizeRequest_AutoChallengesItsSoleConnection()
    {
        var organizations = new StubOrganizationStore().Add(SeededOrganization(brandingJson: null));
        var saml = new StubSamlProviderStore().Add(new SamlProviderConfig
        {
            ConnectionId = "conn-eu-adfs",
            ConnectionName = "Acme EU ADFS",
            EntityId = "urn:acme-eu:sp",
            MetadataLocation = "https://idp.example/metadata",
            OrganizationId = "Org_7fA2",
        });

        var boot = await BootPayloadAsync(
            "acme.authagonal.test", AuthorizeReturn("client_id=app&organization=acme-eu"), organizations, saml: saml);

        Assert.True(HasAutoChallenge(boot, out var autoChallenge));
        Assert.Equal("conn-eu-adfs", autoChallenge.GetProperty("connectionId").GetString());
        Assert.Equal("saml", autoChallenge.GetProperty("type").GetString());
    }

    /// <summary>(c) A client restricted to exactly one organisation selects it for a request that names
    /// none at all, proven at the boot-payload/providers level rather than only against the resolver
    /// directly.</summary>
    [Fact]
    public async Task BootPayload_WithAClientRestrictedToOneOrganization_AutoChallengesItsSoleConnection()
    {
        var organizations = new StubOrganizationStore().Add(SeededOrganization(brandingJson: null));
        var clients = new StubClientStore().Add(
            new OAuthClient { ClientId = "restricted-app", RestrictedToOrganizationIds = ["Org_7fA2"] });
        var oidc = new StubOidcProviderStore().Add(new OidcProviderConfig
        {
            ConnectionId = "conn-eu-google",
            ConnectionName = "Acme EU Google",
            OrganizationId = "Org_7fA2",
        });

        var boot = await BootPayloadAsync(
            "acme.authagonal.test", AuthorizeReturn("client_id=restricted-app"), organizations,
            oidc: oidc, clients: clients);

        Assert.Equal("Org_7fA2", boot.GetProperty("organization").GetProperty("id").GetString());
        Assert.True(HasAutoChallenge(boot, out var autoChallenge));
        Assert.Equal("conn-eu-google", autoChallenge.GetProperty("connectionId").GetString());
    }

    /// <summary>(d) A pushed authorization request (RFC 9126): the organisation rides behind the opaque
    /// `request_uri` rather than on the query string, and resolution must still reach it, the same
    /// pushed-record path <see cref="Resolve_WithAPushedRequestNamingAnOrganization_ResolvesIt"/> proves
    /// for branding, now proven for autoChallenge.</summary>
    [Fact]
    public async Task BootPayload_WithAPushedAuthorizationRequestNamingAnOrganization_AutoChallengesItsSoleConnection()
    {
        var organizations = new StubOrganizationStore().Add(SeededOrganization(brandingJson: null));
        var saml = new StubSamlProviderStore().Add(new SamlProviderConfig
        {
            ConnectionId = "conn-eu-adfs",
            ConnectionName = "Acme EU ADFS",
            EntityId = "urn:acme-eu:sp",
            MetadataLocation = "https://idp.example/metadata",
            OrganizationId = "Org_7fA2",
        });
        var grants = new StubGrantStore().Add(PushedAuthorizationGrant(
            PushedRequestUri, "app", new Dictionary<string, string[]> { ["organization"] = ["acme-eu"] }));

        var boot = await BootPayloadAsync(
            "acme.authagonal.test",
            AuthorizeReturn($"client_id=app&request_uri={Uri.EscapeDataString(PushedRequestUri)}"),
            organizations, saml: saml, grants: grants);

        Assert.Equal("Org_7fA2", boot.GetProperty("organization").GetProperty("id").GetString());
        Assert.True(HasAutoChallenge(boot, out var autoChallenge));
        Assert.Equal("conn-eu-adfs", autoChallenge.GetProperty("connectionId").GetString());
    }

    /// <summary>Negative case for all four: a plain login (no pin, no organization parameter, no
    /// restriction, no PAR) never auto-challenges an organisation's connection. It is offered only once
    /// that organisation has actually been selected.</summary>
    [Fact]
    public async Task BootPayload_WithNoOrganization_NeverAutoChallengesAnOrganizationsConnection()
    {
        var organizations = new StubOrganizationStore().Add(SeededOrganization(brandingJson: null));
        var oidc = new StubOidcProviderStore()
            .Add(new OidcProviderConfig
            {
                ConnectionId = "conn-eu-google",
                ConnectionName = "Acme EU Google",
                OrganizationId = "Org_7fA2",
            })
            // A tenant-level connection with the SAME shape: this one SHOULD show up on a plain login.
            .Add(new OidcProviderConfig { ConnectionId = "conn-tenant-google", ConnectionName = "Acme Google" });

        var boot = await BootPayloadAsync(
            "acme.authagonal.test", AuthorizeReturn("client_id=app"), organizations, oidc: oidc);

        Assert.False(HasAutoChallenge(boot, out _));
        var providers = boot.GetProperty("providers").GetProperty("providers").EnumerateArray().ToList();
        Assert.Single(providers);
        Assert.Equal("conn-tenant-google", providers[0].GetProperty("connectionId").GetString());
    }
}
