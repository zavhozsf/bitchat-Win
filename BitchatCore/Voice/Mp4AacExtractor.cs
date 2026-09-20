using System.Text;

namespace Bitchat.Windows.Voice;

/// <summary>
/// Extracts individual AAC access units from a Media Foundation encoded stream.
/// MF's SinkWriter produces MP4/M4A containers - this parses the 'mdat' box using
/// 'stsz' sample sizes to split into individual access units.
/// </summary>
public static class Mp4AacExtractor
{
    public static List<byte[]> Extract(byte[] data)
    {
        var result = new List<byte[]>();
        byte[]? mdat = null;
        var sampleSizes = new List<int>();

        var pos = 0;
        while (pos + 8 <= data.Length)
        {
            var boxSize = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            if (boxSize < 8 || pos + boxSize > data.Length) break;
            var type = Encoding.ASCII.GetString(data, pos + 4, 4);

            if (type == "mdat")
            {
                mdat = data[(pos + 8)..(pos + (int)boxSize)];
            }
            else if (type == "moov")
            {
                ParseStsz(data, pos + 8, pos + (int)boxSize, sampleSizes);
            }
            pos += (int)boxSize;
        }

        if (mdat != null && sampleSizes.Count > 0)
        {
            var offset = 0;
            foreach (var size in sampleSizes)
            {
                if (offset + size > mdat.Length) break;
                if (size > 0) result.Add(mdat[offset..(offset + size)]);
                offset += size;
            }
        }

        return result;
    }

    private static void ParseStsz(byte[] data, int start, int end, List<int> sizes)
    {
        var p = start;
        while (p + 8 <= end)
        {
            if (data[p] == (byte)'s' && data[p + 1] == (byte)'t' && data[p + 2] == (byte)'s' && data[p + 3] == (byte)'z')
            {
                // stsz: version/flags(4) + sample_size(4) + sample_count(4) + entries
                var sampleCount = (data[p + 12] << 24) | (data[p + 13] << 16) | (data[p + 14] << 8) | data[p + 15];
                var defaultSize = (data[p + 8] << 24) | (data[p + 9] << 16) | (data[p + 10] << 8) | data[p + 11];

                if (defaultSize > 0)
                {
                    // Fixed-size samples
                    for (var i = 0; i < sampleCount; i++) sizes.Add(defaultSize);
                }
                else
                {
                    // Variable-size: entries follow
                    var entryOff = p + 16;
                    for (var i = 0; i < sampleCount && entryOff + 4 <= end; i++)
                    {
                        var sz = (data[entryOff] << 24) | (data[entryOff + 1] << 16) | (data[entryOff + 2] << 8) | data[entryOff + 3];
                        sizes.Add(sz);
                        entryOff += 4;
                    }
                }
                return;
            }
            p++;
        }
    }
}
