using System.Data;
using FodtToPdf;

// Generates the CIT18 contract both as the untouched template and as a
// version filled with sample worker data.
var samples = FindSamplesDirectory();
var templatePath = Path.Combine(samples, "CIT18.fodt");
var templateBytes = File.ReadAllBytes(templatePath);

// 1. Template-only PDFs
var templateQuest = QuestPdfFodtRenderer.Render(templateBytes);
var templateQuestPath = Path.Combine(samples, "cit18-template-questpdf.pdf");
File.WriteAllBytes(templateQuestPath, templateQuest);

var templateLibreOffice = QuestPdfFodtRenderer.RenderWithLibreOffice(templateBytes);
var templateLibreOfficePath = Path.Combine(samples, "cit18-template-libreoffice.pdf");
File.WriteAllBytes(templateLibreOfficePath, templateLibreOffice);

// 2. Filled PDFs
var template = FodtTemplate.Load(templateBytes);
var data = new DataTable("Contrato");
foreach (var column in new[]
{
    "Nombre", "Edad", "EdoCivil", "Curp", "Rfc", "Direccion",
    "Dia1", "Mes1", "Año1", "Mes2", "Año2", "Puesto",
    "Salario", "FormaPago", "diaDescanso", "Testigo1", "Testigo2"
})
{
    data.Columns.Add(column, typeof(string));
}

var row = data.NewRow();
row["Nombre"] = "Juan Carlos Pérez López";
row["Edad"] = "29";
row["EdoCivil"] = "Soltero";
row["Curp"] = "PELJ940315HSRRPN07";
row["Rfc"] = "PELJ940315AB1";
row["Direccion"] = "Calle Chihuahua No. 125 Norte, Despacho 3-B, Ciudad Obregón, Sonora";
row["Dia1"] = "01";
row["Mes1"] = "marzo";
row["Año1"] = "2026";
row["Mes2"] = "abril";
row["Año2"] = "2026";
row["Puesto"] = "Operador de Tractocamión";
row["Salario"] = "450.00";
row["FormaPago"] = "Transferencia bancaria semanal";
row["diaDescanso"] = "domingo";
row["Testigo1"] = "María Guadalupe Ramírez";
row["Testigo2"] = "José Luis Hernández";
data.Rows.Add(row);

var replaced = template.ExecuteMailMerge(row);
if (replaced == 0)
    throw new InvalidOperationException("The CIT18 template did not contain replaceable fields.");

var filled = template.ToBytes();
var filledPath = Path.Combine(samples, "cit18-sample-filled.fodt");
File.WriteAllBytes(filledPath, filled);

var questFilled = QuestPdfFodtRenderer.Render(filled);
var questFilledPath = Path.Combine(samples, "cit18-sample-questpdf.pdf");
File.WriteAllBytes(questFilledPath, questFilled);

var libreOfficeFilled = QuestPdfFodtRenderer.RenderWithLibreOffice(filled);
var libreOfficeFilledPath = Path.Combine(samples, "cit18-sample-libreoffice.pdf");
File.WriteAllBytes(libreOfficeFilledPath, libreOfficeFilled);

Console.WriteLine($"Template QuestPDF:    {templateQuestPath} ({templateQuest.Length} bytes)");
Console.WriteLine($"Template LibreOffice: {templateLibreOfficePath} ({templateLibreOffice.Length} bytes)");
Console.WriteLine($"Fields replaced: {replaced}");
Console.WriteLine($"Filled template: {filledPath}");
Console.WriteLine($"Filled QuestPDF:    {questFilledPath} ({questFilled.Length} bytes)");
Console.WriteLine($"Filled LibreOffice: {libreOfficeFilledPath} ({libreOfficeFilled.Length} bytes)");

static string FindSamplesDirectory()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "CIT18.fodt")))
            return directory.FullName;

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("CIT18.fodt was not found.");
}
