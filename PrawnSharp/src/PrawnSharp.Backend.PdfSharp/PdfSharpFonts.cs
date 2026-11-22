using System.Collections.Concurrent;
using System.Globalization;
using PdfSharp.Drawing;
using PdfSharp.Fonts;

namespace PrawnSharp.Backend.PdfSharp;

/// <summary>
/// Registro global de fuentes TTF/OTF para PDFsharp. Cada archivo se carga una vez y se
/// expone con un nombre de familia interno único para que dos documentos puedan usar
/// familias distintas con el mismo nombre lógico.
/// </summary>
internal static class PdfSharpFontRegistry
{
    private static readonly ConcurrentDictionary<string, byte[]> Files = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> PathToFamily = new(StringComparer.OrdinalIgnoreCase);
    private static int _sequence;

    static PdfSharpFontRegistry()
    {
        GlobalFontSettings.FontResolver = new Resolver();
    }

    public static string Register(FontFace face)
    {
        if (string.IsNullOrWhiteSpace(face.FilePath))
        {
            throw new UnsupportedFontException($"La fuente '{face}' no tiene archivo TTF/OTF.");
        }

        var path = System.IO.Path.GetFullPath(face.FilePath);

        if (!File.Exists(path))
        {
            throw new UnsupportedFontException($"No existe el archivo de fuente '{path}'.");
        }

        return PathToFamily.GetOrAdd(path, fullPath =>
        {
            var family = "PrawnSharpFont" + Interlocked.Increment(ref _sequence);
            Files[family] = File.ReadAllBytes(fullPath);
            return family;
        });
    }

    public static XFont CreateFont(FontFace face, double size) =>
        new(
            Register(face),
            size,
            XFontStyleEx.Regular,
            XPdfFontOptions.UnicodeDefault);

    private sealed class Resolver : IFontResolver
    {
        public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            if (Files.ContainsKey(familyName))
            {
                return new FontResolverInfo(familyName);
            }

            throw new UnsupportedFontException(
                $"PDFsharp pidió la familia '{familyName}', que no está registrada en PrawnSharp.");
        }

        public byte[] GetFont(string faceName)
        {
            if (Files.TryGetValue(faceName, out var bytes))
            {
                return bytes;
            }

            throw new UnsupportedFontException($"No se encontró la fuente '{faceName}'.");
        }
    }
}

/// <summary>Métricas de una fuente TTF/OTF usando el motor de PDFsharp.</summary>
internal sealed class PdfSharpFontMetrics : IFontMetrics
{
    private readonly FontFace _face;
    private readonly string _family;
    private readonly TrueTypeFontInfo? _info;
    private readonly XGraphics _measure;
    private readonly Dictionary<double, XFont> _fonts = [];

    public PdfSharpFontMetrics(FontFace face)
    {
        _face = face;
        _family = PdfSharpFontRegistry.Register(face);
        _info = TrueTypeFontInfo.TryLoad(face.FilePath);
        _measure = XGraphics.CreateMeasureContext(
            new XSize(1000, 1000),
            XGraphicsUnit.Point,
            XPageDirection.Downwards);
    }

    public double WidthOf(string text, double fontSize, bool kerning)
    {
        if (string.IsNullOrEmpty(text) || fontSize <= 0)
        {
            return 0;
        }

        if (_info is { HasWidths: true })
        {
            return _info.WidthOf(text, fontSize, kerning);
        }

        // PDFsharp no aplica kerning; se suma el de la tabla kern cuando se pide.
        var width = _measure.MeasureString(text, Font(fontSize)).Width;

        if (kerning && _info is not null)
        {
            width += _info.KerningSum(text, fontSize);
        }

        return width;
    }

    public double AscentAt(double fontSize) =>
        _info?.AscentAt(fontSize) ?? Math.Abs(Metrics(fontSize).Ascent) * fontSize / Units(fontSize);

    public double DescentAt(double fontSize) =>
        _info?.DescentAt(fontSize) ?? -Math.Abs(Metrics(fontSize).Descent) * fontSize / Units(fontSize);

    public double LineHeightAt(double fontSize)
    {
        if (_info is not null)
        {
            return _info.LineHeightAt(fontSize);
        }

        var metrics = Metrics(fontSize);
        var unit = Math.Max(metrics.UnitsPerEm, 1);
        var line = Math.Abs(metrics.LineSpacing);

        if (line <= 0)
        {
            line = Math.Abs(metrics.Ascent) + Math.Abs(metrics.Descent);
        }

        return line * fontSize / unit;
    }

    public double UnderlinePositionAt(double fontSize) =>
        _info?.UnderlinePositionAt(fontSize) ??
        Metrics(fontSize).UnderlinePosition * fontSize / Units(fontSize);

    public double UnderlineThicknessAt(double fontSize) =>
        _info?.UnderlineThicknessAt(fontSize) ??
        Math.Abs(Metrics(fontSize).UnderlineThickness) * fontSize / Units(fontSize);

    private XFont Font(double size)
    {
        if (_fonts.TryGetValue(size, out var font))
        {
            return font;
        }

        font = PdfSharpFontRegistry.CreateFont(_face, size);
        _fonts[size] = font;
        return font;
    }

    private XFontMetrics Metrics(double size) => Font(size).Metrics;

    private double Units(double size) => Math.Max(Metrics(size).UnitsPerEm, 1);
}
