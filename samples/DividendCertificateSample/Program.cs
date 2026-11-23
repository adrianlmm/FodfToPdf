using FodtToPdf;

// Fills the Mexican dividend withholding certificate template and exports
// both PDF backends. The template uses bookmarks, not repeating regions.
var samples = FindSamplesDirectory();
var template = FodtTemplate.Load(Path.Combine(samples, "formatoDividendos.fodt"));

var values = new Dictionary<string, string>
{
    ["folioFiscal"] = "0DF2065B-404C-42D7-A081-7877513F6EB5",
    ["nacionalidadReceptor"] = "Nacional",
    ["numSerieCertificadoSAT"] = "00001000000404594081",
    ["periodoPago"] = "Diciembre 2026",
    ["folioSerie"] = "000002RET",
    ["CveNombreRetencion"] = "14 Dividendos o utilidades distribuidas",
    ["fechaEmision"] = "2026-11-18T10:00:00",
    ["fechaCertificacion"] = "2026-11-18T10:00:00",
    ["numCertificadoEmisor"] = "00001000000404108700",
    ["rfcEmisor"] = "DIP900505JN2",
    ["nombreEmisor"] = "DESARROLLO INDUSTRIAL PROGRESO SA DE CV",
    ["rfcReceptor"] = "FUTM520601EC1",
    ["nombreReceptor"] = "MARCELA IVONNE FUENTES TERRAZAS",
    ["tipoPago"] = "Pago provisional",
    ["montoBaseImpuesto"] = "246,559.00",
    ["montoIsr_acreditable"] = "73,968.00",
    ["montoTotRet"] = "73,968.00",
    ["cveTipoDividendo"] = "06 - Proviene de CUFIN al 31 de diciembre 2013",
    ["sociedadDividendo"] = "Sociedad Nacional",
    ["montoIsrAcredMexico"] = "172,588.00",
    ["montoIsrAcredNal"] = "73,968.00",
    ["montoIsrAcredExtranjero"] = "0.00",
    ["montoDivAcumNal"] = "246,559.00",
    ["porcentajeParticipacion"] = "0.0",
    ["montoRetExtDivExt"] = "0.00",
    ["montoTotOperacion"] = "246,559.00",
    ["montoTotExent"] = "0.00",
    ["montoTotGrav"] = "246,559.00",
    ["montoTotalRetenido"] = "73,968.00",
    ["cadenaOriginalComplementoSAT"] = "||1.0|0DF2065B-404C-42D7-A081-7877513F6EB5|2026-11-18T10:00:00|sample||",
    ["selloDigitalCFDI"] = "SAMPLE-CFDI-SEAL",
    ["selloDigitalSAT"] = "SAMPLE-SAT-SEAL",
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
var filledPath = Path.Combine(samples, "dividend-certificate-sample-filled.fodt");
File.WriteAllBytes(filledPath, filled);

var prawnSharpPdf = PrawnSharpFodtRenderer.Render(filled);
var prawnSharpPdfPath = Path.Combine(samples, "dividend-certificate-sample-prawnsharp.pdf");
File.WriteAllBytes(prawnSharpPdfPath, prawnSharpPdf);

var libreOfficePdf = PrawnSharpFodtRenderer.RenderWithLibreOffice(filled);
var libreOfficePdfPath = Path.Combine(samples, "dividend-certificate-sample-libreoffice.pdf");
File.WriteAllBytes(libreOfficePdfPath, libreOfficePdf);

Console.WriteLine($"Bookmarks replaced: {replaced}");
Console.WriteLine($"Filled template: {filledPath}");
Console.WriteLine($"PrawnSharp PDF:    {prawnSharpPdfPath} ({prawnSharpPdf.Length} bytes)");
Console.WriteLine($"LibreOffice PDF: {libreOfficePdfPath} ({libreOfficePdf.Length} bytes)");

static string FindSamplesDirectory()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "formatoDividendos.fodt")))
            return directory.FullName;

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("formatoDividendos.fodt was not found.");
}
