using System.Numerics;
using PrawnSharp.Backend.PdfSharp;
using PrawnSharp.Fonts;

namespace PrawnSharp;

/// <summary>
/// Documento PDF con una API inspirada en Prawn. En esta fase (F1) cubre páginas,
/// márgenes, cursor, fuentes base-14, texto y figuras básicas; el layout completo
/// (cortes de línea, tablas, TTF) llega en las fases siguientes.
/// </summary>
public sealed class PrawnDocument : IDisposable
{
    private readonly IPdfBackend _backend;
    private readonly DocumentOptions _options;
    private readonly FontRegistry _fonts = new();

    private IPdfPage _page;
    private FontFace _font;
    private double _fontSize = 12;
    private double _cursorY;
    private Rect _bounds;
    private int _boundingBoxDepth;
    private bool _disposed;
    private readonly List<Action<PrawnDocument>> _repeaters = [];

    /// <summary>Crea un documento con el backend PDFsharp por omisión.</summary>
    public PrawnDocument(DocumentOptions? options = null)
        : this(options, new PdfSharpBackend())
    {
    }

    /// <summary>Crea un documento con un backend explícito (para pruebas o iText).</summary>
    public PrawnDocument(DocumentOptions? options, IPdfBackend backend)
    {
        _options = options ?? new DocumentOptions();
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _backend.Compress = _options.Compress;
        _backend.Metadata = _options.Metadata;
        _backend.Security = _options.Security;
        _font = _fonts.Resolve("Helvetica");
        _page = _backend.AddPage(new PageSetup(_options.PageSize));
        _cursorY = _options.Margin.Top;
        _bounds = MarginBox();
    }

    public int PageCount => _backend.PageCount;

    public Size PageSize => _options.PageSize;

    public Thickness Margin => _options.Margin;

    /// <summary>Posición vertical actual del cursor, desde el borde superior.</summary>
    public double CursorY => _cursorY;

    /// <summary>Área de texto actual (márgenes o bounding box activo) en coordenadas de Prawn.</summary>
    public Rect Bounds => _bounds;

    public string FontFamily => _font.Family;

    public double FontSize => _fontSize;

    /// <summary>
    /// Registra una familia TTF/OTF. Las caras que no se indiquen usan la regular.
    /// </summary>
    public void RegisterFontFamily(
        string family,
        string regularPath,
        string? boldPath = null,
        string? italicPath = null,
        string? boldItalicPath = null)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(family);
        ArgumentException.ThrowIfNullOrWhiteSpace(regularPath);

        _fonts.Register(family, new FontFace(family, FontStyle.Regular, regularPath));

        if (!string.IsNullOrWhiteSpace(boldPath))
        {
            _fonts.Register(family, new FontFace(family, FontStyle.Bold, boldPath));
        }

        if (!string.IsNullOrWhiteSpace(italicPath))
        {
            _fonts.Register(family, new FontFace(family, FontStyle.Italic, italicPath));
        }

        if (!string.IsNullOrWhiteSpace(boldItalicPath))
        {
            _fonts.Register(family, new FontFace(family, FontStyle.BoldItalic, boldItalicPath));
        }
    }

    /// <summary>Selecciona la fuente base-14 o registrada.</summary>
    public void Font(string family, double? size = null, FontStyle style = FontStyle.Regular)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(family);

        if (size is { } requestedSize)
        {
            if (requestedSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(size), "El tamaño de fuente debe ser positivo.");
            }

            _fontSize = requestedSize;
        }

        _font = _fonts.Resolve(family, style);
    }

    /// <summary>
    /// Dibuja texto desde el cursor, cortando por el ancho de la caja y respetando
    /// <c>\n</c>. La justificación distribuye el espacio sobrante con <c>Tw</c> en las
    /// líneas que no son la última del párrafo.
    /// </summary>
    public void Text(string text, TextOptions? options = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(text);

        options ??= new TextOptions();

        var size = options.Size ?? _fontSize;
        var color = options.Color ?? PdfColor.Black;
        var metrics = _backend.GetFontMetrics(_font);
        var lineHeight = metrics.LineHeightAt(size) + Math.Max(options.Leading, 0);
        var kerning = options.Kerning ?? _options.DefaultKerning;
        var width = options.Width ?? _bounds.Width;
        var lines = TextLayout.Wrap(
            text,
            width,
            metrics,
            size,
            kerning,
            options.IndentParagraphs,
            options.CharacterSpacing);
        var ascent = metrics.AscentAt(size);
        var finalGapAdjustment = options.FinalGap ? 0 : lineHeight - (ascent - metrics.DescentAt(size));
        var pageBottom = _options.PageSize.Height - _options.Margin.Bottom;
        var boxHeight = options.Height;
        var startY = _cursorY;

        if (boxHeight is { } height)
        {
            var contentHeight = lines.Count * lineHeight;
            var offset = options.Valign switch
            {
                TextValign.Top => 0,
                TextValign.Center => Math.Max((height - contentHeight) / 2, 0),
                TextValign.Bottom => Math.Max(height - contentHeight, 0),
                _ => 0,
            };

            _cursorY = startY + offset;
        }

        foreach (var line in lines)
        {
            if (boxHeight is null &&
                _boundingBoxDepth == 0 &&
                _cursorY + lineHeight > pageBottom &&
                _cursorY > _bounds.Top + 0.5)
            {
                StartNewPage();
            }

            if (line.Text.Length > 0)
            {
                // La fuente se registra por página: tras un salto, /F1 debe existir en
                // los recursos de la hoja nueva.
                var handle = _page.RegisterFont(_font);
                var available = width - line.Indent;

                var x = options.Align switch
                {
                    TextAlign.Left or TextAlign.Justify => _bounds.Left + line.Indent,
                    TextAlign.Center => _bounds.Left + line.Indent + Math.Max(available - line.Width, 0) / 2,
                    TextAlign.Right => _bounds.Left + line.Indent + Math.Max(available - line.Width, 0),
                    _ => throw new UnsupportedFeatureException($"Alineación no soportada: {options.Align}."),
                };

                var wordSpacing = 0.0;

                if (options.Align == TextAlign.Justify && !line.IsLastLine && line.SpaceCount > 0)
                {
                    wordSpacing = Math.Max((available - line.Width) / line.SpaceCount, 0);
                }

                var baseline = new Point(x, _cursorY + ascent);
                _page.DrawText(
                    new TextRun(
                        line.Text,
                        handle,
                        size,
                        color,
                        options.CharacterSpacing,
                        Kerning: kerning,
                        WordSpacing: wordSpacing,
                        RenderMode: options.Mode,
                        Rotation: options.Rotate),
                    baseline);
            }

            _cursorY += lineHeight;
        }

        if (boxHeight is { } fixedHeight)
        {
            _cursorY = startY + fixedHeight;
        }
        else if (!options.FinalGap)
        {
            _cursorY -= finalGapAdjustment;
        }
    }

    /// <summary>Alto que ocuparía el texto con las opciones dadas, sin dibujarlo.</summary>
    public double HeightOf(string text, TextOptions? options = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(text);

        var size = options?.Size ?? _fontSize;
        var kerning = options?.Kerning ?? _options.DefaultKerning;
        var metrics = _backend.GetFontMetrics(_font);
        var width = options?.Width ?? _bounds.Width;
        var lines = TextLayout.Wrap(
            text,
            width,
            metrics,
            size,
            kerning,
            options?.IndentParagraphs ?? 0,
            options?.CharacterSpacing ?? 0);
        var lineHeight = metrics.LineHeightAt(size) + Math.Max(options?.Leading ?? 0, 0);
        var height = lines.Count * lineHeight;

        if (options is { FinalGap: false })
        {
            height -= lineHeight - (metrics.AscentAt(size) - metrics.DescentAt(size));
        }

        return Math.Max(height, 0);
    }

    /// <summary>Dibuja texto con marcado inline (<c>&lt;b&gt;</c>, <c>&lt;color&gt;</c>...).</summary>
    public void FormattedText(string markup, TextOptions? options = null) =>
        FormattedText(PrawnSharp.FormattedText.Parse(markup), options);

    /// <summary>Dibuja una colección de fragmentos con estilos.</summary>
    public void FormattedText(PrawnSharp.FormattedText text, TextOptions? options = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(text);

        options ??= new TextOptions();

        var size = options.Size ?? _fontSize;
        var color = options.Color ?? PdfColor.Black;
        var kerning = options.Kerning ?? _options.DefaultKerning;
        var resolver = new Func<FontFace, IFontMetrics>(face => _backend.GetFontMetrics(face));
        var resolveFace = new Func<FontFace, FontFace>(face => _fonts.Resolve(face.Family, face.Style));
        var baseStyle = FormattedTextLayout.ResolveBase(_font, size, color, options.CharacterSpacing, resolver);
        var style = FormattedTextLayout.Resolve(text.BaseStyle, baseStyle, resolver, resolveFace);
        var width = options.Width ?? _bounds.Width;
        var lines = FormattedTextLayout.Wrap(
            text,
            width,
            style,
            kerning,
            options.IndentParagraphs,
            Math.Max(options.Leading, 0),
            resolver,
            resolveFace);
        var pageBottom = _options.PageSize.Height - _options.Margin.Bottom;
        var boxHeight = options.Height;
        var startY = _cursorY;

        if (boxHeight is { } height)
        {
            var contentHeight = lines.Sum(l => l.LineHeight);
            var offset = options.Valign switch
            {
                TextValign.Top => 0,
                TextValign.Center => Math.Max((height - contentHeight) / 2, 0),
                TextValign.Bottom => Math.Max(height - contentHeight, 0),
                _ => 0,
            };

            _cursorY = startY + offset;
        }

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];

            if (index > 0)
            {
                // Prawn separa las líneas con la altura de la línea a la que baja,
                // no con la de la línea que acaba de dibujar.
                _cursorY += line.LineHeight;
            }

            if (boxHeight is null &&
                _boundingBoxDepth == 0 &&
                _cursorY + line.LineHeight > pageBottom &&
                _cursorY > _bounds.Top + 0.5)
            {
                StartNewPage();
            }

            var available = width - line.Indent;

            var x = options.Align switch
            {
                TextAlign.Left or TextAlign.Justify => _bounds.Left + line.Indent,
                TextAlign.Center => _bounds.Left + line.Indent + Math.Max(available - line.Width, 0) / 2,
                TextAlign.Right => _bounds.Left + line.Indent + Math.Max(available - line.Width, 0),
                _ => throw new UnsupportedFeatureException($"Alineación no soportada: {options.Align}."),
            };

            var extraSpace = 0.0;

            if (options.Align == TextAlign.Justify && !line.IsLastLine && line.SpaceCount > 0)
            {
                extraSpace = Math.Max((available - line.Width) / line.SpaceCount, 0);
            }

            var baselineY = _cursorY + line.Ascent;

            foreach (var token in line.Tokens)
            {
                if (token.IsLineBreak)
                {
                    continue;
                }

                var tokenWidth = FormattedTextLayout.Measure(token, kerning);

                if (token.IsSpace)
                {
                    // Se dibuja el espacio (no sólo se avanza x): así el texto extraído
                    // conserva la separación entre palabras, como en Prawn.
                    var spaceHandle = _page.RegisterFont(token.Style.Face);
                    var space = token.Text == "\t" ? " " : token.Text;

                    _page.DrawText(
                        new TextRun(
                            space,
                            spaceHandle,
                            token.Style.Size,
                            token.Style.Color,
                            token.Style.CharacterSpacing,
                            Kerning: false,
                            TextRise: token.Style.Rise,
                            RenderMode: options.Mode,
                            Rotation: options.Rotate),
                        new Point(x, baselineY));

                    x += tokenWidth + extraSpace;
                    continue;
                }

                var handle = _page.RegisterFont(token.Style.Face);
                var baseline = new Point(x, baselineY);

                _page.DrawText(
                    new TextRun(
                        token.Text,
                        handle,
                        token.Style.Size,
                        token.Style.Color,
                        token.Style.CharacterSpacing,
                        Kerning: kerning,
                        TextRise: token.Style.Rise,
                        RenderMode: options.Mode,
                        Rotation: options.Rotate),
                    baseline);

                if (token.Style.Underline)
                {
                    var underlineY = baselineY -
                                     token.Style.Font.UnderlinePositionAt(token.Style.Size);
                    var thickness = Math.Max(
                        token.Style.Font.UnderlineThicknessAt(token.Style.Size),
                        0.3);

                    _page.DrawLine(
                        new Point(x, underlineY),
                        new Point(x + tokenWidth, underlineY),
                        new StrokeStyle(token.Style.Color, thickness));
                }

                if (token.Style.Strikethrough)
                {
                    var strikeY = baselineY - (token.Style.Size * 0.3);

                    _page.DrawLine(
                        new Point(x, strikeY),
                        new Point(x + tokenWidth, strikeY),
                        new StrokeStyle(token.Style.Color, 0.5));
                }

                if (!string.IsNullOrWhiteSpace(token.Style.Link))
                {
                    _page.AddLink(
                        new Rect(x, _cursorY, tokenWidth, line.LineHeight),
                        token.Style.Link);
                }

                x += tokenWidth;
            }
        }

        if (lines.Count > 0)
        {
            _cursorY += lines[^1].LineHeight;
        }

        if (boxHeight is { } fixedHeight)
        {
            _cursorY = startY + fixedHeight;
        }
        else if (!options.FinalGap && lines.Count > 0)
        {
            var last = lines[^1];
            _cursorY -= last.LineHeight - (last.Ascent + last.Descent);
        }
    }

    /// <summary>Alto de texto formateado con las opciones dadas, sin dibujarlo.</summary>
    public double HeightOf(PrawnSharp.FormattedText text, TextOptions? options = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(text);

        options ??= new TextOptions();

        var size = options.Size ?? _fontSize;
        var color = options.Color ?? PdfColor.Black;
        var kerning = options.Kerning ?? _options.DefaultKerning;
        var resolver = new Func<FontFace, IFontMetrics>(face => _backend.GetFontMetrics(face));
        var resolveFace = new Func<FontFace, FontFace>(face => _fonts.Resolve(face.Family, face.Style));
        var baseStyle = FormattedTextLayout.ResolveBase(_font, size, color, options.CharacterSpacing, resolver);
        var style = FormattedTextLayout.Resolve(text.BaseStyle, baseStyle, resolver, resolveFace);
        var width = options.Width ?? _bounds.Width;
        var lines = FormattedTextLayout.Wrap(
            text,
            width,
            style,
            kerning,
            options.IndentParagraphs,
            Math.Max(options.Leading, 0),
            resolver,
            resolveFace);

        var height = lines.Sum(l => l.LineHeight);

        if (!options.FinalGap && lines.Count > 0)
        {
            var last = lines[^1];
            height -= last.LineHeight - (last.Ascent + last.Descent);
        }

        return Math.Max(height, 0);
    }

    /// <summary>Ancho de una cadena con la fuente actual, en puntos.</summary>
    public double MeasureText(string text, TextOptions? options = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(text);

        var size = options?.Size ?? _fontSize;
        var kerning = options?.Kerning ?? false;
        return _backend.GetFontMetrics(_font).WidthOf(text, size, kerning);
    }

    /// <summary>Alias de <see cref="MeasureText"/> al estilo Prawn.</summary>
    public double WidthOf(string text, double? size = null, bool kerning = false)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(text);

        return _backend.GetFontMetrics(_font).WidthOf(text, size ?? _fontSize, kerning);
    }

    public void MoveDown(double points)
    {
        ThrowIfDisposed();
        _cursorY += points;
    }

    public void MoveUp(double points)
    {
        ThrowIfDisposed();
        _cursorY -= points;
    }

    public void MoveTo(double y)
    {
        ThrowIfDisposed();
        _cursorY = y;
    }

    /// <summary>
    /// Ejecuta un bloque dentro de una caja. <paramref name="topLeft"/> es relativo al
    /// área actual (como en Prawn); al salir se restauran el área y el cursor.
    /// </summary>
    public void BoundingBox(Point topLeft, double width, double height, Action<BoundingBox> action)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(action);

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "El ancho y el alto deben ser positivos.");
        }

        var absoluteX = _bounds.Left + topLeft.X;
        var absoluteY = _bounds.Top + topLeft.Y;
        var previousBounds = _bounds;
        var previousCursor = _cursorY;

        _bounds = new Rect(absoluteX, absoluteY, width, height);
        _cursorY = absoluteY;
        _boundingBoxDepth++;

        try
        {
            action(new BoundingBox(this, _bounds));
        }
        finally
        {
            _boundingBoxDepth--;
            _bounds = previousBounds;
            _cursorY = previousCursor;
        }
    }

    /// <summary>
    /// Ejecuta un bloque en coordenadas absolutas de la hoja (sin márgenes), como
    /// <c>canvas</c> en Prawn. Mantiene la posición del cursor al salir.
    /// </summary>
    public void Canvas(Action<BoundingBox> action)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(action);

        var previousBounds = _bounds;

        _bounds = new Rect(0, 0, _options.PageSize.Width, _options.PageSize.Height);
        _boundingBoxDepth++;

        try
        {
            action(new BoundingBox(this, _bounds));
        }
        finally
        {
            _boundingBoxDepth--;
            _bounds = previousBounds;
        }
    }

    /// <summary>Empieza una página nueva y devuelve el cursor al margen superior.</summary>
    public void StartNewPage()
    {
        ThrowIfDisposed();
        _page = _backend.AddPage(new PageSetup(_options.PageSize));
        _cursorY = _options.Margin.Top;
        _bounds = MarginBox();
        RunRepeaters();
    }

    /// <summary>
    /// Registra un bloque que se dibujará en cada página nueva (y también en la actual).
    /// El cursor y el área se restauran al terminar.
    /// </summary>
    public void AddRepeater(Action<PrawnDocument> action)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(action);
        _repeaters.Add(action);
        RunRepeaters();
    }

    private void RunRepeaters()
    {
        foreach (var repeater in _repeaters)
        {
            var bounds = _bounds;
            var cursor = _cursorY;

            try
            {
                repeater(this);
            }
            finally
            {
                _bounds = bounds;
                _cursorY = cursor;
            }
        }
    }

    /// <summary>Crea un stamp reutilizable con contenido relativo a su caja.</summary>
    public Stamp CreateStamp(double width, double height, Action<BoundingBox> draw)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(draw);
        return new Stamp(width, height, draw);
    }

    /// <summary>Coloca un stamp en coordenadas absolutas de la hoja.</summary>
    public void StampAt(Stamp stamp, double x, double y)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(stamp);

        var previousBounds = _bounds;
        var previousCursor = _cursorY;
        _bounds = new Rect(x, y, stamp.Width, stamp.Height);
        _cursorY = y;
        _boundingBoxDepth++;

        try
        {
            stamp.Draw(new BoundingBox(this, _bounds));
        }
        finally
        {
            _boundingBoxDepth--;
            _bounds = previousBounds;
            _cursorY = previousCursor;
        }
    }

    public void StampAt(Stamp stamp, Point at) => StampAt(stamp, at.X, at.Y);

    /// <summary>Dibuja una cuadrícula y entrega cada celda al callback.</summary>
    public void Grid(
        int columns,
        int rows,
        double cellWidth,
        double cellHeight,
        Action<BoundingBox, int, int> drawCell,
        PdfColor? borderColor = null,
        double borderWidth = 0.5)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellHeight);
        ArgumentNullException.ThrowIfNull(drawCell);

        var startX = _bounds.Left;
        var startY = _cursorY;

        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var rect = new Rect(
                    startX + (column * cellWidth),
                    startY + (row * cellHeight),
                    cellWidth,
                    cellHeight);

                if (borderColor is { } border)
                {
                    Rectangle(rect.X, rect.Y, rect.Width, rect.Height, null, border, borderWidth);
                }

                var previousBounds = _bounds;
                var previousCursor = _cursorY;
                _bounds = rect;
                _cursorY = rect.Top;
                _boundingBoxDepth++;

                try
                {
                    drawCell(new BoundingBox(this, rect), row, column);
                }
                finally
                {
                    _boundingBoxDepth--;
                    _bounds = previousBounds;
                    _cursorY = previousCursor;
                }
            }
        }

        _cursorY = startY + (rows * cellHeight);
    }

    public void Line(double x1, double y1, double x2, double y2, double width = 1, PdfColor? color = null)
    {
        ThrowIfDisposed();
        DrawPath(
            Path.Create().MoveTo(x1, y1).LineTo(x2, y2),
            null,
            new StrokeStyle(color ?? PdfColor.Black, width));
    }

    public void Rectangle(
        double x,
        double y,
        double width,
        double height,
        PdfColor? fill = null,
        PdfColor? stroke = null,
        double strokeWidth = 1)
    {
        ThrowIfDisposed();
        DrawPath(
            Path.Create().Rectangle(x, y, width, height),
            fill is null ? null : Paint.Fill(fill.Value),
            stroke is null ? null : new StrokeStyle(stroke.Value, strokeWidth));
    }

    /// <summary>Dibuja un path con relleno y/o trazo.</summary>
    public void DrawPath(Path path, Paint? fill, StrokeStyle? stroke, FillRule fillRule = FillRule.NonZero)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(path);
        _page.DrawPath(path, fill, stroke, fillRule);
    }

    public void Fill(Path path, PdfColor color, double alpha = 1) =>
        DrawPath(path, Paint.Solid(color, alpha), null);

    public void FillGradient(Path path, Gradient gradient, double alpha = 1) =>
        DrawPath(path, Paint.GradientFill(gradient, alpha), null);

    public void Stroke(
        Path path,
        PdfColor color,
        double width = 1,
        double alpha = 1,
        LineCap cap = LineCap.Butt,
        LineJoin join = LineJoin.Miter,
        IReadOnlyList<double>? dash = null,
        double dashPhase = 0) =>
        DrawPath(path, null, StrokeStyle.Create(color, width, alpha, cap, join, dash: dash, dashPhase: dashPhase));

    public void StrokeGradient(
        Path path,
        Gradient gradient,
        double width = 1,
        double alpha = 1,
        LineCap cap = LineCap.Butt,
        LineJoin join = LineJoin.Miter,
        IReadOnlyList<double>? dash = null,
        double dashPhase = 0) =>
        DrawPath(path, null, StrokeStyle.Create(default, width, alpha, cap, join, dash: dash, dashPhase: dashPhase, gradient: gradient));

    /// <summary>Guarda transformación, clip y estado gráfico.</summary>
    public void SaveGraphicsState()
    {
        ThrowIfDisposed();
        _page.SaveState();
    }

    /// <summary>Restaura el último estado guardado.</summary>
    public void RestoreGraphicsState()
    {
        ThrowIfDisposed();
        _page.RestoreState();
    }

    public void SetClip(Path path, FillRule fillRule = FillRule.NonZero)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(path);
        _page.SetClip(path, fillRule);
    }

    public void Translate(double x, double y)
    {
        ThrowIfDisposed();
        _page.ConcatMatrix(Matrix3x2.CreateTranslation((float)x, (float)y));
    }

    public void Scale(double x, double y)
    {
        ThrowIfDisposed();
        _page.ConcatMatrix(Matrix3x2.CreateScale((float)x, (float)y));
    }

    public void Rotate(double degrees)
    {
        ThrowIfDisposed();
        var radians = degrees * Math.PI / 180.0;
        _page.ConcatMatrix(Matrix3x2.CreateRotation((float)radians));
    }

    public void Skew(double xDegrees, double yDegrees)
    {
        ThrowIfDisposed();
        var x = xDegrees * Math.PI / 180.0;
        var y = yDegrees * Math.PI / 180.0;
        _page.ConcatMatrix(Matrix3x2.CreateSkew((float)x, (float)y));
    }

    /// <summary>Establece el alfa de relleno/trazo y el modo de mezcla hasta restaurar el estado.</summary>
    public void SetAlpha(double fillAlpha, double strokeAlpha, BlendMode blendMode = BlendMode.Normal)
    {
        ThrowIfDisposed();
        _page.SetAlpha(Math.Clamp(fillAlpha, 0, 1), Math.Clamp(strokeAlpha, 0, 1), blendMode);
    }

    /// <summary>Coloca una imagen PNG/JPEG desde archivo.</summary>
    public void Image(string path, ImageOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Image(ImageData.FromFile(path), options);
    }

    /// <summary>Coloca una imagen PNG/JPEG con tamaño natural u opciones explícitas.</summary>
    public void Image(ImageData image, ImageOptions? options = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(image);

        var dpi = options?.Dpi ?? 0;
        var naturalWidth = image.WidthAt(dpi);
        var naturalHeight = image.HeightAt(dpi);
        var width = options?.Width;
        var height = options?.Height;

        if (options?.Scale is { } scale)
        {
            width ??= naturalWidth * scale;
            height ??= naturalHeight * scale;
        }

        if (width is null && height is null)
        {
            width = naturalWidth;
            height = naturalHeight;
        }
        else if (width is null)
        {
            width = naturalWidth * (height!.Value / naturalHeight);
        }
        else if (height is null)
        {
            height = naturalHeight * (width!.Value / naturalWidth);
        }

        if (options?.Fit == true && options.Width is { } fitWidth && options.Height is { } fitHeight)
        {
            var fitScale = Math.Min(fitWidth / width!.Value, fitHeight / height!.Value);
            width *= fitScale;
            height *= fitScale;
        }

        if (width <= 0 || height <= 0)
        {
            return;
        }

        var x = options?.AtX ?? _bounds.Left;
        var y = options?.AtY ?? _cursorY;
        _page.DrawImage(image, new Rect(x, y, width.Value, height.Value));
    }

    /// <summary>Coloca una imagen en coordenadas absolutas y tamaño opcional.</summary>
    public void Image(
        ImageData image,
        double x,
        double y,
        double? width = null,
        double? height = null,
        bool fit = false) =>
        Image(image, new ImageOptions
        {
            AtX = x,
            AtY = y,
            Width = width,
            Height = height,
            Fit = fit,
        });

    /// <summary>Coloca una imagen a un DPI efectivo concreto.</summary>
    public void ImageAtDpi(ImageData image, double x, double y, double dpi) =>
        Image(image, new ImageOptions { AtX = x, AtY = y, Dpi = dpi });

    /// <summary>Convierte píxeles a puntos con el DPI indicado.</summary>
    public static double PixelsToPoints(int pixels, double dpi) =>
        pixels * 72.0 / (dpi > 0 ? dpi : 72);

    /// <summary>Dibuja una tabla en el cursor actual.</summary>
    public void Table(Table table, TableOptions? options = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(table);
        TableLayout.Render(this, table, options);
    }

    /// <summary>Añade un marcador/outline al documento.</summary>
    public void AddOutline(string title, int? pageIndex = null, double? top = null, bool opened = true)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        _backend.AddOutline(title, pageIndex ?? Math.Max(PageCount - 1, 0), top, opened);
    }

    /// <summary>Añade un enlace interno a otra página.</summary>
    public void AddLinkToPage(Rect rect, int pageIndex, double? top = null)
    {
        ThrowIfDisposed();
        _page.AddLinkToPage(rect, pageIndex, top);
    }

    /// <summary>Alias de <see cref="AddLinkToPage"/>.</summary>
    public void LinkToPage(Rect rect, int pageIndex, double? top = null) =>
        AddLinkToPage(rect, pageIndex, top);

    /// <summary>Renderiza el PDF a memoria.</summary>
    public byte[] RenderBytes()
    {
        ThrowIfDisposed();

        using var output = new MemoryStream();
        _backend.Save(output);
        return output.ToArray();
    }

    /// <summary>Renderiza el PDF a disco.</summary>
    public void RenderFile(string path)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        File.WriteAllBytes(path, RenderBytes());
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _backend.Dispose();
        _disposed = true;
    }

    private Rect MarginBox() => new(
        _options.Margin.Left,
        _options.Margin.Top,
        _options.PageSize.Width - _options.Margin.Horizontal,
        _options.PageSize.Height - _options.Margin.Vertical);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
