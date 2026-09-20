using System.IO;

using System.Collections.Concurrent;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;
using Bitchat.Windows.Protocol;

namespace Bitchat.Windows.Ble;

/// <summary>
/// BLE transport for Windows via WinRT:
///  - peripheral role: GATT server (bitchat service) + connectable advertising;
///  - central role: passive/active scan for bitchat advertisers + GATT client connections.
/// Every link (server session or client connection) feeds packets into the mesh and
/// receives broadcasts from the mesh.
/// </summary>
public sealed class BleTransport : IDisposable
{
    private readonly object _linksLock = new();
    private readonly Dictionary<ulong, ClientLink> _clientLinks = new();       // we are GATT client
    private readonly Dictionary<ulong, DateTime> _connectAttempts = new();
    private readonly ConcurrentDictionary<string, byte> _pendingConnect = new();

    private GattServiceProvider? _serviceProvider;
    private GattLocalCharacteristic? _characteristic;
    private BluetoothLEAdvertisementWatcher? _watcher;
    private BluetoothLEAdvertisementPublisher? _publisher;
    private readonly byte[] _peerIdPrefix;

    public event Action<byte[], ulong, int>? PacketReceived;
    public event Action<string>? Diagnostic;
    public event Action? LinkOpened;

    public bool PeripheralStarted { get; private set; }
    public bool ScanningStarted { get; private set; }

    private readonly object _discoveryLock = new();
    private readonly Dictionary<ulong, (HashSet<Guid> Uuids, DateTime LastSeen)> _discovered = new();
    private static readonly TimeSpan DiscoverCooldown = TimeSpan.FromSeconds(10);

    public bool ScanDebug { get; set; }
    private DateTime _lastDebugLog = DateTime.MinValue;

    public BleTransport(byte[] peerIdPrefix)
    {
        _peerIdPrefix = peerIdPrefix;
    }

    public string? GetPeerIdPrefix(ulong address)
    {
        lock (_discoveryLock)
        {
            return _peerIdByAddress.TryGetValue(address, out var p) ? p : null;
        }
    }

    private readonly Dictionary<ulong, string> _peerIdByAddress = new();

    public async Task<bool> StartAsync()
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter == null)
        {
            Diagnostic?.Invoke("no Bluetooth adapter found");
            return false;
        }

        Diagnostic?.Invoke($"adapter: peripheral role = {adapter.IsPeripheralRoleSupported}, central role = {adapter.IsCentralRoleSupported}");

        var centralOk = await StartCentralAsync();
        if (adapter.IsPeripheralRoleSupported)
        {
            PeripheralStarted = await StartPeripheralAsync();
        }
        else
        {
            Diagnostic?.Invoke("peripheral role not supported by adapter; Windows will relay only as central");
        }

        return centralOk || PeripheralStarted;
    }

    // ---------------- Peripheral ----------------

    private async Task<bool> StartPeripheralAsync()
    {
        try
        {
            var result = await GattServiceProvider.CreateAsync(BitchatConstants.GattServiceUuid);
            if (result.Error != BluetoothError.Success || result.ServiceProvider == null)
            {
                Diagnostic?.Invoke($"GATT server create failed: {result.Error}");
                return false;
            }
            _serviceProvider = result.ServiceProvider;

            var charParams = new GattLocalCharacteristicParameters
            {
                CharacteristicProperties =
                    GattCharacteristicProperties.Read |
                    GattCharacteristicProperties.Write |
                    GattCharacteristicProperties.WriteWithoutResponse |
                    GattCharacteristicProperties.Notify
            };
            var charResult = await _serviceProvider.Service.CreateCharacteristicAsync(
                BitchatConstants.GattCharacteristicUuid, charParams);
            if (charResult.Error != BluetoothError.Success || charResult.Characteristic == null)
            {
                Diagnostic?.Invoke($"characteristic create failed: {charResult.Error}");
                return false;
            }

            _characteristic = charResult.Characteristic;
            _characteristic.WriteRequested += OnServerWriteRequested;
            _characteristic.ReadRequested += OnServerReadRequested;

            // Try advertising variants from most to least capable; the system may
            // reject connectable advertising for unpackaged apps.
            await StartPeripheralAdvertisingAsync();

            if (!PeripheralStarted) return false;

            // Additionally publish a service-data beacon (0x16: bitchat UUID + peerID prefix)
            // so iOS devices can match us while scanning with service filters.
            try
            {
                _publisher = new BluetoothLEAdvertisementPublisher();
                var writer = new DataWriter();
                writer.WriteBytes(AdParser.GuidToAdBytes(BitchatConstants.GattServiceUuid));
                writer.WriteBytes(_peerIdPrefix);
                _publisher.Advertisement.DataSections.Add(new BluetoothLEAdvertisementDataSection(0x16, writer.DetachBuffer()));
                _publisher.StatusChanged += (s, args) =>
                {
                    if (args.Status == BluetoothLEAdvertisementPublisherStatus.Aborted)
                        Diagnostic?.Invoke($"publisher aborted: {args.Error}");
                };
                _publisher.Start();
            }
            catch (Exception ex)
            {
                Diagnostic?.Invoke($"publisher beacon unavailable: {ex.Message}");
            }

            return true;
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"peripheral start failed: {ex.Message}");
            return false;
        }
    }

    private async Task<GattServiceProviderAdvertisementStatus> TryStartAdvertisingAsync(bool connectable, bool discoverable)
    {
        var tcs = new TaskCompletionSource<GattServiceProviderAdvertisementStatus>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(GattServiceProvider s, GattServiceProviderAdvertisementStatusChangedEventArgs args)
        {
            if (args.Status is GattServiceProviderAdvertisementStatus.Started
                or GattServiceProviderAdvertisementStatus.Aborted)
            {
                tcs.TrySetResult(args.Status);
            }
        }

        _serviceProvider!.AdvertisementStatusChanged += Handler;
        try
        {
            _serviceProvider.StartAdvertising(new GattServiceProviderAdvertisingParameters
            {
                IsConnectable = connectable,
                IsDiscoverable = discoverable
            });

            var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(6)));
            if (done == tcs.Task) return tcs.Task.Result;
            return GattServiceProviderAdvertisementStatus.Created; // timeout
        }
        finally
        {
            _serviceProvider.AdvertisementStatusChanged -= Handler;
        }
    }

    private async void OnServerWriteRequested(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
    {
        try
        {
            var request = await args.GetRequestAsync();
            if (request == null) return;

            var data = new byte[request.Value.Length];
            using var reader = DataReader.FromBuffer(request.Value);
            reader.ReadBytes(data);
            if (ScanDebug)
                Diagnostic?.Invoke($"recv {data.Length}B via server write type=0x{(data.Length > 1 ? data[1] : 0):X2}");

            PacketReceived?.Invoke(data, 0, sbyte.MinValue);

            if (request.Option == GattWriteOption.WriteWithResponse)
                request.Respond();
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"server write error: {ex.Message}");
        }
    }

    private async void OnServerReadRequested(GattLocalCharacteristic sender, GattReadRequestedEventArgs args)
    {
        try
        {
            var request = await args.GetRequestAsync();
            var writer = new DataWriter();
            writer.WriteBytes(new byte[] { 0x01 }); // "alive" byte
            request.RespondWithValue(writer.DetachBuffer());
        }
        catch { }
    }

    // ---------------- Central ----------------

    private Task<bool> StartCentralAsync()
    {
        try
        {
            // No platform filter: phones may place the 128-bit service UUID either in the
            // advertising packet or in the scan response, so we merge every AD section we
            // see per device and decide ourselves.
            _watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };
            _watcher.Received += OnAdvertisementReceived;
            _watcher.Stopped += (s, args) =>
            {
                ScanningStarted = false;
                Diagnostic?.Invoke($"scan stopped: {args.Error}");
            };
            _watcher.Start();
            ScanningStarted = true;
            Diagnostic?.Invoke("scan started (active, merging ADV + scan response)");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"scan start failed: {ex.Message}");
            return Task.FromResult(false);
        }
    }

    private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        if (args.BluetoothAddress == 0) return;

        var uuids = AdParser.ExtractServiceUuids(args.Advertisement).ToList();
        var peerPrefix = AdParser.ExtractServiceData(args.Advertisement, BitchatConstants.GattServiceUuid);

        bool hasBitchat;
        lock (_discoveryLock)
        {
            if (!_discovered.TryGetValue(args.BluetoothAddress, out var entry))
                _discovered[args.BluetoothAddress] = entry = (new HashSet<Guid>(), DateTime.MinValue);
            entry.LastSeen = DateTime.UtcNow;
            entry.Uuids.UnionWith(uuids);
            hasBitchat = entry.Uuids.Contains(BitchatConstants.GattServiceUuid);

            if (_discovered.Count > 600)
            {
                foreach (var stale in _discovered.Where(kv => DateTime.UtcNow - kv.Value.LastSeen > TimeSpan.FromMinutes(5)).Select(kv => kv.Key).ToArray())
                    _discovered.Remove(stale);
            }
        }

        if (peerPrefix != null)
        {
            lock (_discoveryLock)
                _peerIdByAddress[args.BluetoothAddress] = ByteArrayExtensions.ToHexString(peerPrefix);
        }

        if (ScanDebug && DateTime.UtcNow - _lastDebugLog > TimeSpan.FromMilliseconds(500))
        {
            _lastDebugLog = DateTime.UtcNow;
            var name = args.Advertisement.LocalName;
            Diagnostic?.Invoke($"adv {args.BluetoothAddress:X12} {args.AdvertisementType} rssi={args.RawSignalStrengthInDBm} uuids=[{string.Join(",", uuids.Select(u => u.ToString()))}]{(name.Length > 0 ? $" name={name}" : "")}");
        }

        if (!hasBitchat) return;

        lock (_linksLock)
        {
            if (_clientLinks.ContainsKey(args.BluetoothAddress)) return;
            if (_connectAttempts.TryGetValue(args.BluetoothAddress, out var lastAttempt) &&
                DateTime.UtcNow - lastAttempt < DiscoverCooldown)
                return;
            _connectAttempts[args.BluetoothAddress] = DateTime.UtcNow;
        }

        _ = ConnectToPeerAsync(args.BluetoothAddress, args.RawSignalStrengthInDBm);
    }

    private readonly Dictionary<ulong, DateTime> _deniedUntil = new();

    private async Task ConnectToPeerAsync(ulong address, int rssi)
    {
        if (!_pendingConnect.TryAdd(address.ToString(), (byte)1)) return;

        try
        {
            lock (_deniedUntil)
            {
                if (_deniedUntil.TryGetValue(address, out var until) && DateTime.UtcNow < until)
                    return;
            }

            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (device == null)
            {
                return;
            }

            var servicesResult = await device.GetGattServicesForUuidAsync(
                BitchatConstants.GattServiceUuid, BluetoothCacheMode.Uncached);
            if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
            {
                Diagnostic?.Invoke($"service discovery failed for {address:X12}: {servicesResult.Status}");
                CleanupDevice(device);
                return;
            }

            var service = servicesResult.Services[0];
            var charsResult = await service.GetCharacteristicsForUuidAsync(
                BitchatConstants.GattCharacteristicUuid, BluetoothCacheMode.Uncached);
            if (charsResult.Status != GattCommunicationStatus.Success || charsResult.Characteristics.Count == 0)
            {
                if (charsResult.Status == GattCommunicationStatus.AccessDenied)
                {
                    lock (_deniedUntil)
                        _deniedUntil[address] = DateTime.UtcNow.AddMinutes(1);
                    Diagnostic?.Invoke($"GATT access denied for {address:X12} (may require pairing) — retrying in 1 min");
                }
                else
                {
                    Diagnostic?.Invoke($"characteristic discovery failed for {address:X12}: {charsResult.Status}");
                }
                CleanupDevice(device);
                return;
            }

            var characteristic = charsResult.Characteristics[0];

            var session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId);
            var mtu = session?.MaxPduSize ?? 23;
            if (session != null) session.MaintainConnection = true;

            var link = new ClientLink(device, characteristic, session, mtu, rssi, this);
            characteristic.ValueChanged += link.OnValueChanged;
            device.ConnectionStatusChanged += link.OnConnectionStatusChanged;

            var status = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if (status != GattCommunicationStatus.Success)
            {
                Diagnostic?.Invoke($"subscribe failed for {address:X12}: {status}");
                CleanupDevice(device);
                return;
            }

            lock (_linksLock)
            {
                _clientLinks[address] = link;
            }
            var peerHint = GetPeerIdPrefix(address);
            Diagnostic?.Invoke($"connected to {address:X12} (mtu {mtu}){(peerHint != null ? $" peer={peerHint}…" : "")}");
            LinkOpened?.Invoke();
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"connect to {address:X12} failed: {ex.Message}");
        }
        finally
        {
            _pendingConnect.TryRemove(address.ToString(), out _);
        }
    }

    private static void CleanupDevice(BluetoothLEDevice device)
    {
        try { device.Dispose(); } catch { }
    }

    // ---------------- Outgoing ----------------

    public void Broadcast(byte[] data)
    {
        List<ClientLink> links;
        lock (_linksLock)
        {
            links = _clientLinks.Values.ToList();
        }
        foreach (var link in links)
        {
            _ = link.SendAsync(data);
        }

        // Also notify subscribed GATT server clients (phones connected to us).
        _ = NotifySubscribedServers(data);
    }

    private async Task NotifySubscribedServers(byte[] data)
    {
        var characteristic = _characteristic;
        if (characteristic == null) return;
        try
        {
            if (characteristic.SubscribedClients.Count == 0) return;
            var writer = new DataWriter();
            writer.WriteBytes(data);
            await characteristic.NotifyValueAsync(writer.DetachBuffer());
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"notify failed: {ex.Message}");
        }
    }

    /// <summary>Re-attempt peripheral advertising (e.g. after package identity was granted).</summary>
    public async Task<bool> RetryPeripheralAsync()
    {
        if (PeripheralStarted) return true;
        if (_serviceProvider == null) return false;
        return await StartPeripheralAdvertisingAsync();
    }

    private async Task<bool> StartPeripheralAdvertisingAsync()
    {
        // The radio sometimes aborts the first attempt right after service creation — retry rounds.
        for (var round = 0; round < 10 && !PeripheralStarted; round++)
        {
            if (round > 0) await Task.Delay(TimeSpan.FromSeconds(3));
            foreach (var (connectable, discoverable, label) in new[]
                     {
                         (true, true, "connectable+discoverable"),
                         (true, false, "connectable"),
                         (false, true, "non-connectable+discoverable")
                     })
            {
                var status = await TryStartAdvertisingAsync(connectable, discoverable);
                if (status == GattServiceProviderAdvertisementStatus.Started)
                {
                    PeripheralStarted = true;
                    Diagnostic?.Invoke($"GATT advertising started ({label})");
                    return true;
                }
                Diagnostic?.Invoke($"GATT advertising {label}: {status} — trying next variant");
                _serviceProvider!.StopAdvertising();
            }
        }

        Diagnostic?.Invoke(
            "peripheral advertising unavailable — central-only mode " +
            "(the client scans and connects out to nearby peers)");
        return false;
    }

    // ---------------- Diagnostics ----------------

    public string GetStatus()
    {
        lock (_linksLock)
        {
            return $"peripheral={(PeripheralStarted ? "advertising" : "off")} scan={(ScanningStarted ? "on" : "off")} links={_clientLinks.Count}";
        }
    }

    public void Dispose()
    {
        try
        {
            _watcher?.Stop();
            _publisher?.Stop();
            _serviceProvider?.StopAdvertising();
            _characteristic = null;

            List<ClientLink> links;
            lock (_linksLock)
            {
                links = _clientLinks.Values.ToList();
                _clientLinks.Clear();
            }
            foreach (var link in links) link.Dispose();
        }
        catch { }
    }

    private sealed class ClientLink : IDisposable
    {
        private readonly BluetoothLEDevice _device;
        private readonly GattCharacteristic _characteristic;
        private readonly GattSession? _session;
        private readonly int _mtu;
        private readonly BleTransport _transport;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private bool _disposed;

        public ClientLink(BluetoothLEDevice device, GattCharacteristic characteristic,
            GattSession? session, int mtu, int rssi, BleTransport transport)
        {
            _device = device;
            _characteristic = characteristic;
            _session = session;
            _mtu = mtu;
            _transport = transport;
        }

        public void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            var data = new byte[args.CharacteristicValue.Length];
            using var reader = DataReader.FromBuffer(args.CharacteristicValue);
            reader.ReadBytes(data);
            if (_transport.ScanDebug)
                _transport.Diagnostic?.Invoke($"recv {data.Length}B from {_device.BluetoothAddress:X12} type=0x{(data.Length > 1 ? data[1] : 0):X2}");
            _transport.PacketReceived?.Invoke(data, _device.BluetoothAddress, 0);
        }

        public void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
        {
            if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
            {
                _transport.Diagnostic?.Invoke($"peer {sender.BluetoothAddress:X12} disconnected");
                lock (_transport._linksLock)
                {
                    _transport._clientLinks.Remove(sender.BluetoothAddress);
                }
                Dispose();
            }
        }

        public async Task SendAsync(byte[] data)
        {
            if (_disposed) return;
            await _sendLock.WaitAsync();
            try
            {
                if (_disposed) return;
                var writer = new DataWriter();
                writer.WriteBytes(data);

                var maxWithoutResponse = _mtu - 3;
                var option = data.Length <= maxWithoutResponse
                    ? GattWriteOption.WriteWithoutResponse
                    : GattWriteOption.WriteWithResponse;

                var status = await _characteristic.WriteValueAsync(writer.DetachBuffer(), option);
                if (_transport.ScanDebug)
                    _transport.Diagnostic?.Invoke($"→ {data.Length}B to {_device.BluetoothAddress:X12} ({option}): {status}");
                if (status != GattCommunicationStatus.Success && status != GattCommunicationStatus.Unreachable)
                {
                    _transport.Diagnostic?.Invoke($"write to {_device.BluetoothAddress:X12}: {status}");
                }
            }
            catch (Exception ex)
            {
                _transport.Diagnostic?.Invoke($"write error: {ex.Message}");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _characteristic.ValueChanged -= OnValueChanged;
                _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
                _session?.Dispose();
                _device.Dispose();
            }
            catch { }
        }
    }
}

