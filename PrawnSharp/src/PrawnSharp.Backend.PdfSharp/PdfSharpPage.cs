using System.Globalization;
using System.Numerics;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PrawnSharp.Fonts;
using SkiaSharp;

namespace PrawnSharp.Backend.PdfSharp;

/// <summary>Página PDFsharp. Recibe coordenadas de Prawn (arriba-izquierda) y las convierte.</summary>
internal sealed class PdfSharpPage : IPdfPage
{
    private readonly PdfDocument _document;
    private readonly PdfPage _page;
    private readonly Dictionary<string, PdfFontHandle> _fonts = new(StringComparer.Ordinal);
    private readonly Dictionary<ImageData, (XImage Image, MemoryStream Stream)> _images = [];
    private readonly Stack<Matrix3x2> _stateStack = new();
    private int _fontSequence;
    private int _gsSequence;
    private Matrix3x2 _transform = Matrix3x2.Identity;

    public PdfSharpPage(PdfDocument document, PdfPage page)
    {
        _document = document;
        _page = page;
    }

    public double Width => _page.Width.Point;

    public double Height => _page.Height.Point;

    public PdfFontHandle RegisterFont(FontFace face)
    {
        ArgumentNullException.ThrowIfNull(face);

        var key = face.ToString();

        if (_fonts.TryGetValue(key, out var existing))
        {
            return existing;
        }

        if (face.FilePath is not null)
        {
            // La fuente TTF/OTF la registra y la incrusta PDFsharp al dibujar.
            PdfSharpFontRegistry.Register(face);
            var ttfHandle = new PdfFontHandle(string.Empty, face);
            _fonts[key] = ttfHandle;
            return ttfHandle;
        }

        if (!Base14Fonts.TryGetPostScriptName(face, out var postScriptName))
        {
            throw new UnsupportedFontException(
                $"El backend PDFsharp de esta fase sólo incrusta las 14 fuentes base de PDF. " +
                $"La fuente '{face}' necesita TTF/OTF (fase F5).");
        }

        // Fuente Type1 base-14: no se incrusta archivo, el visor la conoce.
        var font = new PdfDictionary(_document);
        font.Elements["/Type"] = new PdfName("/Font");
        font.Elements["/Subtype"] = new PdfName("/Type1");
        font.Elements["/BaseFont"] = new PdfName("/" + postScriptName);
        font.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");

        var fonts = _page.Resources.Elements.GetDictionary("/Font");

        if (fonts is null)
        {
            fonts = new PdfDictionary(_document);
            _page.Resources.Elements["/Font"] = fonts;
        }

        var resourceName = $"/F{++_fontSequence}";
        fonts.Elements[resourceName] = font;

        var handle = new PdfFontHandle(resourceName, face);
        _fonts[key] = handle;
        return handle;
    }

    public void DrawText(TextRun run, Point baseline)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (string.IsNullOrEmpty(run.Text))
        {
            return;
        }

        if (run.Font.Face.FilePath is not null)
        {
            DrawTtfText(run, baseline);
            return;
        }

        var pdfY = Height - baseline.Y;
        var ops = new StringBuilder(128);

        ops.Append("q\n");
        ops.Append(run.Color.ToPdfRgb()).Append(" rg\n");
        ops.Append("BT\n");
        ops.Append(run.Font.ResourceName).Append(' ')
           .Append(WinAnsi.Number(run.Size)).Append(" Tf\n");

        if (run.CharacterSpacing != 0)
        {
            ops.Append(WinAnsi.Number(run.CharacterSpacing)).Append(" Tc\n");
        }

        if (run.WordSpacing != 0)
        {
            ops.Append(WinAnsi.Number(run.WordSpacing)).Append(" Tw\n");
        }

        if (Math.Abs(run.HorizontalScaling - 1) > double.Epsilon)
        {
            ops.Append(WinAnsi.Number(run.HorizontalScaling * 100)).Append(" Tz\n");
        }

        if (run.TextRise != 0)
        {
            ops.Append(WinAnsi.Number(run.TextRise)).Append(" Ts\n");
        }

        if (run.RenderMode != TextRenderMode.Fill)
        {
            ops.Append((int)run.RenderMode).Append(" Tr\n");
        }

        // La matriz de texto rota alrededor del punto de la línea base.
        var radians = run.Rotation * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);

        ops.Append(WinAnsi.Number(cos)).Append(' ')
           .Append(WinAnsi.Number(sin)).Append(' ')
           .Append(WinAnsi.Number(-sin)).Append(' ')
           .Append(WinAnsi.Number(cos)).Append(' ')
           .Append(WinAnsi.Number(baseline.X)).Append(' ')
           .Append(WinAnsi.Number(pdfY)).Append(" Tm\n");

        if (run.Kerning)
        {
            AppendKernedText(ops, run);
        }
        else
        {
            ops.Append('<').Append(WinAnsi.EncodeHex(run.Text)).Append("> Tj\n");
        }

        ops.Append("ET\n");
        ops.Append("Q\n");

        AppendRaw(ops.ToString());
    }

    private static void AppendKernedText(StringBuilder ops, TextRun run)
    {
        var afm = TryGetAfm(run.Font.Face);

        if (afm is null)
        {
            ops.Append('<').Append(WinAnsi.EncodeHex(run.Text)).Append("> Tj\n");
            return;
        }

        var bytes = WinAnsi.Encode(run.Text);

        if (bytes.Length == 0)
        {
            return;
        }

        var current = new StringBuilder(16);
        var parts = new List<string>();

        for (var i = 0; i < bytes.Length; i++)
        {
            current.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));

            if (i + 1 >= bytes.Length)
            {
                continue;
            }

            if (IsWordSeparator(bytes[i]) || IsWordSeparator(bytes[i + 1]))
            {
                continue;
            }

            var kern = afm.GetKerning(bytes[i], bytes[i + 1]);

            if (kern == 0)
            {
                continue;
            }

            // En TJ un número positivo mueve a la izquierda; el AFM da -70 para A/V,
            // así que se emite -kern (+70) para acercar los glifos.
            parts.Add("<" + current + ">");
            parts.Add((-kern).ToString("0.###", CultureInfo.InvariantCulture));
            current.Clear();
        }

        if (current.Length > 0)
        {
            parts.Add("<" + current + ">");
        }

        ops.Append('[').Append(string.Join(' ', parts)).Append("] TJ\n");
    }

    private static AfmFont? TryGetAfm(FontFace face)
    {
        if (!Base14Fonts.TryGetPostScriptName(face, out var postScriptName) ||
            postScriptName is "Symbol" or "ZapfDingbats")
        {
            return null;
        }

        return AfmFonts.Get(postScriptName);
    }

    private void DrawTtfText(TextRun run, Point baseline)
    {
        if (run.RenderMode != TextRenderMode.Fill)
        {
            throw new UnsupportedFeatureException(
                "Los modos de renderizado de texto TTF llegan después de F5; usa Fill.");
        }

        if (Math.Abs(run.Rotation) > 0.0001)
        {
            throw new UnsupportedFeatureException(
                "La rotación de texto TTF llega después de F5; usa 0.");
        }

        using var graphics = XGraphics.FromPdfPage(_page, XGraphicsPdfPageOptions.Append);
        var font = PdfSharpFontRegistry.CreateFont(run.Font.Face, run.Size);
        var brush = new XSolidBrush(XColor.FromArgb(run.Color.R, run.Color.G, run.Color.B));
        var format = new XStringFormat
        {
            LineAlignment = XLineAlignment.BaseLine,
            Alignment = XStringAlignment.Near,
        };
        var y = baseline.Y - run.TextRise;
        var info = TrueTypeFontInfo.TryLoad(run.Font.Face.FilePath);
        var useKerning = run.Kerning && info is { HasKerning: true };
        var useMetrics = info is { HasWidths: true };

        if (!useMetrics && !useKerning && run.CharacterSpacing == 0)
        {
            graphics.DrawString(run.Text, font, brush, baseline.X, y, format);
            return;
        }

        var codePoints = TrueTypeFontInfo.EnumerateCodePoints(run.Text).ToList();
        var x = baseline.X;

        for (var i = 0; i < codePoints.Count; i++)
        {
            var value = char.ConvertFromUtf32(codePoints[i]);
            graphics.DrawString(value, font, brush, x, y, format);

            var advance = useMetrics
                ? info!.AdvanceAt(codePoints[i], run.Size)
                : graphics.MeasureString(value, font).Width;

            if (useKerning && i + 1 < codePoints.Count)
            {
                advance += info!.KerningAt(codePoints[i], codePoints[i + 1], run.Size);
            }

            x += advance + run.CharacterSpacing;
        }
    }

    private static bool IsWordSeparator(byte value) => value is 9 or 10 or 13 or 32;

    public void DrawLine(Point from, Point toPoint, StrokeStyle stroke)
    {
        var ops = new StringBuilder(96);
        ops.Append("q\n");
        ops.Append(stroke.Color.ToPdfRgb()).Append(" RG\n");
        ops.Append(WinAnsi.Number(stroke.Width)).Append(" w\n");
        ops.Append(WinAnsi.Number(from.X)).Append(' ')
           .Append(WinAnsi.Number(Height - from.Y)).Append(" m ")
           .Append(WinAnsi.Number(toPoint.X)).Append(' ')
           .Append(WinAnsi.Number(Height - toPoint.Y)).Append(" l S\n");
        ops.Append("Q\n");
        AppendRaw(ops.ToString());
    }

    public void DrawRectangle(Rect rect, Paint? fill, StrokeStyle? stroke)
    {
        if (fill is null && stroke is null)
        {
            return;
        }

        var ops = new StringBuilder(128);
        ops.Append("q\n");

        if (fill is { } f)
        {
            ops.Append(f.Color.ToPdfRgb()).Append(" rg\n");
        }

        if (stroke is { } s)
        {
            ops.Append(s.Color.ToPdfRgb()).Append(" RG\n");
            ops.Append(WinAnsi.Number(s.Width)).Append(" w\n");
        }

        var pdfY = Height - (rect.Y + rect.Height);
        ops.Append(WinAnsi.Number(rect.X)).Append(' ')
           .Append(WinAnsi.Number(pdfY)).Append(' ')
           .Append(WinAnsi.Number(rect.Width)).Append(' ')
           .Append(WinAnsi.Number(rect.Height)).Append(" re\n");

        ops.Append(fill is not null && stroke is not null ? "B\n" : fill is not null ? "f\n" : "S\n");
        ops.Append("Q\n");
        AppendRaw(ops.ToString());
    }

    public void DrawPath(Path path, Paint? fill, StrokeStyle? stroke, FillRule fillRule = FillRule.NonZero)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (path.Commands.Count == 0 || (fill is null && stroke is null))
        {
            return;
        }
        if (fill?.Gradient is not null || stroke?.Gradient is not null)
        {
            DrawGradientPath(path, fill, stroke, fillRule);
            return;
        }

        var ops = new StringBuilder(256);
        ops.Append("q\n");
        AppendAlpha(ops, fill?.Alpha ?? 1, stroke?.Alpha ?? 1);
        AppendPath(ops, path);

        if (fill is { } f)
        {
            ops.Append(f.Color.ToPdfRgb()).Append(" rg\n");
        }

        if (stroke is { } s)
        {
            ops.Append(s.Color.ToPdfRgb()).Append(" RG\n");
            ops.Append(WinAnsi.Number(Math.Max(s.Width, 0.01))).Append(" w\n");
            AppendStrokeStyle(ops, s);
        }

        var op = fill is not null && stroke is not null
            ? (fillRule == FillRule.EvenOdd ? "B*" : "B")
            : fill is not null
                ? (fillRule == FillRule.EvenOdd ? "f*" : "f")
                : "S";

        ops.Append(op).Append('\n');
        ops.Append("Q\n");
        AppendRaw(ops.ToString());
    }

    public void DrawImage(ImageData image, Rect rect)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        if (!_images.TryGetValue(image, out var entry))
        {
            entry = CreateImageEntry(image);
            _images[image] = entry;
        }

        using var graphics = XGraphics.FromPdfPage(_page, XGraphicsPdfPageOptions.Append);
        graphics.DrawImage(entry.Image, rect.X, rect.Y, rect.Width, rect.Height);
    }

    private static (XImage Image, MemoryStream Stream) CreateImageEntry(ImageData image)
    {
        var stream = new MemoryStream(image.Data);

        try
        {
            return (XImage.FromStream(stream), stream);
        }
        catch (InvalidOperationException)
        {
            // PDFsharp 6.2.4 no decodifica PNG de 16 bits; Skia lo baja a 8 bits.
            stream.Dispose();
        }

        using var bitmap = SKBitmap.Decode(image.Data);

        if (bitmap is null)
        {
            throw new UnsupportedFeatureException(
                $"No se pudo decodificar la imagen '{image.Format}' ({image.PixelWidth}x{image.PixelHeight}).");
        }

        using var skImage = SKImage.FromBitmap(bitmap);
        using var encoded = skImage.Encode(SKEncodedImageFormat.Png, 100);
        var converted = new MemoryStream(encoded.ToArray());
        return (XImage.FromStream(converted), converted);
    }

    public void SaveState()
    {
        _stateStack.Push(_transform);
        AppendRaw("q\n");
    }

    public void RestoreState()
    {
        if (_stateStack.Count == 0)
        {
            throw new InvalidOperationException("No hay un estado gráfico guardado para restaurar.");
        }

        _transform = _stateStack.Pop();
        AppendRaw("Q\n");
    }

    public void SetClip(Path path, FillRule fillRule = FillRule.NonZero)
    {
        ArgumentNullException.ThrowIfNull(path);

        var ops = new StringBuilder(128);
        AppendPath(ops, path);
        ops.Append(fillRule == FillRule.EvenOdd ? "W* n\n" : "W n\n");
        AppendRaw(ops.ToString());
    }

    public void ConcatMatrix(Matrix3x2 matrix)
    {
        _transform = Matrix3x2.Multiply(_transform, matrix);
    }

    public void SetAlpha(double fillAlpha, double strokeAlpha, BlendMode blendMode = BlendMode.Normal)
    {
        var name = AddExtGState(fillAlpha, strokeAlpha, blendMode);
        AppendRaw(name + " gs\n");
    }

    private void AppendAlpha(StringBuilder ops, double fillAlpha, double strokeAlpha)
    {
        if (Math.Abs(fillAlpha - 1) < 0.0001 && Math.Abs(strokeAlpha - 1) < 0.0001)
        {
            return;
        }

        ops.Append(AddExtGState(fillAlpha, strokeAlpha, BlendMode.Normal)).Append(" gs\n");
    }

    private string AddExtGState(double fillAlpha, double strokeAlpha, BlendMode blendMode)
    {
        var resourceName = $"/GS{++_gsSequence}";
        var dictionary = new PdfDictionary(_document);
        dictionary.Elements["/Type"] = new PdfName("/ExtGState");
        dictionary.Elements["/ca"] = new PdfReal(Math.Clamp(fillAlpha, 0, 1));
        dictionary.Elements["/CA"] = new PdfReal(Math.Clamp(strokeAlpha, 0, 1));

        if (blendMode != BlendMode.Normal)
        {
            dictionary.Elements["/BM"] = new PdfName("/" + blendMode);
        }

        var states = _page.Resources.Elements.GetDictionary("/ExtGState");

        if (states is null)
        {
            states = new PdfDictionary(_document);
            _page.Resources.Elements["/ExtGState"] = states;
        }

        states.Elements[resourceName] = dictionary;
        return resourceName;
    }

    private static void AppendStrokeStyle(StringBuilder ops, StrokeStyle stroke)
    {
        ops.Append(stroke.Cap switch
        {
            LineCap.Round => "1 J\n",
            LineCap.Square => "2 J\n",
            _ => "0 J\n",
        });

        ops.Append(stroke.Join switch
        {
            LineJoin.Round => "1 j\n",
            LineJoin.Bevel => "2 j\n",
            _ => "0 j\n",
        });

        if (stroke.Join == LineJoin.Miter)
        {
            ops.Append(WinAnsi.Number(stroke.MiterLimit)).Append(" M\n");
        }

        if (stroke.Dash is { Count: > 0 } dash)
        {
            ops.Append('[')
               .Append(string.Join(' ', dash.Select(d => WinAnsi.Number(Math.Max(d, 0)))))
               .Append("] ")
               .Append(WinAnsi.Number(stroke.DashPhase))
               .Append(" d\n");
        }
        else
        {
            ops.Append("[] 0 d\n");
        }
    }

    private void AppendPath(StringBuilder ops, Path path)
    {
        foreach (var command in path.Commands)
        {
            switch (command.Kind)
            {
                case PathCommandKind.MoveTo:
                    AppendPoint(ops, command.P1);
                    ops.Append(" m\n");
                    break;

                case PathCommandKind.LineTo:
                    AppendPoint(ops, command.P1);
                    ops.Append(" l\n");
                    break;

                case PathCommandKind.CurveTo:
                    AppendPoint(ops, command.P1);
                    ops.Append(' ');
                    AppendPoint(ops, command.P2);
                    ops.Append(' ');
                    AppendPoint(ops, command.P3);
                    ops.Append(" c\n");
                    break;

                case PathCommandKind.Close:
                    ops.Append("h\n");
                    break;
            }
        }
    }

    private void AppendPoint(StringBuilder ops, Point point)
    {
        var transformed = Transform(point);
        ops.Append(WinAnsi.Number(transformed.X)).Append(' ')
           .Append(WinAnsi.Number(Height - transformed.Y));
    }

    private Point Transform(Point point)
    {
        if (_transform == Matrix3x2.Identity)
        {
            return point;
        }

        var value = Vector2.Transform(new Vector2((float)point.X, (float)point.Y), _transform);
        return new Point(value.X, value.Y);
    }

    private void DrawGradientPath(Path path, Paint? fill, StrokeStyle? stroke, FillRule fillRule)
    {
        using var graphics = XGraphics.FromPdfPage(_page, XGraphicsPdfPageOptions.Append);
        var xpath = new XGraphicsPath
        {
            FillMode = fillRule == FillRule.EvenOdd ? XFillMode.Alternate : XFillMode.Winding,
        };

        XPoint? current = null;

        foreach (var command in path.Commands)
        {
            switch (command.Kind)
            {
                case PathCommandKind.MoveTo:
                    var move = Transform(command.P1);
                    xpath.StartFigure();
                    current = ToXPoint(move);
                    break;

                case PathCommandKind.LineTo:
                    if (current is null)
                    {
                        throw new InvalidOperationException("Un path debe empezar con MoveTo.");
                    }

                    var line = ToXPoint(Transform(command.P1));
                    xpath.AddLine(current.Value, line);
                    current = line;
                    break;

                case PathCommandKind.CurveTo:
                    if (current is null)
                    {
                        throw new InvalidOperationException("Un path debe empezar con MoveTo.");
                    }

                    var c1 = ToXPoint(Transform(command.P1));
                    var c2 = ToXPoint(Transform(command.P2));
                    var end = ToXPoint(Transform(command.P3));
                    xpath.AddBezier(current.Value, c1, c2, end);
                    current = end;
                    break;

                case PathCommandKind.Close:
                    xpath.CloseFigure();
                    current = null;
                    break;
            }
        }

        var brush = fill is { } f ? CreateBrush(f) : null;
        var pen = stroke is { } s ? CreatePen(s) : null;

        // PDFsharp 6.2.4 pinta negro el XRadialGradientBrush; se aproxima con anillos.
        if (fill is { } radialPaint &&
            radialPaint.Gradient is { Kind: GradientKind.Radial } radialGradient)
        {
            DrawRadialFill(graphics, xpath, radialGradient, radialPaint.Alpha);

            if (pen is not null)
            {
                graphics.DrawPath(pen, xpath);
            }

            return;
        }

        if (brush is not null && pen is not null)
        {
            graphics.DrawPath(pen, brush, xpath);
        }
        else if (brush is not null)
        {
            graphics.DrawPath(brush, xpath);
        }
        else if (pen is not null)
        {
            graphics.DrawPath(pen, xpath);
        }
    }

    private void DrawRadialFill(
        XGraphics graphics,
        XGraphicsPath xpath,
        Gradient gradient,
        double alpha)
    {
        var stops = gradient.Stops.OrderBy(s => s.Offset).ToArray();

        if (stops.Length == 0)
        {
            return;
        }

        var center = Transform(gradient.Start);
        var scale = Math.Sqrt(Math.Abs((_transform.M11 * _transform.M22) - (_transform.M12 * _transform.M21)));
        var radius = Math.Max(gradient.Radius * scale, 0.01);
        var outer = ColorWithAlpha(stops[^1].Color, alpha);

        // Fuera del radio exterior rige el último color.
        graphics.DrawPath(new XSolidBrush(outer), xpath);
        graphics.IntersectClip(xpath);

        const int steps = 48;

        for (var i = steps; i >= 0; i--)
        {
            var t = i / (double)steps;
            var ringRadius = radius * t;
            var color = ColorWithAlpha(InterpolateColor(stops, t), alpha);
            var brush = new XSolidBrush(color);

            if (ringRadius <= 0.01)
            {
                graphics.DrawEllipse(brush, center.X, center.Y, 0.02, 0.02);
            }
            else
            {
                graphics.DrawEllipse(
                    brush,
                    center.X - ringRadius,
                    center.Y - ringRadius,
                    ringRadius * 2,
                    ringRadius * 2);
            }
        }
    }

    private static PdfColor InterpolateColor(GradientStop[] stops, double offset)
    {
        if (offset <= stops[0].Offset)
        {
            return stops[0].Color;
        }

        if (offset >= stops[^1].Offset)
        {
            return stops[^1].Color;
        }

        for (var i = 1; i < stops.Length; i++)
        {
            if (offset > stops[i].Offset)
            {
                continue;
            }

            var previous = stops[i - 1];
            var next = stops[i];
            var span = Math.Max(next.Offset - previous.Offset, 0.000001);
            var t = (offset - previous.Offset) / span;

            return new PdfColor(
                (byte)Math.Round(previous.Color.R + ((next.Color.R - previous.Color.R) * t)),
                (byte)Math.Round(previous.Color.G + ((next.Color.G - previous.Color.G) * t)),
                (byte)Math.Round(previous.Color.B + ((next.Color.B - previous.Color.B) * t)));
        }

        return stops[^1].Color;
    }

    private static XPoint ToXPoint(Point point) => new(point.X, point.Y);

    private XBrush CreateBrush(Paint paint)
    {
        if (paint.Gradient is null)
        {
            return new XSolidBrush(ColorWithAlpha(paint.Color, paint.Alpha));
        }

        var stops = paint.Gradient.Stops.OrderBy(s => s.Offset).ToArray();

        if (stops.Length == 0)
        {
            return new XSolidBrush(ColorWithAlpha(paint.Color, paint.Alpha));
        }

        var first = ColorWithAlpha(stops[0].Color, paint.Alpha);
        var last = ColorWithAlpha(stops[^1].Color, paint.Alpha);

        if (paint.Gradient.Kind == GradientKind.Radial)
        {
            var center = Transform(paint.Gradient.Start);
            return new XRadialGradientBrush(
                new XPoint(center.X, center.Y),
                new XPoint(center.X, center.Y),
                0,
                paint.Gradient.Radius,
                first,
                last);
        }

        var start = Transform(paint.Gradient.Start);
        var end = Transform(paint.Gradient.End);
        return new XLinearGradientBrush(
            new XPoint(start.X, start.Y),
            new XPoint(end.X, end.Y),
            first,
            last);
    }

    private static XPen CreatePen(StrokeStyle stroke)
    {
        var color = stroke.Gradient is { Stops.Count: > 0 } gradient
            ? gradient.Stops[0].Color
            : stroke.Color;
        var pen = new XPen(ColorWithAlpha(color, stroke.Alpha), Math.Max(stroke.Width, 0.01))
        {
            LineCap = stroke.Cap switch
            {
                LineCap.Round => XLineCap.Round,
                LineCap.Square => XLineCap.Square,
                _ => XLineCap.Flat,
            },
            LineJoin = stroke.Join switch
            {
                LineJoin.Round => XLineJoin.Round,
                LineJoin.Bevel => XLineJoin.Bevel,
                _ => XLineJoin.Miter,
            },
            MiterLimit = stroke.MiterLimit,
        };

        if (stroke.Dash is { Count: > 0 } dash)
        {
            pen.DashStyle = XDashStyle.Custom;
            pen.DashPattern = dash.Select(d => Math.Max(d, 0)).ToArray();
            pen.DashOffset = stroke.DashPhase;
        }

        return pen;
    }

    private static XColor ColorWithAlpha(PdfColor color, double alpha)
    {
        var a = (int)Math.Round(Math.Clamp(alpha, 0, 1) * 255);
        return XColor.FromArgb(a, color.R, color.G, color.B);
    }

    public void AppendRaw(string operators)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operators);

        var content = _page.Contents.AppendContent();
        content.CreateStream(Encoding.ASCII.GetBytes(operators));
    }

    public void AddLink(Rect rect, string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var pdfY = Height - rect.Bottom;
        _page.AddWebLink(
            new PdfRectangle(new XRect(rect.X, pdfY, rect.Width, rect.Height)),
            url);
    }

    public void AddLinkToPage(Rect rect, int pageIndex, double? top = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);

        var pdfY = Height - rect.Bottom;
        var point = top is { } value ? new XPoint(0, Height - value) : (XPoint?)null;
        _page.AddDocumentLink(
            new PdfRectangle(new XRect(rect.X, pdfY, rect.Width, rect.Height)),
            pageIndex,
            point);
    }
}
