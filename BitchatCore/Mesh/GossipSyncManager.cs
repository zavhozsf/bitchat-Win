using System.IO;

using System.Collections.Concurrent;
using Bitchat.Windows.Protocol;

namespace Bitchat.Windows.Mesh;

/// <summary>
/// Gossip-based synchronization with on-demand GCS filters - compatible port of the
/// Android/iOS GossipSyncManager. Tracks seen public packets (ANNOUNCE, broadcast
/// MESSAGE), periodically broadcasts REQUEST_SYNC and answers with packets the
/// requester lacks (TTL=0, neighbor-only).
/// </summary>
public sealed class GossipSyncManager : IDisposable
{
    public const int SeenCapacity = 500;
    public const int GcsMaxBytes = 400;
    public const double GcsTargetFpr = 1.0; // percent

    private readonly string _myPeerId;
    private readonly Action<BitchatPacket> _sendSigned;
    private readonly Action<string> _log;

    // broadcast messages: up to SeenCapacity most recent, keyed by packet id
    private readonly object _messagesLock = new();
    private readonly Dictionary<string, BitchatPacket> _messages = new();
    private readonly Queue<string> _messageOrder = new();

    // latest announcement per sender peerID
    private readonly object _announcementsLock = new();
    private readonly Dictionary<string, (string Id, BitchatPacket Packet)> _latestAnnouncementByPeer = new();

    private readonly Timer _periodicTimer;
    private readonly Timer _cleanupTimer;

    public GossipSyncManager(string myPeerId, Action<BitchatPacket> sendSigned, Action<string> log)
    {
        _myPeerId = myPeerId;
        _sendSigned = sendSigned;
        _log = log;

        _periodicTimer = new Timer(_ => Safe(SendRequestSync), null,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        _cleanupTimer = new Timer(_ => Safe(PruneStale), null,
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    private void Safe(Action action)
    {
        try { action(); } catch (Exception ex) { _log($"sync error: {ex.Message}"); }
    }

    public void ScheduleInitialSync(int delayMs = 5000)
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(delayMs);
            Safe(SendRequestSync);
        });
    }

    // ---- Tracking ----

    public void OnPublicPacketSeen(BitchatPacket packet)
    {
        var type = (MessageType)packet.Type;
        var isBroadcastMessage = type == MessageType.Message &&
                                 (packet.RecipientId == null || packet.RecipientId.AsSpan().SequenceEqual(BitchatConstants.BroadcastRecipient));
        var isAnnouncement = type == MessageType.Announce;
        if (!isBroadcastMessage && !isAnnouncement) return;

        var idBytes = PacketIdUtil.ComputeIdBytes(packet);
        var id = Convert.ToHexString(idBytes).ToLowerInvariant();

        if (isBroadcastMessage)
        {
            lock (_messagesLock)
            {
                if (!_messages.TryAdd(id, packet))
                {
                    _messages[id] = packet; // LinkedHashMap.put keeps original position
                }
                else
                {
                    _messageOrder.Enqueue(id);
                }
                while (_messages.Count > SeenCapacity)
                {
                    if (_messageOrder.TryDequeue(out var oldest))
                        _messages.Remove(oldest);
                    else
                        break;
                }
            }
        }
        else
        {
            var age = Now() - (long)packet.Timestamp;
            if (age > (long)TimeSpan.FromMinutes(3).TotalMilliseconds)
                return; // stale announcement

            var sender = Convert.ToHexString(packet.SenderId).ToLowerInvariant();
            lock (_announcementsLock)
            {
                _latestAnnouncementByPeer[sender] = (id, packet);
                while (_latestAnnouncementByPeer.Count > SeenCapacity)
                {
                    string? oldest = null;
                    ulong oldestTs = ulong.MaxValue;
                    foreach (var (key, (_, pkt)) in _latestAnnouncementByPeer)
                    {
                        if (pkt.Timestamp < oldestTs)
                        {
                            oldestTs = pkt.Timestamp;
                            oldest = key;
                        }
                    }
                    if (oldest == null) break;
                    _latestAnnouncementByPeer.Remove(oldest);
                }
            }
        }
    }

    // ---- Requests ----

    public void SendRequestSync()
    {
        var payload = BuildGcsPayload();
        var packet = new BitchatPacket
        {
            Type = (byte)MessageType.RequestSync,
            SenderId = MeshEngine.HexToBytes(_myPeerId),
            Payload = payload,
            Ttl = BitchatConstants.SyncTtlHops // neighbors only
        };
        packet.NowTimestamp();
        _sendSigned(packet);
    }

    public void HandleRequestSync(string fromPeerId, RequestSyncPacket request)
    {
        var sorted = GcsFilter.DecodeToSortedSet(request.P, request.M, request.Data);
        bool MightContain(byte[] idBytes)
        {
            var v = GcsFilter.H64(idBytes) % request.M;
            if (v == 0) v = 1;
            return GcsFilter.Contains(sorted, v);
        }

        // 1) latest announcements per peer
        List<BitchatPacket> toSend = new();
        lock (_announcementsLock)
        {
            foreach (var (_, (_, pkt)) in _latestAnnouncementByPeer)
            {
                var idBytes = PacketIdUtil.ComputeIdBytes(pkt);
                if (!MightContain(idBytes))
                {
                    var copy = CloneWithTtl(pkt, BitchatConstants.SyncTtlHops);
                    toSend.Add(copy);
                }
            }
        }

        // 2) broadcast messages
        lock (_messagesLock)
        {
            foreach (var pkt in _messages.Values)
            {
                var idBytes = PacketIdUtil.ComputeIdBytes(pkt);
                if (!MightContain(idBytes))
                {
                    toSend.Add(CloneWithTtl(pkt, BitchatConstants.SyncTtlHops));
                }
            }
        }

        foreach (var pkt in toSend)
            _sendSigned(pkt);

        if (toSend.Count > 0)
            _log($"sync: sent {toSend.Count} packets to {fromPeerId[..Math.Min(8, fromPeerId.Length)]}");
    }

    // ---- Payload building ----

    private byte[] BuildGcsPayload()
    {
        var list = new List<BitchatPacket>();
        lock (_announcementsLock)
        {
            foreach (var (_, (_, pkt)) in _latestAnnouncementByPeer)
                list.Add(pkt);
        }
        lock (_messagesLock)
        {
            list.AddRange(_messages.Values);
        }

        list.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));

        var p = GcsFilter.DeriveP(GcsTargetFpr);
        var nMax = GcsFilter.EstimateMaxElementsForSize(GcsMaxBytes, p);
        var takeN = Math.Min(Math.Min(nMax, SeenCapacity), list.Count);
        if (takeN <= 0)
            return new RequestSyncPacket(p, 1, Array.Empty<byte>()).Encode();

        var ids = list.Take(takeN).Select(PacketIdUtil.ComputeIdBytes).ToList();
        var parameters = GcsFilter.BuildFilter(ids, GcsMaxBytes, GcsTargetFpr);
        var m = parameters.M <= 0 ? 1 : parameters.M;
        return new RequestSyncPacket(parameters.P, m, parameters.Data).Encode();
    }

    // ---- Maintenance ----

    private void PruneStale()
    {
        var now = Now();
        var stalePeers = new List<string>();
        lock (_announcementsLock)
        {
            foreach (var (peerId, (_, pkt)) in _latestAnnouncementByPeer)
            {
                if (now - (long)pkt.Timestamp > (long)TimeSpan.FromMinutes(3).TotalMilliseconds)
                    stalePeers.Add(peerId);
            }
            foreach (var peerId in stalePeers)
                _latestAnnouncementByPeer.Remove(peerId);
        }

        if (stalePeers.Count == 0) return;

        lock (_messagesLock)
        {
            var toRemove = _messages
                .Where(kv => stalePeers.Contains(Convert.ToHexString(kv.Value.SenderId).ToLowerInvariant()))
                .Select(kv => kv.Key)
                .ToList();
            foreach (var id in toRemove)
                _messages.Remove(id);
        }
    }

    private static BitchatPacket CloneWithTtl(BitchatPacket pkt, byte ttl)
    {
        return new BitchatPacket
        {
            Version = pkt.Version,
            Type = pkt.Type,
            Ttl = ttl,
            Timestamp = pkt.Timestamp,
            SenderId = pkt.SenderId,
            RecipientId = pkt.RecipientId,
            Payload = pkt.Payload,
            Signature = pkt.Signature,
            WireCompressedBytes = pkt.WireCompressedBytes
        };
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public void Dispose()
    {
        _periodicTimer.Dispose();
        _cleanupTimer.Dispose();
    }
}
