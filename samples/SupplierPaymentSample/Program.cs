using System.Data;
using FodtToPdf;

// Fills the supplier-payment master template with sample header data and
// exports both PDF backends. The template has one static summary row.
var samples = FindSamplesDirectory();
var template = FodtTemplate.Load(Path.Combine(samples, "PagosFacturasProveedorMaestro.fodt"));

var data = new DataTable("Pagos");
foreach (var column in new[] { "Titulo", "Proveedor", "Fecha", "Empresa", "NombreImpst", "FolioPago" })
    data.Columns.Add(column, typeof(string));

var row = data.NewRow();
row["Titulo"] = "Pagos a Proveedores";
row["Proveedor"] = "Distribuidora del Noroeste S.A. de C.V.";
row["Fecha"] = "19/11/2026";
row["Empresa"] = "Alimentos Soles S.A. de C.V.";
row["NombreImpst"] = "IVA";
row["FolioPago"] = "PAG-2026-00482";
data.Rows.Add(row);

var replaced = template.ExecuteMailMerge(row);
if (replaced == 0)
    throw new InvalidOperationException("The supplier-payment template did not contain replaceable fields.");

var filled = template.ToBytes();
var filledPath = Path.Combine(samples, "supplier-payment-sample-filled.fodt");
File.WriteAllBytes(filledPath, filled);

var prawnSharpPdf = PrawnSharpFodtRenderer.Render(filled);
var prawnSharpPdfPath = Path.Combine(samples, "supplier-payment-sample-prawnsharp.pdf");
File.WriteAllBytes(prawnSharpPdfPath, prawnSharpPdf);

var libreOfficePdf = PrawnSharpFodtRenderer.RenderWithLibreOffice(filled);
var libreOfficePdfPath = Path.Combine(samples, "supplier-payment-sample-libreoffice.pdf");
File.WriteAllBytes(libreOfficePdfPath, libreOfficePdf);

Console.WriteLine($"Fields replaced: {replaced}");
Console.WriteLine($"Filled template: {filledPath}");
Console.WriteLine($"PrawnSharp PDF:    {prawnSharpPdfPath} ({prawnSharpPdf.Length} bytes)");
Console.WriteLine($"LibreOffice PDF: {libreOfficePdfPath} ({libreOfficePdf.Length} bytes)");

static string FindSamplesDirectory()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "PagosFacturasProveedorMaestro.fodt")))
            return directory.FullName;

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("PagosFacturasProveedorMaestro.fodt was not found.");
}
