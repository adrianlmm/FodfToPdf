namespace FodtToPdf;

/// <summary>
/// Punto de entrada público: convierte un documento .fodt combinado a PDF usando PrawnSharp + PrawnPure
/// (100% C# puro, sin PDFsharp, sin SkiaSharp ni LibreOffice).
///
/// Uso:
///     byte[] pdf = PrawnSharpFodtRenderer.Render(fodtBytes);
///     byte[] pdf = PrawnSharpFodtRenderer.Render("documento.fodt");
/// </summary>
public static class PrawnSharpFodtRenderer
{
    /// <summary>Renderiza un documento .fodt a PDF mediante PrawnSharp.</summary>
    /// <param name="fodtBytes">Bytes del documento .fodt.</param>
    /// <param name="nestedTables">Dibuja tablas anidadas dentro de celdas (habilitado por defecto).</param>
    public static byte[] Render(byte[] fodtBytes, bool nestedTables = true)
    {
        if (fodtBytes == null || fodtBytes.Length == 0)
            throw new ArgumentException("El .fodt está vacío.", nameof(fodtBytes));

        var modelo = ExtractorFodt.Extraer(fodtBytes);
        return PrawnSharpGeneradorPdf.Generar(modelo, nestedTables);
    }

    /// <summary>Renderiza un archivo .fodt a PDF mediante PrawnSharp.</summary>
    public static byte[] Render(string path, bool nestedTables = true)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("No se encontró el documento .fodt.", path);

        return Render(File.ReadAllBytes(path), nestedTables);
    }

    /// <summary>Renderiza un documento .fodt a PDF mediante LibreOffice headless.</summary>
    public static byte[] RenderWithLibreOffice(byte[] fodtBytes, int timeoutSeconds = 120)
    {
        return LibreOfficePdf.ConvertWithLibreOffice(fodtBytes, timeoutSeconds);
    }

    /// <summary>Renderiza un archivo .fodt a PDF mediante LibreOffice headless.</summary>
    public static byte[] RenderWithLibreOffice(string path, int timeoutSeconds = 120)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("No se encontró el documento .fodt.", path);

        return LibreOfficePdf.ConvertWithLibreOffice(File.ReadAllBytes(path), timeoutSeconds);
    }

    /// <summary>Registra PrawnSharp en LibreOfficePdf para que ConvertToPdf use este motor.</summary>
    public static void UsePrawnSharp()
    {
        LibreOfficePdf.SetPdfGenerator((fodt, nestedTables) => Render(fodt, nestedTables));
    }

    /// <summary>Desregistra el generador y vuelve a LibreOffice.</summary>
    public static void DisablePrawnSharp()
    {
        LibreOfficePdf.ClearPdfGenerator();
    }

    /// <summary>Extrae el modelo intermedio del .fodt (diagnóstico).</summary>
    internal static ModeloPlantilla ExtractModel(byte[] fodtBytes)
    {
        return ExtractorFodt.Extraer(fodtBytes);
    }
}
