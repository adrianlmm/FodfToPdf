namespace PrawnSharp.Fonts;

/// <summary>
/// Registro de familias. En F1 sólo resuelve las 14 fuentes base de PDF; las
/// familias TTF/OTF se registrarán aquí en F5.
/// </summary>
public sealed class FontRegistry
{
    private readonly Dictionary<string, FontFace> _registered = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registra una cara concreta (para TTF/OTF en fases posteriores).</summary>
    public void Register(string family, FontFace face)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(family);
        ArgumentNullException.ThrowIfNull(face);

        _registered[Key(family, face.Style)] = face;
    }

    /// <summary>Resuelve familia + estilo a una cara registrada o base-14.</summary>
    public FontFace Resolve(string family, FontStyle style = FontStyle.Regular)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(family);

        if (_registered.TryGetValue(Key(family, style), out var registered))
        {
            return registered;
        }

        // Si la familia TTF está registrada pero falta esa cara, se usa la regular.
        if (style != FontStyle.Regular &&
            _registered.TryGetValue(Key(family, FontStyle.Regular), out var regular))
        {
            return regular;
        }

        var face = FontFace.Base14(family.Trim(), style);

        if (Base14Fonts.TryGetPostScriptName(face, out _))
        {
            return face;
        }

        throw new UnsupportedFontException(
            $"La fuente '{family} {style}' no es una de las 14 fuentes base de PDF ni está registrada. " +
            "Usa RegisterFontFamily(...) para registrar un archivo TTF/OTF.");
    }

    public bool IsSupported(string family)
    {
        if (string.IsNullOrWhiteSpace(family))
        {
            return false;
        }

        var prefix = family.Trim() + "/";
        return Base14Fonts.IsBase14Family(family) ||
               _registered.Keys.Any(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string Key(string family, FontStyle style) => $"{family.Trim()}/{style}";
}
