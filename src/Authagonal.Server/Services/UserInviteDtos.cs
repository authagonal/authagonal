namespace Authagonal.Server.Services;

/// <summary>The account an invitation created.</summary>
/// <param name="UserId">The new account's id.</param>
/// <param name="Email">The invited address, lowercased as it was stored.</param>
public sealed record InvitedUser(string UserId, string Email);

/// <summary>Whether a message was sent.</summary>
/// <param name="Sent">True when the message went out.</param>
public sealed record SentResult(bool Sent);
