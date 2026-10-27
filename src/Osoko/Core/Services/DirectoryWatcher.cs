using System.Collections.Concurrent;
using System.Diagnostics;

namespace Osoko.Core.Services;

/// <summary>A screenshot found in the watched folder and moved into Osoko's cache.</summary>
public sealed class ScreenshotFileEventArgs : EventArgs
{
    public ScreenshotFileEventArgs(string filePath, string fingerprint, string? originalPath, int pixelWidth, int pixelHeight)
    {
        FilePath = filePath;
        Fingerprint = fingerprint;
        OriginalPath = originalPath;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
    }

    /// <summary>Path inside Osoko's scratch cache.</summary>
    public string FilePath { get; }

    public string Fingerprint { get; }

    /// <summary>Original location (e.g. <c>Pictures\Screenshots</c>), when known.</summary>
    public string? OriginalPath { get; }

    public int PixelWidth { get; }

    public int PixelHeight { get; }
}

/// <summary>
/// Watches <c>%USERPROFILE%\Pictures\Screenshots</c> (the folder Snipping Tool and
/// Win+PrintScreen write to by default). New images are moved into the scratch cache so
/// the user's Pictures folder stays clutter-free, then handed to the shelf.
/// </summary>
public sealed class DirectoryWatcher : IDisposable
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff",
    };

    private static readonly TimeSpan FileReadyTimeout = TimeSpan.FromSeconds(3);

    private readonly string _directory;
    private readonly ScratchStore _scratch;
    private readonly ExportTracker _exportTracker;
    private readonly OsokoSettings _settings;
    private readonly IImageLoader _imageLoader;
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _debounceTimer;
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    public DirectoryWatcher(
        string directory,
        ScratchStore scratch,
        ExportTracker exportTracker,
        OsokoSettings settings,
        IImageLoader? imageLoader = null)
    {
        _directory = directory;
        _scratch = scratch;
        _exportTracker = exportTracker;
        _settings = settings;
        _imageLoader = imageLoader ?? new WpfImageLoader();

        _debounceTimer = new Timer(_ => ProcessPending(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Raised on a background thread after a file has been ingested.</summary>
    public event EventHandler<ScreenshotFileEventArgs>? FileCaptured;

    /// <summary>Raised when ingestion fails (file locked, unsupported, …).</summary>
    public event EventHandler<Exception>? IngestionFailed;

    public bool IsRunning => _watcher?.EnableRaisingEvents == true;

    public string Directory => _directory;

    /// <summary>Starts watching. Safe to call multiple times.</summary>
    public void Start()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            System.IO.Directory.CreateDirectory(_directory);

            _watcher ??= CreateWatcher();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Error($"Could not watch '{_directory}'", ex);
        }
    }

    /// <summary>Stops watching without disposing the instance.</summary>
    public void Stop()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _debounceTimer.Dispose();

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnCreated;
            _watcher.Renamed -= OnRenamed;
            _watcher.Dispose();
            _watcher = null;
        }

        _pending.Clear();
    }

    private FileSystemWatcher CreateWatcher()
    {
        var watcher = new FileSystemWatcher(_directory)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };

        watcher.Created += OnCreated;
        watcher.Renamed += OnRenamed;
        watcher.Error += (_, args) => Log.Warn($"Screenshots folder watcher error: {args.GetException().Message}");
        return watcher;
    }

    private void OnCreated(object sender, FileSystemEventArgs e) => Queue(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e) => Queue(e.FullPath);

    private void Queue(string path)
    {
        if (_disposed || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!SupportedExtensions.Contains(Path.GetExtension(path)))
        {
            return;
        }

        // Ignore incomplete downloads / temp files such as "shot.png.tmp" or "~$shot.png".
        var name = Path.GetFileName(path);
        if (name.StartsWith('~') || name.StartsWith(".", StringComparison.Ordinal))
        {
            return;
        }

        _pending[path] = 0;

        // Restart the debounce window; Windows emits Created + Changed for one capture.
        _debounceTimer.Change(300, Timeout.Infinite);
    }

    private void ProcessPending()
    {
        if (_disposed || _pending.IsEmpty)
        {
            return;
        }

        if (!_settings.MonitorScreenshotsFolder)
        {
            _pending.Clear();
            return;
        }

        foreach (var path in _pending.Keys)
        {
            if (!_pending.TryRemove(path, out _))
            {
                continue;
            }

            try
            {
                Ingest(path);
            }
            catch (Exception ex)
            {
                Log.Error($"Could not ingest '{path}'", ex);
                IngestionFailed?.Invoke(this, ex);
            }
        }
    }

    private void Ingest(string sourcePath)
    {
        if (!File.Exists(sourcePath))
        {
            return;
        }

        // Never swallow files Osoko created itself (Save / Save as write here too).
        if (_exportTracker.IsExported(sourcePath))
        {
            return;
        }

        if (!WaitUntilReadable(sourcePath))
        {
            Log.Warn($"Screenshot '{sourcePath}' never became readable; skipping.");
            return;
        }

        var originalPath = sourcePath;
        var cachedPath = _settings.CleanPicturesFolder
            ? _scratch.MoveInto(sourcePath, deleteSource: true)
            : _scratch.CopyInto(sourcePath);

        if (!_settings.CleanPicturesFolder)
        {
            // The original stays in Pictures: remember it so a later restart does not re-ingest it.
            _exportTracker.RegisterExported(originalPath);
        }

        string fingerprint;
        try
        {
            fingerprint = ScratchStore.ComputeFingerprint(cachedPath);
        }
        catch (IOException)
        {
            fingerprint = string.Empty;
        }

        var size = _imageLoader.ReadPixelSize(cachedPath);
        FileCaptured?.Invoke(
            this,
            new ScreenshotFileEventArgs(
                cachedPath,
                fingerprint,
                originalPath,
                size?.Width ?? 0,
                size?.Height ?? 0));
    }

    private static bool WaitUntilReadable(string path)
    {
        var deadline = DateTime.UtcNow + FileReadyTimeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (stream.Length > 0)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Snipping Tool still holds the file — retry shortly.
            }

            Thread.Sleep(80);
        }

        return false;
    }
}
