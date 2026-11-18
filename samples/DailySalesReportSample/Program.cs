using System.Data;
using FodtToPdf;

// Sample: fill daily-sales-report.fodt with fictional English data and export it to PDF.
var samplesDirectory = FindSamplesDirectory();
var templatePath = Path.Combine(samplesDirectory, "daily-sales-report.fodt");

if (!File.Exists(templatePath))
    throw new FileNotFoundException("daily-sales-report.fodt was not found.", templatePath);

var template = FodtTemplate.Load(templatePath);
IPrintTemplate report = template;

Set("report_date", "November 18, 2026");
Set("store_location", "Downtown Flagship Store");
Set("prepared_by", "Jordan Smith");
Set("total_transactions", "128");
Set("total_revenue", "USD 18,742.50");
Set("total_items_sold", "342");
Set("payment_method", "Credit Card");
Set("transaction_count", "86");
Set("amount", "USD 12,940.00");
Set("payment_methods", "Credit Card: 86 transactions / USD 12,940.00\nCash: 42 transactions / USD 5,802.50");

var items = new DataTable("Items");
items.Columns.Add("itemName", typeof(string));
items.Columns.Add("SKU", typeof(string));
items.Columns.Add("quantity", typeof(string));
items.Columns.Add("unitPrice", typeof(string));
items.Columns.Add("itemRevenue", typeof(string));

AddItem("Organic Coffee Beans", "COF-1001", "24", "USD 18.50", "USD 444.00");
AddItem("Ceramic Mug", "MUG-2045", "18", "USD 12.00", "USD 216.00");
AddItem("Cold Brew Bottle", "COF-1088", "30", "USD 6.75", "USD 202.50");
AddItem("Reusable Tote Bag", "BAG-3002", "12", "USD 9.50", "USD 114.00");
AddItem("Gift Card", "GFT-5000", "8", "USD 25.00", "USD 200.00");

var renderedRows = report.ExecuteRegion("Items", items);

if (renderedRows < 0)
    throw new InvalidOperationException("The Items region was not found in the template.");

var filledFodt = report.ToBytes();
var filledPath = Path.Combine(samplesDirectory, "daily-sales-report-filled.fodt");
File.WriteAllBytes(filledPath, filledFodt);

var questPdf = QuestPdfFodtRenderer.Render(filledFodt);
var questPdfPath = Path.Combine(samplesDirectory, "daily-sales-report-sample-questpdf.pdf");
File.WriteAllBytes(questPdfPath, questPdf);

var libreOfficePdf = QuestPdfFodtRenderer.RenderWithLibreOffice(filledFodt);
var libreOfficePdfPath = Path.Combine(samplesDirectory, "daily-sales-report-sample-libreoffice.pdf");
File.WriteAllBytes(libreOfficePdfPath, libreOfficePdf);

Console.WriteLine($"Filled template: {filledPath}");
Console.WriteLine($"QuestPDF PDF:    {questPdfPath} ({questPdf.Length} bytes)");
Console.WriteLine($"LibreOffice PDF: {libreOfficePdfPath} ({libreOfficePdf.Length} bytes)");
Console.WriteLine($"Items rendered: {renderedRows}");

void Set(string bookmark, string value)
{
    if (!report.SetBookmarkIfExists(bookmark, value))
        Console.WriteLine($"Bookmark not found: {bookmark}");
}

void AddItem(string name, string sku, string quantity, string unitPrice, string revenue)
{
    var row = items.NewRow();
    row["itemName"] = name;
    row["SKU"] = sku;
    row["quantity"] = quantity;
    row["unitPrice"] = unitPrice;
    row["itemRevenue"] = revenue;
    items.Rows.Add(row);
}

static string FindSamplesDirectory()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "daily-sales-report.fodt")))
            return directory.FullName;

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("daily-sales-report.fodt was not found in the current directory or its parents.");
}
