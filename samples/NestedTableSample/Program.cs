using FodtToPdf;

// Demonstrates rendering a table nested inside another table cell.
// The template has no data fields; the example shows the required renderer flag.
var samples = FindSamplesDirectory();
var template = FodtTemplate.Load(Path.Combine(samples, "nested_table.fodt"));
var filled = template.ToBytes();

var filledPath = Path.Combine(samples, "nested-table-sample-filled.fodt");
File.WriteAllBytes(filledPath, filled);

var questPdf = QuestPdfFodtRenderer.Render(filled, nestedTables: true);
var questPdfPath = Path.Combine(samples, "nested-table-sample-questpdf.pdf");
File.WriteAllBytes(questPdfPath, questPdf);

var prawnSharpPdf = PrawnSharpFodtRenderer.Render(filled, nestedTables: true);
var prawnSharpPdfPath = Path.Combine(samples, "nested-table-sample-prawnsharp.pdf");
File.WriteAllBytes(prawnSharpPdfPath, prawnSharpPdf);

var libreOfficePdf = QuestPdfFodtRenderer.RenderWithLibreOffice(filled);
var libreOfficePdfPath = Path.Combine(samples, "nested-table-sample-libreoffice.pdf");
File.WriteAllBytes(libreOfficePdfPath, libreOfficePdf);

Console.WriteLine($"Filled template: {filledPath}");
Console.WriteLine($"QuestPDF PDF:    {questPdfPath} ({questPdf.Length} bytes)");
Console.WriteLine($"PrawnSharp PDF:    {prawnSharpPdfPath} ({prawnSharpPdf.Length} bytes)");
Console.WriteLine($"LibreOffice PDF: {libreOfficePdfPath} ({libreOfficePdf.Length} bytes)");

static string FindSamplesDirectory()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "nested_table.fodt")))
            return directory.FullName;

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("nested_table.fodt was not found.");
}
