using System.Text;
using PdfSharp.Pdf.IO;
using Xunit;
using Pig = UglyToad.PdfPig;

namespace PrawnSharp.Tests;

public sealed class ExtrasTests
{
    [Fact]
    public void DrawsStampsAndRepeater()
    {
        using var doc = new PrawnDocument(new DocumentOptions
        {
            PageSize = new Size(400, 500),
            Margin = new Thickness(20),
        });

        var stamp = doc.CreateStamp(100, 30, box =>
        {
            doc.Font("Helvetica", 10);
            doc.Text("STAMP");
        });

        doc.StampAt(stamp, 20, 20);
        doc.StampAt(stamp, 140, 20);
        doc.AddRepeater(d =>
        {
            d.MoveTo(d.PageSize.Height - 30);
            d.Text("PIE", new TextOptions { Size = 8 });
        });

        doc.StartNewPage();
        doc.Text("Página dos");

        using var pdf = Pig.PdfDocument.Open(new MemoryStream(doc.RenderBytes()));

        Assert.Equal(2, pdf.NumberOfPages);
        Assert.Contains("STAMP", pdf.GetPage(1).Text);
        Assert.Contains("PIE", pdf.GetPage(1).Text);
        Assert.Contains("PIE", pdf.GetPage(2).Text);
    }

    [Fact]
    public void DrawsGridCells()
    {
        using var doc = new PrawnDocument(new DocumentOptions
        {
            PageSize = new Size(300, 300),
            Margin = new Thickness(10),
        });

        doc.Grid(3, 2, 60, 30, (box, row, column) =>
        {
            box.Text($"{row},{column}");
        });

        using var pdf = Pig.PdfDocument.Open(new MemoryStream(doc.RenderBytes()));

        Assert.Contains("0,0", pdf.GetPage(1).Text);
        Assert.Contains("1,2", pdf.GetPage(1).Text);
    }

    [Fact]
    public void EncryptsWithUserAndOwnerPassword()
    {
        using var doc = new PrawnDocument(new DocumentOptions
        {
            PageSize = new Size(300, 300),
            Margin = new Thickness(10),
            Security = new PdfSecurityOptions
            {
                UserPassword = "user",
                OwnerPassword = "owner",
                PermitPrint = true,
            },
        });

        doc.Text("Documento cifrado");
        var bytes = doc.RenderBytes();

        Assert.Contains("/Encrypt", Encoding.ASCII.GetString(bytes));

        using var pdf = PdfReader.Open(
            new MemoryStream(bytes),
            "user",
            PdfDocumentOpenMode.Import,
            (PdfPasswordProvider?)null,
            new PdfReaderOptions());

        Assert.Equal(1, pdf.PageCount);
    }
}
