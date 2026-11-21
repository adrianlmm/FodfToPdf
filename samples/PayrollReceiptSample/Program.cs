using System.Data;
using System.Xml.Linq;
using FodtToPdf;

// Generates the payroll CFDI receipt template both untouched and filled with
// sample payroll data.
var samples = FindSamplesDirectory();
var templatePath = Path.Combine(samples, "ComprobantesFiscales.fodt");
var templateBytes = File.ReadAllBytes(templatePath);

// 1. Template-only PDFs
var templateQuest = QuestPdfFodtRenderer.Render(templateBytes);
var templateQuestPath = Path.Combine(samples, "payroll-receipt-template-questpdf.pdf");
File.WriteAllBytes(templateQuestPath, templateQuest);

var templateLibreOffice = QuestPdfFodtRenderer.RenderWithLibreOffice(templateBytes);
var templateLibreOfficePath = Path.Combine(samples, "payroll-receipt-template-libreoffice.pdf");
File.WriteAllBytes(templateLibreOfficePath, templateLibreOffice);

// 2. Filled PDFs
var template = FodtTemplate.Load(templateBytes);
var values = new Dictionary<string, string>
{
    ["NombreEmpresa"] = "CONTRIBUCIONES S.A. de C.V.",
    ["EmisorRFC"] = "FAMH920527HM3",
    ["RegistroIMSS"] = "E60-5126810-1",
    ["TipoComprobante"] = "NÓMINA",
    ["NoEmpleado"] = "189",
    ["NombreEmpleado"] = "FRAGOSO MEZA HEBER FEDERICO",
    ["RFCEmpleado"] = "FAMH920527HK3",
    ["CURPEmpleado"] = "FAMH920527HSRRZB09",
    ["NSSEmpleado"] = "24119268696",
    ["FechaIngresoEmpleado"] = "17/08/2015",
    ["SueldoBaseEmpleado"] = "1,000.00 mensuales",
    ["MetodoPago"] = "Transferencia",
    ["SueldoDiario"] = "100.07",
    ["Puesto"] = "PROGRAMADOR JR",
    ["Periodicidad"] = "CATORCENAL",
    ["EmpresaName"] = "CONTRIBUCIONES Obr",
    ["Departamento"] = "ADMINISTRACION",
    ["Periodo"] = "CAT. #11 del 19 Mayo al 01 Jun 2026",
    ["Folio"] = "31",
    ["Fecha"] = "02/06/2026 07:38:02 p.m.",
    ["TotalPercepciones"] = "1,000.69",
    ["TotalDeducciones"] = "100.69",
    ["NetoPagado"] = "1,000.00",
    ["ImporteConLetra"] = "UN MIL CIENTO DIEZ PESOS 00/100 M.N.",
    ["CuentaVales"] = "208",
    ["ValesDespensa"] = "303.00",
    ["NoCertificadoEmisor"] = "00001000000300698530",
    ["ComprobanteTipo"] = "Egreso",
    ["FolioFiscal"] = "a08c227c-ac55-40ff-8b4b-6066818056f8",
    ["CertificadoSAT"] = "00001000000301634628",
    ["TipoPago"] = "Pago en una sola exhibición",
    ["FechaTimbrado"] = "2026-06-02T19:38:02",
    ["Regimen"] = "Régimen General de Ley Personas Morales",
    ["SelloDigitalEmisor"] = "SAMPLE-EMISOR-SEAL",
    ["SelloDigitalSAT"] = "SAMPLE-SAT-SEAL",
    ["CadenaOriginalSAT"] = "||1.0|a08c227c-ac55-40ff-8b4b-6066818056f8|2026-06-02T19:38:02|sample||",
    ["LugarExpedicion"] = "CD. OBREGON, SONORA",
};

var replaced = 0;
foreach (var (name, value) in values)
{
    if (template.SetBookmarkIfExists(name, value)) replaced++;
    else Console.WriteLine($"Bookmark not found: {name}");
}

var detail = new DataTable("Detalle");
foreach (var column in new[]
{
    "ClavePercepciones", "ConceptoPercepcion", "UnidadPercepcion", "ImportePercepciones",
    "ClaveDeducciones", "ConceptoDeducciones", "ImporteDeducciones", "SaldoActualDeducciones"
})
{
    detail.Columns.Add(column, typeof(string));
}

void AddDetail(string claveP, string conceptoP, string unidadP, string importeP,
               string claveD, string conceptoD, string importeD, string saldoD)
{
    var row = detail.NewRow();
    row["ClavePercepciones"] = claveP;
    row["ConceptoPercepcion"] = conceptoP;
    row["UnidadPercepcion"] = unidadP;
    row["ImportePercepciones"] = importeP;
    row["ClaveDeducciones"] = claveD;
    row["ConceptoDeducciones"] = conceptoD;
    row["ImporteDeducciones"] = importeD;
    row["SaldoActualDeducciones"] = saldoD;
    detail.Rows.Add(row);
}

AddDetail("P001", "Sueldo", "1", "1,000.69", "D001", "ISR", "100.69", "0.00");
AddDetail("P002", "Horas extra", "2", "0.00", "D002", "IMSS", "0.00", "0.00");

var detailRows = template.ExecuteRegion("Detalle", detail);
if (detailRows < 0)
    throw new InvalidOperationException("The Detalle region was not found in the template.");

var filled = template.ToBytes();
var filledPath = Path.Combine(samples, "payroll-receipt-sample-filled.fodt");
File.WriteAllBytes(filledPath, filled);

var questFilled = QuestPdfFodtRenderer.Render(filled);
var questFilledPath = Path.Combine(samples, "payroll-receipt-sample-questpdf.pdf");
File.WriteAllBytes(questFilledPath, questFilled);

var libreOfficeFilled = QuestPdfFodtRenderer.RenderWithLibreOffice(filled);
var libreOfficeFilledPath = Path.Combine(samples, "payroll-receipt-sample-libreoffice.pdf");
File.WriteAllBytes(libreOfficeFilledPath, libreOfficeFilled);

Console.WriteLine($"Template QuestPDF:    {templateQuestPath} ({templateQuest.Length} bytes)");
Console.WriteLine($"Template LibreOffice: {templateLibreOfficePath} ({templateLibreOffice.Length} bytes)");
Console.WriteLine($"Bookmarks replaced: {replaced}");
Console.WriteLine($"Detail rows: {detailRows}");
Console.WriteLine($"Filled template: {filledPath}");
Console.WriteLine($"Filled QuestPDF:    {questFilledPath} ({questFilled.Length} bytes)");
Console.WriteLine($"Filled LibreOffice: {libreOfficeFilledPath} ({libreOfficeFilled.Length} bytes)");

static string FindSamplesDirectory()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "ComprobantesFiscales.fodt")))
            return directory.FullName;

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("ComprobantesFiscales.fodt was not found.");
}
