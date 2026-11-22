using FodtToPdf;

// Renders the merged-cell table template with both PDF backends.
var samples = FindSamplesDirectory();
var templatePath = Path.Combine(samples, "tabla.fodt");

// Read with FileShare.ReadWrite so the sample still works while the .fodt is
// open in LibreOffice (Windows otherwise blocks File.ReadAllBytes).
byte[] templateBytes;
using (var stream = new FileStream(templatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
using (var memory = new MemoryStream())
{
    stream.CopyTo(memory);
    templateBytes = memory.ToArray();
}

var questPdf = QuestPdfFodtRenderer.Render(templateBytes);
var questPdfPath = Path.Combine(samples, "tabla-questpdf.pdf");
File.WriteAllBytes(questPdfPath, questPdf);

var prawnSharpPdf = PrawnSharpFodtRenderer.Render(templateBytes);
var prawnSharpPdfPath = Path.Combine(samples, "tabla-prawnsharp.pdf");
File.WriteAllBytes(prawnSharpPdfPath, prawnSharpPdf);

var libreOfficePdf = QuestPdfFodtRenderer.RenderWithLibreOffice(templateBytes);
var libreOfficePdfPath = Path.Combine(samples, "tabla-libreoffice.pdf");
File.WriteAllBytes(libreOfficePdfPath, libreOfficePdf);

Console.WriteLine($"Template: {templatePath}");
Console.WriteLine($"QuestPDF PDF:     {questPdfPath} ({questPdf.Length} bytes)");
Console.WriteLine($"PrawnSharp PDF:     {prawnSharpPdfPath} ({prawnSharpPdf.Length} bytes)");
Console.WriteLine($"LibreOffice PDF:  {libreOfficePdfPath} ({libreOfficePdf.Length} bytes)");

static string FindSamplesDirectory()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "tabla.fodt")))
            return directory.FullName;

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("tabla.fodt was not found.");
}
