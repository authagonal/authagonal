using Authagonal.Server.Services;

namespace Authagonal.Tests;

/// <summary>
/// Legacy Scrypt.NET (<c>$s2$</c>) password hashes — verified and flagged for rehash to the native
/// PBKDF2v2 format on first login, the same "accept on import, upgrade on next login" contract
/// bcrypt and ASP.NET Identity V3 already get in <c>PasswordHasher</c>.
/// </summary>
public class PasswordHasherScryptTests
{
    private readonly PasswordHasher _hasher = new();

    /// <summary>
    /// A real <c>Scrypt.NET</c> <c>ScryptEncoder</c> output for the password below (default cost
    /// N=16384, r=8, p=1) — synthetic, generated for this test, not client data. Captured so the
    /// verifier is checked against an actual third-party-library hash, not only against this
    /// codebase's own derivation of the RFC vectors in <see cref="ScryptTests"/>.
    /// </summary>
    private const string RealScryptNetHash =
        "$s2$16384$8$1$mcvMQ5XzJFe7uluUG8jc7XIA/32wl7lg1Rot3Ae9zwM=$hOhs1JszxAn81VA+oqKQy3LeljQSM9Oq/vJx1W73Ebw=";

    private const string RealScryptNetPassword = "correct horse battery staple";

    [Fact]
    public void VerifyPassword_RealScryptNetHash_CorrectPassword_ReturnsRehashNeeded()
    {
        var result = _hasher.VerifyPassword(RealScryptNetPassword, RealScryptNetHash);
        Assert.Equal(PasswordVerifyResult.SuccessRehashNeeded, result);
    }

    [Fact]
    public void VerifyPassword_RealScryptNetHash_WrongPassword_Fails()
    {
        var result = _hasher.VerifyPassword("wrong password", RealScryptNetHash);
        Assert.Equal(PasswordVerifyResult.Failed, result);
    }

    [Fact]
    public void FirstLogin_RehashesScryptNetHashToNativeFormat()
    {
        // Mirrors AuthEndpoints' upgrade-on-login step: verify (learns a rehash is owed), then
        // HashPassword to produce the replacement that gets persisted via RecordSuccessfulLoginAsync.
        var verifyResult = _hasher.VerifyPassword(RealScryptNetPassword, RealScryptNetHash);
        Assert.Equal(PasswordVerifyResult.SuccessRehashNeeded, verifyResult);

        var rehashed = _hasher.HashPassword(RealScryptNetPassword);

        Assert.StartsWith("PBKDF2v2$", rehashed);
        Assert.NotEqual(RealScryptNetHash, rehashed);
        // The stored hash is now native, so the SAME password no longer needs a rehash.
        Assert.Equal(PasswordVerifyResult.Success, _hasher.VerifyPassword(RealScryptNetPassword, rehashed));
    }

    // -----------------------------------------------------------------------
    // Negative / malformed-input cases — every one must fail closed, never throw or
    // burn CPU on an attacker-chosen cost parameter, same posture as the bcrypt /
    // ASP.NET Identity V3 paths in PasswordHasherTests / CredentialHardeningTests.
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("$s2$16384$8$1$mcvMQ5XzJFe7uluUG8jc7XIA/32wl7lg1Rot3Ae9zwM=")] // wrong segment count (6, not 7) — missing the key
    [InlineData("$s3$16384$8$1$mcvMQ5XzJFe7uluUG8jc7XIA/32wl7lg1Rot3Ae9zwM=$hOhs1JszxAn81VA+oqKQy3LeljQSM9Oq/vJx1W73Ebw=")] // wrong tag
    [InlineData("$s2$notanumber$8$1$mcvMQ5XzJFe7uluUG8jc7XIA/32wl7lg1Rot3Ae9zwM=$hOhs1JszxAn81VA+oqKQy3LeljQSM9Oq/vJx1W73Ebw=")] // N not numeric
    [InlineData("$s2$16384$8$1$!!!notbase64!!!$hOhs1JszxAn81VA+oqKQy3LeljQSM9Oq/vJx1W73Ebw=")]      // malformed salt base64
    [InlineData("$s2$16384$8$1$mcvMQ5XzJFe7uluUG8jc7XIA/32wl7lg1Rot3Ae9zwM=$!!!notbase64!!!")]      // malformed key base64
    [InlineData("$s2$16384$8$1$YWFh$hOhs1JszxAn81VA+oqKQy3LeljQSM9Oq/vJx1W73Ebw=")]                  // salt decodes to 3 bytes, not 32
    [InlineData("$s2$0$8$1$mcvMQ5XzJFe7uluUG8jc7XIA/32wl7lg1Rot3Ae9zwM=$hOhs1JszxAn81VA+oqKQy3LeljQSM9Oq/vJx1W73Ebw=")]     // N < 2
    [InlineData("$s2$16383$8$1$mcvMQ5XzJFe7uluUG8jc7XIA/32wl7lg1Rot3Ae9zwM=$hOhs1JszxAn81VA+oqKQy3LeljQSM9Oq/vJx1W73Ebw=")] // N not a power of two
    public void VerifyPassword_MalformedScryptHash_FailsClosed(string malformed)
    {
        Assert.Equal(PasswordVerifyResult.Failed, _hasher.VerifyPassword("anything", malformed));
    }

    [Fact]
    public void VerifyPassword_ScryptHash_OversizedN_IsRefusedWithoutDerivingIt()
    {
        // N above Scrypt.MaxN (2^20) is an anonymous CPU/memory-exhaustion primitive exactly like the
        // unbounded ASP.NET Identity iterCount and bcrypt cost cases this file already guards against
        // — refusal must be immediate, not "eventually fails after deriving".
        var oversized =
            $"$s2${1L << 30}$8$1$mcvMQ5XzJFe7uluUG8jc7XIA/32wl7lg1Rot3Ae9zwM=$hOhs1JszxAn81VA+oqKQy3LeljQSM9Oq/vJx1W73Ebw=";

        var started = DateTimeOffset.UtcNow;
        Assert.Equal(PasswordVerifyResult.Failed, _hasher.VerifyPassword("anything", oversized));
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void VerifyPassword_ScryptHash_RTimesPAtTwoToThe30_IsRefused()
    {
        // r*p == 2^30 exactly — the RFC 7914 boundary this format's r/p fields must stay under.
        var poisoned =
            $"$s2$2${1 << 15}${1 << 15}$mcvMQ5XzJFe7uluUG8jc7XIA/32wl7lg1Rot3Ae9zwM=$hOhs1JszxAn81VA+oqKQy3LeljQSM9Oq/vJx1W73Ebw=";

        Assert.Equal(PasswordVerifyResult.Failed, _hasher.VerifyPassword("anything", poisoned));
    }

    [Fact]
    public void IsRecognisedHashFormat_ScryptIsNotClaimed()
    {
        // $s2$ is a USER password import format only — it never appears in OAuthClient
        // ClientSecretHashes, so it must stay out of the admin client API's accepted-format list
        // rather than widen what that endpoint will write.
        Assert.False(PasswordHasher.IsRecognisedHashFormat(RealScryptNetHash));
    }
}
