using System.IO;

using System.Text.Json;

namespace Bitchat.Windows;

/// <summary>Persisted client settings (%LOCALAPPDATA%\bitchat\config.json).</summary>
public static class NickStore
{
    private static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "bitchat");

    private static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    private sealed record Config(string? Nickname, IReadOnlyList<string>? Geohashes);

    private static readonly object IoLock = new();

    private static Config Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new Config(null, null);
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            var nick = doc.RootElement.TryGetProperty("nickname", out var n) ? n.GetString() : null;
            var geos = new List<string>();
            if (doc.RootElement.TryGetProperty("geohashes", out var g) && g.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in g.EnumerateArray())
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) geos.Add(s);
                }
            }
            return new Config(string.IsNullOrWhiteSpace(nick) ? null : nick, geos);
        }
        catch
        {
            return new Config(null, null);
        }
    }

    private static void Save(Config config)
    {
        lock (IoLock)
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, options));
            }
            catch
            {
                // best-effort persistence
            }
        }
    }

    public static string? LoadNickname() => Load().Nickname;

    public static void SaveNickname(string nickname)
    {
        var current = Load();
        Save(new Config(nickname, current.Geohashes));
    }

    public static void ClearNickname()
    {
        var current = Load();
        Save(new Config(null, current.Geohashes));
    }

    public static IReadOnlyList<string> LoadGeohashes() => Load().Geohashes ?? Array.Empty<string>();

    public static void SaveGeohashes(IReadOnlyList<string> geohashes)
    {
        var current = Load();
        Save(new Config(current.Nickname, geohashes.Distinct().ToList()));
    }
}
