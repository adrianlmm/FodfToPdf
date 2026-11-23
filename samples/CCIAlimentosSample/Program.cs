using System.Text;
using System.Xml.Linq;
using FodtToPdf;

// Generates the CCI contract both as the untouched template and with sample
// data. This template contains literal «field» placeholders (no bookmarks or
// database-display fields), so the sample replaces them in the XML text nodes.
var samples = FindSamplesDirectory();
var templatePath = Path.Combine(samples, "CCI_Alimentos Soles S.A. de C.V._CF.fodt");
var templateBytes = File.ReadAllBytes(templatePath);

// 1. Template-only PDFs
var templateprawnSharp = PrawnSharpFodtRenderer.Render(templateBytes);
var templateprawnSharpPath = Path.Combine(samples, "cci-alimentos-template-prawnsharp.pdf");
File.WriteAllBytes(templateprawnSharpPath, templateprawnSharp);

var templateLibreOffice = PrawnSharpFodtRenderer.RenderWithLibreOffice(templateBytes);
var templateLibreOfficePath = Path.Combine(samples, "cci-alimentos-template-libreoffice.pdf");
File.WriteAllBytes(templateLibreOfficePath, templateLibreOffice);

// 2. Filled PDFs. The template uses literal placeholders, not merge fields.
var values = new Dictionary<string, string>
{
    ["Nombre"] = "Juan Carlos Pérez López",
    ["Empresa"] = "Alimentos Soles S.A. de C.V.",
    ["Puesto"] = "Operador de Tractocamión",
    ["RfcEmpresa"] = "ASO0012122H1",
    ["Rfc"] = "PELJ940315AB1",
    ["RegPatronal"] = "E6061707106",
    ["RepresentanteLegal"] = "Julio Antonio Lomelí Macedo",
    ["Rh_Cargo"] = "Gerente de Recursos Humanos",
    ["Direccion"] = "Calle Chihuahua No. 125 Norte, Despacho 3-B, Ciudad Obregón, Sonora",
    ["LugarServicio"] = "Planta Ciudad Obregón",
    ["Edad"] = "29",
    ["Sexo"] = "Masculino",
    ["Ife"] = "0123456789012",
    ["Num_Empleado"] = "EMP-00482",
    ["tipoContrato"] = "Capacitación inicial",
    ["DiaContrato"] = "01",
    ["MesContrato"] = "marzo",
    ["AnioContrato"] = "2026",
    ["NumMes"] = "03",
    ["Salario"] = "450.00",
    ["SueldoMensual"] = "13,500.00",
    ["FormaPago"] = "Transferencia bancaria semanal",
    ["ValeDespensa"] = "1,500.00",
    ["FondoAhorro"] = "500.00",
    ["DiasAguinaldo"] = "15",
    ["DiasEspecificos"] = "6",
    ["HoraEspecifica"] = "08:30",
    ["hora1"] = "08:30",
    ["hora2"] = "17:00",
    ["Testigo1"] = "María Guadalupe Ramírez",
    ["Testigo2"] = "José Luis Hernández",
};

var xml = XDocument.Load(new MemoryStream(templateBytes), LoadOptions.PreserveWhitespace);
var replacements = 0;

foreach (var textNode in xml.Descendants().Nodes().OfType<XText>().ToList())
{
    var text = textNode.Value;
    var original = text;

    foreach (var (field, value) in values)
        text = text.Replace($"«{field}»", value);

    if (text != original)
    {
        textNode.Value = text;
        replacements++;
    }
}

var remaining = xml.Descendants().Nodes().OfType<XText>()
    .Select(x => x.Value)
    .Where(x => x.Contains('«'))
    .ToList();

if (remaining.Count > 0)
    Console.WriteLine($"Warning: {remaining.Count} text nodes still contain «placeholders».");

using var filledStream = new MemoryStream();
xml.Save(filledStream, SaveOptions.DisableFormatting);
var filled = filledStream.ToArray();
var filledPath = Path.Combine(samples, "cci-alimentos-sample-filled.fodt");
File.WriteAllBytes(filledPath, filled);

var filledprawnSharp = PrawnSharpFodtRenderer.Render(filled);
var filledprawnSharpPath = Path.Combine(samples, "cci-alimentos-sample-prawnsharp.pdf");
File.WriteAllBytes(filledprawnSharpPath, filledprawnSharp);

var filledLibreOffice = PrawnSharpFodtRenderer.RenderWithLibreOffice(filled);
var filledLibreOfficePath = Path.Combine(samples, "cci-alimentos-sample-libreoffice.pdf");
File.WriteAllBytes(filledLibreOfficePath, filledLibreOffice);

Console.WriteLine($"Template: {templatePath}");
Console.WriteLine($"Template PrawnSharp:    {templateprawnSharpPath} ({templateprawnSharp.Length} bytes)");
Console.WriteLine($"Template LibreOffice: {templateLibreOfficePath} ({templateLibreOffice.Length} bytes)");
Console.WriteLine($"Replaced text nodes: {replacements}");
Console.WriteLine($"Filled template:     {filledPath}");
Console.WriteLine($"Filled PrawnSharp:     {filledprawnSharpPath} ({filledprawnSharp.Length} bytes)");
Console.WriteLine($"Filled LibreOffice:  {filledLibreOfficePath} ({filledLibreOffice.Length} bytes)");

static string FindSamplesDirectory()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "CCI_Alimentos Soles S.A. de C.V._CF.fodt")))
            return directory.FullName;

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("CCI_Alimentos Soles S.A. de C.V._CF.fodt was not found.");
}
