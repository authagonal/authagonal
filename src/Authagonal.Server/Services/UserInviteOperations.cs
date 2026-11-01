using System.Security.Cryptography;
using System.Text;
using Authagonal.Core.Constants;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.Server.Services;

/// <summary>
/// Inviting someone by email, and sending the invite again, as operations rather than HTTP handlers, so
/// the library's admin endpoints and any host surface doing the same job (a portal route, a tool) run the
/// same code and write the same audit rows.
/// </summary>
/// <remarks>
/// <para>
/// The invite link redeems a <c>password_reset</c> grant at the existing <c>/api/auth/reset-password</c>,
/// which sets the password, confirms the address and fires <see cref="IAuthHook.OnEmailConfirmedAsync"/>;
/// <see cref="OrganizationInviteActivationHook"/> turns the invitee's <c>invited</c> memberships active
/// there. There is no second redeem route.
/// </para>
/// <para>
/// Where the link points is the host's to decide through <see cref="IInviteLinkBuilder"/>. Rate limiting
/// resend and deciding who may invite into an organisation are likewise the host's policy and live in the
/// host's own surface.
/// </para>
/// </remarks>
public sealed class UserInviteOperations(
    IUserStore userStore,
    IOrganizationStore orgStore,
    IOrganizationMembershipStore membershipStore,
    IGrantStore grantStore,
    PasswordHasher passwordHasher,
    IEmailService emailService,
    IInviteLinkBuilder linkBuilder,
    IEnumerable<IAuthHook> hooks,
    IAuditLogger audit,
    IConfiguration configuration,
    ILogger<UserInviteOperations> logger)
{
    /// <summary>How long an invite link stays redeemable. An invite can sit in an inbox, so it is longer than a reset.</summary>
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(7);

    /// <summary>
    /// Invites someone by email. The account exists immediately but cannot be signed into: an unguessable
    /// password hash that is never sent anywhere, plus an unconfirmed address, mean the only way in is the
    /// single-use setup link.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With <paramref name="organizationId"/>, the invite is INTO that organisation: the account is created
    /// with an <see cref="MembershipStatus.Invited"/> membership carrying <paramref name="organizationRoles"/>,
    /// and the email wears the organisation's branding and names it. The activation hook promotes the
    /// membership to active when the invitee sets their password.
    /// </para>
    /// <para>
    /// Everything about the organisation is checked BEFORE the account is created: a refused organisation
    /// must not leave a stray pending user behind. New accounts only: an existing user is a 409, and joins
    /// an organisation through its members instead.
    /// </para>
    /// </remarks>
    public async Task<OperationResult<InvitedUser>> InviteAsync(
        string? email, string? firstName, string? lastName, string? locale,
        string? inviterLocale, string actor, CancellationToken ct,
        string? organizationId = null, List<string>? organizationRoles = null)
    {
        if (string.IsNullOrWhiteSpace(email))
            return OperationResult<InvitedUser>.Invalid("email_required");

        // The address becomes a storage key, so a value the storage service would reject is refused here
        // rather than after the account is half written; the library's other creation paths do the same.
        if (!StorageKeySafety.IsPlausibleEmail(email))
            return OperationResult<InvitedUser>.Invalid("invalid_email", "email must be a valid email address", ["email"]);

        Organization? organization = null;
        if (!string.IsNullOrWhiteSpace(organizationId))
        {
            organization = await orgStore.GetAsync(organizationId.Trim(), ct);
            if (organization is null)
                return OperationResult<InvitedUser>.NotFound("organization_not_found");
            if ((organizationRoles ?? []).Any(ReservedRolePrefixes.IsReserved))
                return OperationResult<InvitedUser>.Invalid("invalid_role",
                    "Organisation roles cannot carry a reserved prefix. Use a role name of your own.");
        }

        var existing = await userStore.FindByEmailAsync(email, ct);
        if (existing is not null)
            return OperationResult<InvitedUser>.Conflict("user_already_exists");

        var user = new AuthUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = email.ToLowerInvariant(),
            NormalizedEmail = email.ToUpperInvariant(),
            PasswordHash = passwordHasher.HashPassword(Convert.ToHexString(RandomNumberGenerator.GetBytes(32))),
            FirstName = firstName,
            LastName = lastName,
            Locale = Locales.Normalize(locale),
            EmailConfirmed = false,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await userStore.CreateAsync(user, ct);

        // Before the hooks and the email, so the invite a user receives never names an organisation they
        // are not yet a member of.
        if (organization is not null)
        {
            var invitedAt = DateTimeOffset.UtcNow;
            await membershipStore.UpsertAsync(new OrganizationMembership
            {
                OrganizationId = organization.Id,
                UserId = user.Id,
                Roles = organizationRoles ?? [],
                Status = MembershipStatus.Invited,
                // The audit actor, so the row keeps who invited this person through acceptance and any
                // later suspension. "unknown" is the actor's absence, not a person.
                InvitedByUserId = actor == "unknown" ? null : actor,
                InvitedAt = invitedAt,
                CreatedAt = invitedAt,
            }, ct);
            await audit.LogAsync(actor, "organization.member.invited", "organization", organization.Id,
                $"user={user.Id}", ct);
        }

        var token = await MintSetupGrantAsync(user.Id, ct);

        foreach (var hook in hooks)
        {
            try { await hook.OnUserCreatedAsync(user.Id, user.Email, "admin", ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "OnUserCreated hook failed for {UserId}", user.Id);
            }
        }

        // Best-effort send: the pending user and the grant already exist, so a transient mail failure does
        // not lose the invite, it can be resent. This is the opposite choice from resend below, where
        // sending the mail IS the whole action.
        try
        {
            await emailService.SendUserInviteEmailAsync(
                user.Email, TenantName, await linkBuilder.BuildAcceptUrlAsync(token, organization, ct),
                inviterLocale, ct, organization);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to send invite email for {UserId}", user.Id);
        }

        // The entity id already carries the user id; the address is masked in detail so an audit dump
        // exposes no identity (domain kept for at-a-glance context).
        await audit.LogAsync(actor, "user.invited", "user", user.Id, MaskEmail(user.Email), ct);

        return OperationResult<InvitedUser>.Ok(new InvitedUser(user.Id, user.Email));
    }

    /// <summary>
    /// Resends a pending invite, minting a fresh setup grant. Only while the invite is still pending:
    /// once accepted, password reset is the right path instead.
    /// </summary>
    /// <remarks>
    /// A mail failure IS an error here, unlike the initial invite, because sending the message is the
    /// entire point of the action and silently succeeding would tell the operator the opposite.
    /// </remarks>
    public async Task<OperationResult<SentResult>> ResendInviteAsync(
        string userId, string? inviterLocale, string actor, CancellationToken ct)
    {
        var user = await userStore.GetAsync(userId, ct);
        if (user is null)
            return OperationResult<SentResult>.NotFound("user_not_found");
        if (user.EmailConfirmed)
            return OperationResult<SentResult>.Invalid("already_accepted");

        var token = await MintSetupGrantAsync(user.Id, ct);
        var organization = await PendingInviteOrganizationAsync(user.Id, ct);

        try
        {
            await emailService.SendUserInviteEmailAsync(
                user.Email, TenantName, await linkBuilder.BuildAcceptUrlAsync(token, organization, ct),
                inviterLocale, ct, organization);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to resend invite email for {UserId}", user.Id);
            return OperationResult<SentResult>.Failed("invite_email_failed");
        }

        await audit.LogAsync(actor, "user.invite_resent", "user", user.Id, MaskEmail(user.Email), ct);

        return OperationResult<SentResult>.Ok(new SentResult(true));
    }

    /// <summary>
    /// The name the invite copy gives for the thing invited to when no organisation is named: the
    /// configured sender name (<c>Email:SenderName</c>), else <c>Authagonal</c>.
    /// </summary>
    private string TenantName =>
        string.IsNullOrWhiteSpace(configuration["Email:SenderName"]) ? "Authagonal" : configuration["Email:SenderName"]!;

    /// <summary>
    /// The organisation a resent invite is for: the one the user holds an invited membership of, when
    /// there is exactly one. Several (an operator added more by hand) cannot pick one honestly, so the
    /// resend falls back to the plain invite.
    /// </summary>
    private async Task<Organization?> PendingInviteOrganizationAsync(string userId, CancellationToken ct)
    {
        var invited = (await membershipStore.ListByUserAsync(userId, ct))
            .Where(m => m.Status == MembershipStatus.Invited)
            .ToList();
        return invited.Count == 1 ? await orgStore.GetAsync(invited[0].OrganizationId, ct) : null;
    }

    /// <summary>
    /// Mints the single-use password-setup grant an invite link redeems. It is the same "password_reset"
    /// grant the reset page uses, with a longer 7-day expiry because an invite can sit in an inbox.
    /// </summary>
    private async Task<string> MintSetupGrantAsync(string userId, CancellationToken ct)
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

        var now = DateTimeOffset.UtcNow;
        await grantStore.StoreAsync(new PersistedGrant
        {
            Key = tokenHash,
            Type = "password_reset",
            SubjectId = userId,
            ClientId = "auth",
            Data = userId,
            CreatedAt = now,
            ExpiresAt = now.Add(InviteLifetime),
        }, ct);

        return token;
    }

    /// <summary><c>***@domain</c>: the local part dropped, the domain kept.</summary>
    internal static string MaskEmail(string email)
    {
        var at = email.LastIndexOf('@');
        return at < 0 ? "***" : $"***{email[at..]}";
    }
}
