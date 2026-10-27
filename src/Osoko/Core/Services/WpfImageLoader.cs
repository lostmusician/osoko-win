using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Osoko.Core.Services;

/// <summary>
/// WPF implementation of <see cref="IImageLoader"/>. Thumbnails are decoded at the
/// target size (never the full resolution) and frozen so the UI thread can bind to
/// them from any thread.
/// </summary>
public sealed class WpfImageLoader : IImageLoader
{
    public BitmapSource? LoadThumbnail(string filePath, int decodePixelWidth)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat;
            bitmap.UriSource = new Uri(Path.GetFullPath(filePath), UriKind.Absolute);
            if (decodePixelWidth > 0)
            {
                bitmap.DecodePixelWidth = decodePixelWidth;
            }

            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            Debug.WriteLine($"[Osoko] Thumbnail decode failed for '{filePath}': {ex.Message}");
            return null;
        }
    }

    public (int Width, int Height)? ReadPixelSize(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None);

            if (decoder.Frames.Count == 0)
            {
                return null;
            }

            var frame = decoder.Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            Debug.WriteLine($"[Osoko] Size probe failed for '{filePath}': {ex.Message}");
            return null;
        }
    }
}
