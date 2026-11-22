using System.Data;
using FodtToPdf;

// Generates the payment receipt/complement template both untouched and filled
// with sample payment data, including the TblPagos detail region.
var samples = FindSamplesDirectory();
var templatePath = Path.Combine(samples, "PlantillaComprobantePago.fodt");
byte[] templateBytes;
using (var stream = new FileStream(templatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
using (var memory = new MemoryStream())
{
    stream.CopyTo(memory);
    templateBytes = memory.ToArray();
}

// 1. Template-only PDFs
var templateQuest = QuestPdfFodtRenderer.Render(templateBytes);
var templateQuestPath = Path.Combine(samples, "payment-complement-template-questpdf.pdf");
File.WriteAllBytes(templateQuestPath, templateQuest);

var templateLibreOffice = QuestPdfFodtRenderer.RenderWithLibreOffice(templateBytes);
var templateLibreOfficePath = Path.Combine(samples, "payment-complement-template-libreoffice.pdf");
File.WriteAllBytes(templateLibreOfficePath, templateLibreOffice);

var templatePrawnSharp = PrawnSharpFodtRenderer.Render(templateBytes);
var templatePrawnSharpPath = Path.Combine(samples, "payment-complement-template-prawnsharp.pdf");
File.WriteAllBytes(templatePrawnSharpPath, templatePrawnSharp);

// 2. Filled PDFs
var template = FodtTemplate.Load(templateBytes);
var values = new Dictionary<string, string>
{
    ["Emisor_Nombre"] = "ALIMENTOS SOLES S.A. de C.V.",
    ["Emisor_RFC"] = "ASO0012122H1",
    ["Emisor_RegimenFiscal"] = "601 - General de Ley Personas Morales",
    ["regimen_Fiscal_Empresa"] = "601 - General de Ley Personas Morales",
    ["Receptor_Nombre"] = "DISTRIBUIDORA DEL NOROESTE S.A. de C.V.",
    ["Receptor_Rfc"] = "DNO850421AB2",
    ["Receptor_UsoCFDI"] = "G01 - Adquisición de mercancías",
    ["Direccion_Empresa"] = "Calle Chihuahua No. 125 Norte, Despacho 3-B, Centro, Ciudad Obregón, Sonora, C.P. 85000",
    ["Direccion_Cliente"] = "Av. Tecnológico No. 450, Parque Industrial, Ciudad Obregón, Sonora, C.P. 85000",
    ["Comprobante_Folio"] = "PAG-2026-00482",
    ["Comprobante_Fecha"] = "2026-11-22T10:30:00",
    ["Comprobante_LugarExpedicion"] = "85000",
    ["Comprobante_NoCertificado"] = "00001000000404108700",
    ["Comprobante_Certificado"] = "SAMPLE-CERTIFICATE",
    ["Complemento_Monto"] = "34,500.00",
    ["Complemento_FechaPago"] = "2026-11-22T10:30:00",
    ["Complemento_FormaDePagoP"] = "03 - Transferencia electrónica de fondos",
    ["Complemento_NumOperacion"] = "OP-2026-00482",
    ["Complemento_RfcEmisorCtaBen"] = "ASO0012122H1",
    ["Complemento_RfcEmisorCtaOrd"] = "DNO850421AB2",
    ["Complemento_CtaBeneficiario"] = "012345678901234567",
    ["Complemento_CtaOrdenante"] = "987654321098765432",
    ["TimbreFiscal_UUID"] = "0DF2065B-404C-42D7-A081-7877513F6EB5",
    ["TimbreFiscal_FechaTimbrado"] = "2026-11-22T10:31:00",
    ["TimbreFiscal_RfcProvCertif"] = "XAXAXAXAXAXAXAXA",
    ["TimbreFiscal_NoCertificadoSAT"] = "00001000000202639096",
    ["TimbreFiscal_SelloCFD"] = "SAMPLE-CFD-SEAL",
    ["TimbreFiscal_SelloSAT"] = "SAMPLE-SAT-SEAL",
    ["Concepto_ClaveProdServ"] = "30102300",
    ["Concepto_ClaveUnidad"] = "KGM",
    ["Concepto_Descripcion"] = "Lámina de acero",
    ["Concepto_ValorUnitario"] = "25.00",
    ["CadenaOriginal"] = "||1.0|sample-payment-complement||",
};

var replaced = 0;
foreach (var (name, value) in values)
{
    if (template.SetBookmarkIfExists(name, value)) replaced++;
    else Console.WriteLine($"Bookmark not found: {name}");
}

var payments = new DataTable("TblPagos");
foreach (var column in new[]
{
    "Pagos_Folio", "Pagos_IdDocumento", "Pagos_ImpPagado",
    "Pagos_ImpSaldoAnt", "Pagos_ImpSaldoInsoluto",
    "Pagos_MetodoDePagoDR", "Pagos_MonedaDR"
})
{
    payments.Columns.Add(column, typeof(string));
}

void AddPayment(string folio, string id, string pagado, string saldoAnt, string insoluto)
{
    var row = payments.NewRow();
    row["Pagos_Folio"] = folio;
    row["Pagos_IdDocumento"] = id;
    row["Pagos_ImpPagado"] = pagado;
    row["Pagos_ImpSaldoAnt"] = saldoAnt;
    row["Pagos_ImpSaldoInsoluto"] = insoluto;
    row["Pagos_MetodoDePagoDR"] = "PUE";
    row["Pagos_MonedaDR"] = "MXN";
    payments.Rows.Add(row);
}

AddPayment("F-20481", "F-20481", "15,000.00", "15,000.00", "0.00");
AddPayment("F-20512", "F-20512", "10,000.00", "10,000.00", "0.00");
AddPayment("F-20544", "F-20544", "9,500.00", "9,500.00", "0.00");

var regionRows = template.ExecuteRegion("TblPagos", payments);
if (regionRows < 0)
    throw new InvalidOperationException("The TblPagos region was not found in the template.");

var filled = template.ToBytes();
var filledPath = Path.Combine(samples, "payment-complement-sample-filled.fodt");
File.WriteAllBytes(filledPath, filled);

var questFilled = QuestPdfFodtRenderer.Render(filled);
var questFilledPath = Path.Combine(samples, "payment-complement-sample-questpdf.pdf");
File.WriteAllBytes(questFilledPath, questFilled);

var libreOfficeFilled = QuestPdfFodtRenderer.RenderWithLibreOffice(filled);
var libreOfficeFilledPath = Path.Combine(samples, "payment-complement-sample-libreoffice.pdf");
File.WriteAllBytes(libreOfficeFilledPath, libreOfficeFilled);

var prawnSharpFilled = PrawnSharpFodtRenderer.Render(filled);
var prawnSharpFilledPath = Path.Combine(samples, "payment-complement-sample-prawnsharp.pdf");
File.WriteAllBytes(prawnSharpFilledPath, prawnSharpFilled);

Console.WriteLine($"Template QuestPDF:    {templateQuestPath} ({templateQuest.Length} bytes)");
Console.WriteLine($"Template LibreOffice: {templateLibreOfficePath} ({templateLibreOffice.Length} bytes)");
Console.WriteLine($"Template PrawnSharp:  {templatePrawnSharpPath} ({templatePrawnSharp.Length} bytes)");
Console.WriteLine($"Bookmarks replaced: {replaced}");
Console.WriteLine($"Payment rows: {regionRows}");
Console.WriteLine($"Filled template: {filledPath}");
Console.WriteLine($"Filled QuestPDF:    {questFilledPath} ({questFilled.Length} bytes)");
Console.WriteLine($"Filled LibreOffice: {libreOfficeFilledPath} ({libreOfficeFilled.Length} bytes)");
Console.WriteLine($"Filled PrawnSharp:  {prawnSharpFilledPath} ({prawnSharpFilled.Length} bytes)");

static string FindSamplesDirectory()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "PlantillaComprobantePago.fodt")))
            return directory.FullName;

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("PlantillaComprobantePago.fodt was not found.");
}
