using System.Text.RegularExpressions;
using Authagonal.Core.Models;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Tests;

/// <summary>
/// Organisation domain claims over a fake <see cref="Authagonal.Core.Services.ITxtResolver"/>: the claim and
/// its proof record, every refusal code, the verify outcomes, idempotence, removal and the audit rows.
/// </summary>
public sealed partial class OrganizationDomainTests
{
    private const string Actor = "admin@example.test";

    private readonly WritableOrganizationStore _orgs = new();
    private readonly WritableOrganizationMembershipStore _members = new();
    private readonly InMemoryUserStore _users = new();
    private readonly RecordingAuditLogger _audit = new();
    private readonly FakeTxtResolver _dns = new();
    private readonly OrganizationOperations _ops;

    public OrganizationDomainTests()
    {
        _ops = new OrganizationOperations(_orgs, _members, _users, _audit, _dns, NullLogger<OrganizationOperations>.Instance);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{43}$")]
    private static partial Regex Base64Url256();

    private static CancellationToken Ct => CancellationToken.None;

    private async Task<string> CreateOrgAsync(string slug)
    {
        var created = await _ops.CreateAsync(slug, $"Name {slug}", null, null, null, null, null, Actor, Ct);
        Assert.True(created.Succeeded, created.Error);
        return created.Value!.Id;
    }

    private async Task<OrganizationDomainDto> ClaimAsync(string orgId, string domain)
    {
        var added = await _ops.AddDomainAsync(orgId, domain, Actor, Ct);
        Assert.True(added.Succeeded, added.Error);
        return added.Value!;
    }

    private static void AssertFailure<T>(OperationResult<T> result, OperationOutcome outcome, string code)
    {
        Assert.False(result.Succeeded);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(code, result.Error);
    }

    // ---- claim -------------------------------------------------------------------------------------

    [Fact]
    public async Task Add_NormalisesAndReturnsTheRecordToPublish()
    {
        var id = await CreateOrgAsync("acme");

        var claim = await ClaimAsync(id, "  Acme.COM. ");

        Assert.Equal("acme.com", claim.Domain);
        Assert.False(claim.Verified);
        Assert.Null(claim.VerifiedAt);
        Assert.Equal("_authagonal-org.acme.com", claim.RecordName);
        Assert.StartsWith("authagonal-org-verify=", claim.RecordValue);
        Assert.Matches(Base64Url256(), claim.RecordValue["authagonal-org-verify=".Length..]);
        Assert.Contains(_audit.Entries, e => e.Action == "organization.domain_added" && e.EntityId == id && e.Detail == "domain=acme.com" && e.Actor == Actor);
    }

    [Fact]
    public async Task Add_TwoClaimsMintDifferentTokens()
    {
        var id = await CreateOrgAsync("acme");

        var a = await ClaimAsync(id, "acme.com");
        var b = await ClaimAsync(id, "acme.org");

        Assert.NotEqual(a.RecordValue, b.RecordValue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("acme")]
    [InlineData("https://acme.com")]
    [InlineData("acme.com/path")]
    [InlineData("acme.com:8080")]
    [InlineData("*.acme.com")]
    [InlineData("a@acme.com")]
    [InlineData("ac me.com")]
    [InlineData("-acme.com")]
    [InlineData("acme-.com")]
    [InlineData("acme..com")]
    [InlineData("10.0.0.1")]
    public async Task Add_NotABareDomain_IsDomainInvalid(string? domain)
    {
        var id = await CreateOrgAsync("acme");

        var result = await _ops.AddDomainAsync(id, domain, Actor, Ct);

        AssertFailure(result, OperationOutcome.Invalid, "domain_invalid");
        Assert.DoesNotContain(_audit.Entries, e => e.Action == "organization.domain_added");
    }

    [Fact]
    public async Task Add_UnknownOrganization_IsOrganizationNotFound()
    {
        var result = await _ops.AddDomainAsync("org_missing", "acme.com", Actor, Ct);

        AssertFailure(result, OperationOutcome.NotFound, "organization_not_found");
    }

    [Fact]
    public async Task Add_SameDomainTwiceOnOneOrganization_IsDomainExists()
    {
        var id = await CreateOrgAsync("acme");
        await ClaimAsync(id, "acme.com");

        var result = await _ops.AddDomainAsync(id, "ACME.com", Actor, Ct);

        AssertFailure(result, OperationOutcome.Conflict, "domain_exists");
        Assert.Single(_audit.Entries, e => e.Action == "organization.domain_added");
    }

    [Fact]
    public async Task Add_DomainAnotherOrganizationHolds_IsDomainTaken_VerifiedOrNot()
    {
        var first = await CreateOrgAsync("first");
        var second = await CreateOrgAsync("second");
        await ClaimAsync(first, "acme.com");

        var result = await _ops.AddDomainAsync(second, "acme.com", Actor, Ct);

        AssertFailure(result, OperationOutcome.Conflict, "domain_taken");
        Assert.Contains("first", result.Message);
        Assert.Single(_audit.Entries, e => e.Action == "organization.domain_added");
    }

    // ---- verify ------------------------------------------------------------------------------------

    [Fact]
    public async Task Verify_MatchingRecord_StampsTheClaimAndAudits()
    {
        var id = await CreateOrgAsync("acme");
        var claim = await ClaimAsync(id, "acme.com");
        _dns.Publish(claim.RecordName, "unrelated", claim.RecordValue);

        var result = await _ops.VerifyDomainAsync(id, "acme.com", Actor, Ct);

        Assert.True(result.Succeeded, result.Error);
        Assert.True(result.Value!.Verified);
        Assert.NotNull(result.Value.VerifiedAt);
        Assert.Equal([claim.RecordName], _dns.Queried);
        var stored = await _orgs.GetAsync(id);
        Assert.NotNull(stored!.Domains.Single().VerifiedAt);
        Assert.Contains(_audit.Entries, e => e.Action == "organization.domain_verified" && e.EntityId == id && e.Detail == "domain=acme.com");
    }

    [Fact]
    public async Task Verify_RecordWithSurroundingWhitespace_StillMatches()
    {
        var id = await CreateOrgAsync("acme");
        var claim = await ClaimAsync(id, "acme.com");
        _dns.Publish(claim.RecordName, $"  {claim.RecordValue}\t");

        var result = await _ops.VerifyDomainAsync(id, "acme.com", Actor, Ct);

        Assert.True(result.Succeeded, result.Error);
    }

    [Fact]
    public async Task Verify_NoRecord_IsVerificationFailedConflictCarryingTheRecordToAdd()
    {
        var id = await CreateOrgAsync("acme");
        var claim = await ClaimAsync(id, "acme.com");

        var result = await _ops.VerifyDomainAsync(id, "acme.com", Actor, Ct);

        AssertFailure(result, OperationOutcome.Conflict, "verification_failed");
        Assert.Contains(claim.RecordName, result.Message);
        Assert.Contains(claim.RecordValue, result.Message);
        Assert.Contains($"No TXT record was found at {claim.RecordName}.", result.Message);
        Assert.Null((await _orgs.GetAsync(id))!.Domains.Single().VerifiedAt);
        Assert.DoesNotContain(_audit.Entries, e => e.Action == "organization.domain_verified");
    }

    [Theory]
    [InlineData("PREFIX")]
    [InlineData("SUFFIX")]
    [InlineData("CASE")]
    public async Task Verify_RecordThatIsNotExactlyTheValue_IsVerificationFailedAndShowsWhatWasFound(string mutation)
    {
        var id = await CreateOrgAsync("acme");
        var claim = await ClaimAsync(id, "acme.com");
        var wrong = mutation switch
        {
            "PREFIX" => "x" + claim.RecordValue,
            "SUFFIX" => claim.RecordValue + "x",
            _ => claim.RecordValue.ToUpperInvariant(),
        };
        _dns.Publish(claim.RecordName, wrong);

        var result = await _ops.VerifyDomainAsync(id, "acme.com", Actor, Ct);

        AssertFailure(result, OperationOutcome.Conflict, "verification_failed");
        Assert.Contains($"Found at {claim.RecordName}: \"{wrong}\"", result.Message);
    }

    [Fact]
    public async Task Verify_LookupThrows_IsVerificationFailedNamingTheRecord()
    {
        var id = await CreateOrgAsync("acme");
        var claim = await ClaimAsync(id, "acme.com");
        _dns.Failure = new InvalidOperationException("dns down");

        var result = await _ops.VerifyDomainAsync(id, "acme.com", Actor, Ct);

        AssertFailure(result, OperationOutcome.Conflict, "verification_failed");
        Assert.Equal(
            $"DNS lookup failed for {claim.RecordName}. Add a TXT record at {claim.RecordName} with the value {claim.RecordValue}.",
            result.Message);
    }

    [Fact]
    public async Task Verify_UnknownDomainOrOrganization_AreNotFound()
    {
        var id = await CreateOrgAsync("acme");

        AssertFailure(await _ops.VerifyDomainAsync(id, "nothere.com", Actor, Ct), OperationOutcome.NotFound, "domain_not_found");
        AssertFailure(await _ops.VerifyDomainAsync("org_missing", "acme.com", Actor, Ct), OperationOutcome.NotFound, "organization_not_found");
    }

    [Fact]
    public async Task Verify_AlreadyVerified_IsIdempotentAndDoesNotLookAgainOrAuditAgain()
    {
        var id = await CreateOrgAsync("acme");
        var claim = await ClaimAsync(id, "acme.com");
        _dns.Publish(claim.RecordName, claim.RecordValue);
        var first = await _ops.VerifyDomainAsync(id, "acme.com", Actor, Ct);

        _dns.Failure = new InvalidOperationException("would fail if asked");
        var second = await _ops.VerifyDomainAsync(id, "acme.com", Actor, Ct);

        Assert.True(second.Succeeded, second.Error);
        Assert.Equal(first.Value!.VerifiedAt, second.Value!.VerifiedAt);
        Assert.Single(_dns.Queried);
        Assert.Single(_audit.Entries, e => e.Action == "organization.domain_verified");
    }

    [Fact]
    public async Task Verify_AnotherOrganizationHoldsTheDomainToo_IsDomainTakenAtVerify()
    {
        var first = await CreateOrgAsync("first");
        var second = await CreateOrgAsync("second");
        var claim = await ClaimAsync(first, "acme.com");
        // The lost race AddDomainAsync's read-then-write cannot rule out: a second claim lands on the row.
        var rival = (await _orgs.GetAsync(second))!;
        rival.Domains.Add(new OrganizationDomain { Domain = "acme.com", VerificationToken = "rival", CreatedAt = DateTimeOffset.UtcNow });
        await _orgs.UpsertAsync(rival);
        _dns.Publish(claim.RecordName, claim.RecordValue);

        var result = await _ops.VerifyDomainAsync(first, "acme.com", Actor, Ct);

        AssertFailure(result, OperationOutcome.Conflict, "domain_taken");
        Assert.Empty(_dns.Queried);
        Assert.Null((await _orgs.GetAsync(first))!.Domains.Single().VerifiedAt);
    }

    // ---- remove ------------------------------------------------------------------------------------

    [Fact]
    public async Task Remove_DropsTheClaimAndAudits_LeavingMembershipsAlone()
    {
        var id = await CreateOrgAsync("acme");
        await ClaimAsync(id, "acme.com");

        var result = await _ops.RemoveDomainAsync(id, "ACME.com", Actor, Ct);

        Assert.True(result.Succeeded, result.Error);
        Assert.Empty((await _orgs.GetAsync(id))!.Domains);
        Assert.Contains(_audit.Entries, e => e.Action == "organization.domain_removed" && e.EntityId == id && e.Detail == "domain=acme.com");
        // and the domain is free for another organisation
        var other = await CreateOrgAsync("other");
        Assert.True((await _ops.AddDomainAsync(other, "acme.com", Actor, Ct)).Succeeded);
    }

    [Fact]
    public async Task Remove_UnknownDomainOrOrganization_AreNotFound_AndAuditNothing()
    {
        var id = await CreateOrgAsync("acme");
        var before = _audit.Entries.Count;

        AssertFailure(await _ops.RemoveDomainAsync(id, "nothere.com", Actor, Ct), OperationOutcome.NotFound, "domain_not_found");
        AssertFailure(await _ops.RemoveDomainAsync("org_missing", "acme.com", Actor, Ct), OperationOutcome.NotFound, "organization_not_found");
        Assert.Equal(before, _audit.Entries.Count);
    }
}
