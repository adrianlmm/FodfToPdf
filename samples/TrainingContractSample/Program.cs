using System.Data;
using FodtToPdf;

// Generates two variants from the same contract template:
//   1. The original template, without filling any fields.
//   2. A filled contract with fictitious worker/company data.
var samples = FindSamplesDirectory();
var templatePath = Path.Combine(samples, "C_CapacitacionInicialPTractocamion.fodt");
var templateBytes = File.ReadAllBytes(templatePath);

// Template-only output
var questTemplate = QuestPdfFodtRenderer.Render(templateBytes);
var questTemplatePath = Path.Combine(samples, "training-contract-template-questpdf.pdf");
File.WriteAllBytes(questTemplatePath, questTemplate);

var prawnSharpTemplate = PrawnSharpFodtRenderer.Render(templateBytes);
var prawnSharpTemplatePath = Path.Combine(samples, "training-contract-template-prawnsharp.pdf");
File.WriteAllBytes(prawnSharpTemplatePath, prawnSharpTemplate);

var libreOfficeTemplate = QuestPdfFodtRenderer.RenderWithLibreOffice(templateBytes);
var libreOfficeTemplatePath = Path.Combine(samples, "training-contract-template-libreoffice.pdf");
File.WriteAllBytes(libreOfficeTemplatePath, libreOfficeTemplate);

// Filled output
var template = FodtTemplate.Load(templateBytes);
var data = new DataTable("Contrato");
foreach (var column in new[]
{
    "Empresa", "Nombre", "Edad", "EdoCivil", "Ife", "Curp", "Rfc", "Sexo",
    "Direccion", "Dia1", "Mes1", "Año1", "Dia2", "Mes2", "Año2", "FechaF",
    "Puesto", "Salario", "FormaPago", "diaDescanso", "Dia0", "Mes0", "Año0",
    "Trabajador", "Testigo1", "Testigo2"
})
{
    data.Columns.Add(column, typeof(string));
}

var row = data.NewRow();
row["Empresa"] = "Agroindustrias del Noroeste S.A. de C.V.";
row["Nombre"] = "Juan Carlos Pérez López";
row["Edad"] = "29";
row["EdoCivil"] = "Soltero";
row["Ife"] = "0123456789012";
row["Curp"] = "PELJ940315HSRRPN07";
row["Rfc"] = "PELJ940315AB1";
row["Sexo"] = "Masculino";
row["Direccion"] = "Calle Chihuahua No. 125 Norte, Despacho 3-B, Ciudad Obregón, Sonora";
row["Dia1"] = "01";
row["Mes1"] = "marzo";
row["Año1"] = "2026";
row["Dia2"] = "30";
row["Mes2"] = "abril";
row["Año2"] = "2026";
row["FechaF"] = "";
row["Puesto"] = "Operador de Tractocamión";
row["Salario"] = "450.00";
row["FormaPago"] = "Transferencia bancaria semanal";
row["diaDescanso"] = "domingo";
row["Dia0"] = "01";
row["Mes0"] = "marzo";
row["Año0"] = "2026";
row["Trabajador"] = "Juan Carlos Pérez López";
row["Testigo1"] = "María Guadalupe Ramírez";
row["Testigo2"] = "José Luis Hernández";
data.Rows.Add(row);

var replaced = template.ExecuteMailMerge(data.Rows[0]);
if (replaced == 0)
    throw new InvalidOperationException("The contract template did not contain replaceable fields.");

var filled = template.ToBytes();
var filledPath = Path.Combine(samples, "training-contract-sample-filled.fodt");
File.WriteAllBytes(filledPath, filled);

var questFilled = QuestPdfFodtRenderer.Render(filled);
var questFilledPath = Path.Combine(samples, "training-contract-sample-questpdf.pdf");
File.WriteAllBytes(questFilledPath, questFilled);

var prawnSharpFilled = PrawnSharpFodtRenderer.Render(filled);
var prawnSharpFilledPath = Path.Combine(samples, "training-contract-sample-prawnsharp.pdf");
File.WriteAllBytes(prawnSharpFilledPath, prawnSharpFilled);

var libreOfficeFilled = QuestPdfFodtRenderer.RenderWithLibreOffice(filled);
var libreOfficeFilledPath = Path.Combine(samples, "training-contract-sample-libreoffice.pdf");
File.WriteAllBytes(libreOfficeFilledPath, libreOfficeFilled);

Console.WriteLine($"Source template: {templatePath}");
Console.WriteLine($"Template QuestPDF:    {questTemplatePath} ({questTemplate.Length} bytes)");
Console.WriteLine($"Template PrawnSharp:    {prawnSharpTemplatePath} ({prawnSharpTemplate.Length} bytes)");
Console.WriteLine($"Template LibreOffice: {libreOfficeTemplatePath} ({libreOfficeTemplate.Length} bytes)");
Console.WriteLine($"Fields replaced: {replaced}");
Console.WriteLine($"Filled template: {filledPath}");
Console.WriteLine($"Filled QuestPDF:    {questFilledPath} ({questFilled.Length} bytes)");
Console.WriteLine($"Filled PrawnSharp:    {prawnSharpFilledPath} ({prawnSharpFilled.Length} bytes)");
Console.WriteLine($"Filled LibreOffice: {libreOfficeFilledPath} ({libreOfficeFilled.Length} bytes)");

static string FindSamplesDirectory()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "C_CapacitacionInicialPTractocamion.fodt")))
            return directory.FullName;

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("C_CapacitacionInicialPTractocamion.fodt was not found.");
}
