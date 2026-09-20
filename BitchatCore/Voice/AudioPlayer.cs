using System.IO;
using NAudio.Wave;

namespace Bitchat.Windows.Voice;

/// <summary>Simple audio playback for voice messages (m4a/aac/mp3/wav/ogg).</summary>
public sealed class AudioPlayer : IDisposable
{
    private WaveOutEvent? _output;
    private MediaFoundationReader? _reader;

    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public string? CurrentFile { get; private set; }

    public TimeSpan Position => _reader?.CurrentTime ?? TimeSpan.Zero;
    public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;

    public event Action? PlaybackStopped;

    public static bool IsAudioFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".m4a" or ".aac" or ".mp3" or ".wav" or ".ogg" or ".mp4";
    }

    public void Toggle(string filePath)
    {
        if (CurrentFile == filePath && IsPlaying)
        {
            Stop();
            return;
        }
        Play(filePath);
    }

    public void Play(string filePath)
    {
        Stop();
        try
        {
            _reader = new MediaFoundationReader(filePath);
            _output = new WaveOutEvent();
            _output.PlaybackStopped += (_, _) =>
            {
                CurrentFile = null;
                PlaybackStopped?.Invoke();
            };
            _output.Init(_reader);
            _output.Play();
            CurrentFile = filePath;
        }
        catch
        {
            Stop();
        }
    }

    public void Stop()
    {
        try { _output?.Stop(); } catch { }
        try { _output?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        _output = null;
        _reader = null;
        CurrentFile = null;
        PlaybackStopped?.Invoke();
    }

    public void Dispose() => Stop();
}
