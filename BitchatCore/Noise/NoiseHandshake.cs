using System.IO;

using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math.EC.Rfc7748;

namespace Bitchat.Windows.Noise;

/// <summary>X25519 key wrapper over BouncyCastle.</summary>
public sealed class X25519Key
{
    private readonly X25519PrivateKeyParameters? _privateKey;
    private readonly X25519PublicKeyParameters? _publicKey;

    private X25519Key(X25519PrivateKeyParameters privateKey)
    {
        _privateKey = privateKey;
        _publicKey = privateKey.GeneratePublicKey();
    }

    private X25519Key(X25519PublicKeyParameters publicKey)
    {
        _publicKey = publicKey;
    }

    public static X25519Key Create()
    {
        var generator = new Org.BouncyCastle.Crypto.Generators.X25519KeyPairGenerator();
        generator.Init(new Org.BouncyCastle.Crypto.KeyGenerationParameters(new Org.BouncyCastle.Security.SecureRandom(), 255));
        var pair = generator.GenerateKeyPair();
        return new X25519Key((X25519PrivateKeyParameters)pair.Private);
    }

    public static X25519Key ImportPrivate(byte[] privateKey)
    {
        if (privateKey.Length != 32) throw new ArgumentException("X25519 private key must be 32 bytes");
        return new X25519Key(new X25519PrivateKeyParameters(privateKey, 0));
    }

    public static X25519Key ImportPublic(byte[] publicKey)
    {
        if (publicKey.Length != 32) throw new ArgumentException("X25519 public key must be 32 bytes");
        return new X25519Key(new X25519PublicKeyParameters(publicKey, 0));
    }

    public byte[] ExportPublic() => _publicKey!.GetEncoded();

    /// <summary>Raw X25519 shared secret (this private key x other public key).</summary>
    public byte[] Dh(X25519Key other)
    {
        var secret = new byte[32];
        X25519.ScalarMult(_privateKey!.GetEncoded(), 0, other._publicKey!.GetEncoded(), 0, secret, 0);
        return secret;
    }
}

/// <summary>
/// Minimal Noise Protocol Framework implementation of the XX pattern with the exact
/// cipher suite used by bitchat: Noise_XX_25519_ChaChaPoly_SHA256.
/// </summary>
public sealed class NoiseHandshake
{
    public const string ProtocolName = "Noise_XX_25519_ChaChaPoly_SHA256";

    private const int KeySize = 32;
    private const int TagSize = 16;

    private byte[] _ck = new byte[KeySize];
    private byte[] _h = new byte[KeySize];

    private byte[]? _k;
    private ulong _n;

    private X25519Key? _localEphemeral;
    private readonly X25519Key _localStatic;
    private X25519Key? _remoteEphemeral;
    private X25519Key? _remoteStatic;

    public byte[]? RemoteStaticKey => _remoteStatic?.ExportPublic();
    public byte[] LocalStaticKey => _localStatic.ExportPublic();
    public bool IsComplete { get; private set; }

    public enum Role { Initiator, Responder }

    private readonly Role _role;
    private int _step;

    private NoiseHandshake(Role role, X25519Key localStatic, byte[]? prologue)
    {
        _role = role;
        _localStatic = localStatic;

        var name = Encoding.ASCII.GetBytes(ProtocolName);
        if (name.Length == KeySize) _h = name;
        else _h = SHA256.HashData(name);
        Array.Copy(_h, _ck, KeySize);
        // The Noise spec (and noise-java, which bitchat uses) always mixes the prologue,
        // even when it is empty: h = SHA256(h || b"").
        MixHash(prologue ?? Array.Empty<byte>());
    }

    public static NoiseHandshake CreateInitiator(X25519Key localStatic, byte[]? prologue = null) =>
        new(Role.Initiator, localStatic, prologue);

    public static NoiseHandshake CreateResponder(X25519Key localStatic, byte[]? prologue = null) =>
        new(Role.Responder, localStatic, prologue);

    /// <summary>Test hook: install a deterministic ephemeral key before the handshake starts.</summary>
    internal void SetEphemeralKeyForTest(byte[] privateKey32) =>
        _localEphemeral = X25519Key.ImportPrivate(privateKey32);

    // ---- Noise core helpers ----

    private static (byte[] Ck, byte[] K) Kdf(byte[] ck, byte[] ikm)
    {
        using var hmac = new HMACSHA256(ck);
        var temp = hmac.ComputeHash(ikm);

        using var hmac1 = new HMACSHA256(temp);
        var o1 = hmac1.ComputeHash(new[] { (byte)0x01 });

        using var hmac2 = new HMACSHA256(temp);
        var o2Input = new byte[o1.Length + 1];
        Array.Copy(o1, o2Input, o1.Length);
        o2Input[^1] = 0x02;
        var o2 = hmac2.ComputeHash(o2Input);

        return (o1, o2);
    }

    private void MixHash(ReadOnlySpan<byte> data)
    {
        var input = new byte[_h.Length + data.Length];
        Array.Copy(_h, input, _h.Length);
        data.CopyTo(input.AsSpan(_h.Length));
        _h = SHA256.HashData(input);
    }

    private void MixKey(ReadOnlySpan<byte> ikm)
    {
        var (ck, k) = Kdf(_ck, ikm.ToArray());
        _ck = ck;
        _k = k;
        _n = 0;
    }

    private byte[] EncryptAndHash(byte[] plaintext)
    {
        byte[] ciphertext;
        if (_k != null)
        {
            ciphertext = Encrypt(_k, _n++, _h, plaintext);
        }
        else
        {
            ciphertext = plaintext;
        }
        MixHash(ciphertext);
        return ciphertext;
    }

    private byte[] DecryptAndHash(byte[] ciphertext)
    {
        byte[] plaintext;
        if (_k != null)
        {
            plaintext = Decrypt(_k, _n++, _h, ciphertext);
        }
        else
        {
            plaintext = ciphertext;
        }
        MixHash(ciphertext);
        return plaintext;
    }

    internal static byte[] MakeNonce(ulong n)
    {
        // Noise: 4 zero bytes + 8-byte big-endian counter = 12-byte IETF ChaCha20 nonce
        var nonce = new byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4, 8), n);
        return nonce;
    }

    private static byte[] Encrypt(byte[] key, ulong n, byte[] ad, byte[] plaintext)
    {
        var (ciphertext, tag) = NoiseChaCha20Poly1305.Encrypt(key, n, ad, plaintext);
        var result = new byte[plaintext.Length + TagSize];
        Array.Copy(ciphertext, result, ciphertext.Length);
        Array.Copy(tag, 0, result, plaintext.Length, TagSize);
        return result;
    }

    private static byte[] Decrypt(byte[] key, ulong n, byte[] ad, byte[] ciphertext)
    {
        return NoiseChaCha20Poly1305.Decrypt(key, n, ad, ciphertext);
    }

    // ---- Handshake messages ----

    /// <summary>Message 1 (initiator): "-> e" + encrypted payload (empty payload for bitchat).</summary>
    public byte[] WriteMessage1(byte[]? payload = null)
    {
        if (_role != Role.Initiator || _step != 0)
            throw new InvalidOperationException("Invalid handshake state for message 1");
        _localEphemeral ??= X25519Key.Create();
        var e = _localEphemeral.ExportPublic();
        MixHash(e);
        var message = new List<byte>(e);
        message.AddRange(EncryptAndHash(payload ?? Array.Empty<byte>()));
        _step = 1;
        return message.ToArray();
    }

    /// <summary>Message 2 (responder): "&lt;- e, ee, s, es" (96 bytes with empty payload).</summary>
    public byte[] WriteMessage2(byte[] message1, byte[]? payload = null)
    {
        if (_role != Role.Responder || _step != 0)
            throw new InvalidOperationException("Invalid handshake state for message 2");
        if (message1.Length < KeySize)
            throw new ArgumentException("Invalid XX message 1 size");

        // 1. Read the remote ephemeral key.
        _remoteEphemeral = X25519Key.ImportPublic(message1.AsSpan(0, KeySize).ToArray());
        MixHash(message1.AsSpan(0, KeySize).ToArray());

        // 2. The remote payload arrives before our own tokens (k is still unset here).
        var receivedPayload = DecryptAndHash(message1[KeySize..]);

        // 3. <- e
        _localEphemeral ??= X25519Key.Create();
        var e = _localEphemeral.ExportPublic();
        MixHash(e);

        // 4. ee, s, es
        MixKey(_localEphemeral.Dh(_remoteEphemeral));
        var encryptedStatic = EncryptAndHash(_localStatic.ExportPublic());
        MixKey(_localStatic.Dh(_remoteEphemeral));

        var message = new List<byte>(e);
        message.AddRange(encryptedStatic);
        message.AddRange(EncryptAndHash(payload ?? Array.Empty<byte>()));
        _step = 1;
        _lastReceivedPayload = receivedPayload;
        return message.ToArray();
    }

    /// <summary>Message 3 (initiator): "-&gt; s, se" (64 bytes with empty payload).</summary>
    public byte[] WriteMessage3(byte[] message2, byte[]? payload = null)
    {
        if (_role != Role.Initiator || _step != 1)
            throw new InvalidOperationException("Invalid handshake state for message 3");
        if (message2.Length < KeySize + KeySize + TagSize)
            throw new ArgumentException("Invalid XX message 2 size");

        _remoteEphemeral = X25519Key.ImportPublic(message2.AsSpan(0, KeySize).ToArray());
        MixHash(_remoteEphemeral.ExportPublic());

        MixKey(_localEphemeral!.Dh(_remoteEphemeral));
        var encryptedRemoteStaticLength = KeySize + TagSize;
        var decryptedStatic = DecryptAndHash(message2[KeySize..(KeySize + encryptedRemoteStaticLength)]);
        _remoteStatic = X25519Key.ImportPublic(decryptedStatic);
        // "es" of message 2: initiator ephemeral x responder static
        MixKey(_localEphemeral.Dh(_remoteStatic));

        // The remote payload.
        var receivedPayload = DecryptAndHash(message2[(KeySize + encryptedRemoteStaticLength)..]);

        var encryptedStatic = EncryptAndHash(_localStatic.ExportPublic());
        // "se" of message 3: initiator static x responder ephemeral
        MixKey(_localStatic.Dh(_remoteEphemeral));

        var message = new List<byte>(encryptedStatic);
        message.AddRange(EncryptAndHash(payload ?? Array.Empty<byte>()));
        _step = 2;
        IsComplete = true;
        _lastReceivedPayload = receivedPayload;
        return message.ToArray();
    }

    /// <summary>Process message 3 (responder); completes the handshake and returns the remote payload.</summary>
    public byte[] ReadMessage3(byte[] message3)
    {
        if (_role != Role.Responder || _step != 1)
            throw new InvalidOperationException("Invalid handshake state for read message 3");
        if (message3.Length < KeySize + TagSize)
            throw new ArgumentException("Invalid XX message 3 size");

        var decryptedStatic = DecryptAndHash(message3.AsSpan(0, KeySize + TagSize).ToArray());
        _remoteStatic = X25519Key.ImportPublic(decryptedStatic);
        MixKey(_localEphemeral!.Dh(_remoteStatic));

        var payload = DecryptAndHash(message3[(KeySize + TagSize)..]);
        _step = 2;
        IsComplete = true;
        return payload;
    }

    /// <summary>Payload received in the previous handshake message (empty for bitchat).</summary>
    public byte[] LastReceivedPayload => _lastReceivedPayload ?? Array.Empty<byte>();
    private byte[]? _lastReceivedPayload;

    /// <summary>Split into transport ciphers: (sender, receiver).</summary>
    public (NoiseCipherState Sender, NoiseCipherState Receiver) Split()
    {
        if (!IsComplete) throw new InvalidOperationException("Handshake not complete");

        var (k1, k2) = Kdf(_ck, Array.Empty<byte>());
        return _role == Role.Initiator
            ? (new NoiseCipherState(k1), new NoiseCipherState(k2))
            : (new NoiseCipherState(k2), new NoiseCipherState(k1));
    }
}

/// <summary>Noise transport cipher with explicit nonce management (bitchat wire format).</summary>
public sealed class NoiseCipherState
{
    private readonly byte[] _key;

    public NoiseCipherState(byte[] key) => _key = key;

    /// <summary>Encrypt with explicit nonce; returns raw ciphertext + 16-byte tag.</summary>
    public byte[] Encrypt(ulong nonce, byte[] plaintext)
    {
        var (ciphertext, tag) = NoiseChaCha20Poly1305.Encrypt(_key, nonce, null, plaintext);
        var result = new byte[plaintext.Length + 16];
        Array.Copy(ciphertext, result, plaintext.Length);
        Array.Copy(tag, 0, result, plaintext.Length, 16);
        return result;
    }

    /// <summary>Decrypt raw ciphertext + 16-byte tag with explicit nonce.</summary>
    public byte[] Decrypt(ulong nonce, byte[] ciphertext)
    {
        return NoiseChaCha20Poly1305.Decrypt(_key, nonce, null, ciphertext);
    }
}
