using System.Globalization;

namespace PrawnSharp;

/// <summary>Color RGB de 8 bits por canal.</summary>
public readonly record struct PdfColor(byte R, byte G, byte B)
{
    public static readonly PdfColor Black = new(0, 0, 0);
    public static readonly PdfColor White = new(255, 255, 255);
    public static readonly PdfColor Red = new(255, 0, 0);
    public static readonly PdfColor Green = new(0, 128, 0);
    public static readonly PdfColor Blue = new(0, 0, 255);
    public static readonly PdfColor Gray = new(128, 128, 128);
    public static readonly PdfColor Transparent = new(0, 0, 0);

    /// <summary>Acepta <c>#RRGGBB</c> o <c>RRGGBB</c>.</summary>
    public static PdfColor FromHex(string hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hex);

        var value = hex.Trim().TrimStart('#');

        if (value.Length != 6)
        {
            throw new FormatException($"Color hexadecimal inválido: '{hex}'.");
        }

        return new PdfColor(
            byte.Parse(value.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(value.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(value.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    /// <summary>Devuelve <c>r g b</c> normalizado en 0..1 para un content stream PDF.</summary>
    public string ToPdfRgb()
    {
        var r = (R / 255.0).ToString("0.#####", CultureInfo.InvariantCulture);
        var g = (G / 255.0).ToString("0.#####", CultureInfo.InvariantCulture);
        var b = (B / 255.0).ToString("0.#####", CultureInfo.InvariantCulture);
        return $"{r} {g} {b}";
    }

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}
