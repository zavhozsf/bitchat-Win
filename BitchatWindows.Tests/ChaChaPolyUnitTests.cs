using System.Security.Cryptography;
using Bitchat.Windows.Noise;
using Xunit;

namespace BitchatWindows.Tests;

public sealed class ChaChaPolyUnitTests
{
    [Fact]
    public void Poly1305_rfc8439_vector()
    {
        var key = Hex("85d6be7857556d337f4452fe42d506a80103808afb0db2fd4abff6af4149f51b");
        var message = System.Text.Encoding.ASCII.GetBytes("Cryptographic Forum Research Group");
        var expected = Hex("a8061dc1305136c6c22b8baf0c0127a9");
        var actual = NoiseChaCha20Poly1305.Poly1305(key, message);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Block_matches_golden_transcript_layout()
    {
        // The noise-c layout cannot be validated with RFC 7539 vectors (different word
        // placement), so validate against the byte extracted from the Cacophony golden
        // transcript: m3's static is encrypted with kES at n=1 (data block counter=1).
        var key = Hex("d9ba856addc3831028dcf7d2724fec42ea939e2134518573e982b8f1b2ddd59b");
        Span<byte> block = stackalloc byte[64];
        NoiseChaCha20Poly1305.Block(key, n: 1, counter: 1, block);
        var expected = Hex("acdaddd0e066c499618d23ca705e331b48fe1bd29a5dad1776b21349631fe7f2");
        Assert.Equal(expected, block.Slice(0, 32).ToArray());
    }

    [Fact]
    public void Aead_roundtrip_and_tamper()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var plaintext = "hello noise transport"u8.ToArray();
        var ad = new byte[] { 1, 2, 3 };

        var (ct, tag) = NoiseChaCha20Poly1305.Encrypt(key, 42, ad, plaintext);
        var ctWithTag = ct.Concat(tag).ToArray();

        var pt = NoiseChaCha20Poly1305.Decrypt(key, 42, ad, ctWithTag);
        Assert.Equal(plaintext, pt);

        // wrong nonce
        Assert.ThrowsAny<CryptographicException>(() => NoiseChaCha20Poly1305.Decrypt(key, 43, ad, ctWithTag));
        // wrong ad
        Assert.ThrowsAny<CryptographicException>(() => NoiseChaCha20Poly1305.Decrypt(key, 42, new byte[] { 9 }, ctWithTag));
        // flipped bit
        ctWithTag[0] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => NoiseChaCha20Poly1305.Decrypt(key, 42, ad, ctWithTag));
    }

    private static byte[] Hex(string v)
    {
        var r = new byte[v.Length / 2];
        for (var i = 0; i < r.Length; i++) r[i] = Convert.ToByte(v.Substring(i * 2, 2), 16);
        return r;
    }
}
