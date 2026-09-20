using System.IO;

using Bitchat.Windows.Ble;
using Bitchat.Windows.Mesh;
using Bitchat.Windows.Noise;
using Bitchat.Windows.Nostr;
using Bitchat.Windows.Protocol;

namespace Bitchat.Windows;

/// <summary>
/// One running bitchat instance: identity + mesh engine + BLE transport.
/// Shared by the console TUI and the WPF GUI.
/// </summary>
public sealed class BitchatRuntime : IDisposable
{
    public NoiseService Noise { get; }
    public MeshEngine Mesh { get; }
    public BleTransport Ble { get; }
    public Voice.AudioPlayer Player { get; } = new();
    public NostrService? Nostr { get; private set; }

    public bool DebugEnabled { get; set; }

    /// <summary>Messages from Nostr geohash channels: (geohash, nickname, content).</summary>
    public event Action<string, string?, string>? NostrMessageReceived;
    public event Action<string>? NostrJoined;
    public event Action<int, int>? NostrRelayStatus;

    public (int Connected, int Total) NostrRelayCount => Nostr?.RelayStatus ?? (0, 0);

    private readonly string _defaultNick;
    private bool _started;

    public static string DefaultIdentityPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "bitchat", "identity.bin");

    public BitchatRuntime(string nickname, string? identityPath = null, bool debug = false)
    {
        DebugEnabled = debug;
        _defaultNick = TruncateNickname(nickname);
        Noise = new NoiseService(identityPath ?? DefaultIdentityPath);
        Mesh = new MeshEngine(Noise, _defaultNick);
        Ble = new BleTransport(MeshEngine.HexToBytes(Noise.MyPeerId));

        Mesh.PacketOut += data => Ble.Broadcast(data);
        Ble.PacketReceived += OnPacket;
        Ble.LinkOpened += () =>
        {
            try { Mesh.SendAnnouncement(); } catch { }
            Mesh.RequestSyncSoon();
        };
    }

    /// <summary>Default nickname: "anon" + 4 decimal digits of the Bluetooth MAC address,
    /// so it is stable per device (random fallback if no adapter is available).</summary>
    public static async Task<string> ResolveDefaultNickAsync()
    {
        try
        {
            var adapter = await global::Windows.Devices.Bluetooth.BluetoothAdapter.GetDefaultAsync();
            if (adapter != null && adapter.BluetoothAddress != 0)
                return "anon" + (adapter.BluetoothAddress % 10000).ToString("D4");
        }
        catch { }

        var digits = System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 10000);
        return "anon" + digits.ToString("D4");
    }

    /// <summary>Nickname resolution: explicit arg (pinned) → saved config → device default.</summary>
    public static async Task<string> ResolveNicknameAsync(string? explicitNick)
    {
        if (!string.IsNullOrWhiteSpace(explicitNick))
        {
            NickStore.SaveNickname(explicitNick);
            return TruncateNickname(explicitNick);
        }

        var saved = NickStore.LoadNickname();
        if (saved != null) return TruncateNickname(saved);

        return await ResolveDefaultNickAsync();
    }

    /// <summary>Change and pin the nickname.</summary>
    public void SetNickname(string nickname)
    {
        var cleaned = TruncateNickname(nickname);
        if (cleaned.Length == 0) return;
        NickStore.SaveNickname(cleaned);
        Mesh.SetNickname(cleaned);
    }

    /// <summary>Reset to the device default (anon + MAC digits) and unpin.</summary>
    public void ResetNickname()
    {
        NickStore.ClearNickname();
        Mesh.SetNickname(_defaultNick);
    }

    public string DefaultNick => _defaultNick;
    public string Nickname => Mesh.Nickname;

    public async Task<bool> StartAsync()
    {
        if (_started) return true;

        // Peripheral advertising sometimes aborts while the radio warms up; grant
        // package identity best-effort first (helps on some systems).
        if (!SparsePackageIdentity.HasIdentity())
        {
            await SparsePackageIdentity.TryRegisterAsync();
        }

var ok = await Ble.StartAsync();
        if (ok && !Ble.PeripheralStarted)
        {
            // The radio can take tens of seconds to warm up - keep retrying in the background.
            _ = Task.Run(async () =>
            {
                while (_started && !Ble.PeripheralStarted)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10));
                    if (Ble.PeripheralStarted) break;
                    if (await Ble.RetryPeripheralAsync())
                        SystemLog?.Invoke("[ble] peripheral advertising is up");
                }
            });
        }
        _started = ok;

        // Auto-restore joined geohash channels (автопродление)
        if (ok)
        {
            foreach (var geohash in NickStore.LoadGeohashes())
            {
                try { await JoinGeohashAsync(geohash); } catch { }
            }
        }
        return ok;
    }

    /// <summary>Join a geohash channel over Nostr (lazy relay transport start).</summary>
    public async Task JoinGeohashAsync(string geohash)
    {
        if (Nostr == null)
        {
            Nostr = new NostrService(() => Mesh.Nickname, msg => SystemLog?.Invoke($"[nostr] {msg}"));
            Nostr.MessageReceived += (geohash, nickname, content, pubkey) =>
                NostrMessageReceived?.Invoke(geohash, nickname, content);
            Nostr.RelayStatusChanged += (connected, total) => NostrRelayStatus?.Invoke(connected, total);
            await Nostr.StartAsync();
        }
        geohash = geohash.Trim().ToLowerInvariant();
        Nostr.JoinGeohash(geohash);

        var saved = NickStore.LoadGeohashes().ToList();
        if (!saved.Contains(geohash))
        {
            saved.Add(geohash);
            NickStore.SaveGeohashes(saved);
        }
    }

    public void LeaveGeohash(string geohash)
    {
        geohash = geohash.Trim().ToLowerInvariant();
        Nostr?.LeaveGeohash(geohash);
        var saved = NickStore.LoadGeohashes().Where(g => g != geohash).ToList();
        NickStore.SaveGeohashes(saved);
    }

    public void SendGeohash(string geohash, string text) => Nostr?.Send(geohash, text);

    public event Action<string>? SystemLog;

    public void Stop()
    {
        try { Mesh.SendLeave(); } catch { }
        Dispose();
    }

    private void OnPacket(byte[] data, ulong fromAddress, int rssi)
    {
        var packet = BinaryProtocol.Decode(data);
        if (packet == null) return;
        Mesh.OnPacketReceived(packet, ByteArrayExtensions.ToHexString(packet.SenderId), rssi);
    }

    public static string TruncateNickname(string value)
    {
        var cleaned = new string(value.Where(c => !char.IsControl(c)).ToArray());
        return cleaned.Length <= BitchatConstants.MaxNicknameLength
            ? cleaned
            : cleaned[..BitchatConstants.MaxNicknameLength];
    }

    public void Dispose()
    {
        try { Ble.Dispose(); } catch { }
    }
}
