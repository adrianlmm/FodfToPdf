using FodtToPdf;

// Render the original template as-is, without replacing fields or expanding regions.
var samples = FindSamplesDirectory();
var templatePath = Path.Combine(samples, "OrdenDeCompraInvalida.fodt");
var templateBytes = File.ReadAllBytes(templatePath);

var questPdf = QuestPdfFodtRenderer.Render(templateBytes, nestedTables: true);
var questPdfPath = Path.Combine(samples, "invalid-purchase-order-template-questpdf.pdf");
File.WriteAllBytes(questPdfPath, questPdf);

Console.WriteLine($"Source template: {templatePath}");
Console.WriteLine($"QuestPDF PDF:    {questPdfPath} ({questPdf.Length} bytes)");

static string FindSamplesDirectory()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "OrdenDeCompraInvalida.fodt")))
            return directory.FullName;

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("OrdenDeCompraInvalida.fodt was not found.");
}
