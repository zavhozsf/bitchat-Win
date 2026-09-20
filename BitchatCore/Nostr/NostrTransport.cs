using System.IO;

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Bitchat.Windows.Nostr;

/// <summary>
/// Minimal Nostr relay client: WebSocket connections, REQ subscriptions with
/// automatic re-subscription on reconnect, EVENT delivery, EVENT publishing.
/// </summary>
public sealed class NostrTransport : IDisposable
{
    private readonly ConcurrentDictionary<string, (string FilterJson, bool Active)> _subscriptions = new();
    private readonly ConcurrentDictionary<string, RelayConnection> _relays = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<string> _log;
    private bool _disposed;

    public event Action<string, NostrEvent>? EventReceived;
    public event Action<int, int>? RelayStatusChanged; // connected, total

    private static readonly string[] DefaultRelays =
    {
        "wss://nostr-02.uid.ovh",
        "wss://relay.ditto.pub",
        "wss://nostr.tac.lol",
        "wss://relay.veganostr.com",
        "wss://relay.wellorder.net",
        "wss://relayone.soundhsa.com"
    };

    public NostrTransport(Action<string> log)
    {
        _log = log;
    }

    public (int Connected, int Total) Status
    {
        get
        {
            var connected = _relays.Values.Count(r => r.IsOpen);
            return (connected, _relays.Count);
        }
    }

    public void Start(IReadOnlyList<string>? relays = null)
    {
        var list = relays ?? DefaultRelays;
        _log($"transport starting {Math.Min(list.Count, 8)} relays");
        foreach (var url in list.Take(8))
        {
            var connection = new RelayConnection(url, HandleMessage, HandleStateChange, _log);
            _relays[url] = connection;
            _ = connection.RunAsync(_cts.Token);
        }
    }

    public void Subscribe(string subId, string filterJson)
    {
        _subscriptions[subId] = (filterJson, true);
        var req = $"[\"REQ\",\"{subId}\",{filterJson}]";
        foreach (var relay in _relays.Values.Where(r => r.IsOpen))
            relay.Send(req);
    }

    public void Unsubscribe(string subId)
    {
        if (_subscriptions.TryRemove(subId, out _))
        {
            var close = $"[\"CLOSE\",\"{subId}\"]";
            foreach (var relay in _relays.Values.Where(r => r.IsOpen))
                relay.Send(close);
        }
    }

    public void Publish(NostrEvent ev)
    {
        var json = $"[\"EVENT\",{ev.ToRelayJson()}]";
        foreach (var relay in _relays.Values.Where(r => r.IsOpen))
            relay.Send(json);
    }

    private void HandleMessage(RelayConnection relay, string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2) return;

            var type = root[0].GetString();
            if (type == "EVENT")
            {
                if (root.GetArrayLength() < 3) return;
                var subId = root[1].GetString() ?? "";
                var ev = NostrEvent.FromJson(root[2]);
                if (ev != null)
                    EventReceived?.Invoke(subId, ev);
            }
            // OK/NOTICE/EOSE/CLOSED - ignored
        }
        catch
        {
            // malformed relay message
        }
    }

    private void HandleStateChange(RelayConnection relay)
    {
        if (_disposed) return;
        var (connected, total) = Status;
        RelayStatusChanged?.Invoke(connected, total);

        // On reconnect, re-subscribe everything.
        if (relay.IsOpen)
        {
            foreach (var (subId, (filterJson, active)) in _subscriptions)
            {
                if (active)
                    relay.Send($"[\"REQ\",\"{subId}\",{filterJson}]");
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _cts.Cancel();
        foreach (var relay in _relays.Values)
            relay.Dispose();
        _relays.Clear();
    }

    public sealed class RelayConnection : IDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private readonly Action<RelayConnection, string> _onMessage;
        private readonly Action<RelayConnection> _onStateChange;
        private readonly Action<string> _log;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly string _url;
        private volatile bool _open;

        public string Url => _url;
        public bool IsOpen => _open;

        public RelayConnection(string url, Action<RelayConnection, string> onMessage,
            Action<RelayConnection> onStateChange, Action<string> log)
        {
            _url = url;
            _onMessage = onMessage;
            _onStateChange = onStateChange;
            _log = log;
        }

        public async Task RunAsync(CancellationToken token)
        {
            var backoff = TimeSpan.FromSeconds(5);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await _socket.ConnectAsync(new Uri(_url), token);
                    _open = true;
                    _onStateChange(this);
                    _log($"relay connected: {_url}");
                    await ReceiveLoop(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log($"relay {_url}: {ex.Message}");
                }
                finally
                {
                    _open = false;
                }
                _onStateChange(this);

                try { await Task.Delay(backoff, token); } catch { break; }
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
            }
        }

        private async Task ReceiveLoop(CancellationToken token)
        {
            var buffer = new byte[16 * 1024];
            var message = new MemoryStream();
            while (_socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, token);
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var text = Encoding.UTF8.GetString(message.ToArray());
                _onMessage(this, text);
            }
        }

        public void Send(string data)
        {
            if (!_open) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await _sendLock.WaitAsync();
                    try
                    {
                        if (!_open) return;
                        var bytes = Encoding.UTF8.GetBytes(data);
                        await _socket.SendAsync(new ArraySegment<byte>(bytes),
                            WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                    finally
                    {
                        _sendLock.Release();
                    }
                }
                catch
                {
                    // send failure - reconnect loop will handle
                }
            });
        }

        public void Dispose()
        {
            try { _socket.Dispose(); } catch { }
        }
    }
}
