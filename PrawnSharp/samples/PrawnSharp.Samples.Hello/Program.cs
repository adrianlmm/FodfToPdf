using PrawnSharp;

using var doc = new PrawnDocument(new DocumentOptions
{
    PageSize = PageSizes.A4,
    Margin = new Thickness(32),
});

doc.Font("Helvetica", 12);
doc.Text("Hola mundo");
doc.MoveDown(12);
doc.Text("Generado con PrawnSharp");

doc.MoveDown(24);
doc.Line(32, doc.CursorY, 300, doc.CursorY, width: 1, color: PdfColor.Gray);
doc.MoveDown(12);
doc.Rectangle(32, doc.CursorY, 120, 40, fill: PdfColor.FromHex("#DDEBF7"), stroke: PdfColor.Black);
doc.MoveDown(60);

doc.FormattedText(
    "Normal, <b>negrita</b>, <i>itálica</i>, " +
    "<color rgb=\"#CC0000\">rojo</color> y " +
    "<link href=\"https://example.com\">enlace</link>.",
    new TextOptions { Width = 300 });

var path = System.IO.Path.Combine(AppContext.BaseDirectory, "hola.pdf");
doc.RenderFile(path);

Console.WriteLine($"PDF generado: {path}");
Console.WriteLine($"Páginas: {doc.PageCount}");
