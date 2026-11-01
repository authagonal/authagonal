using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.Server.Services;

/// <summary>
/// Turns an organisation invite into a membership when the invitee accepts it: every
/// <see cref="MembershipStatus.Invited"/> membership the user holds becomes
/// <see cref="MembershipStatus.Active"/> the moment their address is confirmed.
/// </summary>
/// <remarks>
/// <para>
/// Accepting an invite IS confirming the address: the invite link redeems the same single-use grant a
/// password reset does, and that redemption marks the email confirmed and fires
/// <see cref="IAuthHook.OnEmailConfirmedAsync"/> (only when it was not already confirmed), so the hook
/// needs no state of its own about which invite was accepted. It also runs for an address confirmed any
/// other way, which is the same fact.
/// </para>
/// <para>
/// Only <see cref="MembershipStatus.Invited"/> rows move; a suspended membership is an operator's decision
/// and stays suspended. Roles, inviter and invitation time are kept. Best-effort: the confirmation is
/// already persisted when this runs, and a failure here must not turn a successful password setup into an
/// error page, so it is logged and the membership can still be activated by hand. Idempotent: a second run
/// finds nothing invited.
/// </para>
/// <para>
/// A singleton, because hooks are resolved by singleton consumers; the stores it writes through can be
/// scoped, so they come from the request's own scope (or a fresh one when there is no request).
/// </para>
/// </remarks>
public sealed class OrganizationInviteActivationHook(
    IHttpContextAccessor httpContextAccessor,
    IServiceScopeFactory scopeFactory,
    ILogger<OrganizationInviteActivationHook> logger) : IAuthHook
{
    /// <inheritdoc />
    public async Task OnEmailConfirmedAsync(string userId, string email, CancellationToken ct = default)
    {
        try
        {
            if (httpContextAccessor.HttpContext?.RequestServices is { } requestServices)
            {
                await ActivateAsync(requestServices, userId, ct);
                return;
            }

            using var scope = scopeFactory.CreateScope();
            await ActivateAsync(scope.ServiceProvider, userId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not activate invited organisation memberships for {UserId}", userId);
        }
    }

    private static async Task ActivateAsync(IServiceProvider services, string userId, CancellationToken ct)
    {
        var memberships = services.GetService<IOrganizationMembershipStore>();
        if (memberships is null) return;
        var audit = services.GetService<IAuditLogger>();

        foreach (var membership in await memberships.ListByUserAsync(userId, ct))
        {
            if (membership.Status != MembershipStatus.Invited) continue;

            var now = DateTimeOffset.UtcNow;
            membership.Status = MembershipStatus.Active;
            membership.JoinedAt ??= now;
            membership.UpdatedAt = now;
            await memberships.UpsertAsync(membership, ct);
            if (audit is not null)
                await audit.LogAsync(userId, "organization.member.joined", "organization", membership.OrganizationId,
                    $"user={userId}", ct);
        }
    }

    // No-ops: this hook only cares about the address being confirmed.
    /// <inheritdoc />
    public Task OnUserAuthenticatedAsync(string userId, string email, string method, string? clientId, CancellationToken ct) => Task.CompletedTask;
    /// <inheritdoc />
    public Task OnUserCreatedAsync(string userId, string email, string createdVia, CancellationToken ct) => Task.CompletedTask;
    /// <inheritdoc />
    public Task OnLoginFailedAsync(string email, string reason, CancellationToken ct) => Task.CompletedTask;
    /// <inheritdoc />
    public Task OnTokenIssuedAsync(string? subjectId, string clientId, string grantType, CancellationToken ct) => Task.CompletedTask;
    /// <inheritdoc />
    public Task<MfaPolicy> ResolveMfaPolicyAsync(string userId, string email, MfaPolicy clientPolicy, string clientId, CancellationToken ct) => Task.FromResult(clientPolicy);
    /// <inheritdoc />
    public Task OnMfaVerifiedAsync(string userId, string email, string mfaMethod, CancellationToken ct) => Task.CompletedTask;
    /// <inheritdoc />
    public Task OnUserUpdatedAsync(string userId, string email, string updatedVia, CancellationToken ct) => Task.CompletedTask;
    /// <inheritdoc />
    public Task OnUserDeletedAsync(string userId, string email, string deletedVia, CancellationToken ct) => Task.CompletedTask;
}
