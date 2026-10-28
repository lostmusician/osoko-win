using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Osoko.Core.Services;

namespace Osoko.Tests;

/// <summary>
/// The 1x downscaler is used by a primary card action, so its pixel math and output
/// format are part of the user-visible contract.
/// </summary>
public sealed class ImageScalerTests
{
    [Theory]
    [InlineData(100, 100, 50, 50)]
    [InlineData(101, 61, 50, 30)]
    [InlineData(1, 1, 1, 1)]
    [InlineData(200, 150, 100, 75)]
    public void DownscaleTo1x_Halves_Each_Dimension(int sourceWidth, int sourceHeight, int expectedWidth, int expectedHeight)
    {
        var service = new ImageScalerService();
        var source = CreateBitmap(sourceWidth, sourceHeight);

        var scaled = service.DownscaleTo1x(source);

        Assert.Equal(expectedWidth, scaled.PixelWidth);
        Assert.Equal(expectedHeight, scaled.PixelHeight);
        Assert.True(scaled.IsFrozen);
    }

    [Fact]
    public void DownscaleTo1x_Throws_For_Null_Source()
    {
        var service = new ImageScalerService();

        Assert.Throws<ArgumentNullException>(() => service.DownscaleTo1x((BitmapSource)null!));
    }

    [Fact]
    public void DownscaleTo1x_Throws_For_Zero_Dimension_Source()
    {
        var service = new ImageScalerService();

        Assert.Throws<ArgumentException>(() => service.DownscaleTo1x(new ZeroDimensionBitmapSource()));
    }

    [Fact]
    public void EncodePng_Produces_A_Png_Signature()
    {
        var service = new ImageScalerService();

        var png = Assert.IsType<byte[]>(service.EncodePng(CreateBitmap(20, 10)));

        Assert.True(png.Length >= 8);
        Assert.Equal((byte)0x89, png[0]);
        Assert.Equal((byte)'P', png[1]);
        Assert.Equal((byte)'N', png[2]);
        Assert.Equal((byte)'G', png[3]);
        Assert.Equal((byte)0x0D, png[4]);
        Assert.Equal((byte)0x0A, png[5]);
        Assert.Equal((byte)0x1A, png[6]);
        Assert.Equal((byte)0x0A, png[7]);
    }

    [Fact]
    public void DownscaleTo1xImage_Returns_Null_For_Missing_File()
    {
        var service = new ImageScalerService();
        var path = Path.Combine(Path.GetTempPath(), $"osoko-missing-{Guid.NewGuid():N}.png");

        Assert.False(File.Exists(path));
        Assert.Null(service.DownscaleTo1xImage(path));
    }

    private static BitmapSource CreateBitmap(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];

        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 0x40;     // B
            pixels[index + 1] = 0x80; // G
            pixels[index + 2] = 0xC0; // R
            pixels[index + 3] = 0xFF; // A
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);

        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// A BitmapSource that reports no pixels without touching WIC. Real WPF factories
    /// reject zero dimensions, so this small stub is used to exercise the guard.
    /// </summary>
    private sealed class ZeroDimensionBitmapSource : BitmapSource
    {
        public override int PixelWidth => 0;

        public override int PixelHeight => 0;

        protected override Freezable CreateInstanceCore() => new ZeroDimensionBitmapSource();
    }
}
