using System.IO;

using System.Buffers.Binary;

namespace Bitchat.Windows.Protocol;

/// <summary>
/// REQUEST_SYNC payload with GCS (Golomb-Coded Set) parameters.
/// TLV (type, length16 BE, value): 0x01 P (u8), 0x02 M (u32 BE), 0x03 data.
/// </summary>
public sealed class RequestSyncPacket
{
    public const int MaxAcceptFilterBytes = 1024;

    public int P { get; }
    public long M { get; }
    public byte[] Data { get; }

    public RequestSyncPacket(int p, long m, byte[] data)
    {
        P = p;
        M = m;
        Data = data;
    }

    public byte[] Encode()
    {
        var outBytes = new List<byte>();
        AppendTlv(outBytes, 0x01, new[] { (byte)P });

        var m32 = Math.Min(M, 0xFFFF_FFFFL);
        AppendTlv(outBytes, 0x02, new[]
        {
            (byte)((m32 >> 24) & 0xFF),
            (byte)((m32 >> 16) & 0xFF),
            (byte)((m32 >> 8) & 0xFF),
            (byte)(m32 & 0xFF)
        });
        AppendTlv(outBytes, 0x03, Data);
        return outBytes.ToArray();
    }

    public static RequestSyncPacket? Decode(byte[] data)
    {
        var off = 0;
        int? p = null;
        long? m = null;
        byte[]? payload = null;

        while (off + 3 <= data.Length)
        {
            var t = data[off]; off += 1;
            var len = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(off, 2)); off += 2;
            if (off + len > data.Length) return null;
            var v = data[off..(off + len)]; off += len;

            switch (t)
            {
                case 0x01 when len == 1:
                    p = v[0];
                    break;
                case 0x02 when len == 4:
                    m = BinaryPrimitives.ReadUInt32BigEndian(v);
                    break;
                case 0x03:
                    if (v.Length > MaxAcceptFilterBytes) return null;
                    payload = v;
                    break;
            }
        }

        if (p is not { } pp || m is not { } mm || payload is not { } dd) return null;
        if (pp < 1 || mm <= 0) return null;
        return new RequestSyncPacket(pp, mm, dd);
    }

    private static void AppendTlv(List<byte> outBytes, byte type, byte[] value)
    {
        outBytes.Add(type);
        outBytes.Add((byte)((value.Length >> 8) & 0xFF));
        outBytes.Add((byte)(value.Length & 0xFF));
        outBytes.AddRange(value);
    }
}
