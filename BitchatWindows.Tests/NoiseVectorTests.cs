using Bitchat.Windows.Noise;
using Xunit;

namespace BitchatWindows.Tests;

/// <summary>
/// Official Cacophony/Noise-C vector for Noise_XX_25519_ChaChaPoly_SHA256 —
/// the same golden transcript the Android client is tested against.
/// Proves byte-level wire compatibility of the handshake and transport ciphers.
/// </summary>
public sealed class NoiseVectorTests
{
    private const string Payload1 = "4c756477696720766f6e204d69736573";
    private const string Cipher1 =
        "ca35def5ae56cec33dc2036731ab14896bc4c75dbb07a61f879f8e3afa4c7944" +
        "4c756477696720766f6e204d69736573";

    private const string Payload2 = "4d757272617920526f746862617264";
    private const string Cipher2 =
        "95ebc60d2b1fa672c1f46a8aa265ef51bfe38e7ccb39ec5be34069f144808843" +
        "81cbad1f276e038c48378ffce2b65285e08d6b68aaa3629a5a8639392490e5b9" +
        "bd5269c2f1e4f488ed8831161f19b7815528f8982ffe09be9b5c412f8a0db50f" +
        "8814c7194e83f23dbd8d162c9326ad";

    private const string Payload3 = "462e20412e20486179656b";
    private const string Cipher3 =
        "c7195ffacac1307ff99046f219750fc47693e23c3cb08b89c2af808b444850a8" +
        "0ae475b9df0f169ae80a89be0865b57f58c9fea0d4ec82a286427402f113e4b6" +
        "ae769a1d95941d49b25030";

    private const string Payload4 = "4361726c204d656e676572";
    private const string Cipher4 = "96763ed773f8e47bb3712f0e29b3060ffc956ffc146cee53d5e1df";

    private const string Payload5 = "4a65616e2d426170746973746520536179";
    private const string Cipher5 = "3e40f15f6f3a46ae446b253bf8b1d9ffb6ed9b174d272328ff91a7e2e5c79c07f5";

    private const string Payload6 = "457567656e2042f6686d20766f6e2042617765726b";
    private const string Cipher6 =
        "eb3f3515110702e047a6c9da4478b6ead94873c11c0f2d710ddb3f09fce024b3" +
        "a58502ae3f";

    private static readonly byte[] Prologue = Hex("4a6f686e2047616c74");
    private static readonly byte[] InitiatorStatic = Hex("e61ef9919cde45dd5f82166404bd08e38bceb5dfdfded0a34c8df7ed542214d1");
    private static readonly byte[] ResponderStatic = Hex("4a3acbfdb163dec651dfa3194dece676d437029c62a408b4c5ea9114246e4893");
    private static readonly byte[] InitiatorEphemeral = Hex("893e28b9dc6ca8d611ab664754b8ceb7bac5117349a4439a6b0569da977c464a");
    private static readonly byte[] ResponderEphemeral = Hex("bbdb4cdbd309f1a1f2e1456967fe288cadd6f712d65dc7b7793d5e63da6b375b");

    [Fact]
    public void XX_transcript_matches_every_handshake_and_transport_byte()
    {
        var initiator = CreateState(initiator: true);
        var responder = CreateState(initiator: false);

        // -> e
        var message1 = initiator.WriteMessage1(Hex(Payload1));
        Assert.Equal(Hex(Cipher1), message1);
        var message2 = responder.WriteMessage2(message1, Hex(Payload2));
        Assert.Equal(Hex(Cipher2), message2);

        // -> s, se
        var message3 = initiator.WriteMessage3(Hex(Cipher2), Hex(Payload3));
        Assert.Equal(Hex(Cipher3), message3);
        var payload3 = responder.ReadMessage3(message3);
        Assert.Equal(Hex(Payload3), payload3);

        // SPLIT + transport
        var (initSend, initRecv) = initiator.Split();
        var (respSend, respRecv) = responder.Split();

        Assert.Equal(Hex(Cipher4), respSend.Encrypt(0, Hex(Payload4)));
        Assert.Equal(Hex(Payload4), initRecv.Decrypt(0, Hex(Cipher4)));

        Assert.Equal(Hex(Cipher5), initSend.Encrypt(0, Hex(Payload5)));
        Assert.Equal(Hex(Payload5), respRecv.Decrypt(0, Hex(Cipher5)));

        Assert.Equal(Hex(Cipher6), respSend.Encrypt(1, Hex(Payload6)));
        Assert.Equal(Hex(Payload6), initRecv.Decrypt(1, Hex(Cipher6)));
    }

    [Fact]
    public void XX_random_handshake_roundtrips_and_transport_works()
    {
        var initiator = NoiseHandshake.CreateInitiator(X25519Key.Create());
        var responder = NoiseHandshake.CreateResponder(X25519Key.Create());

        var m1 = initiator.WriteMessage1();
        var m2 = responder.WriteMessage2(m1);
        var m3 = initiator.WriteMessage3(m2);
        var payload = responder.ReadMessage3(m3);
        Assert.Empty(payload);

        Assert.Equal(initiator.RemoteStaticKey, responder.LocalStaticKey);

        var (initSend, initRecv) = initiator.Split();
        var (respSend, respRecv) = responder.Split();

        var secret = new byte[] { 1, 2, 3, 4, 5 };
        var ct = initSend.Encrypt(42, secret);
        Assert.Equal(secret, respRecv.Decrypt(42, ct));

        var reply = respSend.Encrypt(7, new byte[] { 9, 9 });
        Assert.Equal(new byte[] { 9, 9 }, initRecv.Decrypt(7, reply));
    }

    [Fact]
    public void XX_tampered_message_is_rejected()
    {
        var initiator = CreateState(initiator: true);
        var responder = CreateState(initiator: false);

        var m1 = initiator.WriteMessage1(Hex(Payload1));
        var m2 = responder.WriteMessage2(m1, Hex(Payload2));
        var tampered = (byte[])m2.Clone();
        tampered[^1] ^= 0x01;

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
            () => initiator.WriteMessage3(tampered, Hex(Payload3)));
    }

    private static NoiseHandshake CreateState(bool initiator)
    {
        var handshake = initiator
            ? NoiseHandshake.CreateInitiator(X25519Key.ImportPrivate(InitiatorStatic), Prologue)
            : NoiseHandshake.CreateResponder(X25519Key.ImportPrivate(ResponderStatic), Prologue);
        handshake.SetEphemeralKeyForTest(initiator ? InitiatorEphemeral : ResponderEphemeral);
        return handshake;
    }

    private static byte[] Hex(string value)
    {
        var result = new byte[value.Length / 2];
        for (var i = 0; i < result.Length; i++)
            result[i] = Convert.ToByte(value.Substring(i * 2, 2), 16);
        return result;
    }
}
