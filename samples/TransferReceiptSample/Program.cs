using System.Data;
using FodtToPdf;

// Fills the CFDI transfer receipt template with header bookmarks and a
// repeating concept region, then exports both PDF backends.
var samples = FindSamplesDirectory();
var template = FodtTemplate.Load(Path.Combine(samples, "ComprobanteTraslado.fodt"));

var values = new Dictionary<string, string>
{
    ["folioFiscal"] = "0DF2065B-404C-42D7-A081-7877513F6EB5",
    ["serieCSD"] = "00001000000404108700",
    ["fechaHora"] = "2026-11-18T10:30:00",
    ["rfcEmisor"] = "ASO0012122H1",
    ["nombreEmisor"] = "ALIMENTOS SOLES SA DE CV",
    ["rfcReceptor"] = "FUTM520601EC1",
    ["nombreReceptor"] = "MARCELA IVONNE FUENTES TERRAZAS",
    ["regimenFiscal"] = "General de Ley Personas Morales",
    ["numSerieCertificadoSAT"] = "00001000000202639096",
    ["rfcProveedorCertificacion"] = "XAXAXAXAXAXAXAXA",
    ["fechaCertificacion"] = "2026-11-18T10:31:00",
    ["selloDigitalCFDI"] = "SAMPLE-CFDI-SEAL",
    ["selloDigitalSAT"] = "SAMPLE-SAT-SEAL",
    ["cadenaOriginalComplementoSAT"] = "||1.0|0DF2065B-404C-42D7-A081-7877513F6EB5|2026-11-18T10:30:00|sample||",
};

var replaced = 0;
foreach (var (name, value) in values)
    if (template.SetBookmarkIfExists(name, value)) replaced++;

var concepts = new DataTable("TablaConceptos");
concepts.Columns.Add("claveProducto", typeof(string));
concepts.Columns.Add("cantidad", typeof(string));
concepts.Columns.Add("claveUnidad", typeof(string));
concepts.Columns.Add("descripcion", typeof(string));

void AddConcept(string clave, string cantidad, string unidad, string descripcion)
{
    var row = concepts.NewRow();
    row["claveProducto"] = clave;
    row["cantidad"] = cantidad;
    row["claveUnidad"] = unidad;
    row["descripcion"] = descripcion;
    concepts.Rows.Add(row);
}

AddConcept("01010101", "2", "H87", "Caja de cartón corrugado para transporte");
AddConcept("01010102", "4", "XPK", "Película protectora para embalaje");
AddConcept("01010103", "10", "EA", "Etiqueta de identificación de producto");

var regionRows = template.ExecuteRegion("TablaConceptos", concepts);
if (regionRows < 0)
    throw new InvalidOperationException("The TablaConceptos region was not found in the template.");

var filled = template.ToBytes();
var filledPath = Path.Combine(samples, "transfer-receipt-sample-filled.fodt");
File.WriteAllBytes(filledPath, filled);

var questPdf = QuestPdfFodtRenderer.Render(filled);
var questPdfPath = Path.Combine(samples, "transfer-receipt-sample-questpdf.pdf");
File.WriteAllBytes(questPdfPath, questPdf);

var libreOfficePdf = QuestPdfFodtRenderer.RenderWithLibreOffice(filled);
var libreOfficePdfPath = Path.Combine(samples, "transfer-receipt-sample-libreoffice.pdf");
File.WriteAllBytes(libreOfficePdfPath, libreOfficePdf);

Console.WriteLine($"Bookmarks replaced: {replaced}");
Console.WriteLine($"Concept rows:       {regionRows}");
Console.WriteLine($"Filled template:    {filledPath}");
Console.WriteLine($"QuestPDF PDF:       {questPdfPath} ({questPdf.Length} bytes)");
Console.WriteLine($"LibreOffice PDF:    {libreOfficePdfPath} ({libreOfficePdf.Length} bytes)");

static string FindSamplesDirectory()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "ComprobanteTraslado.fodt")))
            return directory.FullName;

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("ComprobanteTraslado.fodt was not found.");
}
