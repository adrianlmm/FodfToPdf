using System.Data;
using FodtToPdf;

// Fills the physical-signature sheet template with six sample signer names
// and exports both PDF backends.
var samples = FindSamplesDirectory();
var template = FodtTemplate.Load(Path.Combine(samples, "Formato_FirmasPFisica.fodt"));

var data = new DataTable("Firmas");
foreach (var column in new[] { "Nombre1", "Nombre2", "Nombre3", "Nombre4", "Nombre5", "Nombre6" })
    data.Columns.Add(column, typeof(string));

var row = data.NewRow();
row["Nombre1"] = "María Guadalupe Ramírez";
row["Nombre2"] = "José Luis Hernández";
row["Nombre3"] = "Carlos Alberto Pérez";
row["Nombre4"] = "Ana Delia Leyva";
row["Nombre5"] = "Juan Carlos Pérez López";
row["Nombre6"] = "Claudia Zazil Madrid Gámez";
data.Rows.Add(row);

var replaced = template.ExecuteMailMerge(row);
if (replaced == 0)
    throw new InvalidOperationException("The signature template did not contain replaceable fields.");

var filled = template.ToBytes();
var filledPath = Path.Combine(samples, "signature-sheet-sample-filled.fodt");
File.WriteAllBytes(filledPath, filled);

var questPdf = QuestPdfFodtRenderer.Render(filled);
var questPdfPath = Path.Combine(samples, "signature-sheet-sample-questpdf.pdf");
File.WriteAllBytes(questPdfPath, questPdf);

var prawnSharpPdf = PrawnSharpFodtRenderer.Render(filled);
var prawnSharpPdfPath = Path.Combine(samples, "signature-sheet-sample-prawnsharp.pdf");
File.WriteAllBytes(prawnSharpPdfPath, prawnSharpPdf);

var libreOfficePdf = QuestPdfFodtRenderer.RenderWithLibreOffice(filled);
var libreOfficePdfPath = Path.Combine(samples, "signature-sheet-sample-libreoffice.pdf");
File.WriteAllBytes(libreOfficePdfPath, libreOfficePdf);

Console.WriteLine($"Fields replaced: {replaced}");
Console.WriteLine($"Filled template: {filledPath}");
Console.WriteLine($"QuestPDF PDF:    {questPdfPath} ({questPdf.Length} bytes)");
Console.WriteLine($"PrawnSharp PDF:    {prawnSharpPdfPath} ({prawnSharpPdf.Length} bytes)");
Console.WriteLine($"LibreOffice PDF: {libreOfficePdfPath} ({libreOfficePdf.Length} bytes)");

static string FindSamplesDirectory()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "Formato_FirmasPFisica.fodt")))
            return directory.FullName;

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("Formato_FirmasPFisica.fodt was not found.");
}
