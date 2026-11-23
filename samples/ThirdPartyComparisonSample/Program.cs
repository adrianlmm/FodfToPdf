using System.Data;
using FodtToPdf;

// Generates the third-party comparison report both as the untouched template
// and as a version filled with sample comparison rows.
var samples = FindSamplesDirectory();
var templatePath = Path.Combine(samples, "ComparativoTerceros.fodt");
var templateBytes = File.ReadAllBytes(templatePath);

// 1. Template-only PDFs
var templateprawnSharp = PrawnSharpFodtRenderer.Render(templateBytes);
var templateprawnSharpPath = Path.Combine(samples, "comparativo-terceros-template-prawnsharp.pdf");
File.WriteAllBytes(templateprawnSharpPath, templateprawnSharp);

var templateLibreOffice = PrawnSharpFodtRenderer.RenderWithLibreOffice(templateBytes);
var templateLibreOfficePath = Path.Combine(samples, "comparativo-terceros-template-libreoffice.pdf");
File.WriteAllBytes(templateLibreOfficePath, templateLibreOffice);

// 2. Filled PDFs
var template = FodtTemplate.Load(templateBytes);
template.SetBookmarkIfExists("Titulo", "Comparativo de Terceros");
template.SetBookmarkIfExists("Subtitulo1", "Del 01 al 18 de noviembre de 2026");
template.SetBookmarkIfExists("Subtitulo2", "Almacén Soles contra Almacén Tercero");
template.SetBookmarkIfExists("Subtitulo3", "Moneda: MXP");

var rows = new DataTable("DetalleComparativo");
foreach (var column in new[]
{
    "Producto", "Nombre", "KilosTercero", "KilosSurtidos",
    "CajasTercero", "CajasSurtidas", "DiferenciaKilos",
    "DiferenciaCajas", "Observaciones"
})
{
    rows.Columns.Add(column, typeof(string));
}

void AddRow(string producto, string nombre, string kilosTercero, string kilosSurtidos,
            string cajasTercero, string cajasSurtidas, string diferenciaKilos,
            string diferenciaCajas, string observaciones)
{
    var row = rows.NewRow();
    row["Producto"] = producto;
    row["Nombre"] = nombre;
    row["KilosTercero"] = kilosTercero;
    row["KilosSurtidos"] = kilosSurtidos;
    row["CajasTercero"] = cajasTercero;
    row["CajasSurtidas"] = cajasSurtidas;
    row["DiferenciaKilos"] = diferenciaKilos;
    row["DiferenciaCajas"] = diferenciaCajas;
    row["Observaciones"] = observaciones;
    rows.Rows.Add(row);
}

AddRow("ALI-1001", "Alimento balanceado para ganado", "12,450.00", "12,420.00", "420", "418", "-30.00", "-2", "Diferencia por merma");
AddRow("ALI-1002", "Complemento vitamínico", "3,200.00", "3,200.00", "160", "160", "0.00", "0", "Sin diferencias");
AddRow("ALI-1003", "Sal mineral", "5,750.00", "5,730.00", "230", "229", "-20.00", "-1", "Revisar conteo");

var regionRows = template.ExecuteRegion("DetalleComparativo", rows);
if (regionRows < 0)
    throw new InvalidOperationException("The DetalleComparativo region was not found in the template.");

var filled = template.ToBytes();
var filledPath = Path.Combine(samples, "comparativo-terceros-sample-filled.fodt");
File.WriteAllBytes(filledPath, filled);

var prawnSharpFilled = PrawnSharpFodtRenderer.Render(filled);
var prawnSharpFilledPath = Path.Combine(samples, "comparativo-terceros-sample-prawnsharp.pdf");
File.WriteAllBytes(prawnSharpFilledPath, prawnSharpFilled);

var libreOfficeFilled = PrawnSharpFodtRenderer.RenderWithLibreOffice(filled);
var libreOfficeFilledPath = Path.Combine(samples, "comparativo-terceros-sample-libreoffice.pdf");
File.WriteAllBytes(libreOfficeFilledPath, libreOfficeFilled);

Console.WriteLine($"Template PrawnSharp:    {templateprawnSharpPath} ({templateprawnSharp.Length} bytes)");
Console.WriteLine($"Template LibreOffice: {templateLibreOfficePath} ({templateLibreOffice.Length} bytes)");
Console.WriteLine($"Comparison rows: {regionRows}");
Console.WriteLine($"Filled template: {filledPath}");
Console.WriteLine($"Filled PrawnSharp:    {prawnSharpFilledPath} ({prawnSharpFilled.Length} bytes)");
Console.WriteLine($"Filled LibreOffice: {libreOfficeFilledPath} ({libreOfficeFilled.Length} bytes)");

static string FindSamplesDirectory()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "ComparativoTerceros.fodt")))
            return directory.FullName;

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("ComparativoTerceros.fodt was not found.");
}
