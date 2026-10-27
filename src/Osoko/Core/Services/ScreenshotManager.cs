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
