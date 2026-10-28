using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Media.Imaging;

namespace Osoko.Core.Services;

/// <summary>Why the stack changed.</summary>
public enum StackChangeKind
{
    Added,
    Removed,
    Pruned,
    Cleared,
    Pinned,
}

/// <summary>Event payload for <see cref="ScreenshotManager.StackChanged"/>.</summary>
public sealed class StackChangedEventArgs : EventArgs
{
    public StackChangedEventArgs(
        StackChangeKind kind,
        ScreenshotItem? item = null,
        IReadOnlyList<ScreenshotItem>? prunedItems = null)
    {
        Kind = kind;
        Item = item;
        PrunedItems = prunedItems ?? Array.Empty<ScreenshotItem>();
    }

    public StackChangeKind Kind { get; }

    public ScreenshotItem? Item { get; }

    public IReadOnlyList<ScreenshotItem> PrunedItems { get; }
}

/// <summary>
/// Outcome of executing a <see cref="CardActionKind"/> for a screenshot. The manager
/// never throws for ordinary failures, so callers can surface <see cref="Message"/> as
/// a toast and inspect the optional payload fields.
/// </summary>
public sealed record CardActionResult(
    CardActionKind Action,
    bool Success,
    string Message,
    string? SavedPath = null,
    int MatchCount = 0,
    string? ClipboardText = null)
{
    public bool IsSuccess => Success;

    public string StatusMessage => Message;

    public string? ErrorMessage => Success ? null : Message;

    public static CardActionResult Succeeded(
        CardActionKind action,
        string message,
        string? savedPath = null,
        int matchCount = 0,
        string? clipboardText = null) =>
        new(action, true, message, savedPath, matchCount, clipboardText);

    public static CardActionResult Failed(CardActionKind action, string message) =>
        new(action, false, message);

    public static CardActionResult NoOp(CardActionKind action) =>
        new(action, true, "No action");
}

/// <summary>
/// The heart of Osoko: owns the ordered stack of captures, enforces the configured
/// stack limit (pinned items are exempt), and coordinates clipboard, OCR and save
/// operations for every item.
/// <para>
/// The manager is UI-thread affine. Watchers marshal onto the dispatcher before
/// calling into it; in tests (no synchronization context) calls run inline.
/// </para>
/// </summary>
public sealed class ScreenshotManager : IDisposable
{
    private const int ThumbnailDecodeWidth = 560;

    /// <summary>Identical images arriving within this window are treated as one capture.</summary>
    private static readonly TimeSpan FingerprintWindow = TimeSpan.FromSeconds(20);

    private readonly OsokoSettings _settings;
    private readonly ExportTracker _exportTracker;
    private readonly IImageLoader _imageLoader;
    private readonly IClipboardService? _clipboard;
    private readonly IFileDialogService? _fileDialogs;
    private readonly IScreenshotOcrService? _ocr;
    private readonly Action<string> _deleteScratchFile;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<string, DateTimeOffset> _fingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private bool _disposed;

    public ScreenshotManager(
        OsokoSettings settings,
        ExportTracker exportTracker,
        IImageLoader? imageLoader = null,
        IClipboardService? clipboard = null,
        IFileDialogService? fileDialogs = null,
        IScreenshotOcrService? ocr = null,
        Action<string>? deleteScratchFile = null,
        Func<DateTimeOffset>? clock = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _exportTracker = exportTracker ?? throw new ArgumentNullException(nameof(exportTracker));
        _imageLoader = imageLoader ?? new WpfImageLoader();
        _clipboard = clipboard;
        _fileDialogs = fileDialogs;
        _ocr = ocr;
        _deleteScratchFile = deleteScratchFile ?? DeleteScratchFileDefault;
        _clock = clock ?? (() => DateTimeOffset.Now);

        Items = new ObservableCollection<ScreenshotItem>();
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(HasItems));
        };
    }

    /// <summary>Newest capture first (index 0 is the top card).</summary>
    public ObservableCollection<ScreenshotItem> Items { get; }

    public int Count => Items.Count;

    public bool HasItems => Items.Count > 0;

    public OsokoSettings Settings => _settings;

    public event EventHandler<StackChangedEventArgs>? StackChanged;

    public event EventHandler<ScreenshotItem>? ScreenshotAdded;

    public event EventHandler<ScreenshotItem>? ScreenshotRemoved;

    public event EventHandler<ScreenshotItem>? ScreenshotSaved;

    public event PropertyChangedEventHandler? PropertyChanged;

    // ---------------------------------------------------------------------
    // Stack mutations
    // ---------------------------------------------------------------------

    /// <summary>
    /// Adds a screenshot to the top of the stack.
    /// </summary>
    /// <param name="filePath">Absolute path to the cached image.</param>
    /// <param name="source">How the image reached Osoko.</param>
    /// <param name="fingerprint">
    /// Optional pre-computed SHA-256 of the file, used to drop duplicate captures that
    /// arrive through both the clipboard and the folder watcher.
    /// </param>
    /// <returns>The new item, or null when the file is missing or a duplicate.</returns>
    public ScreenshotItem? AddScreenshot(
        string filePath,
        ScreenshotSource source = ScreenshotSource.Imported,
        string? fingerprint = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(filePath);
        if (Items.Any(item => string.Equals(item.FilePath, fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        if (fingerprint is null)
        {
            try
            {
                fingerprint = ScratchStore.ComputeFingerprint(fullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"[Osoko] Could not fingerprint '{fullPath}': {ex.Message}");
            }
        }

        if (fingerprint is not null && IsDuplicateFingerprint(fingerprint))
        {
            return null;
        }

        var item = new ScreenshotItem(fullPath, source, _clock());
        PopulateImageMetadata(item);

        Items.Insert(0, item);
        if (fingerprint is not null)
        {
            RememberFingerprint(fingerprint);
        }

        PruneToLimit();

        // Pre-warm the shared cache, then fill in the payload when the background scan
        // finishes. Detection never blocks capture ingestion and starts before events so
        // a handler that immediately removes the item also clears the cache entry.
        if (LooksLikeImageFile(item.FilePath))
        {
            BarcodeDetectionService.Shared.Prewarm(item.FilePath);
            _ = DetectBarcodeForItemAsync(item, SynchronizationContext.Current);
        }

        StackChanged?.Invoke(this, new StackChangedEventArgs(StackChangeKind.Added, item));
        ScreenshotAdded?.Invoke(this, item);

        return item;
    }

    /// <summary>Removes an item from the shelf. Scratch files are deleted unless told otherwise.</summary>
    public bool RemoveScreenshot(Guid id, bool deleteFile = true)
    {
        var item = GetById(id);
        if (item is null)
        {
            return false;
        }

        BarcodeDetectionService.Shared.ClearCache(item.FilePath);
        Items.Remove(item);
        if (deleteFile)
        {
            _deleteScratchFile(item.FilePath);
        }

        StackChanged?.Invoke(this, new StackChangedEventArgs(StackChangeKind.Removed, item));
        ScreenshotRemoved?.Invoke(this, item);
        return true;
    }

    /// <summary>Flips the pinned state of an item. Pinned items survive pruning.</summary>
    public bool PinScreenshot(Guid id) => PinScreenshot(id, !(GetById(id)?.IsPinned ?? false));

    /// <summary>Sets the pinned state of an item.</summary>
    public bool PinScreenshot(Guid id, bool pinned)
    {
        var item = GetById(id);
        if (item is null)
        {
            return false;
        }

        item.IsPinned = pinned;
        StackChanged?.Invoke(this, new StackChangedEventArgs(StackChangeKind.Pinned, item));
        return true;
    }

    /// <summary>
    /// Removes items beyond <see cref="OsokoSettings.MaxStackItems"/>, oldest first.
    /// Pinned items are never pruned.
    /// </summary>
    /// <returns>The items that were removed.</returns>
    public IReadOnlyList<ScreenshotItem> PruneToLimit()
    {
        var max = Math.Max(OsokoSettings.MinStackItemCount, _settings.MaxStackItems);
        List<ScreenshotItem> pruned = [];

        for (var index = Items.Count - 1; index >= 0 && Items.Count > max; index--)
        {
            var candidate = Items[index];
            if (candidate.IsPinned)
            {
                continue;
            }

            BarcodeDetectionService.Shared.ClearCache(candidate.FilePath);
            Items.RemoveAt(index);
            _deleteScratchFile(candidate.FilePath);
            pruned.Add(candidate);
        }

        if (pruned.Count > 0)
        {
            StackChanged?.Invoke(this, new StackChangedEventArgs(StackChangeKind.Pruned, prunedItems: pruned));
        }

        return pruned;
    }

    /// <summary>Empties the shelf. Pinned items are preserved when <paramref name="keepPinned"/> is true.</summary>
    /// <returns>Number of removed items.</returns>
    public int ClearAll(bool keepPinned = false)
    {
        var removed = new List<ScreenshotItem>();
        for (var index = Items.Count - 1; index >= 0; index--)
        {
            var item = Items[index];
            if (keepPinned && item.IsPinned)
            {
                continue;
            }

            BarcodeDetectionService.Shared.ClearCache(item.FilePath);
            Items.RemoveAt(index);
            _deleteScratchFile(item.FilePath);
            removed.Add(item);
        }

        if (removed.Count > 0)
        {
            StackChanged?.Invoke(this, new StackChangedEventArgs(StackChangeKind.Cleared, prunedItems: removed));
            foreach (var item in removed)
            {
                ScreenshotRemoved?.Invoke(this, item);
            }
        }

        return removed.Count;
    }

    /// <summary>
    /// Called after a card has been dragged out of the shelf. The file stays alive for
    /// 15 minutes so the drop target can finish reading it, then the sweeper deletes it.
    /// </summary>
    public bool ConsumeDrop(Guid id, TimeSpan? retention = null)
    {
        var item = GetById(id);
        if (item is null)
        {
            return false;
        }

        _exportTracker.ScheduleDeferredCleanup(item.FilePath, retention ?? ExportTracker.DefaultRetention);
        BarcodeDetectionService.Shared.ClearCache(item.FilePath);
        Items.Remove(item);

        StackChanged?.Invoke(this, new StackChangedEventArgs(StackChangeKind.Removed, item));
        ScreenshotRemoved?.Invoke(this, item);
        return true;
    }

    // ---------------------------------------------------------------------
    // Clipboard & OCR
    // ---------------------------------------------------------------------

    /// <summary>Places the item's image on the clipboard.</summary>
    public bool CopyImage(Guid id)
    {
        var item = GetById(id);
        if (item is null || _clipboard is null)
        {
            return false;
        }

        try
        {
            _clipboard.SetImage(item.FilePath);
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or InvalidOperationException)
        {
            Debug.WriteLine($"[Osoko] Copy image failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Recognizes (or reuses cached) text, puts it on the clipboard and returns it.
    /// Returns an empty string when the image contains no text.
    /// </summary>
    public async Task<string> CopyOcrTextAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var item = GetById(id);
        if (item is null)
        {
            return string.Empty;
        }

        var text = await EnsureOcrTextAsync(item, cancellationToken).ConfigureAwait(true);
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (_clipboard is not null)
        {
            try
            {
                _clipboard.SetText(text);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                Debug.WriteLine($"[Osoko] Copy text failed: {ex.Message}");
            }
        }

        return text;
    }

    /// <summary>Returns text without copying it, recognizing it on demand.</summary>
    public async Task<string> EnsureOcrTextAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var item = GetById(id);
        return item is null ? string.Empty : await EnsureOcrTextAsync(item, cancellationToken).ConfigureAwait(true);
    }

    // ---------------------------------------------------------------------
    // Card actions
    // ---------------------------------------------------------------------

    /// <summary>Copies a 1x (half-resolution) copy of the capture to the clipboard.</summary>
    public bool CopyDownscaled(Guid id)
    {
        var item = GetById(id);
        if (item is null || _clipboard is null)
        {
            return false;
        }

        return ImageScalerService.Shared.CopyDownscaledToClipboard(item.FilePath, _clipboard);
    }

    /// <summary>
    /// Redacts sensitive data in the capture, copies the result to the clipboard and
    /// returns the number of redacted regions (0 when nothing was found).
    /// </summary>
    public async Task<int> RedactSensitiveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var item = GetById(id);
        if (item is null || _clipboard is null)
        {
            return 0;
        }

        try
        {
            // Keep the caller's context so the clipboard write stays on the UI (STA) thread.
            return await PrivacyRedactionService.Shared
                .CopyRedactedToClipboardAsync(item.FilePath, _clipboard, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Osoko] Redaction failed for '{item.FilePath}': {ex.Message}");
            return 0;
        }
    }

    /// <summary>Copies the detected QR/barcode payload to the clipboard.</summary>
    public bool CopyQrLink(Guid id)
    {
        var item = GetById(id);
        var payload = item?.DetectedBarcode?.Trim();
        if (item is null || _clipboard is null || string.IsNullOrEmpty(payload))
        {
            return false;
        }

        try
        {
            _clipboard.SetText(payload);
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            Debug.WriteLine($"[Osoko] Copy QR link failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Opens the detected QR/barcode web link in the default browser.</summary>
    public bool OpenQrLink(Guid id)
    {
        var item = GetById(id);
        var payload = item?.DetectedBarcode?.Trim();
        if (string.IsNullOrEmpty(payload))
        {
            return false;
        }

        if (!Uri.TryCreate(payload, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        return TryStartShell(payload);
    }

    /// <summary>
    /// Executes any card action and reports what happened. Operational failures are
    /// returned as a <see cref="CardActionResult"/>; cancellation is propagated.
    /// </summary>
    public async Task<CardActionResult> ExecuteActionAsync(
        CardActionKind action,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (action is CardActionKind.None)
        {
            return CardActionResult.NoOp(action);
        }

        var item = GetById(id);
        if (item is null)
        {
            return CardActionResult.Failed(action, "Screenshot not found");
        }

        try
        {
            switch (action)
            {
                case CardActionKind.CopyDownscaled:
                    return CopyDownscaled(id)
                        ? CardActionResult.Succeeded(action, "Copied 1x ✓")
                        : CardActionResult.Failed(action, "Copy 1x failed");

                case CardActionKind.CopyOriginal:
                    return CopyImage(id)
                        ? CardActionResult.Succeeded(action, "Image Copied ✓")
                        : CardActionResult.Failed(action, "Could not copy image");

                case CardActionKind.CopyOcr:
                    {
                        var text = await CopyOcrTextAsync(id, cancellationToken).ConfigureAwait(true);
                        return string.IsNullOrEmpty(text)
                            ? CardActionResult.Failed(action, "No text found")
                            : CardActionResult.Succeeded(action, "Text Copied ✓", clipboardText: text);
                    }

                case CardActionKind.RedactSensitive:
                    {
                        if (_clipboard is null)
                        {
                            return CardActionResult.Failed(action, "Clipboard is unavailable");
                        }

                        var count = await RedactSensitiveAsync(id, cancellationToken).ConfigureAwait(true);
                        return count > 0
                            ? CardActionResult.Succeeded(action, $"Redacted ({count}) ✓", matchCount: count)
                            : CardActionResult.Failed(action, "No Sensitive Data");
                    }

                case CardActionKind.CopyQrLink:
                    {
                        if (!item.HasDetectedBarcode)
                        {
                            return CardActionResult.Failed(action, "No QR Code");
                        }

                        if (_clipboard is null)
                        {
                            return CardActionResult.Failed(action, "Clipboard is unavailable");
                        }

                        return CopyQrLink(id)
                            ? CardActionResult.Succeeded(
                                action,
                                "Link Copied ✓",
                                clipboardText: item.DetectedBarcode?.Trim())
                            : CardActionResult.Failed(action, "No QR Code");
                    }

                case CardActionKind.OpenQrLink:
                    return OpenQrLink(id)
                        ? CardActionResult.Succeeded(action, "Opening Link...")
                        : CardActionResult.Failed(
                            action,
                            item.HasDetectedBarcode ? "Barcode is not a web link" : "No QR Code");

                case CardActionKind.SaveToFolder:
                    {
                        var saved = SaveToLocation(id);
                        return saved is not null
                            ? CardActionResult.Succeeded(action, "Saved ✓", savedPath: saved)
                            : CardActionResult.Failed(action, "Save failed");
                    }

                case CardActionKind.SaveAs:
                    {
                        var saved = SaveAs(id);
                        return saved is not null
                            ? CardActionResult.Succeeded(
                                action,
                                $"Saved as {Path.GetFileName(saved)} ✓",
                                savedPath: saved)
                            : CardActionResult.Failed(action, "Save cancelled");
                    }

                case CardActionKind.Pin:
                    return PinScreenshot(id)
                        ? CardActionResult.Succeeded(action, item.IsPinned ? "Pinned ✓" : "Unpinned")
                        : CardActionResult.Failed(action, "Could not pin screenshot");

                case CardActionKind.Share:
                    return TryStartShell(item.FilePath, "share")
                        ? CardActionResult.Succeeded(action, "Share sheet opened")
                        : CardActionResult.Failed(action, "Sharing is unavailable");

                case CardActionKind.OpenInPreview:
                    return TryStartShell(item.FilePath)
                        ? CardActionResult.Succeeded(action, "Opened in default app")
                        : CardActionResult.Failed(action, "Could not open image");

                default:
                    return CardActionResult.Failed(action, "Unknown action");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Osoko] Card action '{action}' failed: {ex.Message}");
            return CardActionResult.Failed(action, "Something went wrong");
        }
    }

    // ---------------------------------------------------------------------
    // Saving
    // ---------------------------------------------------------------------

    /// <summary>One-click save: honours <see cref="OsokoSettings.PromptToRenameOnSave"/>.</summary>
    public string? Save(Guid id) =>
        _settings.PromptToRenameOnSave && _fileDialogs is not null ? SaveAs(id) : SaveToLocation(id);

    /// <summary>Shows a "Save as" dialog and copies the image there.</summary>
    public string? SaveAs(Guid id)
    {
        var item = GetById(id);
        if (item is null || _fileDialogs is null)
        {
            return null;
        }

        var suggested = BuildSuggestedFileName(item);
        var directory = GetSaveDirectory();
        var destination = _fileDialogs.PickSavePath(suggested, directory);
        if (string.IsNullOrWhiteSpace(destination))
        {
            return null;
        }

        return ExportTo(item, destination);
    }

    /// <summary>Copies the image into the configured save folder without prompting.</summary>
    public string? SaveToLocation(Guid id)
    {
        var item = GetById(id);
        if (item is null)
        {
            return null;
        }

        var directory = GetSaveDirectory();
        var destination = EnsureUniquePath(directory, BuildSuggestedFileName(item));
        return ExportTo(item, destination);
    }

    // ---------------------------------------------------------------------
    // Queries
    // ---------------------------------------------------------------------

    public ScreenshotItem? GetById(Guid id) => Items.FirstOrDefault(item => item.Id == id);

    public ScreenshotItem? GetByPath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        return Items.FirstOrDefault(item =>
            string.Equals(item.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when an identical image was ingested recently (clipboard + folder dedupe).</summary>
    public bool IsDuplicateFingerprint(string fingerprint)
    {
        lock (_gate)
        {
            PruneFingerprints();
            return _fingerprints.ContainsKey(fingerprint);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StackChanged = null;
        ScreenshotAdded = null;
        ScreenshotRemoved = null;
        ScreenshotSaved = null;
    }

    // ---------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------

    private async Task<string> EnsureOcrTextAsync(ScreenshotItem item, CancellationToken cancellationToken)
    {
        if (item.OcrText is not null)
        {
            return item.OcrText;
        }

        if (_ocr is null)
        {
            return string.Empty;
        }

        item.IsRecognizing = true;
        try
        {
            var recognized = await _ocr.RecognizeTextAsync(item.FilePath, cancellationToken).ConfigureAwait(true);
            item.OcrText = recognized ?? string.Empty;
            return item.OcrText;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Osoko] OCR failed for '{item.FilePath}': {ex.Message}");
            return string.Empty;
        }
        finally
        {
            item.IsRecognizing = false;
        }
    }

    private string? ExportTo(ScreenshotItem item, string destination)
    {
        try
        {
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Copy(item.FilePath, destination, overwrite: true);
            _exportTracker.RegisterExported(destination);
            ScreenshotSaved?.Invoke(this, item);
            return destination;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Debug.WriteLine($"[Osoko] Save failed for '{destination}': {ex.Message}");
            return null;
        }
    }

    private string GetSaveDirectory()
    {
        var directory = _settings.SaveLocationPath;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            directory = AppPaths.DesktopDirectory;
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = AppPaths.ScratchDirectory;
            }
        }

        return directory;
    }

    internal static string BuildSuggestedFileName(ScreenshotItem item)
    {
        var stamp = item.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
        return $"Osoko {stamp}.png";
    }

    /// <summary>Returns a non-existing path inside <paramref name="directory"/>.</summary>
    internal static string EnsureUniquePath(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);

        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; index < 10_000; index++)
        {
            candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{stem} {Guid.NewGuid():N}{extension}");
    }

    private static bool LooksLikeImageFile(string path)
    {
        Span<byte> header = stackalloc byte[12];

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var read = stream.Read(header);
            if (read >= 4)
            {
                if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
                {
                    return true; // PNG
                }

                if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
                {
                    return true; // JPEG
                }

                if (header[0] == (byte)'G' && header[1] == (byte)'I' &&
                    header[2] == (byte)'F' && header[3] == (byte)'8')
                {
                    return true; // GIF
                }

                if (header[0] == (byte)'B' && header[1] == (byte)'M')
                {
                    return true; // BMP
                }

                if ((header[0] == (byte)'I' && header[1] == (byte)'I' &&
                     header[2] == 0x2A && header[3] == 0x00) ||
                    (header[0] == (byte)'M' && header[1] == (byte)'M' &&
                     header[2] == 0x00 && header[3] == 0x2A))
                {
                    return true; // TIFF
                }

                if (header[0] == 0x00 && header[1] == 0x00 &&
                    header[2] == 0x01 && header[3] == 0x00)
                {
                    return true; // ICO
                }

                if (header[0] == (byte)'I' && header[1] == (byte)'I' && header[2] == 0xBC)
                {
                    return true; // JPEG XR
                }
            }

            return read >= 12 &&
                   header[0] == (byte)'R' && header[1] == (byte)'I' &&
                   header[2] == (byte)'F' && header[3] == (byte)'F' &&
                   header[8] == (byte)'W' && header[9] == (byte)'E' &&
                   header[10] == (byte)'B' && header[11] == (byte)'P';
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            NotSupportedException or ArgumentException or System.Security.SecurityException)
        {
            Debug.WriteLine($"[Osoko] Could not inspect '{path}': {ex.Message}");
            return false;
        }
    }

    private async Task DetectBarcodeForItemAsync(
        ScreenshotItem item,
        SynchronizationContext? synchronizationContext)
    {
        try
        {
            var payload = await BarcodeDetectionService.Shared
                .DetectBarcodeAsync(item.FilePath)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(payload) || _disposed)
            {
                return;
            }

            ApplyDetectedBarcode(item, payload, synchronizationContext);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"[Osoko] Barcode detection failed for '{item.FilePath}': {ex.Message}");
        }
    }

    private static void ApplyDetectedBarcode(
        ScreenshotItem item,
        string payload,
        SynchronizationContext? synchronizationContext)
    {
        // PropertyChanged must be raised on the UI thread so bindings update safely.
        if (synchronizationContext is not null &&
            SynchronizationContext.Current != synchronizationContext)
        {
            synchronizationContext.Post(_ => item.DetectedBarcode = payload, null);
            return;
        }

        item.DetectedBarcode = payload;
    }

    private static bool TryStartShell(string fileName, string? verb = null)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = true,
            };

            if (!string.IsNullOrEmpty(verb))
            {
                startInfo.Verb = verb;
            }

            using var process = Process.Start(startInfo);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or
            ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or
            System.Security.SecurityException)
        {
            Debug.WriteLine($"[Osoko] Could not open '{fileName}': {ex.Message}");
            return false;
        }
    }

    private void PopulateImageMetadata(ScreenshotItem item)
    {
        var size = _imageLoader.ReadPixelSize(item.FilePath);
        if (size is { } dimensions && dimensions.Width > 0 && dimensions.Height > 0)
        {
            item.PixelWidth = dimensions.Width;
            item.PixelHeight = dimensions.Height;
        }

        item.Thumbnail = _imageLoader.LoadThumbnail(item.FilePath, ThumbnailDecodeWidth);
    }

    private void RememberFingerprint(string fingerprint)
    {
        lock (_gate)
        {
            PruneFingerprints();
            _fingerprints[fingerprint] = _clock();
        }
    }

    private void PruneFingerprints()
    {
        if (_fingerprints.Count == 0)
        {
            return;
        }

        var cutoff = _clock() - FingerprintWindow;
        foreach (var key in _fingerprints.Where(pair => pair.Value < cutoff).Select(pair => pair.Key).ToArray())
        {
            _fingerprints.Remove(key);
        }
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static void DeleteScratchFileDefault(string path)
    {
        if (!AppPaths.IsScratchFile(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Osoko] Could not delete '{path}': {ex.Message}");
        }
    }
}
