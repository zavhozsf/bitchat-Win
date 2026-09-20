using System.IO;

using System.Text;

namespace Bitchat.Windows;

/// <summary>Standard geohash encoder (base32, interleaved lon/lat bits).</summary>
public static class GeoUtils
{
    private const string Base32 = "0123456789bcdefghjkmnpqrstuvwxyz";

    public static string Encode(double latitude, double longitude, int precision)
    {
        if (precision is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(precision));
        if (latitude < -90 || latitude > 90) throw new ArgumentOutOfRangeException(nameof(latitude));
        if (longitude < -180 || longitude > 180) throw new ArgumentOutOfRangeException(nameof(longitude));

        double latMin = -90, latMax = 90, lonMin = -180, lonMax = 180;
        var hash = new StringBuilder(precision);
        var bits = 0;
        var bit = 0;
        var even = true; // even bit index → longitude bit

        while (hash.Length < precision)
        {
            if (even)
            {
                var mid = (lonMin + lonMax) / 2;
                if (longitude >= mid) { bit = (bit << 1) | 1; lonMin = mid; }
                else { bit <<= 1; lonMax = mid; }
            }
            else
            {
                var mid = (latMin + latMax) / 2;
                if (latitude >= mid) { bit = (bit << 1) | 1; latMin = mid; }
                else { bit <<= 1; latMax = mid; }
            }
            even = !even;
            bits++;
            if (bits == 5)
            {
                hash.Append(Base32[bit]);
                bits = 0;
                bit = 0;
            }
        }
        return hash.ToString();
    }

    /// <summary>Channel precision presets used by bitchat.</summary>
    public static string ChannelGeohash(double lat, double lon, ChannelScope scope) => scope switch
    {
        ChannelScope.Block => Encode(lat, lon, 7),
        ChannelScope.Neighborhood => Encode(lat, lon, 6),
        _ => Encode(lat, lon, 5) // City
    };
}

public enum ChannelScope { City, Neighborhood, Block }
