using QuestPDF.Fluent;

namespace FodtToPdf;

// Public entry point: a filled .fodt document -> PDF without LibreOffice.
//
// Usage:
//     byte[] pdf = QuestPdfFodtRenderer.Render(fodtBytes);
//     byte[] pdf = QuestPdfFodtRenderer.Render(fodtBytes, nestedTables: true);
//
// Alternative to LibreOfficePdf.ConvertWithLibreOffice; the .fodt is produced
// by FodtTemplate either way.
public static class QuestPdfFodtRenderer
{
    /// <summary>Renders a filled .fodt document to PDF.</summary>
    /// <param name="fodtBytes">The filled .fodt document.</param>
    /// <param name="nestedTables">Draws tables nested inside table cells. Required for reports such as purchase orders; keep false for invoices.</param>
    /// <param name="ownPagination">Uses the internal pagination estimator. Disabled by default.</param>
    public static byte[] Render(byte[] fodtBytes, bool nestedTables = false, bool ownPagination = false)
    {
        if (fodtBytes == null || fodtBytes.Length == 0)
            throw new ArgumentException("El .fodt está vacío.", nameof(fodtBytes));

        // Los documentos largos (contratos) se maquetan hasta tres veces: la primera
        // descubre en qué página cae cada bloque, las siguientes le quitan el espacio
        // anterior a los que abren página, como hace LibreOffice (SwFlowFrame::MoveBwd).
        // Los documentos cortos van de una sola pasada, como siempre.
        var prueba = ExtractModel(fodtBytes);

        if (!PaginarEnDosPasadas || prueba.Flujo.Count < 80)
        {
            GeneradorPdf.ColapsarEspacios = new HashSet<int>();

            return Generar(fodtBytes, nestedTables, ownPagination);
        }

        GeneradorPdf.ColapsarEspacios = new HashSet<int>();

        var pdf = Generar(fodtBytes, nestedTables, ownPagination);

        for (var vuelta = 0; vuelta < 2; vuelta++)
        {
            var colapsar = PaginasDeBloques.QueEmpiezanPagina(prueba.Flujo);

            if (colapsar.SetEquals(GeneradorPdf.ColapsarEspacios)) break;

            GeneradorPdf.ColapsarEspacios = colapsar;
            pdf = Generar(fodtBytes, nestedTables, ownPagination);
        }

        return pdf;
    }

    // La paginación en dos pasadas (marcar página y colapsar el espacio anterior) NO
    // cambió el resultado en el contrato CCI (la página 4 seguía empezando en y=97
    // contra 89 de LibreOffice), así que se deja apagada para no maquetar tres veces.
    // La paginación en dos pasadas sólo servía para anular el espacio de los bloques que
    // abren página. MEDIDO contra LibreOffice: ese espacio SÍ se respeta (el contrato de
    // tractocamión salía en 10 hojas en vez de 11 y cada página ganaba un renglón). Se
    // apaga: además ahorra maquetar el documento tres veces.
    const bool PaginarEnDosPasadas = false;

    static byte[] Generar(byte[] fodtBytes, bool nestedTables, bool ownPagination)
    {
        GeneradorPdf.CortesPropiosActivo = ownPagination;
        PaginasDeBloques.Limpiar();

        var modelo = ExtractorFodt.Extraer(fodtBytes);
        Calibracion.Aplicar(modelo);

        return GeneradorPdf.ADocumento(modelo, nestedTables).GeneratePdf();
    }

    /// <summary>Renders a filled .fodt document to PDF with headless LibreOffice.</summary>
    public static byte[] RenderWithLibreOffice(byte[] fodtBytes, int timeoutSeconds = 120)
    {
        return LibreOfficePdf.ConvertWithLibreOffice(fodtBytes, timeoutSeconds);
    }

    /// <summary>Registers this renderer in LibreOfficePdf so ConvertToPdf uses QuestPDF.</summary>
    public static void UseQuestPdf()
    {
        LibreOfficePdf.SetQuestPdfGenerator((fodt, nestedTables) => Render(fodt, nestedTables));
    }

    /// <summary>Clears the QuestPDF registration and falls back to LibreOffice.</summary>
    public static void DisableQuestPdf()
    {
        LibreOfficePdf.ClearQuestPdfGenerator();
    }

    /// <summary>Renders a .fodt file to PDF with headless LibreOffice.</summary>
    public static byte[] RenderWithLibreOffice(string path, int timeoutSeconds = 120)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("The .fodt document was not found.", path);

        return LibreOfficePdf.ConvertWithLibreOffice(File.ReadAllBytes(path), timeoutSeconds);
    }

    /// <summary>Renders a .fodt file to PDF.</summary>
    public static byte[] Render(string path, bool nestedTables = false, bool ownPagination = false)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("The .fodt document was not found.", path);

        return Render(File.ReadAllBytes(path), nestedTables, ownPagination);
    }

    /// <summary>Extracts the intermediate .fodt model. Intended for diagnostics.</summary>
    internal static ModeloPlantilla ExtractModel(byte[] fodtBytes)
    {
        var modelo = ExtractorFodt.Extraer(fodtBytes);
        Calibracion.Aplicar(modelo);
        return modelo;
    }
}
