using Authagonal.Core.Services;
using DnsClient;
using DnsClient.Protocol;

namespace Authagonal.Server.Services;

/// <summary>Real DNS, via the system resolver, over DnsClient. The library's default <see cref="ITxtResolver"/>.</summary>
public sealed class DnsTxtResolver : ITxtResolver
{
    private readonly LookupClient _dns = new();

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ResolveTxtAsync(string name, CancellationToken ct = default)
    {
        var result = await _dns.QueryAsync(name, QueryType.TXT, cancellationToken: ct);
        return JoinCharacterStrings(result.Answers.TxtRecords());
    }

    /// <summary>
    /// One value per record. A TXT record longer than 255 bytes arrives as several character-strings that
    /// together form one value (RFC 7208 section 3.3 uses the same rule), so each record's strings are
    /// joined, not split.
    /// </summary>
    internal static IReadOnlyList<string> JoinCharacterStrings(IEnumerable<TxtRecord> records) =>
        records.Select(r => string.Concat(r.Text)).ToList();
}
