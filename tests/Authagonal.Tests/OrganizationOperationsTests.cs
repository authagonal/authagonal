using System.Text.RegularExpressions;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Tests;

/// <summary>
/// <see cref="OrganizationOperations"/> over the suite's writable stores: the outcome AND the exact code of
/// every refusal, the audit row of every write, and the absence of an audit row on every failure.
/// </summary>
public sealed partial class OrganizationOperationsTests
{
    private const string Actor = "admin@example.test";

    private readonly WritableOrganizationStore _orgs = new();
    private readonly WritableOrganizationMembershipStore _members = new();
    private readonly InMemoryUserStore _users = new();
    private readonly RecordingAuditLogger _audit = new();
    private readonly FakeTxtResolver _dns = new();
    private readonly OrganizationOperations _ops;

    public OrganizationOperationsTests()
    {
        _ops = new OrganizationOperations(_orgs, _members, _users, _audit, _dns, NullLogger<OrganizationOperations>.Instance);
    }

    [GeneratedRegex("^org_[0-9a-f]{32}$")]
    private static partial Regex OrgIdPattern();

    private static CancellationToken Ct => CancellationToken.None;

    private async Task<OrganizationDto> CreateOkAsync(string slug, string? name = null)
    {
        var result = await _ops.CreateAsync(slug, name ?? $"Name {slug}", null, null, null, null, null, Actor, Ct);
        Assert.True(result.Succeeded, $"create '{slug}' failed: {result.Error}");
        return result.Value!;
    }

    private async Task<AuthUser> SeedUserAsync(string email = "member@example.test", string first = "Mem", string last = "Ber")
    {
        var user = new AuthUser
        {
            Id = Guid.NewGuid().ToString("N"),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            FirstName = first,
            LastName = last,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await _users.CreateAsync(user);
        return user;
    }

    private static void AssertFailure<T>(OperationResult<T> result, OperationOutcome outcome, string code)
    {
        Assert.False(result.Succeeded);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(code, result.Error);
    }

    // -----------------------------------------------------------------------
    // Create
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Create_GeneratesOrgPrefixedId_AndDefaults()
    {
        var result = await _ops.CreateAsync("acme", "Acme Inc", null, null, null, null, null, Actor, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(OperationOutcome.Ok, result.Outcome);
        var dto = result.Value!;
        Assert.Matches(OrgIdPattern(), dto.Id);
        Assert.Equal("acme", dto.Slug);
        Assert.Equal("Acme Inc", dto.Name);
        Assert.True(dto.Enabled);
        Assert.True(dto.RequireMembershipForTokens);
        Assert.False(dto.AllowAutoMembership);
        Assert.Null(dto.BrandingJson);
        Assert.Null(dto.UpdatedAt);
        Assert.Empty(dto.Metadata);
        Assert.Empty(dto.Domains);

        var stored = await _orgs.GetAsync(dto.Id);
        Assert.NotNull(stored);
        Assert.Equal("acme", stored!.Slug);
    }

    [Fact]
    public async Task Create_HonoursExplicitFlagsBrandingAndMetadata()
    {
        var metadata = new Dictionary<string, string> { ["tier"] = "gold", ["region"] = "au" };

        var result = await _ops.CreateAsync(
            "flagged", "Flagged", "{\"logo\":\"x\"}", enabled: false, requireMembershipForTokens: false,
            allowAutoMembership: true, metadata, Actor, Ct);

        Assert.True(result.Succeeded);
        var dto = result.Value!;
        Assert.False(dto.Enabled);
        Assert.False(dto.RequireMembershipForTokens);
        Assert.True(dto.AllowAutoMembership);
        Assert.Equal("{\"logo\":\"x\"}", dto.BrandingJson);
        Assert.Equal("gold", dto.Metadata["tier"]);
        Assert.Equal("au", dto.Metadata["region"]);

        var stored = await _orgs.GetAsync(dto.Id);
        Assert.False(stored!.Enabled);
        Assert.False(stored.RequireMembershipForTokens);
        Assert.True(stored.AllowAutoMembership);
    }

    [Fact]
    public async Task Create_WritesTheAuditRow()
    {
        var dto = await CreateOkAsync("audited");

        var row = Assert.Single(_audit.Entries);
        Assert.Equal(Actor, row.Actor);
        Assert.Equal("organization.created", row.Action);
        Assert.Equal("organization", row.EntityType);
        Assert.Equal(dto.Id, row.EntityId);
        Assert.Equal("audited", row.Detail);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_BlankSlug_IsSlugRequired(string? slug)
    {
        var result = await _ops.CreateAsync(slug, "Name", null, null, null, null, null, Actor, Ct);

        AssertFailure(result, OperationOutcome.Invalid, "slug_required");
        Assert.Empty(_audit.Entries);
    }

    [Theory]
    [InlineData("Acme")]
    [InlineData("-acme")]
    [InlineData("acme-")]
    [InlineData("ac me")]
    [InlineData("acme_inc")]
    public async Task Create_MalformedSlug_IsSlugInvalid(string slug)
    {
        var result = await _ops.CreateAsync(slug, "Name", null, null, null, null, null, Actor, Ct);

        AssertFailure(result, OperationOutcome.Invalid, "slug_invalid");
        Assert.Empty(_audit.Entries);
        Assert.Empty(await _orgs.ListAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Create_BlankName_IsNameRequired(string? name)
    {
        var result = await _ops.CreateAsync("acme", name, null, null, null, null, null, Actor, Ct);

        AssertFailure(result, OperationOutcome.Invalid, "name_required");
        Assert.Empty(_audit.Entries);
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("not json")]
    [InlineData("\"a string\"")]
    public async Task Create_BrandingThatIsNotAJsonObject_IsInvalidBrandingJson(string branding)
    {
        var result = await _ops.CreateAsync("acme", "Acme", branding, null, null, null, null, Actor, Ct);

        AssertFailure(result, OperationOutcome.Invalid, "invalid_branding_json");
        Assert.Empty(_audit.Entries);
        Assert.Empty(await _orgs.ListAsync());
    }

    [Fact]
    public async Task Create_SlugAlreadyHeld_IsSlugTakenConflict()
    {
        await CreateOkAsync("taken");
        _audit.Entries.Clear();

        var result = await _ops.CreateAsync("taken", "Other", null, null, null, null, null, Actor, Ct);

        AssertFailure(result, OperationOutcome.Conflict, "slug_taken");
        Assert.Empty(_audit.Entries);
        Assert.Single(await _orgs.ListAsync());
    }

    [Fact]
    public async Task Create_SlugEqualToAnExistingOrganisationsId_IsSlugTakenConflict()
    {
        // A bare-Guid-"N" id is itself slug-shaped, so a slug can equal another organisation's id.
        var slugShapedId = Guid.NewGuid().ToString("N");
        Assert.True(OrganizationSlug.IsValid(slugShapedId));
        _orgs.With(slugShapedId, "legacy-org", "Legacy");

        var result = await _ops.CreateAsync(slugShapedId, "Squatter", null, null, null, null, null, Actor, Ct);

        AssertFailure(result, OperationOutcome.Conflict, "slug_taken");
        Assert.Empty(_audit.Entries);
        Assert.Single(await _orgs.ListAsync());
    }

    [Fact]
    public async Task Create_StoreThrowsInvalidOperation_IsSlugTakenConflict()
    {
        var racing = new ThrowingUpsertOrganizationStore(_orgs);
        var ops = new OrganizationOperations(racing, _members, _users, _audit, _dns, NullLogger<OrganizationOperations>.Instance);

        var result = await ops.CreateAsync("racer", "Racer", null, null, null, null, null, Actor, Ct);

        AssertFailure(result, OperationOutcome.Conflict, "slug_taken");
        Assert.Equal(1, racing.UpsertCalls);
        Assert.Empty(_audit.Entries);
    }

    // -----------------------------------------------------------------------
    // Get
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Get_ByIdAndBySlug_ReturnTheSameOrganisation()
    {
        var created = await CreateOkAsync("lookup", "Lookup Ltd");

        var byId = await _ops.GetAsync(created.Id, Ct);
        var bySlug = await _ops.GetAsync("lookup", Ct);

        Assert.True(byId.Succeeded);
        Assert.True(bySlug.Succeeded);
        Assert.Equal(created.Id, byId.Value!.Id);
        Assert.Equal(created.Id, bySlug.Value!.Id);
        Assert.Equal("Lookup Ltd", bySlug.Value.Name);
    }

    [Fact]
    public async Task Get_Unknown_IsOrganizationNotFound()
    {
        var result = await _ops.GetAsync("nope", Ct);

        AssertFailure(result, OperationOutcome.NotFound, "organization_not_found");
    }

    // -----------------------------------------------------------------------
    // Update
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Update_ChangesFields_StampsUpdatedAt_AndAudits()
    {
        var created = await CreateOkAsync("upd", "Before");
        _audit.Entries.Clear();

        var result = await _ops.UpdateAsync(
            created.Id, "After", "{\"c\":1}", slug: null, enabled: false, requireMembershipForTokens: false,
            allowAutoMembership: true, metadata: new Dictionary<string, string> { ["k"] = "v" }, Actor, Ct);

        Assert.True(result.Succeeded);
        var dto = result.Value!;
        Assert.Equal("After", dto.Name);
        Assert.Equal("{\"c\":1}", dto.BrandingJson);
        Assert.False(dto.Enabled);
        Assert.False(dto.RequireMembershipForTokens);
        Assert.True(dto.AllowAutoMembership);
        Assert.Equal("v", dto.Metadata["k"]);
        Assert.NotNull(dto.UpdatedAt);
        Assert.Equal("upd", dto.Slug);

        var stored = await _orgs.GetAsync(created.Id);
        Assert.Equal("After", stored!.DisplayName);
        Assert.NotNull(stored.UpdatedAt);

        var row = Assert.Single(_audit.Entries);
        Assert.Equal(Actor, row.Actor);
        Assert.Equal("organization.updated", row.Action);
        Assert.Equal("organization", row.EntityType);
        Assert.Equal(created.Id, row.EntityId);
        Assert.Equal("upd", row.Detail);
    }

    [Fact]
    public async Task Update_NullFields_LeaveTheOrganisationAsItWas()
    {
        var created = (await _ops.CreateAsync(
            "keep", "Keep", "{\"a\":1}", true, true, true, new Dictionary<string, string> { ["x"] = "y" }, Actor, Ct)).Value!;

        var result = await _ops.UpdateAsync(
            created.Id, null, null, null, null, null, null, null, Actor, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal("Keep", result.Value!.Name);
        Assert.Equal("{\"a\":1}", result.Value.BrandingJson);
        Assert.True(result.Value.AllowAutoMembership);
        Assert.Equal("y", result.Value.Metadata["x"]);
    }

    [Fact]
    public async Task Update_DifferentSlug_IsSlugImmutable()
    {
        var created = await CreateOkAsync("fixed");
        _audit.Entries.Clear();

        var result = await _ops.UpdateAsync(
            created.Id, "New", null, slug: "renamed", null, null, null, null, Actor, Ct);

        AssertFailure(result, OperationOutcome.Invalid, "slug_immutable");
        Assert.Empty(_audit.Entries);
        Assert.Equal("Name fixed", (await _orgs.GetAsync(created.Id))!.DisplayName);
    }

    [Fact]
    public async Task Update_SameSlug_IsAccepted()
    {
        var created = await CreateOkAsync("same");

        var result = await _ops.UpdateAsync(
            created.Id, "Renamed Display", null, slug: "same", null, null, null, null, Actor, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal("Renamed Display", result.Value!.Name);
        Assert.Equal("same", result.Value.Slug);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("nope")]
    public async Task Update_BrandingThatIsNotAJsonObject_IsInvalidBrandingJson(string branding)
    {
        var created = await CreateOkAsync("brand");
        _audit.Entries.Clear();

        var result = await _ops.UpdateAsync(
            created.Id, null, branding, null, null, null, null, null, Actor, Ct);

        AssertFailure(result, OperationOutcome.Invalid, "invalid_branding_json");
        Assert.Empty(_audit.Entries);
        Assert.Null((await _orgs.GetAsync(created.Id))!.BrandingJson);
    }

    [Fact]
    public async Task Update_Unknown_IsOrganizationNotFound()
    {
        var result = await _ops.UpdateAsync(
            "org_missing", "X", null, null, null, null, null, null, Actor, Ct);

        AssertFailure(result, OperationOutcome.NotFound, "organization_not_found");
        Assert.Empty(_audit.Entries);
    }

    // -----------------------------------------------------------------------
    // Delete
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Delete_RemovesMembershipsAndOrganisation_AndAudits()
    {
        var created = await CreateOkAsync("gone");
        var other = await CreateOkAsync("stays");
        _members.With(created.Id, "u1").With(created.Id, "u2").With(other.Id, "u1");
        _audit.Entries.Clear();

        var result = await _ops.DeleteAsync(created.Id, Actor, Ct);

        Assert.True(result.Succeeded);
        Assert.True(result.Value);
        Assert.Null(await _orgs.GetAsync(created.Id));
        Assert.Empty(await _members.ListByOrganizationAsync(created.Id));
        Assert.Single(await _members.ListByOrganizationAsync(other.Id));

        var row = Assert.Single(_audit.Entries);
        Assert.Equal(Actor, row.Actor);
        Assert.Equal("organization.deleted", row.Action);
        Assert.Equal("organization", row.EntityType);
        Assert.Equal(created.Id, row.EntityId);
        Assert.Equal("gone", row.Detail);
    }

    [Fact]
    public async Task Delete_Unknown_IsOrganizationNotFound()
    {
        var result = await _ops.DeleteAsync("org_missing", Actor, Ct);

        AssertFailure(result, OperationOutcome.NotFound, "organization_not_found");
        Assert.Empty(_audit.Entries);
    }

    // -----------------------------------------------------------------------
    // ListPage
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ListPage_FollowsNextCursorToTheEnd_SeeingEveryOrganisationOnce()
    {
        var expected = new List<string>();
        for (var i = 0; i < 5; i++)
            expected.Add((await CreateOkAsync($"page-{i}")).Id);

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var result = await _ops.ListPageAsync(cursor, 2, Ct);
            Assert.True(result.Succeeded);
            Assert.True(result.Value!.Items.Count <= 2);
            seen.AddRange(result.Value.Items.Select(o => o.Id));
            cursor = result.Value.NextCursor;
            pages++;
            Assert.True(pages <= 10, "paging did not terminate");
        } while (cursor is not null);

        Assert.Equal(3, pages);
        Assert.Equal(5, seen.Count);
        Assert.Equal(5, seen.Distinct().Count());
        Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal), seen);
    }

    [Fact]
    public async Task ListPage_MalformedCursor_IsInvalidCursorWithAMessage()
    {
        await CreateOkAsync("any");

        var result = await _ops.ListPageAsync("zzz", 2, Ct);

        AssertFailure(result, OperationOutcome.Invalid, "invalid_cursor");
        Assert.NotNull(result.Message);
        Assert.NotEmpty(result.Message!);
    }

    // -----------------------------------------------------------------------
    // AddMember
    // -----------------------------------------------------------------------

    [Fact]
    public async Task AddMember_ByUserId_DefaultsToActive_WithJoinedAt_AndAudits()
    {
        var org = await CreateOkAsync("m1");
        var user = await SeedUserAsync("alice@example.test", "Alice", "Smith");
        _audit.Entries.Clear();

        var result = await _ops.AddMemberAsync(org.Id, user.Id, null, null, ["billing"], Actor, Ct);

        Assert.True(result.Succeeded);
        var dto = result.Value!;
        Assert.Equal(org.Id, dto.OrganizationId);
        Assert.Equal(user.Id, dto.UserId);
        Assert.Equal("alice@example.test", dto.Email);
        Assert.Equal("Alice Smith", dto.Name);
        Assert.Equal("active", dto.Status);
        Assert.Equal(["billing"], dto.Roles);

        var stored = await _members.GetAsync(org.Id, user.Id);
        Assert.NotNull(stored);
        Assert.Equal("active", stored!.Status);
        Assert.NotNull(stored.JoinedAt);

        var row = Assert.Single(_audit.Entries);
        Assert.Equal(Actor, row.Actor);
        Assert.Equal("organization.member.added", row.Action);
        Assert.Equal("organization", row.EntityType);
        Assert.Equal(org.Id, row.EntityId);
        Assert.Equal($"user={user.Id}", row.Detail);
    }

    [Fact]
    public async Task AddMember_ByEmail_ResolvesTheUser()
    {
        var org = await CreateOkAsync("m2");
        var user = await SeedUserAsync("bob@example.test", "Bob", "Jones");

        var result = await _ops.AddMemberAsync(org.Id, null, "bob@example.test", null, null, Actor, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(user.Id, result.Value!.UserId);
        Assert.Equal("bob@example.test", result.Value.Email);
        Assert.Equal("Bob Jones", result.Value.Name);
        Assert.Empty(result.Value.Roles);
    }

    [Fact]
    public async Task AddMember_Invited_LeavesJoinedAtNull()
    {
        var org = await CreateOkAsync("m3");
        var user = await SeedUserAsync();

        var result = await _ops.AddMemberAsync(org.Id, user.Id, null, "invited", null, Actor, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal("invited", result.Value!.Status);
        Assert.Null((await _members.GetAsync(org.Id, user.Id))!.JoinedAt);
    }

    [Fact]
    public async Task AddMember_UnknownUserIdOrEmail_IsUserNotFound()
    {
        var org = await CreateOkAsync("m4");
        _audit.Entries.Clear();

        var byId = await _ops.AddMemberAsync(org.Id, "no-such-user", null, null, null, Actor, Ct);
        var byEmail = await _ops.AddMemberAsync(org.Id, null, "ghost@example.test", null, null, Actor, Ct);
        var neither = await _ops.AddMemberAsync(org.Id, null, null, null, null, Actor, Ct);

        AssertFailure(byId, OperationOutcome.NotFound, "user_not_found");
        AssertFailure(byEmail, OperationOutcome.NotFound, "user_not_found");
        AssertFailure(neither, OperationOutcome.NotFound, "user_not_found");
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task AddMember_UnknownOrganisation_IsOrganizationNotFound()
    {
        var user = await SeedUserAsync();

        var result = await _ops.AddMemberAsync("org_missing", user.Id, null, null, null, Actor, Ct);

        AssertFailure(result, OperationOutcome.NotFound, "organization_not_found");
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task AddMember_AlreadyAMember_IsMembershipExistsConflict()
    {
        var org = await CreateOkAsync("m5");
        var user = await SeedUserAsync();
        Assert.True((await _ops.AddMemberAsync(org.Id, user.Id, null, null, null, Actor, Ct)).Succeeded);
        _audit.Entries.Clear();

        var again = await _ops.AddMemberAsync(org.Id, user.Id, null, null, null, Actor, Ct);

        AssertFailure(again, OperationOutcome.Conflict, "membership_exists");
        Assert.Empty(_audit.Entries);
    }

    [Theory]
    [InlineData("tenant:admin")]
    [InlineData("Tenant:admin")]
    [InlineData("platform:x")]
    public async Task AddMember_ReservedRole_IsInvalidRole(string role)
    {
        var org = await CreateOkAsync("m6");
        var user = await SeedUserAsync();
        _audit.Entries.Clear();

        var result = await _ops.AddMemberAsync(org.Id, user.Id, null, null, ["billing", role], Actor, Ct);

        AssertFailure(result, OperationOutcome.Invalid, "invalid_role");
        Assert.Empty(_audit.Entries);
        Assert.Null(await _members.GetAsync(org.Id, user.Id));
    }

    [Fact]
    public async Task AddMember_UnknownStatus_IsInvalidStatus()
    {
        var org = await CreateOkAsync("m7");
        var user = await SeedUserAsync();
        _audit.Entries.Clear();

        var result = await _ops.AddMemberAsync(org.Id, user.Id, null, "bogus", null, Actor, Ct);

        AssertFailure(result, OperationOutcome.Invalid, "invalid_status");
        Assert.Empty(_audit.Entries);
        Assert.Null(await _members.GetAsync(org.Id, user.Id));
    }

    // -----------------------------------------------------------------------
    // UpdateMember
    // -----------------------------------------------------------------------

    [Fact]
    public async Task UpdateMember_Unknown_IsMembershipNotFound()
    {
        var org = await CreateOkAsync("u1");

        var result = await _ops.UpdateMemberAsync(org.Id, "nobody", "active", null, Actor, Ct);

        AssertFailure(result, OperationOutcome.NotFound, "membership_not_found");
        Assert.DoesNotContain(_audit.Entries, e => e.Action == "organization.member.updated");
    }

    [Fact]
    public async Task UpdateMember_BogusStatus_IsInvalidStatus()
    {
        var org = await CreateOkAsync("u2");
        var user = await SeedUserAsync();
        _members.With(org.Id, user.Id);

        var result = await _ops.UpdateMemberAsync(org.Id, user.Id, "bogus", null, Actor, Ct);

        AssertFailure(result, OperationOutcome.Invalid, "invalid_status");
        Assert.DoesNotContain(_audit.Entries, e => e.Action == "organization.member.updated");
    }

    [Theory]
    [InlineData("tenant:admin")]
    [InlineData("Tenant:admin")]
    [InlineData("platform:x")]
    public async Task UpdateMember_ReservedRole_IsInvalidRole(string role)
    {
        var org = await CreateOkAsync("u3");
        var user = await SeedUserAsync();
        _members.With(org.Id, user.Id, MembershipStatus.Active, "billing");

        var result = await _ops.UpdateMemberAsync(org.Id, user.Id, null, [role], Actor, Ct);

        AssertFailure(result, OperationOutcome.Invalid, "invalid_role");
        Assert.DoesNotContain(_audit.Entries, e => e.Action == "organization.member.updated");
        Assert.Equal(["billing"], (await _members.GetAsync(org.Id, user.Id))!.Roles);
    }

    [Fact]
    public async Task UpdateMember_InvitedToActive_StampsJoinedAt_AndAudits()
    {
        var org = await CreateOkAsync("u4");
        var user = await SeedUserAsync("carol@example.test", "Carol", "Lee");
        _members.With(org.Id, user.Id, MembershipStatus.Invited);
        Assert.Null((await _members.GetAsync(org.Id, user.Id))!.JoinedAt);
        _audit.Entries.Clear();

        var result = await _ops.UpdateMemberAsync(org.Id, user.Id, "active", null, Actor, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal("active", result.Value!.Status);
        Assert.Equal("carol@example.test", result.Value.Email);
        Assert.NotNull(result.Value.UpdatedAt);
        var stored = await _members.GetAsync(org.Id, user.Id);
        Assert.NotNull(stored!.JoinedAt);
        Assert.NotNull(stored.UpdatedAt);

        var row = Assert.Single(_audit.Entries);
        Assert.Equal(Actor, row.Actor);
        Assert.Equal("organization.member.updated", row.Action);
        Assert.Equal("organization", row.EntityType);
        Assert.Equal(org.Id, row.EntityId);
        Assert.Equal($"user={user.Id}", row.Detail);
    }

    [Fact]
    public async Task UpdateMember_ReplacesRoles()
    {
        var org = await CreateOkAsync("u5");
        var user = await SeedUserAsync();
        _members.With(org.Id, user.Id, MembershipStatus.Active, "billing", "support");

        var result = await _ops.UpdateMemberAsync(org.Id, user.Id, null, ["owner"], Actor, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(["owner"], result.Value!.Roles);
        Assert.Equal(["owner"], (await _members.GetAsync(org.Id, user.Id))!.Roles);
        Assert.Equal("active", (await _members.GetAsync(org.Id, user.Id))!.Status);
    }

    // -----------------------------------------------------------------------
    // RemoveMember
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RemoveMember_Unknown_IsMembershipNotFound()
    {
        var org = await CreateOkAsync("r1");
        _audit.Entries.Clear();

        var result = await _ops.RemoveMemberAsync(org.Id, "nobody", Actor, Ct);

        AssertFailure(result, OperationOutcome.NotFound, "membership_not_found");
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task RemoveMember_DeletesTheMembership_AndAudits()
    {
        var org = await CreateOkAsync("r2");
        var user = await SeedUserAsync();
        _members.With(org.Id, user.Id);
        _audit.Entries.Clear();

        var result = await _ops.RemoveMemberAsync(org.Id, user.Id, Actor, Ct);

        Assert.True(result.Succeeded);
        Assert.True(result.Value);
        Assert.Null(await _members.GetAsync(org.Id, user.Id));

        var row = Assert.Single(_audit.Entries);
        Assert.Equal(Actor, row.Actor);
        Assert.Equal("organization.member.removed", row.Action);
        Assert.Equal("organization", row.EntityType);
        Assert.Equal(org.Id, row.EntityId);
        Assert.Equal($"user={user.Id}", row.Detail);
    }

    // -----------------------------------------------------------------------
    // ListMembersPage
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ListMembersPage_UnknownOrganisation_IsOrganizationNotFound()
    {
        var result = await _ops.ListMembersPageAsync("org_missing", null, 2, Ct);

        AssertFailure(result, OperationOutcome.NotFound, "organization_not_found");
    }

    [Fact]
    public async Task ListMembersPage_FollowsNextCursorToTheEnd_SeeingEveryMemberOnce()
    {
        var org = await CreateOkAsync("lm");
        var ids = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var user = await SeedUserAsync($"member{i}@example.test", $"First{i}", "Last");
            _members.With(org.Id, user.Id);
            ids.Add(user.Id);
        }

        var seen = new List<MembershipDto>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var result = await _ops.ListMembersPageAsync(org.Id, cursor, 2, Ct);
            Assert.True(result.Succeeded);
            Assert.True(result.Value!.Items.Count <= 2);
            seen.AddRange(result.Value.Items);
            cursor = result.Value.NextCursor;
            pages++;
            Assert.True(pages <= 10, "paging did not terminate");
        } while (cursor is not null);

        Assert.Equal(3, pages);
        Assert.Equal(5, seen.Count);
        Assert.Equal(ids.OrderBy(x => x, StringComparer.Ordinal), seen.Select(m => m.UserId));
        Assert.All(seen, m => Assert.False(string.IsNullOrEmpty(m.Email)));
    }

    [Fact]
    public async Task ListMembersPage_MalformedCursor_IsInvalidCursor()
    {
        var org = await CreateOkAsync("lmc");

        var result = await _ops.ListMembersPageAsync(org.Id, "zzz", 2, Ct);

        AssertFailure(result, OperationOutcome.Invalid, "invalid_cursor");
        Assert.NotNull(result.Message);
    }

    [Fact]
    public async Task ListMembersPage_MemberWithoutAUserRecord_HasNullEmailAndName()
    {
        var org = await CreateOkAsync("lmn");
        _members.With(org.Id, "deleted-user");

        var result = await _ops.ListMembersPageAsync(org.Id, null, null, Ct);

        var item = Assert.Single(result.Value!.Items);
        Assert.Equal("deleted-user", item.UserId);
        Assert.Null(item.Email);
        Assert.Null(item.Name);
    }

    // -----------------------------------------------------------------------
    // ListUserOrganizations
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ListUserOrganizations_ReturnsOrgSummaryWithStatusAndRoles()
    {
        var org = await CreateOkAsync("uo", "UO Corp");
        _members.With(org.Id, "user-1", MembershipStatus.Invited, "billing", "support");

        var items = await _ops.ListUserOrganizationsAsync("user-1", Ct);

        var item = Assert.Single(items);
        Assert.Equal(org.Id, item.OrganizationId);
        Assert.Equal("uo", item.Slug);
        Assert.Equal("UO Corp", item.Name);
        Assert.Equal("invited", item.Status);
        Assert.Equal(["billing", "support"], item.Roles);
    }

    [Fact]
    public async Task ListUserOrganizations_SkipsAMembershipWhoseOrganisationIsGone()
    {
        var kept = await CreateOkAsync("kept");
        var removed = await CreateOkAsync("removed");
        _members.With(kept.Id, "user-2").With(removed.Id, "user-2");
        await _orgs.DeleteAsync(removed.Id); // straight to the store: the membership is left orphaned

        var items = await _ops.ListUserOrganizationsAsync("user-2", Ct);

        var item = Assert.Single(items);
        Assert.Equal(kept.Id, item.OrganizationId);
    }

    [Fact]
    public async Task ListUserOrganizations_NoMemberships_IsEmpty()
    {
        Assert.Empty(await _ops.ListUserOrganizationsAsync("loner", Ct));
    }

    /// <summary>Delegates to a real store but refuses every upsert the way a durable store refuses a
    /// slug that a concurrent create got to first.</summary>
    private sealed class ThrowingUpsertOrganizationStore(IOrganizationStore inner) : IOrganizationStore
    {
        public int UpsertCalls { get; private set; }

        public Task<Organization?> GetAsync(string organizationId, CancellationToken ct = default) =>
            inner.GetAsync(organizationId, ct);

        public Task<Organization?> GetBySlugAsync(string slug, CancellationToken ct = default) =>
            inner.GetBySlugAsync(slug, ct);

        public Task<IReadOnlyList<Organization>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);

        public Task UpsertAsync(Organization organization, CancellationToken ct = default)
        {
            UpsertCalls++;
            throw new InvalidOperationException("slug already held by a concurrent create");
        }

        public Task DeleteAsync(string organizationId, CancellationToken ct = default) =>
            inner.DeleteAsync(organizationId, ct);
    }
}
