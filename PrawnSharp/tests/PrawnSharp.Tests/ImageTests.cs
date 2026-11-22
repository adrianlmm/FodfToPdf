using Xunit;
using Pig = UglyToad.PdfPig;

namespace PrawnSharp.Tests;

public sealed class ImageTests
{
    private static string ImagePath(string name) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "images", name);

    [Fact]
    public void ReadsPngAndJpegMetadata()
    {
        var png = ImageData.FromFile(ImagePath("arrow.png"));
        var jpg = ImageData.FromFile(ImagePath("fractal.jpg"));

        Assert.Equal(ImageFormat.Png, png.Format);
        Assert.Equal(42, png.PixelWidth);
        Assert.Equal(23, png.PixelHeight);
        Assert.Equal(42, png.WidthAt(72));
        Assert.Equal(ImageFormat.Jpeg, jpg.Format);
        Assert.Equal(240, jpg.PixelWidth);
        Assert.Equal(240, jpg.PixelHeight);
    }

    [Fact]
    public void PlacesPngJpegAlphaIndexedAnd16Bit()
    {
        using var doc = new PrawnDocument(new DocumentOptions
        {
            PageSize = new Size(420, 420),
            Margin = new Thickness(10),
        });

        doc.Image(ImageData.FromFile(ImagePath("arrow.png")), 10, 10, 120, 66);
        doc.Image(ImageData.FromFile(ImagePath("dice.png")), 150, 10, 120, 90);
        doc.Image(ImageData.FromFile(ImagePath("indexed_transparency.png")), 290, 10, 100, 100);
        doc.Image(ImageData.FromFile(ImagePath("16bit.png")), 10, 130, 80, 80);
        doc.Image(ImageData.FromFile(ImagePath("fractal.jpg")), 110, 130, 120, 120);
        doc.Image(ImageData.FromFile(ImagePath("pigs.jpg")), 250, 130, 140, 100);

        using var pdf = Pig.PdfDocument.Open(new MemoryStream(doc.RenderBytes()));

        Assert.Equal(1, pdf.NumberOfPages);
        Assert.Equal(420, pdf.GetPage(1).Width);
    }

    [Fact]
    public void KeepsJpegSizeReasonablyClose()
    {
        var jpeg = ImageData.FromFile(ImagePath("pigs.jpg"));

        using var doc = new PrawnDocument(new DocumentOptions
        {
            PageSize = new Size(300, 300),
            Margin = new Thickness(10),
        });

        doc.Image(jpeg, 10, 10, 200, 200);
        var pdf = doc.RenderBytes();

        // El JPEG se conserva; el PDF sólo añade estructura y el objeto de imagen.
        Assert.True(
            pdf.Length < jpeg.Data.Length + 40_000,
            $"PDF {pdf.Length} vs JPEG {jpeg.Data.Length}; posible recompresión.");
    }

    [Fact]
    public void FitsImageAndAppliesDpi()
    {
        using var doc = new PrawnDocument(new DocumentOptions
        {
            PageSize = new Size(300, 300),
            Margin = new Thickness(10),
        });

        var image = ImageData.FromFile(ImagePath("arrow.png"));

        doc.Image(image, new ImageOptions
        {
            AtX = 10,
            AtY = 10,
            Width = 120,
            Height = 120,
            Fit = true,
        });

        doc.ImageAtDpi(image, 10, 180, 144);

        using var pdf = Pig.PdfDocument.Open(new MemoryStream(doc.RenderBytes()));

        Assert.Equal(1, pdf.NumberOfPages);
        Assert.Equal(36, PrawnDocument.PixelsToPoints(72, 144), 3);
    }
}
