using System.Text;

namespace PrawnSharp.Fonts;

/// <summary>Codificador WinAnsi (CP1252) usado para medir texto base-14.</summary>
internal static class WinAnsiEncoding
{
    private static readonly Encoding Cp1252;

    static WinAnsiEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp1252 = Encoding.GetEncoding(
            1252,
            EncoderFallback.ReplacementFallback,
            DecoderFallback.ReplacementFallback);
    }

    public static byte[] Encode(string text) => Cp1252.GetBytes(text ?? string.Empty);
}
