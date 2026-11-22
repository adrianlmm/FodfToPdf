using Xunit;
using Pig = UglyToad.PdfPig;

namespace PrawnSharp.Tests;

public sealed class TableTests
{
    private static TableCellStyle BorderStyle => new()
    {
        Padding = new Thickness(5),
        BorderColor = PdfColor.Black,
        BorderWidth = 0.5,
    };

    [Fact]
    public void DrawsSimpleTableWithHeaderAndExplicitWidths()
    {
        var table = new Table(
            TableRow.Header(TableCell.From("Concepto"), TableCell.From("Importe")),
            new TableRow(TableCell.From("Servicio"), TableCell.From("$1,250.00")),
            new TableRow(TableCell.From("IVA"), TableCell.From("$200.00")))
        {
            ColumnWidths = [260, 140],
            HeaderRows = 1,
            DefaultCellStyle = BorderStyle,
        };

        using var doc = new PrawnDocument(new DocumentOptions
        {
            PageSize = new Size(500, 500),
            Margin = new Thickness(20),
        });

        doc.Table(table, new TableOptions { Width = 400 });
        using var pdf = Pig.PdfDocument.Open(new MemoryStream(doc.RenderBytes()));

        Assert.Equal(1, pdf.NumberOfPages);
        Assert.Contains("Concepto", pdf.GetPage(1).Text);
        Assert.Contains("$1,250.00", pdf.GetPage(1).Text);
    }

    [Fact]
    public void SupportsColspanAndRowspan()
    {
        var table = new Table(
            TableRow.Header(TableCell.From("Concepto"), TableCell.From("Importe")),
            new TableRow(
                new TableCell { Text = "Total", ColSpan = 2, Style = BorderStyle },
                new TableCell { Text = "ignorada", Style = BorderStyle }),
            new TableRow(
                new TableCell { Text = "A", RowSpan = 2, Style = BorderStyle },
                new TableCell { Text = "10", Style = BorderStyle }),
            new TableRow(
                new TableCell { Text = "20", Style = BorderStyle }))
        {
            ColumnWidths = [220, 180],
            HeaderRows = 1,
            DefaultCellStyle = BorderStyle,
        };

        using var doc = new PrawnDocument(new DocumentOptions
        {
            PageSize = new Size(500, 500),
            Margin = new Thickness(20),
        });

        doc.Table(table);
        using var pdf = Pig.PdfDocument.Open(new MemoryStream(doc.RenderBytes()));

        Assert.Equal(1, pdf.NumberOfPages);
        Assert.Contains("Total", pdf.GetPage(1).Text);
        Assert.Contains("A", pdf.GetPage(1).Text);
    }

    [Fact]
    public void AutomaticallyCalculatesColumnWidths()
    {
        var table = new Table(
            TableRow.Header(TableCell.From("Descripción larga"), TableCell.From("Importe")),
            new TableRow(TableCell.From("Un servicio con un texto bastante largo"), TableCell.From("100.00")))
        {
            HeaderRows = 1,
            DefaultCellStyle = BorderStyle,
        };

        using var doc = new PrawnDocument(new DocumentOptions
        {
            PageSize = new Size(500, 500),
            Margin = new Thickness(20),
        });

        doc.Table(table);
        using var pdf = Pig.PdfDocument.Open(new MemoryStream(doc.RenderBytes()));

        Assert.Equal(1, pdf.NumberOfPages);
        Assert.Contains("Descripción larga", pdf.GetPage(1).Text);
    }

    [Fact]
    public void PaginatesAndRepeatsHeader()
    {
        var rows = new List<TableRow>
        {
            TableRow.Header(TableCell.From("Concepto"), TableCell.From("Importe")),
        };

        for (var i = 1; i <= 120; i++)
        {
            rows.Add(new TableRow(TableCell.From($"Concepto {i}"), TableCell.From($"{i:0.00}")));
        }

        var table = new Table(rows)
        {
            ColumnWidths = [300, 150],
            HeaderRows = 1,
            DefaultCellStyle = BorderStyle,
        };

        using var doc = new PrawnDocument(new DocumentOptions
        {
            PageSize = new Size(500, 600),
            Margin = new Thickness(20),
        });

        doc.Table(table);
        using var pdf = Pig.PdfDocument.Open(new MemoryStream(doc.RenderBytes()));

        Assert.True(pdf.NumberOfPages > 1);
        Assert.Contains("Concepto", pdf.GetPage(2).Text);
        Assert.Contains("120.00", pdf.GetPage(pdf.NumberOfPages).Text);
    }
}
