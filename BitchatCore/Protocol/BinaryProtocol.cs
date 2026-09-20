using System.IO;

using System.IO.Compression;
using System.Security.Cryptography;

namespace Bitchat.Windows.Protocol;

public enum MessageType : byte
{
    Announce = 0x01,
    Message = 0x02,
    Leave = 0x03,
    NoiseHandshake = 0x10,
    NoiseEncrypted = 0x11,
    Fragment = 0x20,
    RequestSync = 0x21,
    FileTransfer = 0x22,
    VoiceFrame = 0x29
}

[Flags]
public enum PacketFlags : byte
{
    None = 0,
    HasRecipient = 0x01,
    HasSignature = 0x02,
    IsCompressed = 0x04,
    HasRoute = 0x08
}

public sealed class BitchatPacket
{
    public byte Version { get; set; } = BitchatConstants.ProtocolVersion;
    public byte Type { get; set; }
    public byte Ttl { get; set; }
    public ulong Timestamp { get; set; }
    public byte Flags { get; set; }
    public byte[] SenderId { get; set; } = new byte[8];
    public byte[]? RecipientId { get; set; }
    public byte[] Payload { get; set; } = Array.Empty<byte>();
    public byte[]? Signature { get; set; }

    /// <summary>Compressed bytes exactly as they arrived on the wire (if the packet was compressed).</summary>
    public byte[]? WireCompressedBytes { get; set; }

    public ulong NowTimestamp()
    {
        Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return Timestamp;
    }

    public BitchatPacket CloneForSigning()
    {
        return new BitchatPacket
        {
            Version = Version,
            Type = Type,
            // Android/iOS sign the canonical packet with a fixed TTL=0 (SYNC_TTL_HOPS):
            // TTL changes during relay and must not be part of the signature preimage.
            Ttl = BitchatConstants.SyncTtlHops,
            Timestamp = Timestamp,
            SenderId = SenderId,
            RecipientId = RecipientId,
            Payload = Payload,
            Signature = null,
            WireCompressedBytes = WireCompressedBytes
        };
    }
}

public enum ForceCompression
{
    Auto,
    Never,
    Always
}

public static class BinaryProtocol
{
    public static bool ShouldPadForBle(byte type) =>
        type is (byte)MessageType.NoiseEncrypted or (byte)MessageType.NoiseHandshake;

    public static byte[]? Encode(BitchatPacket packet, bool pad = true, ForceCompression force = ForceCompression.Auto)
    {
        try
        {
            if (packet.Payload.Length > BitchatConstants.MaxPayloadLength) return null;

            var payload = packet.Payload;
            int? originalPayloadSize = null;
            var isCompressed = false;

            // Re-encode of a decoded packet reuses the originator's wire bytes (WirePayload semantics).
            var wire = packet.WireCompressedBytes;
            if (wire != null && force == ForceCompression.Auto)
            {
                payload = wire;
                originalPayloadSize = packet.Payload.Length;
                isCompressed = true;
            }
            else if (force == ForceCompression.Always || (force == ForceCompression.Auto && ShouldCompress(packet.Payload)))
            {
                var compressed = Compress(packet.Payload);
                if (compressed != null && (force == ForceCompression.Always || compressed.Length < payload.Length))
                {
                    originalPayloadSize = payload.Length;
                    payload = compressed;
                    isCompressed = true;
                }
            }

            var sizeFieldBytes = isCompressed ? 2 : 0;
            var headerSize = packet.Version >= 2 ? 16 : BitchatConstants.HeaderSizeV1;

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            bw.Write(packet.Version);
            bw.Write(packet.Type);
            bw.Write(packet.Ttl);
            bw.Write(ReverseBytes(packet.Timestamp));

            // Flags are derived from the packet content, matching the Android/iOS encoders.
            var flags = (byte)PacketFlags.None;
            if (packet.RecipientId != null) flags |= (byte)PacketFlags.HasRecipient;
            if (packet.Signature != null) flags |= (byte)PacketFlags.HasSignature;
            if (isCompressed) flags |= (byte)PacketFlags.IsCompressed;
            bw.Write(flags);
            if (packet.Version >= 2)
                bw.Write(ReverseBytes((uint)(payload.Length + sizeFieldBytes)));
            else
            {
                var payloadDataSize = payload.Length + sizeFieldBytes;
                if (payloadDataSize > 0xFFFF || (originalPayloadSize ?? 0) > 0xFFFF) return null;
                bw.Write(ReverseBytes((ushort)payloadDataSize));
            }

            var senderBytes = new byte[BitchatConstants.SenderIdSize];
            Array.Copy(packet.SenderId, senderBytes, Math.Min(packet.SenderId.Length, BitchatConstants.SenderIdSize));
            bw.Write(senderBytes);

            if (packet.RecipientId != null)
            {
                var recipientBytes = new byte[BitchatConstants.RecipientIdSize];
                Array.Copy(packet.RecipientId, recipientBytes, Math.Min(packet.RecipientId.Length, BitchatConstants.RecipientIdSize));
                bw.Write(recipientBytes);
            }

            if (isCompressed && originalPayloadSize.HasValue)
                bw.Write(ReverseBytes((ushort)originalPayloadSize.Value));

            bw.Write(payload);

            if (packet.Signature != null)
            {
                if (packet.Signature.Length != BitchatConstants.SignatureSize) return null;
                bw.Write(packet.Signature);
            }

            var result = ms.ToArray();
            if (!pad) return result;

            var optimalSize = MessagePadding.OptimalBlockSize(result.Length);
            return optimalSize > result.Length ? MessagePadding.Pad(result, optimalSize) : result;
        }
        catch
        {
            return null;
        }
    }

    private static ulong ReverseBytes(ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static ushort ReverseBytes(ushort value) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value);
    private static uint ReverseBytes(uint value) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value);

    public static BitchatPacket? Decode(byte[] data)
    {
        var direct = DecodeCore(data);
        if (direct != null) return direct;

        var unpadded = MessagePadding.Unpad(data);
        if (unpadded.Length == data.Length) return null;
        return DecodeCore(unpadded);
    }

    private static BitchatPacket? DecodeCore(byte[] raw)
    {
        try
        {
            if (raw.Length < BitchatConstants.HeaderSizeV1 + BitchatConstants.SenderIdSize) return null;

            var offset = 0;
            var version = raw[offset++];
            if (version is not (1 or 2)) return null;

            var headerSize = version >= 2 ? 16 : BitchatConstants.HeaderSizeV1;

            var type = raw[offset++];
            var ttl = raw[offset++];

            var timestamp = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(raw.AsSpan(offset, 8));
            offset += 8;

            var flags = raw[offset++];
            var hasRecipient = (flags & (byte)PacketFlags.HasRecipient) != 0;
            var hasSignature = (flags & (byte)PacketFlags.HasSignature) != 0;
            var isCompressed = (flags & (byte)PacketFlags.IsCompressed) != 0;

            int payloadLength;
            if (version >= 2)
            {
                payloadLength = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(offset, 4));
                offset += 4;
            }
            else
            {
                payloadLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(offset, 2));
                offset += 2;
            }

            if (payloadLength > BitchatConstants.MaxPayloadLength) return null;

            var expectedSize = headerSize + BitchatConstants.SenderIdSize + payloadLength;
            if (hasRecipient) expectedSize += BitchatConstants.RecipientIdSize;
            if (hasSignature) expectedSize += BitchatConstants.SignatureSize;
            if (raw.Length < expectedSize) return null;

            var senderId = new byte[BitchatConstants.SenderIdSize];
            Array.Copy(raw, offset, senderId, 0, BitchatConstants.SenderIdSize);
            offset += BitchatConstants.SenderIdSize;

            byte[]? recipientId = null;
            if (hasRecipient)
            {
                recipientId = new byte[BitchatConstants.RecipientIdSize];
                Array.Copy(raw, offset, recipientId, 0, BitchatConstants.RecipientIdSize);
                offset += BitchatConstants.RecipientIdSize;
            }

            byte[] payload;
            byte[]? wireCompressed = null;
            if (isCompressed)
            {
                if (payloadLength < 2) return null;

                var originalSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(offset, 2));
                offset += 2;

                if (originalSize <= 0 || originalSize > BitchatConstants.MaxPayloadLength) return null;

                var compressedSize = payloadLength - 2;
                if (compressedSize == 0) return null;

                // Compression bomb protection (iOS compatible)
                var ratio = originalSize / (double)compressedSize;
                if (ratio > BitchatConstants.MaxCompressionRatio) return null;

                wireCompressed = new byte[compressedSize];
                Array.Copy(raw, offset, wireCompressed, 0, compressedSize);
                offset += compressedSize;

                payload = Decompress(wireCompressed, originalSize);
                if (payload == null || payload.Length != originalSize) return null;
            }
            else
            {
                payload = new byte[payloadLength];
                Array.Copy(raw, offset, payload, 0, payloadLength);
                offset += payloadLength;
            }

            byte[]? signature = null;
            if (hasSignature)
            {
                signature = new byte[BitchatConstants.SignatureSize];
                Array.Copy(raw, offset, signature, 0, BitchatConstants.SignatureSize);
                offset += BitchatConstants.SignatureSize;
            }

            return new BitchatPacket
            {
                Version = version,
                Type = type,
                Ttl = ttl,
                Timestamp = timestamp,
                Flags = flags,
                SenderId = senderId,
                RecipientId = recipientId,
                Payload = payload,
                Signature = signature,
                WireCompressedBytes = wireCompressed
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool ShouldCompress(byte[] payload) => payload.Length > BitchatConstants.CompressionThresholdBytes;

    public static byte[]? Compress(byte[] data)
    {
        try
        {
            using var output = new MemoryStream();
            using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }
        catch
        {
            return null;
        }
    }

    public static byte[]? Decompress(byte[] data, int originalSize)
    {
        try
        {
            var output = new byte[originalSize];
            using var input = new MemoryStream(data);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            var total = 0;
            while (total < originalSize)
            {
                var read = deflate.Read(output, total, originalSize - total);
                if (read == 0) break;
                total += read;
            }
            if (deflate.Read(new byte[1], 0, 1) > 0) return null; // trailing garbage
            return total == originalSize ? output : null;
        }
        catch
        {
            return null;
        }
    }
}

public static class MessagePadding
{
    public static int OptimalBlockSize(int dataSize)
    {
        var totalSize = dataSize + 16;
        foreach (var blockSize in BitchatConstants.PaddingBlockSizes)
        {
            if (totalSize <= blockSize) return blockSize;
        }
        return dataSize;
    }

    public static byte[] Pad(byte[] data, int targetSize)
    {
        if (data.Length >= targetSize) return data;
        var paddingNeeded = targetSize - data.Length;
        if (paddingNeeded <= 0 || paddingNeeded > 255) return data;

        var result = new byte[targetSize];
        Array.Copy(data, result, data.Length);
        Array.Fill(result, (byte)paddingNeeded, data.Length, paddingNeeded);
        return result;
    }

    public static byte[] Unpad(byte[] data)
    {
        if (data.Length == 0) return data;
        var last = data[^1];
        var paddingLength = last;
        if (paddingLength <= 0 || paddingLength > data.Length) return data;

        for (var i = data.Length - paddingLength; i < data.Length; i++)
        {
            if (data[i] != last) return data;
        }
        return data[..^paddingLength];
    }
}
