using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.Common;

namespace Osoko.Core.Services;

/// <summary>
/// Detects QR codes and 1D/2D barcodes in screenshots with ZXing.Net.
/// <para>
/// Decoding is CPU bound, so it runs on the thread pool and results are cached per
/// canonical file path. Concurrent requests for the same path share a single decode,
/// and a cache entry is invalidated automatically when the file changes on disk.
/// Only successful detections are cached: a miss is cheap to retry and may succeed
/// after the capture is edited.
/// </para>
/// </summary>
public sealed class BarcodeDetectionService
{
    /// <summary>Larger images are scaled down before decoding to keep latency predictable.</summary>
    public const int MaxDecodeDimension = 4096;

    private static readonly BarcodeFormat[] SupportedFormatsInternal =
    [
        BarcodeFormat.QR_CODE,
        BarcodeFormat.AZTEC,
        BarcodeFormat.DATA_MATRIX,
        BarcodeFormat.CODE_128,
        BarcodeFormat.EAN_13,
    ];

    private static readonly ReadOnlyCollection<BarcodeFormat> SupportedFormatsReadOnly =
        Array.AsReadOnly(SupportedFormatsInternal);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Default instance for callers that do not construct their own.</summary>
    public static BarcodeDetectionService Shared { get; } = new();

    /// <summary>Barcode symbologies Osoko understands.</summary>
    public IReadOnlyList<BarcodeFormat> SupportedFormats => SupportedFormatsReadOnly;

    /// <summary>Number of paths currently cached (hits and in-flight scans).</summary>
    public int CachedCount => _cache.Count;

    /// <summary>
    /// Returns the first non-empty payload of a QR code or barcode found in
    /// <paramref name="filePath"/>, or <see langword="null"/> when the image cannot be
    /// read or contains no supported code.
    /// </summary>
    /// <remarks>
    /// Cancelling only abandons the wait; the shared decode keeps running so the
    /// result is available to the next caller.
    /// </remarks>
    public async Task<string?> DetectBarcodeAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        cancellationToken.ThrowIfCancellationRequested();

        var canonicalPath = TryCanonicalize(filePath);
        if (canonicalPath is null)
        {
            return null;
        }

        var stamp = FileStamp.Read(canonicalPath);
        var fresh = CreateEntry(canonicalPath, stamp);
        var entry = _cache.AddOrUpdate(
            canonicalPath,
            fresh,
            (_, existing) => existing.Stamp.IsCompatibleWith(stamp) ? existing : fresh);

        string? payload;
        try
        {
            payload = await entry.Detection.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The shared decode keeps running and evicts itself if it finds nothing.
            throw;
        }

        return payload;
    }

    /// <summary>
    /// Returns the first non-empty payload of a QR code or barcode found in
    /// <paramref name="imageStream"/> (a file path has no meaning for a raw stream), or <see langword="null"/> when the stream cannot be
    /// decoded or contains no supported code.
    /// </summary>
    /// <remarks>
    /// Decoding starts at the stream's current position. The stream is left open and
    /// results are not cached (a stream has no stable path). Non-seekable streams are
    /// buffered in memory first so WIC can decode them.
    /// </remarks>
    public Task<string?> DetectBarcodeFromStreamAsync(Stream imageStream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageStream);
        if (!imageStream.CanRead)
        {
            throw new ArgumentException("The image stream must be readable.", nameof(imageStream));
        }

        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(
            () =>
            {
                try
                {
                    if (imageStream.CanSeek)
                    {
                        return DecodeStream(imageStream);
                    }

                    using var buffer = new MemoryStream();
                    imageStream.CopyTo(buffer);
                    buffer.Position = 0;
                    return DecodeStream(buffer);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warn($"Barcode detection failed for a stream: {ex.Message}");
                    return null;
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// Starts detection in the background so the first explicit request is instant.
    /// Failures are logged and otherwise ignored.
    /// </summary>
    public void Prewarm(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await DetectBarcodeAsync(filePath, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"Barcode prewarm failed for '{filePath}': {ex.Message}");
            }
        });
    }

    /// <summary>Forgets the cached result for <paramref name="filePath"/>.</summary>
    public void ClearCache(string filePath)
    {
        var canonicalPath = TryCanonicalize(filePath);
        if (canonicalPath is not null)
        {
            _cache.TryRemove(canonicalPath, out _);
        }
    }

    /// <summary>Forgets every cached result.</summary>
    public void ClearCache() => _cache.Clear();

    private CacheEntry CreateEntry(string canonicalPath, FileStamp stamp)
    {
        CacheEntry? entry = null;

        async Task<string?> DetectAndEvictMissAsync()
        {
            var payload = await Task
                .Run(() => DetectCore(canonicalPath), CancellationToken.None)
                .ConfigureAwait(false);

            // Only successful detections stay cached. Evicting from inside the shared
            // task guarantees a miss is forgotten even when every waiter cancels.
            if (payload is null && entry is not null)
            {
                _cache.TryRemove(new KeyValuePair<string, CacheEntry>(canonicalPath, entry));
            }

            return payload;
        }

        // The Lazy factory only runs after the entry has been assigned below, so the
        // closure always observes a non-null entry.
        entry = new CacheEntry(stamp, new Lazy<Task<string?>>(DetectAndEvictMissAsync));
        return entry;
    }

    private static string? DetectCore(string canonicalPath)
    {
        if (!File.Exists(canonicalPath))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(canonicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return DecodeStream(stream);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Detection is best effort: a corrupt or unsupported image must never
            // surface as an exception to the card action that requested it.
            Log.Warn($"Barcode detection failed for '{canonicalPath}': {ex.Message}");
            return null;
        }
    }

    private static string? DecodeStream(Stream stream)
    {
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad);

        if (decoder.Frames.Count == 0)
        {
            return null;
        }

        var bitmap = PrepareForDecoding(decoder.Frames[0]);
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
        {
            return null;
        }

        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                TryInverted = true,
                PossibleFormats = new List<BarcodeFormat>(SupportedFormatsInternal),
            },
        };

        var luminance = new RGBLuminanceSource(
            pixels,
            bitmap.PixelWidth,
            bitmap.PixelHeight,
            RGBLuminanceSource.BitmapFormat.BGRA32);

        var results = reader.DecodeMultiple(luminance);
        if (results is { Length: > 0 })
        {
            foreach (var result in results)
            {
                if (TryReadPayload(result.Text, out var payload))
                {
                    return payload;
                }
            }
        }

        // Some symbologies only expose a single result; try the cheaper path as a fallback.
        return TryReadPayload(reader.Decode(luminance)?.Text, out var single) ? single : null;
    }

    private static bool TryReadPayload(string? text, out string payload)
    {
        payload = text?.Trim() ?? string.Empty;
        return payload.Length > 0;
    }

    private static BitmapSource PrepareForDecoding(BitmapSource frame)
    {
        BitmapSource current = frame;
        var longestSide = Math.Max(frame.PixelWidth, frame.PixelHeight);
        if (longestSide > MaxDecodeDimension)
        {
            var scale = MaxDecodeDimension / (double)longestSide;
            var scaled = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
            scaled.Freeze();
            current = scaled;
        }

        if (current.Format != PixelFormats.Bgra32)
        {
            var converted = new FormatConvertedBitmap(current, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            current = converted;
        }
        else if (!current.IsFrozen)
        {
            current.Freeze();
        }

        return current;
    }

    private static string? TryCanonicalize(string filePath)
    {
        try
        {
            var fullPath = Path.GetFullPath(filePath);
            if (File.Exists(fullPath))
            {
                try
                {
                    var target = File.ResolveLinkTarget(fullPath, returnFinalTarget: true);
                    if (target is not null)
                    {
                        fullPath = target.FullName;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    // Keep the unresolved full path; it is still a stable cache key.
                }
            }

            return fullPath;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private readonly record struct FileStamp(bool Exists, long Length, DateTime LastWriteTimeUtc)
    {
        public static FileStamp Read(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? new FileStamp(true, info.Length, info.LastWriteTimeUtc) : default;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                return default;
            }
        }

        /// <summary>
        /// A cached result survives deleting the source file (the macOS upstream
        /// behaves the same way) but is invalidated when the file is rewritten.
        /// </summary>
        public bool IsCompatibleWith(FileStamp current) => !current.Exists || Equals(current);
    }

    private sealed class CacheEntry
    {
        public CacheEntry(FileStamp stamp, Lazy<Task<string?>> detection)
        {
            Stamp = stamp;
            Detection = detection;
        }

        public FileStamp Stamp { get; }

        public Lazy<Task<string?>> Detection { get; }
    }
}
