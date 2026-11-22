using PdfSharp.Pdf.IO;
using Xunit;

namespace PrawnSharp.Tests;

public sealed class OutlineTests
{
    [Fact]
    public void CreatesBookmarksAndInternalLinks()
    {
        using var doc = new PrawnDocument(new DocumentOptions
        {
            PageSize = new Size(400, 500),
            Margin = new Thickness(20),
        });

        doc.Text("Página uno");
        doc.AddOutline("Inicio", 0);

        doc.StartNewPage();
        doc.Text("Página dos");
        doc.AddOutline("Segunda página", 1);

        doc.AddLinkToPage(new Rect(20, 20, 160, 20), 1);

        var bytes = doc.RenderBytes();
        using var pdf = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);

        Assert.Equal(2, pdf.PageCount);
        Assert.Equal(2, pdf.Outlines.Count);
        Assert.Equal("Inicio", pdf.Outlines[0].Title);
        Assert.Equal("Segunda página", pdf.Outlines[1].Title);
        Assert.NotNull(pdf.Outlines[0].DestinationPage);
        Assert.NotNull(pdf.Outlines[1].DestinationPage);
        var destination0 = pdf.Outlines[0].DestinationPage!;
        var destination1 = pdf.Outlines[1].DestinationPage!;
        Assert.Equal(0, pdf.Pages.Cast<PdfSharp.Pdf.PdfPage>().ToList().IndexOf(destination0));
        Assert.Equal(1, pdf.Pages.Cast<PdfSharp.Pdf.PdfPage>().ToList().IndexOf(destination1));
    }

    [Fact]
    public void MeasurementExtensionsMatchPrawn()
    {
        Assert.Equal(72, 1.In(), 3);
        Assert.Equal(28.3464567, 1.Cm(), 4);
        Assert.Equal(2.8346457, 1.Mm(), 4);
        Assert.Equal(12, 12.Pt(), 3);
    }
}
