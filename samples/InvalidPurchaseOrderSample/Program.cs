using System.Data;
using FodtToPdf;

// Generates the invalid purchase-order template both untouched and filled
// with a purchase-order header and repeating detail region.
var samples = FindSamplesDirectory();
var templatePath = Path.Combine(samples, "OrdenDeCompraInvalida.fodt");
var templateBytes = File.ReadAllBytes(templatePath);

// Template-only PDFs
var templateprawnSharp = PrawnSharpFodtRenderer.Render(templateBytes);
var templateprawnSharpPath = Path.Combine(samples, "invalid-purchase-order-template-prawnsharp.pdf");
File.WriteAllBytes(templateprawnSharpPath, templateprawnSharp);

var templateLibreOffice = PrawnSharpFodtRenderer.RenderWithLibreOffice(templateBytes);
var templateLibreOfficePath = Path.Combine(samples, "invalid-purchase-order-template-libreoffice.pdf");
File.WriteAllBytes(templateLibreOfficePath, templateLibreOffice);

// Filled PDFs
var template = FodtTemplate.Load(templateBytes);

var header = new DataTable("OrdenDeCompra");
foreach (var column in new[]
{
    "Empresa", "RFCEmpresa", "DireccionEmpresa", "LocacionEmpresa",
    "Proveedor", "Direccion", "Folio", "IdRequisicion", "Fecha",
    "ClaveProveedor", "RFC", "TipoCompra", "Comentarios",
    "Usuario", "UsuarioAutorizador", "UsuarioRecibe"
})
{
    header.Columns.Add(column, typeof(string));
}

var row = header.NewRow();
row["Empresa"] = "Alimentos Soles S.A. de C.V.";
row["RFCEmpresa"] = "ASO0012122H1";
row["DireccionEmpresa"] = "Calle Chihuahua No. 125 Norte, Despacho 3-B";
row["LocacionEmpresa"] = "Ciudad Obregón, Sonora";
row["Proveedor"] = "Distribuidora del Noroeste S.A. de C.V.";
row["Direccion"] = "Av. Tecnológico No. 450, Parque Industrial";
row["Folio"] = "OC-2026-00127";
row["IdRequisicion"] = "REQ-2026-0091";
row["Fecha"] = "18/11/2026";
row["ClaveProveedor"] = "PROV-00482";
row["RFC"] = "DNO850421AB2";
row["TipoCompra"] = "Compra local";
row["Comentarios"] = "Entrega en almacén central antes del 30 de noviembre.";
row["Usuario"] = "María Guadalupe Ramírez";
row["UsuarioAutorizador"] = "José Luis Hernández";
row["UsuarioRecibe"] = "Carlos Alberto Pérez";
header.Rows.Add(row);

var replaced = template.ExecuteMailMerge(row);

var detail = new DataTable("DetalleOrden");
foreach (var column in new[] { "Producto", "Nombre", "CC", "NombreCC", "Cuenta", "Moneda", "Cantidad", "Precio", "Importe", "Impuesto", "Total" })
    detail.Columns.Add(column, typeof(string));

void AddItem(string producto, string nombre, string cc, string nombreCc, string cuenta,
              string moneda, string cantidad, string precio, string importe, string impuesto, string total)
{
    var item = detail.NewRow();
    item["Producto"] = producto;
    item["Nombre"] = nombre;
    item["CC"] = cc;
    item["NombreCC"] = nombreCc;
    item["Cuenta"] = cuenta;
    item["Moneda"] = moneda;
    item["Cantidad"] = cantidad;
    item["Precio"] = precio;
    item["Importe"] = importe;
    item["Impuesto"] = impuesto;
    item["Total"] = total;
    detail.Rows.Add(item);
}

AddItem("ALI-1001", "Alimento balanceado para ganado", "CC-01", "Producción", "6101-001", "MXP", "40", "1,250.00", "50,000.00", "8,000.00", "58,000.00");
AddItem("ALI-1002", "Complemento vitamínico", "CC-01", "Producción", "6101-002", "MXP", "12", "2,450.00", "29,400.00", "4,704.00", "34,104.00");
AddItem("ALI-1003", "Sal mineral", "CC-02", "Nutrición", "6101-003", "MXP", "25", "890.00", "22,250.00", "3,560.00", "25,810.00");

var regionRows = template.ExecuteRegion("DetalleOrden", detail);
if (regionRows < 0)
    throw new InvalidOperationException("The DetalleOrden region was not found in the template.");

template.SetBookmarkIfExists("Codigo", "OC-2026-00127");

var filled = template.ToBytes();
var filledPath = Path.Combine(samples, "invalid-purchase-order-sample-filled.fodt");
File.WriteAllBytes(filledPath, filled);

var prawnSharpPdf = PrawnSharpFodtRenderer.Render(filled);
var prawnSharpPdfPath = Path.Combine(samples, "invalid-purchase-order-sample-prawnsharp.pdf");
File.WriteAllBytes(prawnSharpPdfPath, prawnSharpPdf);

var libreOfficePdf = PrawnSharpFodtRenderer.RenderWithLibreOffice(filled);
var libreOfficePdfPath = Path.Combine(samples, "invalid-purchase-order-sample-libreoffice.pdf");
File.WriteAllBytes(libreOfficePdfPath, libreOfficePdf);

Console.WriteLine($"Template PrawnSharp:    {templateprawnSharpPath} ({templateprawnSharp.Length} bytes)");
Console.WriteLine($"Template LibreOffice: {templateLibreOfficePath} ({templateLibreOffice.Length} bytes)");
Console.WriteLine($"Header fields replaced: {replaced}");
Console.WriteLine($"Detail rows:           {regionRows}");
Console.WriteLine($"Filled template:        {filledPath}");
Console.WriteLine($"PrawnSharp PDF:           {prawnSharpPdfPath} ({prawnSharpPdf.Length} bytes)");
Console.WriteLine($"LibreOffice PDF:        {libreOfficePdfPath} ({libreOfficePdf.Length} bytes)");

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
