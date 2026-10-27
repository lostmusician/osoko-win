using System.Diagnostics;
using System.Media;
using System.Text;

namespace Osoko.Core.Services;

/// <summary>Small, unobtrusive sound cues.</summary>
public enum AudioCue
{
    /// <summary>A new screenshot landed on the shelf.</summary>
    Capture,

    /// <summary>Something was copied (image or OCR text).</summary>
    Copy,

    /// <summary>An item was removed from the shelf.</summary>
    Delete,
}

/// <summary>
/// Generates soft, Windows-y tones at runtime (no WAV assets to ship) and plays them
/// through <see cref="SoundPlayer"/>. Falls back to the system asterisk sound when
/// audio playback is unavailable. Every failure is swallowed: audio is a nicety.
/// </summary>
public sealed class AudioService : IDisposable
{
    private const int SampleRate = 44_100;

    private readonly Func<bool> _isEnabled;
    private readonly Dictionary<AudioCue, SoundPlayer> _players = [];
    private readonly object _gate = new();
    private bool _disposed;

    public AudioService(Func<bool>? isEnabled = null)
    {
        _isEnabled = isEnabled ?? (() => true);
    }

    /// <summary>Plays the cue when sound is enabled in settings.</summary>
    public void Play(AudioCue cue)
    {
        if (_disposed || !_isEnabled())
        {
            return;
        }

        try
        {
            var player = GetPlayer(cue);
            if (player is null)
            {
                SystemSounds.Asterisk.Play();
                return;
            }

            player.Play();
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Osoko] Audio cue '{cue}' failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_gate)
        {
            foreach (var player in _players.Values)
            {
                player.Dispose();
            }

            _players.Clear();
        }
    }

    private SoundPlayer? GetPlayer(AudioCue cue)
    {
        lock (_gate)
        {
            if (_players.TryGetValue(cue, out var existing))
            {
                return existing;
            }

            var stream = BuildToneStream(cue);
            if (stream is null)
            {
                return null;
            }

            var player = new SoundPlayer(stream);
            player.Load();
            _players[cue] = player;
            return player;
        }
    }

    private static MemoryStream? BuildToneStream(AudioCue cue)
    {
        var notes = cue switch
        {
            // Rising perfect fifth — "captured".
            AudioCue.Capture => new[] { (880.0, 55, 0.16), (1318.5, 70, 0.13) },
            // Single soft blip — "copied".
            AudioCue.Copy => new[] { (1046.5, 45, 0.10) },
            // Falling minor third — "removed".
            AudioCue.Delete => new[] { (392.0, 60, 0.12), (311.1, 75, 0.10) },
            _ => Array.Empty<(double, int, double)>(),
        };

        if (notes.Length == 0)
        {
            return null;
        }

        var samples = new List<short>(notes.Sum(note => (int)(SampleRate * note.Item2 / 1000.0)));

        foreach (var (frequency, durationMs, amplitude) in notes)
        {
            var sampleCount = (int)(SampleRate * durationMs / 1000.0);
            var fadeSamples = Math.Max(1, sampleCount / 8);

            for (var index = 0; index < sampleCount; index++)
            {
                // Linear attack / release envelope avoids clicks.
                var envelope = index < fadeSamples
                    ? index / (double)fadeSamples
                    : index > sampleCount - fadeSamples
                        ? (sampleCount - index) / (double)fadeSamples
                        : 1.0;

                var value = Math.Sin(2 * Math.PI * frequency * index / SampleRate) * amplitude * envelope;
                samples.Add((short)(value * short.MaxValue));
            }
        }

        return new MemoryStream(BuildWaveFile(samples));
    }

    private static byte[] BuildWaveFile(IReadOnlyList<short> samples)
    {
        const short channels = 1;
        const short bitsPerSample = 16;
        var dataLength = samples.Count * sizeof(short);

        using var stream = new MemoryStream(44 + dataLength);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);                       // Subchunk1Size (PCM)
        writer.Write((short)1);                 // AudioFormat (PCM)
        writer.Write(channels);
        writer.Write(SampleRate);
        writer.Write(SampleRate * channels * bitsPerSample / 8); // ByteRate
        writer.Write((short)(channels * bitsPerSample / 8));     // BlockAlign
        writer.Write(bitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(dataLength);

        foreach (var sample in samples)
        {
            writer.Write(sample);
        }

        writer.Flush();
        return stream.ToArray();
    }
}
