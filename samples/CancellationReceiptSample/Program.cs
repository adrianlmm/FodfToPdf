using FodtToPdf;

// Fills the Mexican CFDI cancellation acknowledgement template and exports
// both PDF backends. The template uses bookmarks only.
var samples = FindSamplesDirectory();
var template = FodtTemplate.Load(Path.Combine(samples, "AcuseCancelacion.fodt"));

var values = new Dictionary<string, string>
{
    ["folioFiscal"] = "0DF2065B-404C-42D7-A081-7877513F6EB5",
    ["rfcEmisor"] = "DIP900505JN2",
    ["fechaSolicitud"] = "2026-11-18T09:45:00",
    ["fechaCancelacion"] = "2026-11-18T10:15:00",
    // The bookmark ends in the next paragraph, which already contains the final line
    // "tL3fAhi6Gg==" in the template. Replace only its first line here.
    ["selloDigitalSat"] = "gb/7f7+ylnZMjPZhqF8ZK/id7Co03h19h6LoHTCtoc13wgDaW1hlQ/KPaO2TaxKJSX4EoRv8vA6b",
};

var replaced = 0;
foreach (var (name, value) in values)
{
    if (template.SetBookmarkIfExists(name, value))
        replaced++;
    else
        Console.WriteLine($"Bookmark not found: {name}");
}

var filled = template.ToBytes();
var filledPath = Path.Combine(samples, "acuse-cancelacion-sample-filled.fodt");
File.WriteAllBytes(filledPath, filled);

var prawnSharpPdf = PrawnSharpFodtRenderer.Render(filled);
var prawnSharpPdfPath = Path.Combine(samples, "acuse-cancelacion-sample-prawnsharp.pdf");
File.WriteAllBytes(prawnSharpPdfPath, prawnSharpPdf);

var libreOfficePdf = PrawnSharpFodtRenderer.RenderWithLibreOffice(filled);
var libreOfficePdfPath = Path.Combine(samples, "acuse-cancelacion-sample-libreoffice.pdf");
File.WriteAllBytes(libreOfficePdfPath, libreOfficePdf);

Console.WriteLine($"Bookmarks replaced: {replaced}");
Console.WriteLine($"Filled template: {filledPath}");
Console.WriteLine($"PrawnSharp PDF:    {prawnSharpPdfPath} ({prawnSharpPdf.Length} bytes)");
Console.WriteLine($"LibreOffice PDF: {libreOfficePdfPath} ({libreOfficePdf.Length} bytes)");

static string FindSamplesDirectory()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "AcuseCancelacion.fodt")))
            return directory.FullName;

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("AcuseCancelacion.fodt was not found.");
}
