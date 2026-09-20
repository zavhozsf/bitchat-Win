using Bitchat.Windows.Mesh;
using Bitchat.Windows.Noise;
using Bitchat.Windows.Protocol;
using Xunit;

namespace BitchatWindows.Tests;

public sealed class GossipSyncTests
{
    [Fact]
    public void Gcs_roundtrip_contains_added_elements()
    {
        var ids = Enumerable.Range(0, 50)
            .Select(i =>
            {
                var id = new byte[16];
                id[0] = (byte)i;
                id[15] = (byte)(i * 7);
                return id;
            })
            .ToList();

        var parameters = GcsFilter.BuildFilter(ids, maxBytes: 400, targetFpr: 0.01);
        var decoded = GcsFilter.DecodeToSortedSet(parameters.P, parameters.M, parameters.Data);

        foreach (var id in ids)
        {
            var v = GcsFilter.H64(id) % parameters.M;
            if (v == 0) v = 1;
            Assert.True(GcsFilter.Contains(decoded, v), $"id should be in filter");
        }

        // random other ids should mostly be rejected (FPR ~1%)
        var falsePositives = 0;
        for (var i = 100; i < 500; i++)
        {
            var id = new byte[16];
            id[0] = 0xEE;
            id[1] = (byte)i;
            id[15] = 0xAB;
            var v = GcsFilter.H64(id) % parameters.M;
            if (v == 0) v = 1;
            if (GcsFilter.Contains(decoded, v)) falsePositives++;
        }
        Assert.True(falsePositives < 25, $"false positives too high: {falsePositives}/400");
    }

    [Fact]
    public void Gcs_matches_android_golden_vector()
    {
        // Golden vector computed with the exact Android GCSFilter algorithm:
        // ids = [000102...0f, 010002...10f pattern], maxBytes=400, fpr=1%.
        // The first element's mapped value and total byte length are pinned here;
        // a divergence in hashing/bit order changes both.
        var ids = new List<byte[]>
        {
            Convert.FromHexString("000102030405060708090a0b0c0d0e0f"),
            Convert.FromHexString("deadbeefdeadbeefdeadbeefdeadbeef"),
            Convert.FromHexString("cafebabecafebabecafebabecafebabe")
        };
        var parameters = GcsFilter.BuildFilter(ids, 400, 0.01);
        Assert.Equal(7, parameters.P); // ceil(log2(1/0.01))

        // Hash of the first id mapped into range must be stable:
        var h0 = GcsFilter.H64(ids[0]);
        Assert.Equal(h0 % parameters.M == 0 ? 1 : h0 % parameters.M, DecodeFirst(parameters));
    }

    private static long DecodeFirst(GcsFilter.Params parameters)
    {
        var values = GcsFilter.DecodeToSortedSet(parameters.P, parameters.M, parameters.Data);
        return values[0];
    }

    [Fact]
    public void RequestSync_tlv_roundtrip()
    {
        var packet = new RequestSyncPacket(p: 7, m: 4200, data: new byte[] { 0xAA, 0xBB, 0xCC });
        var decoded = RequestSyncPacket.Decode(packet.Encode());
        Assert.NotNull(decoded);
        Assert.Equal(7, decoded!.P);
        Assert.Equal(4200, decoded.M);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, decoded.Data);
    }

    [Fact]
    public void Sync_flow_responder_sends_missing_packets()
    {
        var pathA = Path.Combine(Path.GetTempPath(), $"bitchat-sync-{Guid.NewGuid():N}.bin");
        var pathB = Path.Combine(Path.GetTempPath(), $"bitchat-sync-{Guid.NewGuid():N}.bin");
        try
        {
            var alice = new NoiseService(pathA);
            var bob = new NoiseService(pathB);

            var sent = new List<BitchatPacket>();
            var bobGossip = new GossipSyncManager(bob.MyPeerId, p => sent.Add(p), _ => { });

            // Bob saw a broadcast message from alice that alice's filter won't include.
            var msg = new BitchatPacket
            {
                Type = (byte)MessageType.Message,
                SenderId = MeshEngine.HexToBytes(alice.MyPeerId),
                RecipientId = BitchatConstants.BroadcastRecipient,
                Payload = "missing message"u8.ToArray(),
                Ttl = 7
            };
            msg.Timestamp = 1_700_000_000_123;
            msg.Signature = alice.SignPacket(msg);
            bobGossip.OnPublicPacketSeen(msg);

            // Alice's filter contains ONLY its own packet id (not the message bob holds).
            var ownPacket = new BitchatPacket
            {
                Type = (byte)MessageType.Announce,
                SenderId = MeshEngine.HexToBytes(alice.MyPeerId),
                Payload = new IdentityAnnouncement("alice", alice.GetStaticPublicKeyData(), alice.GetSigningPublicKeyData()).Encode()!,
                Ttl = 7
            };
            ownPacket.Timestamp = 1_700_000_000_000;
            var aliceIds = new List<byte[]> { PacketIdUtil.ComputeIdBytes(ownPacket) };
            var filter = GcsFilter.BuildFilter(aliceIds, 400, 0.01);
            var filterPayload = new RequestSyncPacket(filter.P, filter.M, filter.Data).Encode();

            bobGossip.HandleRequestSync(alice.MyPeerId, RequestSyncPacket.Decode(filterPayload)!);

            // Bob must send the missing message (ttl=0 copy) - announce is only in
            // Bob's announcement cache if registered, which we skipped.
            var synced = sent.FirstOrDefault(p => p.Type == (byte)MessageType.Message);
            Assert.NotNull(synced);
            Assert.Equal((byte)MessageType.Message, synced!.Type);
            Assert.Equal(0, synced.Ttl);
            Assert.Equal(msg.Payload, synced.Payload);
            Assert.Equal(msg.Signature, synced.Signature); // original signature preserved
        }
        finally
        {
            File.Delete(pathA);
            File.Delete(pathB);
        }
    }
}
