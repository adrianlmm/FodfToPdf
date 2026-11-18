using System.Data;
using System.Globalization;
using FodtToPdf;

// Grouped report: each region is cloned from the detail template and appended
// to the master. At least 10 groups are emitted so the PDF spans pages.
var samples = FindSamplesDirectory();
var master = FodtTemplate.Load(Path.Combine(samples, "regional-sales-master.fodt"));
var detail = FodtTemplate.Load(Path.Combine(samples, "regional-sales-detail.fodt"));
var report = master.Clone();

report.SetBookmarkIfExists("report_date", "November 18, 2026");
report.SetBookmarkIfExists("prepared_by", "Jordan Smith");

var groups = new (string Region, (string Id, string Date, string Customer, decimal Amount)[] Sales)[]
{
    ("North", new[] {
        ("S-1001", "2026-11-01", "Acme Distribution", 1240.00m),
        ("S-1002", "2026-11-03", "Northwind Retail", 875.50m),
        ("S-1003", "2026-11-05", "Bluebird Foods", 1620.75m) }),
    ("South", new[] {
        ("S-2001", "2026-11-02", "Sunrise Grocers", 1425.25m),
        ("S-2002", "2026-11-04", "Palmetto Supply", 760.00m),
        ("S-2003", "2026-11-07", "Gulf Coast Co.", 1180.50m) }),
    ("East", new[] {
        ("S-3001", "2026-11-01", "Atlantic Wholesale", 2100.00m),
        ("S-3002", "2026-11-05", "Metro Market", 980.25m),
        ("S-3003", "2026-11-08", "Pioneer Retail", 1540.00m) }),
    ("West", new[] {
        ("S-4001", "2026-11-03", "Pacific Grocers", 1875.00m),
        ("S-4002", "2026-11-06", "Redwood Supply", 640.50m),
        ("S-4003", "2026-11-09", "Golden State Co.", 1440.00m) }),
    ("Central", new[] {
        ("S-5001", "2026-11-02", "Heartland Retail", 1320.00m),
        ("S-5002", "2026-11-05", "Prairie Market", 710.00m),
        ("S-5003", "2026-11-08", "Midwest Supply", 1680.50m) }),
    ("Northeast", new[] {
        ("S-6001", "2026-11-01", "Empire Wholesale", 2380.00m),
        ("S-6002", "2026-11-04", "Hudson Retail", 880.00m),
        ("S-6003", "2026-11-07", "Liberty Foods", 1590.25m) }),
    ("Northwest", new[] {
        ("S-7001", "2026-11-02", "Evergreen Foods", 1460.00m),
        ("S-7002", "2026-11-06", "Cascade Retail", 990.00m),
        ("S-7003", "2026-11-09", "Sound Supply", 1320.25m) }),
    ("Southeast", new[] {
        ("S-8001", "2026-11-03", "Coastal Grocers", 1715.00m),
        ("S-8002", "2026-11-06", "Peach State Co.", 830.50m),
        ("S-8003", "2026-11-10", "Bayou Foods", 1240.00m) }),
    ("Southwest", new[] {
        ("S-9001", "2026-11-01", "Desert Market", 1980.00m),
        ("S-9002", "2026-11-04", "Cactus Retail", 620.00m),
        ("S-9003", "2026-11-08", "Rio Grande Foods", 1360.50m) }),
    ("Mid-Atlantic", new[] {
        ("S-1101", "2026-11-02", "Chesapeake Co.", 1625.00m),
        ("S-1102", "2026-11-05", "Potomac Retail", 790.25m),
        ("S-1103", "2026-11-09", "Blue Ridge Foods", 1140.00m) }),
    ("Great Lakes", new[] {
        ("S-1201", "2026-11-03", "Lakeshore Grocers", 1880.00m),
        ("S-1202", "2026-11-06", "Motor City Co.", 960.00m),
        ("S-1203", "2026-11-10", "Huron Supply", 1235.50m) }),
    ("Mountain", new[] {
        ("S-1301", "2026-11-01", "Alpine Market", 1540.00m),
        ("S-1302", "2026-11-04", "Summit Retail", 1080.00m),
        ("S-1303", "2026-11-08", "Rocky Foods", 1325.75m) }),
};

var renderedGroups = 0;
var detailRows = 0;

foreach (var group in groups)
{
    var part = detail.Clone();
    var total = group.Sales.Sum(x => x.Amount);

    part.SetBookmarkIfExists("region", group.Region);
    part.SetBookmarkIfExists("region_total", total.ToString("C2", CultureInfo.GetCultureInfo("en-US")));

    var sales = new DataTable("Sales");
    sales.Columns.Add("SaleId", typeof(string));
    sales.Columns.Add("Date", typeof(string));
    sales.Columns.Add("Customer", typeof(string));
    sales.Columns.Add("Amount", typeof(string));

    foreach (var sale in group.Sales)
    {
        var row = sales.NewRow();
        row["SaleId"] = sale.Id;
        row["Date"] = sale.Date;
        row["Customer"] = sale.Customer;
        row["Amount"] = sale.Amount.ToString("C2", CultureInfo.GetCultureInfo("en-US"));
        sales.Rows.Add(row);
    }

    var rows = part.ExecuteRegion("Sales", sales);
    if (rows < 0)
        throw new InvalidOperationException($"Sales region was not found for {group.Region}.");

    report.AppendDoc(part);
    renderedGroups++;
    detailRows += rows;
}

var filled = report.ToBytes();
var filledPath = Path.Combine(samples, "regional-sales-report-filled.fodt");
File.WriteAllBytes(filledPath, filled);

var questPdf = QuestPdfFodtRenderer.Render(filled);
var questPdfPath = Path.Combine(samples, "regional-sales-report-sample-questpdf.pdf");
File.WriteAllBytes(questPdfPath, questPdf);

var libreOfficePdf = QuestPdfFodtRenderer.RenderWithLibreOffice(filled);
var libreOfficePdfPath = Path.Combine(samples, "regional-sales-report-sample-libreoffice.pdf");
File.WriteAllBytes(libreOfficePdfPath, libreOfficePdf);

Console.WriteLine($"Groups rendered: {renderedGroups}");
Console.WriteLine($"Detail rows:    {detailRows}");
Console.WriteLine($"Filled template: {filledPath}");
Console.WriteLine($"QuestPDF PDF:    {questPdfPath} ({questPdf.Length} bytes)");
Console.WriteLine($"LibreOffice PDF: {libreOfficePdfPath} ({libreOfficePdf.Length} bytes)");

static string FindSamplesDirectory()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "regional-sales-master.fodt")))
            return directory.FullName;

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("regional-sales-master.fodt was not found.");
}
