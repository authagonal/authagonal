using Authagonal.Server.Services;
using DnsClient;
using DnsClient.Protocol;

namespace Authagonal.Tests;

/// <summary>
/// <see cref="DnsTxtResolver"/>'s join of a TXT record's character-strings. A record longer than 255 bytes
/// is split by DNS into several character-strings; the value a user published is those strings run
/// together, so a verification token near the limit only matches if they are joined.
/// </summary>
public sealed class DnsTxtResolverTests
{
    private static TxtRecord Record(params string[] characterStrings) =>
        new(new ResourceRecordInfo("_authagonal-org.acme.com", ResourceRecordType.TXT, QueryClass.IN, 300, 0),
            characterStrings, characterStrings);

    [Fact]
    public void JoinCharacterStrings_ConcatenatesTheStringsOfOneRecordWithNothingBetween()
    {
        var joined = DnsTxtResolver.JoinCharacterStrings([Record("authagonal-org-verify=AAAA", "BBBB", "CCCC")]);

        Assert.Equal(["authagonal-org-verify=AAAABBBBCCCC"], joined);
    }

    [Fact]
    public void JoinCharacterStrings_KeepsRecordsApartAndInOrder()
    {
        var joined = DnsTxtResolver.JoinCharacterStrings(
            [Record("v=spf1 ", "-all"), Record("authagonal-org-verify=token")]);

        Assert.Equal(["v=spf1 -all", "authagonal-org-verify=token"], joined);
    }

    [Fact]
    public void JoinCharacterStrings_NoRecords_IsEmpty()
    {
        Assert.Empty(DnsTxtResolver.JoinCharacterStrings([]));
    }
}
