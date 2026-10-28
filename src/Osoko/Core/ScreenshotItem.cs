using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace Osoko.Core;

/// <summary>Where a shelf item originally came from.</summary>
public enum ScreenshotSource
{
    /// <summary>Copied to the clipboard by Windows Snipping Tool / Print Screen.</summary>
    SnippingTool,

    /// <summary>Image copied to the clipboard by another application.</summary>
    Clipboard,

    /// <summary>Discovered in <c>%USERPROFILE%\Pictures\Screenshots</c>.</summary>
    ScreenshotsFolder,

    /// <summary>Added manually (drag &amp; drop, command line, tests).</summary>
    Imported,
}

/// <summary>
/// A single screenshot living on the shelf. Instances are lightweight: the decoded
/// thumbnail is a frozen <see cref="BitmapSource"/> so it can be created off the UI
/// thread and shared between the card, the tray and OCR.
/// </summary>
public sealed class ScreenshotItem : INotifyPropertyChanged
{
    private const int MaxBarcodeLabelLength = 48;

    private BitmapSource? _thumbnail;
    private bool _isPinned;
    private string? _ocrText;
    private bool _isRecognizing;
    private string? _detectedBarcode;
    private int _pixelWidth;
    private int _pixelHeight;

    public ScreenshotItem(
        string filePath,
        ScreenshotSource source = ScreenshotSource.Imported,
        DateTimeOffset? createdAt = null,
        string? detectedBarcode = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A screenshot needs a file path.", nameof(filePath));
        }

        FilePath = Path.GetFullPath(filePath);
        Source = source;
        CreatedAt = createdAt ?? DateTimeOffset.Now;
        DetectedBarcode = detectedBarcode;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Stable identity used by commands, drag &amp; drop and pin toggles.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Absolute path of the cached PNG (inside the scratch folder).</summary>
    public string FilePath { get; }

    public string FileName => Path.GetFileName(FilePath);

    public ScreenshotSource Source { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>Friendly capture time, e.g. "14:32".</summary>
    public string TimeLabel => CreatedAt.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    /// <summary>"just now", "4m ago", "2h ago". Evaluated when the UI binds it.</summary>
    public string AgeLabel
    {
        get
        {
            var age = DateTimeOffset.Now - CreatedAt;
            if (age.TotalSeconds < 45)
            {
                return "just now";
            }

            if (age.TotalMinutes < 60)
            {
                return $"{Math.Max(1, (int)age.TotalMinutes)}m ago";
            }

            if (age.TotalHours < 24)
            {
                return $"{(int)age.TotalHours}h ago";
            }

            return CreatedAt.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
        }
    }

    public string SourceLabel => Source switch
    {
        ScreenshotSource.SnippingTool => "Snipping Tool",
        ScreenshotSource.Clipboard => "Clipboard",
        ScreenshotSource.ScreenshotsFolder => "Pictures\\Screenshots",
        _ => "Imported",
    };

    /// <summary>Decoded, frozen preview used by the card. May be null when decoding failed.</summary>
    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        internal set
        {
            if (ReferenceEquals(_thumbnail, value))
            {
                return;
            }

            _thumbnail = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasThumbnail));
        }
    }

    public bool HasThumbnail => _thumbnail is not null;

    public int PixelWidth
    {
        get => _pixelWidth;
        internal set
        {
            if (Set(ref _pixelWidth, value))
            {
                OnPropertyChanged(nameof(DimensionsLabel));
            }
        }
    }

    public int PixelHeight
    {
        get => _pixelHeight;
        internal set
        {
            if (Set(ref _pixelHeight, value))
            {
                OnPropertyChanged(nameof(DimensionsLabel));
            }
        }
    }

    /// <summary>Human readable size, e.g. "1920 × 1080".</summary>
    public string DimensionsLabel =>
        _pixelWidth > 0 && _pixelHeight > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0} × {1}", _pixelWidth, _pixelHeight)
            : "—";

    /// <summary>Pinned items survive stack pruning and "Clear stack".</summary>
    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (Set(ref _isPinned, value))
            {
                OnPropertyChanged(nameof(PinGlyph));
                OnPropertyChanged(nameof(PinTooltip));
            }
        }
    }

    /// <summary>Segoe Fluent glyph shown on the pin button (Pin / Unpin).</summary>
    public string PinGlyph => _isPinned ? "\uE77A" : "\uE718";

    public string PinTooltip => _isPinned ? "Unpin" : "Pin to shelf";

    /// <summary>Cached OCR result. Null = not recognized yet, empty string = no text found.</summary>
    public string? OcrText
    {
        get => _ocrText;
        internal set
        {
            if (Set(ref _ocrText, value))
            {
                OnPropertyChanged(nameof(HasOcrText));
            }
        }
    }

    public bool HasOcrText => !string.IsNullOrEmpty(_ocrText);

    /// <summary>
    /// Payload of the first QR code or barcode detected in the capture. Null until the
    /// background scan completes (or when the image contains no supported code).
    /// </summary>
    public string? DetectedBarcode
    {
        get => _detectedBarcode;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (Set(ref _detectedBarcode, normalized))
            {
                OnPropertyChanged(nameof(HasDetectedBarcode));
                OnPropertyChanged(nameof(DetectedBarcodeLabel));
            }
        }
    }

    public bool HasDetectedBarcode => !string.IsNullOrWhiteSpace(_detectedBarcode);

    /// <summary>
    /// Short, bindable description of the barcode: the host name for web links,
    /// otherwise the payload (truncated when it is long).
    /// </summary>
    public string DetectedBarcodeLabel
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_detectedBarcode))
            {
                return string.Empty;
            }

            var payload = _detectedBarcode.Trim();
            if (Uri.TryCreate(payload, UriKind.Absolute, out var uri) &&
                !string.IsNullOrWhiteSpace(uri.Host))
            {
                return uri.Host;
            }

            return payload.Length <= MaxBarcodeLabelLength
                ? payload
                : payload[..(MaxBarcodeLabelLength - 1)] + "…";
        }
    }

    /// <summary>True while the OCR engine is working on this item (drives the spinner).</summary>
    public bool IsRecognizing
    {
        get => _isRecognizing;
        internal set => Set(ref _isRecognizing, value);
    }

    /// <summary>True when <see cref="FilePath"/> lives in Osoko's scratch cache.</summary>
    public bool IsTemporary => AppPaths.IsScratchFile(FilePath);

    public override string ToString() => $"{FileName} ({DimensionsLabel})";

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
