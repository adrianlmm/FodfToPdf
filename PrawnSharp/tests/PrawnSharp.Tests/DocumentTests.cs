using System.Linq;
using PrawnSharp;
using Xunit;
using Pig = UglyToad.PdfPig;

namespace PrawnSharp.Tests;

public sealed class DocumentTests
{
    [Fact]
    public void GeneratesOnePageWithText()
    {
        using var doc = new PrawnDocument(new DocumentOptions
        {
            PageSize = PageSizes.A4,
            Margin = new Thickness(32),
        });

        doc.Font("Helvetica", 12);
        doc.Text("Hola mundo");

        var bytes = doc.RenderBytes();

        Assert.True(bytes.Length > 200, "El PDF debería tener contenido.");
        Assert.Equal(1, doc.PageCount);

        using var pdf = Pig.PdfDocument.Open(new MemoryStream(bytes));
        Assert.Equal(1, pdf.NumberOfPages);

        var page = pdf.GetPage(1);
        var words = page.GetWords().Select(w => w.Text).ToList();

        Assert.Contains("Hola", words);
        Assert.Contains("mundo", words);

        var hola = page.GetWords().First(w => w.Text == "Hola");
        Assert.InRange(hola.BoundingBox.Left, 31.0, 33.0);
    }

    [Fact]
    public void StartsNewPageAndKeepsText()
    {
        using var doc = new PrawnDocument();

        doc.Text("uno");
        doc.StartNewPage();
        doc.Text("dos");

        Assert.Equal(2, doc.PageCount);

        using var pdf = Pig.PdfDocument.Open(new MemoryStream(doc.RenderBytes()));
        Assert.Equal(2, pdf.NumberOfPages);

        Assert.Contains("uno", pdf.GetPage(1).Text);
        Assert.Contains("dos", pdf.GetPage(2).Text);
    }

    [Fact]
    public void DrawsLineAndRectangle()
    {
        using var doc = new PrawnDocument();

        doc.Line(10, 10, 100, 10);
        doc.Rectangle(10, 20, 100, 50, fill: PdfColor.Red);

        var bytes = doc.RenderBytes();

        Assert.True(bytes.Length > 200);
    }

    [Fact]
    public void RejectsNonBase14Font()
    {
        using var doc = new PrawnDocument();

        Assert.Throws<UnsupportedFontException>(() => doc.Font("Arial", 12));
    }

    [Fact]
    public void FontWithoutSizeKeepsCurrentSize()
    {
        using var doc = new PrawnDocument();

        doc.Font("Helvetica", 18);
        Assert.Equal(18, doc.FontSize);

        doc.Font("Times");
        Assert.Equal(18, doc.FontSize);
        Assert.Equal("Times", doc.FontFamily);

        doc.Text("prueba", new TextOptions { Size = 8 });
        Assert.Equal(18, doc.FontSize);
    }

    [Fact]
    public void MeasureTextMatchesPrawn()
    {
        using var doc = new PrawnDocument();
        doc.Font("Helvetica", 12);

        Assert.Equal(64.692, doc.MeasureText("Hola mundo"), 3);
        Assert.Equal(64.692, doc.WidthOf("Hola mundo"), 3);
        Assert.Equal(15.168, doc.MeasureText("AV", new TextOptions { Size = 12, Kerning = true }), 3);
    }

    [Fact]
    public void FirstLinePositionsMatchPrawn()
    {
        using var doc = new PrawnDocument(new DocumentOptions
        {
            PageSize = PageSizes.A4,
            Margin = new Thickness(32),
        });

        doc.Font("Helvetica", 12);
        doc.Text("Hola mundo");

        using var pdf = Pig.PdfDocument.Open(new MemoryStream(doc.RenderBytes()));
        var words = pdf.GetPage(1).GetWords().ToList();

        var hola = words.First(w => w.Text == "Hola").BoundingBox;
        var mundo = words.First(w => w.Text == "mundo").BoundingBox;

        Assert.Equal(32.000, hola.Left, 2);
        Assert.Equal(24.672, hola.Width, 3);
        Assert.Equal(60.008, mundo.Left, 2);
        Assert.Equal(36.564, mundo.Width, 3);
    }

    [Fact]
    public void CenterAndRightAlignmentUseAfmWidths()
    {
        using var doc = new PrawnDocument(new DocumentOptions
        {
            PageSize = PageSizes.A4,
            Margin = new Thickness(32),
        });

        doc.Font("Helvetica", 12);
        doc.Text("Hola", new TextOptions { Align = TextAlign.Center });
        doc.Text("Hola", new TextOptions { Align = TextAlign.Right });

        using var pdf = Pig.PdfDocument.Open(new MemoryStream(doc.RenderBytes()));
        var words = pdf.GetPage(1).GetWords().Where(w => w.Text == "Hola").ToList();

        Assert.Equal(2, words.Count);

        var centered = words[0].BoundingBox;
        Assert.InRange((centered.Left + centered.Right) / 2, 296.5, 298.5);

        var right = words[1].BoundingBox;
        Assert.InRange(right.Right, 562.5, 563.5);
    }

    [Fact]
    public void KerningChangesDrawnWidth()
    {
        var normal = RenderWord("AV", kerning: false);
        var kerned = RenderWord("AV", kerning: true);

        Assert.InRange(normal.BoundingBox.Width - kerned.BoundingBox.Width, 0.7, 1.0);
    }

    [Fact]
    public void TextKernsByDefaultLikePrawn()
    {
        var defaultWord = RenderWord("AV");

        Assert.InRange(defaultWord.BoundingBox.Width, 15.0, 15.3);
    }

    private static UglyToad.PdfPig.Content.Word RenderWord(string text, bool? kerning = null)
    {
        using var doc = new PrawnDocument();
        doc.Font("Helvetica", 12);
        doc.Text(text, new TextOptions { Kerning = kerning });

        using var pdf = Pig.PdfDocument.Open(new MemoryStream(doc.RenderBytes()));
        return pdf.GetPage(1).GetWords().First(w => w.Text == text);
    }

    [Fact]
    public void DrawsWrappedParagraphLikePrawn()
    {
        const string paragraph =
            "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor " +
            "incididunt ut labore et dolore magna aliqua.";

        using var doc = new PrawnDocument(new DocumentOptions
        {
            PageSize = new Size(300, 500),
            Margin = new Thickness(10),
        });

        doc.Font("Helvetica", 12);
        doc.Text(paragraph, new TextOptions { Width = 150 });

        using var pdf = Pig.PdfDocument.Open(new MemoryStream(doc.RenderBytes()));
        var lines = ExtractLines(pdf);

        Assert.Collection(
            lines,
            l => Assert.Equal("Lorem ipsum dolor sit amet,", l),
            l => Assert.Equal("consectetur adipiscing elit,", l),
            l => Assert.Equal("sed do eiusmod tempor", l),
            l => Assert.Equal("incididunt ut labore et", l),
            l => Assert.Equal("dolore magna aliqua.", l));
    }

    [Fact]
    public void JustifiesAllButLastLine()
    {
        const string paragraph =
            "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor " +
            "incididunt ut labore et dolore magna aliqua.";

        using var doc = new PrawnDocument(new DocumentOptions
        {
            PageSize = new Size(300, 500),
            Margin = new Thickness(10),
        });

        doc.Font("Helvetica", 12);
        doc.Text(paragraph, new TextOptions { Width = 150, Align = TextAlign.Justify });

        using var pdf = Pig.PdfDocument.Open(new MemoryStream(doc.RenderBytes()));
        var groups = pdf.GetPage(1).GetWords()
            .GroupBy(w => Math.Round(w.BoundingBox.Bottom, 1))
            .OrderByDescending(g => g.Key)
            .ToList();

        var firstLine = groups[0].OrderBy(w => w.BoundingBox.Left).ToList();
        Assert.InRange(firstLine[^1].BoundingBox.Right, 159.0, 161.0);

        var lastLine = groups[^1].OrderBy(w => w.BoundingBox.Left).ToList();
        Assert.True(lastLine[^1].BoundingBox.Right < 150.0);
    }

    [Fact]
    public void BreaksPagesWhenTextOverflows()
    {
        using var doc = new PrawnDocument(new DocumentOptions
        {
            PageSize = new Size(200, 200),
            Margin = new Thickness(10),
        });

        doc.Font("Helvetica", 12);
        doc.Text(string.Join('\n', Enumerable.Range(1, 30).Select(i => $"line {i}")));

        Assert.True(doc.PageCount >= 2);

        using var pdf = Pig.PdfDocument.Open(new MemoryStream(doc.RenderBytes()));
        Assert.Equal(doc.PageCount, pdf.NumberOfPages);
        Assert.Contains("line 30", pdf.GetPage(pdf.NumberOfPages).Text);
    }

    [Fact]
    public void FinalGapChangesCursorAdvance()
    {
        using var doc = new PrawnDocument(new DocumentOptions
        {
            PageSize = PageSizes.A4,
            Margin = new Thickness(32),
        });

        doc.Text("one", new TextOptions { FinalGap = false });
        var withoutGap = doc.CursorY;

        doc.MoveTo(32);
        doc.Text("one");
        var withGap = doc.CursorY;

        Assert.True(withoutGap < withGap);
        Assert.InRange(withGap - withoutGap, 2.5, 3.0);
    }

    [Fact]
    public void SupportsRotatedAndStrokedText()
    {
        using var doc = new PrawnDocument();
        doc.Font("Helvetica", 12);

        doc.Text("rotado", new TextOptions
        {
            Rotate = 45,
            Mode = TextRenderMode.FillStroke,
        });

        var bytes = doc.RenderBytes();

        Assert.True(bytes.Length > 200);
    }

    [Fact]
    public void AlignsTextVerticallyInFixedHeight()
    {
        var top = RenderTextBottom("Hola", TextValign.Top);
        var center = RenderTextBottom("Hola", TextValign.Center);
        var bottom = RenderTextBottom("Hola", TextValign.Bottom);

        Assert.Equal(43.064, top - center, 2);
        Assert.Equal(86.128, top - bottom, 2);
    }

    [Fact]
    public void AlignsFormattedTextVerticallyInFixedHeight()
    {
        using var doc = new PrawnDocument(new DocumentOptions
        {
            PageSize = PageSizes.A4,
            Margin = new Thickness(32),
        });

        doc.Font("Helvetica", 12);
        doc.FormattedText("Hola", new TextOptions
        {
            Width = 200,
            Height = 100,
            Valign = TextValign.Bottom,
        });

        Assert.Equal(132, doc.CursorY, 3);

        using var pdf = Pig.PdfDocument.Open(new MemoryStream(doc.RenderBytes()));
        var bottom = pdf.GetPage(1).GetWords().First(w => w.Text == "Hola").BoundingBox.Bottom;

        Assert.InRange(bottom, 714.0, 716.0);
    }

    private static double RenderTextBottom(string text, TextValign valign)
    {
        using var doc = new PrawnDocument(new DocumentOptions
        {
            PageSize = PageSizes.A4,
            Margin = new Thickness(32),
        });

        doc.Font("Helvetica", 12);
        doc.Text(text, new TextOptions
        {
            Width = 200,
            Height = 100,
            Valign = valign,
        });

        Assert.Equal(132, doc.CursorY, 3);

        using var pdf = Pig.PdfDocument.Open(new MemoryStream(doc.RenderBytes()));
        return pdf.GetPage(1).GetWords().First(w => w.Text == text).BoundingBox.Bottom;
    }

    private static List<string> ExtractLines(Pig.PdfDocument pdf, int pageNumber = 1) =>
        pdf.GetPage(pageNumber).GetWords()
            .GroupBy(w => Math.Round(w.BoundingBox.Bottom, 1))
            .OrderByDescending(g => g.Key)
            .Select(g => string.Join(' ', g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)))
            .ToList();

    [Fact]
    public void BoundingBoxScopesTextAndRestoresCursor()
    {
        using var doc = new PrawnDocument(new DocumentOptions
        {
            PageSize = PageSizes.A4,
            Margin = new Thickness(32),
        });

        Assert.Equal(32, doc.CursorY);

        doc.BoundingBox(new Point(10, 20), 100, 50, box =>
        {
            Assert.Equal(42, box.Left);
            Assert.Equal(52, box.Top);
            box.Text("dentro");
        });

        Assert.Equal(32, doc.CursorY);

        using var pdf = Pig.PdfDocument.Open(new MemoryStream(doc.RenderBytes()));
        var word = pdf.GetPage(1).GetWords().First(w => w.Text == "dentro");
        Assert.InRange(word.BoundingBox.Left, 41.0, 43.0);
    }

    [Fact]
    public void CanvasUsesPageOrigin()
    {
        using var doc = new PrawnDocument(new DocumentOptions
        {
            PageSize = PageSizes.A4,
            Margin = new Thickness(32),
        });

        doc.Canvas(canvas =>
        {
            Assert.Equal(0, canvas.Left);
            Assert.Equal(0, canvas.Top);
            canvas.Text("canvas");
        });

        using var pdf = Pig.PdfDocument.Open(new MemoryStream(doc.RenderBytes()));
        var word = pdf.GetPage(1).GetWords().First(w => w.Text == "canvas");
        Assert.InRange(word.BoundingBox.Left, -0.5, 0.5);
    }

    [Fact]
    public void RenderFileWritesPdf()
    {
        using var doc = new PrawnDocument();
        doc.Text("archivo");

        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"prawnsharp-{Guid.NewGuid():N}.pdf");

        try
        {
            doc.RenderFile(path);

            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path).Length > 200);

            var header = File.ReadAllBytes(path).Take(5).ToArray();
            Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(header));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
