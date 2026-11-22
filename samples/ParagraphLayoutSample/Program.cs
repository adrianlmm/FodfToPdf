using FodtToPdf;

// Renders the paragraph-layout template with both PDF backends.
var samples = FindSamplesDirectory();
var templatePath = Path.Combine(samples, "parrafos.fodt");

// Tolerate the .fodt being open in LibreOffice on Windows.
byte[] templateBytes;
using (var stream = new FileStream(templatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
using (var memory = new MemoryStream())
{
    stream.CopyTo(memory);
    templateBytes = memory.ToArray();
}

var questPdf = QuestPdfFodtRenderer.Render(templateBytes);
var questPdfPath = Path.Combine(samples, "parrafos-questpdf.pdf");
File.WriteAllBytes(questPdfPath, questPdf);

var prawnSharpPdf = PrawnSharpFodtRenderer.Render(templateBytes);
var prawnSharpPdfPath = Path.Combine(samples, "parrafos-prawnsharp.pdf");
File.WriteAllBytes(prawnSharpPdfPath, prawnSharpPdf);

var libreOfficePdf = QuestPdfFodtRenderer.RenderWithLibreOffice(templateBytes);
var libreOfficePdfPath = Path.Combine(samples, "parrafos-libreoffice.pdf");
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
        if (File.Exists(Path.Combine(directory.FullName, "parrafos.fodt")))
            return directory.FullName;

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("parrafos.fodt was not found.");
}
