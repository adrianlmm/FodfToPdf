using System.Globalization;
using System.Numerics;
using System.Text;
using PrawnPure.PdfCore.Document;
using PrawnPure.PdfCore.Objects;
using PrawnPure.TTFunk;
using PrawnSharp;
using PrawnSharp.Fonts;

namespace PrawnPure.Backend;

/// <summary>
/// Pure C# IPdfPage implementation backed by PrawnPure.PdfCore + PrawnPure.TTFunk.
/// Receives Prawn coordinates (top-left origin, Y downward) and emits native PDF content stream operators.
/// </summary>
internal sealed class PurePdfPage : IPdfPage
{
    private readonly PurePdfBackend _backend;
    private readonly PdfCoreDocument _document;
    private readonly Dictionary<string, (PdfFontHandle Handle, PdfTtfFont? TtfFont)> _pageFonts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pageImages = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pageGs = new(StringComparer.Ordinal);
    private readonly Stack<Matrix3x2> _stateStack = new();
    private Matrix3x2 _transform = Matrix3x2.Identity;

    internal PdfCorePage CorePage { get; }

    public PurePdfPage(PurePdfBackend backend, PdfCoreDocument document, PdfCorePage corePage)
    {
        _backend = backend;
        _document = document;
        CorePage = corePage;
    }

    public double Width => CorePage.Width;

    public double Height => CorePage.Height;

    public PdfFontHandle RegisterFont(FontFace face)
    {
        ArgumentNullException.ThrowIfNull(face);

        var key = face.FilePath is not null
            ? "TTF:" + System.IO.Path.GetFullPath(face.FilePath)
            : "B14:" + face.Family + ":" + face.Style;

        if (_pageFonts.TryGetValue(key, out var existing))
            return existing.Handle;

        var (resName, fontObj, ttfFont) = _backend.GetOrRegisterFont(face);
        CorePage.AddFontResource(resName, fontObj.Reference);

        var handle = new PdfFontHandle(resName, face);
        _pageFonts[key] = (handle, ttfFont);
        return handle;
    }

    public void DrawText(TextRun run, Point baseline)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (string.IsNullOrEmpty(run.Text)) return;

        // Ensure font is registered on this page even if handle came from another page
        var handle = RegisterFont(run.Font.Face);
        var key = run.Font.Face.FilePath is not null
            ? "TTF:" + System.IO.Path.GetFullPath(run.Font.Face.FilePath)
            : "B14:" + run.Font.Face.Family + ":" + run.Font.Face.Style;
        var ttfFont = _pageFonts[key].TtfFont;

        var pt = Transform(baseline);
        var pdfY = Height - pt.Y;
        var ops = new StringBuilder(128);

        ops.Append("q\n");
        ops.Append(run.Color.ToPdfRgb()).Append(" rg\n");
        if (run.RenderMode is TextRenderMode.Stroke or TextRenderMode.FillStroke or TextRenderMode.StrokeClip or TextRenderMode.FillStrokeClip)
        {
            ops.Append(run.Color.ToPdfRgb()).Append(" RG\n");
        }

        ops.Append("BT\n");
        ops.Append(handle.ResourceName).Append(' ').Append(Num(run.Size)).Append(" Tf\n");

        if (Math.Abs(run.CharacterSpacing) > 1e-6)
            ops.Append(Num(run.CharacterSpacing)).Append(" Tc\n");

        if (ttfFont is null && Math.Abs(run.WordSpacing) > 1e-6)
            ops.Append(Num(run.WordSpacing)).Append(" Tw\n");

        if (Math.Abs(run.HorizontalScaling - 1.0) > 1e-6)
            ops.Append(Num(run.HorizontalScaling * 100.0)).Append(" Tz\n");

        if (Math.Abs(run.TextRise) > 1e-6)
            ops.Append(Num(run.TextRise)).Append(" Ts\n");

        if (run.RenderMode != TextRenderMode.Fill)
            ops.Append((int)run.RenderMode).Append(" Tr\n");

        // Text matrix (supports arbitrary rotation around baseline point)
        if (Math.Abs(run.Rotation) > 1e-6)
        {
            var rad = run.Rotation * Math.PI / 180.0;
            var cos = Math.Cos(rad);
            var sin = Math.Sin(rad);
            ops.Append(Num(cos)).Append(' ')
               .Append(Num(sin)).Append(' ')
               .Append(Num(-sin)).Append(' ')
               .Append(Num(cos)).Append(' ')
               .Append(Num(pt.X)).Append(' ')
               .Append(Num(pdfY)).Append(" Tm\n");
        }
        else
        {
            ops.Append("1 0 0 1 ")
               .Append(Num(pt.X)).Append(' ')
               .Append(Num(pdfY)).Append(" Tm\n");
        }

        if (ttfFont is not null)
        {
            var wordSpacingPerMille = (Math.Abs(run.WordSpacing) > 1e-6 && run.Size > 0)
                ? run.WordSpacing * 1000.0 / run.Size
                : 0;
            var showOp = ttfFont.EncodePdfShowOperator(run.Text, run.Kerning, wordSpacingPerMille);
            if (!string.IsNullOrEmpty(showOp))
                ops.Append(showOp).Append('\n');
        }
        else
        {
            AppendBase14Text(ops, run);
        }

        ops.Append("ET\nQ\n");
        CorePage.AppendContent(ops.ToString());
    }

    public void DrawLine(Point from, Point toPoint, StrokeStyle stroke)
    {
        var p1 = Transform(from);
        var p2 = Transform(toPoint);
        var ops = new StringBuilder(96);
        ops.Append("q\n");
        AppendAlpha(ops, 1, stroke.Alpha);
        ops.Append(stroke.Color.ToPdfRgb()).Append(" RG\n");
        ops.Append(Num(stroke.Width)).Append(" w\n");
        AppendStrokeStyle(ops, stroke);
        ops.Append(Num(p1.X)).Append(' ').Append(Num(Height - p1.Y)).Append(" m ")
           .Append(Num(p2.X)).Append(' ').Append(Num(Height - p2.Y)).Append(" l S\n");
        ops.Append("Q\n");
        CorePage.AppendContent(ops.ToString());
    }

    public void DrawRectangle(Rect rect, Paint? fill, StrokeStyle? stroke)
    {
        if (fill is null && stroke is null) return;

        if (_transform != Matrix3x2.Identity ||
            fill is { IsGradient: true } ||
            stroke is { Gradient: not null })
        {
            DrawPath(PrawnSharp.Path.Create().Rectangle(rect), fill, stroke);
            return;
        }

        var ops = new StringBuilder(128);
        ops.Append("q\n");
        AppendAlpha(ops, fill?.Alpha ?? 1, stroke?.Alpha ?? 1);

        if (fill is { } f)
            ops.Append(f.Color.ToPdfRgb()).Append(" rg\n");

        if (stroke is { } s)
        {
            ops.Append(s.Color.ToPdfRgb()).Append(" RG\n");
            ops.Append(Num(s.Width)).Append(" w\n");
            AppendStrokeStyle(ops, s);
        }

        var pdfY = Height - rect.Bottom;
        ops.Append(Num(rect.X)).Append(' ')
           .Append(Num(pdfY)).Append(' ')
           .Append(Num(rect.Width)).Append(' ')
           .Append(Num(rect.Height)).Append(" re ");

        ops.Append((fill, stroke) switch
        {
            (not null, not null) => "B\n",
            (not null, null) => "f\n",
            (null, not null) => "S\n",
            _ => "n\n",
        });

        ops.Append("Q\n");
        CorePage.AppendContent(ops.ToString());
    }

    public void DrawPath(PrawnSharp.Path path, Paint? fill, StrokeStyle? stroke, FillRule fillRule = FillRule.NonZero)
    {
        ArgumentNullException.ThrowIfNull(path);
        if ((fill is null && stroke is null) || path.Commands.Count == 0) return;

        // Handle gradient fill using native PDF Shading (Type 2 linear / Type 3 radial)
        if (fill is { IsGradient: true } gradFill && gradFill.Gradient is { } gradient)
        {
            DrawGradientFill(path, gradient, gradFill.Alpha, fillRule);
            if (stroke is { } gradStroke)
                DrawPath(path, fill: null, stroke: gradStroke, fillRule: fillRule);
            return;
        }

        var ops = new StringBuilder(160);
        ops.Append("q\n");
        AppendAlpha(ops, fill?.Alpha ?? 1, stroke?.Alpha ?? 1);

        if (fill is { } f)
            ops.Append(f.Color.ToPdfRgb()).Append(" rg\n");

        if (stroke is { } s)
        {
            var strokeColor = s.Gradient is { Stops.Count: > 0 } sg ? sg.Stops[0].Color : s.Color;
            ops.Append(strokeColor.ToPdfRgb()).Append(" RG\n");
            ops.Append(Num(s.Width)).Append(" w\n");
            AppendStrokeStyle(ops, s);
        }

        AppendPathCommands(ops, path);

        var evenOdd = fillRule == FillRule.EvenOdd;
        ops.Append((fill, stroke, evenOdd) switch
        {
            (not null, not null, false) => "B\n",
            (not null, not null, true) => "B*\n",
            (not null, null, false) => "f\n",
            (not null, null, true) => "f*\n",
            (null, not null, _) => "S\n",
            _ => "n\n",
        });

        ops.Append("Q\n");
        CorePage.AppendContent(ops.ToString());
    }

    public void DrawImage(ImageData image, Rect rect)
    {
        ArgumentNullException.ThrowIfNull(image);

        var (resName, imgObj) = _backend.GetOrEmbedImage(image);
        if (_pageImages.Add(resName))
            CorePage.AddXObjectResource(resName, imgObj.Reference);

        var ops = new StringBuilder(96);
        ops.Append("q\n");

        if (_transform == Matrix3x2.Identity)
        {
            var pdfY = Height - rect.Bottom;
            ops.Append(Num(rect.Width)).Append(" 0 0 ")
               .Append(Num(rect.Height)).Append(' ')
               .Append(Num(rect.X)).Append(' ')
               .Append(Num(pdfY)).Append(" cm\n");
        }
        else
        {
            // Map unit square [0,1]x[0,1] -> Prawn rect -> _transform -> PDF coords
            var bl = Transform(new Point(rect.X, rect.Bottom));
            var br = Transform(new Point(rect.Right, rect.Bottom));
            var tl = Transform(new Point(rect.X, rect.Y));

            var a = br.X - bl.X;
            var b = (Height - br.Y) - (Height - bl.Y);
            var c = tl.X - bl.X;
            var d = (Height - tl.Y) - (Height - bl.Y);
            var e = bl.X;
            var f = Height - bl.Y;

            ops.Append(Num(a)).Append(' ').Append(Num(b)).Append(' ')
               .Append(Num(c)).Append(' ').Append(Num(d)).Append(' ')
               .Append(Num(e)).Append(' ').Append(Num(f)).Append(" cm\n");
        }

        ops.Append(resName).Append(" Do\nQ\n");
        CorePage.AppendContent(ops.ToString());
    }

    public void SaveState()
    {
        _stateStack.Push(_transform);
        CorePage.AppendContent("q\n");
    }

    public void RestoreState()
    {
        if (_stateStack.Count == 0)
            throw new InvalidOperationException("No hay un estado gráfico guardado para restaurar.");

        _transform = _stateStack.Pop();
        CorePage.AppendContent("Q\n");
    }

    public void SetClip(PrawnSharp.Path path, FillRule fillRule = FillRule.NonZero)
    {
        ArgumentNullException.ThrowIfNull(path);
        var ops = new StringBuilder(128);
        AppendPathCommands(ops, path);
        ops.Append(fillRule == FillRule.EvenOdd ? "W* n\n" : "W n\n");
        CorePage.AppendContent(ops.ToString());
    }

    public void ConcatMatrix(Matrix3x2 matrix)
    {
        _transform = Matrix3x2.Multiply(_transform, matrix);
    }

    public void SetAlpha(double fillAlpha, double strokeAlpha, BlendMode blendMode = BlendMode.Normal)
    {
        var name = RegisterExtGState(fillAlpha, strokeAlpha, blendMode);
        CorePage.AppendContent(name + " gs\n");
    }

    public void AddLink(Rect rect, string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        var pdfBottom = Height - rect.Bottom;
        var pdfTop = Height - rect.Y;
        CorePage.AddUriLink(rect.X, pdfBottom, rect.Right, pdfTop, url);
    }

    public void AddLinkToPage(Rect rect, int pageIndex, double? top = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        var pdfBottom = Height - rect.Bottom;
        var pdfTop = Height - rect.Y;

        // Resolve target page on BeforeRender so forward links to later pages work seamlessly
        _document.OnBeforeRender(_ =>
        {
            if (pageIndex < _backend.PageCount)
            {
                var target = _backend.GetCorePage(pageIndex);
                double? destPdfTop = top.HasValue ? target.Height - top.Value : null;
                CorePage.AddPageLink(rect.X, pdfBottom, rect.Right, pdfTop, target.PageObject.Reference, destPdfTop);
            }
        });
    }

    public void AppendRaw(string operators)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operators);
        CorePage.AppendContent(operators);
    }

    private void DrawGradientFill(PrawnSharp.Path path, Gradient gradient, double alpha, FillRule fillRule)
    {
        var stops = gradient.Stops.OrderBy(s => s.Offset).ToArray();
        if (stops.Length == 0) return;

        if (stops.Length == 1)
        {
            DrawPath(path, Paint.Solid(stops[0].Color, alpha), stroke: null, fillRule: fillRule);
            return;
        }

        // Build PDF Function (Type 2 for 2 stops, Type 3 stitching for >= 3 stops)
        var funcRef = BuildGradientFunction(stops);
        var shadingDict = new PdfDictionary
        {
            ["ColorSpace"] = new PdfName("DeviceRGB"),
            ["Function"] = funcRef,
            ["Extend"] = new PdfArray(PdfBool.True, PdfBool.True),
        };

        if (gradient.Kind == GradientKind.Radial)
        {
            var center = Transform(gradient.Start);
            var scale = Math.Sqrt(Math.Abs((_transform.M11 * _transform.M22) - (_transform.M12 * _transform.M21)));
            var radius = Math.Max(gradient.Radius * scale, 0.01);
            shadingDict["ShadingType"] = new PdfInt(3);
            shadingDict["Coords"] = PdfArray.OfReals(center.X, Height - center.Y, 0, center.X, Height - center.Y, radius);
        }
        else
        {
            var p0 = Transform(gradient.Start);
            var p1 = Transform(gradient.End);
            shadingDict["ShadingType"] = new PdfInt(2);
            shadingDict["Coords"] = PdfArray.OfReals(p0.X, Height - p0.Y, p1.X, Height - p1.Y);
        }

        var shadingObj = _document.Store.Add(shadingDict);
        var shName = _backend.NextShadingName();
        CorePage.AddShadingResource(shName, shadingObj.Reference);

        var ops = new StringBuilder(160);
        ops.Append("q\n");
        AppendAlpha(ops, alpha, 1);
        AppendPathCommands(ops, path);
        ops.Append(fillRule == FillRule.EvenOdd ? "W* n\n" : "W n\n");
        ops.Append(shName).Append(" sh\n");
        ops.Append("Q\n");
        CorePage.AppendContent(ops.ToString());
    }

    private PdfRef BuildGradientFunction(GradientStop[] stops)
    {
        if (stops.Length == 2)
        {
            var f2 = _document.Store.Add(new PdfDictionary
            {
                ["FunctionType"] = new PdfInt(2),
                ["Domain"] = PdfArray.OfReals(0, 1),
                ["C0"] = ColorToArray(stops[0].Color),
                ["C1"] = ColorToArray(stops[1].Color),
                ["N"] = new PdfReal(1),
            });
            return f2.Reference;
        }

        // Multi-stop Type 3 stitching function (ISO 32000-1 §7.10.4)
        var functions = new PdfArray();
        var bounds = new PdfArray();
        var encode = new PdfArray();

        for (var i = 0; i < stops.Length - 1; i++)
        {
            var seg = _document.Store.Add(new PdfDictionary
            {
                ["FunctionType"] = new PdfInt(2),
                ["Domain"] = PdfArray.OfReals(0, 1),
                ["C0"] = ColorToArray(stops[i].Color),
                ["C1"] = ColorToArray(stops[i + 1].Color),
                ["N"] = new PdfReal(1),
            });
            functions.Add(seg.Reference);
            encode.Add(new PdfReal(0));
            encode.Add(new PdfReal(1));
            if (i > 0)
                bounds.Add(new PdfReal(stops[i].Offset));
        }

        var stitch = _document.Store.Add(new PdfDictionary
        {
            ["FunctionType"] = new PdfInt(3),
            ["Domain"] = PdfArray.OfReals(stops[0].Offset, stops[^1].Offset),
            ["Functions"] = functions,
            ["Bounds"] = bounds,
            ["Encode"] = encode,
        });
        return stitch.Reference;
    }

    private static PdfArray ColorToArray(PdfColor c)
        => PdfArray.OfReals(c.R / 255.0, c.G / 255.0, c.B / 255.0);

    private void AppendBase14Text(StringBuilder ops, TextRun run)
    {
        var bytes = EncodeWinAnsi(run.Text);
        if (bytes.Length == 0) return;

        AfmFont? afm = null;
        if (run.Kerning && Base14Fonts.TryGetPostScriptName(run.Font.Face, out var psName) &&
            psName is not ("Symbol" or "ZapfDingbats"))
        {
            afm = AfmFonts.Get(psName);
        }

        if (afm is null)
        {
            ops.Append('<').Append(Convert.ToHexString(bytes)).Append("> Tj\n");
            return;
        }

        var current = new StringBuilder(16);
        var parts = new List<string>();
        for (var i = 0; i < bytes.Length; i++)
        {
            current.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
            if (i + 1 >= bytes.Length) continue;
            if (bytes[i] is 9 or 10 or 13 or 32 || bytes[i + 1] is 9 or 10 or 13 or 32) continue;

            var kern = afm.GetKerning(bytes[i], bytes[i + 1]);
            if (kern == 0) continue;

            parts.Add("<" + current + ">");
            parts.Add((-kern).ToString("0.###", CultureInfo.InvariantCulture));
            current.Clear();
        }

        if (current.Length > 0)
            parts.Add("<" + current + ">");

        ops.Append('[').Append(string.Join(' ', parts)).Append("] TJ\n");
    }

    private void AppendAlpha(StringBuilder ops, double fillAlpha, double strokeAlpha)
    {
        if (Math.Abs(fillAlpha - 1) < 0.0001 && Math.Abs(strokeAlpha - 1) < 0.0001)
            return;

        ops.Append(RegisterExtGState(fillAlpha, strokeAlpha, BlendMode.Normal)).Append(" gs\n");
    }

    private string RegisterExtGState(double fillAlpha, double strokeAlpha, BlendMode blendMode)
    {
        var (resName, gsObj) = _backend.GetOrCreateExtGState(fillAlpha, strokeAlpha, blendMode);
        if (_pageGs.Add(resName))
            CorePage.AddExtGStateResource(resName, gsObj.Reference);
        return resName;
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
            ops.Append(Num(stroke.MiterLimit)).Append(" M\n");

        if (stroke.Dash is { Count: > 0 } dash)
        {
            ops.Append('[')
               .Append(string.Join(' ', dash.Select(d => Num(Math.Max(d, 0)))))
               .Append("] ")
               .Append(Num(stroke.DashPhase))
               .Append(" d\n");
        }
        else
        {
            ops.Append("[] 0 d\n");
        }
    }

    private void AppendPathCommands(StringBuilder ops, PrawnSharp.Path path)
    {
        foreach (var cmd in path.Commands)
        {
            switch (cmd.Kind)
            {
                case PathCommandKind.MoveTo:
                    AppendPoint(ops, cmd.P1);
                    ops.Append(" m\n");
                    break;
                case PathCommandKind.LineTo:
                    AppendPoint(ops, cmd.P1);
                    ops.Append(" l\n");
                    break;
                case PathCommandKind.CurveTo:
                    AppendPoint(ops, cmd.P1);
                    ops.Append(' ');
                    AppendPoint(ops, cmd.P2);
                    ops.Append(' ');
                    AppendPoint(ops, cmd.P3);
                    ops.Append(" c\n");
                    break;
                case PathCommandKind.Close:
                    ops.Append("h\n");
                    break;
            }
        }
    }

    private void AppendPoint(StringBuilder ops, Point p)
    {
        var t = Transform(p);
        ops.Append(Num(t.X)).Append(' ').Append(Num(Height - t.Y));
    }

    private Point Transform(Point p)
    {
        if (_transform == Matrix3x2.Identity) return p;
        var v = Vector2.Transform(new Vector2((float)p.X, (float)p.Y), _transform);
        return new Point(v.X, v.Y);
    }

    private static string Num(double v) => PdfObject.FormatReal(v);

    private static byte[] EncodeWinAnsi(string text)
    {
        var result = new byte[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch <= 0x7F || (ch >= 0xA0 && ch <= 0xFF))
            {
                result[i] = (byte)ch;
            }
            else
            {
                result[i] = ch switch
                {
                    '\u20AC' => 0x80, '\u201A' => 0x82, '\u0192' => 0x83, '\u201E' => 0x84,
                    '\u2026' => 0x85, '\u2020' => 0x86, '\u2021' => 0x87, '\u02C6' => 0x88,
                    '\u2030' => 0x89, '\u0160' => 0x8A, '\u2039' => 0x8B, '\u0152' => 0x8C,
                    '\u017D' => 0x8E, '\u2018' => 0x91, '\u2019' => 0x92, '\u201C' => 0x93,
                    '\u201D' => 0x94, '\u2022' => 0x95, '\u2013' => 0x96, '\u2014' => 0x97,
                    '\u02DC' => 0x98, '\u2122' => 0x99, '\u0161' => 0x9A, '\u203A' => 0x9B,
                    '\u0153' => 0x9C, '\u017E' => 0x9E, '\u0178' => 0x9F,
                    _ => (byte)'?',
                };
            }
        }
        return result;
    }
}
