namespace PrawnSharp;

/// <summary>Estilo de una cara de fuente.</summary>
public enum FontStyle
{
    Regular = 0,
    Bold = 1,
    Italic = 2,
    BoldItalic = 3,
}

/// <summary>
/// Cara de fuente solicitada por la API. En esta fase sólo se resuelven las 14
/// fuentes base de PDF; <see cref="FilePath"/> queda para TTF/OTF (fase F5).
/// </summary>
public sealed record FontFace(string Family, FontStyle Style = FontStyle.Regular, string? FilePath = null)
{
    public static FontFace Base14(string family, FontStyle style = FontStyle.Regular) => new(family, style);

    public override string ToString() =>
        FilePath is null ? $"{Family}/{Style}" : $"{Family}/{Style} ({FilePath})";
}

/// <summary>Fuente ya registrada en una página; <see cref="ResourceName"/> es el nombre PDF (<c>/F1</c>).</summary>
public sealed record PdfFontHandle(string ResourceName, FontFace Face);

/// <summary>Una ejecución de texto con su fuente, tamaño y color ya resueltos.</summary>
public sealed record TextRun(
    string Text,
    PdfFontHandle Font,
    double Size,
    PdfColor Color,
    double CharacterSpacing = 0,
    double HorizontalScaling = 1,
    double TextRise = 0,
    bool Kerning = false,
    double WordSpacing = 0,
    TextRenderMode RenderMode = TextRenderMode.Fill,
    double Rotation = 0);

/// <summary>Relleno de una figura: color sólido o gradiente, con alfa opcional.</summary>
public readonly record struct Paint(PdfColor Color, double Alpha = 1, Gradient? Gradient = null)
{
    public bool IsGradient => Gradient is not null;

    public static Paint Fill(PdfColor color) => new(color);

    public static Paint Solid(PdfColor color, double alpha = 1) => new(color, Math.Clamp(alpha, 0, 1));

    public static Paint GradientFill(Gradient gradient, double alpha = 1) =>
        new(default, Math.Clamp(alpha, 0, 1), gradient);
}

/// <summary>Trazo de una figura, con estilo de línea y alfa.</summary>
public readonly record struct StrokeStyle(
    PdfColor Color,
    double Width = 1,
    double Alpha = 1,
    LineCap Cap = LineCap.Butt,
    LineJoin Join = LineJoin.Miter,
    double MiterLimit = 10,
    IReadOnlyList<double>? Dash = null,
    double DashPhase = 0,
    Gradient? Gradient = null)
{
    public static StrokeStyle Default(PdfColor color) => new(color, 1);

    public static StrokeStyle Create(
        PdfColor color,
        double width = 1,
        double alpha = 1,
        LineCap cap = LineCap.Butt,
        LineJoin join = LineJoin.Miter,
        double miterLimit = 10,
        IReadOnlyList<double>? dash = null,
        double dashPhase = 0,
        Gradient? gradient = null) =>
        new(color, width, Math.Clamp(alpha, 0, 1), cap, join, miterLimit, dash, dashPhase, gradient);
}
