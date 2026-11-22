using System.Data;
using FodtToPdf;

// Generates the employee information form both untouched and filled with
// sample personal, job, and skills data.
var samples = FindSamplesDirectory();
var templatePath = Path.Combine(samples, "InformacionEmpleado.fodt");
var templateBytes = File.ReadAllBytes(templatePath);

// 1. Template-only PDFs
var templateQuest = QuestPdfFodtRenderer.Render(templateBytes);
var templateQuestPath = Path.Combine(samples, "employee-information-template-questpdf.pdf");
File.WriteAllBytes(templateQuestPath, templateQuest);

var templateprawnSharp = PrawnSharpFodtRenderer.Render(templateBytes);
var templateprawnSharpPath = Path.Combine(samples, "employee-information-template-prawnsharp.pdf");
File.WriteAllBytes(templateprawnSharpPath, templateprawnSharp);

var templateLibreOffice = QuestPdfFodtRenderer.RenderWithLibreOffice(templateBytes);
var templateLibreOfficePath = Path.Combine(samples, "employee-information-template-libreoffice.pdf");
File.WriteAllBytes(templateLibreOfficePath, templateLibreOffice);

// 2. Filled PDFs
var template = FodtTemplate.Load(templateBytes);
var values = new Dictionary<string, string>
{
    ["Nombres"] = "Misael de Jesús",
    ["ApellidoP"] = "Ontiveros",
    ["ApellidoM"] = "Rodríguez",
    ["FechaNacimiento"] = "05/09/1986",
    ["Calle"] = "Chihuahua",
    ["Numero"] = "125",
    ["Entre1"] = "Misión",
    ["Entre2"] = "Reforma",
    ["Colonia"] = "Centro",
    ["Ciudad"] = "Ciudad Obregón",
    ["TelCasa"] = "644-410-7900",
    ["TelCelular"] = "644-123-4567",
    ["CorreoE"] = "misael.ontiveros@example.com",
    ["TelRecados"] = "644-410-7901",
    ["RecadoCon"] = "Panchita",
    ["EdoCivil"] = "Casado",
    ["TieneHijos"] = "Si",
    ["NumHijos"] = "1",
    ["EdadesHijos"] = "8",
    ["Casa"] = "Comprada",
    ["TieneConyugue"] = "Si",
    ["TrabConyugue"] = "Trabajadora",
    ["UltimoGE"] = "Ingeniería",
    ["GradoEscolarNum"] = "4 años",
    ["Completo"] = "Si",
    ["Escuela"] = "ITESCA",
    ["ComprobanteEsc"] = "Título Profesional",
    ["OtrosEst"] = "Ninguno",
    ["EstudiaAct"] = "No",
    ["QueEstudia"] = "Nada",
    ["Grado"] = "N/A",
    ["EscuelaAct"] = "N/A",
    ["NomPuestoAct"] = "Programador Jr",
    ["DepartamentoAct"] = "Administración",
    ["FechaPuestoAct"] = "17/08/2015",
    ["JefeInmediatoAct"] = "José Luis Hernández",
    ["CentroTrabajo"] = "Plaza Comercial",
    ["ConocHabilidades"] = "Análisis de datos, SQL, trabajo en equipo",
    ["ApoyoJefeI"] = "Capacitación y retroalimentación",
    ["ActTimeFree"] = "Ciclismo y lectura",
};

var replaced = 0;
foreach (var (name, value) in values)
{
    if (template.SetBookmarkIfExists(name, value)) replaced++;
    else Console.WriteLine($"Bookmark not found: {name}");
}

var otherJobs = Table("NomOtroPuestoSoles", "OtroDptoSoles", "FechaOcupSoles", "OtroJefeInm");
Add(otherJobs, "Auxiliar de sistemas", "Sistemas", "01/2014", "Ing. Ramírez");
Add(otherJobs, "Becario de desarrollo", "Desarrollo", "06/2013", "Ing. López");

var previousJobs = Table("NombreEmpAnte", "PuestoAnt", "DptoAnt", "TiempoPAnt");
Add(previousJobs, "Tecnología del Yaqui", "Programador", "Desarrollo", "2 años");

var systems = Table("NombreSistema", "DominioSis");
Add(systems, "SAP", "Avanzado");
Add(systems, "SQL Server", "Avanzado");

var tools = Table("PaqueteHerramientas", "DominioPaqHerr");
Add(tools, "Visual Studio", "Avanzado");
Add(tools, "Excel", "Avanzado");

var competent = Table("PuestosCompetentes", "EnCuantoTimePComp", "NesecitasPComp");
Add(competent, "Analista de sistemas", "1 año", "Certificación y experiencia");

var notCompetent = Table("PuestosAunNoCompetentes", "EnCuantoTimeAunNoComp", "NesecitasAunNoComp");
Add(notCompetent, "Gerente de TI", "3 años", "Liderazgo y gestión de proyectos");

var strengths = Table("Fortalezas");
Add(strengths, "Responsabilidad y compromiso");
Add(strengths, "Facilidad para aprender");
Add(strengths, "Trabajo en equipo");

var improvements = Table("Mejoras");
Add(improvements, "Delegación de tareas");
Add(improvements, "Comunicación con áreas usuarias");
Add(improvements, "Gestión del tiempo");

var regionCounts = new (string Name, int Rows)[]
{
    ("DetalleOtrosPuestosDentro", template.ExecuteRegion("DetalleOtrosPuestosDentro", otherJobs)),
    ("DetallePuestosAntes", template.ExecuteRegion("DetallePuestosAntes", previousJobs)),
    ("DetalleSistemasDominados", template.ExecuteRegion("DetalleSistemasDominados", systems)),
    ("DetalleHerramientas", template.ExecuteRegion("DetalleHerramientas", tools)),
    ("DetallePuestosCompetentes", template.ExecuteRegion("DetallePuestosCompetentes", competent)),
    ("DetallePuestosNOCompetentes", template.ExecuteRegion("DetallePuestosNOCompetentes", notCompetent)),
    ("DetalleFortalezas", template.ExecuteRegion("DetalleFortalezas", strengths)),
    ("DetalleMejoras", template.ExecuteRegion("DetalleMejoras", improvements)),
};

foreach (var (name, rows) in regionCounts)
{
    if (rows < 0) throw new InvalidOperationException($"Region {name} was not found.");
    Console.WriteLine($"{name}: {rows} rows");
}

var filled = template.ToBytes();
var filledPath = Path.Combine(samples, "employee-information-sample-filled.fodt");
File.WriteAllBytes(filledPath, filled);

var questFilled = QuestPdfFodtRenderer.Render(filled);
var questFilledPath = Path.Combine(samples, "employee-information-sample-questpdf.pdf");
File.WriteAllBytes(questFilledPath, questFilled);

var prawnSharpFilled = PrawnSharpFodtRenderer.Render(filled);
var prawnSharpFilledPath = Path.Combine(samples, "employee-information-sample-prawnsharp.pdf");
File.WriteAllBytes(prawnSharpFilledPath, prawnSharpFilled);

var libreOfficeFilled = QuestPdfFodtRenderer.RenderWithLibreOffice(filled);
var libreOfficeFilledPath = Path.Combine(samples, "employee-information-sample-libreoffice.pdf");
File.WriteAllBytes(libreOfficeFilledPath, libreOfficeFilled);

Console.WriteLine($"Template QuestPDF:    {templateQuestPath} ({templateQuest.Length} bytes)");
Console.WriteLine($"Template PrawnSharp:    {templateprawnSharpPath} ({templateprawnSharp.Length} bytes)");
Console.WriteLine($"Template LibreOffice: {templateLibreOfficePath} ({templateLibreOffice.Length} bytes)");
Console.WriteLine($"Bookmarks replaced: {replaced}");
Console.WriteLine($"Filled template: {filledPath}");
Console.WriteLine($"Filled QuestPDF:    {questFilledPath} ({questFilled.Length} bytes)");
Console.WriteLine($"Filled PrawnSharp:    {prawnSharpFilledPath} ({prawnSharpFilled.Length} bytes)");
Console.WriteLine($"Filled LibreOffice: {libreOfficeFilledPath} ({libreOfficeFilled.Length} bytes)");

static DataTable Table(params string[] columns)
{
    var table = new DataTable();
    foreach (var column in columns) table.Columns.Add(column, typeof(string));
    return table;
}

static void Add(DataTable table, params string[] values)
{
    var row = table.NewRow();
    for (var i = 0; i < values.Length; i++) row[i] = values[i];
    table.Rows.Add(row);
}

static string FindSamplesDirectory()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "InformacionEmpleado.fodt")))
            return directory.FullName;

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("InformacionEmpleado.fodt was not found.");
}
