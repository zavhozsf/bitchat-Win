using System.IO;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bitchat.Windows.Nostr;

/// <summary>NIP-01 event with id computation, signing and verification.</summary>
public sealed class NostrEvent
{
    public string Pubkey { get; set; } = "";
    public long CreatedAt { get; set; }
    public int Kind { get; set; }
    public string[][] Tags { get; set; } = Array.Empty<string[]>();
    public string Content { get; set; } = "";
    public string? Id { get; set; }
    public string? Sig { get; set; }

    public string? TagValue(string tagName)
    {
        foreach (var tag in Tags)
        {
            if (tag.Length >= 2 && tag[0] == tagName) return tag[1];
        }
        return null;
    }

    /// <summary>NIP-01 canonical serialization: [0,pubkey,created_at,kind,tags,content], no whitespace.</summary>
    public string SerializeForId()
    {
        var sb = new StringBuilder(256);
        sb.Append("[0,").Append(Json(Pubkey)).Append(',').Append(CreatedAt).Append(',')
          .Append(Kind).Append(',').Append(TagsJson()).Append(',').Append(Json(Content)).Append(']');
        return sb.ToString();
    }

    public string TagsJson()
    {
        var sb = new StringBuilder(64);
        sb.Append('[');
        for (var i = 0; i < Tags.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('[');
            for (var j = 0; j < Tags[i].Length; j++)
            {
                if (j > 0) sb.Append(',');
                sb.Append(Json(Tags[i][j]));
            }
            sb.Append(']');
        }
        sb.Append(']');
        return sb.ToString();
    }

    public static string Json(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    public string ComputeId() =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SerializeForId()))).ToLowerInvariant();

    public void Sign(byte[] privateKey32)
    {
        Id = ComputeId();
        var aux = new byte[32];
        RandomNumberGenerator.Fill(aux);
        Sig = Convert.ToHexString(NostrCrypto.Sign(
            Convert.FromHexString(Id!), privateKey32, aux)).ToLowerInvariant();
    }

    public bool Verify()
    {
        if (Id == null || Sig == null || Pubkey.Length != 64) return false;
        if (ComputeId() != Id) return false;
        return NostrCrypto.Verify(Convert.FromHexString(Id), Convert.FromHexString(Pubkey), Convert.FromHexString(Sig));
    }

    /// <summary>Full event JSON for relay publishing.</summary>
    public string ToRelayJson()
    {
        var sb = new StringBuilder(256);
        sb.Append("{\"id\":").Append(Json(Id ?? "")).Append(",\"pubkey\":").Append(Json(Pubkey))
          .Append(",\"created_at\":").Append(CreatedAt)
          .Append(",\"kind\":").Append(Kind)
          .Append(",\"tags\":").Append(TagsJson())
          .Append(",\"content\":").Append(Json(Content))
          .Append(",\"sig\":").Append(Json(Sig ?? ""))
          .Append('}');
        return sb.ToString();
    }

    public static NostrEvent? FromJson(JsonElement element)
    {
        try
        {
            var ev = new NostrEvent
            {
                Id = element.TryGetProperty("id", out var id) ? id.GetString() : null,
                Pubkey = element.TryGetProperty("pubkey", out var pubkey) ? pubkey.GetString() ?? "" : "",
                CreatedAt = element.TryGetProperty("created_at", out var ca) ? ca.GetInt64() : 0,
                Kind = element.TryGetProperty("kind", out var kind) ? kind.GetInt32() : 0,
                Content = element.TryGetProperty("content", out var content) ? content.GetString() ?? "" : "",
                Sig = element.TryGetProperty("sig", out var sig) ? sig.GetString() : null
            };
            if (element.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string[]>();
                foreach (var tag in tags.EnumerateArray())
                {
                    var parts = new List<string>();
                    foreach (var part in tag.EnumerateArray())
                        parts.Add(part.GetString() ?? "");
                    list.Add(parts.ToArray());
                }
                ev.Tags = list.ToArray();
            }
            return ev;
        }
        catch
        {
            return null;
        }
    }
}
