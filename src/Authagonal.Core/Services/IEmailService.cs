using Authagonal.Core.Models;

namespace Authagonal.Core.Services;

public interface IEmailService
{
    Task SendVerificationEmailAsync(string email, string callbackUrl, CancellationToken ct = default);
    Task SendPasswordResetEmailAsync(string email, string callbackUrl, CancellationToken ct = default);

    /// <summary>
    /// Sent when someone attempts to register an email that already has an account. Lets the real
    /// owner sign in / reset their password, while the registration endpoint returns the same neutral
    /// response whether or not the email existed (no account enumeration). Default no-op so existing
    /// implementors keep compiling.
    /// </summary>
    Task SendAccountExistsEmailAsync(string email, string signInUrl, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>
    /// Sent when an administrator invites someone. <paramref name="acceptUrl"/> redeems the same
    /// single-use grant a password reset does, so the invitee sets their own password, which also confirms
    /// the address. <paramref name="locale"/> is the inviting administrator's language (the invitee has no
    /// stored preference yet); <paramref name="organization"/>, when the invite is into one, brands the
    /// message and names the organisation in the copy. Default body sends the password-reset email with the
    /// accept link, so an implementor written before invitations existed still delivers the link.
    /// </summary>
    Task SendUserInviteEmailAsync(string toEmail, string tenantName, string acceptUrl, string? locale = null,
        CancellationToken ct = default, Organization? organization = null)
        => SendPasswordResetEmailAsync(toEmail, acceptUrl, ct);
}
