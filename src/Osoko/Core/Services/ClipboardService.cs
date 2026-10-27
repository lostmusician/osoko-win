using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Osoko.Core.Services;

/// <summary>
/// WPF clipboard wrapper with retry-on-Busy handling. Raises
/// <see cref="OwnWriteStarted"/> so the clipboard watcher can ignore Osoko's own writes.
/// </summary>
public sealed class ClipboardService : IClipboardService
{
    /// <summary>Raised immediately before Osoko writes to the clipboard.</summary>
    public event EventHandler? OwnWriteStarted;

    public void SetImage(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        // Decode fully into memory so the clipboard does not hold a file lock.
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad);

        var frame = decoder.Frames[0];
        if (!frame.IsFrozen)
        {
            frame.Freeze();
        }

        SetImage(frame);
    }

    public void SetImage(BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        OwnWriteStarted?.Invoke(this, EventArgs.Empty);

        // WPF's Clipboard.SetImage writes CF_BITMAP + CF_DIB so Paint, Word and Photoshop all paste.
        RunWithRetry(() => Clipboard.SetImage(image));
    }

    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        OwnWriteStarted?.Invoke(this, EventArgs.Empty);

        RunWithRetry(() => Clipboard.SetText(text));
    }

    private static void RunWithRetry(Action action, int attempts = 6)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (COMException) when (attempt < attempts - 1)
            {
                // Another process is holding the clipboard; wait a beat and retry.
                Thread.Sleep(30);
            }
        }
    }
}
