using System.IO;

using System.Security.Cryptography;

namespace Bitchat.Windows.Mesh;

/// <summary>
/// Golomb-Coded Set (GCS) filter for gossip sync — byte-compatible port of the
/// Android/iOS bitchat implementation.
/// h64(id) = first 8 bytes of SHA-256 over the 16-byte PacketId (big-endian, positive);
/// values mapped to [0, M) via (h64 % M); deltas Golomb-Rice coded with parameter P,
/// bitstream packed MSB-first.
/// </summary>
public static class GcsFilter
{
    public sealed record Params(int P, long M, byte[] Data);

    public static int DeriveP(double targetFpr)
    {
        var f = Math.Clamp(targetFpr, 0.000001, 0.25);
        return Math.Max(1, (int)Math.Ceiling(Math.Log(1.0 / f) / Math.Log(2.0)));
    }

    public static int EstimateMaxElementsForSize(int bytes, int p)
    {
        var bits = Math.Max(bytes * 8, 8);
        var per = Math.Max(p + 2, 3);
        return Math.Max(1, bits / per);
    }

    public static Params BuildFilter(IReadOnlyList<byte[]> ids, int maxBytes, double targetFpr)
    {
        var p = DeriveP(targetFpr);
        var nCap = EstimateMaxElementsForSize(maxBytes, p);
        var trimmedN = Math.Min(ids.Count, nCap);

        var finalM = Math.Max((long)trimmedN << p, 1L);
        var selected = ids.Take(trimmedN).ToList();
        var mapped = selected
            .Select(id => MapToRange(id, finalM))
            .Distinct()
            .OrderBy(v => v)
            .ToList();
        var encoded = Encode(mapped, p);

        while (encoded.Length > maxBytes && trimmedN > 0)
        {
            trimmedN = trimmedN * 9 / 10;
            finalM = Math.Max((long)trimmedN << p, 1L);
            selected = ids.Take(trimmedN).ToList();
            mapped = selected
                .Select(id => MapToRange(id, finalM))
                .Distinct()
                .OrderBy(v => v)
                .ToList();
            encoded = Encode(mapped, p);
        }

        return new Params(p, finalM, encoded);
    }

    private static long MapToRange(byte[] id16, long m)
    {
        var v = H64(id16) % m;
        return v == 0 ? 1 : v;
    }

    public static long H64(byte[] id16)
    {
        var digest = SHA256.HashData(id16);
        long x = 0;
        for (var i = 0; i < 8; i++)
            x = (x << 8) | digest[i];
        return x & 0x7FFF_FFFF_FFFF_FFFFL;
    }

    public static long[] DecodeToSortedSet(int p, long m, byte[] data)
    {
        var values = new List<long>();
        var reader = new BitReader(data);
        long acc = 0;
        while (!reader.Eof)
        {
            long q = 0;
            while (true)
            {
                var b = reader.ReadBit();
                if (b == null) break;
                if (b == 1) q++;
                else break;
            }
            if (reader.LastWasEof) break;

            var r = reader.ReadBits(p);
            if (r == null) break;

            var x = (q << p) + r.Value + 1;
            acc += x;
            if (acc >= m) break;
            values.Add(acc);
        }
        return values.ToArray();
    }

    public static bool Contains(long[] sortedValues, long candidate)
    {
        var lo = 0;
        var hi = sortedValues.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var v = sortedValues[mid];
            if (v == candidate) return true;
            if (v < candidate) lo = mid + 1;
            else hi = mid - 1;
        }
        return false;
    }

    private static byte[] Encode(List<long> sorted, int p)
    {
        var bw = new BitWriter();
        long prev = 0;
        var mask = (1L << p) - 1;
        foreach (var v in sorted)
        {
            var x = v - prev;
            prev = v;
            var q = (x - 1) >> p;
            var r = (x - 1) & mask;
            for (var i = 0; i < q; i++) bw.WriteBit(1);
            bw.WriteBit(0);
            bw.WriteBits(r, p);
        }
        return bw.ToByteArray();
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _buf = new();
        private int _cur;
        private int _nbits;

        public void WriteBit(int bit)
        {
            _cur = (_cur << 1) | (bit & 1);
            _nbits++;
            if (_nbits == 8)
            {
                _buf.Add((byte)_cur);
                _cur = 0;
                _nbits = 0;
            }
        }

        public void WriteBits(long value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
                WriteBit((int)((value >> i) & 1));
        }

        public byte[] ToByteArray()
        {
            if (_nbits > 0)
            {
                _buf.Add((byte)(_cur << (8 - _nbits)));
                _cur = 0;
                _nbits = 0;
            }
            return _buf.ToArray();
        }
    }

    private sealed class BitReader
    {
        private readonly byte[] _data;
        private int _index;
        private int _cur;
        private int _left = 8;

        public BitReader(byte[] data)
        {
            _data = data;
            _cur = data.Length > 0 ? data[0] : 0;
        }

        public bool Eof => _index >= _data.Length;
        public bool LastWasEof { get; private set; }

        public int? ReadBit()
        {
            if (_index >= _data.Length)
            {
                LastWasEof = true;
                return null;
            }
            var bit = (_cur >> 7) & 1;
            _cur = (_cur << 1) & 0xFF;
            _left--;
            if (_left == 0)
            {
                _index++;
                if (_index < _data.Length)
                {
                    _cur = _data[_index];
                    _left = 8;
                }
            }
            return bit;
        }

        public long? ReadBits(int count)
        {
            long v = 0;
            for (var k = 0; k < count; k++)
            {
                var b = ReadBit();
                if (b == null) return null;
                v = (v << 1) | b.Value;
            }
            return v;
        }
    }
}
