using Authagonal.Core.Models;

namespace Authagonal.Core.Services;

/// <summary>
/// Builds the link an invitation email carries. The library's default points at the issuer's own
/// <c>/accept-invite</c> page; a host that serves the accept page elsewhere (an organisation's own domain,
/// a separate front end) registers its own before <c>AddAuthagonalCore</c>, and invite and resend both
/// build every link through it.
/// </summary>
public interface IInviteLinkBuilder
{
    /// <summary>
    /// The URL the invitee opens to set a password and accept.
    /// </summary>
    /// <param name="token">The single-use setup token. Not yet escaped for a URL.</param>
    /// <param name="organization">The organisation the invite is into, or null for a plain invite.</param>
    /// <param name="ct">Cancellation.</param>
    Task<string> BuildAcceptUrlAsync(string token, Organization? organization, CancellationToken ct = default);
}
