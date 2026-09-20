using System.IO;

using System.Security.Cryptography;
using Bitchat.Windows.Protocol;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Crypto.Parameters;

namespace Bitchat.Windows.Noise;

/// <summary>Sliding-window replay protection (iOS compatible, 1024-entry window).</summary>
internal sealed class ReplayWindow
{
    public const int Size = 1024;
    private const int WindowBytes = Size / 8;
    private long _highest;
    private readonly byte[] _window = new byte[WindowBytes];

    public bool IsValid(long nonce)
    {
        if (nonce + Size <= _highest) return false;
        if (nonce > _highest) return true;
        var offset = (int)(_highest - nonce);
        return (_window[offset / 8] & (1 << (offset % 8))) == 0;
    }

    public void MarkSeen(long nonce)
    {
        if (nonce > _highest)
        {
            var shift = (int)(nonce - _highest);
            if (shift >= Size)
            {
                Array.Clear(_window);
            }
            else
            {
                for (var i = WindowBytes - 1; i >= 0; i--)
                {
                    var sourceByteIndex = i - shift / 8;
                    int newByte = 0;
                    if (sourceByteIndex >= 0)
                    {
                        newByte = _window[sourceByteIndex] >> (shift % 8);
                        if (sourceByteIndex > 0 && shift % 8 != 0)
                            newByte |= _window[sourceByteIndex - 1] << (8 - shift % 8);
                    }
                    _window[i] = (byte)(newByte & 0xFF);
                }
            }
            _highest = nonce;
            _window[0] |= 1;
        }
        else
        {
            var offset = (int)(_highest - nonce);
            _window[offset / 8] |= (byte)(1 << (offset % 8));
        }
    }
}

/// <summary>Transport session with a peer after a completed Noise XX handshake.</summary>
public sealed class NoiseSession
{
    private const int NonceSizeBytes = 4;

    private readonly object _lock = new();
    private NoiseCipherState? _sendCipher;
    private NoiseCipherState? _receiveCipher;
    private long _messagesSent;
    private readonly ReplayWindow _replayWindow = new();

    public string PeerId { get; }
    public bool IsInitiator { get; }
    public byte[] RemoteStaticKey { get; }
    public bool Established { get; private set; }

    public NoiseSession(string peerId, bool isInitiator, byte[] remoteStaticKey)
    {
        PeerId = peerId;
        IsInitiator = isInitiator;
        RemoteStaticKey = remoteStaticKey;
    }

    internal void Complete(NoiseCipherState sender, NoiseCipherState receiver)
    {
        lock (_lock)
        {
            _sendCipher = sender;
            _receiveCipher = receiver;
            _messagesSent = 0;
            Established = true;
        }
    }

    /// <summary>Wire format: &lt;nonce u32 big-endian&gt;&lt;ChaChaPoly ciphertext + 16-byte tag&gt;.</summary>
    public byte[] Encrypt(byte[] data)
    {
        lock (_lock)
        {
            if (!Established || _sendCipher == null) throw new InvalidOperationException("Session not established");
            if (_messagesSent > uint.MaxValue - 1) throw new InvalidOperationException("Nonce exceeded");

            var currentNonce = _messagesSent;
            var ciphertext = _sendCipher.Encrypt((ulong)currentNonce, data);
            _messagesSent++;

            var result = new byte[NonceSizeBytes + ciphertext.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(result, (uint)currentNonce);
            Array.Copy(ciphertext, 0, result, NonceSizeBytes, ciphertext.Length);
            return result;
        }
    }

    public byte[] Decrypt(byte[] combinedPayload)
    {
        lock (_lock)
        {
            if (!Established || _receiveCipher == null) throw new InvalidOperationException("Session not established");
            if (combinedPayload.Length < NonceSizeBytes + 16) throw new InvalidOperationException("Payload too small");

            var nonce = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(combinedPayload);
            var ciphertext = combinedPayload[NonceSizeBytes..];

            if (!_replayWindow.IsValid(nonce)) throw new InvalidOperationException("Replay attack detected");

            var plaintext = _receiveCipher.Decrypt(nonce, ciphertext);
            _replayWindow.MarkSeen(nonce);
            return plaintext;
        }
    }
}

/// <summary>Result of processing an incoming handshake message.</summary>
public sealed record HandshakeResult(byte[]? Response, bool EstablishedNow, byte[]? RemoteStaticKey);

/// <summary>
/// Manages the persistent identity (Noise static key + Ed25519 signing key), peer sessions,
/// and Ed25519 packet signing. Compatible with the iOS/Android implementations.
/// </summary>
public sealed class NoiseService
{
    private const string ProtocolName = NoiseHandshake.ProtocolName;

    private readonly object _sessionsLock = new();

    private sealed class PeerState
    {
        public NoiseSession? Session;                       // established transport
        public NoiseHandshake? PendingInitiator;            // we sent m1, waiting m2
        public NoiseHandshake? ResponderCandidate;          // we received m1, waiting m3
        public NoiseSession? ResponderSession;              // completed responder candidate
    }

    private readonly Dictionary<string, PeerState> _sessions = new();

    public X25519Key StaticIdentityKey { get; }
    public byte[] StaticIdentityPublicKey { get; }
    public Ed25519KeyPair SigningKey { get; }

    public string MyPeerId { get; }

    public NoiseService(string identityFilePath)
    {
        var identity = IdentityStore.LoadOrCreate(identityFilePath);
        StaticIdentityKey = X25519Key.ImportPrivate(identity.NoisePrivateKey);
        StaticIdentityPublicKey = identity.NoisePublicKey;
        SigningKey = new Ed25519KeyPair(identity.SigningPrivateKey, identity.SigningPublicKey);

        using var md = SHA256.Create();
        var hash = md.ComputeHash(StaticIdentityPublicKey);
        MyPeerId = Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    public byte[] GetStaticPublicKeyData() => StaticIdentityPublicKey;
    public byte[] GetSigningPublicKeyData() => SigningKey.PublicKey;

    // ---- Handshake management ----

    public byte[]? InitiateHandshake(string peerId)
    {
        lock (_sessionsLock)
        {
            if (!_sessions.TryGetValue(peerId, out var state))
                _sessions[peerId] = state = new PeerState();

            // Already established? No handshake needed.
            if (state.Session is { Established: true }) return null;
            // Already mid-handshake as initiator — do not clobber it.
            if (state.PendingInitiator != null) return null;

            var handshake = NoiseHandshake.CreateInitiator(StaticIdentityKey);
            var message1 = handshake.WriteMessage1();
            state.PendingInitiator = handshake;
            return message1;
        }
    }

    public bool HasPendingInitiator(string peerId)
    {
        lock (_sessionsLock)
        {
            return _sessions.TryGetValue(peerId, out var state) && state.PendingInitiator != null;
        }
    }

/// <summary>Process an incoming handshake message; returns the response to send, if any.
    /// Handles simultaneous-initiator collisions with a peer-ID tiebreaker (Android-compatible).</summary>
    public HandshakeResult ProcessHandshakeMessage(byte[] data, string claimedPeerId)
    {
        bool isMessage1 = data.Length == 32;
        bool isMessage2 = data.Length == 96;
        bool isMessage3 = data.Length == 64;

        lock (_sessionsLock)
        {
            if (!_sessions.TryGetValue(claimedPeerId, out var state))
                _sessions[claimedPeerId] = state = new PeerState();

            // ---- Existing responder candidate (replacement in progress) ----
            if (state.ResponderCandidate != null)
            {
                if (isMessage1)
                {
                    // Peer re-initiated while our candidate is in flight: yield only if we're the higher peer.
                    if (MyPeerId.CompareTo(claimedPeerId) > 0)
                    {
                        state.ResponderCandidate = null;
                        // fall through to create a new responder below
                    }
                    else
                    {
                        // They should yield — keep our candidate, drop their m1
                        return new HandshakeResult(null, false, null);
                    }
                }
                else
                {
                    // m2 or m3 → process with the existing candidate below
                }
            }
            else if (state.PendingInitiator != null && isMessage1)
            {
                // ---- Collision: both sides initiated ----
                if (MyPeerId.CompareTo(claimedPeerId) > 0)
                {
                    // We yield: discard our initiator, become responder for their m1
                    state.PendingInitiator = null;
                }
                else
                {
                    // We keep our initiator; they will yield when they receive our m1
                    return new HandshakeResult(null, false, null);
                }
            }

            // ---- Dispatch to the correct handshake ----
            if (isMessage1)
            {
                // Create a fresh responder (normal path or after yielding)
                var handshake = NoiseHandshake.CreateResponder(StaticIdentityKey);
                var response = handshake.WriteMessage2(data);
                state.ResponderCandidate = handshake;
                return new HandshakeResult(response, false, null);
            }

            if (isMessage2)
            {
                // Initiator path: we sent message 1 earlier.
                if (state.PendingInitiator == null)
                    return new HandshakeResult(null, false, null);

                var handshake = state.PendingInitiator;
                var message3 = handshake.WriteMessage3(data);
                var (sender, receiver) = handshake.Split();
                var remoteStatic = handshake.RemoteStaticKey!;
                var session = new NoiseSession(claimedPeerId, true, remoteStatic);
                session.Complete(sender, receiver);
                state.Session = session;
                state.PendingInitiator = null;
                return new HandshakeResult(message3, true, remoteStatic);
            }

            if (isMessage3)
            {
                // Responder path: finish the candidate and promote it.
                if (state.ResponderCandidate == null)
                    return new HandshakeResult(null, false, null);

                var handshake = state.ResponderCandidate;
                handshake.ReadMessage3(data);
                var (sender, receiver) = handshake.Split();
                var remoteStatic = handshake.RemoteStaticKey!;
                var session = new NoiseSession(claimedPeerId, false, remoteStatic);
                session.Complete(sender, receiver);
                state.Session = session;
                state.ResponderCandidate = null;
                state.PendingInitiator = null;
                return new HandshakeResult(null, true, remoteStatic);
            }

            return new HandshakeResult(null, false, null);
        }
    }

    public bool HasEstablishedSession(string peerId)
    {
        lock (_sessionsLock)
        {
            return _sessions.TryGetValue(peerId, out var entry) && entry.Session is { Established: true };
        }
    }

    public byte[]? GetRemoteStaticKey(string peerId)
    {
        lock (_sessionsLock)
        {
return _sessions.TryGetValue(peerId, out var entry) && entry.Session is { Established: true }
    ? entry.Session.RemoteStaticKey
    : null;
        }
    }

    public IReadOnlyList<string> GetEstablishedPeerIds()
    {
        lock (_sessionsLock)
        {
            return _sessions.Where(kv => kv.Value.Session is { Established: true }).Select(kv => kv.Key).ToList();
        }
    }

    public void RemoveSession(string peerId)
    {
        lock (_sessionsLock)
        {
            _sessions.Remove(peerId);
        }
    }

    public byte[] Encrypt(byte[] data, string peerId)
    {
        lock (_sessionsLock)
        {
if (!_sessions.TryGetValue(peerId, out var entry) || entry.Session is not { Established: true })
                throw new InvalidOperationException($"No established session with {peerId}");
            return entry.Session.Encrypt(data);
        }
    }

    public byte[] Decrypt(byte[] encryptedData, string peerId)
    {
        lock (_sessionsLock)
        {
if (!_sessions.TryGetValue(peerId, out var entry) || entry.Session is not { Established: true })
                throw new InvalidOperationException($"No established session with {peerId}");
            return entry.Session.Decrypt(encryptedData);
        }
    }

    // ---- Ed25519 packet signing ----

    public byte[]? SignPacket(BitchatPacket packet)
    {
        var packetData = BinaryProtocol.Encode(packet.CloneForSigning(), pad: true);
        if (packetData == null)
        {
            Console.Error.WriteLine($"[sign] encode failed: version={packet.Version} type=0x{packet.Type:X2} payload={packet.Payload.Length}B compressedWire={(packet.WireCompressedBytes?.Length.ToString() ?? "none")}");
        }
        if (packetData == null) return null;
        return Ed25519.Sign(SigningKey.PrivateKey, packetData);
    }

    public bool VerifyPacketSignature(BitchatPacket packet, byte[] signingPublicKey)
    {
        if (packet.Signature == null) return false;

        // Primary candidate mirrors the Android/iOS verification path, including wire-byte reuse.
        var primary = BinaryProtocol.Encode(packet.CloneForSigning(), pad: true);
        if (primary != null && Ed25519.Verify(packet.Signature, primary, signingPublicKey)) return true;

        // Fallbacks cover minor deflate output differences between clients.
        var clone = packet.CloneForSigning();
        clone.WireCompressedBytes = null;
        var uncompressed = BinaryProtocol.Encode(clone, pad: true, force: ForceCompression.Never);
        if (uncompressed != null && Ed25519.Verify(packet.Signature, uncompressed, signingPublicKey)) return true;

        if (packet.Payload.Length > BitchatConstants.CompressionThresholdBytes)
        {
            var compressed = BinaryProtocol.Encode(clone, pad: true, force: ForceCompression.Always);
            if (compressed != null && Ed25519.Verify(packet.Signature, compressed, signingPublicKey)) return true;
        }
        return false;
    }

    public static bool VerifySignature(byte[] signature, byte[] data, byte[] publicKey) =>
        Ed25519.Verify(signature, data, publicKey);

    /// <summary>Mesh peer ID: first 8 bytes of SHA-256(staticPublicKey) as 16 lowercase hex chars.</summary>
    public static string DerivePeerId(byte[] staticPublicKey)
    {
        using var md = SHA256.Create();
        var hash = md.ComputeHash(staticPublicKey);
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}

/// <summary>Ed25519 signing helpers (BouncyCastle).</summary>
public static class Ed25519
{
    public static byte[] Sign(byte[] privateKey, byte[] data)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(privateKey, 0));
        signer.BlockUpdate(data, 0, data.Length);
        return signer.GenerateSignature();
    }

    public static bool Verify(byte[] signature, byte[] data, byte[] publicKey)
    {
        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
        verifier.BlockUpdate(data, 0, data.Length);
        return verifier.VerifySignature(signature);
    }

    public static byte[] PublicKeyFromPrivate(byte[] privateKey)
    {
        var priv = new Ed25519PrivateKeyParameters(privateKey, 0);
        return priv.GeneratePublicKey().GetEncoded();
    }
}

/// <summary>Ed25519 key pair holder for signing.</summary>
public sealed class Ed25519KeyPair
{
    public byte[] PrivateKey { get; }
    public byte[] PublicKey { get; }

    public Ed25519KeyPair(byte[] privateKey, byte[] publicKey)
    {
        PrivateKey = privateKey;
        PublicKey = publicKey;
    }
}

/// <summary>Persistent identity storage (Noise static key + Ed25519 signing key).</summary>
public static class IdentityStore
{
    private const uint Magic = 0x42435449; // "BCTI"

    public sealed record Identity(byte[] NoisePrivateKey, byte[] NoisePublicKey, byte[] SigningPrivateKey, byte[] SigningPublicKey);

    public static Identity LoadOrCreate(string filePath)
    {
        if (File.Exists(filePath))
        {
            try
            {
                var data = File.ReadAllBytes(filePath);
                var identity = Decode(data);
                if (identity != null) return identity;
            }
            catch
            {
                // fall through to regeneration
            }
        }

        var noisePrivate = new byte[32];
        RandomNumberGenerator.Fill(noisePrivate);
        var x25519 = X25519Key.ImportPrivate(noisePrivate);
        var noisePublic = x25519.ExportPublic();

        var signingPrivate = new byte[32];
        RandomNumberGenerator.Fill(signingPrivate);
        var signingPublic = Ed25519.PublicKeyFromPrivate(signingPrivate);

        var identityNew = new Identity(noisePrivate, noisePublic, signingPrivate, signingPublic);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllBytes(filePath, Encode(identityNew));
        return identityNew;
    }

    private static byte[] Encode(Identity identity)
    {
        var result = new byte[4 + 32 * 4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(result, Magic);
        Array.Copy(identity.NoisePrivateKey, 0, result, 4, 32);
        Array.Copy(identity.NoisePublicKey, 0, result, 36, 32);
        Array.Copy(identity.SigningPrivateKey, 0, result, 68, 32);
        Array.Copy(identity.SigningPublicKey, 0, result, 100, 32);
        return result;
    }

    private static Identity? Decode(byte[] data)
    {
        if (data.Length != 132) return null;
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data) != Magic) return null;
        return new Identity(
            data[4..36],
            data[36..68],
            data[68..100],
            data[100..132]);
    }
}
