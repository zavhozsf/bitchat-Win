using System.IO;

using System.Security.Cryptography;
using System.Text;

namespace Bitchat.Windows.Nostr;

/// <summary>
/// Nostr identity derivation matching the mobile clients: a persisted random device
/// seed, then per-geohash keys via HMAC-SHA256(seed, geohash || iterationLE32) with
/// validity retries and a SHA-256(seed || geohash) fallback.
/// </summary>
public static class NostrIdentity
{
    private static string SeedPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "bitchat", "nostr-seed.bin");

    private static byte[]? _seed;

    public static byte[] DeviceSeed()
    {
        if (_seed != null) return _seed;
        try
        {
            if (File.Exists(SeedPath))
            {
                var loaded = File.ReadAllBytes(SeedPath);
                if (loaded.Length == 32)
                {
                    _seed = loaded;
                    return _seed;
                }
            }
        }
        catch { }

        var seed = new byte[32];
        RandomNumberGenerator.Fill(seed);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SeedPath)!);
            File.WriteAllBytes(SeedPath, seed);
        }
        catch { }
        _seed = seed;
        return _seed;
    }

    /// <summary>Stable per-device, per-geohash Nostr private key (32 bytes).</summary>
    public static byte[] DeriveForGeohash(string geohash)
    {
        var seed = DeviceSeed();
        var geohashBytes = Encoding.UTF8.GetBytes(geohash);

        for (uint i = 0; i < 10; i++)
        {
            using var hmac = new HMACSHA256(seed);
            var input = geohashBytes.Concat(BitConverter.GetBytes(i)).ToArray();
            var candidate = hmac.ComputeHash(input);
            if (NostrCrypto.ValidPrivateKey(candidate))
                return candidate;
        }

        // Fallback exactly like the mobile clients
        return SHA256.HashData(seed.Concat(geohashBytes).ToArray());
    }
}
