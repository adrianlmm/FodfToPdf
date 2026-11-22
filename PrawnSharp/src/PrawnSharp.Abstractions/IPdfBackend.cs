using System.Numerics;

namespace PrawnSharp;

/// <summary>
/// Escritor PDF de bajo nivel. La capa de layout no conoce PDFsharp ni ningún otro
/// motor: sólo habla con esta interfaz.
/// </summary>
public interface IPdfBackend : IDisposable
{
    int PageCount { get; }

    PdfMetadata Metadata { get; set; }

    /// <summary>Opciones de cifrado/permisos; nulo = PDF sin cifrar.</summary>
    PdfSecurityOptions? Security { get; set; }

    /// <summary>Comprime los content streams con FlateDecode.</summary>
    bool Compress { get; set; }

    /// <summary>Resuelve las métricas de una fuente (AFM base-14 o TTF/OTF).</summary>
    IFontMetrics GetFontMetrics(FontFace face);

    IPdfPage AddPage(PageSetup setup);

    /// <summary>Añade un marcador/outline al documento.</summary>
    void AddOutline(string title, int pageIndex, double? top = null, bool opened = true);

    void Save(Stream output);
}

/// <summary>
/// Una página PDF. Todas las coordenadas que recibe son de Prawn (origen
/// arriba-izquierda); el backend las convierte a coordenadas PDF.
/// </summary>
public interface IPdfPage
{
    double Width { get; }

    double Height { get; }

    /// <summary>Registra una fuente y devuelve el nombre de recurso para los content streams.</summary>
    PdfFontHandle RegisterFont(FontFace face);

    /// <summary>Dibuja una línea de texto en la línea base indicada.</summary>
    void DrawText(TextRun run, Point baseline);

    void DrawLine(Point from, Point toPoint, StrokeStyle stroke);

    void DrawRectangle(Rect rect, Paint? fill, StrokeStyle? stroke);

    /// <summary>Dibuja un path con relleno y/o trazo.</summary>
    void DrawPath(Path path, Paint? fill, StrokeStyle? stroke, FillRule fillRule = FillRule.NonZero);

    /// <summary>Dibuja una imagen PNG/JPEG en el rectángulo indicado.</summary>
    void DrawImage(ImageData image, Rect rect);

    /// <summary>Guarda el estado gráfico actual (transformación y estado PDF).</summary>
    void SaveState();

    /// <summary>Restaura el último estado guardado.</summary>
    void RestoreState();

    /// <summary>Establece la región de recorte; se mantiene hasta restaurar el estado.</summary>
    void SetClip(Path path, FillRule fillRule = FillRule.NonZero);

    /// <summary>Concatena una transformación en coordenadas de Prawn.</summary>
    void ConcatMatrix(Matrix3x2 matrix);

    /// <summary>Establece alfa de relleno/trazo y modo de mezcla.</summary>
    void SetAlpha(double fillAlpha, double strokeAlpha, BlendMode blendMode = BlendMode.Normal);

    /// <summary>Añade una anotación de enlace sobre <paramref name="rect"/>.</summary>
    void AddLink(Rect rect, string url);

    /// <summary>Añade un enlace interno a otra página del documento.</summary>
    void AddLinkToPage(Rect rect, int pageIndex, double? top = null);

    /// <summary>
    /// Escape para operadores PDF que el backend todavía no cubre. Se añade como un
    /// content stream independiente, envuelto por quien lo usa.
    /// </summary>
    void AppendRaw(string operators);
}
