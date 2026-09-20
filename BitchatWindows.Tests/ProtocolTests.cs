using Bitchat.Windows.Mesh;
using Bitchat.Windows.Noise;
using Bitchat.Windows.Protocol;
using Xunit;

namespace BitchatWindows.Tests;

public sealed class BinaryProtocolTests
{
    [Fact]
    public void Packet_roundtrip_with_recipient_and_signature()
    {
        var packet = new BitchatPacket
        {
            Version = 1,
            Type = (byte)MessageType.Message,
            SenderId = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
            RecipientId = new byte[] { 9, 8, 7, 6, 5, 4, 3, 2 },
            Payload = "hello mesh"u8.ToArray(),
            Ttl = 7
        };
        packet.Timestamp = 1_700_000_000_000;

        var encoded = BinaryProtocol.Encode(packet, pad: false);
        Assert.NotNull(encoded);

        var decoded = BinaryProtocol.Decode(encoded!);
        Assert.NotNull(decoded);
        Assert.Equal(packet.Type, decoded!.Type);
        Assert.Equal(packet.Ttl, decoded.Ttl);
        Assert.Equal(packet.Timestamp, decoded.Timestamp);
        Assert.Equal(packet.SenderId, decoded.SenderId);
        Assert.Equal(packet.RecipientId, decoded.RecipientId);
        Assert.Equal(packet.Payload, decoded.Payload);
    }

    [Fact]
    public void Noise_frames_are_padded_to_block_boundaries()
    {
        var packet = new BitchatPacket
        {
            Type = (byte)MessageType.NoiseEncrypted,
            SenderId = new byte[8],
            Payload = new byte[80],
            Ttl = 7
        };
        packet.Timestamp = 42;
        var encoded = BinaryProtocol.Encode(packet, pad: true);
        Assert.NotNull(encoded);
        Assert.Equal(256, encoded!.Length);

        var decoded = BinaryProtocol.Decode(encoded);
        Assert.NotNull(decoded);
        Assert.Equal(80, decoded!.Payload.Length);

        // Non-noise frames stay unpadded when sent through the BLE policy
        var announce = new BitchatPacket
        {
            Type = (byte)MessageType.Announce,
            SenderId = new byte[8],
            Payload = new byte[80],
            Ttl = 7
        };
        announce.Timestamp = 42;
        var announceEncoded = BinaryProtocol.Encode(announce, pad: BinaryProtocol.ShouldPadForBle(announce.Type));
        Assert.NotNull(announceEncoded);
        Assert.True(announceEncoded!.Length < 150);

        // But noise frames go through padded
        var noisePacket = new BitchatPacket
        {
            Type = (byte)MessageType.NoiseHandshake,
            SenderId = new byte[8],
            Payload = new byte[80],
            Ttl = 7
        };
        noisePacket.Timestamp = 42;
        var noiseEncoded = BinaryProtocol.Encode(noisePacket, pad: BinaryProtocol.ShouldPadForBle(noisePacket.Type));
        Assert.Equal(256, noiseEncoded!.Length);
    }

    [Fact]
    public void Compression_roundtrip()
    {
        var data = new byte[500];
        new Random(1).NextBytes(data);
        // make it compressible
        for (var i = 0; i < 500; i++) data[i] = (byte)(i % 4);

        var compressed = BinaryProtocol.Compress(data);
        Assert.NotNull(compressed);
        Assert.True(compressed!.Length < data.Length);
        var decompressed = BinaryProtocol.Decompress(compressed, data.Length);
        Assert.Equal(data, decompressed);
    }

    [Fact]
    public void Fragment_payload_roundtrip()
    {
        var fragment = new FragmentPayload(
            FragmentPayload.GenerateFragmentId(), index: 3, total: 7,
            originalType: (byte)MessageType.Message, data: new byte[] { 0xAA, 0xBB });
        var decoded = FragmentPayload.Decode(fragment.Encode());
        Assert.NotNull(decoded);
        Assert.Equal(fragment.Index, decoded!.Index);
        Assert.Equal(fragment.Total, decoded.Total);
        Assert.Equal(fragment.OriginalType, decoded.OriginalType);
        Assert.Equal(fragment.Data, decoded.Data);
        Assert.Equal(fragment.FragmentId, decoded.FragmentId);
    }
}

public sealed class NoiseServiceTests
{
    private static string TempIdentityPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bitchat-test-{Guid.NewGuid():N}.bin");
        return path;
    }

    [Fact]
    public void Identity_is_persistent_and_peer_id_stable()
    {
        var path = TempIdentityPath();
        try
        {
            var noise1 = new NoiseService(path);
            var noise2 = new NoiseService(path);
            Assert.Equal(noise1.MyPeerId, noise2.MyPeerId);
            Assert.Equal(16, noise1.MyPeerId.Length);
            Assert.Equal(noise1.GetSigningPublicKeyData(), noise2.GetSigningPublicKeyData());
            Assert.Equal(noise1.GetStaticPublicKeyData(), noise2.GetStaticPublicKeyData());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Full_handshake_and_private_message_roundtrip_between_two_identities()
    {
        var pathA = TempIdentityPath();
        var pathB = TempIdentityPath();
        try
        {
            var alice = new NoiseService(pathA);
            var bob = new NoiseService(pathB);

            // Alice initiates
            var m1 = alice.InitiateHandshake(bob.MyPeerId);
            Assert.Equal(32, m1!.Length);

            // Bob responds
            var result1 = bob.ProcessHandshakeMessage(m1, alice.MyPeerId);
            Assert.NotNull(result1.Response);
            Assert.Equal(96, result1.Response!.Length);

            // Alice completes
            var result2 = alice.ProcessHandshakeMessage(result1.Response, bob.MyPeerId);
            Assert.NotNull(result2.Response);
            Assert.Equal(64, result2.Response!.Length);
            Assert.True(result2.EstablishedNow);

            // Bob finishes
            var result3 = bob.ProcessHandshakeMessage(result2.Response, alice.MyPeerId);
            Assert.Null(result3.Response);
            Assert.True(result3.EstablishedNow);

            // Peer ID binding
            Assert.Equal(bob.MyPeerId, NoiseService.DerivePeerId(result2.RemoteStaticKey!));
            Assert.Equal(alice.MyPeerId, NoiseService.DerivePeerId(result3.RemoteStaticKey!));

            // Private message roundtrip
            var pm = new PrivateMessagePacket("MSG-1", "привет, mesh!");
            var tlv = pm.Encode()!;
            var payload = new NoisePayload(NoisePayloadType.PrivateMessage, tlv).Encode();
            var encrypted = alice.Encrypt(payload, bob.MyPeerId);
            var decrypted = bob.Decrypt(encrypted, alice.MyPeerId);
            var decodedPayload = NoisePayload.Decode(decrypted);
            Assert.NotNull(decodedPayload);
            Assert.Equal(NoisePayloadType.PrivateMessage, decodedPayload!.Type);
            var decodedPm = PrivateMessagePacket.Decode(decodedPayload.Data);
            Assert.Equal("привет, mesh!", decodedPm!.Content);
        }
        finally
        {
            File.Delete(pathA);
            File.Delete(pathB);
        }
    }

    [Fact]
    public void Signed_announcement_passes_validation()
    {
        var path = TempIdentityPath();
        try
        {
            var noise = new NoiseService(path);
            var announcement = IdentityAnnouncement.ForLocalPeer(
                "windows-user", noise.GetStaticPublicKeyData(), noise.GetSigningPublicKeyData());
            var payload = announcement.Encode()!;

            var packet = new BitchatPacket
            {
                Type = (byte)MessageType.Announce,
                SenderId = MeshEngine.HexToBytes(noise.MyPeerId),
                Payload = payload,
                Ttl = 7
            };
            packet.Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            packet.Signature = noise.SignPacket(packet);

            Assert.NotNull(packet.Signature);
            Assert.Equal(64, packet.Signature!.Length);

            var validated = AnnouncementValidator.Verify(noise, packet, noise.MyPeerId);
            Assert.NotNull(validated);
            Assert.Equal("windows-user", validated!.Nickname);

            // Tampered payload must fail
            var tampered = new BitchatPacket
            {
                Type = packet.Type,
                SenderId = packet.SenderId,
                Payload = "tampered"u8.ToArray(),
                Ttl = packet.Ttl,
                Timestamp = packet.Timestamp,
                Signature = packet.Signature
            };
            Assert.Null(AnnouncementValidator.Verify(noise, tampered, noise.MyPeerId));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Signed_broadcast_message_verifies_and_survives_relay_ttl_change()
    {
        var path = TempIdentityPath();
        try
        {
            var noise = new NoiseService(path);
            var packet = new BitchatPacket
            {
                Type = (byte)MessageType.Message,
                SenderId = MeshEngine.HexToBytes(noise.MyPeerId),
                RecipientId = BitchatConstants.BroadcastRecipient,
                Payload = "relay me"u8.ToArray(),
                Ttl = 7
            };
            packet.Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            packet.Signature = noise.SignPacket(packet);

            // Relay: clone with TTL-1 keeps the same signature over canonical TTL=7 preimage.
            var relayed = packet.CloneForSigning();
            relayed.Ttl = 6;
            relayed.Signature = packet.Signature;
            var encoded = BinaryProtocol.Encode(relayed, pad: false);
            var decoded = BinaryProtocol.Decode(encoded!);

            Assert.True(noise.VerifyPacketSignature(decoded!, noise.GetSigningPublicKeyData()));

            // Corrupt payload -> invalid
            var corrupted = decoded!.CloneForSigning();
            corrupted.Payload = "corrupt"u8.ToArray();
            Assert.False(noise.VerifyPacketSignature(corrupted, noise.GetSigningPublicKeyData()));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
