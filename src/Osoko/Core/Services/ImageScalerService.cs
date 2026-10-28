using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Osoko.Core.Services;

/// <summary>
/// Downscales screenshots for the "Copy 1x" card action. Windows captures are
/// commonly 2x (Retina-class) bitmaps, so halving the pixel dimensions produces an
/// image whose pixels map 1:1 to logical points.
/// <para>
/// Scaling is delegated to WIC through <see cref="TransformedBitmap"/>, which uses
/// high-quality Fant interpolation and preserves the source pixel format (alpha
/// included). Results are frozen so they can be handed to the clipboard or another
/// thread without marshalling.
/// </para>
/// </summary>
public sealed class ImageScalerService
{
    /// <summary>The fraction applied by <see cref="DownscaleTo1x(BitmapSource)"/>.</summary>
    public const double DownscaleFactor = 0.5;

    /// <summary>Default instance for callers that do not construct their own.</summary>
    public static ImageScalerService Shared { get; } = new();

    /// <summary>
    /// Downscales the image at <paramref name="filePath"/> to half its pixel dimensions
    /// and returns the encoded PNG. Returns <see langword="null"/> when the file is
    /// missing or cannot be decoded.
    /// </summary>
    public byte[]? DownscaleTo1x(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var image = DownscaleTo1xImage(filePath);
        return image is null ? null : EncodePng(image);
    }

    /// <summary>
    /// Downscales the image at <paramref name="filePath"/> to half its pixel dimensions
    /// and returns the frozen bitmap. Returns <see langword="null"/> when the file is
    /// missing or cannot be decoded.
    /// </summary>
    public BitmapSource? DownscaleTo1xImage(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count == 0)
            {
                return null;
            }

            var frame = decoder.Frames[0];
            if (!frame.IsFrozen)
            {
                frame.Freeze();
            }

            return DownscaleTo1x(frame);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or
            ArgumentException or InvalidOperationException or COMException or FileFormatException)
        {
            Log.Warn($"Image downscale failed for '{filePath}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Returns a frozen copy of <paramref name="source"/> at half its pixel dimensions
    /// (each dimension clamped to at least 1 pixel).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="source"/> has no pixels.</exception>
    public BitmapSource DownscaleTo1x(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.PixelWidth <= 0 || source.PixelHeight <= 0)
        {
            throw new ArgumentException("The source image has no pixels.", nameof(source));
        }

        // Integer division matches the macOS upstream: 101 -> 50, 61 -> 30.
        var targetWidth = Math.Max(1, source.PixelWidth / 2);
        var targetHeight = Math.Max(1, source.PixelHeight / 2);

        var scaleX = targetWidth / (double)source.PixelWidth;
        var scaleY = targetHeight / (double)source.PixelHeight;

        // WIC rounds the scaled size; using the exact target ratio (rather than a
        // literal 0.5) keeps odd dimensions on the intended pixel count.
        var scaled = new TransformedBitmap(source, new ScaleTransform(scaleX, scaleY));
        scaled.Freeze();
        return scaled;
    }

    /// <summary>Encodes <paramref name="image"/> as a PNG byte array, or null on failure.</summary>
    public byte[]? EncodePng(BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);

        try
        {
            using var stream = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            encoder.Save(stream);
            return stream.ToArray();
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidOperationException or COMException)
        {
            Log.Error("PNG encoding failed", ex);
            return null;
        }
    }

    /// <summary>
    /// Copies a 1x downscaled copy of the image at <paramref name="filePath"/> to the
    /// clipboard. Returns false when the image could not be decoded.
    /// </summary>
    /// <remarks>WPF's clipboard requires an STA thread, so call this from the UI thread.</remarks>
    public bool CopyDownscaledToClipboard(string filePath, IClipboardService clipboard)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(clipboard);

        var image = DownscaleTo1xImage(filePath);
        if (image is null)
        {
            return false;
        }

        try
        {
            clipboard.SetImage(image);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            Log.Error($"Copying the downscaled image '{filePath}' failed", ex);
            return false;
        }
    }
}
