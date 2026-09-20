using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Bitchat.Windows.Voice;

/// <summary>
/// Live push-to-talk voice: records mic (16 kHz mono PCM), encodes AAC-LC via
/// Media Foundation, packetizes with VoiceBurstPacketizer, and plays received bursts.
/// Wire-compatible with the mobile clients (type 0x29 / NoisePayload 0x08).
/// </summary>
public sealed class VoiceService : IDisposable
{
    private WasapiCapture? _capture;
    private BufferedWaveProvider? _buffered;
    private readonly object _recordLock = new();
    private DateTime _recordStart;
    private long _capturedBytes;
    private long durationMs;
    private WaveFormat _captureFormat = new WaveFormat(48000, 32, 2);

    private readonly Dictionary<string, (List<(int Seq, byte[] Au)> Units, DateTime Started)> _incoming = new();
    private readonly object _incomingLock = new();
    private IWavePlayer? _playback;

    /// <summary>Builds the wire packet and sends it (provided by the mesh engine).</summary>
    private readonly Action<byte[]> _sendBurstBroadcast;
    private readonly Func<string, byte[], bool> _sendBurstPrivate;
    private readonly Action<string> _log;
    private readonly Func<bool> _debug;

    public bool IsRecording { get; private set; }
    public event Action<bool>? RecordingStateChanged;
    public event Action<string, string, int, bool>? VoiceReceived;
    public Func<string?>? NicknameProvider { get; set; }

    /// <summary>Fired when a voice recording is ready: (aacBytes, durationMs, isPrivate, peerId).</summary>
    public event Action<byte[], long, bool, string?>? VoiceFileReady;

    public VoiceService(Action<byte[]> sendBurstBroadcast, Func<string, byte[], bool> sendBurstPrivate,
        Action<string> log, Func<bool> debug)
    {
        _sendBurstBroadcast = sendBurstBroadcast;
        _sendBurstPrivate = sendBurstPrivate;
        _log = log;
        _debug = debug;
    }

    // ---- Recording ----

    public void StartRecording()
    {
        lock (_recordLock)
        {
            if (IsRecording) return;
            try
            {
                _capturedBytes = 0;
                _capture = new WasapiCapture();
                _captureFormat = _capture.WaveFormat;
                _buffered = new BufferedWaveProvider(_captureFormat)
                {
                    DiscardOnBufferOverflow = true,
                    BufferDuration = TimeSpan.FromMinutes(10),
                    ReadFully = false // must end when the buffer drains, or the resampler loops forever
                };
                _capture.DataAvailable += (_, e) =>
                {
                    try { _buffered?.AddSamples(e.Buffer, 0, e.BytesRecorded); _capturedBytes += e.BytesRecorded; } catch { }
                };
                _capture.StartRecording();
                _recordStart = DateTime.UtcNow;
                IsRecording = true;
                RecordingStateChanged?.Invoke(true);
            }
            catch (Exception ex)
            {
                _log($"микрофон недоступен: {ex.Message}");
                _capture = null;
                IsRecording = false;
            }
        }
    }

    /// <summary>Stops recording and transmits the burst on a background thread.</summary>
    public long StopRecordingAndSend(bool isPrivate, string? privatePeerId)
    {
        lock (_recordLock)
        {
            if (!IsRecording) return 0;
            IsRecording = false;
            RecordingStateChanged?.Invoke(false);
            durationMs = (long)(DateTime.UtcNow - _recordStart).TotalMilliseconds;

            try { _capture?.StopRecording(); } catch { }
            System.Threading.Thread.Sleep(120);

            // Grab PCM on this thread (fast), encode+send on background.
            var pcmTask = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var resampler = new MediaFoundationResampler(_buffered!, new WaveFormat(16000, 16, 1))
                    {
                        ResamplerQuality = 60
                    };
                    var pcm = new List<byte>();
                    var buf = new byte[64000];
                    int n;
                    while ((n = resampler.Read(buf, 0, buf.Length)) > 0)
                        pcm.AddRange(buf.AsSpan(0, n).ToArray());
                    resampler.Dispose();
                    return pcm.ToArray();
                }
                catch (Exception ex)
                {
                    _log($"PCM conversion failed: {ex.Message}");
                    return Array.Empty<byte>();
                }
            });
            _ = pcmTask.ContinueWith(t =>
            {
                try
                {
                    var pcm = t.Result;
                    if (pcm.Length < 800) { _log($"PCM too short ({pcm.Length} Б)"); return; }
                    var aac = EncodeAac(pcm);
                    if (aac.Length == 0) { _log("AAC-поток пуст"); return; }
                    TransmitBurst(aac, durationMs, isPrivate, privatePeerId);
                }
                catch (Exception ex)
                {
                    _log($"voice send failed: {ex.GetType().Name}: {ex.Message}");
                }
            });
            return durationMs;
        }
    }

    private void TransmitBurst(byte[] aac, long durationMs, bool isPrivate, string? privatePeerId)
    {
        _log($"voice: AAC {aac.Length}Б, {durationMs}мс, private={isPrivate}");
        VoiceFileReady?.Invoke(aac, durationMs, isPrivate, privatePeerId);
    }

    private static byte[] EncodeAac(byte[] pcm16kMono)
    {
        // WaveFileWriter/Reader own their streams, so feed them throwaway copies.
        byte[] wavBytes;
        using (var wav = new MemoryStream())
        {
            using (var writer = new WaveFileWriter(wav, new WaveFormat(16000, 16, 1)))
            {
                writer.Write(pcm16kMono, 0, pcm16kMono.Length);
            }
            wavBytes = wav.ToArray();
        }

        using var output = new MemoryStream();
        using (var wavStream = new MemoryStream(wavBytes))
        using (var reader = new WaveFileReader(wavStream))
        {
            MediaFoundationEncoder.EncodeToAac(reader, output, 24000);
        }
        return output.ToArray();
    }

    /// <summary>ADTS stream → raw AAC access units (headers stripped).</summary>
    internal static List<byte[]> ParseAdts(byte[] data)
    {
        var units = new List<byte[]>();
        var i = 0;
        while (i + 7 <= data.Length)
        {
            if (data[i] != 0xFF || (data[i + 1] & 0xF0) != 0xF0)
            {
                i++;
                continue;
            }
            var frameLength = ((data[i + 3] & 0x03) << 11) | ((data[i + 4] & 0xFF) << 3) | ((data[i + 5] >> 5) & 0x07);
            if (frameLength < 7 || i + frameLength > data.Length) break;
            units.Add(data[(i + 7)..(i + frameLength)]);
            i += frameLength;
        }
        return units;
    }

    // ---- Receiving ----

    public void HandleFrame(string peerId, string nickname, byte[] burstBytes, long timestampMs, bool isPrivate)
    {
        var burst = VoiceBurstPacket.Decode(burstBytes);
        if (burst == null) return;
        var key = VoiceBurstPacket.BurstIdHex(burst.BurstId);

        lock (_incomingLock)
        {
            if (burst.Value is VoiceBurstPacket.Kind.Start)
            {
                _incoming[key] = (new List<(int, byte[])>(), DateTime.UtcNow);
            }
            else if (burst.Value is VoiceBurstPacket.Kind.Frames fr)
            {
                if (!_incoming.ContainsKey(key)) _incoming[key] = (new List<(int, byte[])>(), DateTime.UtcNow);
                var (units, _) = _incoming[key];
                foreach (var f in fr.FrameList)
                    if (!units.Any(u => u.Seq == burst.Sequence))
                        units.Add((burst.Sequence, f));
            }
            else if (burst.Value is VoiceBurstPacket.Kind.End end)
            {
                if (!_incoming.TryGetValue(key, out var session))
                {
                    _incoming[key] = session = (new List<(int, byte[])>(), DateTime.UtcNow);
                }
                var (units, _) = session;
                var sorted = units.OrderBy(u => u.Seq).Select(u => u.Au).ToList();
                _incoming.Remove(key);
                if (sorted.Count == 0) return;

                var adts = new MemoryStream();
                foreach (var au in sorted)
                {
                    try { var framed = AdtsFramer.Frame(au); adts.Write(framed); } catch { }
                }
                if (adts.Length == 0) return;

                var filePath = Path.Combine(Mesh.MeshEngine.MediaDirectory, $"voice-{key}.aac");
                Directory.CreateDirectory(Mesh.MeshEngine.MediaDirectory);
                File.WriteAllBytes(filePath, adts.ToArray());

                var duration = end.DurationMs > 0 ? end.DurationMs : sorted.Count * 20;
                Task.Run(() => Play(filePath));
                VoiceReceived?.Invoke(peerId, nickname, (int)Math.Min(duration, int.MaxValue), isPrivate);
            }
        }
    }

    private void Play(string path)
    {
        try
        {
            _playback?.Stop();
            _playback?.Dispose();
            _playback = new WaveOutEvent();
            var reader = new MediaFoundationReader(path);
            _playback.Init(reader);
            _playback.Play();
        }
        catch (Exception ex)
        {
            _log($"воспроизведение не удалось: {ex.Message}");
        }
    }

    private static void Safe(Action a) { try { a(); } catch { } }

    public void Dispose()
    {
        lock (_recordLock)
        {
            try { _capture?.StopRecording(); } catch { }
            try { _capture?.Dispose(); } catch { }
        }
        try { _playback?.Stop(); _playback?.Dispose(); } catch { }
    }
}
