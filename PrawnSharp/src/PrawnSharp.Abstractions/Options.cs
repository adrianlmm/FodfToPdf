namespace PrawnSharp;

/// <summary>Metadatos del documento (diccionario Info de PDF).</summary>
public sealed record PdfMetadata
{
    public string? Title { get; init; }
    public string? Author { get; init; }
    public string? Subject { get; init; }
    public string? Keywords { get; init; }
    public string? Creator { get; init; } = "PrawnSharp";
}

/// <summary>Opciones de seguridad estándar PDF (RC4/AES según PDFsharp 6.2.4).</summary>
public sealed record PdfSecurityOptions
{
    public string? UserPassword { get; init; }

    public string? OwnerPassword { get; init; }

    public bool PermitPrint { get; init; } = true;

    public bool PermitFullQualityPrint { get; init; } = true;

    public bool PermitModifyDocument { get; init; }

    public bool PermitExtractContent { get; init; }

    public bool PermitAnnotations { get; init; }

    public bool PermitFormsFill { get; init; }

    public bool PermitAssembleDocument { get; init; }
}

/// <summary>Opciones de creación de un <c>PrawnDocument</c>.</summary>
public sealed record DocumentOptions
{
    /// <summary>Tamaño de página; por omisión, carta (igual que Prawn).</summary>
    public Size PageSize { get; init; } = PageSizes.Letter;

    /// <summary>Márgenes; por omisión, 32 pt en los cuatro lados.</summary>
    public Thickness Margin { get; init; } = new(32);

    /// <summary>Comprime los content streams con FlateDecode.</summary>
    public bool Compress { get; init; } = true;

    /// <summary>Kerning por omisión al dibujar texto (Prawn lo activa si la fuente tiene pares).</summary>
    public bool DefaultKerning { get; init; } = true;

    public PdfMetadata Metadata { get; init; } = new();

    /// <summary>Si se indica, el PDF se guarda cifrado con contraseñas/permisos.</summary>
    public PdfSecurityOptions? Security { get; init; }
}

/// <summary>Modo de renderizado de texto (operador <c>Tr</c>).</summary>
public enum TextRenderMode
{
    Fill = 0,
    Stroke = 1,
    FillStroke = 2,
    Invisible = 3,
    FillClip = 4,
    StrokeClip = 5,
    FillStrokeClip = 6,
    Clip = 7,
}

/// <summary>Alineación vertical dentro de una caja de alto fijo.</summary>
public enum TextValign
{
    Top = 0,
    Center = 1,
    Bottom = 2,
}

/// <summary>Alineación horizontal del texto.</summary>
public enum TextAlign
{
    Left = 0,
    Center = 1,
    Right = 2,
    Justify = 3,
}

/// <summary>Opciones de una llamada a <c>PrawnDocument.Text</c>.</summary>
public sealed record TextOptions
{
    /// <summary>Tamaño en puntos; si es nulo se usa el tamaño de fuente actual.</summary>
    public double? Size { get; init; }

    /// <summary>Color; si es nulo se usa negro.</summary>
    public PdfColor? Color { get; init; }

    public TextAlign Align { get; init; } = TextAlign.Left;

    /// <summary>Ancho de la caja de texto; si es nulo se usa el ancho de <c>Bounds</c>.</summary>
    public double? Width { get; init; }

    /// <summary>Alto fijo de la caja; si es nulo se usa el alto del contenido.</summary>
    public double? Height { get; init; }

    /// <summary>Alineación vertical dentro de <see cref="Height"/>.</summary>
    public TextValign Valign { get; init; } = TextValign.Top;

    /// <summary>Sangría de la primera línea de cada párrafo, en puntos.</summary>
    public double IndentParagraphs { get; init; }

    /// <summary>Espacio adicional entre líneas, en puntos.</summary>
    public double Leading { get; init; }

    /// <summary>Espaciado entre caracteres (operador <c>Tc</c>).</summary>
    public double CharacterSpacing { get; init; }

    /// <summary>Aplica kerning AFM al medir y dibujar. Null usa <see cref="DocumentOptions.DefaultKerning"/>.</summary>
    public bool? Kerning { get; init; }

    /// <summary>Incluye el espacio bajo la última línea al avanzar el cursor.</summary>
    public bool FinalGap { get; init; } = true;

    /// <summary>Modo de renderizado (relleno, trazo, invisible...).</summary>
    public TextRenderMode Mode { get; init; } = TextRenderMode.Fill;

    /// <summary>Rotación del texto en grados, alrededor del punto de la línea base.</summary>
    public double Rotate { get; init; }
}
