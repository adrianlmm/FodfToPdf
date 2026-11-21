using System.Data;
using System.Xml.Linq;
using FodtToPdf;

// Generates the carta porte template both untouched and filled with sample
// header bookmarks and three repeating regions.
var samples = FindSamplesDirectory();
var templatePath = Path.Combine(samples, "PlantillaSolicitudEntregaCartaPorte_V3-3.fodt");
var templateBytes = File.ReadAllBytes(templatePath);

// 1. Template-only PDFs
var templateQuest = QuestPdfFodtRenderer.Render(templateBytes);
var templateQuestPath = Path.Combine(samples, "carta-porte-template-questpdf.pdf");
File.WriteAllBytes(templateQuestPath, templateQuest);

var templateLibreOffice = QuestPdfFodtRenderer.RenderWithLibreOffice(templateBytes);
var templateLibreOfficePath = Path.Combine(samples, "carta-porte-template-libreoffice.pdf");
File.WriteAllBytes(templateLibreOfficePath, templateLibreOffice);

// 2. Filled PDFs
var template = FodtTemplate.Load(templateBytes);
XNamespace text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
var bookmarks = XDocument.Load(templatePath)
    .Descendants(text + "bookmark-start")
    .Select(x => (string?)x.Attribute(text + "name"))
    .Where(x => !string.IsNullOrWhiteSpace(x))
    .Distinct()
    .ToList();

var bookmarkCount = 0;
foreach (var bookmark in bookmarks)
{
    if (bookmark is "logotipo" or "QRCode" or "qrcode") continue;
    if (template.SetBookmarkIfExists(bookmark, SampleValue(bookmark))) bookmarkCount++;
}

var mercancias = new DataTable("DetalleMercancia");
foreach (var column in new[] { "Mercancia", "BienesTransportados", "ClaveUnidad", "Cantidad", "PesoEnKG" })
    mercancias.Columns.Add(column, typeof(string));
AddRow(mercancias, "Acero", "Lámina de acero", "KGM", "1,200", "1,200");
AddRow(mercancias, "Plástico", "Tarimas de plástico", "H87", "25", "375");

var figuras = new DataTable("FigurasTransporte");
foreach (var column in new[] { "Nombre", "RFC", "Licencia" })
    figuras.Columns.Add(column, typeof(string));
AddRow(figuras, "Juan Carlos Pérez López", "PELJ940315AB1", "LIC-2026-00482");
AddRow(figuras, "José Luis Hernández", "HELJ900101AB2", "LIC-2026-00483");

var detalle = new DataTable("DetalleInvoice");
foreach (var column in new[] { "Cantidad", "UM", "Clave", "Descripcion", "claveProductoServicio", "claveUM", "NoPaquetes", "Precio", "Total" })
    detalle.Columns.Add(column, typeof(string));
AddRow(detalle, "1,200", "KGM", "ACERO-01", "Lámina de acero", "30102300", "KGM", "12", "25.00", "30,000.00");
AddRow(detalle, "25", "H87", "PLAST-02", "Tarimas de plástico", "24112400", "H87", "25", "180.00", "4,500.00");

var mercanciaRows = template.ExecuteRegion("DetalleMercancia", mercancias);
var figuraRows = template.ExecuteRegion("FigurasTransporte", figuras);
var invoiceRows = template.ExecuteRegion("DetalleInvoice", detalle);

var filled = template.ToBytes();
var filledPath = Path.Combine(samples, "carta-porte-sample-filled.fodt");
File.WriteAllBytes(filledPath, filled);

var questFilled = QuestPdfFodtRenderer.Render(filled);
var questFilledPath = Path.Combine(samples, "carta-porte-sample-questpdf.pdf");
File.WriteAllBytes(questFilledPath, questFilled);

var libreOfficeFilled = QuestPdfFodtRenderer.RenderWithLibreOffice(filled);
var libreOfficeFilledPath = Path.Combine(samples, "carta-porte-sample-libreoffice.pdf");
File.WriteAllBytes(libreOfficeFilledPath, libreOfficeFilled);

Console.WriteLine($"Bookmarks filled: {bookmarkCount}");
Console.WriteLine($"Mercancía rows: {mercanciaRows}; figuras: {figuraRows}; invoice: {invoiceRows}");
Console.WriteLine($"Template QuestPDF:    {templateQuestPath} ({templateQuest.Length} bytes)");
Console.WriteLine($"Template LibreOffice: {templateLibreOfficePath} ({templateLibreOffice.Length} bytes)");
Console.WriteLine($"Filled template: {filledPath}");
Console.WriteLine($"Filled QuestPDF:    {questFilledPath} ({questFilled.Length} bytes)");
Console.WriteLine($"Filled LibreOffice: {libreOfficeFilledPath} ({libreOfficeFilled.Length} bytes)");

static void AddRow(DataTable table, params string[] values)
{
    var row = table.NewRow();
    for (var i = 0; i < values.Length; i++) row[i] = values[i];
    table.Rows.Add(row);
}

static string SampleValue(string field) => field switch
{
    "NombreLargoEmpresa" => "Alimentos Soles S.A. de C.V.",
    "nombreCliente" => "Distribuidora del Noroeste S.A. de C.V.",
    "RFCEmpresa" => "ASO0012122H1",
    "RFCCliente" => "DNO850421AB2",
    "rfcUbicacion2" => "DNO850421AB2",
    "rcfUbicacion1" => "ASO0012122H1",
    "nombreUbicacion1" => "Almacén Central Soles",
    "nombreUbicacion2" => "Bodega del Cliente",
    "domicilioUbicacion2" => "Av. Tecnológico No. 450, Parque Industrial",
    "direccionEmpresa" => "Calle Chihuahua No. 125 Norte, Despacho 3-B",
    "direccionEmpresa Copia 1" => "Calle Chihuahua No. 125 Norte, Despacho 3-B",
    "telefonoEmpresa" or "telefonoEmpresa Copia 1" => "644-410-7900",
    "correoEmpresa" or "correoEmpresa Copia 1" => "facturacion@soles.com.mx",
    "codigoCliente" => "C-000482",
    "codigoLugarExpedicionEmpresa" => "85000",
    "nombreSerie" => "CP",
    "versionCP" => "2.0",
    "fechaEmision" or "fechaTimbrado" or "fechavHoraCP1" or "fechavHoraCP2" => "19/11/2026 10:30",
    "tipoDocumento" => "Solicitud de entrega",
    "doctoSAP" => "CP-2026-00482",
    "documentoElectronico" => "CFDI de traslado",
    "usoCFDI" => "G01 - Adquisición de mercancías",
    "regimenFiscal" or "regimenCliente" or "regimenFiscalEmpresa" => "601 - General de Ley Personas Morales",
    "numeroRegimenFiscalEmpresa" => "601",
    "lugarExpedicion" => "Ciudad Obregón, Sonora",
    "metodoPago33" => "PUE - Pago en una sola exhibición",
    "tipoMetodoPago" => "PUE",
    "tipoPago" => "Transferencia",
    "condicionPago" => "Contado",
    "tipoUbicacion1" or "tipoUbicacion2" => "Origen/Destino",
    "distanciaRecorridaUbicacion2" => "12.5 km",
    "PesoBrutoTotalCP" => "1,575.00",
    "UnidadPesoCP" => "KGM",
    "NumeroTotalMercanciasCP" => "2",
    "PermSCT" => "TPAF01",
    "NumPermisoSCT" => "SCT-2026-00482",
    "ConfigVehicular" or "ConfigVehiculiar" => "C2",
    "PlacaVM" => "VBR-2026-A",
    "AnoModeloVM" => "2024",
    "AsegRespCivil" => "Seguros del Norte",
    "NumPolizaSeguro" => "POL-2026-99881",
    "subTotal" => "34,500.00",
    "IVA" => "0.00",
    "total" => "34,500.00",
    "totalDRCP" => "12.5",
    "transporteIntCP" => "No",
    "tipoIVA" => "0%",
    "retencion" => "0.00",
    "importeConLetra" => "TREINTA Y CUATRO MIL QUINIENTOS PESOS 00/100 M.N.",
    "InformacionAdicional" or "comentarios" => "Entrega en horario de 9:00 a 17:00.",
    "noCertificado" => "00001000000404108700",
    "numeroSerieCertificadoSAT" => "00001000000404594081",
    "selloDigitalCFDI" => "SAMPLE-CFDI-SEAL",
    "selloDigitalSAT" => "SAMPLE-SAT-SEAL",
    "cadenaOriginalComplementoSAT" => "||1.0|sample-carta-porte||",
    _ => "Dato de ejemplo",
};

static string FindSamplesDirectory()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "PlantillaSolicitudEntregaCartaPorte_V3-3.fodt")))
            return directory.FullName;

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("PlantillaSolicitudEntregaCartaPorte_V3-3.fodt was not found.");
}
