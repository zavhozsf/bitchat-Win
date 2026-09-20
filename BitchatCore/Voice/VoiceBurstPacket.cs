using System.IO;
using System.Security.Cryptography;

namespace Bitchat.Windows.Voice;

/// <summary>iOS-compatible codec identifier carried by a live push-to-talk START packet.</summary>
public enum VoiceBurstCodec : byte
{
    AacLc16kMono = 0x01
}

/// <summary>
/// One live push-to-talk packet - byte-compatible with the mobile clients.
/// Wire format: [burstID: 8][seq: UInt16 BE][flags: u8][payload...].
/// Flags: 0x01 START (+codec byte), 0x00 frames (u16-len each, max 8),
/// 0x02 END (u16 totalDataPackets, u32 durationMs), 0x04 canceled.
/// </summary>
public sealed class VoiceBurstPacket
{
    public const int BurstIdSize = 8;
    public const int HeaderSize = BurstIdSize + 2 + 1;
    public const int MaxFramesPerPacket = 8;
    public const int MaxContentBytes = 210;

    private const byte FlagStart = 0x01;
    private const byte FlagEnd = 0x02;
    private const byte FlagCanceled = 0x04;

    public byte[] BurstId { get; }
    public int Sequence { get; }
    public Kind Value { get; }

    public abstract record Kind
    {
        public sealed record Start(VoiceBurstCodec Codec) : Kind;
        public sealed record Frames(IReadOnlyList<byte[]> FrameList) : Kind;
        public sealed record End(int TotalDataPackets, long DurationMs) : Kind;
        public sealed record Canceled : Kind;
    }

    private VoiceBurstPacket(byte[] burstId, int sequence, Kind kind)
    {
        BurstId = burstId;
        Sequence = sequence;
        Value = kind;
    }

    public static VoiceBurstPacket? Create(byte[] burstId, int sequence, Kind kind)
    {
        if (burstId.Length != BurstIdSize || sequence is < 0 or > 0xFFFF) return null;
        switch (kind)
        {
            case Kind.Frames frames:
                if (frames.FrameList.Count == 0 || frames.FrameList.Count > MaxFramesPerPacket) return null;
                if (frames.FrameList.Any(f => f.Length == 0 || f.Length > 0xFFFF)) return null;
                break;
            case Kind.End end:
                if (end.TotalDataPackets is < 0 or > 0xFFFF || end.DurationMs < 0 || end.DurationMs > 0xFFFF_FFFF) return null;
                break;
        }
        return new VoiceBurstPacket((byte[])burstId.Clone(), sequence, kind);
    }

    public byte[] Encode()
    {
        using var output = new MemoryStream(HeaderSize + 16);
        output.Write(BurstId);
        output.WriteByte((byte)((Sequence >> 8) & 0xFF));
        output.WriteByte((byte)(Sequence & 0xFF));
        switch (Value)
        {
            case Kind.Start start:
                output.WriteByte(FlagStart);
                output.WriteByte((byte)start.Codec);
                break;
            case Kind.Frames frames:
                output.WriteByte(0);
                foreach (var frame in frames.FrameList)
                {
                    output.WriteByte((byte)((frame.Length >> 8) & 0xFF));
                    output.WriteByte((byte)(frame.Length & 0xFF));
                    output.Write(frame);
                }
                break;
            case Kind.End end:
                output.WriteByte(FlagEnd);
                output.WriteByte((byte)((end.TotalDataPackets >> 8) & 0xFF));
                output.WriteByte((byte)(end.TotalDataPackets & 0xFF));
                output.WriteByte((byte)((end.DurationMs >> 24) & 0xFF));
                output.WriteByte((byte)((end.DurationMs >> 16) & 0xFF));
                output.WriteByte((byte)((end.DurationMs >> 8) & 0xFF));
                output.WriteByte((byte)(end.DurationMs & 0xFF));
                break;
            case Kind.Canceled:
                output.WriteByte(FlagCanceled);
                break;
        }
        return output.ToArray();
    }

    public static VoiceBurstPacket? Decode(byte[] data)
    {
        if (data.Length < HeaderSize) return null;
        var burstId = data[..BurstIdSize];
        var sequence = ((data[BurstIdSize] & 0xFF) << 8) | (data[BurstIdSize + 1] & 0xFF);
        var flags = data[BurstIdSize + 2] & 0xFF;
        var offset = HeaderSize;

        Kind kind;
        switch (flags)
        {
            case FlagStart:
            {
                if (offset >= data.Length) return null;
                if (!Enum.IsDefined(typeof(VoiceBurstCodec), data[offset])) return null;
                kind = new Kind.Start((VoiceBurstCodec)data[offset]);
                break;
            }
            case FlagEnd:
            {
                if (data.Length - offset < 6) return null;
                var total = ((data[offset] & 0xFF) << 8) | (data[offset + 1] & 0xFF);
                long duration = 0;
                for (var i = 0; i < 4; i++)
                    duration = (duration << 8) | (data[offset + 2 + i] & 0xFFL);
                kind = new Kind.End(total, duration);
                break;
            }
            case FlagCanceled:
                kind = new Kind.Canceled();
                break;
            case 0:
            {
                var frames = new List<byte[]>();
                var cursor = offset;
                while (cursor < data.Length)
                {
                    if (data.Length - cursor < 2 || frames.Count >= MaxFramesPerPacket) return null;
                    var length = ((data[cursor] & 0xFF) << 8) | (data[cursor + 1] & 0xFF);
                    cursor += 2;
                    if (length <= 0 || data.Length - cursor < length) return null;
                    frames.Add(data[cursor..(cursor + length)]);
                    cursor += length;
                }
                if (frames.Count == 0) return null;
                kind = new Kind.Frames(frames);
                break;
            }
            default:
                return null;
        }
        return Create(burstId, sequence, kind);
    }

    public static byte[] MakeBurstId()
    {
        var id = new byte[BurstIdSize];
        RandomNumberGenerator.Fill(id);
        return id;
    }

    public static string BurstIdHex(byte[] burstId) => Convert.ToHexString(burstId).ToLowerInvariant();
}

/// <summary>Greedy packetizer: bundles frames so a Noise-wrapped packet stays out of fragmentation.</summary>
public sealed class VoiceBurstPacketizer
{
    private readonly List<byte[]> _pending = new();
    private int _pendingSize;

    public VoiceBurstPacketizer(byte[] burstId, int budget = VoiceBurstPacket.MaxContentBytes)
    {
        BurstId = burstId;
        Budget = budget;
    }

    public byte[] BurstId { get; }
    public int Budget { get; }
    public int NextSequence { get; private set; } = 1;
    public int DataPacketCount { get; private set; }
    public int DroppedFrameCount { get; private set; }

    /// <summary>Adds a frame; returns full data packets ready to send (may be empty).</summary>
    public List<VoiceBurstPacket> Add(byte[] frame)
    {
        var frameCost = 2 + frame.Length;
        if (VoiceBurstPacket.HeaderSize + frameCost > Budget)
        {
            DroppedFrameCount++;
            return new List<VoiceBurstPacket>();
        }
        var output = new List<VoiceBurstPacket>();
        if (_pending.Count > 0 &&
            (VoiceBurstPacket.HeaderSize + _pendingSize + frameCost > Budget ||
             _pending.Count >= VoiceBurstPacket.MaxFramesPerPacket))
        {
            output.AddRange(Flush());
        }
        _pending.Add((byte[])frame.Clone());
        _pendingSize += frameCost;
        return output;
    }

    public List<VoiceBurstPacket> Flush()
    {
        if (_pending.Count == 0) return new List<VoiceBurstPacket>();
        var packet = VoiceBurstPacket.Create(
            BurstId, NextSequence, new VoiceBurstPacket.Kind.Frames(_pending.ToList()));
        _pending.Clear();
        _pendingSize = 0;
        if (packet == null) return new List<VoiceBurstPacket>();
        NextSequence = (NextSequence + 1) & 0xFFFF;
        DataPacketCount = Math.Min(DataPacketCount + 1, 0xFFFF);
        return new List<VoiceBurstPacket> { packet };
    }
}

/// <summary>Adds a 7-byte ADTS header to an ADTS-less AAC-LC/16kHz/mono access unit.</summary>
public static class AdtsFramer
{
    public static byte[] Frame(byte[] payload)
    {
        var frameLength = payload.Length + 7;
        if (frameLength > 0x1FFF) throw new ArgumentException("AAC frame is too large for ADTS");
        var output = new byte[frameLength];
        output[0] = 0xFF;
        output[1] = 0xF1;
        output[2] = 0x60; // AAC-LC, 16 kHz frequency index, mono channel config high bit
        output[3] = (byte)(0x40 | ((frameLength >> 11) & 0x03));
        output[4] = (byte)((frameLength >> 3) & 0xFF);
        output[5] = (byte)(((frameLength & 0x07) << 5) | 0x1F);
        output[6] = 0xFC;
        Array.Copy(payload, 0, output, 7, payload.Length);
        return output;
    }
}
