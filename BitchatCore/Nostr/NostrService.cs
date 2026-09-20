using System.IO;

using System.Collections.Concurrent;
using System.Text.Json;

namespace Bitchat.Windows.Nostr;

/// <summary>
/// Geohash location channels over Nostr: kind 20000 ephemeral chat messages with
/// ["g", geohash] and ["n", nickname] tags - protocol-compatible with bitchat mobile.
/// </summary>
public sealed class NostrService : IDisposable
{
    private readonly NostrTransport _transport;
    private readonly Func<string> _nicknameProvider;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<string, byte[]> _geohashIdentities = new();
    private readonly ConcurrentDictionary<string, byte> _seenEventIds = new();
    private readonly ConcurrentDictionary<string, byte> _joined = new();

    public event Action<string, string?, string, string>? MessageReceived;
    public event Action<string>? Joined;
    public event Action<int, int>? RelayStatusChanged;

    public NostrService(Func<string> nicknameProvider, Action<string> log)
    {
        _nicknameProvider = nicknameProvider;
        _log = log;
        _transport = new NostrTransport(log);
        _transport.EventReceived += OnEvent;
        _transport.RelayStatusChanged += (c, t) => RelayStatusChanged?.Invoke(c, t);
    }

    public (int Connected, int Total) RelayStatus => _transport.Status;
    public IReadOnlyCollection<string> JoinedGeohashes => _joined.Keys.ToArray();

    /// <summary>Join a geohash channel: derive identity + subscribe on relays.</summary>
    public void JoinGeohash(string geohash)
    {
        geohash = Sanitize(geohash);
        var identity = _geohashIdentities.GetOrAdd(geohash, NostrIdentity.DeriveForGeohash);

        if (_joined.TryAdd(geohash, 0))
        {
            var since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600;
            var filterJson = $"{{\"kinds\":[20000,20001],\"#g\":[\"{geohash}\"],\"since\":{since},\"limit\":200}}";
            _transport.Subscribe($"geohash-{geohash}", filterJson);
            _log($"joined geohash #{geohash}");
            Joined?.Invoke(geohash);
        }
        else
        {
            var since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600;
            _transport.Subscribe($"geohash-{geohash}",
                $"{{\"kinds\":[20000,20001],\"#g\":[\"{geohash}\"],\"since\":{since},\"limit\":200}}");
        }
    }

    public void LeaveGeohash(string geohash)
    {
        var g = Sanitize(geohash);
        _joined.TryRemove(g, out _);
        _transport.Unsubscribe($"geohash-{g}");
    }

    public Task StartAsync()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                _log("nostr transport starting (fetching relay list)…");
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var addrs = await System.Net.Dns.GetHostAddressesAsync("raw.githubusercontent.com");
                    _log($"dns ok ({addrs.Length} addrs, {sw.ElapsedMilliseconds}ms)");
                    foreach (var (host, port) in new[] { ("raw.githubusercontent.com", 443), ("nostr-02.uid.ovh", 443) })
                    {
                        using var tcp = new System.Net.Sockets.TcpClient();
                        sw.Restart();
                        var connectTask = tcp.ConnectAsync(host, port);
                        var finished = await Task.WhenAny(connectTask, Task.Delay(5000));
                        if (finished == connectTask && tcp.Connected)
                            _log($"tcp ok {host}:443 in {sw.ElapsedMilliseconds}ms");
                        else
                            _log($"tcp TIMEOUT {host}:443 (5s) - outbound blocked for this process?");
                    }
                }
                catch (Exception nex) { _log($"network self-test failed: {nex.Message}"); }
                var relays = await FetchRelayListAsync();
                _log($"relay list: {(relays == null ? "defaults" : relays.Count + " fetched")}");
                _transport.Start(relays);
            }
            catch (Exception ex)
            {
                _log($"nostr transport start failed: {ex.Message}");
                try { _transport.Start(null); } catch (Exception ex2) { _log($"nostr start error: {ex2.Message}"); }
            }
        });
        return Task.CompletedTask;
    }

    public void Send(string geohash, string content)
    {
        var g = Sanitize(geohash);
        var priv = _geohashIdentities.GetOrAdd(g, NostrIdentity.DeriveForGeohash);
        var pubkey = Convert.ToHexString(NostrCrypto.PublicKeyFromPrivate(priv)).ToLowerInvariant();

        var ev = new NostrEvent
        {
            Pubkey = pubkey,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Kind = 20000,
            Tags = new[] { new[] { "g", g }, new[] { "n", _nicknameProvider() } },
            Content = content
        };
        ev.Sign(priv);
        _transport.Publish(ev);
    }

    private void OnEvent(string subId, NostrEvent ev)
    {
        if (ev.Id == null || !_seenEventIds.TryAdd(ev.Id, 0)) return;
        if (_seenEventIds.Count > 10_000)
        {
            foreach (var key in _seenEventIds.Keys.Take(2000))
                _seenEventIds.TryRemove(key, out _);
        }

        if (!ev.Verify())
        {
            _log($"dropped nostr event with bad signature {(ev.Id.Length > 12 ? ev.Id[..12] : ev.Id)}…");
            return;
        }

        var geohash = ev.TagValue("g") ?? subId["geohash-".Length..];
        var nickname = ev.TagValue("n");

        if (ev.Kind == 20000 && ev.Content.Length > 0)
        {
            MessageReceived?.Invoke(geohash, nickname, ev.Content, ev.Pubkey);
        }
        // kind 20001 - presence, ignored for chat display
    }

    private static string Sanitize(string geohash)
    {
        var cleaned = new string(geohash.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return cleaned.Length is < 1 or > 12 ? throw new ArgumentException("bad geohash") : cleaned;
    }

    private static async Task<IReadOnlyList<string>?> FetchRelayListAsync()
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var csv = await http.GetStringAsync(
                "https://raw.githubusercontent.com/permissionlesstech/georelays/refs/heads/main/nostr_relays.csv", cts.Token);
            var urls = new List<string>();
            foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
            {
                var host = line.Split(',')[0].Trim();
                if (host.Length == 0) continue;
                if (!host.Contains("://")) host = "wss://" + host;
                else host = host.Replace("http://", "wss://").Replace("https://", "wss://");
                urls.Add(host);
                if (urls.Count >= 12) break;
            }
            return urls.Count > 0 ? urls : null;
        }
        catch
        {
            return null; // transport falls back to embedded defaults
        }
    }

    public void Dispose()
    {
        _transport.Dispose();
    }
}
