using System.Security.Cryptography;
using System.Text;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Tests;

/// <summary>
/// <see cref="UserInviteOperations"/> over the suite's in-memory stores: the outcome and exact code of every
/// refusal, what an invite writes (the unconfirmed account, the invited membership, the grant), the audit
/// row of every write, and the absence of one on every failure.
/// </summary>
public sealed class UserInviteOperationsTests
{
    private const string Actor = "admin@example.test";
    private const string Issuer = "https://auth.invite.test";

    private readonly InMemoryUserStore _users = new();
    private readonly WritableOrganizationStore _orgs = new();
    private readonly WritableOrganizationMembershipStore _members = new();
    private readonly InMemoryGrantStore _grants = new();
    private readonly RecordingAuditLogger _audit = new();
    private readonly RecordingInviteEmail _email = new();
    private readonly RecordingCreatedHook _hook = new();
    private readonly UserInviteOperations _ops;

    public UserInviteOperationsTests() => _ops = Build(new DefaultInviteLinkBuilder(new TestTenantContext(Issuer)));

    private static CancellationToken Ct => CancellationToken.None;

    private UserInviteOperations Build(IInviteLinkBuilder links, IDictionary<string, string?>? config = null) =>
        new(_users, _orgs, _members, _grants, new PasswordHasher(), _email, links, [_hook], _audit,
            new ConfigurationBuilder().AddInMemoryCollection(config ?? new Dictionary<string, string?>()).Build(),
            NullLogger<UserInviteOperations>.Instance);

    private async Task<Organization> SeedOrgAsync(string slug = "acme", string name = "Acme Inc", string? branding = null)
    {
        var org = new Organization { Id = $"org_{slug}", Slug = slug, DisplayName = name, BrandingJson = branding };
        await _orgs.UpsertAsync(org);
        return org;
    }

    private static void AssertFailure<T>(OperationResult<T> result, OperationOutcome outcome, string code)
    {
        Assert.False(result.Succeeded);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(code, result.Error);
    }

    private static string TokenOf(string url) =>
        Uri.UnescapeDataString(System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["p"]!);

    private static string HashOf(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    // -----------------------------------------------------------------------
    // Invite: refusals
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Invite_WithoutAnEmail_IsRefused_email_required(string? email)
    {
        var result = await _ops.InviteAsync(email, null, null, null, null, Actor, Ct);

        AssertFailure(result, OperationOutcome.Invalid, "email_required");
        Assert.Empty(_audit.Entries);
        Assert.Empty(_email.Invites);
    }

    [Fact]
    public async Task Invite_WithAnAddressThatCannotBeAStorageKey_IsRefused_invalid_email()
    {
        var result = await _ops.InviteAsync("not an address/with#bad?chars", null, null, null, null, Actor, Ct);

        AssertFailure(result, OperationOutcome.Invalid, "invalid_email");
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task Invite_IntoAnOrganisationThatDoesNotExist_IsRefused_organization_not_found_AndCreatesNoUser()
    {
        var result = await _ops.InviteAsync("a@invite.test", null, null, null, null, Actor, Ct, "org_missing");

        AssertFailure(result, OperationOutcome.NotFound, "organization_not_found");
        Assert.Null(await _users.FindByEmailAsync("a@invite.test"));
        Assert.Empty(_audit.Entries);
        Assert.Empty(_email.Invites);
    }

    [Theory]
    [InlineData("platform:admin")]
    [InlineData("tenant:owner")]
    public async Task Invite_WithAReservedRolePrefix_IsRefused_invalid_role_AndCreatesNoUser(string role)
    {
        var org = await SeedOrgAsync();

        var result = await _ops.InviteAsync("a@invite.test", null, null, null, null, Actor, Ct, org.Id, ["billing", role]);

        AssertFailure(result, OperationOutcome.Invalid, "invalid_role");
        Assert.Contains("reserved prefix", result.Message);
        Assert.Null(await _users.FindByEmailAsync("a@invite.test"));
        Assert.Empty(await _members.ListByOrganizationAsync(org.Id));
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task Invite_OfAnAddressThatAlreadyHasAnAccount_IsAConflict_user_already_exists()
    {
        await _users.CreateAsync(new AuthUser
        {
            Id = "u1", Email = "taken@invite.test", NormalizedEmail = "TAKEN@INVITE.TEST",
        });

        var result = await _ops.InviteAsync("taken@invite.test", null, null, null, null, Actor, Ct);

        AssertFailure(result, OperationOutcome.Conflict, "user_already_exists");
        Assert.Empty(_audit.Entries);
        Assert.Empty(_email.Invites);
    }

    // -----------------------------------------------------------------------
    // Invite: what it writes
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Invite_CreatesAnUnconfirmedActiveAccount_WithAnUnguessablePassword()
    {
        var result = await _ops.InviteAsync("New.Person@Invite.Test", "New", "Person", " fr ", null, Actor, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal("new.person@invite.test", result.Value!.Email);
        var user = (await _users.GetAsync(result.Value.UserId))!;
        Assert.Equal("new.person@invite.test", user.Email);
        Assert.Equal("NEW.PERSON@INVITE.TEST", user.NormalizedEmail);
        Assert.Equal("New", user.FirstName);
        Assert.Equal("Person", user.LastName);
        Assert.Equal("fr", user.Locale);
        Assert.False(user.EmailConfirmed);
        Assert.True(user.IsActive);
        Assert.False(string.IsNullOrEmpty(user.PasswordHash));

        // Random, never sent anywhere: a second invite gets a different one.
        var second = await _ops.InviteAsync("other@invite.test", null, null, null, null, Actor, Ct);
        Assert.NotEqual(user.PasswordHash, (await _users.GetAsync(second.Value!.UserId))!.PasswordHash);
    }

    [Fact]
    public async Task Invite_WithoutAnOrganisation_WritesNoMembership_AndLinksWithoutAnOrganisation()
    {
        var result = await _ops.InviteAsync("plain@invite.test", null, null, null, "de", Actor, Ct);

        Assert.True(result.Succeeded);
        Assert.Empty(await _members.ListByUserAsync(result.Value!.UserId));
        var invite = Assert.Single(_email.Invites);
        Assert.Equal("plain@invite.test", invite.To);
        Assert.Equal("Authagonal", invite.Tenant);
        Assert.Null(invite.Organization);
        Assert.Equal("de", invite.Locale);
        Assert.StartsWith($"{Issuer}/accept-invite?p=", invite.Url);
        Assert.DoesNotContain("organization=", invite.Url);
        Assert.Equal(["user.invited"], _audit.Entries.Select(e => e.Action));
    }

    [Fact]
    public async Task Invite_IntoAnOrganisation_WritesAnInvitedMembership_AndAuditsBothInOrder()
    {
        var org = await SeedOrgAsync();
        var before = DateTimeOffset.UtcNow;

        var result = await _ops.InviteAsync("m@invite.test", null, null, null, null, Actor, Ct, org.Id, ["billing", "reader"]);

        Assert.True(result.Succeeded);
        var membership = Assert.Single(await _members.ListByOrganizationAsync(org.Id));
        Assert.Equal(result.Value!.UserId, membership.UserId);
        Assert.Equal(MembershipStatus.Invited, membership.Status);
        Assert.Equal(["billing", "reader"], membership.Roles);
        Assert.Equal(Actor, membership.InvitedByUserId);
        Assert.InRange(membership.InvitedAt!.Value, before, DateTimeOffset.UtcNow);
        Assert.Null(membership.JoinedAt);

        Assert.Collection(_audit.Entries,
            e =>
            {
                Assert.Equal((Actor, "organization.member.invited", "organization", org.Id), (e.Actor, e.Action, e.EntityType, e.EntityId));
                Assert.Equal($"user={result.Value.UserId}", e.Detail);
            },
            e =>
            {
                Assert.Equal((Actor, "user.invited", "user", result.Value.UserId), (e.Actor, e.Action, e.EntityType, e.EntityId));
                Assert.Equal("***@invite.test", e.Detail);
            });

        var invite = Assert.Single(_email.Invites);
        Assert.Equal(org.Id, invite.Organization!.Id);
        Assert.EndsWith("&organization=acme", invite.Url);
    }

    [Fact]
    public async Task Invite_ByAnUnknownActor_RecordsNoInviter()
    {
        var org = await SeedOrgAsync();

        await _ops.InviteAsync("m@invite.test", null, null, null, null, "unknown", Ct, org.Id);

        Assert.Null(Assert.Single(await _members.ListByOrganizationAsync(org.Id)).InvitedByUserId);
    }

    [Fact]
    public async Task Invite_MintsAPasswordResetGrant_TheLinkRedeems()
    {
        var before = DateTimeOffset.UtcNow;
        var result = await _ops.InviteAsync("g@invite.test", null, null, null, null, Actor, Ct);

        var token = TokenOf(Assert.Single(_email.Invites).Url);
        // 32 random bytes, base64url without padding.
        Assert.Equal(43, token.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", token);

        var grant = (await _grants.GetAsync(HashOf(token)))!;
        Assert.Equal("password_reset", grant.Type);
        Assert.Equal(result.Value!.UserId, grant.SubjectId);
        Assert.Equal(result.Value.UserId, grant.Data);
        Assert.Equal("auth", grant.ClientId);
        Assert.Null(grant.ConsumedAt);
        Assert.InRange(grant.CreatedAt, before, DateTimeOffset.UtcNow);
        Assert.InRange(grant.ExpiresAt - grant.CreatedAt, TimeSpan.FromDays(7) - TimeSpan.FromSeconds(1), TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Invite_BuildsEveryLinkThroughTheLinkBuilder()
    {
        var org = await SeedOrgAsync();
        var builder = new RecordingLinkBuilder();
        var ops = Build(builder);

        await ops.InviteAsync("s@invite.test", null, null, null, null, Actor, Ct, org.Id);

        var call = Assert.Single(builder.Calls);
        Assert.Equal(org.Id, call.Organization!.Id);
        Assert.Equal($"https://elsewhere.test/join?t={call.Token}", Assert.Single(_email.Invites).Url);
    }

    [Fact]
    public async Task Invite_NamesTheConfiguredSenderNameAsTheTenant()
    {
        var ops = Build(new DefaultInviteLinkBuilder(new TestTenantContext(Issuer)),
            new Dictionary<string, string?> { ["Email:SenderName"] = "Acme Auth" });

        await ops.InviteAsync("t@invite.test", null, null, null, null, Actor, Ct);

        Assert.Equal("Acme Auth", Assert.Single(_email.Invites).Tenant);
    }

    [Fact]
    public async Task Invite_RunsTheUserCreatedHooks_AsAdmin_AndSurvivesAFailingOne()
    {
        _hook.Throw = true;

        var result = await _ops.InviteAsync("h@invite.test", null, null, null, null, Actor, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal((result.Value!.UserId, "h@invite.test", "admin"), Assert.Single(_hook.Created));
        Assert.Single(_email.Invites);
    }

    [Fact]
    public async Task Invite_WhenTheSendFails_StillSucceeds_AndAudits()
    {
        _email.FailWith = new InvalidOperationException("mail down");

        var result = await _ops.InviteAsync("f@invite.test", null, null, null, null, Actor, Ct);

        Assert.True(result.Succeeded);
        Assert.NotNull(await _users.FindByEmailAsync("f@invite.test"));
        Assert.Equal(["user.invited"], _audit.Entries.Select(e => e.Action));
    }

    // -----------------------------------------------------------------------
    // Resend
    // -----------------------------------------------------------------------

    private async Task<string> InviteOkAsync(string email, Organization? org = null)
    {
        var result = await _ops.InviteAsync(email, null, null, null, null, Actor, Ct, org?.Id);
        Assert.True(result.Succeeded, result.Error);
        _audit.Entries.Clear();
        _email.Invites.Clear();
        return result.Value!.UserId;
    }

    [Fact]
    public async Task Resend_ForAMissingUser_IsNotFound_user_not_found()
    {
        var result = await _ops.ResendInviteAsync("nobody", null, Actor, Ct);

        AssertFailure(result, OperationOutcome.NotFound, "user_not_found");
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task Resend_ForAnAcceptedInvite_IsRefused_already_accepted()
    {
        var userId = await InviteOkAsync("done@invite.test");
        var user = (await _users.GetAsync(userId))!;
        user.EmailConfirmed = true;
        await _users.UpdateAsync(user);

        var result = await _ops.ResendInviteAsync(userId, null, Actor, Ct);

        AssertFailure(result, OperationOutcome.Invalid, "already_accepted");
        Assert.Empty(_email.Invites);
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task Resend_MintsAFreshLink_ThatBothRedeemOnTheGrantStore_AndAudits()
    {
        var userId = await InviteOkAsync("r@invite.test");

        var result = await _ops.ResendInviteAsync(userId, "ja", Actor, Ct);

        Assert.True(result.Succeeded);
        Assert.True(result.Value!.Sent);
        var invite = Assert.Single(_email.Invites);
        Assert.Equal("ja", invite.Locale);
        Assert.Null(invite.Organization);
        Assert.NotNull(await _grants.GetAsync(HashOf(TokenOf(invite.Url))));
        var row = Assert.Single(_audit.Entries);
        Assert.Equal((Actor, "user.invite_resent", "user", userId, "***@invite.test"), (row.Actor, row.Action, row.EntityType, row.EntityId, row.Detail));
    }

    [Fact]
    public async Task Resend_ToASingleInvitedOrganisation_NamesItInTheLink()
    {
        var org = await SeedOrgAsync();
        var userId = await InviteOkAsync("o@invite.test", org);

        await _ops.ResendInviteAsync(userId, null, Actor, Ct);

        var invite = Assert.Single(_email.Invites);
        Assert.Equal(org.Id, invite.Organization!.Id);
        Assert.EndsWith("&organization=acme", invite.Url);
    }

    [Fact]
    public async Task Resend_WithSeveralInvitedOrganisations_FallsBackToThePlainInvite()
    {
        var first = await SeedOrgAsync("one", "One");
        var second = await SeedOrgAsync("two", "Two");
        var userId = await InviteOkAsync("two@invite.test", first);
        _members.With(second.Id, userId, MembershipStatus.Invited);

        await _ops.ResendInviteAsync(userId, null, Actor, Ct);

        var invite = Assert.Single(_email.Invites);
        Assert.Null(invite.Organization);
        Assert.DoesNotContain("organization=", invite.Url);
    }

    [Fact]
    public async Task Resend_IgnoresMembershipsThatAreNotInvited()
    {
        var first = await SeedOrgAsync("one", "One");
        var second = await SeedOrgAsync("two", "Two");
        var userId = await InviteOkAsync("mix@invite.test", first);
        _members.With(second.Id, userId, MembershipStatus.Suspended);

        await _ops.ResendInviteAsync(userId, null, Actor, Ct);

        Assert.Equal(first.Id, Assert.Single(_email.Invites).Organization!.Id);
    }

    [Fact]
    public async Task Resend_WhenTheSendThrows_IsFailed_invite_email_failed_WithNoAuditRow()
    {
        var userId = await InviteOkAsync("down@invite.test");
        _email.FailWith = new InvalidOperationException("mail down");

        var result = await _ops.ResendInviteAsync(userId, null, Actor, Ct);

        AssertFailure(result, OperationOutcome.Failed, "invite_email_failed");
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task Resend_WhenCancelled_PropagatesRatherThanReportingAMailFailure()
    {
        var userId = await InviteOkAsync("cancel@invite.test");
        _email.FailWith = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(() => _ops.ResendInviteAsync(userId, null, Actor, Ct));
    }

    // -----------------------------------------------------------------------
    // Doubles
    // -----------------------------------------------------------------------

    private sealed class RecordingInviteEmail : IEmailService
    {
        public List<(string To, string Tenant, string Url, string? Locale, Organization? Organization)> Invites { get; } = [];

        public Exception? FailWith { get; set; }

        public Task SendVerificationEmailAsync(string email, string callbackUrl, CancellationToken ct = default) => Task.CompletedTask;

        public Task SendPasswordResetEmailAsync(string email, string callbackUrl, CancellationToken ct = default) => Task.CompletedTask;

        public Task SendUserInviteEmailAsync(string toEmail, string tenantName, string acceptUrl, string? locale = null,
            CancellationToken ct = default, Organization? organization = null)
        {
            if (FailWith is not null) throw FailWith;
            Invites.Add((toEmail, tenantName, acceptUrl, locale, organization));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLinkBuilder : IInviteLinkBuilder
    {
        public List<(string Token, Organization? Organization)> Calls { get; } = [];

        public Task<string> BuildAcceptUrlAsync(string token, Organization? organization, CancellationToken ct = default)
        {
            Calls.Add((token, organization));
            return Task.FromResult($"https://elsewhere.test/join?t={token}");
        }
    }

    private sealed class RecordingCreatedHook : IAuthHook
    {
        public List<(string UserId, string Email, string CreatedVia)> Created { get; } = [];

        public bool Throw { get; set; }

        public Task OnUserCreatedAsync(string userId, string email, string createdVia, CancellationToken ct = default)
        {
            Created.Add((userId, email, createdVia));
            return Throw ? throw new InvalidOperationException("hook down") : Task.CompletedTask;
        }

        public Task OnUserAuthenticatedAsync(string userId, string email, string method, string? clientId = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task OnLoginFailedAsync(string email, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task OnTokenIssuedAsync(string? subjectId, string clientId, string grantType, CancellationToken ct = default) => Task.CompletedTask;
        public Task<MfaPolicy> ResolveMfaPolicyAsync(string userId, string email, MfaPolicy clientPolicy, string clientId, CancellationToken ct = default) => Task.FromResult(clientPolicy);
        public Task OnMfaVerifiedAsync(string userId, string email, string mfaMethod, CancellationToken ct = default) => Task.CompletedTask;
        public Task OnUserUpdatedAsync(string userId, string email, string updatedVia, CancellationToken ct = default) => Task.CompletedTask;
        public Task OnUserDeletedAsync(string userId, string email, string deletedVia, CancellationToken ct = default) => Task.CompletedTask;
    }
}
