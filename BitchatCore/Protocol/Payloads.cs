using System.IO;

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Bitchat.Windows.Protocol;

/// <summary>Feature bits advertised in IdentityAnnouncement TLV 0x05 (little-endian bitfield).</summary>
public sealed class PeerCapabilities
{
    public ulong RawValue { get; }

    public PeerCapabilities(ulong rawValue) => RawValue = rawValue;

    public static readonly PeerCapabilities None = new(0);
    public static readonly PeerCapabilities Prekeys = new(1UL << 0);
    public static readonly PeerCapabilities WifiBulk = new(1UL << 1);
    public static readonly PeerCapabilities Gateway = new(1UL << 2);
    public static readonly PeerCapabilities Groups = new(1UL << 3);
    public static readonly PeerCapabilities Board = new(1UL << 4);
    public static readonly PeerCapabilities Vouch = new(1UL << 5);
    public static readonly PeerCapabilities MeshDiagnostics = new(1UL << 6);
    public static readonly PeerCapabilities Bridge = new(1UL << 7);
    public static readonly PeerCapabilities PrivateMedia = new(1UL << 8);
    public static readonly PeerCapabilities PrivateMediaReceipts = new(1UL << 9);

    /// <summary>Capabilities advertised by this client (matches the Android build).</summary>
    public static readonly PeerCapabilities LocalSupported = PrivateMedia;

    public byte[] Encode()
    {
        var remaining = RawValue;
        var bytes = new List<byte>();
        do
        {
            bytes.Add((byte)remaining);
            remaining >>= 8;
        } while (remaining != 0);
        return bytes.ToArray();
    }

    public static PeerCapabilities Decode(byte[] data)
    {
        ulong rawValue = 0;
        for (var i = 0; i < Math.Min(data.Length, 8); i++)
            rawValue |= (ulong)data[i] << (8 * i);
        return new PeerCapabilities(rawValue);
    }
}

/// <summary>Identity announcement TLV structure (iOS compatible).</summary>
public sealed class IdentityAnnouncement
{
    private const byte TlvNickname = 0x01;
    private const byte TlvNoisePublicKey = 0x02;
    private const byte TlvSigningPublicKey = 0x03;
    private const byte TlvCapabilities = 0x05;

    public string Nickname { get; }
    public byte[] NoisePublicKey { get; }
    public byte[] SigningPublicKey { get; }
    public PeerCapabilities? Capabilities { get; }
    public IReadOnlyList<(byte Type, byte[] Value)> UnknownTlvs { get; }

    public IdentityAnnouncement(
        string nickname,
        byte[] noisePublicKey,
        byte[] signingPublicKey,
        PeerCapabilities? capabilities = null,
        IReadOnlyList<(byte Type, byte[] Value)>? unknownTlvs = null)
    {
        Nickname = nickname;
        NoisePublicKey = noisePublicKey;
        SigningPublicKey = signingPublicKey;
        Capabilities = capabilities;
        UnknownTlvs = unknownTlvs ?? Array.Empty<(byte, byte[])>();
    }

    public static IdentityAnnouncement ForLocalPeer(string nickname, byte[] noisePublicKey, byte[] signingPublicKey) =>
        new(nickname, noisePublicKey, signingPublicKey, PeerCapabilities.LocalSupported);

    public byte[]? Encode()
    {
        var nicknameData = Encoding.UTF8.GetBytes(Nickname);
        if (nicknameData.Length > 255 || NoisePublicKey.Length > 255 || SigningPublicKey.Length > 255)
            return null;

        var result = new List<byte>();
        AppendTlv(result, TlvNickname, nicknameData);
        AppendTlv(result, TlvNoisePublicKey, NoisePublicKey);
        AppendTlv(result, TlvSigningPublicKey, SigningPublicKey);
        if (Capabilities != null)
            AppendTlv(result, TlvCapabilities, Capabilities.Encode());
        foreach (var (type, value) in UnknownTlvs)
            AppendTlv(result, type, value);
        return result.ToArray();
    }

    public static IdentityAnnouncement? Decode(byte[] data)
    {
        string? nickname = null;
        byte[]? noisePublicKey = null;
        byte[]? signingPublicKey = null;
        PeerCapabilities? capabilities = null;
        List<(byte, byte[])> unknown = new();

        var offset = 0;
        while (offset + 2 <= data.Length)
        {
            var type = data[offset++];
            var length = data[offset++];
            if (offset + length > data.Length) return null;
            var value = data[offset..(offset + length)];
            offset += length;

            switch (type)
            {
                case TlvNickname: nickname = Encoding.UTF8.GetString(value); break;
                case TlvNoisePublicKey: noisePublicKey = value; break;
                case TlvSigningPublicKey: signingPublicKey = value; break;
                case TlvCapabilities: capabilities = PeerCapabilities.Decode(value); break;
                default: unknown.Add((type, value)); break;
            }
        }

        return nickname != null && noisePublicKey != null && signingPublicKey != null
            ? new IdentityAnnouncement(nickname, noisePublicKey, signingPublicKey, capabilities, unknown)
            : null;
    }

    private static void AppendTlv(List<byte> result, byte type, byte[] value)
    {
        result.Add(type);
        result.Add((byte)value.Length);
        result.AddRange(value);
    }
}

/// <summary>Payload types embedded within noiseEncrypted messages (iOS compatible).</summary>
public enum NoisePayloadType : byte
{
    PrivateMessage = 0x01,
    ReadReceipt = 0x02,
    Delivered = 0x03,
    VoiceFrame = 0x08,
    VerifyChallenge = 0x10,
    VerifyResponse = 0x11,
    FileTransfer = 0x20,
    PeerState = 0x21
}

public sealed class NoisePayload
{
    public NoisePayloadType Type { get; }
    public byte[] Data { get; }

    public NoisePayload(NoisePayloadType type, byte[] data)
    {
        Type = type;
        Data = data;
    }

    public byte[] Encode()
    {
        var result = new byte[1 + Data.Length];
        result[0] = (byte)Type;
        Array.Copy(Data, 0, result, 1, Data.Length);
        return result;
    }

    public static NoisePayload? Decode(byte[] data)
    {
        if (data.Length == 0) return null;
        if (!Enum.IsDefined(typeof(NoisePayloadType), data[0])) return null;
        var payloadData = data.Length > 1 ? data[1..] : Array.Empty<byte>();
        return new NoisePayload((NoisePayloadType)data[0], payloadData);
    }
}

/// <summary>Private message TLV packet (iOS compatible).</summary>
public sealed class PrivateMessagePacket
{
    private const byte TlvMessageId = 0x00;
    private const byte TlvContent = 0x01;

    public string MessageId { get; }
    public string Content { get; }

    public PrivateMessagePacket(string messageId, string content)
    {
        MessageId = messageId;
        Content = content;
    }

    public byte[]? Encode()
    {
        var idData = Encoding.UTF8.GetBytes(MessageId);
        var contentData = Encoding.UTF8.GetBytes(Content);
        if (idData.Length > 255 || contentData.Length > 255) return null;

        var result = new List<byte>();
        result.Add(TlvMessageId);
        result.Add((byte)idData.Length);
        result.AddRange(idData);
        result.Add(TlvContent);
        result.Add((byte)contentData.Length);
        result.AddRange(contentData);
        return result.ToArray();
    }

    public static PrivateMessagePacket? Decode(byte[] data)
    {
        var offset = 0;
        string? messageId = null;
        string? content = null;

        while (offset + 2 <= data.Length)
        {
            var type = data[offset++];
            var length = data[offset++];
            if (offset + length > data.Length) return null;
            var value = data[offset..(offset + length)];
            offset += length;

            if (type == TlvMessageId) messageId = Encoding.UTF8.GetString(value);
            else if (type == TlvContent) content = Encoding.UTF8.GetString(value);
            else return null;
        }

        return messageId != null && content != null ? new PrivateMessagePacket(messageId, content) : null;
    }
}

/// <summary>Fragment payload structure (iOS compatible): 8B id + u16 index + u16 total + u8 type + data.</summary>
public sealed class FragmentPayload
{
    public const int HeaderSize = 13;
    public const int FragmentIdSize = 8;

    public byte[] FragmentId { get; }
    public int Index { get; }
    public int Total { get; }
    public byte OriginalType { get; }
    public byte[] Data { get; }

    public FragmentPayload(byte[] fragmentId, int index, int total, byte originalType, byte[] data)
    {
        FragmentId = fragmentId;
        Index = index;
        Total = total;
        OriginalType = originalType;
        Data = data;
    }

    public byte[] Encode()
    {
        if (Index is < 0 or > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(Index));
        if (Total is < 1 or > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(Total));

        var payload = new byte[HeaderSize + Data.Length];
        Array.Copy(FragmentId, payload, FragmentIdSize);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(8, 2), (ushort)Index);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(10, 2), (ushort)Total);
        payload[12] = OriginalType;
        Array.Copy(Data, 0, payload, HeaderSize, Data.Length);
        return payload;
    }

    public static FragmentPayload? Decode(byte[] payloadData)
    {
        if (payloadData.Length < HeaderSize) return null;

        var fragmentId = payloadData[..FragmentIdSize];
        var index = BinaryPrimitives.ReadUInt16BigEndian(payloadData.AsSpan(8, 2));
        var total = BinaryPrimitives.ReadUInt16BigEndian(payloadData.AsSpan(10, 2));
        var originalType = payloadData[12];
        var data = payloadData.Length > HeaderSize ? payloadData[HeaderSize..] : Array.Empty<byte>();
        return new FragmentPayload(fragmentId, index, total, originalType, data);
    }

    public static byte[] GenerateFragmentId()
    {
        var id = new byte[FragmentIdSize];
        RandomNumberGenerator.Fill(id);
        return id;
    }
}

public static class PacketIdUtil
{
    /// <summary>SHA-256 over [type | senderID | timestamp | payload], truncated to 16 bytes (hex).</summary>
    public static string ComputeIdHex(BitchatPacket packet)
    {
        using var md5Like = SHA256.Create();
        md5Like.TransformBlock(new[] { packet.Type }, 0, 1, null, 0);
        md5Like.TransformBlock(packet.SenderId, 0, packet.SenderId.Length, null, 0);
        var ts = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(ts, packet.Timestamp);
        md5Like.TransformBlock(ts, 0, 8, null, 0);
        md5Like.TransformFinalBlock(packet.Payload, 0, packet.Payload.Length);
        return Convert.ToHexString(md5Like.Hash!, 0, 16).ToLowerInvariant();
    }

    /// <summary>16-byte PacketId used as GCS filter input (sync).</summary>
    public static byte[] ComputeIdBytes(BitchatPacket packet)
    {
        using var md = SHA256.Create();
        md.TransformBlock(new[] { packet.Type }, 0, 1, null, 0);
        md.TransformBlock(packet.SenderId, 0, packet.SenderId.Length, null, 0);
        var ts = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(ts, packet.Timestamp);
        md.TransformBlock(ts, 0, 8, null, 0);
        md.TransformFinalBlock(packet.Payload, 0, packet.Payload.Length);
        var hash = md.Hash!;
        var id = new byte[16];
        Array.Copy(hash, id, 16);
        return id;
    }
}

public static class ByteArrayExtensions
{
    public static string ToHexString(this byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
