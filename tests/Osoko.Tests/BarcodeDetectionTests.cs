using System.Windows.Media;
using System.Windows.Media.Imaging;
using Osoko.Core.Services;
using ZXing;
using ZXing.QrCode;

namespace Osoko.Tests;

/// <summary>
/// ZXing decoding is covered end-to-end with a real QR image, plus cache invalidation
/// and graceful handling of missing files.
/// </summary>
public sealed class BarcodeDetectionTests : IDisposable
{
    private const string Payload = "https://osoko.app/test";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "Osoko.Tests",
        "barcode",
        Guid.NewGuid().ToString("N"));

    public BarcodeDetectionTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temp cleanup is best effort.
        }
    }

    [Fact]
    public void SupportedFormats_Contain_All_Requested_Symbologies()
    {
        var service = new BarcodeDetectionService();

        Assert.Contains(BarcodeFormat.QR_CODE, service.SupportedFormats);
        Assert.Contains(BarcodeFormat.AZTEC, service.SupportedFormats);
        Assert.Contains(BarcodeFormat.DATA_MATRIX, service.SupportedFormats);
        Assert.Contains(BarcodeFormat.CODE_128, service.SupportedFormats);
        Assert.Contains(BarcodeFormat.EAN_13, service.SupportedFormats);
    }

    [Fact]
    public async Task DetectBarcodeAsync_Decodes_Qr_From_File_And_Stream()
    {
        var path = CreateQrCodeFile();
        var service = new BarcodeDetectionService();

        var fromFile = await service.DetectBarcodeAsync(path);

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var fromStream = await service.DetectBarcodeFromStreamAsync(stream);
            Assert.Equal(Payload, fromStream);
        }

        Assert.Equal(Payload, fromFile);
    }

    [Fact]
    public async Task Cache_Is_Reused_Until_Cleared()
    {
        var path = CreateQrCodeFile();
        var service = new BarcodeDetectionService();

        Assert.Equal(Payload, await service.DetectBarcodeAsync(path));
        Assert.Equal(1, service.CachedCount);

        // A successful result survives the source being deleted, matching the upstream
        // cache behavior.
        File.Delete(path);
        Assert.Equal(Payload, await service.DetectBarcodeAsync(path));
        Assert.Equal(1, service.CachedCount);

        service.ClearCache(path);
        Assert.Equal(0, service.CachedCount);
        Assert.Null(await service.DetectBarcodeAsync(path));
    }

    [Fact]
    public async Task DetectBarcodeAsync_Returns_Null_For_Missing_File()
    {
        var service = new BarcodeDetectionService();
        var path = Path.Combine(_directory, $"missing-{Guid.NewGuid():N}.png");

        Assert.Null(await service.DetectBarcodeAsync(path));
    }

    private string CreateQrCodeFile()
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new QrCodeEncodingOptions
            {
                Width = 320,
                Height = 320,
                Margin = 2,
            },
        };

        var pixels = writer.Write(Payload);
        var bitmap = BitmapSource.Create(
            pixels.Width,
            pixels.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels.Pixels,
            pixels.Width * 4);

        bitmap.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        var path = Path.Combine(_directory, $"qr-{Guid.NewGuid():N}.png");
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }
}
