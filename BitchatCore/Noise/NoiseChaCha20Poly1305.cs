using System.IO;

using System.Numerics;
using System.Security.Cryptography;

namespace Bitchat.Windows.Noise;

/// <summary>
/// ChaCha20-Poly1305 with the noise-c/noise-java state layout that bitchat uses
/// (verified against the official Cacophony vectors):
///   words 12-13 = 64-bit block counter (0 = Poly1305 key block, then 1, 2, ...),
///   words 14-15 = 64-bit nonce (little-endian words of the big-endian nonce value).
/// This differs from the RFC 8439 IETF layout, so System.Security.Cryptography.ChaCha20Poly1305
/// cannot be used here.
/// </summary>
public static class NoiseChaCha20Poly1305
{
    private const int TagSize = 16;

    public static int MacLength => TagSize;

    public static byte[] MakeNonce(ulong n)
    {
        // Kept for API symmetry; the nonce is passed to the block function directly.
        var nonce = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(nonce, n);
        return nonce;
    }

    public static (byte[] Ciphertext, byte[] Tag) Encrypt(byte[] key, ulong n, byte[]? associatedData, byte[] plaintext)
    {
        Span<byte> block = stackalloc byte[64];

        // Poly1305 key from block counter 0
        Block(key, n, 0, block);
        var macKey = block.Slice(0, 32).ToArray();

        // Encrypt with keystream blocks starting at counter 1
        var ciphertext = new byte[plaintext.Length];
        var offset = 0;
        ulong counter = 1;
        while (offset < plaintext.Length)
        {
            Block(key, n, counter, block);
            var len = Math.Min(64, plaintext.Length - offset);
            for (var i = 0; i < len; i++)
                ciphertext[offset + i] = (byte)(plaintext[offset + i] ^ block[i]);
            offset += len;
            counter++;
        }

        var tag = Mac(macKey, associatedData ?? Array.Empty<byte>(), ciphertext);
        return (ciphertext, tag);
    }

    public static byte[] Decrypt(byte[] key, ulong n, byte[]? associatedData, byte[] ciphertextWithTag)
    {
        if (ciphertextWithTag.Length < TagSize)
            throw new CryptographicException("ciphertext too short");

        var ctLength = ciphertextWithTag.Length - TagSize;
        var ciphertext = ciphertextWithTag.AsSpan(0, ctLength).ToArray();
        var tag = ciphertextWithTag[^TagSize..];

        Span<byte> block = stackalloc byte[64];
        Block(key, n, 0, block);
        var macKey = block.Slice(0, 32).ToArray();

        var expected = Mac(macKey, associatedData ?? Array.Empty<byte>(), ciphertext);
        if (!CryptographicOperations.FixedTimeEquals(expected, tag))
            throw new CryptographicException("authentication tag mismatch");

        var plaintext = new byte[ctLength];
        var offset = 0;
        ulong counter = 1;
        while (offset < ctLength)
        {
            Block(key, n, counter, block);
            var len = Math.Min(64, ctLength - offset);
            for (var i = 0; i < len; i++)
                plaintext[offset + i] = (byte)(ciphertext[offset + i] ^ block[i]);
            offset += len;
            counter++;
        }
        return plaintext;
    }

    /// <summary>Raw keystream block (noise-c layout).</summary>
    public static void Block(byte[] key, ulong n, ulong counter, Span<byte> output64)
    {
        Span<uint> st = stackalloc uint[16];
        var constants = "expand 32-byte k"u8;
        for (var i = 0; i < 4; i++)
            st[i] = ReadLe(constants.Slice(i * 4, 4));
        for (var i = 0; i < 8; i++)
            st[4 + i] = ReadLe(key.AsSpan(i * 4, 4));
        st[12] = (uint)counter;
        st[13] = (uint)(counter >> 32);
        st[14] = (uint)n;
        st[15] = (uint)(n >> 32);

        Span<uint> x = stackalloc uint[16];
        st.CopyTo(x);
        for (var i = 0; i < 10; i++)
        {
            QuarterRound(x, 0, 4, 8, 12);
            QuarterRound(x, 1, 5, 9, 13);
            QuarterRound(x, 2, 6, 10, 14);
            QuarterRound(x, 3, 7, 11, 15);
            QuarterRound(x, 0, 5, 10, 15);
            QuarterRound(x, 1, 6, 11, 12);
            QuarterRound(x, 2, 7, 8, 13);
            QuarterRound(x, 3, 4, 9, 14);
        }
        for (var i = 0; i < 16; i++)
            WriteLe(output64.Slice(i * 4, 4), x[i] + st[i]);
    }

    private static void QuarterRound(Span<uint> x, int a, int b, int c, int d)
    {
        x[a] += x[b]; x[d] = Rotl(x[d] ^ x[a], 16);
        x[c] += x[d]; x[b] = Rotl(x[b] ^ x[c], 12);
        x[a] += x[b]; x[d] = Rotl(x[d] ^ x[a], 8);
        x[c] += x[d]; x[b] = Rotl(x[b] ^ x[c], 7);
    }

    private static uint Rotl(uint v, int c) => (v << c) | (v >> (32 - c));

    private static uint ReadLe(ReadOnlySpan<byte> src) =>
        src[0] | ((uint)src[1] << 8) | ((uint)src[2] << 16) | ((uint)src[3] << 24);

    private static void WriteLe(Span<byte> dst, uint v)
    {
        dst[0] = (byte)v;
        dst[1] = (byte)(v >> 8);
        dst[2] = (byte)(v >> 16);
        dst[3] = (byte)(v >> 24);
    }

    // ---- Poly1305 (RFC 8439 MAC; identical for all ChaChaPoly variants) ----

    private static byte[] Mac(byte[] macKey, byte[] ad, byte[] ciphertext)
    {
        var macData = BuildMacInput(ad, ciphertext);
        return Poly1305(macKey, macData);
    }

    private static byte[] BuildMacInput(byte[] ad, byte[] ciphertext)
    {
        var adPad = (16 - ad.Length % 16) % 16;
        var ctPad = (16 - ciphertext.Length % 16) % 16;
        var total = ad.Length + adPad + ciphertext.Length + ctPad + 16;
        var result = new byte[total];
        var o = 0;
        Array.Copy(ad, 0, result, o, ad.Length); o += ad.Length + adPad;
        Array.Copy(ciphertext, 0, result, o, ciphertext.Length); o += ciphertext.Length + ctPad;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(o, 8), (ulong)ad.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(o + 8, 8), (ulong)ciphertext.Length);
        return result;
    }

    internal static byte[] Poly1305(byte[] key, byte[] message)
    {
        var rBytes = new byte[16];
        Array.Copy(key, 0, rBytes, 0, 16);
        rBytes[3] &= 15; rBytes[7] &= 15; rBytes[11] &= 15; rBytes[15] &= 15;
        rBytes[4] &= 252; rBytes[8] &= 252; rBytes[12] &= 252;
        var r = new BigInteger(rBytes);
        var s = new BigInteger(key[16..32].Concat(new byte[] { 0 }).ToArray()); // unsigned
        var p = (BigInteger.One << 130) - 5;

        BigInteger acc = 0;
        var offset = 0;
        while (offset < message.Length)
        {
            var len = Math.Min(16, message.Length - offset);
            var block = new byte[len + 1];
            Array.Copy(message, offset, block, 0, len);
            block[len] = 1;
            acc = (acc + new BigInteger(block)) * r % p;
            offset += len;
        }

        acc += s;
        var tag = new byte[16];
        var bytes = acc.ToByteArray(); // little-endian two's complement
        Array.Copy(bytes, 0, tag, 0, Math.Min(16, bytes.Length));
        return tag;
    }
}
