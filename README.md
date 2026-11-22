# FodtToPdf

.NET libraries for reading OpenDocument Flat XML templates (`.fodt`), filling them with data, and exporting the result to PDF. The repository contains both the core template/conversion library and an optional QuestPDF-based PDF renderer.

![Invoice template before and after processing](invoice_before_after.gif)

The animation shows the original invoice template and the generated result.

## Projects

| Project | Description |
|---|---|
| `FodtToPdf` | Core library: loads `.fodt` templates, replaces bookmarks and repeating regions, inserts images, supports master/detail documents, and converts to PDF through LibreOffice. |
| `FodtToPdf.QuestPdf` | Optional renderer that draws the extracted template model with QuestPDF and SkiaSharp, without invoking LibreOffice. |
| `samples/InvoiceSample` | Fills `invoice-template.fodt` with sample invoice data and writes both QuestPDF and LibreOffice PDFs. |
| `samples/DailySalesReportSample` | Fills `daily-sales-report.fodt` with sample daily-sales data and writes both PDF variants. |
| `samples/RegionalSalesReportSample` | Builds a grouped master/detail report with 12 regions, cloning a detail template and appending it to the master document. |
| `samples/TrainingContractSample` | Generates both the raw individual training contract template `C_CapacitacionInicialPTractocamion.fodt` and a version filled with sample worker data, each in both PDF variants. |
| `samples/DividendCertificateSample` | Fills the Mexican dividend withholding certificate template `formatoDividendos.fodt` using its bookmarks and exports both PDF variants. |
| `samples/CancellationReceiptSample` | Fills the CFDI cancellation acknowledgement template `AcuseCancelacion.fodt` using its bookmarks and exports both PDF variants. |
| `samples/PurchaseOrderSample` | Generates the purchase order template `OrdenDeCompra.fodt` both untouched and filled with a header and repeating detail region, in both PDF variants. |
| `samples/InvalidPurchaseOrderSample` | Generates the invalid purchase order template `OrdenDeCompraInvalida.fodt`, both untouched and filled, in both PDF variants. |
| `samples/PhysicalSignatureSample` | Fills the physical-signature sheet template `Formato_FirmasPFisica.fodt` with six signer names and exports both PDF variants. |
| `samples/NestedTableSample` | Renders `nested_table.fodt` with `nestedTables: true` to demonstrate a table nested inside a table cell. |
| `samples/SupplierPaymentSample` | Fills the supplier-payment master template `PagosFacturasProveedorMaestro.fodt` with header data and exports both PDF variants. |
| `samples/SupplierInvoicesSample` | Fills the supplier-invoice report template `FacturasProveedor.fodt` with header bookmarks and a repeating invoice region, then exports both PDF variants. |
| `samples/CCIAlimentosSample` | Generates the CCI contract template `CCI_Alimentos Soles S.A. de C.V._CF.fodt` both untouched and with literal placeholders replaced by sample data, in both PDF variants. |
| `samples/ThirdPartyComparisonSample` | Generates the third-party comparison template `ComparativoTerceros.fodt` both untouched and filled with sample comparison rows, in both PDF variants. |
| `samples/BusinessCreditApplicationSample` | Generates the SC_PMoral business credit application both untouched and filled with sample values for all database-display fields, in both PDF variants. |
| `samples/CartaPorteSample` | Generates the carta porte request template `PlantillaSolicitudEntregaCartaPorte_V3-3.fodt` both untouched and filled with header bookmarks and three detail regions, in both PDF variants. |
| `samples/DepositApplicationSample` | Generates the deposit application template `PlantillaDepositos.fodt` both untouched and filled with invoice/deposit rows, in both PDF variants. |
| `samples/InterestCertificateSample` | Generates the interest withholding certificate template `PlantillaIntereses.fodt` both untouched and filled with sample withholding data, in both PDF variants. |
| `samples/PayrollReceiptSample` | Generates the payroll CFDI receipt template `ComprobantesFiscales.fodt` both untouched and filled with sample payroll data, in both PDF variants. |
| `samples/PaymentComplementSample` | Generates the payment receipt/complement template `PlantillaComprobantePago.fodt` both untouched and filled with sample payment data, in QuestPDF, LibreOffice, and PrawnSharp variants. |
| `samples/EmployeeInformationSample` | Generates the employee information form `InformacionEmpleado.fodt` both untouched and filled with sample personal, job, and skills data, in both PDF variants. |
| `samples/MergedTableSample` | Renders the merged-cell table template `tabla.fodt` in both PDF variants. |
| `samples/ParagraphLayoutSample` | Renders the paragraph-layout template `parrafos.fodt` in both PDF variants. |
| `samples/CIT18Sample` | Generates the CIT18 contract template `CIT18.fodt` both untouched and filled with sample worker data, in both PDF variants. |
| `samples/TransferReceiptSample` | Generates the CFDI transfer receipt template `ComprobanteTraslado.fodt` both untouched and filled with header bookmarks and a repeating concept region, in both PDF variants. |
| `samples/ExitInterviewSample` | Generates the employee exit-interview template `EntrevistaSalida.fodt` both untouched and filled with 67 fictional answers, in both PDF variants. |
| `samples/DaycareLetterSample` | Fills the IMSS daycare letter template `CartaGuarderia.fodt` using its bookmarks and exports both PDF variants. |

## Main features

- Template loading from a file or a byte array.
- Bookmark replacement for scalar values.
- Repeating table regions backed by `DataTable`.
- Master/detail reports through `Clone()` and `AppendDoc()`.
- Image insertion at bookmarks, with explicit width and height.
- Paragraph, table, cell, border, header, and footer support.
- Table width handling based on the declared ODF table and column widths.
- Font substitution for commonly missing fonts, including `Open Sans` and `Aptos`.
- Two PDF backends:
  - QuestPDF (no LibreOffice process required).
  - LibreOffice headless (fallback and reference output).

## Requirements

- .NET 10 SDK.
- LibreOffice installed for the LibreOffice conversion path.
- Windows system fonts for the default renderer configuration. Missing fonts are substituted using the rules in `FodtToPdf.QuestPdf`.

NuGet dependencies for the QuestPDF renderer are declared in `FodtToPdf.QuestPdf.csproj`.

## Basic usage

```csharp
using FodtToPdf;

var template = FodtTemplate.Load("invoice-template.fodt");

template.SetBookmarkIfExists("customer", "Acme Distribution LLC");
template.SetBookmarkIfExists("total", "$3,661.88");

var items = new DataTable("Items");
items.Columns.Add("description");
items.Columns.Add("amount");
items.Rows.Add("Consulting", "$1,250.00");

template.ExecuteRegion("Items", items);

var filledFodt = template.ToBytes();
var pdf = QuestPdfFodtRenderer.Render(filledFodt);

File.WriteAllBytes("invoice.pdf", pdf);
```

For LibreOffice output:

```csharp
var pdf = QuestPdfFodtRenderer.RenderWithLibreOffice(filledFodt);
```

## Build

Build the core library and the QuestPDF renderer:

```bash
dotnet build FodtToPdf/FodtToPdf.csproj
dotnet build FodtToPdf.QuestPdf/FodtToPdf.QuestPdf.csproj
```

## Samples

Run any sample from its project directory:

```bash
cd samples/InvoiceSample
dotnet run
```

Each sample creates a filled `.fodt` and two PDFs, one for each backend:

```text
<sample>-questpdf.pdf
<sample>-libreoffice.pdf
```

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE).
