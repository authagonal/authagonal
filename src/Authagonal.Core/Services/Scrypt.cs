using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Authagonal.Core.Services;

/// <summary>
/// The RFC 7914 scrypt key derivation function: Salsa20/8 core, BlockMix, ROMix, and an outer
/// PBKDF2-HMAC-SHA256. <c>System.Security.Cryptography</c> ships PBKDF2 and HKDF but no scrypt on
/// either net9.0 or net10.0, so this is a from-scratch implementation.
/// </summary>
/// <remarks>
/// It exists solely so <c>PasswordHasher</c> can verify legacy Scrypt.NET (<c>$s2$</c>) password
/// hashes on a user's first login after import; nothing in this codebase uses it to mint new
/// hashes — new hashes are always native PBKDF2v2.
/// </remarks>
public static class Scrypt
{
    /// <summary>
    /// Highest cost parameter N this implementation will run: 2^20. The same defensive posture as
    /// every other attacker-influenced cost value <c>PasswordHasher</c> pulls out of a stored hash
    /// (see its <c>MaxImportedIterations</c>, or bcrypt's <c>MaxCost</c>) — a crafted hash must not
    /// be able to turn an anonymous login attempt into an unbounded CPU/memory burn.
    /// </summary>
    public const int MaxN = 1 << 20;

    /// <summary>
    /// True when (N, r, p) are safe to run: N is a power of two in [2, <see cref="MaxN"/>], r and p
    /// are positive, and r*p is less than 2^30 (the bound RFC 7914 Section 4 places on scrypt's
    /// parameters, since PBKDF2's block counter is a 32-bit big-endian integer).
    /// </summary>
    public static bool AreParametersValid(int n, int r, int p) =>
        n >= 2 && (n & (n - 1)) == 0 && n <= MaxN
        && r > 0 && p > 0
        && (long)r * p < (1L << 30);

    /// <summary>
    /// Derives a <paramref name="dkLen"/>-byte key from <paramref name="password"/> and
    /// <paramref name="salt"/> using scrypt(N, r, p).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// (N, r, p) fail <see cref="AreParametersValid"/>, or <paramref name="dkLen"/> is not positive.
    /// </exception>
    public static byte[] DeriveKey(byte[] password, byte[] salt, int n, int r, int p, int dkLen)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dkLen);
        if (!AreParametersValid(n, r, p))
            throw new ArgumentOutOfRangeException(nameof(n),
                $"Invalid scrypt parameters N={n}, r={r}, p={p}: N must be a power of two in " +
                $"[2, {MaxN}], r and p must be positive, and r*p must be less than 2^30.");

        var blockWords = checked(32 * r); // one scrypt "block" is 2r Salsa20 blocks = 128r bytes = 32r words.
        var blockBytes = checked(blockWords * 4);

        // B = PBKDF2-HMAC-SHA256(P, S, 1, p * 128r) — p independent blocks, ROMix'd separately below.
        var b = Rfc2898DeriveBytes.Pbkdf2(password, salt, 1, HashAlgorithmName.SHA256, checked(p * blockBytes));

        // Scratch reused across every BlockMix/ROMix step below, so DeriveKey allocates once per
        // call rather than once per (of up to 2*N*p) inner iteration.
        var block = new uint[blockWords];
        var v = new uint[checked(n * blockWords)];
        var x = new uint[blockWords];
        var y = new uint[blockWords];
        var t = new uint[16];

        for (var i = 0; i < p; i++)
        {
            var slice = b.AsSpan(i * blockBytes, blockBytes);
            BytesToWords(slice, block);
            RoMix(block, v, x, y, t, n);
            WordsToBytes(block, slice);
        }

        return Rfc2898DeriveBytes.Pbkdf2(password, b, 1, HashAlgorithmName.SHA256, dkLen);
    }

    // ROMix_{Salsa20/8, N}: fills V with N successive states of the block, then randomly walks it
    // back down. x/y/t are caller-owned scratch (see DeriveKey).
    private static void RoMix(uint[] block, uint[] v, uint[] x, uint[] y, uint[] t, int n)
    {
        var blockWords = block.Length;
        Array.Copy(block, x, blockWords);

        for (var i = 0; i < n; i++)
        {
            Array.Copy(x, 0, v, i * blockWords, blockWords);
            BlockMix(x, y, t);
        }

        for (var i = 0; i < n; i++)
        {
            // Integerify(X) mod N: N is a power of two, and the first word of X's last 64-byte
            // sub-block already holds the low 32 bits of that little-endian integer, so a mask on
            // just that word suffices.
            var j = x[blockWords - 16] & (uint)(n - 1);
            var vOffset = (int)j * blockWords;
            for (var k = 0; k < blockWords; k++)
                x[k] ^= v[vOffset + k];
            BlockMix(x, y, t);
        }

        Array.Copy(x, block, blockWords);
    }

    // BlockMix_{Salsa20/8, r}: mixes a 32r-word block in place, using y/t as scratch.
    private static void BlockMix(uint[] b, uint[] y, uint[] t)
    {
        var twoR = b.Length / 16;
        Array.Copy(b, b.Length - 16, t, 0, 16); // X = B[2r - 1]

        for (var i = 0; i < twoR; i++)
        {
            var off = i * 16;
            for (var k = 0; k < 16; k++)
                t[k] ^= b[off + k];
            Salsa8(t);
            Array.Copy(t, 0, y, off, 16);
        }

        // Even-indexed 64-byte blocks go to the first half of the output, odd-indexed to the second.
        var half = twoR / 2 * 16;
        for (var i = 0; i < twoR / 2; i++)
        {
            Array.Copy(y, 2 * i * 16, b, i * 16, 16);
            Array.Copy(y, (2 * i + 1) * 16, b, half + i * 16, 16);
        }
    }

    // The Salsa20/8 core: 4 double-rounds (8 rounds) over 16 little-endian uint32 words, in place.
    private static void Salsa8(uint[] block)
    {
        Span<uint> x = stackalloc uint[16];
        block.AsSpan().CopyTo(x);

        for (var i = 0; i < 8; i += 2)
        {
            x[4] ^= Rotl(x[0] + x[12], 7); x[8] ^= Rotl(x[4] + x[0], 9);
            x[12] ^= Rotl(x[8] + x[4], 13); x[0] ^= Rotl(x[12] + x[8], 18);
            x[9] ^= Rotl(x[5] + x[1], 7); x[13] ^= Rotl(x[9] + x[5], 9);
            x[1] ^= Rotl(x[13] + x[9], 13); x[5] ^= Rotl(x[1] + x[13], 18);
            x[14] ^= Rotl(x[10] + x[6], 7); x[2] ^= Rotl(x[14] + x[10], 9);
            x[6] ^= Rotl(x[2] + x[14], 13); x[10] ^= Rotl(x[6] + x[2], 18);
            x[3] ^= Rotl(x[15] + x[11], 7); x[7] ^= Rotl(x[3] + x[15], 9);
            x[11] ^= Rotl(x[7] + x[3], 13); x[15] ^= Rotl(x[11] + x[7], 18);

            x[1] ^= Rotl(x[0] + x[3], 7); x[2] ^= Rotl(x[1] + x[0], 9);
            x[3] ^= Rotl(x[2] + x[1], 13); x[0] ^= Rotl(x[3] + x[2], 18);
            x[6] ^= Rotl(x[5] + x[4], 7); x[7] ^= Rotl(x[6] + x[5], 9);
            x[4] ^= Rotl(x[7] + x[6], 13); x[5] ^= Rotl(x[4] + x[7], 18);
            x[11] ^= Rotl(x[10] + x[9], 7); x[8] ^= Rotl(x[11] + x[10], 9);
            x[9] ^= Rotl(x[8] + x[11], 13); x[10] ^= Rotl(x[9] + x[8], 18);
            x[12] ^= Rotl(x[15] + x[14], 7); x[13] ^= Rotl(x[12] + x[15], 9);
            x[14] ^= Rotl(x[13] + x[12], 13); x[15] ^= Rotl(x[14] + x[13], 18);
        }

        for (var i = 0; i < 16; i++)
            block[i] += x[i];
    }

    private static uint Rotl(uint v, int c) => (v << c) | (v >> (32 - c));

    private static void BytesToWords(ReadOnlySpan<byte> bytes, uint[] words)
    {
        for (var i = 0; i < words.Length; i++)
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(i * 4, 4));
    }

    private static void WordsToBytes(uint[] words, Span<byte> bytes)
    {
        for (var i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(i * 4, 4), words[i]);
    }
}
