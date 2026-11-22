using System.Linq;
using PrawnSharp;
using Xunit;
using Pig = UglyToad.PdfPig;

namespace PrawnSharp.Tests;

public sealed class TtfTests
{
    private static string FontPath(string name) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "fonts", name);

    [Fact]
    public void MeasuresTtfFont()
    {
        using var doc = new PrawnDocument();
        doc.RegisterFontFamily("DejaVu", FontPath("DejaVuSans.ttf"));
        doc.Font("DejaVu", 12);

        // Prawn: width_of("Hola mundo", 12) = 72.636, font.height = 14.388
        Assert.InRange(doc.MeasureText("Hola mundo"), 72.0, 73.0);
        Assert.InRange(doc.HeightOf("Hola mundo"), 14.38, 14.40);
    }

    [Fact]
    public void AppliesTtfKerning()
    {
        using var doc = new PrawnDocument();
        doc.RegisterFontFamily("DejaVu", FontPath("DejaVuSans.ttf"));
        doc.Font("DejaVu", 12);

        var without = doc.MeasureText("AV", new TextOptions { Kerning = false });
        var with = doc.MeasureText("AV", new TextOptions { Kerning = true });

        // Prawn: 16.416 sin kerning, 15.648421875 con kerning.
        Assert.InRange(without - with, 0.74, 0.80);
    }

    [Fact]
    public void TtfBaselineMatchesPrawn()
    {
        using var doc = new PrawnDocument(new DocumentOptions
        {
            PageSize = PageSizes.A4,
            Margin = new Thickness(32),
        });

        doc.RegisterFontFamily("DejaVu", FontPath("DejaVuSans.ttf"));
        doc.Font("DejaVu", 12);
        doc.Text("Hola");

        using var pdf = Pig.PdfDocument.Open(new MemoryStream(doc.RenderBytes()));
        var word = pdf.GetPage(1).GetWords().First(w => w.Text == "Hola");

        // Prawn: baseline 41,108 desde arriba en A4 -> 800,782 desde abajo.
        Assert.InRange(word.BoundingBox.Bottom, 800.70, 800.90);
    }

    [Fact]
    public void WrapsTtfLikePrawn()
    {
        const string paragraph =
            "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor " +
            "incididunt ut labore et dolore magna aliqua.";

        using var doc = new PrawnDocument(new DocumentOptions
        {
            PageSize = new Size(300, 500),
            Margin = new Thickness(10),
        });

        doc.RegisterFontFamily("DejaVu", FontPath("DejaVuSans.ttf"));
        doc.Font("DejaVu", 12);
        doc.Text(paragraph, new TextOptions { Width = 150 });

        using var pdf = Pig.PdfDocument.Open(new MemoryStream(doc.RenderBytes()));
        var lines = pdf.GetPage(1).GetWords()
            .GroupBy(w => Math.Round(w.BoundingBox.Bottom, 1))
            .OrderByDescending(g => g.Key)
            .Select(g => string.Join(' ', g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)))
            .ToList();

        Assert.Collection(
            lines,
            l => Assert.Equal("Lorem ipsum dolor sit", l),
            l => Assert.Equal("amet, consectetur", l),
            l => Assert.Equal("adipiscing elit, sed do", l),
            l => Assert.Equal("eiusmod tempor", l),
            l => Assert.Equal("incididunt ut labore et", l),
            l => Assert.Equal("dolore magna aliqua.", l));
    }

    [Fact]
    public void RendersUnicodeWithTtf()
    {
        using var doc = new PrawnDocument();
        doc.RegisterFontFamily("DejaVu", FontPath("DejaVuSans.ttf"));
        doc.Font("DejaVu", 12);
        doc.Text("Hola ñ á € — Привет");

        var bytes = doc.RenderBytes();

        Assert.True(
            bytes.Length < 300_000,
            $"El PDF pesa {bytes.Length} bytes; parece que se incrustó la fuente completa.");

        using var pdf = Pig.PdfDocument.Open(new MemoryStream(bytes));
        var text = pdf.GetPage(1).Text;

        Assert.Contains("Hola", text);
        Assert.Contains("Привет", text);
    }

    [Fact]
    public void RendersOtfFont()
    {
        using var doc = new PrawnDocument();
        doc.RegisterFontFamily("Bodoni", FontPath("Bodoni-Book.otf"));
        doc.Font("Bodoni", 12);
        doc.Text("Hola mundo");

        using var pdf = Pig.PdfDocument.Open(new MemoryStream(doc.RenderBytes()));

        Assert.Contains("Hola", pdf.GetPage(1).Text);
    }

    [Fact]
    public void FormattedTextWithTtfFamily()
    {
        using var doc = new PrawnDocument();
        doc.RegisterFontFamily("DejaVu", FontPath("DejaVuSans.ttf"));
        doc.Font("DejaVu", 12);
        doc.FormattedText(FormattedText.Parse("Hola <b>mundo</b>"));

        using var pdf = Pig.PdfDocument.Open(new MemoryStream(doc.RenderBytes()));
        var text = pdf.GetPage(1).Text;

        Assert.Contains("Hola", text);
        Assert.Contains("mundo", text);
    }

    [Fact]
    public void TtfSurvivesPageBreak()
    {
        using var doc = new PrawnDocument();
        doc.RegisterFontFamily("DejaVu", FontPath("DejaVuSans.ttf"));
        doc.Font("DejaVu", 12);

        doc.Text("uno");
        doc.StartNewPage();
        doc.Text("dos");

        using var pdf = Pig.PdfDocument.Open(new MemoryStream(doc.RenderBytes()));

        Assert.Equal(2, pdf.NumberOfPages);
        Assert.Contains("uno", pdf.GetPage(1).Text);
        Assert.Contains("dos", pdf.GetPage(2).Text);
    }
}
