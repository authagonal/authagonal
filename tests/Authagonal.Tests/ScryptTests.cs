using System.Text;
using Authagonal.Core.Services;

namespace Authagonal.Tests;

/// <summary>
/// The RFC 7914 scrypt implementation in <c>Authagonal.Core.Services.Scrypt</c> — added to verify
/// legacy Scrypt.NET (<c>$s2$</c>) password hashes on first login (see
/// <c>PasswordHasherScryptTests</c> for that integration).
/// </summary>
public class ScryptTests
{
    // -----------------------------------------------------------------------
    // RFC 7914 Section 12 test vectors
    // -----------------------------------------------------------------------

    [Fact]
    public void Rfc7914Vector1_EmptyPasswordAndSalt()
    {
        var dk = Scrypt.DeriveKey([], [], n: 16, r: 1, p: 1, dkLen: 64);

        Assert.Equal(
            "77d6576238657b203b19ca42c18a0497f16b4844e3074ae8dfdffa3fede21442" +
            "fcd0069ded0948f8326a753a0fc81f17e8d3e0fb2e0d3628cf35e20c38d18906",
            Convert.ToHexString(dk).ToLowerInvariant());
    }

    [Fact]
    public void Rfc7914Vector2_PasswordAndNaClSalt()
    {
        var dk = Scrypt.DeriveKey(
            Encoding.ASCII.GetBytes("password"), Encoding.ASCII.GetBytes("NaCl"),
            n: 1024, r: 8, p: 16, dkLen: 64);

        Assert.Equal(
            "fdbabe1c9d3472007856e7190d01e9fe7c6ad7cbc8237830e77376634b3731622" +
            "eaf30d92e22a3886ff109279d9830dac727afb94a83ee6d8360cbdfa2cc0640",
            Convert.ToHexString(dk).ToLowerInvariant());
    }

    [Fact]
    public void Rfc7914Vector3_SameCostAsScryptNetDefaults()
    {
        // N=16384, r=8, p=1 is exactly what Scrypt.NET's ScryptEncoder uses by default — the
        // parameters PasswordHasher.VerifyScrypt actually re-derives at in production.
        var dk = Scrypt.DeriveKey(
            Encoding.ASCII.GetBytes("pleaseletmein"), Encoding.ASCII.GetBytes("SodiumChloride"),
            n: 16384, r: 8, p: 1, dkLen: 64);

        Assert.Equal(
            "7023bdcb3afd7348461c06cd81fd38ebfda8fbba904f8e3ea9b543f6545da1f2" +
            "d5432955613f0fcf62d49705242a9af9e61e85dc0d651e40dfcf017b45575887",
            Convert.ToHexString(dk).ToLowerInvariant());
    }

    [Fact]
    public void Rfc7914Vector3_First32Bytes_MatchTheDkLen32DerivationUsedInProduction()
    {
        // PBKDF2's output blocks are independent of the requested length when the iteration count
        // is 1 (as scrypt's outer PBKDF2 always is), so a 32-byte derivation for the same P/S/N/r/p
        // must equal the first 32 bytes of the 64-byte vector above. This is what
        // PasswordHasher.VerifyScrypt actually calls (dkLen=32, matching Scrypt.NET's fixed 32-byte
        // salt/key).
        var dk32 = Scrypt.DeriveKey(
            Encoding.ASCII.GetBytes("pleaseletmein"), Encoding.ASCII.GetBytes("SodiumChloride"),
            n: 16384, r: 8, p: 1, dkLen: 32);
        var dk64 = Scrypt.DeriveKey(
            Encoding.ASCII.GetBytes("pleaseletmein"), Encoding.ASCII.GetBytes("SodiumChloride"),
            n: 16384, r: 8, p: 1, dkLen: 64);

        Assert.Equal(dk64.AsSpan(0, 32).ToArray(), dk32);
    }

    [Fact]
    public void Rfc7914Vector4_MaximumCost_AtTheMaxNBoundary()
    {
        // N=2^20 is exactly Scrypt.MaxN — this doubles as the boundary-acceptance case for
        // AreParametersValid. ~1 GiB and a few seconds; not cheap, but well within a normal test run.
        var dk = Scrypt.DeriveKey(
            Encoding.ASCII.GetBytes("pleaseletmein"), Encoding.ASCII.GetBytes("SodiumChloride"),
            n: 1_048_576, r: 8, p: 1, dkLen: 64);

        Assert.Equal(
            "2101cb9b6a511aaeaddbbe09cf70f881ec568d574a2ffd4dabe5ee9820adaa4" +
            "78e56fd8f4ba5d09ffa1c6d927c40f4c337304049e8a952fbcbf45c6fa77a41a4",
            Convert.ToHexString(dk).ToLowerInvariant());
    }

    // -----------------------------------------------------------------------
    // Parameter validation — the same defensive bound every other imported cost
    // parameter in PasswordHasher gets.
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(0, 8, 1)]      // N < 2
    [InlineData(1, 8, 1)]      // N < 2
    [InlineData(15, 8, 1)]     // not a power of two
    [InlineData(16384, 0, 1)]  // r <= 0
    [InlineData(16384, 8, 0)]  // p <= 0
    public void AreParametersValid_RejectsOutOfBoundValues(int n, int r, int p)
    {
        Assert.False(Scrypt.AreParametersValid(n, r, p));
    }

    [Fact]
    public void AreParametersValid_RejectsNAboveMax()
    {
        Assert.False(Scrypt.AreParametersValid(Scrypt.MaxN * 2, 8, 1));
    }

    [Fact]
    public void AreParametersValid_AcceptsNAtMax()
    {
        Assert.True(Scrypt.AreParametersValid(Scrypt.MaxN, 1, 1));
    }

    [Fact]
    public void AreParametersValid_RejectsRTimesPAtOrAboveTwoToThe30()
    {
        Assert.False(Scrypt.AreParametersValid(2, 1 << 15, 1 << 15)); // r*p == 2^30
        Assert.True(Scrypt.AreParametersValid(2, 1 << 15, (1 << 15) - 1)); // just under
    }

    [Fact]
    public void AreParametersValid_AcceptsScryptNetDefaults()
    {
        Assert.True(Scrypt.AreParametersValid(16384, 8, 1));
    }

    [Fact]
    public void DeriveKey_ThrowsForInvalidParameters()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Scrypt.DeriveKey([1], [1], n: 15, r: 8, p: 1, dkLen: 32));
    }

    [Fact]
    public void DeriveKey_ThrowsForNonPositiveDkLen()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Scrypt.DeriveKey([1], [1], n: 16384, r: 8, p: 1, dkLen: 0));
    }

    [Fact]
    public void DeriveKey_IsDeterministic()
    {
        var a = Scrypt.DeriveKey("x"u8.ToArray(), "y"u8.ToArray(), n: 16, r: 1, p: 1, dkLen: 32);
        var b = Scrypt.DeriveKey("x"u8.ToArray(), "y"u8.ToArray(), n: 16, r: 1, p: 1, dkLen: 32);
        Assert.Equal(a, b);
    }

    [Fact]
    public void DeriveKey_DifferentSalt_ProducesDifferentKey()
    {
        var a = Scrypt.DeriveKey("x"u8.ToArray(), "y"u8.ToArray(), n: 16, r: 1, p: 1, dkLen: 32);
        var b = Scrypt.DeriveKey("x"u8.ToArray(), "z"u8.ToArray(), n: 16, r: 1, p: 1, dkLen: 32);
        Assert.NotEqual(a, b);
    }
}
