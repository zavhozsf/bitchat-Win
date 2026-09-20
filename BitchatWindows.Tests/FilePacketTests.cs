using Bitchat.Windows;
using Bitchat.Windows.Noise;
using Bitchat.Windows.Protocol;
using Xunit;

namespace BitchatWindows.Tests;

public sealed class FilePacketTests
{
    [Fact]
    public void Tlv_layout_matches_android()
    {
        var content = new byte[] { 1, 2, 3, 4, 5 };
        var packet = new BitchatFilePacket("photo.png", content.Length, "image/png", content);
        var encoded = packet.Encode();

        // TLV: 0x01 u16len name | 0x02 u16len=4 size | 0x03 u16len mime | 0x04 u32len content
        var expectedTotal = (1 + 2 + "photo.png".Length) + (1 + 2 + 4) + (1 + 2 + "image/png".Length) + (1 + 4 + 5);
        Assert.Equal(expectedTotal, encoded.Length);

        // name TLV
        Assert.Equal(0x01, encoded[0]);
        var nameLen = (encoded[1] << 8) | encoded[2];
        Assert.Equal("photo.png".Length, nameLen);

        // size TLV: 4-byte value (u16 length field = 0x0004)
        var sizeOff = 1 + 2 + nameLen;
        Assert.Equal(0x02, encoded[sizeOff]);
        Assert.Equal(0, encoded[sizeOff + 1]); // len high byte
        Assert.Equal(4, encoded[sizeOff + 2]); // len low byte

        // content TLV: 4-byte length
        var contentOff = sizeOff + 1 + 2 + 4 + 1 + 2 + "image/png".Length;
        Assert.Equal(0x04, encoded[contentOff]);

        var decoded = BitchatFilePacket.Decode(encoded);
        Assert.NotNull(decoded);
        Assert.Equal("photo.png", decoded!.FileName);
        Assert.Equal(content.Length, decoded.FileSize);
        Assert.Equal("image/png", decoded.MimeType);
        Assert.Equal(content, decoded.Content);
    }

    [Fact]
    public void Large_file_single_content_tlv()
    {
        var content = new byte[70_000]; // > 65535 — one CONTENT TLV with 4-byte length
        new Random(1).NextBytes(content);
        var packet = new BitchatFilePacket("big.bin", content.Length, "application/octet-stream", content);
        var decoded = BitchatFilePacket.Decode(packet.Encode());
        Assert.NotNull(decoded);
        Assert.Equal(content.Length, decoded!.Content.Length);
        Assert.Equal(content, decoded.Content);
    }

    [Fact]
    public void Roundtrip_through_binary_protocol_v2()
    {
        var content = new byte[2000];
        new Random(2).NextBytes(content);
        var file = new BitchatFilePacket("img.jpg", content.Length, "image/jpeg", content);
        var packet = new BitchatPacket
        {
            Version = 2,
            Type = (byte)MessageType.FileTransfer,
            SenderId = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
            RecipientId = BitchatConstants.BroadcastRecipient,
            Payload = file.Encode(),
            Ttl = 7
        };
        packet.Timestamp = 1_700_000_000_000;

        var encoded = BinaryProtocol.Encode(packet, pad: false);
        Assert.NotNull(encoded);

        var decoded = BinaryProtocol.Decode(encoded);
        Assert.NotNull(decoded);
        Assert.Equal(2, decoded!.Version);
        var decodedFile = BitchatFilePacket.Decode(decoded.Payload);
        Assert.NotNull(decodedFile);
        Assert.Equal("img.jpg", decodedFile!.FileName);
        Assert.Equal(content, decodedFile.Content);
    }
}

public sealed class FileSendTests
{
    [Fact]
    public void Broadcast_file_packet_signs_and_encodes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bitchat-fs-{Guid.NewGuid():N}.bin");
        try
        {
            var noise = new NoiseService(path);
            var content = new byte[1200];
            new Random(3).NextBytes(content);
            var file = new BitchatFilePacket("photo.png", content.Length, "image/png", content);

            var packet = new BitchatPacket
            {
                Version = 2,
                Type = (byte)MessageType.FileTransfer,
                SenderId = new byte[] { 9, 8, 7, 6, 5, 4, 3, 2 },
                RecipientId = BitchatConstants.BroadcastRecipient,
                Payload = file.Encode(),
                Ttl = 7
            };
            packet.Timestamp = 1_700_000_000_000;
            packet.Signature = noise.SignPacket(packet);
            Assert.True(packet.Signature != null, $"SignPacket failed; payload={packet.Payload.Length}");

            var encoded = BinaryProtocol.Encode(packet, pad: false);
            Assert.NotNull(encoded);
            var decoded = BinaryProtocol.Decode(encoded!);
            Assert.NotNull(decoded);
            Assert.True(noise.VerifyPacketSignature(decoded!, noise.GetSigningPublicKeyData()));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Broadcast_file_packet_signs_with_real_sizes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bitchat-big-{Guid.NewGuid():N}.bin");
        try
        {
            var noise = new NoiseService(path);
            foreach (var size in new[] { 400, 4_000, 400_000, 2_000_000 })
            {
                var content = new byte[size];
                new Random(size).NextBytes(content);
                var file = new BitchatFilePacket("IMG_2026.JPG", content.Length, "image/jpeg", content);
                var packet = new BitchatPacket
                {
                    Version = 2,
                    Type = (byte)MessageType.FileTransfer,
                    SenderId = new byte[] { 9, 8, 7, 6, 5, 4, 3, 2 },
                    RecipientId = BitchatConstants.BroadcastRecipient,
                    Payload = file.Encode(),
                    Ttl = 7
                };
                packet.Timestamp = 1_700_000_000_000;
                packet.Signature = noise.SignPacket(packet);
                Assert.True(packet.Signature != null, $"SignPacket failed at payload size {size}; wirePayload={packet.WireCompressedBytes?.Length.ToString() ?? "none"}");
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Private_file_over_64kb_uses_v2_and_roundtrips()
    {
        var pathA = Path.Combine(Path.GetTempPath(), $"bitchat-big2-{Guid.NewGuid():N}.bin");
        var pathB = Path.Combine(Path.GetTempPath(), $"bitchat-big2-{Guid.NewGuid():N}.bin");
        try
        {
            var alice = new NoiseService(pathA);
            var bob = new NoiseService(pathB);

            var m1 = alice.InitiateHandshake(bob.MyPeerId);
            var r1 = bob.ProcessHandshakeMessage(m1!, alice.MyPeerId);
            var r2 = alice.ProcessHandshakeMessage(r1.Response!, bob.MyPeerId);
            bob.ProcessHandshakeMessage(r2.Response!, alice.MyPeerId);

            var content = new byte[75_000]; // > 0xFFFF — must use v2
            new Random(9).NextBytes(content);
            var file = new BitchatFilePacket("IMG_2026.JPG", content.Length, "image/jpeg", content);
            var encrypted = alice.Encrypt(new NoisePayload(NoisePayloadType.FileTransfer, file.Encode()).Encode(), bob.MyPeerId);
            Assert.True(encrypted.Length > 0xFFFF, $"test payload must exceed 64KB (got {encrypted.Length})");

            var packet = new BitchatPacket
            {
                Version = Bitchat.Windows.Mesh.MeshEngine.VersionForPayload(encrypted.Length),
                Type = (byte)MessageType.NoiseEncrypted,
                SenderId = new byte[] { 9, 8, 7, 6, 5, 4, 3, 2 },
                RecipientId = Bitchat.Windows.Mesh.MeshEngine.HexToBytes(bob.MyPeerId),
                Payload = encrypted,
                Ttl = 7
            };
            packet.Timestamp = 1_700_000_000_000;
            packet.Signature = alice.SignPacket(packet);
            Assert.NotNull(packet.Signature);

            var encoded = BinaryProtocol.Encode(packet, pad: true);
            Assert.NotNull(encoded);
            var decoded = BinaryProtocol.Decode(encoded!);
            Assert.NotNull(decoded);
            Assert.Equal(2, decoded!.Version);
            Assert.True(alice.VerifyPacketSignature(decoded!, alice.GetSigningPublicKeyData()));

            var decrypted = bob.Decrypt(decoded.Payload, alice.MyPeerId);
            var payload = NoisePayload.Decode(decrypted);
            var decodedFile = BitchatFilePacket.Decode(payload!.Data);
            Assert.NotNull(decodedFile);
            Assert.Equal(content, decodedFile!.Content);
        }
        finally { File.Delete(pathA); File.Delete(pathB); }
    }

    [Fact]
    public void Private_file_packet_signs_and_encodes()
    {
        var pathA = Path.Combine(Path.GetTempPath(), $"bitchat-fp-{Guid.NewGuid():N}.bin");
        var pathB = Path.Combine(Path.GetTempPath(), $"bitchat-fp-{Guid.NewGuid():N}.bin");
        try
        {
            var alice = new NoiseService(pathA);
            var bob = new NoiseService(pathB);

            var m1 = alice.InitiateHandshake(bob.MyPeerId);
            var r1 = bob.ProcessHandshakeMessage(m1!, alice.MyPeerId);
            var r2 = alice.ProcessHandshakeMessage(r1.Response!, bob.MyPeerId);
            bob.ProcessHandshakeMessage(r2.Response!, alice.MyPeerId);

            var content = new byte[1200];
            new Random(4).NextBytes(content);
            var file = new BitchatFilePacket("photo.png", content.Length, "image/png", content);
            var noisePayload = new NoisePayload(NoisePayloadType.FileTransfer, file.Encode());
            var encrypted = alice.Encrypt(noisePayload.Encode(), bob.MyPeerId);

            var packet = new BitchatPacket
            {
                Version = 1,
                Type = (byte)MessageType.NoiseEncrypted,
                SenderId = new byte[] { 9, 8, 7, 6, 5, 4, 3, 2 },
                RecipientId = Bitchat.Windows.Mesh.MeshEngine.HexToBytes(bob.MyPeerId),
                Payload = encrypted,
                Ttl = 7
            };
            packet.Timestamp = 1_700_000_000_000;
            packet.Signature = alice.SignPacket(packet);
            Assert.True(packet.Signature != null, $"SignPacket failed; encrypted={packet.Payload.Length}");

            var encoded = BinaryProtocol.Encode(packet, pad: true);
            Assert.NotNull(encoded);
            var decoded = BinaryProtocol.Decode(encoded!);
            Assert.NotNull(decoded);
            Assert.True(alice.VerifyPacketSignature(decoded!, alice.GetSigningPublicKeyData()));

            var decrypted = bob.Decrypt(decoded!.Payload, alice.MyPeerId);
            var payload = NoisePayload.Decode(decrypted);
            Assert.NotNull(payload);
            Assert.Equal(NoisePayloadType.FileTransfer, payload!.Type);
            var decodedFile = BitchatFilePacket.Decode(payload.Data);
            Assert.NotNull(decodedFile);
            Assert.Equal("photo.png", decodedFile!.FileName);
        }
        finally { File.Delete(pathA); File.Delete(pathB); }
    }
}
