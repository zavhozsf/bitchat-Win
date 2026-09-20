using System.IO;

using System.Net.Http;
using System.Text.Json;

namespace Bitchat.Windows;

/// <summary>
/// Approximate location via public IP geolocation services (no keys required).
/// Returns a geohash channel suggestion - the same flow the phone clients use
/// (globe picker), minus the globe.
/// </summary>
public static class LocationService
{
    public sealed record Detected(string Geohash, double Latitude, double Longitude, string? City, string? Country);

    private sealed record Coords(double Lat, double Lon, string? City, string? Country);

    public static async Task<Detected?> DetectAsync(ChannelScope scope = ChannelScope.City)
    {
        Coords? coords = null;

        foreach (var attempt in new Func<Task<Coords?>>[] { TryIpapiCo, TryIpInfoIo, TryIpApiCom })
        {
            try
            {
                coords = await attempt();
                if (coords != null) break;
            }
            catch { }
        }

        if (coords == null) return null;
        return new Detected(GeoUtils.ChannelGeohash(coords.Lat, coords.Lon, scope), coords.Lat, coords.Lon, coords.City, coords.Country);
    }

    private static async Task<Coords?> TryIpapiCo()
    {
        using var http = CreateClient();
        var json = await http.GetStringAsync("https://ipapi.co/json/");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!TryGetDouble(root, "latitude", out var lat) || !TryGetDouble(root, "longitude", out var lon)) return null;
        var city = root.TryGetProperty("city", out var ci) ? ci.GetString() : null;
        var country = root.TryGetProperty("country_name", out var co) ? co.GetString() : null;
        return new Coords(lat, lon, city, country);
    }

    private static async Task<Coords?> TryIpInfoIo()
    {
        using var http = CreateClient();
        var json = await http.GetStringAsync("https://ipinfo.io/json");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("loc", out var loc)) return null;
        var parts = (loc.GetString() ?? "").Split(',');
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var lat) ||
            !double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var lon)) return null;
        var city = root.TryGetProperty("city", out var ci) ? ci.GetString() : null;
        var country = root.TryGetProperty("country", out var co) ? co.GetString() : null;
        return new Coords(lat, lon, city, country);
    }

    private static async Task<Coords?> TryIpApiCom()
    {
        using var http = CreateClient();
        var json = await http.GetStringAsync("http://ip-api.com/json/");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!TryGetDouble(root, "lat", out var lat) || !TryGetDouble(root, "lon", out var lon)) return null;
        var city = root.TryGetProperty("city", out var ci) ? ci.GetString() : null;
        var country = root.TryGetProperty("country", out var co) ? co.GetString() : null;
        return new Coords(lat, lon, city, country);
    }

    private static bool TryGetDouble(JsonElement element, string name, out double value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Number) return false;
        return prop.TryGetDouble(out value);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("bitchat-windows/1.0");
        return client;
    }
}
