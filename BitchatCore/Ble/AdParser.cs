using System.IO;

using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace Bitchat.Windows.Ble;

/// <summary>Parsing of Bluetooth LE advertisement service UUID / service data sections.</summary>
public static class AdParser
{
    public static IEnumerable<Guid> ExtractServiceUuids(BluetoothLEAdvertisement advertisement)
    {
        foreach (var uuid in advertisement.ServiceUuids)
            yield return uuid;

        foreach (var section in advertisement.DataSections)
        {
            var d = section.Data;
            if (d.Length == 0) continue;
            var data = new byte[d.Length];
            using var reader = DataReader.FromBuffer(d);
            reader.ReadBytes(data);

            switch (section.DataType)
            {
                case 0x02: // incomplete 16-bit UUIDs
                case 0x03: // complete 16-bit UUIDs
                    for (var i = 0; i + 2 <= data.Length; i += 2)
                        yield return GuidFromShort(data[i] | (data[i + 1] << 8));
                    break;

                case 0x06: // incomplete 128-bit UUIDs
                case 0x07: // complete 128-bit UUIDs
                    for (var i = 0; i + 16 <= data.Length; i += 16)
                        yield return GuidFromAdBytes(data, i);
                    break;

                case 0x16: // service data (UUID + payload)
                    if (data.Length >= 18)
                        yield return GuidFromAdBytes(data, 0);
                    else if (data.Length >= 2)
                        yield return GuidFromShort(data[0] | (data[1] << 8));
                    break;
            }
        }
    }

    /// <summary>Service data payload for the given 128-bit service UUID (section 0x16), if present.</summary>
    public static byte[]? ExtractServiceData(BluetoothLEAdvertisement advertisement, Guid serviceUuid)
    {
        foreach (var section in advertisement.DataSections)
        {
            if (section.DataType != 0x16) continue;
            var d = section.Data;
            if (d.Length < 18) continue;
            var data = new byte[d.Length];
            using var reader = DataReader.FromBuffer(d);
            reader.ReadBytes(data);
            if (GuidFromAdBytes(data, 0) == serviceUuid)
                return data[16..];
        }
        return null;
    }

    /// <summary>128-bit UUID in Bluetooth AD byte order (little-endian groups).</summary>
    public static Guid GuidFromAdBytes(byte[] data, int offset)
    {
        var b = new byte[16];
        Array.Copy(data, offset, b, 0, 16);
        // reverse of GuidToAdBytes
        var g = new byte[16];
        g[0] = b[3]; g[1] = b[2]; g[2] = b[1]; g[3] = b[0];
        g[4] = b[5]; g[5] = b[4];
        g[6] = b[7]; g[7] = b[6];
        Array.Copy(b, 8, g, 8, 8);
        return new Guid(g);
    }

    /// <summary>128-bit GUID to Bluetooth AD byte order.</summary>
    public static byte[] GuidToAdBytes(Guid guid)
    {
        var b = guid.ToByteArray();
        var outBytes = new byte[16];
        outBytes[0] = b[3]; outBytes[1] = b[2]; outBytes[2] = b[1]; outBytes[3] = b[0];
        outBytes[4] = b[5]; outBytes[5] = b[4];
        outBytes[6] = b[7]; outBytes[7] = b[6];
        Array.Copy(b, 8, outBytes, 8, 8);
        return outBytes;
    }

    private static Guid GuidFromShort(int uuid16) =>
        new((short)uuid16, 0x0000, 0x1000, 0x80, 0x00, 0x00, 0x80, 0x5F, 0x9B, 0x34, 0xFB);
}
