using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;

namespace PrawnSharp.Backend.PdfSharp;

/// <summary>
/// Métricas verticales, cmap y kerning de un TTF/OTF. Lee head/hhea/OS/2/post, cmap y
/// kern con el mismo criterio que ttfunk (OS/2 typo metrics si no son cero; milésimas
/// de em truncadas hacia cero).
/// </summary>
internal sealed class TrueTypeFontInfo
{
    private static readonly ConcurrentDictionary<string, TrueTypeFontInfo?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<int, int> _cmap = [];
    private readonly List<(int Start, int End, int Glyph)> _cmapRanges = [];
    private readonly Dictionary<int, int> _kernPairs = [];
    private int[] _advanceWidths = [];

    private TrueTypeFontInfo(
        int unitsPerEm,
        int ascentPerMille,
        int descentPerMille,
        int lineGapPerMille,
        int underlinePositionPerMille,
        int underlineThicknessPerMille)
    {
        UnitsPerEm = unitsPerEm;
        AscentPerMille = ascentPerMille;
        DescentPerMille = descentPerMille;
        LineGapPerMille = lineGapPerMille;
        UnderlinePositionPerMille = underlinePositionPerMille;
        UnderlineThicknessPerMille = underlineThicknessPerMille;
    }

    public int UnitsPerEm { get; }

    public int AscentPerMille { get; }

    public int DescentPerMille { get; }

    public int LineGapPerMille { get; }

    public int UnderlinePositionPerMille { get; }

    public int UnderlineThicknessPerMille { get; }

    public bool HasKerning => _kernPairs.Count > 0;

    public bool HasWidths => _advanceWidths.Length > 0;

    public double AscentAt(double size) => AscentPerMille / 1000.0 * size;

    public double DescentAt(double size) => -DescentPerMille / 1000.0 * size;

    public double LineHeightAt(double size) =>
        (AscentPerMille + DescentPerMille + LineGapPerMille) / 1000.0 * size;

    public double UnderlinePositionAt(double size) => UnderlinePositionPerMille / 1000.0 * size;

    public double UnderlineThicknessAt(double size) => UnderlineThicknessPerMille / 1000.0 * size;

    /// <summary>Avance de un carácter en puntos, con el ancho hmtx truncado a milésimas.</summary>
    public double AdvanceAt(int codePoint, double fontSize)
    {
        if (!HasWidths)
        {
            return 0;
        }

        var glyph = GlyphFor(codePoint);
        var advance = AdvanceWidth(glyph);
        return (int)(advance * 1000.0 / UnitsPerEm) / 1000.0 * fontSize;
    }

    /// <summary>
    /// Ancho de una cadena replicando Prawn/ttfunk: cada ancho hmtx se trunca a
    /// milésimas de em, se suman y luego se aplica el tamaño; el kerning se resta.
    /// </summary>
    public double WidthOf(string text, double fontSize, bool kerning)
    {
        if (string.IsNullOrEmpty(text) || !HasWidths)
        {
            return 0;
        }

        var codePoints = EnumerateCodePoints(text).ToList();
        var widths = 0;

        foreach (var codePoint in codePoints)
        {
            widths += (int)(AdvanceWidth(GlyphFor(codePoint)) * 1000.0 / UnitsPerEm);
        }

        var kern = 0.0;

        if (kerning && _kernPairs.Count > 0)
        {
            for (var i = 0; i + 1 < codePoints.Count; i++)
            {
                var left = GlyphFor(codePoints[i]);
                var right = GlyphFor(codePoints[i + 1]);

                if (left != 0 && right != 0 &&
                    _kernPairs.TryGetValue((left << 16) | right, out var value))
                {
                    kern += value * 1000.0 / UnitsPerEm;
                }
            }
        }

        return (widths + kern) * fontSize / 1000.0;
    }

    /// <summary>Kerning entre dos code points, en puntos (negativo = acercar).</summary>
    public double KerningAt(int leftCodePoint, int rightCodePoint, double fontSize)
    {
        if (_kernPairs.Count == 0)
        {
            return 0;
        }

        var left = GlyphFor(leftCodePoint);
        var right = GlyphFor(rightCodePoint);

        if (left == 0 || right == 0)
        {
            return 0;
        }

        return _kernPairs.TryGetValue((left << 16) | right, out var value)
            ? value * fontSize / UnitsPerEm
            : 0;
    }

    /// <summary>Suma de kerning de una cadena, en puntos.</summary>
    public double KerningSum(string text, double fontSize)
    {
        if (_kernPairs.Count == 0 || string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var codePoints = EnumerateCodePoints(text).ToList();
        var total = 0.0;

        for (var i = 0; i + 1 < codePoints.Count; i++)
        {
            total += KerningAt(codePoints[i], codePoints[i + 1], fontSize);
        }

        return total;
    }

    public static IEnumerable<int> EnumerateCodePoints(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                yield return char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
            }
            else
            {
                yield return text[i];
            }
        }
    }

    public static TrueTypeFontInfo? TryLoad(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var fullPath = System.IO.Path.GetFullPath(path);

        if (!File.Exists(fullPath))
        {
            return null;
        }

        return Cache.GetOrAdd(fullPath, p => Parse(File.ReadAllBytes(p)));
    }

    private static TrueTypeFontInfo? Parse(byte[] data)
    {
        if (data.Length < 12)
        {
            return null;
        }

        var version = BinaryPrimitives.ReadUInt32BigEndian(data);
        var isTrueType = version is 0x00010000 or 0x74727565; // 1.0, 'true'
        var isOtto = version == 0x4F54544F;                    // 'OTTO'

        if (!isTrueType && !isOtto)
        {
            return null;
        }

        var tableCount = U16(data, 4);
        var tables = new Dictionary<string, (int Offset, int Length)>(StringComparer.Ordinal);

        for (var i = 0; i < tableCount; i++)
        {
            var record = 12 + (i * 16);

            if (record + 16 > data.Length)
            {
                break;
            }

            var tag = Encoding.ASCII.GetString(data, record, 4);
            var offset = (int)U32(data, record + 8);
            var length = (int)U32(data, record + 12);

            if (offset >= 0 && length >= 0 && offset + length <= data.Length)
            {
                tables[tag] = (offset, length);
            }
        }

        var unitsPerEm = 1000;

        if (tables.TryGetValue("head", out var head) && head.Length >= 20)
        {
            unitsPerEm = U16(data, head.Offset + 18);

            if (unitsPerEm <= 0)
            {
                unitsPerEm = 1000;
            }
        }

        var ascent = 0;
        var descent = 0;
        var lineGap = 0;

        if (tables.TryGetValue("hhea", out var hhea) && hhea.Length >= 10)
        {
            ascent = I16(data, hhea.Offset + 4);
            descent = I16(data, hhea.Offset + 6);
            lineGap = I16(data, hhea.Offset + 8);
        }

        if (tables.TryGetValue("OS/2", out var os2) && os2.Length >= 74)
        {
            var typoAscent = I16(data, os2.Offset + 68);
            var typoDescent = I16(data, os2.Offset + 70);
            var typoLineGap = I16(data, os2.Offset + 72);

            if (typoAscent != 0 || typoDescent != 0)
            {
                ascent = typoAscent;
                descent = typoDescent;
            }

            if (typoLineGap != 0)
            {
                lineGap = typoLineGap;
            }
        }

        var underlinePosition = 0;
        var underlineThickness = 0;

        if (tables.TryGetValue("post", out var post) && post.Length >= 12)
        {
            underlinePosition = I16(data, post.Offset + 8);
            underlineThickness = I16(data, post.Offset + 10);
        }

        var scale = 1000.0 / unitsPerEm;
        var info = new TrueTypeFontInfo(
            unitsPerEm,
            (int)(ascent * scale),
            (int)(Math.Abs(descent) * scale),
            (int)(lineGap * scale),
            (int)(underlinePosition * scale),
            (int)(Math.Abs(underlineThickness) * scale));

        info.ParseCmap(data, tables);
        info.ParseHorizontalMetrics(data, tables);
        info.ParseKerning(data, tables);
        return info;
    }

    private void ParseHorizontalMetrics(byte[] data, Dictionary<string, (int Offset, int Length)> tables)
    {
        if (!tables.TryGetValue("hmtx", out var hmtx))
        {
            return;
        }

        var numGlyphs = 0;

        if (tables.TryGetValue("maxp", out var maxp) && maxp.Length >= 6)
        {
            numGlyphs = U16(data, maxp.Offset + 4);
        }

        var numberOfHMetrics = 0;

        if (tables.TryGetValue("hhea", out var hhea) && hhea.Length >= 36)
        {
            numberOfHMetrics = U16(data, hhea.Offset + 34);
        }

        var count = Math.Max(numGlyphs, numberOfHMetrics);

        if (count <= 0)
        {
            return;
        }

        var widths = new int[count];
        var metrics = Math.Min(numberOfHMetrics, count);
        var last = 0;

        for (var i = 0; i < metrics; i++)
        {
            last = U16(data, hmtx.Offset + (4 * i));
            widths[i] = last;
        }

        for (var i = metrics; i < count; i++)
        {
            widths[i] = last;
        }

        _advanceWidths = widths;
    }

    private int AdvanceWidth(int glyph)
    {
        if (_advanceWidths.Length == 0)
        {
            return 0;
        }

        return glyph >= 0 && glyph < _advanceWidths.Length
            ? _advanceWidths[glyph]
            : _advanceWidths[^1];
    }

    private void ParseCmap(byte[] data, Dictionary<string, (int Offset, int Length)> tables)
    {
        if (!tables.TryGetValue("cmap", out var cmap) || cmap.Length < 4)
        {
            return;
        }

        var count = U16(data, cmap.Offset + 2);
        var bestOffset = -1;
        var bestScore = -1;

        for (var i = 0; i < count; i++)
        {
            var record = cmap.Offset + 4 + (i * 8);

            if (record + 8 > data.Length)
            {
                break;
            }

            var platform = U16(data, record);
            var encoding = U16(data, record + 2);
            var offset = (int)U32(data, record + 4);

            if (offset <= 0 || cmap.Offset + offset + 2 > data.Length)
            {
                continue;
            }

            var absolute = cmap.Offset + offset;
            var format = U16(data, absolute);
            var score =
                (platform == 3 && encoding == 10 ? 100 : 0) +
                (platform == 0 ? 90 : 0) +
                (platform == 3 && encoding == 1 ? 80 : 0) +
                (platform == 3 && encoding == 0 ? 70 : 0) +
                (format == 12 ? 10 : format == 4 ? 5 : 0);

            if (score > bestScore)
            {
                bestScore = score;
                bestOffset = absolute;
            }
        }

        if (bestOffset < 0)
        {
            return;
        }

        var selectedFormat = U16(data, bestOffset);

        if (selectedFormat == 4)
        {
            ParseCmap4(data, bestOffset);
        }
        else if (selectedFormat == 12)
        {
            ParseCmap12(data, bestOffset);
        }
    }

    private void ParseCmap4(byte[] data, int offset)
    {
        var segCountX2 = U16(data, offset + 6);
        var segCount = segCountX2 / 2;
        var endCodes = offset + 14;
        var startCodes = endCodes + segCountX2 + 2;
        var idDeltas = startCodes + segCountX2;
        var idRangeOffsets = idDeltas + segCountX2;

        for (var segment = 0; segment < segCount; segment++)
        {
            var end = U16(data, endCodes + (2 * segment));
            var start = U16(data, startCodes + (2 * segment));
            var delta = I16(data, idDeltas + (2 * segment));
            var rangeOffset = U16(data, idRangeOffsets + (2 * segment));

            if (start > end)
            {
                continue;
            }

            for (var code = start; code <= end && code <= 0xFFFF; code++)
            {
                int glyph;

                if (rangeOffset == 0)
                {
                    glyph = (code + delta) & 0xFFFF;
                }
                else
                {
                    var address = idRangeOffsets + (2 * segment) + rangeOffset + (2 * (code - start));
                    var raw = U16(data, address);

                    if (raw == 0)
                    {
                        continue;
                    }

                    glyph = (raw + delta) & 0xFFFF;
                }

                if (glyph != 0)
                {
                    _cmap[code] = glyph;
                }
            }
        }
    }

    private void ParseCmap12(byte[] data, int offset)
    {
        var groups = (int)U32(data, offset + 12);
        var position = offset + 16;

        for (var i = 0; i < groups; i++, position += 12)
        {
            if (position + 12 > data.Length)
            {
                break;
            }

            var start = (int)U32(data, position);
            var end = (int)U32(data, position + 4);
            var glyph = (int)U32(data, position + 8);

            if (end < start)
            {
                continue;
            }

            _cmapRanges.Add((start, end, glyph));
        }
    }

    private void ParseKerning(byte[] data, Dictionary<string, (int Offset, int Length)> tables)
    {
        if (!tables.TryGetValue("kern", out var kern) || kern.Length < 4)
        {
            return;
        }

        var version = U16(data, kern.Offset);

        if (version != 0)
        {
            // La versión Apple (0x00010000) es poco común; se deja sin kerning.
            return;
        }

        var count = U16(data, kern.Offset + 2);
        var position = kern.Offset + 4;

        for (var table = 0; table < count; table++)
        {
            if (position + 6 > data.Length)
            {
                break;
            }

            var length = U16(data, position + 2);
            var coverage = U16(data, position + 4);
            var format = coverage >> 8;
            var horizontal = (coverage & 1) != 0;
            var minimum = (coverage & 2) != 0;
            var crossStream = (coverage & 4) != 0;

            if (format == 0 && horizontal && !minimum && !crossStream)
            {
                var pairs = U16(data, position + 6);
                var pairPosition = position + 14;

                for (var pair = 0; pair < pairs; pair++, pairPosition += 6)
                {
                    if (pairPosition + 6 > data.Length)
                    {
                        break;
                    }

                    var left = U16(data, pairPosition);
                    var right = U16(data, pairPosition + 2);
                    var value = I16(data, pairPosition + 4);

                    _kernPairs[(left << 16) | right] = value;
                }
            }

            if (length <= 0)
            {
                break;
            }

            position += length;
        }
    }

    private int GlyphFor(int codePoint)
    {
        foreach (var (start, end, glyph) in _cmapRanges)
        {
            if (codePoint >= start && codePoint <= end)
            {
                return glyph + (codePoint - start);
            }
        }

        return _cmap.TryGetValue(codePoint, out var result) ? result : 0;
    }

    private static int U16(byte[] data, int offset) =>
        offset >= 0 && offset + 2 <= data.Length
            ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset))
            : 0;

    private static int I16(byte[] data, int offset) =>
        offset >= 0 && offset + 2 <= data.Length
            ? BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(offset))
            : 0;

    private static long U32(byte[] data, int offset) =>
        offset >= 0 && offset + 4 <= data.Length
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset))
            : 0;
}
