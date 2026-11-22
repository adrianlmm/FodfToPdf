namespace PrawnSharp.Fonts;

/// <summary>
/// Nombres PostScript de las 14 fuentes estándar de PDF y su variante por estilo.
/// Las métricas AFM llegan en la fase F2; aquí sólo se resuelve el nombre.
/// </summary>
public static class Base14Fonts
{
    private static readonly Dictionary<string, string[]> Variants = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Helvetica"] = new[] { "Helvetica", "Helvetica-Bold", "Helvetica-Oblique", "Helvetica-BoldOblique" },
        ["Times"] = new[] { "Times-Roman", "Times-Bold", "Times-Italic", "Times-BoldItalic" },
        ["Times-Roman"] = new[] { "Times-Roman", "Times-Bold", "Times-Italic", "Times-BoldItalic" },
        ["Times New Roman"] = new[] { "Times-Roman", "Times-Bold", "Times-Italic", "Times-BoldItalic" },
        ["Courier"] = new[] { "Courier", "Courier-Bold", "Courier-Oblique", "Courier-BoldOblique" },
        ["Courier New"] = new[] { "Courier", "Courier-Bold", "Courier-Oblique", "Courier-BoldOblique" },
        ["Symbol"] = new[] { "Symbol", "Symbol", "Symbol", "Symbol" },
        ["ZapfDingbats"] = new[] { "ZapfDingbats", "ZapfDingbats", "ZapfDingbats", "ZapfDingbats" },
    };

    /// <summary>Nombres aceptados por <see cref="FontRegistry.Resolve"/>.</summary>
    public static IReadOnlyCollection<string> Families => Variants.Keys;

    public static bool IsBase14Family(string family) =>
        !string.IsNullOrWhiteSpace(family) && Variants.ContainsKey(family.Trim());

    public static bool TryGetPostScriptName(FontFace face, out string postScriptName)
    {
        postScriptName = string.Empty;

        if (face.FilePath is not null)
        {
            return false;
        }

        if (!Variants.TryGetValue(face.Family.Trim(), out var variants))
        {
            return false;
        }

        postScriptName = variants[(int)face.Style];
        return true;
    }
}
