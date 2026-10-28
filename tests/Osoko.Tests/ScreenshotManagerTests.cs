using System.Windows.Media;
using System.Windows.Media.Imaging;
using Osoko.Core;
using Osoko.Core.Services;

namespace Osoko.Tests;

/// <summary>
/// The stack manager is the piece users feel the most: ordering, pruning, pinning and
/// clearing all have to be exact, and pinned captures must never be silently discarded.
/// </summary>
public sealed class ScreenshotManagerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "Osoko.Tests",
        "stack",
        Guid.NewGuid().ToString("N"));

    private readonly string _saveDirectory;
    private readonly OsokoSettings _settings;
    private readonly ExportTracker _tracker;
    private readonly ScreenshotManager _manager;
    private readonly List<string> _deleted = [];
    private int _counter;

    public ScreenshotManagerTests()
    {
        Directory.CreateDirectory(_directory);
        _saveDirectory = Path.Combine(_directory, "exports");
        Directory.CreateDirectory(_saveDirectory);

        _settings = OsokoSettings.CreateDefault();
        _settings.MaxStackItems = 5;
        _settings.SaveLocationPath = _saveDirectory;

        _tracker = new ExportTracker(enableBackgroundSweep: false);

        _manager = new ScreenshotManager(
            _settings,
            _tracker,
            new FakeImageLoader(),
            deleteScratchFile: path =>
            {
                _deleted.Add(path);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            });
    }

    public void Dispose()
    {
        _manager.Dispose();
        _tracker.Dispose();

        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public void AddScreenshot_Inserts_Newest_First()
    {
        var first = _manager.AddScreenshot(CreateFile(), ScreenshotSource.SnippingTool);
        var second = _manager.AddScreenshot(CreateFile(), ScreenshotSource.Clipboard);
        var third = _manager.AddScreenshot(CreateFile(), ScreenshotSource.ScreenshotsFolder);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(third);
        Assert.Equal(3, _manager.Count);
        Assert.True(_manager.HasItems);
        Assert.Same(third, _manager.Items[0]);
        Assert.Same(second, _manager.Items[1]);
        Assert.Same(first, _manager.Items[2]);
        Assert.NotEqual(first!.Id, second!.Id);
        Assert.Equal("640 × 480", third!.DimensionsLabel);
        Assert.Equal(640, third.PixelWidth);
        Assert.Equal(480, third.PixelHeight);
        Assert.Null(third.Thumbnail);
        Assert.False(third.HasThumbnail);
        Assert.Equal(ScreenshotSource.ScreenshotsFolder, third.Source);
    }

    [Fact]
    public void AddScreenshot_Rejects_Missing_Files_And_Empty_Paths()
    {
        Assert.Null(_manager.AddScreenshot(Path.Combine(_directory, "nope.png")));
        Assert.Null(_manager.AddScreenshot("   "));
        Assert.Equal(0, _manager.Count);
    }

    [Fact]
    public void AddScreenshot_Ignores_The_Same_Path_Twice()
    {
        var path = CreateFile();

        Assert.NotNull(_manager.AddScreenshot(path));
        Assert.Null(_manager.AddScreenshot(path));
        Assert.Equal(1, _manager.Count);
    }

    [Fact]
    public void AddScreenshot_Deduplicates_By_Fingerprint()
    {
        var first = CreateFile();
        var second = CreateFile();

        Assert.NotNull(_manager.AddScreenshot(first, fingerprint: "fingerprint-1"));
        Assert.Null(_manager.AddScreenshot(second, fingerprint: "fingerprint-1"));
        Assert.Equal(1, _manager.Count);
    }

    [Fact]
    public void AddScreenshot_Raises_Added_Events()
    {
        StackChangedEventArgs? change = null;
        ScreenshotItem? added = null;
        _manager.StackChanged += (_, e) => change = e;
        _manager.ScreenshotAdded += (_, item) => added = item;

        var item = _manager.AddScreenshot(CreateFile());

        Assert.NotNull(change);
        Assert.Equal(StackChangeKind.Added, change!.Kind);
        Assert.Same(item, change.Item);
        Assert.Same(item, added);
    }

    [Fact]
    public void PruneToLimit_Removes_Oldest_Unpinned_Items()
    {
        var files = Enumerable.Range(0, 5).Select(_ => CreateFile()).ToArray();
        var items = files.Select(path => _manager.AddScreenshot(path)!).ToArray();

        _settings.MaxStackItems = 3;
        var pruned = _manager.PruneToLimit();

        Assert.Equal(2, pruned.Count);
        Assert.Equal(3, _manager.Count);
        Assert.Equal(2, _deleted.Count);
        Assert.Contains(files[0], _deleted);
        Assert.Contains(files[1], _deleted);
        Assert.Same(items[4], _manager.Items[0]);
        Assert.Same(items[2], _manager.Items[^1]);
    }

    [Fact]
    public void PruneToLimit_Never_Removes_Pinned_Items()
    {
        var oldest = CreateFile();
        var second = CreateFile();
        var third = CreateFile();
        var newest = CreateFile();

        var oldestItem = _manager.AddScreenshot(oldest)!;
        _manager.AddScreenshot(second);
        _manager.AddScreenshot(third);
        _manager.AddScreenshot(newest);

        Assert.True(_manager.PinScreenshot(oldestItem.Id));
        _settings.MaxStackItems = 2;

        var pruned = _manager.PruneToLimit();

        Assert.Equal(2, _manager.Count);
        Assert.Contains(_manager.Items, item => item.Id == oldestItem.Id);
        Assert.True(oldestItem.IsPinned);
        Assert.DoesNotContain(_manager.Items, item => item.FilePath == second);
        Assert.DoesNotContain(_manager.Items, item => item.FilePath == third);
        Assert.Equal(2, pruned.Count);
        Assert.DoesNotContain(oldest, _deleted);
    }

    [Fact]
    public void PruneToLimit_Keeps_Everything_When_All_Items_Are_Pinned()
    {
        var items = Enumerable.Range(0, 4).Select(_ => _manager.AddScreenshot(CreateFile())!).ToArray();
        foreach (var item in items)
        {
            _manager.PinScreenshot(item.Id);
        }

        _settings.MaxStackItems = 2;
        var pruned = _manager.PruneToLimit();

        Assert.Empty(pruned);
        Assert.Equal(4, _manager.Count);
        Assert.Empty(_deleted);
    }

    [Fact]
    public void RemoveScreenshot_Deletes_The_File_And_Raises_Removed()
    {
        var path = CreateFile();
        var item = _manager.AddScreenshot(path)!;
        ScreenshotItem? removed = null;
        _manager.ScreenshotRemoved += (_, candidate) => removed = candidate;

        Assert.True(_manager.RemoveScreenshot(item.Id));

        Assert.Equal(0, _manager.Count);
        Assert.Same(item, removed);
        Assert.Contains(path, _deleted);
        Assert.False(File.Exists(path));
        Assert.False(_manager.RemoveScreenshot(item.Id));
    }

    [Fact]
    public void RemoveScreenshot_Can_Keep_The_File()
    {
        var path = CreateFile();
        var item = _manager.AddScreenshot(path)!;

        Assert.True(_manager.RemoveScreenshot(item.Id, deleteFile: false));

        Assert.Empty(_deleted);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void ClearAll_Removes_Every_Item()
    {
        Enumerable.Range(0, 3).Select(_ => _manager.AddScreenshot(CreateFile())).ToArray();
        var cleared = 0;
        _manager.StackChanged += (_, e) =>
        {
            if (e.Kind == StackChangeKind.Cleared)
            {
                cleared++;
            }
        };

        var removed = _manager.ClearAll();

        Assert.Equal(3, removed);
        Assert.Equal(0, _manager.Count);
        Assert.False(_manager.HasItems);
        Assert.Equal(1, cleared);
        Assert.Equal(3, _deleted.Count);
    }

    [Fact]
    public void ClearAll_Can_Keep_Pinned_Items()
    {
        var pinnedPath = CreateFile();
        var pinned = _manager.AddScreenshot(pinnedPath)!;
        _manager.AddScreenshot(CreateFile());
        _manager.PinScreenshot(pinned.Id);

        var removed = _manager.ClearAll(keepPinned: true);

        Assert.Equal(1, removed);
        Assert.Equal(1, _manager.Count);
        Assert.Same(pinned, _manager.Items[0]);
        Assert.DoesNotContain(pinnedPath, _deleted);
    }

    [Fact]
    public void PinScreenshot_Toggles_And_Reports_Changes()
    {
        var item = _manager.AddScreenshot(CreateFile())!;
        var changes = new List<StackChangeKind>();
        _manager.StackChanged += (_, e) => changes.Add(e.Kind);

        Assert.False(item.IsPinned);
        Assert.True(_manager.PinScreenshot(item.Id));
        Assert.True(item.IsPinned);
        Assert.Equal("\uE77A", item.PinGlyph);
        Assert.True(_manager.PinScreenshot(item.Id));
        Assert.False(item.IsPinned);
        Assert.Equal("\uE718", item.PinGlyph);
        Assert.Contains(StackChangeKind.Pinned, changes);
        Assert.False(_manager.PinScreenshot(Guid.NewGuid()));
    }

    [Fact]
    public void ConsumeDrop_Removes_Item_But_Keeps_File_For_Deferred_Cleanup()
    {
        var path = CreateFile();
        var item = _manager.AddScreenshot(path)!;

        Assert.True(_manager.ConsumeDrop(item.Id));

        Assert.Equal(0, _manager.Count);
        Assert.Equal(1, _tracker.PendingCleanupCount);
        Assert.True(File.Exists(path));
        Assert.DoesNotContain(path, _deleted);
    }

    [Fact]
    public void SaveToLocation_Copies_File_And_Registers_The_Export()
    {
        var item = _manager.AddScreenshot(CreateFile())!;

        var saved = _manager.SaveToLocation(item.Id);

        Assert.NotNull(saved);
        Assert.True(File.Exists(saved));
        Assert.StartsWith(_saveDirectory, saved);
        Assert.True(_tracker.IsExported(saved));
    }

    [Fact]
    public void SaveToLocation_Never_Overwrites_An_Existing_File()
    {
        var item = _manager.AddScreenshot(CreateFile())!;

        var first = _manager.SaveToLocation(item.Id);
        var second = _manager.SaveToLocation(item.Id);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public void CopyImage_Returns_False_Without_A_Clipboard_Service()
    {
        var item = _manager.AddScreenshot(CreateFile())!;

        Assert.False(_manager.CopyImage(item.Id));
        Assert.False(_manager.CopyImage(Guid.NewGuid()));
    }

    [Fact]
    public async Task CopyOcrTextAsync_Returns_Empty_Without_An_Ocr_Service()
    {
        var item = _manager.AddScreenshot(CreateFile())!;

        var text = await _manager.CopyOcrTextAsync(item.Id);

        Assert.Equal(string.Empty, text);
        Assert.Null(item.OcrText);
        Assert.False(item.IsRecognizing);
    }

    [Fact]
    public void DetectedBarcodeLabel_Uses_Host_For_Urls_And_Notifies()
    {
        var item = new ScreenshotItem(Path.Combine(_directory, "barcode.png"));
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        item.DetectedBarcode = " https://osoko.app/test ";

        Assert.True(item.HasDetectedBarcode);
        Assert.Equal("https://osoko.app/test", item.DetectedBarcode);
        Assert.Equal("osoko.app", item.DetectedBarcodeLabel);
        Assert.Contains(nameof(ScreenshotItem.DetectedBarcode), changed);
        Assert.Contains(nameof(ScreenshotItem.HasDetectedBarcode), changed);
        Assert.Contains(nameof(ScreenshotItem.DetectedBarcodeLabel), changed);
    }

    [Fact]
    public void DetectedBarcodeLabel_Truncates_Long_NonUrl_Payloads()
    {
        var payload = new string('x', 80);
        var item = new ScreenshotItem(Path.Combine(_directory, "barcode.png"))
        {
            DetectedBarcode = payload,
        };

        Assert.True(item.HasDetectedBarcode);
        Assert.Equal(48, item.DetectedBarcodeLabel.Length);
        Assert.EndsWith("…", item.DetectedBarcodeLabel);
        Assert.Equal(new string('x', 47) + "…", item.DetectedBarcodeLabel);
    }

    [Fact]
    public void DetectedBarcodeLabel_Is_Empty_When_No_Barcode_Exists()
    {
        var item = new ScreenshotItem(Path.Combine(_directory, "barcode.png"));

        Assert.False(item.HasDetectedBarcode);
        Assert.Null(item.DetectedBarcode);
        Assert.Equal(string.Empty, item.DetectedBarcodeLabel);
    }

    [Fact]
    public void CopyDownscaled_Uses_The_Clipboard_With_Half_Size_Image()
    {
        var path = CreatePngFile(100, 100);
        var clipboard = new FakeClipboardService();
        using var manager = CreateManager(clipboard);
        var item = manager.AddScreenshot(path);

        Assert.NotNull(item);
        Assert.True(manager.CopyDownscaled(item!.Id));
        Assert.NotNull(clipboard.LastImage);
        Assert.Equal(50, clipboard.LastImage!.PixelWidth);
        Assert.Equal(50, clipboard.LastImage.PixelHeight);
        Assert.True(clipboard.LastImage.IsFrozen);
    }

    [Fact]
    public void CopyDownscaled_Returns_False_Without_A_Clipboard_Service()
    {
        var item = _manager.AddScreenshot(CreateFile())!;

        Assert.False(_manager.CopyDownscaled(item.Id));
    }

    [Fact]
    public async Task ExecuteActionAsync_Pin_Toggles_And_Reports_Status()
    {
        var item = _manager.AddScreenshot(CreateFile())!;

        var pinned = await _manager.ExecuteActionAsync(CardActionKind.Pin, item.Id);
        Assert.True(pinned.Success);
        Assert.Equal(CardActionKind.Pin, pinned.Action);
        Assert.True(item.IsPinned);
        Assert.Equal("Pinned ✓", pinned.Message);

        var unpinned = await _manager.ExecuteActionAsync(CardActionKind.Pin, item.Id);
        Assert.True(unpinned.Success);
        Assert.False(item.IsPinned);
        Assert.Equal("Unpinned", unpinned.Message);
    }

    [Fact]
    public async Task ExecuteActionAsync_None_Is_A_NoOp()
    {
        var item = _manager.AddScreenshot(CreateFile())!;

        var result = await _manager.ExecuteActionAsync(CardActionKind.None, item.Id);

        Assert.True(result.Success);
        Assert.Equal(CardActionKind.None, result.Action);
        Assert.Equal("No action", result.Message);
        Assert.Equal(1, _manager.Count);
    }

    [Fact]
    public async Task ExecuteActionAsync_CopyOriginal_Uses_Clipboard_Service()
    {
        var clipboard = new FakeClipboardService();
        using var manager = CreateManager(clipboard);
        var path = CreateFile();
        var item = manager.AddScreenshot(path);

        Assert.NotNull(item);
        var result = await manager.ExecuteActionAsync(CardActionKind.CopyOriginal, item!.Id);

        Assert.True(result.Success);
        Assert.Equal(CardActionKind.CopyOriginal, result.Action);
        Assert.Equal(path, clipboard.LastImagePath);
        Assert.Contains("Copied", result.Message);
    }

    [Fact]
    public void GetByPath_Is_Case_Insensitive()
    {
        var path = CreateFile();
        var item = _manager.AddScreenshot(path)!;

        Assert.Same(item, _manager.GetByPath(path.ToUpperInvariant()));
        Assert.Same(item, _manager.GetById(item.Id));
        Assert.Null(_manager.GetByPath(null));
    }

    private ScreenshotManager CreateManager(IClipboardService clipboard) =>
        new(
            _settings,
            _tracker,
            new FakeImageLoader(),
            clipboard: clipboard,
            deleteScratchFile: path =>
            {
                _deleted.Add(path);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            });

    private string CreatePngFile(int width, int height)
    {
        var path = Path.Combine(_directory, $"image-{++_counter:D3}.png");
        var stride = width * 4;
        var pixels = new byte[stride * height];

        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 0x30;
            pixels[index + 1] = 0x80;
            pixels[index + 2] = 0xC0;
            pixels[index + 3] = 0xFF;
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private string CreateFile()
    {
        var path = Path.Combine(_directory, $"capture-{++_counter:D3}.png");
        File.WriteAllText(path, $"fake image content {_counter}");
        return path;
    }

    private sealed class FakeClipboardService : IClipboardService
    {
        public string? LastImagePath { get; private set; }

        public BitmapSource? LastImage { get; private set; }

        public string? LastText { get; private set; }

        public void SetImage(string filePath) => LastImagePath = filePath;

        public void SetImage(BitmapSource image) => LastImage = image;

        public void SetText(string text) => LastText = text;
    }

    private sealed class FakeImageLoader : IImageLoader
    {
        public BitmapSource? LoadThumbnail(string filePath, int decodePixelWidth) => null;

        public (int Width, int Height)? ReadPixelSize(string filePath) => (640, 480);
    }
}
