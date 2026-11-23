using System.Data;
using FodtToPdf;

// Fills the employee exit-interview template with fictional data and exports
// both PDF backends. All content is stored in database-display fields.
var samples = FindSamplesDirectory();
var templatePath = Path.Combine(samples, "EntrevistaSalida.fodt");
var templateBytes = File.ReadAllBytes(templatePath);

// Template-only PDFs
var templateprawnSharp = PrawnSharpFodtRenderer.Render(templateBytes);
var templateprawnSharpPath = Path.Combine(samples, "exit-interview-template-prawnsharp.pdf");
File.WriteAllBytes(templateprawnSharpPath, templateprawnSharp);

var templateLibreOffice = PrawnSharpFodtRenderer.RenderWithLibreOffice(templateBytes);
var templateLibreOfficePath = Path.Combine(samples, "exit-interview-template-libreoffice.pdf");
File.WriteAllBytes(templateLibreOfficePath, templateLibreOffice);

// Filled PDFs
var template = FodtTemplate.Load(templateBytes);

var data = new DataTable("Entrevista");
foreach (var column in new[]
{
    "Empresa", "Area", "Entrevistador", "FechaAct", "FechaIngreso", "FechaEgreso",
    "Nombre", "Puesto", "JefeI", "NombreJefeI", "Otro", "Razon",
    "Coment1", "Coment2", "Coment3", "Coment4", "Coment5", "Coment6", "Coment7", "Coment8",
    "JI11", "JI12", "JI13", "JI14", "JI21", "JI22", "JI23", "JI24",
    "JI31", "JI32", "JI41", "JI42", "JI51", "JI52", "JI53", "JI54",
    "JI55", "JI56", "JI57", "JI58", "JI59", "JI510", "JI511", "JI512",
    "R11", "R12", "R13", "R21", "R22", "R23", "R24", "R25",
    "R31", "R32", "R33", "R34", "R35", "R51", "R52", "R53",
    "R54", "R55", "R71", "R72", "R73"
})
{
    data.Columns.Add(column, typeof(string));
}

var row = data.NewRow();
row["Empresa"] = "Alimentos Soles S.A. de C.V.";
row["Area"] = "Operaciones";
row["Entrevistador"] = "María Guadalupe Ramírez";
row["FechaAct"] = "2026-11-18";
row["FechaIngreso"] = "2024-02-01";
row["FechaEgreso"] = "2026-11-15";
row["Nombre"] = "Juan Carlos Pérez López";
row["Puesto"] = "Operador de Tractocamión";
row["JefeI"] = "José Luis Hernández";
row["NombreJefeI"] = "José Luis Hernández";
row["Otro"] = "Cambio de residencia a otra ciudad.";
row["Razon"] = "Crecimiento profesional y mejores condiciones laborales.";

row["Coment1"] = "El trabajador cumplió con sus actividades y entregó su equipo completo.";
row["Coment2"] = "Mantuvo una actitud respetuosa durante la entrevista.";
row["Coment3"] = "Se le explicaron los procedimientos de finiquito.";
row["Coment4"] = "No hubo incidencias disciplinarias.";
row["Coment5"] = "Solicitó constancia laboral.";
row["Coment6"] = "Entregó credencial y uniformes.";
row["Coment7"] = "Se desactivaron sus accesos el mismo día.";
row["Coment8"] = "Documentación archivada correctamente.";

// Selected answers use X; every other option is left empty.
row["JI12"] = "X"; // Regular en el desempeño
row["JI24"] = "X"; // Excelente relación con el equipo
row["JI31"] = "X"; // Contratable
row["JI41"] = "X"; // Sí
row["JI51"] = "X"; // Mejor empleo
row["JI58"] = "X"; // Otro motivo
row["JI59"] = "X"; // Detalle adicional
row["JI510"] = "X";
row["JI511"] = "X";
row["JI512"] = "X";

row["R11"] = "X"; // Renuncia voluntaria
row["R23"] = "X"; // Bien
row["R34"] = "X"; // Muy bien
row["R53"] = "X"; // Bien
row["R71"] = "X"; // Sí lo recomendaría
row["R72"] = "";
row["R73"] = "";

data.Rows.Add(row);

var replaced = template.ExecuteMailMerge(row);
if (replaced == 0)
    throw new InvalidOperationException("The exit-interview template did not contain replaceable fields.");

var filled = template.ToBytes();
var filledPath = Path.Combine(samples, "exit-interview-sample-filled.fodt");
File.WriteAllBytes(filledPath, filled);

var prawnSharpPdf = PrawnSharpFodtRenderer.Render(filled);
var prawnSharpPdfPath = Path.Combine(samples, "exit-interview-sample-prawnsharp.pdf");
File.WriteAllBytes(prawnSharpPdfPath, prawnSharpPdf);

var libreOfficePdf = PrawnSharpFodtRenderer.RenderWithLibreOffice(filled);
var libreOfficePdfPath = Path.Combine(samples, "exit-interview-sample-libreoffice.pdf");
File.WriteAllBytes(libreOfficePdfPath, libreOfficePdf);

Console.WriteLine($"Fields replaced: {replaced}");
Console.WriteLine($"Template PrawnSharp:    {templateprawnSharpPath} ({templateprawnSharp.Length} bytes)");
Console.WriteLine($"Template LibreOffice: {templateLibreOfficePath} ({templateLibreOffice.Length} bytes)");
Console.WriteLine($"Filled template: {filledPath}");
Console.WriteLine($"PrawnSharp PDF:    {prawnSharpPdfPath} ({prawnSharpPdf.Length} bytes)");
Console.WriteLine($"LibreOffice PDF: {libreOfficePdfPath} ({libreOfficePdf.Length} bytes)");

static string FindSamplesDirectory()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "EntrevistaSalida.fodt")))
            return directory.FullName;

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("EntrevistaSalida.fodt was not found.");
}
