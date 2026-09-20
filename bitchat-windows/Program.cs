using System.Text;
using Bitchat.Windows;
using Bitchat.Windows.Mesh;

namespace Bitchat.Windows;

internal static class Program
{
    private static readonly object ConsoleLock = new();
    private static BitchatRuntime? _runtime;

    private static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var nickname = await BitchatRuntime.ResolveNicknameAsync(ResolveArg(args, "--nick") ?? ResolveArg(args, "-n"));
        var identityPath = ResolveArg(args, "--identity");
        var debug = args.Contains("--scan-debug") || args.Contains("--debug") ||
                    Environment.GetEnvironmentVariable("BITCHAT_SCAN_DEBUG") == "1";

        _runtime = new BitchatRuntime(nickname, identityPath, debug);
        var mesh = _runtime.Mesh;
        _runtime.SystemLog += msg => PrintLine(msg, ConsoleColor.DarkGray);
        _runtime.NostrMessageReceived += (geohash, nick, content) =>
            PrintLine($"[#$geohash] {nick ?? "nostr"}: {content}", ConsoleColor.Cyan);

        PrintLine($"bitchat (console) — peer id {_runtime.Noise.MyPeerId}", ConsoleColor.DarkCyan);
        PrintLine($"nickname: {nickname} ({(NickStore.LoadNickname() != null ? "pinned" : "device default, reset: /nick reset")})",
            ConsoleColor.DarkCyan);

        mesh.MessageReceived += OnMessage;
        mesh.SystemMessage += msg => PrintLine(msg, ConsoleColor.DarkGray);
        mesh.PeersChanged += _ => { };
        MeshEngine.SetStaticLog(msg => PrintLine(msg, ConsoleColor.DarkYellow));
        MeshEngine.DebugPackets = debug;

        var bleOk = await _runtime.StartAsync();
        if (!bleOk)
        {
            PrintLine("BLE transport failed to start. Check that Bluetooth is on.", ConsoleColor.Red);
        }
        else
        {
            PrintLine("BLE mesh running. Nearby phones with bitchat will appear automatically.", ConsoleColor.DarkCyan);
        }

        PrintHelp();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _runtime?.Stop();
            Environment.Exit(0);
        };

        while (true)
        {
            var line = Console.ReadLine();
            if (line == null) break;
            HandleInput(line.Trim());
        }
    }

    private static string? ResolveArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    private static void OnMessage(ChatMessage message)
    {
        var time = message.Timestamp.LocalDateTime.ToString("HH:mm");
        if (message.IsFromMe)
        {
            var prefix = message.IsPrivate ? " (to " + (message.Status ?? "") + ")" : "";
            PrintLine($"[{time}] me{prefix}: {message.Content}", ConsoleColor.DarkGreen);
        }
        else if (message.IsPrivate)
        {
            PrintLine($"[{time}] *{message.Sender} (PM): {message.Content}", ConsoleColor.Magenta);
        }
        else if (message.FilePath != null)
        {
            PrintLine($"[{time}] {message.Sender}: 📎 {message.FileName} ({message.FileSizeBytes / 1024.0:F0} KB) → {message.FilePath}", ConsoleColor.Cyan);
        }
        else
        {
            PrintLine($"[{time}] {message.Sender}: {message.Content}", ConsoleColor.White);
        }
    }

    private static void HandleInput(string input)
    {
        if (input.Length == 0 || _runtime == null) return;

        if (input.StartsWith('/'))
        {
            HandleCommand(input);
            return;
        }
        _runtime.Mesh.SendBroadcast(input);
    }

    private static void HandleCommand(string command)
    {
        var runtime = _runtime!;
        var mesh = runtime.Mesh;
        var parts = command.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();

        switch (cmd)
        {
            case "/help":
                PrintHelp();
                break;

            case "/peers":
            {
                var peers = mesh.GetPeersSnapshot();
                if (peers.Count == 0)
                    PrintLine("no peers yet — waiting for nearby bitchat devices…", ConsoleColor.DarkGray);
                else
                {
                    PrintLine($"peers ({peers.Count}):", ConsoleColor.Cyan);
                    foreach (var peer in peers.OrderByDescending(p => p.LastSeen))
                        PrintLine($"  {peer.Nickname,-15} {peer.PeerId}", ConsoleColor.Cyan);
                }
                break;
            }

            case "/msg":
            {
                if (parts.Length < 3)
                {
                    PrintLine("usage: /msg <nickname|peer-id> <text>", ConsoleColor.Yellow);
                    break;
                }
                var peer = FindPeer(parts[1]);
                if (peer == null)
                {
                    PrintLine($"no peer named '{parts[1]}' — try /peers", ConsoleColor.Yellow);
                    break;
                }
                mesh.SendPrivate(parts[2], peer.PeerId);
                break;
            }

            case "/nick":
            {
                if (parts.Length < 2)
                {
                    PrintLine("usage: /nick <name> | /nick reset", ConsoleColor.Yellow);
                    break;
                }
                if (parts[1] == "reset")
                {
                    runtime.ResetNickname();
                    PrintLine($"nickname reset to device default: {runtime.Nickname}", ConsoleColor.DarkGray);
                }
                else
                {
                    runtime.SetNickname(parts[1]);
                    PrintLine($"nickname set to {runtime.Nickname} (pinned) — re-announced", ConsoleColor.DarkGray);
                }
                break;
            }

            case "/links":
                PrintLine($"ble: {_runtime.Ble.GetStatus()}", ConsoleColor.Cyan);
                break;

            case "/debug":
                runtime.DebugEnabled = !runtime.DebugEnabled;
                runtime.Ble.ScanDebug = runtime.DebugEnabled;
                MeshEngine.DebugPackets = runtime.DebugEnabled;
                PrintLine($"packet debug: {(runtime.DebugEnabled ? "ON" : "OFF")}", ConsoleColor.DarkGray);
                break;

            case "/geo":
                if (parts.Length >= 2)
                {
                    _ = _runtime.JoinGeohashAsync(parts[1]);
                    PrintLine($"joining geohash #{parts[1]}…", ConsoleColor.DarkGray);
                }
                else
                {
                    _ = JoinLocationAsync();
                }
                break;
            case "/file":
                if (parts.Length >= 3)
                {
                    var peer = FindPeer(parts[1]);
                    if (peer == null)
                    {
                        PrintLine($"no peer '{parts[1]}'", ConsoleColor.Yellow);
                        break;
                    }
                    _runtime.Mesh.SendFilePrivate(peer.PeerId, parts[2]);
                    PrintLine($"→ file to {peer.Nickname}", ConsoleColor.DarkGray);
                }
                else if (parts.Length == 2)
                {
                    _runtime.Mesh.SendFileBroadcast(parts[1]);
                    PrintLine("→ file broadcast", ConsoleColor.DarkGray);
                }
                else PrintLine("usage: /file [nick] <path>", ConsoleColor.Yellow);
                break;
            case "/voice":
                if (_runtime.Mesh.Voice.IsRecording)
                {
                    var dur = _runtime.Mesh.StopVoiceRecording();
                    PrintLine($"🎙 отправлено ({dur / 1000.0:F0} с)", ConsoleColor.DarkGray);
                }
                else
                {
                    _runtime.Mesh.SetVoiceTarget(false, null);
                    _runtime.Mesh.StartVoiceRecording();
                    PrintLine("🎙 запись… /voice ещё раз чтобы остановить и отправить", ConsoleColor.DarkGray);
                }
                break;
            case "/here":
                _ = JoinLocationAsync();
                break;

            case "/announce":
                mesh.SendAnnouncement();
                PrintLine("announcement sent", ConsoleColor.DarkGray);
                break;

            case "/status":
                PrintLine($"mesh: peer {mesh.MyPeerId}, {mesh.GetPeersSnapshot().Count} peers, nick {runtime.Nickname}", ConsoleColor.Cyan);
                PrintLine($"ble:  {_runtime.Ble.GetStatus()}", ConsoleColor.Cyan);
                if (_runtime.Nostr != null)
                {
                    var (conn, total) = _runtime.NostrRelayCount;
                    PrintLine($"nostr: {conn}/{total} relays, channels: {string.Join(", ", _runtime.Nostr.JoinedGeohashes)}", ConsoleColor.Cyan);
                }
                break;

            case "/quit":
            case "/exit":
                runtime.Stop();
                Environment.Exit(0);
                break;

            default:
                PrintLine($"unknown command {cmd} — /help", ConsoleColor.Yellow);
                break;
        }
    }

    private static async Task JoinLocationAsync()
    {
        PrintLine("detecting location via IP…", ConsoleColor.DarkGray);
        var detected = await LocationService.DetectAsync();
        if (detected == null)
        {
            PrintLine("location detection failed — use /geo <geohash>", ConsoleColor.Yellow);
            return;
        }
        var place = detected.City != null ? $"{detected.City} ({detected.Country})" : $"{detected.Latitude:F2}, {detected.Longitude:F2}";
        PrintLine($"📍 {place} → #{detected.Geohash}", ConsoleColor.Cyan);
        await _runtime!.JoinGeohashAsync(detected.Geohash);
    }

    private static PeerInfo? FindPeer(string target)
    {
        var mesh = _runtime!.Mesh;
        if (target.Length == 16)
            return mesh.GetPeersSnapshot().FirstOrDefault(p => p.PeerId == target.ToLowerInvariant());
        return mesh.FindPeerByNickname(target);
    }

    private static void PrintHelp()
    {
        PrintLine("commands: /peers  /msg <nick|id> <text>  /nick <name>  /links  /debug  /announce  /status  /quit", ConsoleColor.DarkCyan);
        PrintLine("any other input is broadcast to the mesh.", ConsoleColor.DarkCyan);
    }

    private static void PrintLine(string text, ConsoleColor color = ConsoleColor.Gray)
    {
        lock (ConsoleLock)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ForegroundColor = prev;
        }
    }
}
