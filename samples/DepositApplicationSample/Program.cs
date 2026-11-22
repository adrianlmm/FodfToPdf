using System.Data;
using FodtToPdf;

// Generates the deposit application template both untouched and filled with
// sample invoice/deposit rows.
var samples = FindSamplesDirectory();
var templatePath = Path.Combine(samples, "PlantillaDepositos.fodt");
var templateBytes = File.ReadAllBytes(templatePath);

// 1. Template-only PDFs
var templateQuest = QuestPdfFodtRenderer.Render(templateBytes);
var templateQuestPath = Path.Combine(samples, "deposit-application-template-questpdf.pdf");
File.WriteAllBytes(templateQuestPath, templateQuest);

var templateprawnSharp = PrawnSharpFodtRenderer.Render(templateBytes);
var templateprawnSharpPath = Path.Combine(samples, "deposit-application-template-prawnsharp.pdf");
File.WriteAllBytes(templateprawnSharpPath, templateprawnSharp);

var templateLibreOffice = QuestPdfFodtRenderer.RenderWithLibreOffice(templateBytes);
var templateLibreOfficePath = Path.Combine(samples, "deposit-application-template-libreoffice.pdf");
File.WriteAllBytes(templateLibreOfficePath, templateLibreOffice);

// 2. Filled PDFs
var template = FodtTemplate.Load(templateBytes);
template.SetBookmarkIfExists("Folio", "DEP-2026-00482");
template.SetBookmarkIfExists("Importe", "34,500.00");
template.SetBookmarkIfExists("Asignado", "34,500.00");

var invoices = new DataTable("Facturas");
foreach (var column in new[] { "FolioFactura", "Fecha", "Cliente", "Nombre", "Importe", "Asignado" })
    invoices.Columns.Add(column, typeof(string));

void AddInvoice(string folio, string fecha, string cliente, string nombre, string importe, string asignado)
{
    var row = invoices.NewRow();
    row["FolioFactura"] = folio;
    row["Fecha"] = fecha;
    row["Cliente"] = cliente;
    row["Nombre"] = nombre;
    row["Importe"] = importe;
    row["Asignado"] = asignado;
    invoices.Rows.Add(row);
}

AddInvoice("F-20481", "05/11/2026", "C-000482", "Distribuidora del Noroeste S.A. de C.V.", "15,000.00", "15,000.00");
AddInvoice("F-20512", "11/11/2026", "C-000482", "Distribuidora del Noroeste S.A. de C.V.", "10,000.00", "10,000.00");
AddInvoice("F-20544", "18/11/2026", "C-000482", "Distribuidora del Noroeste S.A. de C.V.", "9,500.00", "9,500.00");

var regionRows = template.ExecuteRegion("Facturas", invoices);
if (regionRows < 0)
    throw new InvalidOperationException("The Facturas region was not found in the template.");

var filled = template.ToBytes();
var filledPath = Path.Combine(samples, "deposit-application-sample-filled.fodt");
File.WriteAllBytes(filledPath, filled);

var questFilled = QuestPdfFodtRenderer.Render(filled);
var questFilledPath = Path.Combine(samples, "deposit-application-sample-questpdf.pdf");
File.WriteAllBytes(questFilledPath, questFilled);

var prawnSharpFilled = PrawnSharpFodtRenderer.Render(filled);
var prawnSharpFilledPath = Path.Combine(samples, "deposit-application-sample-prawnsharp.pdf");
File.WriteAllBytes(prawnSharpFilledPath, prawnSharpFilled);

var libreOfficeFilled = QuestPdfFodtRenderer.RenderWithLibreOffice(filled);
var libreOfficeFilledPath = Path.Combine(samples, "deposit-application-sample-libreoffice.pdf");
File.WriteAllBytes(libreOfficeFilledPath, libreOfficeFilled);

Console.WriteLine($"Template QuestPDF:    {templateQuestPath} ({templateQuest.Length} bytes)");
Console.WriteLine($"Template PrawnSharp:    {templateprawnSharpPath} ({templateprawnSharp.Length} bytes)");
Console.WriteLine($"Template LibreOffice: {templateLibreOfficePath} ({templateLibreOffice.Length} bytes)");
Console.WriteLine($"Invoice rows: {regionRows}");
Console.WriteLine($"Filled template: {filledPath}");
Console.WriteLine($"Filled QuestPDF:    {questFilledPath} ({questFilled.Length} bytes)");
Console.WriteLine($"Filled PrawnSharp:    {prawnSharpFilledPath} ({prawnSharpFilled.Length} bytes)");
Console.WriteLine($"Filled LibreOffice: {libreOfficeFilledPath} ({libreOfficeFilled.Length} bytes)");

static string FindSamplesDirectory()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "PlantillaDepositos.fodt")))
            return directory.FullName;

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("PlantillaDepositos.fodt was not found.");
}
