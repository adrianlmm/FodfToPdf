using System.Globalization;
using System.Text;

namespace PrawnSharp.Backend.PdfSharp;

/// <summary>Codificación WinAnsi (CP1252) y serialización de cadenas PDF.</summary>
internal static class WinAnsi
{
    private static readonly Encoding Cp1252;

    static WinAnsi()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp1252 = Encoding.GetEncoding(
            1252,
            EncoderFallback.ReplacementFallback,
            DecoderFallback.ReplacementFallback);
    }

    public static byte[] Encode(string text) => Cp1252.GetBytes(text ?? string.Empty);

    /// <summary>Cadena hexadecimal PDF: <c>&lt;486F6C61&gt;</c>.</summary>
    public static string EncodeHex(string text) => Convert.ToHexString(Encode(text));

    public static string Number(double value) => value.ToString("0.#####", CultureInfo.InvariantCulture);
}
