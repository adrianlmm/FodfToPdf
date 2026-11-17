using System.Collections.Concurrent;
using SkiaSharp;

namespace FodtToPdf;

// Kerning TTF tomado del parser de PrawnSharp (tabla `kern`, formato 0).
// SkiaSharp.MeasureText NO aplica kerning; esto sólo ajusta la medición propia
// (paginación, tabuladores y decidir si un párrafo es corto).
internal static class KerningMetrics
{
    private static readonly ConcurrentDictionary<string, KernData> Cache = new();

    public static double Ajuste(string texto, string familia, bool negrita, double tamano)
    {
        if (string.IsNullOrEmpty(texto) || tamano <= 0) return 0;

        var data = Cache.GetOrAdd(
            (familia ?? "Arial") + (negrita ? "-b" : "-n"),
            _ => Cargar(familia, negrita));

        if (data.Pairs.Count == 0) return 0;

        var total = 0;
        for (var i = 0; i + 1 < texto.Length; i++)
        {
            var left = texto[i];
            var right = texto[i + 1];

            // Prawn no aplica kerning a través de separadores de palabra.
            if (left is '\t' or '\n' or '\r' or ' ' || right is '\t' or '\n' or '\r' or ' ') continue;

            var gl = data.Typeface.GetGlyph(left);
            var gr = data.Typeface.GetGlyph(right);
            if (gl == 0 || gr == 0) continue;

            if (data.Pairs.TryGetValue(((int)gl << 16) | gr, out var value))
                total += value;
        }

        return total * tamano / Math.Max(data.UnitsPerEm, 1);
    }

    private static KernData Cargar(string familia, bool negrita)
    {
        var typeface = SKTypeface.FromFamilyName(
            string.IsNullOrWhiteSpace(familia) ? "Arial" : familia,
            negrita ? SKFontStyle.Bold : SKFontStyle.Normal) ?? SKTypeface.Default;

        var pairData = typeface.GetTableData(0x6B65726E); // 'kern'
        var head = typeface.GetTableData(0x68656164);     // 'head'
        var units = 1000;

        if (head is { Length: >= 20 })
            units = (head[18] << 8) | head[19];

        var pares = new Dictionary<int, int>();

        if (pairData is { Length: >= 4 })
        {
            var n = (pairData[2] << 8) | pairData[3];
            var off = 4;

            for (var t = 0; t < n && off + 6 <= pairData.Length; t++)
            {
                var length = (pairData[off + 2] << 8) | pairData[off + 3];
                var coverage = (pairData[off + 4] << 8) | pairData[off + 5];
                var format = coverage >> 8;

                if (length <= 0) break;

                if (format == 0 && off + 14 <= pairData.Length)
                {
                    var pairs = (pairData[off + 6] << 8) | pairData[off + 7];
                    var p = off + 14;

                    for (var i = 0; i < pairs && p + 6 <= off + length && p + 6 <= pairData.Length; i++, p += 6)
                    {
                        var left = (pairData[p] << 8) | pairData[p + 1];
                        var right = (pairData[p + 2] << 8) | pairData[p + 3];
                        var value = (short)((pairData[p + 4] << 8) | pairData[p + 5]);
                        if (value != 0) pares[((int)left << 16) | right] = value;
                    }
                }

                off += length;
            }
        }

        return new KernData(typeface, pares, units);
    }

    private sealed record KernData(SKTypeface Typeface, Dictionary<int, int> Pairs, int UnitsPerEm);
}
