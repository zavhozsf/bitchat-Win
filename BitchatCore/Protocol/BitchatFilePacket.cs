using System.IO;

using System.Buffers.Binary;
using System.Text;

namespace Bitchat.Windows.Protocol;

/// <summary>
/// BitchatFilePacket: TLV file transfer payload — byte-compatible with the mobile clients.
/// TLVs: 0x01 filename (u16-len), 0x02 size (4 bytes), 0x03 mime (u16-len),
/// 0x04 content (single TLV, 4-byte length). Unknown TLVs are skipped.
/// The outer packet uses version 2 so payloads above 64 KiB are possible.
/// </summary>
public sealed class BitchatFilePacket
{
    public string FileName { get; }
    public long FileSize { get; }
    public string MimeType { get; }
    public byte[] Content { get; }

    public BitchatFilePacket(string fileName, long fileSize, string mimeType, byte[] content)
    {
        FileName = fileName;
        FileSize = fileSize;
        MimeType = mimeType;
        Content = content;
    }

    public byte[] Encode()
    {
        var nameBytes = Encoding.UTF8.GetBytes(FileName);
        var mimeBytes = Encoding.UTF8.GetBytes(MimeType);
        if (nameBytes.Length > 0xFFFF || mimeBytes.Length > 0xFFFF) return Array.Empty<byte>();

        var capacity = (1 + 2 + nameBytes.Length) + (1 + 2 + 4) + (1 + 2 + mimeBytes.Length) +
                       (1 + 4 + Content.Length);
        var buf = new byte[capacity];
        var off = 0;

        buf[off++] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(off, 2), (ushort)nameBytes.Length); off += 2;
        Array.Copy(nameBytes, 0, buf, off, nameBytes.Length); off += nameBytes.Length;

        buf[off++] = 0x02;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(off, 2), 4); off += 2;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(off, 4), (uint)Math.Min(FileSize, int.MaxValue)); off += 4;

        buf[off++] = 0x03;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(off, 2), (ushort)mimeBytes.Length); off += 2;
        Array.Copy(mimeBytes, 0, buf, off, mimeBytes.Length); off += mimeBytes.Length;

        buf[off++] = 0x04;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(off, 4), (uint)Content.Length); off += 4;
        Array.Copy(Content, 0, buf, off, Content.Length); off += Content.Length;

        return buf;
    }

    public static BitchatFilePacket? Decode(byte[] data)
    {
        try
        {
            var off = 0;
            string? name = null;
            long? size = null;
            string? mime = null;
            byte[]? content = null;

            while (off < data.Length)
            {
                if (data.Length - off < 3) return null;
                var t = data[off]; off += 1;

                int len;
                if (t == 0x04)
                {
                    if (off + 4 > data.Length) return null;
                    len = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(off, 4));
                    off += 4;
                }
                else
                {
                    if (off + 2 > data.Length) return null;
                    len = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(off, 2));
                    off += 2;
                }
                if (len < 0 || off + len > data.Length) return null;
                var value = data[off..(off + len)];
                off += len;

                switch (t)
                {
                    case 0x01: name = Encoding.UTF8.GetString(value); break;
                    case 0x02:
                        if (len != 4) return null;
                        size = BinaryPrimitives.ReadUInt32BigEndian(value);
                        break;
                    case 0x03: mime = Encoding.UTF8.GetString(value); break;
                    case 0x04: content = content == null ? value : content.Concat(value).ToArray(); break;
                }
            }

            var n = name;
            var c = content;
            if (n == null || c == null) return null;
            return new BitchatFilePacket(n, size ?? c.Length, mime ?? "application/octet-stream", c);
        }
        catch
        {
            return null;
        }
    }
}
