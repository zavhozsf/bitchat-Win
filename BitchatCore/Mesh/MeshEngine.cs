using System.IO;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Bitchat.Windows.Noise;
using Bitchat.Windows.Protocol;

namespace Bitchat.Windows.Mesh;

public sealed class PeerInfo
{
    public string PeerId { get; init; } = "";
    public string Nickname { get; set; } = "";
    public byte[] NoisePublicKey { get; set; } = Array.Empty<byte>();
    public byte[] SigningPublicKey { get; set; } = Array.Empty<byte>();
    public ulong LastSeen { get; set; }
    public int Rssi { get; set; }
    public bool IsDirectConnection { get; set; }
}

public sealed class ChatMessage
{
    public string Sender { get; init; } = "";
    public string SenderPeerId { get; init; } = "";
    public string Content { get; init; } = "";
    public bool IsPrivate { get; init; }
    public bool IsFromMe { get; init; }
    public string? Status { get; set; }
    public string? MessageId { get; set; }
    public string? FilePath { get; set; }
    public string? FileName { get; set; }
    public long FileSizeBytes { get; set; }
    public DateTimeOffset Timestamp { get; init; }
}

public sealed record IncomingPacket(BitchatPacket Packet, string FromPeerId, int Rssi);

/// <summary>
/// Core mesh logic: peers, announcements, dedup, TTL relay, broadcast/private messages.
/// Mirrors the Android/iOS implementations.
/// </summary>
public sealed class MeshEngine
{
    private static readonly TimeSpan StalePeerTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan AnnounceInterval = TimeSpan.FromSeconds(45);

    private readonly NoiseService _noise;
    private string _myNickname;

    private readonly object _peersLock = new();
    private readonly Dictionary<string, PeerInfo> _peers = new();
    private readonly object _blockLock = new();
    private readonly HashSet<string> _blockedPeerIds = new();

    private readonly object _dedupLock = new();
    private readonly Dictionary<string, long> _processedMessages = new(); // id -> first seen ms

    private readonly object _fragmentsLock = new();
    private readonly Dictionary<string, FragmentSet> _fragmentSets = new();

    private readonly ConcurrentQueue<(string PeerId, string MessageId, string Content)> _pendingPrivateMessages = new();

    private readonly CancellationTokenSource _cts = new();
    private readonly Timer _announceTimer;
    private readonly Timer _cleanupTimer;

    public event Action<ChatMessage>? MessageReceived;
    public event Action<IReadOnlyCollection<PeerInfo>>? PeersChanged;
    public event Action<string>? SystemMessage;
    public event Action<byte[]>? PacketOut; // broadcast over all BLE links
    public event Action<string>? HandshakeInitiated;

    /// <summary>Verbose packet-level diagnostics (set from the /debug toggle).</summary>
    public static bool DebugPackets;

    /// <summary>Status transitions for our outgoing private messages ("delivered"/"read").</summary>
    public event Action<string, string, string>? OutgoingStatusChanged;

    private readonly object _receiptsLock = new();
    private readonly Dictionary<string, (string PeerId, string Status)> _outgoingMessages = new();
    private readonly Dictionary<string, (string PeerId, bool Read)> _incomingMessages = new();

    public readonly GossipSyncManager Gossip;

    public MeshEngine(NoiseService noise, string nickname)
    {
        _noise = noise;
        _myNickname = nickname;
        _announceTimer = new Timer(_ => SafeAnnounce(), null, TimeSpan.Zero, AnnounceInterval);
        _cleanupTimer = new Timer(_ => Cleanup(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        Gossip = new GossipSyncManager(noise.MyPeerId, SignAndDispatch, msg => SystemMessage?.Invoke(msg));
        Voice = new Voice.VoiceService(
            SendVoiceBurstBroadcast,
            SendVoiceBurstPrivate,
            msg => SystemMessage?.Invoke(msg),
            () => DebugPackets);
        Voice.VoiceReceived += (peerId, nickname, durationMs, isPrivate) =>
            MessageReceived?.Invoke(new ChatMessage
            {
                Sender = nickname,
                SenderPeerId = peerId,
                Content = $"🎙 голосовое {durationMs / 1000.0:F0} с",
                IsPrivate = isPrivate,
                Timestamp = DateTimeOffset.Now
            });
        Voice.VoiceFileReady += (aacBytes, durationMs, isPrivate, peerId) =>
        {
            var fileName = $"voice-{DateTime.Now:yyyyMMdd-HHmmss}.m4a";
            var filePayload = new BitchatFilePacket(fileName, aacBytes.Length, "audio/mp4", aacBytes).Encode();

            // Save a local copy so the sender can also play their own voice message.
            var localPath = SaveIncomingMedia(fileName, aacBytes);

            if (isPrivate && peerId != null)
            {
                var encrypted = _noise.Encrypt(new NoisePayload(NoisePayloadType.FileTransfer, filePayload).Encode(), peerId);
                var pkt = new BitchatPacket
                {
                    Version = VersionForPayload(encrypted.Length),
                    Type = (byte)MessageType.NoiseEncrypted,
                    SenderId = HexToBytes(MyPeerId),
                    RecipientId = HexToBytes(peerId),
                    Payload = encrypted,
                    Ttl = BitchatConstants.MessageTtlHops
                };
                pkt.NowTimestamp();
                SignAndDispatch(pkt);
                MessageReceived?.Invoke(new ChatMessage
                {
                    Sender = _myNickname,
                    SenderPeerId = peerId,
                    Content = fileName,
                    FilePath = localPath,
                    FileName = fileName,
                    FileSizeBytes = aacBytes.Length,
                    IsPrivate = true,
                    IsFromMe = true,
                    Timestamp = DateTimeOffset.Now
                });
            }
            else
            {
                var pkt = new BitchatPacket
                {
                    Version = 2,
                    Type = (byte)MessageType.FileTransfer,
                    SenderId = HexToBytes(MyPeerId),
                    RecipientId = BitchatConstants.BroadcastRecipient,
                    Payload = filePayload,
                    Ttl = BitchatConstants.MessageTtlHops
                };
                pkt.NowTimestamp();
                SignAndDispatch(pkt);
                Gossip.OnPublicPacketSeen(pkt);
                MessageReceived?.Invoke(new ChatMessage
                {
                    Sender = _myNickname,
                    SenderPeerId = MyPeerId,
                    Content = fileName,
                    FilePath = localPath,
                    FileName = fileName,
                    FileSizeBytes = aacBytes.Length,
                    IsFromMe = true,
                    Timestamp = DateTimeOffset.Now
                });
            }
        };
    }

    /// <summary>Request a history sync from all neighbors (e.g. after a new link opens).</summary>
    public void RequestSyncSoon()
    {
        Gossip.ScheduleInitialSync();
    }

    private static event Action<string>? StaticMessage;

    public static void SetStaticLog(Action<string> handler) => StaticMessage += handler;

    private static void Reject(string reason, string peerId, BitchatPacket packet)
    {
        if (DebugPackets)
            StaticMessage?.Invoke($"[drop] {reason} from {peerId[..Math.Min(8, peerId.Length)]} type=0x{packet.Type:X2}");
    }

    public string MyPeerId => _noise.MyPeerId;
    public string Nickname => _myNickname;

    public void SetNickname(string nickname)
    {
        var cleaned = BitchatRuntime.TruncateNickname(nickname);
        if (cleaned.Length == 0 || cleaned == _myNickname) return;
        _myNickname = cleaned;
        SendAnnouncement();
    }

    // ---- Peer management ----

    public IReadOnlyCollection<PeerInfo> GetPeersSnapshot()
    {
        lock (_peersLock) return _peers.Values.Select(p => p).ToArray();
    }

    private void UpdatePeer(string peerId, Action<PeerInfo> update)
    {
        PeerInfo? info;
        lock (_peersLock)
        {
            if (!_peers.TryGetValue(peerId, out info))
            {
                info = new PeerInfo { PeerId = peerId, Nickname = peerId[..8] };
                _peers[peerId] = info;
            }
            update(info);
            info.LastSeen = (ulong)Now();
        }
        PeersChanged?.Invoke(GetPeersSnapshot());
    }

    public PeerInfo? FindPeerByNickname(string nickname)
    {
        lock (_peersLock)
        {
            return _peers.Values.FirstOrDefault(p =>
                string.Equals(p.Nickname, nickname, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ---- Outgoing ----

    private void SafeAnnounce()
    {
        try { SendAnnouncement(); } catch { }
    }

    public void SendAnnouncement()
    {
        var announcement = IdentityAnnouncement.ForLocalPeer(_myNickname, _noise.GetStaticPublicKeyData(), _noise.GetSigningPublicKeyData());
        var payload = announcement.Encode();
        if (payload == null) return;

        var packet = new BitchatPacket
        {
            Type = (byte)MessageType.Announce,
            SenderId = HexToBytes(MyPeerId),
            Payload = payload,
            Ttl = BitchatConstants.MessageTtlHops
        };
        packet.NowTimestamp();
        SignAndDispatch(packet);
        Gossip.OnPublicPacketSeen(packet);
    }

    public void SendBroadcast(string content)
    {
        var packet = new BitchatPacket
        {
            Version = 1,
            Type = (byte)MessageType.Message,
            SenderId = HexToBytes(MyPeerId),
            RecipientId = BitchatConstants.BroadcastRecipient,
            Payload = Encoding.UTF8.GetBytes(content),
            Ttl = BitchatConstants.MessageTtlHops
        };
        packet.NowTimestamp();
        SignAndDispatch(packet);
        Gossip.OnPublicPacketSeen(packet);

        MessageReceived?.Invoke(new ChatMessage
        {
            Sender = _myNickname,
            SenderPeerId = MyPeerId,
            Content = content,
            IsFromMe = true,
            Timestamp = DateTimeOffset.Now,
            Status = "sent"
        });
    }

public void SendPrivate(string content, string recipientPeerId)
    {
        var messageId = Guid.NewGuid().ToString().ToUpperInvariant();

        if (_noise.HasEstablishedSession(recipientPeerId))
        {
            SendPrivateEncrypted(content, recipientPeerId, messageId);
        }
        else
        {
            InitiateHandshakeGuarded(recipientPeerId);
            _pendingPrivateMessages.Enqueue((recipientPeerId, messageId, content));
            SystemMessage?.Invoke($"no session with {recipientPeerId[..8]} yet — handshake started, message queued");
        }

        MessageReceived?.Invoke(new ChatMessage
        {
            Sender = _myNickname,
            SenderPeerId = recipientPeerId,
            Content = content,
            IsPrivate = true,
            IsFromMe = true,
            MessageId = messageId,
            Timestamp = DateTimeOffset.Now,
            Status = $"→ {recipientPeerId[..8]}"
        });
    }

    private void SendPrivateEncrypted(string content, string recipientPeerId, string messageId)
    {
        try
        {
            var pm = new PrivateMessagePacket(messageId, content);
            var tlv = pm.Encode();
            if (tlv == null) return;

            var noisePayload = new NoisePayload(NoisePayloadType.PrivateMessage, tlv);
            var encrypted = _noise.Encrypt(noisePayload.Encode(), recipientPeerId);

            var packet = new BitchatPacket
            {
                Version = VersionForPayload(encrypted.Length),
                Type = (byte)MessageType.NoiseEncrypted,
                SenderId = HexToBytes(MyPeerId),
                RecipientId = HexToBytes(recipientPeerId),
                Payload = encrypted,
                Ttl = BitchatConstants.MessageTtlHops
            };
            packet.NowTimestamp();
            SignAndDispatch(packet);

            lock (_receiptsLock)
            {
                _outgoingMessages[messageId] = (recipientPeerId, "sent");
            }
        }
        catch (Exception ex)
        {
            SystemMessage?.Invoke($"encrypt failed: {ex.Message}");
        }
    }

    private void SendDeliveryAck(string messageId, string peerId)
    {
        try
        {
            var payload = new NoisePayload(NoisePayloadType.Delivered, Encoding.UTF8.GetBytes(messageId));
            var encrypted = _noise.Encrypt(payload.Encode(), peerId);
            var packet = new BitchatPacket
            {
                Version = VersionForPayload(encrypted.Length),
                Type = (byte)MessageType.NoiseEncrypted,
                SenderId = HexToBytes(MyPeerId),
                RecipientId = HexToBytes(peerId),
                Payload = encrypted,
                Ttl = BitchatConstants.MessageTtlHops
            };
            packet.NowTimestamp();
            SignAndDispatch(packet);
        }
        catch { }
    }

    public void SendLeave()
    {
        var packet = new BitchatPacket
        {
            Type = (byte)MessageType.Leave,
            SenderId = HexToBytes(MyPeerId),
            Payload = Array.Empty<byte>(),
            Ttl = BitchatConstants.MessageTtlHops
        };
        packet.NowTimestamp();
        SignAndDispatch(packet);
    }

    public void InitiateHandshake(string peerId)
    {
        var data = _noise.InitiateHandshake(peerId);
        if (data == null) return;
        if (data == null) return;

        var packet = new BitchatPacket
        {
            Version = 1,
            Type = (byte)MessageType.NoiseHandshake,
            SenderId = HexToBytes(MyPeerId),
            RecipientId = HexToBytes(peerId),
            Payload = data,
            Ttl = BitchatConstants.MessageTtlHops
        };
        packet.NowTimestamp();
        Dispatch(packet);
        HandshakeInitiated?.Invoke(peerId);
    }

    private void SignAndDispatch(BitchatPacket packet)
    {
        packet.Signature = _noise.SignPacket(packet);
        if (packet.Signature == null)
        {
            var detail = $"v{packet.Version} type=0x{packet.Type:X2} payload={packet.Payload.Length}B";
            SystemMessage?.Invoke($"failed to sign packet ({detail}) — пришлите этот текст разработчику");
            return;
        }
        Dispatch(packet);
    }

    private void Dispatch(BitchatPacket packet)
    {
        var encoded = EncodeForBle(packet);
        if (encoded == null) return;

        if (encoded.Length > BitchatConstants.FragmentSizeThreshold)
        {
            SendFragmented(packet, encoded);
        }
        else
        {
            PacketOut?.Invoke(encoded);
        }
    }

    public static byte[]? EncodeForBle(BitchatPacket packet) =>
        BinaryProtocol.Encode(packet, pad: BinaryProtocol.ShouldPadForBle(packet.Type));

    private void SendFragmented(BitchatPacket original, byte[] encoded)
    {
        var fragmentId = FragmentPayload.GenerateFragmentId();
        var chunks = (encoded.Length + BitchatConstants.MaxFragmentSize - 1) / BitchatConstants.MaxFragmentSize;
        if (chunks > 0xFFFF) return;

        // Pace fragments like the mobile clients (20 ms): flooding drops them in the BLE stack.
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < chunks; i++)
            {
                var offset = i * BitchatConstants.MaxFragmentSize;
                var size = Math.Min(BitchatConstants.MaxFragmentSize, encoded.Length - offset);
                var chunk = new byte[size];
                Array.Copy(encoded, offset, chunk, 0, size);

                var fragmentPacket = new BitchatPacket
                {
                    Version = 1,
                    Type = (byte)MessageType.Fragment,
                    SenderId = original.SenderId,
                    RecipientId = original.RecipientId,
                    Payload = new FragmentPayload(fragmentId, i, chunks, original.Type, chunk).Encode(),
                    Ttl = original.Ttl
                };
                fragmentPacket.Timestamp = original.Timestamp;

                var encodedFragment = BinaryProtocol.Encode(fragmentPacket, pad: false);
                if (encodedFragment == null) return;
                PacketOut?.Invoke(encodedFragment);
                if (i < chunks - 1)
                    await Task.Delay(20);
            }
            if (DebugPackets)
                SystemMessage?.Invoke($"[frag] sent {chunks} fragments type=0x{original.Type:X2}");
        });
    }

    // ---- Incoming ----

    public void OnPacketReceived(BitchatPacket packet, string fromPeerId, int rssi)
    {
        var fromHex = fromPeerId;
        if (fromHex == MyPeerId) return;

        var type = (MessageType)packet.Type;

        if (!ValidatePacket(packet, fromHex))
        {
            return;
        }

        switch (type)
        {
            case MessageType.Announce:
                HandleAnnounce(packet, fromHex, rssi);
                break;
            case MessageType.Message:
                HandleMessage(packet, fromHex);
                break;
            case MessageType.Leave:
                HandleLeave(fromHex);
                break;
            case MessageType.NoiseHandshake:
                HandleNoiseHandshake(packet, fromHex);
                break;
            case MessageType.NoiseEncrypted:
                HandleNoiseEncrypted(packet, fromHex);
                break;
            case MessageType.Fragment:
                HandleFragment(packet, fromHex, rssi);
                break;
            case MessageType.RequestSync:
                HandleRequestSync(packet, fromHex);
                break;
            case MessageType.FileTransfer:
            case MessageType.VoiceFrame:
                break;
            default:
                return;
        }

        UpdatePeerLastSeen(fromHex);
        RelayIfNeeded(packet, fromHex);
    }

    public bool BlockPeer(string peerId)
    {
        lock (_blockLock)
        {
            if (!_blockedPeerIds.Add(peerId)) return false;
        }
        lock (_peersLock)
        {
            _peers.Remove(peerId);
        }
        SystemMessage?.Invoke($"* peer {peerId[..8]} blocked");
        PeersChanged?.Invoke(GetPeersSnapshot());
        return true;
    }

    public bool UnblockPeer(string peerId)
    {
        lock (_blockLock)
        {
            return _blockedPeerIds.Remove(peerId);
        }
    }

    public IReadOnlyCollection<string> BlockedPeers
    {
        get { lock (_blockLock) return _blockedPeerIds.ToArray(); }
    }

    private bool ValidatePacket(BitchatPacket packet, string peerId)
    {
        lock (_blockLock)
        {
            if (_blockedPeerIds.Contains(peerId)) return false;
        }

        var type = (MessageType)packet.Type;

        // Duplicate detection (5-minute window), fresh announces exempt
        var messageId = $"{peerId}-{PacketIdUtil.ComputeIdHex(packet)}";
        var now = Now();
        lock (_dedupLock)
        {
            if (_processedMessages.ContainsKey(messageId))
            {
                var isFreshAnnounce = type == MessageType.Announce && packet.Ttl >= BitchatConstants.MessageTtlHops;
                if (!isFreshAnnounce) return false;
            }
        }

        // Mandatory signature verification for public packet types
        if (type is MessageType.Announce or MessageType.Message or MessageType.Leave or MessageType.FileTransfer)
        {
            byte[]? signingKey = null;
            lock (_peersLock)
            {
                if (_peers.TryGetValue(peerId, out var peer)) signingKey = peer.SigningPublicKey;
            }

            if (type == MessageType.Announce)
            {
                var announcement = AnnouncementValidator.Verify(_noise, packet, peerId);
                if (announcement == null)
                {
                    Reject("announce invalid (sig/skew/binding)", peerId, packet);
                    return false;
                }
            }
            else
            {
                if (signingKey == null)
                {
                    Reject("no signing key for peer yet", peerId, packet);
                    return false;
                }
                if (!_noise.VerifyPacketSignature(packet, signingKey))
                {
                    Reject("signature invalid", peerId, packet);
                    return false;
                }
            }
        }

        lock (_dedupLock)
        {
            _processedMessages[messageId] = now;
            if (_processedMessages.Count > BitchatConstants.MaxProcessedMessages)
            {
                var cutoff = now - (long)BitchatConstants.MessageTimeout.TotalMilliseconds;
                foreach (var key in _processedMessages.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToArray())
                    _processedMessages.Remove(key);
            }
        }
        return true;
    }

    private void HandleAnnounce(BitchatPacket packet, string peerId, int rssi)
    {
        var announcement = AnnouncementValidator.Verify(_noise, packet, peerId);
        if (announcement == null) return;

        var isNew = false;
        lock (_peersLock)
        {
            isNew = !_peers.ContainsKey(peerId);
        }

        UpdatePeer(peerId, p =>
        {
            p.Nickname = announcement.Nickname;
            p.NoisePublicKey = announcement.NoisePublicKey;
            p.SigningPublicKey = announcement.SigningPublicKey;
            if (rssi != sbyte.MinValue) p.Rssi = rssi;
        });

        Gossip.OnPublicPacketSeen(packet);

        if (isNew)
        {
            SystemMessage?.Invoke($"* {announcement.Nickname} joined the mesh");
            // Respond with our own announcement so they learn about us (once).
            SendAnnouncement();

// Kick off a handshake so private messages work immediately.
            Task.Run(() => InitiateHandshakeGuarded(peerId));
        }
    }

    private readonly Dictionary<string, DateTime> _handshakeAttempts = new();
    private static readonly TimeSpan HandshakeCooldown = TimeSpan.FromSeconds(15);

    /// <summary>Initiates a handshake unless one is already in flight or established.</summary>
    public void InitiateHandshakeGuarded(string peerId)
    {
        if (_noise.HasEstablishedSession(peerId) || _noise.HasPendingInitiator(peerId)) return;
        lock (_handshakeAttempts)
        {
            var now = DateTime.UtcNow;
            if (_handshakeAttempts.TryGetValue(peerId, out var last) && now - last < HandshakeCooldown) return;
            _handshakeAttempts[peerId] = now;
        }
        InitiateHandshake(peerId);
    }

    private void HandleMessage(BitchatPacket packet, string peerId)
    {
        var recipientId = packet.RecipientId;
        if (recipientId == null || recipientId.AsSpan().SequenceEqual(BitchatConstants.BroadcastRecipient))
        {
            if ((MessageType)packet.Type == MessageType.FileTransfer)
            {
                HandleIncomingFile(packet.Payload, peerId, isPrivate: false, packet.Timestamp);
                return;
            }
            if ((MessageType)packet.Type == MessageType.VoiceFrame)
            {
                HandleVoiceFrameData(peerId, packet.Payload, isPrivate: false, packet.Timestamp);
                return;
            }

            // Broadcast message
            string? nickname;
            lock (_peersLock)
            {
                nickname = _peers.TryGetValue(peerId, out var p) ? p.Nickname : null;
            }
            MessageReceived?.Invoke(new ChatMessage
            {
                Sender = nickname ?? peerId[..8],
                SenderPeerId = peerId,
                Content = Encoding.UTF8.GetString(packet.Payload),
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds((long)packet.Timestamp)
            });
            Gossip.OnPublicPacketSeen(packet);
        }
        else if (ByteArrayExtensions.ToHexString(recipientId) == MyPeerId)
        {
            // Private message (legacy type MESSAGE with recipient)
            string? nickname;
            lock (_peersLock)
            {
                nickname = _peers.TryGetValue(peerId, out var p) ? p.Nickname : null;
            }
            MessageReceived?.Invoke(new ChatMessage
            {
                Sender = nickname ?? peerId[..8],
                SenderPeerId = peerId,
                Content = Encoding.UTF8.GetString(packet.Payload),
                IsPrivate = true,
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds((long)packet.Timestamp)
            });
        }
    }

    private void HandleLeave(string peerId)
    {
        string? nickname;
        lock (_peersLock)
        {
            if (_peers.Remove(peerId, out var info)) nickname = info.Nickname;
            else nickname = null;
        }
        if (nickname != null)
        {
            SystemMessage?.Invoke($"* {nickname} left the mesh");
            PeersChanged?.Invoke(GetPeersSnapshot());
        }
    }

    private void HandleNoiseHandshake(BitchatPacket packet, string peerId)
    {
        if (packet.RecipientId == null || ByteArrayExtensions.ToHexString(packet.RecipientId) != MyPeerId) return;
        if (packet.Payload.Length == 0) return;

        try
        {
            var result = _noise.ProcessHandshakeMessage(packet.Payload, peerId);
            if (result.Response != null)
            {
                var response = new BitchatPacket
                {
                    Version = 1,
                    Type = (byte)MessageType.NoiseHandshake,
                    SenderId = HexToBytes(MyPeerId),
                    RecipientId = HexToBytes(peerId),
                    Payload = result.Response,
                    Ttl = BitchatConstants.MessageTtlHops
                };
                response.NowTimestamp();
                Dispatch(response);
            }

            if (result.EstablishedNow && result.RemoteStaticKey != null)
            {
                OnSessionEstablished(peerId, result.RemoteStaticKey);
            }
        }
        catch (Exception ex)
        {
            SystemMessage?.Invoke($"handshake with {peerId[..8]} failed: {ex.Message}");
        }
    }

    private readonly Dictionary<string, int> _consecutiveDecryptFailures = new();

    /// <summary>After 3 consecutive decrypt failures the Noise session is stale: reset and re-handshake.</summary>
    private void RegisterDecryptFailure(string peerId)
    {
        if (peerId == MyPeerId) return;
        lock (_consecutiveDecryptFailures)
        {
            var failures = _consecutiveDecryptFailures.TryGetValue(peerId, out var f) ? f + 1 : 1;
            _consecutiveDecryptFailures[peerId] = f;
            if (failures < 3) return;
            _consecutiveDecryptFailures.Remove(peerId);
        }
        if (!_noise.HasEstablishedSession(peerId)) return;
        SystemMessage?.Invoke($"🔒 сессия с {peerId[..8]} устарела — переустанавливаю шифрование");
        _noise.RemoveSession(peerId);
        Task.Run(() => InitiateHandshakeGuarded(peerId));
    }

    private void HandleNoiseEncrypted(BitchatPacket packet, string peerId)
    {
        if (packet.RecipientId == null || ByteArrayExtensions.ToHexString(packet.RecipientId) != MyPeerId) return;

        try
        {
            var decrypted = _noise.Decrypt(packet.Payload, peerId);
            lock (_decryptFailuresLock) { _consecutiveDecryptFailures.Remove(peerId); }
            var payload = NoisePayload.Decode(decrypted);
            if (payload == null) return;

            switch (payload.Type)
            {
                case NoisePayloadType.PrivateMessage:
                {
                    var pm = PrivateMessagePacket.Decode(payload.Data);
                    if (pm == null) return;
                    string? nickname;
                    lock (_peersLock)
                    {
                        nickname = _peers.TryGetValue(peerId, out var p) ? p.Nickname : null;
                    }
                    lock (_receiptsLock)
                    {
                        _incomingMessages[pm.MessageId] = (peerId, false);
                    }

                    MessageReceived?.Invoke(new ChatMessage
                    {
                        Sender = nickname ?? peerId[..8],
                        SenderPeerId = peerId,
                        Content = pm.Content,
                        IsPrivate = true,
                        MessageId = pm.MessageId,
                        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds((long)packet.Timestamp)
                    });
                    SendDeliveryAck(pm.MessageId, peerId);
                    break;
                }
                case NoisePayloadType.Delivered:
                {
                    var messageId = Encoding.UTF8.GetString(payload.Data);
                    UpdateOutgoingStatus(messageId, peerId, "delivered");
                    break;
                }
                case NoisePayloadType.ReadReceipt:
                {
                    var messageId = Encoding.UTF8.GetString(payload.Data);
                    UpdateOutgoingStatus(messageId, peerId, "read");
                    break;
                }
                case NoisePayloadType.FileTransfer:
                {
                    HandleIncomingFile(payload.Data, peerId, isPrivate: true, packet.Timestamp);
                    break;
                }
                case NoisePayloadType.VoiceFrame:
                {
                    HandleVoiceFrameData(peerId, payload.Data, isPrivate: true, packet.Timestamp);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            RegisterDecryptFailure(peerId);
            if (DebugPackets)
                SystemMessage?.Invoke($"decrypt from {peerId[..8]} failed: {ex.Message}");
        }
    }

    private readonly object _decryptFailuresLock = new();

    private void HandleRequestSync(BitchatPacket packet, string fromPeerId)
    {
        var request = RequestSyncPacket.Decode(packet.Payload);
        if (request == null) return;
        Gossip.HandleRequestSync(fromPeerId, request);
    }

    private void HandleFragment(BitchatPacket packet, string fromPeerId, int rssi)
    {
        var fragment = FragmentPayload.Decode(packet.Payload);
        if (fragment == null || fragment.FragmentId.Length != FragmentPayload.FragmentIdSize) return;

        var key = $"{fromPeerId}-{Convert.ToHexString(fragment.FragmentId)}";
        BitchatPacket? reassembled = null;

        lock (_fragmentsLock)
        {
            if (!_fragmentSets.TryGetValue(key, out var set))
            {
                set = new FragmentSet(fragment.Total);
                _fragmentSets[key] = set;
            }
            set.Add(fragment.Index, fragment.Data);
            if (set.IsComplete)
            {
                reassembled = BinaryProtocol.Decode(set.Combine());
                _fragmentSets.Remove(key);
            }
        }

        if (reassembled != null)
        {
            OnPacketReceived(reassembled, fromPeerId, rssi);
        }
    }

    private void OnSessionEstablished(string peerId, byte[] remoteStaticKey)
    {
        // Bind the claimed peer ID to the authenticated static key.
        var derived = NoiseService.DerivePeerId(remoteStaticKey);
        if (derived != peerId)
        {
            SystemMessage?.Invoke($"handshake identity mismatch: claimed {peerId[..8]}, derived {derived[..8]}");
            _noise.RemoveSession(peerId);
            return;
        }

        SystemMessage?.Invoke($"🔒 secure session established with {peerId[..8]}");

        // Send AuthenticatedPeerState so iOS/Android know we support private media.
        Task.Run(() =>
        {
            try
            {
                var state = new AuthenticatedPeerState(PeerCapabilities.LocalSupported, _noise.GetSigningPublicKeyData());
                var encrypted = _noise.Encrypt(new NoisePayload(NoisePayloadType.PeerState, state.Encode()).Encode(), peerId);
                var pkt = new BitchatPacket
                {
                    Version = Bitchat.Windows.Mesh.MeshEngine.VersionForPayload(encrypted.Length),
                    Type = (byte)MessageType.NoiseEncrypted,
                    SenderId = HexToBytes(MyPeerId),
                    RecipientId = HexToBytes(peerId),
                    Payload = encrypted,
                    Ttl = BitchatConstants.MessageTtlHops
                };
                pkt.NowTimestamp();
                SignAndDispatch(pkt);
                if (DebugPackets) SystemMessage?.Invoke($"[peer-state] sent to {peerId[..8]}");
            }
            catch (Exception ex)
            {
                SystemMessage?.Invoke($"peer-state send failed: {ex.Message}");
            }
        });

        // Flush queued private messages.
        while (_pendingPrivateMessages.TryDequeue(out var pending) && pending.PeerId == peerId)
        {
            SendPrivateEncrypted(pending.Content, peerId, pending.MessageId);
        }
    }

    private void RelayIfNeeded(BitchatPacket packet, string fromPeerId)
    {
        var addressedToMe = packet.RecipientId != null &&
                            ByteArrayExtensions.ToHexString(packet.RecipientId) == MyPeerId;
        if (addressedToMe) return;
        if (packet.Ttl == 0) return;

        // Adaptive relay: small networks always relay (Android logic).
        var networkSize = GetPeersSnapshot().Count + 1;
        bool shouldRelay;
        if (packet.Ttl >= 4 || networkSize <= 10) shouldRelay = true;
        else if (networkSize <= 30) shouldRelay = Random.Shared.NextDouble() < 0.85;
        else if (networkSize <= 50) shouldRelay = Random.Shared.NextDouble() < 0.7;
        else if (networkSize <= 100) shouldRelay = Random.Shared.NextDouble() < 0.55;
        else shouldRelay = Random.Shared.NextDouble() < 0.4;

        if (!shouldRelay) return;

        var relayed = packet.CloneForSigning();
        relayed.Ttl = (byte)(packet.Ttl - 1);
        relayed.Signature = packet.Signature; // keep original signature
        relayed.WireCompressedBytes = packet.WireCompressedBytes;

        var encoded = EncodeForBle(relayed);
        if (encoded != null && encoded.Length <= BitchatConstants.FragmentSizeThreshold)
        {
            PacketOut?.Invoke(encoded);
        }
    }

    // ---- Maintenance ----

    private void UpdatePeerLastSeen(string peerId) => UpdatePeer(peerId, _ => { });

    private void Cleanup()
    {
        var cutoff = Now() - (long)StalePeerTimeout.TotalMilliseconds;
        var removed = new List<string>();
        lock (_peersLock)
        {
            foreach (var key in _peers.Where(kv => kv.Value.LastSeen < (ulong)cutoff).Select(kv => kv.Key).ToArray())
            {
                removed.Add(_peers[key].Nickname);
                _peers.Remove(key);
            }
        }
        if (removed.Count > 0)
        {
            foreach (var nickname in removed)
                SystemMessage?.Invoke($"* {nickname} timed out");
            PeersChanged?.Invoke(GetPeersSnapshot());
        }

        var fragmentCutoff = Now() - (long)BitchatConstants.FragmentTimeout.TotalMilliseconds;
        lock (_fragmentsLock)
        {
            foreach (var key in _fragmentSets.Where(kv => kv.Value.Created < fragmentCutoff).Select(kv => kv.Key).ToArray())
                _fragmentSets.Remove(key);
        }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private void UpdateOutgoingStatus(string messageId, string peerId, string status)
    {
        lock (_receiptsLock)
        {
            if (!_outgoingMessages.TryGetValue(messageId, out var entry)) return;
            if (entry.Status == "read") return; // terminal state
            _outgoingMessages[messageId] = (entry.PeerId, status);
        }
        OutgoingStatusChanged?.Invoke(messageId, peerId, status);
    }

    /// <summary>Send read receipts for all unread incoming PMs from the given peer.</summary>
    public void MarkIncomingRead(string peerId)
    {
        List<string> toConfirm;
        lock (_receiptsLock)
        {
            toConfirm = _incomingMessages
                .Where(kv => kv.Value.PeerId == peerId && !kv.Value.Read)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var id in toConfirm)
                _incomingMessages[id] = (peerId, true);
        }
        foreach (var messageId in toConfirm)
            SendReadReceipt(messageId, peerId);
    }

    private void SendReadReceipt(string messageId, string peerId)
    {
        try
        {
            var payload = new NoisePayload(NoisePayloadType.ReadReceipt, Encoding.UTF8.GetBytes(messageId));
            var encrypted = _noise.Encrypt(payload.Encode(), peerId);
            var packet = new BitchatPacket
            {
                Version = VersionForPayload(encrypted.Length),
                Type = (byte)MessageType.NoiseEncrypted,
                SenderId = HexToBytes(MyPeerId),
                RecipientId = HexToBytes(peerId),
                Payload = encrypted,
                Ttl = BitchatConstants.MessageTtlHops
            };
            packet.NowTimestamp();
            SignAndDispatch(packet);
        }
        catch { }
    }

    // ---- Media (images/files) ----

    public static string MediaDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "bitchat", "media");

    public const long MaxFileSizeBytes = 10L * 1024 * 1024 - 132L * 1024; // like Android

    /// <summary>Images are downscaled to 512px JPEG q85 like the mobile clients.</summary>
    private string PrepareImageForSend(string filePath)
    {
        if (!ImageUtils.IsImage(filePath)) return filePath;
        return ImageUtils.DownscaleForSend(filePath);
    }

    public static string? GuessMime(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".mp3" => "audio/mpeg",
            ".ogg" => "audio/ogg",
            ".wav" => "audio/wav",
            ".m4a" or ".aac" => "audio/aac",
            ".mp4" => "video/mp4",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            ".txt" => "text/plain",
            _ => "application/octet-stream"
        };
    }

    private string SaveIncomingMedia(string suggestedName, byte[] content)
    {
        Directory.CreateDirectory(MediaDirectory);
        var safeName = string.Join("_", suggestedName.Split(Path.GetInvalidFileNameChars())).Trim();
        if (safeName.Length == 0) safeName = "file";
        var name = $"{DateTime.Now:yyyyMMdd-HHmmss}_{safeName}";
        var path = Path.Combine(MediaDirectory, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    public void SendFileBroadcast(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return;
            var displayName = Path.GetFileName(filePath);
            filePath = PrepareImageForSend(filePath);
            var content = File.ReadAllBytes(filePath);
            if (content.Length > MaxFileSizeBytes)
            {
                SystemMessage?.Invoke($"файл больше {MaxFileSizeBytes / 1024 / 1024} МБ — не отправлен");
                return;
            }
            var file = new BitchatFilePacket(displayName, content.Length, GuessMime(filePath), content);
            var payload = file.Encode();
            if (payload.Length == 0) return;

            var packet = new BitchatPacket
            {
                Version = 2,
                Type = (byte)MessageType.FileTransfer,
                SenderId = HexToBytes(MyPeerId),
                RecipientId = BitchatConstants.BroadcastRecipient,
                Payload = payload,
                Ttl = BitchatConstants.MessageTtlHops
            };
            packet.NowTimestamp();
            var encodedLen = BinaryProtocol.Encode(packet, pad: false)?.Length ?? -1;
            SystemMessage?.Invoke($"→ file broadcast: {content.Length}B payload, wire {encodedLen}B");
            SignAndDispatch(packet);
            Gossip.OnPublicPacketSeen(packet);

            MessageReceived?.Invoke(new ChatMessage
            {
                Sender = _myNickname,
                SenderPeerId = MyPeerId,
                Content = displayName,
                FilePath = filePath,
                FileName = displayName,
                FileSizeBytes = content.Length,
                IsFromMe = true,
                Timestamp = DateTimeOffset.Now
            });
        }
        catch (Exception ex)
        {
            SystemMessage?.Invoke($"send file failed: {ex.Message}");
        }
    }

    public void SendFilePrivate(string recipientPeerId, string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return;
            var displayName = Path.GetFileName(filePath);
            filePath = PrepareImageForSend(filePath);
            var content = File.ReadAllBytes(filePath);
            if (content.Length > MaxFileSizeBytes)
            {
                SystemMessage?.Invoke($"файл больше {MaxFileSizeBytes / 1024 / 1024} МБ — не отправлен");
                return;
            }
            var file = new BitchatFilePacket(displayName, content.Length, GuessMime(filePath), content);
            var payload = new NoisePayload(NoisePayloadType.FileTransfer, file.Encode());
            var encrypted = _noise.Encrypt(payload.Encode(), recipientPeerId);

            var packet = new BitchatPacket
            {
                Version = VersionForPayload(encrypted.Length),
                Type = (byte)MessageType.NoiseEncrypted,
                SenderId = HexToBytes(MyPeerId),
                RecipientId = HexToBytes(recipientPeerId),
                Payload = encrypted,
                Ttl = BitchatConstants.MessageTtlHops
            };
            packet.NowTimestamp();
            SignAndDispatch(packet);

            MessageReceived?.Invoke(new ChatMessage
            {
                Sender = _myNickname,
                SenderPeerId = recipientPeerId,
                Content = Path.GetFileName(filePath),
                FilePath = filePath,
                FileName = Path.GetFileName(filePath),
                FileSizeBytes = content.Length,
                IsPrivate = true,
                IsFromMe = true,
                Timestamp = DateTimeOffset.Now
            });
        }
        catch (Exception ex)
        {
            SystemMessage?.Invoke($"send file failed: {ex.Message}");
        }
    }

    private void HandleIncomingFile(byte[] fileBytes, string peerId, bool isPrivate, ulong timestamp)
    {
        if (DebugPackets)
            SystemMessage?.Invoke($"[file] incoming {fileBytes.Length}B from {peerId[..8]} private={isPrivate}");
        var file = BitchatFilePacket.Decode(fileBytes);
        if (file == null)
        {
            SystemMessage?.Invoke($"файл от {peerId[..8]} повреждён ({fileBytes.Length}B)");
            return;
        }
        var savedPath = SaveIncomingMedia(file.FileName, file.Content);
        var messageId = PacketIdUtil.ComputeIdHex(packet: new BitchatPacket
        {
            Type = (byte)MessageType.FileTransfer,
            SenderId = HexToBytes(peerId),
            Timestamp = timestamp,
            Payload = fileBytes
        });

        lock (_receiptsLock)
        {
            _incomingMessages[messageId] = (peerId, false);
        }

        MessageReceived?.Invoke(new ChatMessage
        {
            Sender = NicknameFor(peerId),
            SenderPeerId = peerId,
            Content = file.FileName,
            FilePath = savedPath,
            FileName = file.FileName,
            FileSizeBytes = file.Content.Length,
            MessageId = messageId,
            IsPrivate = isPrivate,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds((long)timestamp)
        });

        if (isPrivate) SendDeliveryAck(messageId, peerId);
    }

    private string NicknameFor(string peerId)
    {
        lock (_peersLock)
        {
            return _peers.TryGetValue(peerId, out var p) ? p.Nickname : peerId[..8];
        }
    }

    /// <summary>v1 payload length is u16; larger payloads need a v2 packet (like Android).</summary>
    // ---- Voice (PTT) ----

    public Voice.VoiceService Voice { get; }

    public void StartVoiceRecording() => Voice.StartRecording();

    public long StopVoiceRecording() => _isPrivateVoice switch
    {
        true when _voicePrivatePeer != null => Voice.StopRecordingAndSend(true, _voicePrivatePeer),
        _ => Voice.StopRecordingAndSend(false, null)
    };

    private bool _isPrivateVoice;
    private string? _voicePrivatePeer;

    /// <summary>Voice recording goes to the currently selected conversation.</summary>
    public void SetVoiceTarget(bool isPrivate, string? privatePeerId)
    {
        _isPrivateVoice = isPrivate;
        _voicePrivatePeer = privatePeerId;
    }

    public IReadOnlyList<string> EstablishedPeerIds => _noise.GetEstablishedPeerIds();

    private void SendVoiceBurstBroadcast(byte[] burstBytes)
    {
        var packet = new BitchatPacket
        {
            Version = 1,
            Type = (byte)MessageType.VoiceFrame,
            SenderId = HexToBytes(MyPeerId),
            RecipientId = BitchatConstants.BroadcastRecipient,
            Payload = burstBytes,
            Ttl = BitchatConstants.MessageTtlHops
        };
        packet.NowTimestamp();
        SignAndDispatch(packet);
    }

private bool SendVoiceBurstPrivate(string peerId, byte[] burstBytes)
    {
        if (!_noise.HasEstablishedSession(peerId))
        {
            SystemMessage?.Invoke($"нет шифрованной сессии с {peerId[..8]} — переустанавливаю, отправь голосовое после «🔒»");
            InitiateHandshakeGuarded(peerId);
            return false;
        }
        try
        {
            var encrypted = _noise.Encrypt(new NoisePayload(NoisePayloadType.VoiceFrame, burstBytes).Encode(), peerId);
            var packet = new BitchatPacket
            {
                Version = 1,
                Type = (byte)MessageType.NoiseEncrypted,
                SenderId = HexToBytes(MyPeerId),
                RecipientId = HexToBytes(peerId),
                Payload = encrypted,
                Ttl = BitchatConstants.MessageTtlHops
            };
            packet.NowTimestamp();
            SignAndDispatch(packet);
            return true;
        }
        catch { return false; }
    }

    private void HandleVoiceFrameData(string peerId, byte[] burstBytes, bool isPrivate, ulong timestamp)
    {
        var nickname = NicknameFor(peerId);
        Voice.HandleFrame(peerId, nickname, burstBytes, (long)timestamp, isPrivate);
    }

    public static byte VersionForPayload(int payloadLength) =>
        payloadLength > 0xFFFF ? (byte)2 : (byte)1;

    public static byte[] HexToBytes(string hex)
    {
        var result = new byte[8];
        var index = 0;
        var temp = hex;
        while (temp.Length >= 2 && index < 8)
        {
            if (byte.TryParse(temp[..2], System.Globalization.NumberStyles.HexNumber, null, out var b))
                result[index] = b;
            temp = temp[2..];
            index++;
        }
        return result;
    }

    private sealed class FragmentSet
    {
        public readonly long Created = Now();
        private readonly byte[][] _parts;
        private int _received;

        public FragmentSet(int total) => _parts = new byte[total][];

        public void Add(int index, byte[] data)
        {
            if (_parts[index] == null) _received++;
            _parts[index] = data;
        }

        public bool IsComplete => _received == _parts.Length;

        public byte[] Combine()
        {
            var total = _parts.Sum(p => p.Length);
            var result = new byte[total];
            var offset = 0;
            foreach (var part in _parts)
            {
                Array.Copy(part, 0, result, offset, part.Length);
                offset += part.Length;
            }
            return result;
        }
    }
}

/// <summary>Canonical announcement validation (AnnouncementIdentityValidator compatible).</summary>
public static class AnnouncementValidator
{
    public static IdentityAnnouncement? Verify(NoiseService noise, BitchatPacket packet, string claimedPeerId)
    {
        if (packet.Type != (byte)MessageType.Announce) return null;

        var now = Now();
        var skew = packet.Timestamp >= now ? packet.Timestamp - now : now - packet.Timestamp;
        if (skew > (ulong)BitchatConstants.AnnounceClockSkewTolerance.TotalMilliseconds) return null;

        var announcement = IdentityAnnouncement.Decode(packet.Payload);
        if (announcement == null || announcement.SigningPublicKey.Length != 32) return null;

        var derivedPeerId = NoiseService.DerivePeerId(announcement.NoisePublicKey);
        if (ByteArrayExtensions.ToHexString(packet.SenderId) != derivedPeerId || claimedPeerId != derivedPeerId)
            return null;

        if (packet.Signature == null) return null;
        var canonicalData = BinaryProtocol.Encode(packet.CloneForSigning(), pad: true);
        if (canonicalData == null) return null;

        return NoiseService.VerifySignature(packet.Signature, canonicalData, announcement.SigningPublicKey)
            ? announcement
            : null;
    }

    private static ulong Now() => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
