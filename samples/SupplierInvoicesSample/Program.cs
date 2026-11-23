using System.Data;
using FodtToPdf;

// Fills the supplier-invoice report template with header bookmarks and a
// repeating invoice region, then exports both PDF backends.
var samples = FindSamplesDirectory();
var template = FodtTemplate.Load(Path.Combine(samples, "FacturasProveedor.fodt"));

template.SetBookmarkIfExists("Titulo", "Facturas de Proveedores");
template.SetBookmarkIfExists("Subtitulo1", "Del 01 al 18 de noviembre de 2026");
template.SetBookmarkIfExists("Subtitulo2", "Empresa: Alimentos Soles S.A. de C.V.");
template.SetBookmarkIfExists("Subtitulo3", "Moneda: MXP");

var invoices = new DataTable("FacturasProveedor");
foreach (var column in new[]
{
    "Id", "Empresa", "Fecha", "ClaveProveedor", "NombreProveedor",
    "FolioFactura", "SubTotal", "Impuesto", "Total"
})
{
    invoices.Columns.Add(column, typeof(string));
}

void AddInvoice(string id, string empresa, string fecha, string clave, string proveedor,
                string folio, string subtotal, string impuesto, string total)
{
    var row = invoices.NewRow();
    row["Id"] = id;
    row["Empresa"] = empresa;
    row["Fecha"] = fecha;
    row["ClaveProveedor"] = clave;
    row["NombreProveedor"] = proveedor;
    row["FolioFactura"] = folio;
    row["SubTotal"] = subtotal;
    row["Impuesto"] = impuesto;
    row["Total"] = total;
    invoices.Rows.Add(row);
}

AddInvoice("1", "Alimentos Soles S.A. de C.V.", "05/11/2026", "PROV-00482", "Distribuidora del Noroeste S.A. de C.V.", "F-20481", "$50,000.00", "$8,000.00", "$58,000.00");
AddInvoice("2", "Alimentos Soles S.A. de C.V.", "11/11/2026", "PROV-00482", "Distribuidora del Noroeste S.A. de C.V.", "F-20512", "$29,400.00", "$4,704.00", "$34,104.00");
AddInvoice("3", "Alimentos Soles S.A. de C.V.", "18/11/2026", "PROV-00482", "Distribuidora del Noroeste S.A. de C.V.", "F-20544", "$22,250.00", "$3,560.00", "$25,810.00");

var regionRows = template.ExecuteRegion("FacturasProveedor", invoices);
if (regionRows < 0)
    throw new InvalidOperationException("The FacturasProveedor region was not found in the template.");

template.SetBookmarkIfExists("Total", "117,914.00");

var filled = template.ToBytes();
var filledPath = Path.Combine(samples, "supplier-invoices-sample-filled.fodt");
File.WriteAllBytes(filledPath, filled);

var prawnSharpPdf = PrawnSharpFodtRenderer.Render(filled);
var prawnSharpPdfPath = Path.Combine(samples, "supplier-invoices-sample-prawnsharp.pdf");
File.WriteAllBytes(prawnSharpPdfPath, prawnSharpPdf);

var libreOfficePdf = PrawnSharpFodtRenderer.RenderWithLibreOffice(filled);
var libreOfficePdfPath = Path.Combine(samples, "supplier-invoices-sample-libreoffice.pdf");
File.WriteAllBytes(libreOfficePdfPath, libreOfficePdf);

Console.WriteLine($"Invoice rows:    {regionRows}");
Console.WriteLine($"Filled template: {filledPath}");
Console.WriteLine($"PrawnSharp PDF:    {prawnSharpPdfPath} ({prawnSharpPdf.Length} bytes)");
Console.WriteLine($"LibreOffice PDF: {libreOfficePdfPath} ({libreOfficePdf.Length} bytes)");

static string FindSamplesDirectory()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "FacturasProveedor.fodt")))
            return directory.FullName;

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("FacturasProveedor.fodt was not found.");
}
