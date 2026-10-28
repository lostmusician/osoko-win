using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Globalization;
using Windows.Media.Ocr;
using WinImaging = Windows.Graphics.Imaging;

namespace Osoko.Core.Services;

/// <summary>The output of a privacy-redaction pass over a screenshot.</summary>
/// <param name="PngBytes">PNG bytes of the redacted image (or the original PNG bytes when nothing matched).</param>
/// <param name="RedactedImage">Frozen bitmap matching <paramref name="PngBytes"/>.</param>
/// <param name="MatchCount">Number of sensitive regions that were covered.</param>
public sealed record RedactionResult(byte[] PngBytes, BitmapSource RedactedImage, int MatchCount)
{
    /// <summary>True when at least one sensitive region was found and covered.</summary>
    public bool HasRedactions => MatchCount > 0;

    /// <summary>Fallback used when the source image cannot be read.</summary>
    public static RedactionResult Empty { get; } = new(Array.Empty<byte>(), CreatePlaceholder(), 0);

    private static BitmapSource CreatePlaceholder()
    {
        var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        image.Freeze();
        return image;
    }
}

/// <summary>
/// Finds and redacts sensitive text (API keys, tokens, emails, card numbers and
/// password-bearing URLs) in screenshots.
/// <para>
/// Recognition uses the offline Windows OCR engine. Because OCR exposes a bounding
/// rectangle for every word, each regex match is mapped back to the words it covers
/// and those words are painted over with solid black rounded rectangles. When no
/// sensitive text is found, PNG sources are returned byte-for-byte untouched while
/// other formats are re-encoded as PNG.
/// </para>
/// </summary>
public sealed class PrivacyRedactionService : IDisposable
{
    private const int PaddingPixels = 2;
    private const int CornerRadiusPixels = 3;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly string[] FallbackOcrLanguages = ["en-US", "en-GB"];

    // Keep these in sync with the macOS upstream (Sources/OsokoKit/Utilities/PrivacyRedactionService.swift).
    private static readonly Regex[] SensitivePatterns =
    [
        new(@"\b(?:sk|pk)_(?:live|test)_[a-zA-Z0-9]{20,}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant, RegexTimeout),
        new(@"\bsk-[a-zA-Z0-9_\-]{20,}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant, RegexTimeout),
        new(@"\b(?:ghp|gho|ghu|ghs|ghr)_[a-zA-Z0-9]{36,}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant, RegexTimeout),
        new(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant, RegexTimeout),
        new(@"\beyJ[a-zA-Z0-9_\-]{10,}\.eyJ[a-zA-Z0-9_\-]{10,}\.[a-zA-Z0-9_\-]{10,}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant, RegexTimeout),
        new(@"(?i)\b(?:bearer\s+)([a-zA-Z0-9_\-\.]{24,})\b", RegexOptions.Compiled | RegexOptions.CultureInvariant, RegexTimeout),
        new(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant, RegexTimeout),
        new(@"\b(?:\d{4}[ -]?){3}\d{4}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant, RegexTimeout),
        new(@"https?://[^:\s]+:[^@\s]+@[^\s]+", RegexOptions.Compiled | RegexOptions.CultureInvariant, RegexTimeout),
    ];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private OcrEngine? _engine;
    private bool _engineUnavailable;
    private bool _disposed;

    /// <summary>Default instance for callers that do not construct their own.</summary>
    public static PrivacyRedactionService Shared { get; } = new();

    /// <summary>True once a Windows OCR language pack has been found and the engine is ready.</summary>
    public bool IsOcrAvailable => _engine is not null;

    /// <summary>
    /// Returns a copy of the image at <paramref name="imagePath"/> with every detected
    /// secret covered by a solid black rounded rectangle.
    /// </summary>
    /// <remarks>
    /// When the file is unreadable <see cref="RedactionResult.Empty"/> is returned. When
    /// OCR is unavailable, or no sensitive text is found, the original PNG bytes are returned
    /// with <see cref="RedactionResult.MatchCount"/> set to zero.
    /// </remarks>
    public async Task<RedactionResult> RedactSensitiveDataAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(imagePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Log.Warn($"Privacy redaction skipped: invalid path '{imagePath}' ({ex.Message}).");
            return RedactionResult.Empty;
        }

        if (!File.Exists(fullPath))
        {
            Log.Warn($"Privacy redaction skipped: '{fullPath}' does not exist.");
            return RedactionResult.Empty;
        }

        BitmapSource? source;
        try
        {
            source = await Task.Run(() => LoadBgra32(fullPath), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsRecoverableImageException(ex))
        {
            Log.Error($"Privacy redaction could not decode '{fullPath}'", ex);
            return RedactionResult.Empty;
        }

        if (source is null)
        {
            Log.Warn($"Privacy redaction skipped: '{fullPath}' produced no image.");
            return RedactionResult.Empty;
        }

        IReadOnlyList<Int32Rect> areas;
        try
        {
            areas = await FindSensitiveAreasAsync(fullPath, source, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"Privacy redaction OCR failed for '{fullPath}'", ex);
            areas = Array.Empty<Int32Rect>();
        }

        if (areas.Count == 0)
        {
            // PNG sources are preserved byte-for-byte, matching the macOS upstream.
            // Other formats are normalised to PNG so PngBytes always means PNG.
            byte[] originalBytes;
            try
            {
                originalBytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Privacy redaction could not read '{fullPath}': {ex.Message}");
                return new RedactionResult(Array.Empty<byte>(), source, 0);
            }

            if (IsPng(originalBytes))
            {
                return new RedactionResult(originalBytes, source, 0);
            }

            var pngBytes = await Task.Run(() => EncodePng(source), cancellationToken).ConfigureAwait(false);
            return new RedactionResult(pngBytes ?? Array.Empty<byte>(), source, 0);
        }

        return await Task.Run(() => ApplyRedaction(source, areas), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Redacts <paramref name="imagePath"/> and, when at least one secret was found,
    /// puts the redacted bitmap on the clipboard. Returns the number of redacted
    /// regions. The clipboard is left untouched when nothing matched.
    /// </summary>
    /// <remarks>WPF's clipboard requires an STA thread, so call this from the UI thread.</remarks>
    public async Task<int> CopyRedactedToClipboardAsync(
        string imagePath,
        IClipboardService clipboard,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clipboard);

        // Deliberately capture the caller's context so clipboard access stays on the UI thread.
        var result = await RedactSensitiveDataAsync(imagePath, cancellationToken).ConfigureAwait(true);
        if (!result.HasRedactions)
        {
            return 0;
        }

        clipboard.SetImage(result.RedactedImage);
        return result.MatchCount;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _engine = null;
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private static BitmapSource? LoadBgra32(string fullPath)
    {
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad);

        if (decoder.Frames.Count == 0)
        {
            return null;
        }

        BitmapSource frame = decoder.Frames[0];
        if (frame.Format != PixelFormats.Bgra32)
        {
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            frame = converted;
        }

        if (!frame.IsFrozen)
        {
            frame.Freeze();
        }

        return frame;
    }

    private async Task<IReadOnlyList<Int32Rect>> FindSensitiveAreasAsync(
        string fullPath,
        BitmapSource source,
        CancellationToken cancellationToken)
    {
        var engine = await EnsureEngineAsync(cancellationToken).ConfigureAwait(false);
        if (engine is null)
        {
            return Array.Empty<Int32Rect>();
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var randomAccessStream = stream.AsRandomAccessStream();
            var decoder = await WinImaging.BitmapDecoder
                .CreateAsync(randomAccessStream)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0)
            {
                return Array.Empty<Int32Rect>();
            }

            var transform = BuildTransform(decoder.PixelWidth, decoder.PixelHeight, OcrEngine.MaxImageDimension);

            using var bitmap = await decoder
                .GetSoftwareBitmapAsync(
                    WinImaging.BitmapPixelFormat.Bgra8,
                    WinImaging.BitmapAlphaMode.Premultiplied,
                    transform,
                    WinImaging.ExifOrientationMode.IgnoreExifOrientation,
                    WinImaging.ColorManagementMode.DoNotColorManage)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            var ocrResult = await engine
                .RecognizeAsync(bitmap)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            return MapMatchesToAreas(
                ocrResult,
                bitmap.PixelWidth,
                bitmap.PixelHeight,
                source.PixelWidth,
                source.PixelHeight);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<OcrEngine?> EnsureEngineAsync(CancellationToken cancellationToken)
    {
        if (_engine is not null || _engineUnavailable)
        {
            return _engine;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_engine is not null || _engineUnavailable)
            {
                return _engine;
            }

            // OcrEngine creation touches WinRT activation: do it off the UI thread.
            _engine = await Task.Run(CreateEngine, cancellationToken).ConfigureAwait(false);
            if (_engine is null)
            {
                _engineUnavailable = true;
                Log.Warn("No Windows OCR language pack is installed; privacy redaction is disabled.");
            }

            return _engine;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static OcrEngine? CreateEngine()
    {
        try
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is not null)
            {
                return engine;
            }

            foreach (var tag in FallbackOcrLanguages)
            {
                engine = OcrEngine.TryCreateFromLanguage(new Language(tag));
                if (engine is not null)
                {
                    return engine;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is COMException or TypeLoadException or FileNotFoundException)
        {
            Log.Error("Windows OCR engine creation failed", ex);
            return null;
        }
    }

    private static WinImaging.BitmapTransform BuildTransform(uint pixelWidth, uint pixelHeight, uint maxDimension)
    {
        var transform = new WinImaging.BitmapTransform();
        var longestSide = Math.Max(pixelWidth, pixelHeight);
        if (longestSide <= maxDimension || maxDimension == 0)
        {
            return transform;
        }

        var scale = maxDimension / (double)longestSide;
        transform.ScaledWidth = Math.Max(1u, (uint)Math.Round(pixelWidth * scale));
        transform.ScaledHeight = Math.Max(1u, (uint)Math.Round(pixelHeight * scale));
        transform.InterpolationMode = WinImaging.BitmapInterpolationMode.Fant;
        return transform;
    }

    private static IReadOnlyList<Int32Rect> MapMatchesToAreas(
        OcrResult result,
        int ocrWidth,
        int ocrHeight,
        int targetWidth,
        int targetHeight)
    {
        if (ocrWidth <= 0 || ocrHeight <= 0 || targetWidth <= 0 || targetHeight <= 0)
        {
            return Array.Empty<Int32Rect>();
        }

        var scaleX = targetWidth / (double)ocrWidth;
        var scaleY = targetHeight / (double)ocrHeight;
        var areas = new List<Int32Rect>();

        foreach (var line in result.Lines)
        {
            var lineText = line.Text;
            if (string.IsNullOrEmpty(lineText))
            {
                // Some language packs leave OcrLine.Text empty; rebuild it from words.
                lineText = string.Join(" ", line.Words.Select(word => word.Text));
            }

            if (string.IsNullOrEmpty(lineText))
            {
                continue;
            }

            var ranges = FindSensitiveRanges(lineText);
            if (ranges.Count == 0)
            {
                continue;
            }

            var words = BuildWordSpans(line, lineText);
            if (words.Count == 0)
            {
                continue;
            }

            var lineBounds = UnionAll(words);
            foreach (var (start, end) in ranges)
            {
                // If no word maps back to the match (OCR split it oddly), fall back to
                // the whole line so the secret is never left visible.
                var matchBounds = UnionMatching(words, start, end) ?? lineBounds;
                AddArea(areas, matchBounds, scaleX, scaleY, targetWidth, targetHeight);
            }
        }

        return areas;
    }

    private static List<(int Start, int End)> FindSensitiveRanges(string text)
    {
        var ranges = new List<(int Start, int End)>();
        foreach (var pattern in SensitivePatterns)
        {
            try
            {
                foreach (Match match in pattern.Matches(text))
                {
                    if (match.Success && match.Length > 0)
                    {
                        ranges.Add((match.Index, match.Index + match.Length));
                    }
                }
            }
            catch (RegexMatchTimeoutException ex)
            {
                Log.Warn($"Sensitive-data regex timed out: {ex.Message}");
            }
        }

        return ranges;
    }

    private static List<WordSpan> BuildWordSpans(OcrLine line, string lineText)
    {
        var spans = new List<WordSpan>();
        var cursor = 0;

        foreach (var word in line.Words)
        {
            var wordText = word.Text;
            if (string.IsNullOrEmpty(wordText))
            {
                continue;
            }

            // OCR lines concatenate words with single spaces; walk the line so each
            // word keeps its character range for regex overlap tests.
            var index = lineText.IndexOf(wordText, cursor, StringComparison.Ordinal);
            if (index < 0)
            {
                index = lineText.IndexOf(wordText, StringComparison.Ordinal);
            }

            if (index < 0)
            {
                continue;
            }

            var bounds = word.BoundingRect;
            spans.Add(new WordSpan(
                index,
                index + wordText.Length,
                new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height)));
            cursor = index + wordText.Length;
        }

        return spans;
    }

    private static Rect UnionAll(List<WordSpan> spans)
    {
        var bounds = spans[0].Bounds;
        for (var i = 1; i < spans.Count; i++)
        {
            bounds = Rect.Union(bounds, spans[i].Bounds);
        }

        return bounds;
    }

    private static Rect? UnionMatching(List<WordSpan> spans, int start, int end)
    {
        Rect? union = null;
        foreach (var span in spans)
        {
            if (span.End <= start || span.Start >= end)
            {
                continue;
            }

            union = union is null ? span.Bounds : Rect.Union(union.Value, span.Bounds);
        }

        return union;
    }

    private static void AddArea(
        List<Int32Rect> areas,
        Rect bounds,
        double scaleX,
        double scaleY,
        int imageWidth,
        int imageHeight)
    {
        var left = (int)Math.Floor(bounds.X * scaleX) - PaddingPixels;
        var top = (int)Math.Floor(bounds.Y * scaleY) - PaddingPixels;
        var right = (int)Math.Ceiling((bounds.X + bounds.Width) * scaleX) + PaddingPixels;
        var bottom = (int)Math.Ceiling((bounds.Y + bounds.Height) * scaleY) + PaddingPixels;

        left = Math.Clamp(left, 0, imageWidth);
        top = Math.Clamp(top, 0, imageHeight);
        right = Math.Clamp(right, 0, imageWidth);
        bottom = Math.Clamp(bottom, 0, imageHeight);

        if (right > left && bottom > top)
        {
            areas.Add(new Int32Rect(left, top, right - left, bottom - top));
        }
    }

    private static RedactionResult ApplyRedaction(BitmapSource source, IReadOnlyList<Int32Rect> areas)
    {
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var stride = width * 4;
        if (width <= 0 || height <= 0 || (long)stride * height > int.MaxValue)
        {
            Log.Warn($"Redaction skipped: {width}x{height} is too large to copy into memory.");
            return new RedactionResult(Array.Empty<byte>(), source, 0);
        }

        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        foreach (var area in areas)
        {
            FillRoundedRectangle(pixels, stride, width, height, area);
        }

        var dpiX = source.DpiX > 0 ? source.DpiX : 96;
        var dpiY = source.DpiY > 0 ? source.DpiY : 96;
        var redacted = BitmapSource.Create(width, height, dpiX, dpiY, PixelFormats.Bgra32, null, pixels, stride);
        redacted.Freeze();

        var pngBytes = EncodePng(redacted);
        return new RedactionResult(pngBytes ?? Array.Empty<byte>(), redacted, areas.Count);
    }

    private static void FillRoundedRectangle(
        byte[] pixels,
        int stride,
        int imageWidth,
        int imageHeight,
        Int32Rect area)
    {
        var left = Math.Max(0, area.X);
        var top = Math.Max(0, area.Y);
        var right = Math.Min(imageWidth, area.X + area.Width) - 1;
        var bottom = Math.Min(imageHeight, area.Y + area.Height) - 1;
        if (left > right || top > bottom)
        {
            return;
        }

        var radius = Math.Min(CornerRadiusPixels, Math.Min(right - left + 1, bottom - top + 1) / 2);

        for (var y = top; y <= bottom; y++)
        {
            var inset = 0;
            if (radius > 0 && y < top + radius)
            {
                var dy = top + radius - y;
                inset = radius - (int)Math.Round(Math.Sqrt((radius * radius) - (dy * dy)));
            }
            else if (radius > 0 && y > bottom - radius)
            {
                var dy = y - (bottom - radius);
                inset = radius - (int)Math.Round(Math.Sqrt((radius * radius) - (dy * dy)));
            }

            var startX = left + inset;
            var endX = right - inset;
            if (startX > endX)
            {
                continue;
            }

            var rowOffset = y * stride;
            for (var x = startX; x <= endX; x++)
            {
                var offset = rowOffset + (x * 4);
                pixels[offset] = 0;
                pixels[offset + 1] = 0;
                pixels[offset + 2] = 0;
                pixels[offset + 3] = 255;
            }
        }
    }

    private static bool IsPng(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 8 &&
        bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
        bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A;

    private static byte[]? EncodePng(BitmapSource image)
    {
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
            Log.Error("Encoding the redacted image failed", ex);
            return null;
        }
    }

    private static bool IsRecoverableImageException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or
            InvalidOperationException or COMException or FileFormatException;

    private readonly record struct WordSpan(int Start, int End, Rect Bounds);
}
