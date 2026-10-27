using System.Diagnostics;

namespace Osoko.Core.Services;

/// <summary>
/// Remembers files Osoko itself created (saved copies, dropped files) so the file /
/// clipboard watchers never re-ingest their own output.
/// <para>
/// A dropped file cannot be deleted immediately — the receiving application may still
/// be reading it — so <see cref="ScheduleDeferredCleanup"/> keeps it alive for a
/// configurable retention window (15 minutes by default) before the sweeper removes it.
/// </para>
/// </summary>
public sealed class ExportTracker : IDisposable
{
    /// <summary>Default retention for files handed to other applications.</summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, TrackedFile> _tracked = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string> _deleteFile;
    private readonly Timer? _sweepTimer;
    private bool _disposed;

    /// <summary>
    /// Creates a tracker.
    /// </summary>
    /// <param name="clock">Injectable time source (tests). Defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    /// <param name="deleteFile">Injectable deletion callback (tests). Defaults to <see cref="DeleteFileSafe"/>.</param>
    /// <param name="enableBackgroundSweep">When true a timer expires queued files automatically.</param>
    /// <param name="sweepInterval">How often the background sweeper runs.</param>
    public ExportTracker(
        Func<DateTimeOffset>? clock = null,
        Action<string>? deleteFile = null,
        bool enableBackgroundSweep = true,
        TimeSpan? sweepInterval = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _deleteFile = deleteFile ?? DeleteFileSafe;

        if (enableBackgroundSweep)
        {
            var interval = sweepInterval ?? DefaultSweepInterval;
            _sweepTimer = new Timer(_ => PurgeExpired(), null, interval, interval);
        }
    }

    /// <summary>Raised after a deferred file has been removed from disk.</summary>
    public event EventHandler<string>? DeferredFileDeleted;

    /// <summary>Total number of tracked paths (permanent + deferred).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _tracked.Count;
            }
        }
    }

    /// <summary>Number of paths waiting for deferred deletion.</summary>
    public int PendingCleanupCount
    {
        get
        {
            lock (_gate)
            {
                return _tracked.Values.Count(entry => entry.ExpiresAt is not null);
            }
        }
    }

    /// <summary>Snapshot of every tracked path (mainly for diagnostics and tests).</summary>
    public IReadOnlyList<string> RegisteredPaths
    {
        get
        {
            lock (_gate)
            {
                return _tracked.Keys.ToArray();
            }
        }
    }

    /// <summary>True when the path is a permanent export or a pending deferred cleanup.</summary>
    public bool IsExported(string? filePath)
    {
        if (!TryNormalize(filePath, out var key))
        {
            return false;
        }

        lock (_gate)
        {
            return _tracked.ContainsKey(key);
        }
    }

    /// <summary>Alias of <see cref="IsExported"/> with a more explicit name.</summary>
    public bool IsRegistered(string? filePath) => IsExported(filePath);

    /// <summary>
    /// Marks a file as produced by Osoko. It stays registered until <see cref="Forget"/>
    /// or <see cref="Clear"/> is called.
    /// </summary>
    public void RegisterExported(string? filePath) => Track(filePath, expiresAt: null);

    /// <summary>
    /// Keeps the file on disk for <paramref name="retention"/> and deletes it afterwards.
    /// Re-scheduling an existing entry extends the window.
    /// </summary>
    public void ScheduleDeferredCleanup(string? filePath, TimeSpan? retention = null)
    {
        var window = retention ?? DefaultRetention;
        if (window < TimeSpan.Zero)
        {
            window = TimeSpan.Zero;
        }

        Track(filePath, _clock() + window);
    }

    /// <summary>Stops tracking a path (for example when the user saves over it).</summary>
    public bool CancelDeferredCleanup(string? filePath)
    {
        if (!TryNormalize(filePath, out var key))
        {
            return false;
        }

        lock (_gate)
        {
            return _tracked.Remove(key);
        }
    }

    /// <summary>Removes a path from the tracker regardless of its state.</summary>
    public void Forget(string? filePath) => CancelDeferredCleanup(filePath);

    /// <summary>
    /// Deletes every deferred file whose retention has elapsed.
    /// </summary>
    /// <returns>The number of files that were released.</returns>
    public int PurgeExpired()
    {
        var now = _clock();
        List<string> expired = [];

        lock (_gate)
        {
            foreach (var (path, entry) in _tracked)
            {
                if (entry.ExpiresAt is { } expiry && expiry <= now)
                {
                    expired.Add(path);
                }
            }

            foreach (var path in expired)
            {
                _tracked.Remove(path);
            }
        }

        foreach (var path in expired)
        {
            try
            {
                _deleteFile(path);
                DeferredFileDeleted?.Invoke(this, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"[Osoko] Deferred cleanup failed for '{path}': {ex.Message}");
            }
        }

        return expired.Count;
    }

    /// <summary>Drops all registrations without touching the files.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _tracked.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sweepTimer?.Dispose();
    }

    /// <summary>Best-effort file deletion that never throws for missing files.</summary>
    public static void DeleteFileSafe(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Osoko] Could not delete '{path}': {ex.Message}");
        }
    }

    private void Track(string? filePath, DateTimeOffset? expiresAt)
    {
        if (!TryNormalize(filePath, out var key))
        {
            return;
        }

        lock (_gate)
        {
            _tracked[key] = new TrackedFile(_clock(), expiresAt);
        }
    }

    private static bool TryNormalize(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            normalized = Path.GetFullPath(path);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private readonly record struct TrackedFile(DateTimeOffset RegisteredAt, DateTimeOffset? ExpiresAt);
}
