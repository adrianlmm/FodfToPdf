using FodtToPdf;

// Generates the interest withholding certificate both untouched and filled
// with sample data.
var samples = FindSamplesDirectory();
var templatePath = Path.Combine(samples, "PlantillaIntereses.fodt");
var templateBytes = File.ReadAllBytes(templatePath);

// 1. Template-only PDFs
var templateprawnSharp = PrawnSharpFodtRenderer.Render(templateBytes);
var templateprawnSharpPath = Path.Combine(samples, "interest-certificate-template-prawnsharp.pdf");
File.WriteAllBytes(templateprawnSharpPath, templateprawnSharp);

var templateLibreOffice = PrawnSharpFodtRenderer.RenderWithLibreOffice(templateBytes);
var templateLibreOfficePath = Path.Combine(samples, "interest-certificate-template-libreoffice.pdf");
File.WriteAllBytes(templateLibreOfficePath, templateLibreOffice);

// 2. Filled PDFs
var template = FodtTemplate.Load(templateBytes);
var values = new Dictionary<string, string>
{
    ["emisor"] = "ALIMENTOS SOLES SA DE CV",
    ["rfc_emisor"] = "ASO0012122H1",
    ["folio"] = "INT-2026-00482",
    ["fecha"] = "19/11/2026",
    ["hora"] = "10:30",
    ["receptor"] = "MARCELA IVONNE FUENTES TERRAZAS",
    ["rfc_receptor"] = "FUTM520601EC1",
    ["periodo_inicial"] = "Enero",
    ["periodo_final"] = "Diciembre",
    ["ejercicio"] = "2026",
    ["importe_base"] = "2,082,549.00",
    ["tipo_impuesto"] = "ISR",
    ["impuesto_retenido"] = "301,970.00",
    ["tipo_retencion"] = "Pago provisional",
    ["sist_financiero"] = "NO",
    ["retiro_aores"] = "NO",
    ["oper_financ"] = "NO",
    ["interes_nominal"] = "2,082,549.00",
    ["intereses_reales"] = "0.00",
    ["total_operaciones"] = "2,082,549.00",
    ["total_retenciones"] = "301,970.00",
    ["perdida"] = "1,285,975.00",
    ["importe_letra"] = "TRESCIENTOS UN MIL NOVECIENTOS SETENTA PESOS 00/100 M.N.",
    ["numSerieCertificadoSAT"] = "00001000000202639096",
    ["numCertificadoEmisor"] = "00001000000203615487",
    ["fechaCertificacion"] = "2026-11-19T10:31:00",
    ["folioFiscal"] = "0DF2065B-404C-42D7-A081-7877513F6EB5",
    ["selloDigitalCFDI"] = "SAMPLE-CFDI-SEAL",
    ["selloDigitalSAT"] = "SAMPLE-SAT-SEAL",
    ["cadenaOriginalComplementoSAT"] = "||1.0|sample-interest-certificate||",
    ["QRCode"] = "QR",
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
var filledPath = Path.Combine(samples, "interest-certificate-sample-filled.fodt");
File.WriteAllBytes(filledPath, filled);

var prawnSharpFilled = PrawnSharpFodtRenderer.Render(filled);
var prawnSharpFilledPath = Path.Combine(samples, "interest-certificate-sample-prawnsharp.pdf");
File.WriteAllBytes(prawnSharpFilledPath, prawnSharpFilled);

var libreOfficeFilled = PrawnSharpFodtRenderer.RenderWithLibreOffice(filled);
var libreOfficeFilledPath = Path.Combine(samples, "interest-certificate-sample-libreoffice.pdf");
File.WriteAllBytes(libreOfficeFilledPath, libreOfficeFilled);

Console.WriteLine($"Template PrawnSharp:    {templateprawnSharpPath} ({templateprawnSharp.Length} bytes)");
Console.WriteLine($"Template LibreOffice: {templateLibreOfficePath} ({templateLibreOffice.Length} bytes)");
Console.WriteLine($"Bookmarks replaced: {replaced}");
Console.WriteLine($"Filled template: {filledPath}");
Console.WriteLine($"Filled PrawnSharp:    {prawnSharpFilledPath} ({prawnSharpFilled.Length} bytes)");
Console.WriteLine($"Filled LibreOffice: {libreOfficeFilledPath} ({libreOfficeFilled.Length} bytes)");

static string FindSamplesDirectory()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "PlantillaIntereses.fodt")))
            return directory.FullName;

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("PlantillaIntereses.fodt was not found.");
}
