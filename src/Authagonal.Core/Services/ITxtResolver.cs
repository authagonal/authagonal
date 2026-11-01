namespace Authagonal.Core.Services;

/// <summary>Resolves the TXT values published at a name; empty when there are none.</summary>
/// <remarks>
/// The seam the organisation email-domain proof reads DNS through, so that proof is testable without
/// DNS and a host with its own resolver (an internal one, a DNS-over-HTTPS client) can substitute it.
/// A lookup that fails throws; a name with no record answers an empty list. The library registers a
/// DnsClient-backed default with <c>TryAdd</c>, so a host's own registration wins.
/// </remarks>
public interface ITxtResolver
{
    /// <summary>The TXT values published at <paramref name="name"/>, one string per record.</summary>
    Task<IReadOnlyList<string>> ResolveTxtAsync(string name, CancellationToken ct = default);
}
