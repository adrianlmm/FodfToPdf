using System.Data;
using FodtToPdf;

// Fills the individual training contract template with fictitious data and
// exports both PDF backends.
var samples = FindSamplesDirectory();
var template = FodtTemplate.Load(Path.Combine(samples, "C_CapacitacionInicialPTractocamion.fodt"));

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
    throw new InvalidOperationException("The contract template did not contain any replaceable fields.");

var filledPath = Path.Combine(samples, "training-contract-sample-filled.fodt");
File.WriteAllBytes(filledPath, template.ToBytes());

var questPdf = QuestPdfFodtRenderer.Render(File.ReadAllBytes(filledPath));
var questPdfPath = Path.Combine(samples, "training-contract-sample-questpdf.pdf");
File.WriteAllBytes(questPdfPath, questPdf);

var libreOfficePdf = QuestPdfFodtRenderer.RenderWithLibreOffice(File.ReadAllBytes(filledPath));
var libreOfficePdfPath = Path.Combine(samples, "training-contract-sample-libreoffice.pdf");
File.WriteAllBytes(libreOfficePdfPath, libreOfficePdf);

Console.WriteLine($"Fields replaced: {replaced}");
Console.WriteLine($"Filled template: {filledPath}");
Console.WriteLine($"QuestPDF PDF:    {questPdfPath} ({questPdf.Length} bytes)");
Console.WriteLine($"LibreOffice PDF: {libreOfficePdfPath} ({libreOfficePdf.Length} bytes)");

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
