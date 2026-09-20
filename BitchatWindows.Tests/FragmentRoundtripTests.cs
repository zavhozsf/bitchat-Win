using Bitchat.Windows;
using Bitchat.Windows.Mesh;
using Bitchat.Windows.Noise;
using Bitchat.Windows.Protocol;
using Xunit;

namespace BitchatWindows.Tests;

/// <summary>Fragmentation roundtrip: fragment a big FILE_TRANSFER packet like the sender,
/// feed the fragments through the receiver-side reassembly, and verify the packet.</summary>
public sealed class FragmentRoundtripTests
{
    [Fact]
    public void Fragmented_file_packet_survives_reassembly()
    {
        var content = new byte[30_000]; // ~65 fragments of 469 bytes
        new Random(7).NextBytes(content);
        // make it JPEG-ish (incompressible)
        for (var i = 0; i < content.Length; i += 7) content[i] = 0xFF;

        var file = new BitchatFilePacket("test-image.jpg", content.Length, "image/jpeg", content);
        var packet = new BitchatPacket
        {
            Version = 2,
            Type = (byte)MessageType.FileTransfer,
            SenderId = new byte[] { 0xAA, 1, 2, 3, 4, 5, 6, 7 },
            RecipientId = BitchatConstants.BroadcastRecipient,
            Payload = file.Encode(),
            Ttl = 7
        };
        packet.Timestamp = 1_700_000_000_500;
        packet.Signature = new byte[64]; // placeholder signature (not checked here)

        var encoded = MeshEngine.EncodeForBle(packet);
        Assert.NotNull(encoded);
        Assert.True(encoded!.Length > BitchatConstants.FragmentSizeThreshold, "packet must be fragmented");

        // --- sender side: fragment like MeshEngine.SendFragmented ---
        var fragmentId = FragmentPayload.GenerateFragmentId();
        var chunks = (encoded.Length + BitchatConstants.MaxFragmentSize - 1) / BitchatConstants.MaxFragmentSize;
        var fragments = new List<byte[]>();
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
                SenderId = packet.SenderId,
                RecipientId = packet.RecipientId,
                Payload = new FragmentPayload(fragmentId, i, chunks, packet.Type, chunk).Encode(),
                Ttl = packet.Ttl
            };
            fragmentPacket.Timestamp = packet.Timestamp;
            fragments.Add(BinaryProtocol.Encode(fragmentPacket, pad: false)!);
        }

        // --- receiver side: feed through BinaryProtocol.Decode + reassembly ---
        var parts = new byte[chunks][];
        foreach (var fragmentBytes in fragments)
        {
            var fragmentPacket = BinaryProtocol.Decode(fragmentBytes);
            Assert.NotNull(fragmentPacket);
            Assert.Equal((byte)MessageType.Fragment, fragmentPacket!.Type);

            var fragment = FragmentPayload.Decode(fragmentPacket.Payload);
            Assert.NotNull(fragment);
            Assert.Equal(chunks, fragment!.Total);
            parts[fragment.Index] = fragment.Data;
        }

        var combined = new byte[parts.Sum(p => p!.Length)];
        var off = 0;
        foreach (var part in parts)
        {
            Array.Copy(part!, 0, combined, off, part.Length);
            off += part.Length;
        }

        var reassembled = BinaryProtocol.Decode(combined);
        Assert.NotNull(reassembled);
        Assert.Equal((byte)MessageType.FileTransfer, reassembled!.Type);
        Assert.Equal(2, reassembled.Version);

        var decodedFile = BitchatFilePacket.Decode(reassembled.Payload);
        Assert.NotNull(decodedFile);
        Assert.Equal("test-image.jpg", decodedFile!.FileName);
        Assert.Equal(content, decodedFile.Content);
    }
}
