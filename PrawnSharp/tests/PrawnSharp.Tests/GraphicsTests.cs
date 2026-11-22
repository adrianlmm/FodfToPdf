using System.Text;
using Xunit;
using Pig = UglyToad.PdfPig;

namespace PrawnSharp.Tests;

public sealed class GraphicsTests
{
    [Fact]
    public void DrawsPathWithFillStrokeAndDash()
    {
        using var doc = new PrawnDocument(new DocumentOptions
        {
            PageSize = new Size(300, 300),
            Margin = new Thickness(10),
            Compress = false,
        });

        var path = PrawnSharp.Path.Create()
            .MoveTo(20, 20)
            .LineTo(250, 20)
            .LineTo(130, 220)
            .Close();

        doc.DrawPath(
            path,
            Paint.Solid(PdfColor.Red, 0.8),
            StrokeStyle.Create(
                PdfColor.Black,
                2,
                0.9,
                LineCap.Round,
                LineJoin.Bevel,
                dash: [5, 2]));

        var bytes = doc.RenderBytes();
        using var pdf = Pig.PdfDocument.Open(new MemoryStream(bytes));

        Assert.Equal(1, pdf.NumberOfPages);
        Assert.Equal(300, pdf.GetPage(1).Width);
        Assert.Contains(" RG", Encoding.ASCII.GetString(bytes));
        Assert.Contains(" d", Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void DrawsLinearAndRadialGradients()
    {
        using var doc = new PrawnDocument(new DocumentOptions
        {
            PageSize = new Size(300, 300),
            Margin = new Thickness(10),
        });

        doc.FillGradient(
            PrawnSharp.Path.Create().Rectangle(20, 20, 120, 80),
            Gradient.Linear(
                new Point(20, 20),
                new Point(140, 20),
                new GradientStop(0, PdfColor.Red),
                new GradientStop(1, PdfColor.Blue)));

        doc.FillGradient(
            PrawnSharp.Path.Create().Circle(180, 180, 60),
            Gradient.Radial(
                new Point(180, 180),
                60,
                new GradientStop(0, PdfColor.White),
                new GradientStop(1, PdfColor.Black)));

        using var pdf = Pig.PdfDocument.Open(new MemoryStream(doc.RenderBytes()));

        Assert.Equal(1, pdf.NumberOfPages);
    }

    [Fact]
    public void AppliesTransformsClipAndAlpha()
    {
        using var doc = new PrawnDocument(new DocumentOptions
        {
            PageSize = new Size(300, 300),
            Margin = new Thickness(10),
        });

        doc.SaveGraphicsState();
        doc.Translate(40, 40);
        doc.Rotate(15);
        doc.Scale(1.2, 1.2);
        doc.SetClip(PrawnSharp.Path.Create().Rectangle(0, 0, 160, 100));
        doc.SetAlpha(0.5, 0.5, BlendMode.Multiply);
        doc.Fill(PrawnSharp.Path.Create().Rectangle(0, 0, 160, 100), PdfColor.Green);
        doc.RestoreGraphicsState();

        using var pdf = Pig.PdfDocument.Open(new MemoryStream(doc.RenderBytes()));

        Assert.Equal(1, pdf.NumberOfPages);
    }

    [Fact]
    public void DrawsEllipsePolygonAndEvenOddFill()
    {
        using var doc = new PrawnDocument(new DocumentOptions
        {
            PageSize = new Size(300, 300),
            Margin = new Thickness(10),
        });

        var evenOdd = PrawnSharp.Path.Create()
            .Rectangle(20, 20, 260, 260)
            .Circle(150, 150, 80);

        doc.DrawPath(evenOdd, Paint.Fill(PdfColor.Blue), null, FillRule.EvenOdd);

        var polygon = PrawnSharp.Path.Create().Polygon(
            new Point(50, 180),
            new Point(120, 240),
            new Point(190, 180));

        doc.DrawPath(polygon, null, new StrokeStyle(PdfColor.Red, 3));

        using var pdf = Pig.PdfDocument.Open(new MemoryStream(doc.RenderBytes()));

        Assert.Equal(1, pdf.NumberOfPages);
    }
}
