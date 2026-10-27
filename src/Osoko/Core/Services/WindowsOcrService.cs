using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Osoko.Core.Services;

/// <summary>
/// Offline OCR built on the Windows 10/11 imaging stack
/// (<see cref="OcrEngine"/>). No cloud calls, no extra runtimes: the recognition
/// models ship with Windows and support every installed display language.
/// <para>
/// The engine is created once, on a background thread, and reused. Large images are
/// downscaled to <see cref="OcrEngine.MaxImageDimension"/> before recognition, which is
/// both faster and more accurate than feeding the engine an out-of-range bitmap.
/// </para>
/// </summary>
public sealed class WindowsOcrService : IScreenshotOcrService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OcrEngine? _engine;
    private bool _engineUnavailable;
    private bool _disposed;

    /// <summary>True when a Windows OCR language pack is installed and the engine is ready.</summary>
    public bool IsAvailable => _engine is not null;

    /// <summary>BCP-47 tag of the active recognizer, e.g. <c>en-US</c>.</summary>
    public string? EngineLanguage => _engine?.RecognizerLanguage?.LanguageTag;

    /// <inheritdoc />
    public async Task PrewarmAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureEngineAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"OCR prewarm failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<string> RecognizeTextAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        if (!File.Exists(imagePath))
        {
            return string.Empty;
        }

        var engine = await EnsureEngineAsync(cancellationToken).ConfigureAwait(false);
        if (engine is null)
        {
            return string.Empty;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RecognizeCoreAsync(engine, imagePath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"OCR failed for '{imagePath}'", ex);
            return string.Empty;
        }
        finally
        {
            _gate.Release();
        }
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
                Log.Warn("No Windows OCR language pack is installed; text recognition is disabled.");
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
            // Prefer the user's languages so mixed-language desktops pick the right model.
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is not null)
            {
                return engine;
            }

            foreach (var tag in new[] { "en-US", "en-GB" })
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

    private static async Task<string> RecognizeCoreAsync(OcrEngine engine, string imagePath, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var randomAccessStream = stream.AsRandomAccessStream();

        var decoder = await BitmapDecoder.CreateAsync(randomAccessStream).AsTask(cancellationToken).ConfigureAwait(false);
        if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0)
        {
            return string.Empty;
        }

        var transform = BuildTransform(decoder.PixelWidth, decoder.PixelHeight, OcrEngine.MaxImageDimension);

        using var softwareBitmap = await decoder
            .GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        var result = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken).ConfigureAwait(false);
        return NormalizeText(result);
    }

    private static BitmapTransform BuildTransform(uint pixelWidth, uint pixelHeight, uint maxDimension)
    {
        var transform = new BitmapTransform();
        var longestSide = Math.Max(pixelWidth, pixelHeight);
        if (longestSide <= maxDimension || maxDimension == 0)
        {
            return transform;
        }

        var scale = maxDimension / (double)longestSide;
        transform.ScaledWidth = Math.Max(1u, (uint)Math.Round(pixelWidth * scale));
        transform.ScaledHeight = Math.Max(1u, (uint)Math.Round(pixelHeight * scale));
        transform.InterpolationMode = BitmapInterpolationMode.Fant;
        return transform;
    }

    private static string NormalizeText(OcrResult result)
    {
        var text = result.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            // Fall back to line-by-line assembly; some language packs leave OcrResult.Text empty.
            text = string.Join(Environment.NewLine, result.Lines.Select(line => line.Text));
        }

        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }
}
