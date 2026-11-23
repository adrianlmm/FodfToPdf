using FodtToPdf;

// Fills the IMSS daycare letter template with sample employer and worker data
// and exports both PDF backends.
var samples = FindSamplesDirectory();
var template = FodtTemplate.Load(Path.Combine(samples, "CartaGuarderia.fodt"));

var values = new Dictionary<string, string>
{
    ["NombreEmpresa"] = "ALIMENTOS SOLES SA DE CV",
    ["NombreEmpresaG"] = "Asistencia Eficaz S.A. de C.V.",
    ["RegistroPatronal"] = "E6061707106",
    ["antiguedad"] = "8 AÑOS, 248 DÍAS",
    ["dia_descanso"] = "DOMINGO",
    ["fecha_documento"] = "18 de Noviembre del 2026",
    ["horario"] = "DE 8:30 AM A 5:00 PM",
    ["nombre_guarderia"] = "Guardería Pininos II",
    ["nombre_jefe"] = "ANA DELIA LEYVA",
    ["nombre_trabajador"] = "CLAUDIA ZAZIL MADRID GAMEZ",
    ["numero_afiliacion"] = "24058223132",
    ["periodo_vacacional"] = "14",
    ["puesto"] = "GESTIÓN DE PAGOS PROVEEDORES",
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
var filledPath = Path.Combine(samples, "daycare-letter-sample-filled.fodt");
File.WriteAllBytes(filledPath, filled);

var prawnSharpPdf = PrawnSharpFodtRenderer.Render(filled);
var prawnSharpPdfPath = Path.Combine(samples, "daycare-letter-sample-prawnsharp.pdf");
File.WriteAllBytes(prawnSharpPdfPath, prawnSharpPdf);

var libreOfficePdf = PrawnSharpFodtRenderer.RenderWithLibreOffice(filled);
var libreOfficePdfPath = Path.Combine(samples, "daycare-letter-sample-libreoffice.pdf");
File.WriteAllBytes(libreOfficePdfPath, libreOfficePdf);

Console.WriteLine($"Bookmarks replaced: {replaced}");
Console.WriteLine($"Filled template:    {filledPath}");
Console.WriteLine($"PrawnSharp PDF:       {prawnSharpPdfPath} ({prawnSharpPdf.Length} bytes)");
Console.WriteLine($"LibreOffice PDF:    {libreOfficePdfPath} ({libreOfficePdf.Length} bytes)");

static string FindSamplesDirectory()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "CartaGuarderia.fodt")))
            return directory.FullName;

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("CartaGuarderia.fodt was not found.");
}
