using System.Globalization;
using System.Text;

namespace PrawnSharp.Fonts;

/// <summary>
/// Métricas AFM de una fuente base-14: anchos por glifo, kerning y métricas verticales.
/// Los anchos están en unidades de 1/1000 em.
/// </summary>
public sealed class AfmFont : IFontMetrics
{
    private readonly double[] _widthsByCode = new double[256];
    private readonly Dictionary<string, double> _glyphWidths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _kernPairs = new(StringComparer.Ordinal);

    private AfmFont(string postScriptName)
    {
        PostScriptName = postScriptName;
    }

    public string PostScriptName { get; }

    public double Ascender { get; private set; }

    public double Descender { get; private set; }

    public double CapHeight { get; private set; }

    public double XHeight { get; private set; }

    public double UnderlinePosition { get; private set; }

    public double UnderlineThickness { get; private set; }

    /// <summary>Espacio entre el descender y el borde inferior de la caja de fuente (1/1000 em).</summary>
    public double LineGap { get; private set; }

    private double _bboxTop;
    private double _bboxBottom;

    public bool HasKerningData => _kernPairs.Count > 0;

    /// <summary>Ancho de un glifo por nombre Adobe, en 1/1000 em.</summary>
    public double GetGlyphWidth(string glyphName) =>
        _glyphWidths.TryGetValue(glyphName, out var width) ? width : 0;

    /// <summary>Ancho de un código WinAnsi, en 1/1000 em.</summary>
    public double GetWidth(byte code) => _widthsByCode[code];

    /// <summary>Kerning entre dos códigos WinAnsi, en 1/1000 em (negativo = acercar).</summary>
    public double GetKerning(byte left, byte right)
    {
        var leftName = WinAnsiGlyphs.Names[left];
        var rightName = WinAnsiGlyphs.Names[right];

        if (leftName == ".notdef" || rightName == ".notdef")
        {
            return 0;
        }

        return _kernPairs.TryGetValue(Key(leftName, rightName), out var kern) ? kern : 0;
    }

    /// <summary>Ancho de una cadena a un tamaño dado, en puntos.</summary>
    public double WidthOf(string text, double fontSize, bool kerning = false)
    {
        if (string.IsNullOrEmpty(text) || fontSize <= 0)
        {
            return 0;
        }

        var bytes = WinAnsiEncoding.Encode(text);
        var total = 0.0;

        for (var i = 0; i < bytes.Length; i++)
        {
            total += _widthsByCode[bytes[i]];

            // Prawn dibuja/mede el texto por palabras: no aplica kerning a través de
            // un espacio (p. ej. el par period+space del AFM).
            if (kerning && i + 1 < bytes.Length && !IsWordSeparator(bytes[i]) && !IsWordSeparator(bytes[i + 1]))
            {
                total += GetKerning(bytes[i], bytes[i + 1]);
            }
        }

        return total * fontSize / 1000.0;
    }

    public double AscentAt(double fontSize) => Ascender * fontSize / 1000.0;

    public double DescentAt(double fontSize) => Descender * fontSize / 1000.0;

    /// <summary>Altura de línea de la fuente, como <c>font.height</c> de Prawn.</summary>
    public double LineHeightAt(double fontSize) =>
        (Ascender - Descender + LineGap) * fontSize / 1000.0;

    public double UnderlinePositionAt(double fontSize) => UnderlinePosition * fontSize / 1000.0;

    public double UnderlineThicknessAt(double fontSize) => UnderlineThickness * fontSize / 1000.0;

    internal static AfmFont Load(string postScriptName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(postScriptName);

        var resourceName = $"PrawnSharp.Fonts.Afm.{postScriptName}.afm";

        using var stream = typeof(AfmFont).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new UnsupportedFontException(
                $"No se encontró la métrica AFM '{resourceName}'. " +
                "Las 14 fuentes base-14 deberían venir incrustadas en PrawnSharp.Fonts.");

        using var reader = new StreamReader(stream, Encoding.ASCII);
        var font = new AfmFont(postScriptName);
        font.Parse(reader);
        font.BuildWidthsByCode();
        return font;
    }

    private void Parse(TextReader reader)
    {
        string? line;

        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();

            if (line.Length == 0 || line.StartsWith("Comment", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("Ascender ", StringComparison.Ordinal))
            {
                Ascender = ParseNumber(line[9..]);
            }
            else if (line.StartsWith("FontBBox ", StringComparison.Ordinal))
            {
                var parts = line[9..].Split(' ', StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length >= 4)
                {
                    _bboxBottom = ParseNumber(parts[1]);
                    _bboxTop = ParseNumber(parts[3]);
                }
            }
            else if (line.StartsWith("Descender ", StringComparison.Ordinal))
            {
                Descender = ParseNumber(line[10..]);
            }
            else if (line.StartsWith("CapHeight ", StringComparison.Ordinal))
            {
                CapHeight = ParseNumber(line[10..]);
            }
            else if (line.StartsWith("XHeight ", StringComparison.Ordinal))
            {
                XHeight = ParseNumber(line[8..]);
            }
            else if (line.StartsWith("UnderlinePosition ", StringComparison.Ordinal))
            {
                UnderlinePosition = ParseNumber(line[18..]);
            }
            else if (line.StartsWith("UnderlineThickness ", StringComparison.Ordinal))
            {
                UnderlineThickness = ParseNumber(line[19..]);
            }
            else if (line.StartsWith("StartCharMetrics", StringComparison.Ordinal))
            {
                ParseCharMetrics(reader);
            }
            else if (line.StartsWith("StartKernPairs", StringComparison.Ordinal))
            {
                ParseKernPairs(reader);
            }
        }
    }

    private void ParseCharMetrics(TextReader reader)
    {
        string? line;

        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();

            if (line.StartsWith("EndCharMetrics", StringComparison.Ordinal))
            {
                return;
            }

            if (!line.StartsWith("C ", StringComparison.Ordinal))
            {
                continue;
            }

            double? width = null;
            string? name = null;

            foreach (var part in line.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (part.StartsWith("WX ", StringComparison.Ordinal))
                {
                    width = ParseNumber(part[3..]);
                }
                else if (part.StartsWith("N ", StringComparison.Ordinal))
                {
                    name = part[2..];
                }
            }

            if (name is not null && width is not null)
            {
                _glyphWidths[name] = width.Value;
            }
        }
    }

    private void ParseKernPairs(TextReader reader)
    {
        string? line;

        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();

            if (line.StartsWith("EndKernPairs", StringComparison.Ordinal))
            {
                return;
            }

            if (!line.StartsWith("KPX ", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 4)
            {
                _kernPairs[Key(parts[1], parts[2])] = ParseNumber(parts[3]);
            }
        }
    }

    private void BuildWidthsByCode()
    {
        if (_bboxTop != 0 || _bboxBottom != 0)
        {
            LineGap = (_bboxTop - _bboxBottom) - (Ascender - Descender);
        }

        for (var code = 0; code < 256; code++)
        {
            var name = WinAnsiGlyphs.Names[code];

            if (name != ".notdef" && _glyphWidths.TryGetValue(name, out var width))
            {
                _widthsByCode[code] = width;
            }
        }
    }

    private static string Key(string left, string right) => left + "\0" + right;

    private static bool IsWordSeparator(byte value) => value is 9 or 10 or 13 or 32;

    private static double ParseNumber(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : 0;
}
