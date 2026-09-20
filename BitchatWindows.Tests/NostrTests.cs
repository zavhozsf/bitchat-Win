using System.Text.Json;
using Bitchat.Windows.Nostr;
using Xunit;

namespace BitchatWindows.Tests;

/// <summary>Official BIP-340 test vectors (bitcoin/bips bip-0340/test-vectors.csv).</summary>
public sealed class NostrCryptoTests
{
    private static byte[] Hex(string v)
    {
        var r = new byte[v.Length / 2];
        for (var i = 0; i < r.Length; i++) r[i] = Convert.ToByte(v.Substring(i * 2, 2), 16);
        return r;
    }

    [Theory]
    [InlineData(
        "0000000000000000000000000000000000000000000000000000000000000003",
        "F9308A019258C31049344F85F89D5229B531C845836F99B08601F113BCE036F9",
        "0000000000000000000000000000000000000000000000000000000000000000",
        "0000000000000000000000000000000000000000000000000000000000000000",
        "E907831F80848D1069A5371B402410364BDF1C5F8307B0084C55F1CE2DCA821525F66A4A85EA8B71E482A74F382D2CE5EBEEE8FDB2172F477DF4900D310536C0")]
    [InlineData(
        "B7E151628AED2A6ABF7158809CF4F3C762E7160F38B4DA56A784D9045190CFEF",
        "DFF1D77F2A671C5F36183726DB2341BE58FEAE1DA2DECED843240F7B502BA659",
        "0000000000000000000000000000000000000000000000000000000000000001",
        "243F6A8885A308D313198A2E03707344A4093822299F31D0082EFA98EC4E6C89",
        "6896BD60EEAE296DB48A229FF71DFE071BDE413E6D43F917DC8DCF8C78DE33418906D11AC976ABCCB20B091292BFF4EA897EFCB639EA871CFA95F6DE339E4B0A")]
    [InlineData(
        "C90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B14E5C9",
        "DD308AFEC5777E13121FA72B9CC1B7CC0139715309B086C960E18FD969774EB8",
        "C87AA53824B4D7AE2EB035A2B5BBBCCC080E76CDC6D1692C4B0B62D798E6D906",
        "7E2D58D8B3BCDF1ABADEC7829054F90DDA9805AAB56C77333024B9D0A508B75C",
        "5831AAEED7B44BB74E5EAB94BA9D4294C49BCF2A60728D8B4C200F50DD313C1BAB745879A5AD954A72C45A91C3A51D3C7ADEA98D82F8481E0E1E03674A6F3FB7")]
    public void Bip340_sign_matches_vectors(string secretHex, string expectedPubHex, string auxHex, string msgHex, string expectedSigHex)
    {
        var priv = Hex(secretHex);
        var pub = NostrCrypto.PublicKeyFromPrivate(priv);
        Assert.Equal(expectedPubHex.ToLowerInvariant(), Convert.ToHexString(pub).ToLowerInvariant());

        var sig = NostrCrypto.Sign(Hex(msgHex), priv, Hex(auxHex));
        Assert.Equal(expectedSigHex.ToLowerInvariant(), Convert.ToHexString(sig).ToLowerInvariant());

        Assert.True(NostrCrypto.Verify(Hex(msgHex), pub, sig));
    }

    [Fact]
    public void Bip340_verify_rejects_tampered()
    {
        var priv = Hex("B7E151628AED2A6ABF7158809CF4F3C762E7160F38B4DA56A784D9045190CFEF");
        var pub = NostrCrypto.PublicKeyFromPrivate(priv);
        var msg = Hex("243F6A8885A308D313198A2E03707344A4093822299F31D0082EFA98EC4E6C89");
        var aux = Hex("0000000000000000000000000000000000000000000000000000000000000001");
        var sig = NostrCrypto.Sign(msg, priv, aux);

        var tamperedMsg = (byte[])msg.Clone();
        tamperedMsg[0] ^= 0x01;
        Assert.False(NostrCrypto.Verify(tamperedMsg, pub, sig));

        var tamperedSig = (byte[])sig.Clone();
        tamperedSig[63] ^= 0x01;
        Assert.False(NostrCrypto.Verify(msg, pub, tamperedSig));
    }

    [Fact]
    public void Event_serialization_is_compact_and_stable()
    {
        var ev = new NostrEvent
        {
            Pubkey = new string('a', 64),
            CreatedAt = 1700000000,
            Kind = 20000,
            Tags = new[] { new[] { "g", "dr5rs" }, new[] { "n", "anon9010" } },
            Content = "привет \"мир\"\n"
        };
        Assert.Equal(
            "[0,\"aaaa…aaa\",1700000000,20000,[[\"g\",\"dr5rs\"],[\"n\",\"anon9010\"]],\"привет \\\"мир\\\"\\n\"]"
                .Replace("aaaa…aaa", new string('a', 64)),
            ev.SerializeForId());

        var id = ev.ComputeId();
        Assert.Equal(64, id.Length);
        Assert.Equal(id, ev.ComputeId()); // stable
    }

    [Fact]
    public void Event_sign_then_verify_and_parse_roundtrip()
    {
        var priv = new byte[32];
        Random.Shared.NextBytes(priv);
        while (!NostrCrypto.ValidPrivateKey(priv))
        {
            Random.Shared.NextBytes(priv);
        }

        var ev = new NostrEvent
        {
            Pubkey = Convert.ToHexString(NostrCrypto.PublicKeyFromPrivate(priv)).ToLowerInvariant(),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Kind = 20000,
            Tags = new[] { new[] { "g", "dr5rs" }, new[] { "n", "tester" } },
            Content = "hello geohash"
        };
        ev.Sign(priv);

        Assert.True(ev.Verify());

        // Parse from relay JSON and verify again
        var relayJson = $"[\"EVENT\",\"sub\",{ev.ToRelayJson()}]";
        using var doc = JsonDocument.Parse(relayJson);
        var parsed = NostrEvent.FromJson(doc.RootElement[2]);
        Assert.NotNull(parsed);
        Assert.Equal(ev.Id, parsed!.Id);
        Assert.True(parsed.Verify());

        // Tampered content must fail
        parsed.Content = "tampered";
        Assert.False(parsed.Verify());
    }

    [Fact]
    public void Geohash_identity_is_stable_and_distinct_per_geohash()
    {
        var a1 = NostrIdentity.DeriveForGeohash("dr5rs");
        var a2 = NostrIdentity.DeriveForGeohash("dr5rs");
        var b = NostrIdentity.DeriveForGeohash("u09tu");

        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, b);
        Assert.Equal(32, a1.Length);
        Assert.True(NostrCrypto.ValidPrivateKey(a1));
    }
}
