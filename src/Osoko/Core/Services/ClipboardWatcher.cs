using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Osoko.Interop;

namespace Osoko.Core.Services;

/// <summary>A clipboard image that was successfully captured into the scratch cache.</summary>
public sealed class ClipboardImageEventArgs : EventArgs
{
    public ClipboardImageEventArgs(
        string filePath,
        string fingerprint,
        int pixelWidth,
        int pixelHeight,
        ScreenshotSource source,
        string? ownerProcessName)
    {
        FilePath = filePath;
        Fingerprint = fingerprint;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        Source = source;
        OwnerProcessName = ownerProcessName;
    }

    public string FilePath { get; }

    public string Fingerprint { get; }

    public int PixelWidth { get; }

    public int PixelHeight { get; }

    public ScreenshotSource Source { get; }

    public string? OwnerProcessName { get; }
}

/// <summary>
/// Hidden message-only window that listens for <c>WM_CLIPBOARDUPDATE</c>
/// (<c>AddClipboardFormatListener</c>). When Windows Snipping Tool copies a capture we
/// pull the bitmap off the clipboard, write it to the scratch cache and hand it to the
/// shelf. Images copied by Osoko itself are ignored so the app never re-ingests its own
/// output.
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    private static readonly string[] SnippingToolProcesses =
    [
        "ScreenClippingHost",
        "SnippingTool",
        "snippingtool",
    ];

    private static readonly string[] ShellProcesses =
    [
        "explorer",
        "ShellExperienceHost",
        "ApplicationFrameHost",
        "LockApp",
    ];

    private readonly HwndSource _messageSource;
    private readonly IntPtr _hwnd;
    private readonly ScratchStore _scratch;
    private readonly OsokoSettings _settings;
    private readonly DispatcherTimer _debounceTimer;
    private readonly int _selfProcessId;
    private bool _suppressNextUpdate;
    private bool _processing;
    private bool _disposed;
    private string? _lastFingerprint;
    private DateTimeOffset _lastCaptureAt = DateTimeOffset.MinValue;

    public ClipboardWatcher(ScratchStore scratch, OsokoSettings settings)
    {
        _scratch = scratch ?? throw new ArgumentNullException(nameof(scratch));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _selfProcessId = Environment.ProcessId;

        var parameters = new HwndSourceParameters("Osoko.ClipboardWatcher")
        {
            ParentWindow = NativeMethods.HWND_MESSAGE,
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
        };

        _messageSource = new HwndSource(parameters);
        _messageSource.AddHook(WndProc);
        _hwnd = _messageSource.Handle;

        if (!NativeMethods.AddClipboardFormatListener(_hwnd))
        {
            Log.Warn("AddClipboardFormatListener failed; clipboard interception is disabled.");
        }

        _debounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(180),
        };
        _debounceTimer.Tick += OnDebounceElapsed;
    }

    /// <summary>Raised on the UI thread after a clipboard image has been persisted to scratch.</summary>
    public event EventHandler<ClipboardImageEventArgs>? ImageCaptured;

    /// <summary>When false the watcher stays installed but ignores updates.</summary>
    public bool Enabled { get; set; } = true;

    public bool IsRunning => !_disposed;

    /// <summary>Suppresses the very next clipboard update (used after Osoko writes to the clipboard).</summary>
    public void SuppressNextUpdate() => _suppressNextUpdate = true;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _debounceTimer.Stop();
        _debounceTimer.Tick -= OnDebounceElapsed;

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.RemoveClipboardFormatListener(_hwnd);
        }

        _messageSource.RemoveHook(WndProc);
        _messageSource.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_CLIPBOARDUPDATE)
        {
            // Coalesce bursts: Snipping Tool can emit several updates for one capture.
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        return IntPtr.Zero;
    }

    private async void OnDebounceElapsed(object? sender, EventArgs e)
    {
        _debounceTimer.Stop();

        if (_processing)
        {
            // Another capture is being written; re-arm so the newest state is read.
            _debounceTimer.Start();
            return;
        }

        _processing = true;
        try
        {
            await ProcessClipboardAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error("Clipboard processing failed", ex);
        }
        finally
        {
            _processing = false;
        }
    }

    private async Task ProcessClipboardAsync()
    {
        if (!Enabled || _disposed)
        {
            return;
        }

        if (_suppressNextUpdate)
        {
            _suppressNextUpdate = false;
            return;
        }

        var containsImage = await RetryClipboardAsync(Clipboard.ContainsImage).ConfigureAwait(true);
        if (!containsImage)
        {
            return;
        }

        var owner = ClipboardOwnerInfo.Detect(_selfProcessId);
        if (owner.IsSelf)
        {
            return;
        }

        var accept = owner.IsSnippingTool || owner.IsShell || owner.IsUnknown || _settings.CaptureClipboardFromAnyApp;
        if (!accept)
        {
            return;
        }

        var image = await RetryClipboardAsync<BitmapSource?>(Clipboard.GetImage).ConfigureAwait(true);
        if (image is null || image.PixelWidth < 16 || image.PixelHeight < 16)
        {
            return;
        }

        if (!image.IsFrozen)
        {
            image.Freeze();
        }

        string fingerprint;
        try
        {
            fingerprint = ScratchStore.ComputeFingerprint(image);
        }
        catch (Exception ex)
        {
            Log.Warn($"Fingerprinting clipboard image failed: {ex.Message}");
            return;
        }

        var now = DateTimeOffset.Now;
        if (string.Equals(fingerprint, _lastFingerprint, StringComparison.OrdinalIgnoreCase) &&
            now - _lastCaptureAt < TimeSpan.FromSeconds(3))
        {
            return;
        }

        string path;
        try
        {
            path = _scratch.SaveBitmap(image, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Error("Could not persist clipboard image", ex);
            return;
        }

        _lastFingerprint = fingerprint;
        _lastCaptureAt = now;

        var source = owner.IsSnippingTool ? ScreenshotSource.SnippingTool : ScreenshotSource.Clipboard;
        ImageCaptured?.Invoke(
            this,
            new ClipboardImageEventArgs(path, fingerprint, image.PixelWidth, image.PixelHeight, source, owner.ProcessName));
    }

    /// <summary>
    /// Clipboard APIs fail with <see cref="COMException"/> while another process holds the
    /// clipboard. Retrying on the dispatcher keeps the UI responsive.
    /// </summary>
    private static async Task<T> RetryClipboardAsync<T>(Func<T> action, int attempts = 8)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (COMException) when (attempt < attempts - 1)
            {
                await Task.Delay(25).ConfigureAwait(true);
            }
        }
    }

    private readonly record struct ClipboardOwnerInfo(
        string? ProcessName,
        bool IsSnippingTool,
        bool IsShell,
        bool IsSelf,
        bool IsUnknown)
    {
        internal static ClipboardOwnerInfo Detect(int selfProcessId)
        {
            var hwnd = NativeMethods.GetClipboardOwner();
            if (hwnd == IntPtr.Zero)
            {
                hwnd = NativeMethods.GetForegroundWindow();
            }

            if (hwnd == IntPtr.Zero)
            {
                return new ClipboardOwnerInfo(null, false, false, false, true);
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == 0)
            {
                return new ClipboardOwnerInfo(null, false, false, false, true);
            }

            string? processName = null;
            try
            {
                using var process = Process.GetProcessById((int)processId);
                processName = process.ProcessName;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // Process already exited — treat as unknown owner.
            }

            var isSelf = processId == (uint)selfProcessId;
            var isSnippingTool = processName is not null &&
                SnippingToolProcesses.Contains(processName, StringComparer.OrdinalIgnoreCase);
            var isShell = processName is not null &&
                ShellProcesses.Contains(processName, StringComparer.OrdinalIgnoreCase);

            return new ClipboardOwnerInfo(processName, isSnippingTool, isShell, isSelf, processName is null);
        }
    }
}
