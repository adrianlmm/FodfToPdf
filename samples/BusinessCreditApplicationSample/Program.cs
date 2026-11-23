using System.Data;
using System.Xml.Linq;
using FodtToPdf;

// Generates the SC_PMoral credit application both as the untouched template
// and with sample values for every database-display field.
var samples = FindSamplesDirectory();
var templatePath = Path.Combine(samples, "SC_PMoral.fodt");
var templateBytes = File.ReadAllBytes(templatePath);

// 1. Template-only PDFs
var templateprawnSharp = PrawnSharpFodtRenderer.Render(templateBytes);
var templateprawnSharpPath = Path.Combine(samples, "sc-pmoral-template-prawnsharp.pdf");
File.WriteAllBytes(templateprawnSharpPath, templateprawnSharp);

var templateLibreOffice = PrawnSharpFodtRenderer.RenderWithLibreOffice(templateBytes);
var templateLibreOfficePath = Path.Combine(samples, "sc-pmoral-template-libreoffice.pdf");
File.WriteAllBytes(templateLibreOfficePath, templateLibreOffice);

// 2. Filled PDFs. Discover all database-display fields from the template.
XNamespace text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
var fields = XDocument.Load(templatePath)
    .Descendants(text + "database-display")
    .Select(x => (string?)x.Attribute(text + "column-name"))
    .Where(x => !string.IsNullOrWhiteSpace(x) &&
                !x.StartsWith("TableStart:", StringComparison.Ordinal) &&
                !x.StartsWith("TableEnd:", StringComparison.Ordinal))
    .Distinct()
    .OrderBy(x => x)
    .ToList();

var data = new DataTable("SolicitudCredito");
foreach (var field in fields)
    data.Columns.Add(field, typeof(string));

var row = data.NewRow();
foreach (var field in fields)
    row[field] = SampleValue(field);
data.Rows.Add(row);

var template = FodtTemplate.Load(templateBytes);
var replaced = template.ExecuteMailMerge(row);
if (replaced == 0)
    throw new InvalidOperationException("The SC_PMoral template did not contain replaceable fields.");

var filled = template.ToBytes();
var filledPath = Path.Combine(samples, "sc-pmoral-sample-filled.fodt");
File.WriteAllBytes(filledPath, filled);

var prawnSharpFilled = PrawnSharpFodtRenderer.Render(filled);
var prawnSharpFilledPath = Path.Combine(samples, "sc-pmoral-sample-prawnsharp.pdf");
File.WriteAllBytes(prawnSharpFilledPath, prawnSharpFilled);

var libreOfficeFilled = PrawnSharpFodtRenderer.RenderWithLibreOffice(filled);
var libreOfficeFilledPath = Path.Combine(samples, "sc-pmoral-sample-libreoffice.pdf");
File.WriteAllBytes(libreOfficeFilledPath, libreOfficeFilled);

Console.WriteLine($"Fields discovered: {fields.Count}");
Console.WriteLine($"Fields replaced:   {replaced}");
Console.WriteLine($"Template PrawnSharp:    {templateprawnSharpPath} ({templateprawnSharp.Length} bytes)");
Console.WriteLine($"Template LibreOffice: {templateLibreOfficePath} ({templateLibreOffice.Length} bytes)");
Console.WriteLine($"Filled template: {filledPath}");
Console.WriteLine($"Filled PrawnSharp:    {prawnSharpFilledPath} ({prawnSharpFilled.Length} bytes)");
Console.WriteLine($"Filled LibreOffice: {libreOfficeFilledPath} ({libreOfficeFilled.Length} bytes)");

static string SampleValue(string field) => field switch
{
    "CardName" or "NombreV" or "RepresentanteL" or "NomAval" or "NomCony" => "Juan Carlos Pérez López",
    "CardCode" => "C-000482",
    "RFCC" or "RFCA" => "ASO0012122H1",
    "Calle" or "CalleA" => "Calle Chihuahua No. 125 Norte",
    "Colonia" or "ColoniaA" => "Centro",
    "CP" or "CPA" => "85000",
    "City" or "CdA" => "Ciudad Obregón",
    "Estado" or "EdoA" or "EdoL" or "EdoS" => "Sonora",
    "Email" or "EmailA" => "contacto@example.com",
    "Fecha" or "FechaA" => "19/11/2026",
    "Monto" or "SID" or "SIA" or "Salario" => "1,000.00",
    "DiasCred" => "30",
    "Ocupacion" => "Comercio",
    "ObjetoSocial" => "Compraventa de productos alimenticios",
    "EdoCivil" => "Soltero",
    "Distribuidor" => "Distribuidor autorizado",
    "NomInst" => "Institución bancaria",
    "Parentesco" or "ParentescoRP1" or "ParentescoRP2" or "ParentescoRP3" => "Familiar",
    "NombreRP1" => "María Guadalupe Ramírez",
    "NombreRP2" => "José Luis Hernández",
    "NombreRP3" => "Ana Delia Leyva",
    "Banco1" or "Banco2" or "Banco3" => "Banco de ejemplo",
    "TipoC1" or "TipoC2" or "TipoC3" => "Cuenta de cheques",
    "Ant1" or "Ant2" or "Ant3" => "5 años",
    "No" or "NoA" or "NoD" => "125",
    "TEL1" or "TEL2" or "TEL3" or "Tel1A" or "Tel2A" or "Tel1RC" or "Tel2RC" or "Tel3RC" or "Tel4RC" or "TelefonoRP1" or "TelefonoRP2" or "TelefonoRP3" => "644-410-7900",
    "Bien1" or "Bien2" or "Bien3" or "Bien4" or "Bien5" => "Bien inmueble de ejemplo",
    "Regis1" or "Regis2" or "Regis3" or "Regis4" or "Regis5" => "Registro Público de la Propiedad",
    "Vol1" or "Vol2" or "Vol3" or "Vol4" or "Vol5" => "1",
    "Libro1" or "Libro2" or "Libro3" or "Libro4" or "Libro5" => "1",
    "LG1" or "LG2" or "LG3" or "LG4" or "LG5" => "Libre",
    "Empresa1" or "Empresa2" or "Empresa3" or "Empresa4" => "Empresa de ejemplo S.A. de C.V.",
    "PerContacto1" or "PerContacto2" or "PerContacto3" or "PerContacto4" => "Contacto de ejemplo",
    "Direccion1" or "Direccion2" or "Direccion3" or "Direccion4" => "Dirección de ejemplo",
    "DireccionRP1" or "DireccionRP2" or "DireccionRP3" => "Dirección de referencia",
    _ => "Dato de ejemplo",
};

static string FindSamplesDirectory()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "SC_PMoral.fodt")))
            return directory.FullName;

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("SC_PMoral.fodt was not found.");
}
