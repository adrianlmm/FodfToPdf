using System.Data;
using FodtToPdf;

// Sample: fill the invoice-template.fodt with fictional English data and
// export the result to PDF with the QuestPDF backend.
var samplesDirectory = FindSamplesDirectory();
var templatePath = Path.Combine(samplesDirectory, "invoice-template.fodt");

if (!File.Exists(templatePath))
    throw new FileNotFoundException("invoice-template.fodt was not found.", templatePath);

var template = FodtTemplate.Load(templatePath);
IPrintTemplate invoice = template;

Set("enterprice", "Northstar Trading Co.");
Set("address1", "1200 Market Street");
Set("address2", "Suite 450");
Set("address3", "San Francisco, CA 94103");
Set("address4", "United States");
Set("balance", "USD 3,661.88");
Set("customer", "Acme Distribution LLC");
Set("customer_address1", "780 Industrial Avenue");
Set("customer_address2", "Building 4");
Set("customer_address3", "Oakland, CA 94607");
Set("ship_to1", "Acme Distribution LLC");
Set("ship_to2", "780 Industrial Avenue, Building 4");
Set("ship_to3", "Oakland, CA 94607");

var logoPath = Path.Combine(samplesDirectory, "logo.png");
if (File.Exists(logoPath))
    invoice.InsertImage("logo", File.ReadAllBytes(logoPath), 100, 80);

var items = new DataTable("Items");
items.Columns.Add("Id", typeof(string));
items.Columns.Add("description", typeof(string));
items.Columns.Add("quantity", typeof(string));
items.Columns.Add("amount", typeof(string));
items.Columns.Add("amount2", typeof(string));

AddItem("1", "Industrial Widget", "10", "USD 1,250.00", "USD 1,250.00");
AddItem("2", "Steel Bracket", "25", "USD 375.00", "USD 375.00");
AddItem("3", "Assembly Kit", "5", "USD 500.00", "USD 500.00");

var renderedRows = invoice.ExecuteRegion("Items", items);

if (renderedRows < 0)
    throw new InvalidOperationException("The Items region was not found in the template.");

Set("subtotal", "USD 3,375.00");
Set("tax_rate", "8.50%");
Set("total", "USD 3,661.88");
Set("balance_due", "USD 3,661.88");

var filledFodt = invoice.ToBytes();
var filledPath = Path.Combine(samplesDirectory, "invoice-sample-filled.fodt");
File.WriteAllBytes(filledPath, filledFodt);

var questPdf = QuestPdfFodtRenderer.Render(filledFodt);
var questPdfPath = Path.Combine(samplesDirectory, "invoice-sample-questpdf.pdf");
File.WriteAllBytes(questPdfPath, questPdf);

var libreOfficePdf = QuestPdfFodtRenderer.RenderWithLibreOffice(filledFodt);
var libreOfficePdfPath = Path.Combine(samplesDirectory, "invoice-sample-libreoffice.pdf");
File.WriteAllBytes(libreOfficePdfPath, libreOfficePdf);

Console.WriteLine($"Filled template: {filledPath}");
Console.WriteLine($"QuestPDF PDF:    {questPdfPath} ({questPdf.Length} bytes)");
Console.WriteLine($"LibreOffice PDF: {libreOfficePdfPath} ({libreOfficePdf.Length} bytes)");
Console.WriteLine($"Items rendered: {renderedRows}");

void Set(string bookmark, string value)
{
    if (!invoice.SetBookmarkIfExists(bookmark, value))
        Console.WriteLine($"Bookmark not found: {bookmark}");
}

void AddItem(string id, string description, string quantity, string amount, string amount2)
{
    var row = items.NewRow();
    row["Id"] = id;
    row["description"] = description;
    row["quantity"] = quantity;
    row["amount"] = amount;
    row["amount2"] = amount2;
    items.Rows.Add(row);
}

static string FindSamplesDirectory()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "invoice-template.fodt")))
            return directory.FullName;

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("invoice-template.fodt was not found in the current directory or its parents.");
}
