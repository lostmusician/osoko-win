using System.Windows.Media.Imaging;

namespace Osoko.Core.Services;

/// <summary>Decodes thumbnails and image metadata. Swappable so tests run headless.</summary>
public interface IImageLoader
{
    /// <summary>Decodes a downscaled, frozen preview. Returns null when the file is unreadable.</summary>
    BitmapSource? LoadThumbnail(string filePath, int decodePixelWidth);

    /// <summary>Reads the true pixel size without decoding the whole image.</summary>
    (int Width, int Height)? ReadPixelSize(string filePath);
}

/// <summary>Minimal clipboard façade (WPF's <c>Clipboard</c> is static and hard to fake).</summary>
public interface IClipboardService
{
    void SetImage(string filePath);

    void SetImage(BitmapSource image);

    void SetText(string text);
}

/// <summary>Native save / folder dialogs, abstracted for unit tests.</summary>
public interface IFileDialogService
{
    /// <summary>Shows a "Save as" dialog. Returns null when the user cancels.</summary>
    string? PickSavePath(string suggestedFileName, string initialDirectory);

    /// <summary>Shows a folder picker. Returns null when the user cancels.</summary>
    string? PickFolder(string title, string initialDirectory);
}

/// <summary>Offline OCR engine (Windows.Media.Ocr in production).</summary>
public interface IScreenshotOcrService
{
    /// <summary>Warms up the OCR engine in the background so the first copy is instant.</summary>
    Task PrewarmAsync(CancellationToken cancellationToken = default);

    /// <summary>Recognizes text in the image at <paramref name="imagePath"/>.</summary>
    Task<string> RecognizeTextAsync(string imagePath, CancellationToken cancellationToken = default);
}
