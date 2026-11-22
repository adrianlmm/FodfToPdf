using System.Text;

namespace PrawnSharp;

/// <summary>Estilo resuelto: fuente concreta, AFM y métricas de un fragmento.</summary>
internal sealed record ResolvedStyle(
    FontFace Face,
    IFontMetrics Font,
    double Size,
    PdfColor Color,
    string? Link,
    bool Underline,
    bool Strikethrough,
    ScriptPosition Script,
    double Rise,
    double CharacterSpacing);

/// <summary>Token de texto formateado: palabra, espacio o salto de párrafo.</summary>
internal sealed record FormattedToken(
    string Text,
    ResolvedStyle Style,
    bool IsSpace,
    bool IsLineBreak);

/// <summary>Línea de texto formateado ya cortada.</summary>
internal sealed record FormattedLine(
    IReadOnlyList<FormattedToken> Tokens,
    double Width,
    int SpaceCount,
    bool IsLastLine,
    double Indent,
    double LineHeight,
    double Ascent,
    double Descent);

/// <summary>
/// Corte y posicionamiento de texto con varios estilos. Cada token conserva su fuente y
/// tamaño; las líneas se miden con la suma de tokens y los espacios se usan para justificar.
/// </summary>
internal static class FormattedTextLayout
{
    public static ResolvedStyle ResolveBase(
        FontFace face,
        double size,
        PdfColor color,
        double characterSpacing,
        Func<FontFace, IFontMetrics> metrics)
    {
        var font = metrics(face);

        return new ResolvedStyle(
            face,
            font,
            size,
            color,
            null,
            false,
            false,
            ScriptPosition.None,
            0,
            characterSpacing);
    }

    public static ResolvedStyle Resolve(
        TextStyle? overlay,
        ResolvedStyle baseStyle,
        Func<FontFace, IFontMetrics> metrics,
        Func<FontFace, FontFace> resolveFace)
    {
        if (overlay is null)
        {
            return baseStyle;
        }

        var family = overlay.FontFamily ?? baseStyle.Face.Family;
        var style = overlay.FontStyle ?? baseStyle.Face.Style;
        var size = overlay.Size ?? baseStyle.Size;
        var color = overlay.Color ?? baseStyle.Color;
        var link = overlay.Link ?? baseStyle.Link;
        var underline = overlay.Underline ?? baseStyle.Underline;
        var strikethrough = overlay.Strikethrough ?? baseStyle.Strikethrough;
        var script = overlay.Script ?? baseStyle.Script;
        var characterSpacing = overlay.CharacterSpacing ?? baseStyle.CharacterSpacing;
        var face = resolveFace(new FontFace(family, style));
        var font = metrics(face);

        var rise = 0.0;

        if (script != ScriptPosition.None)
        {
            size *= 0.7;
            rise = script == ScriptPosition.Subscript ? -size * 0.2 : size * 0.4;
        }

        return new ResolvedStyle(
            face,
            font,
            size,
            color,
            link,
            underline,
            strikethrough,
            script,
            rise,
            characterSpacing);
    }

    public static double Measure(FormattedToken token, bool kerning) =>
        Measure(token.Text, token.Style, kerning);

    public static double Measure(string text, ResolvedStyle style, bool kerning) =>
        style.Font.WidthOf(text, style.Size, kerning) + (style.CharacterSpacing * text.Length);

    public static IReadOnlyList<FormattedLine> Wrap(
        FormattedText text,
        double maxWidth,
        ResolvedStyle baseStyle,
        bool kerning,
        double firstLineIndent,
        double leading,
        Func<FontFace, IFontMetrics> metrics,
        Func<FontFace, FontFace> resolveFace)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(baseStyle);

        if (maxWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxWidth), "El ancho de la caja debe ser positivo.");
        }

        if (firstLineIndent < 0 || firstLineIndent >= maxWidth)
        {
            throw new ArgumentOutOfRangeException(nameof(firstLineIndent), "La sangría debe caber en el ancho.");
        }

        var tokens = Tokenize(text, baseStyle, metrics, resolveFace);
        var lines = new List<FormattedLine>();
        var current = new List<FormattedToken>();
        var indent = firstLineIndent;
        var available = maxWidth - indent;

        foreach (var token in tokens)
        {
            if (token.IsLineBreak)
            {
                AddLine(lines, current, indent, isLast: true, baseStyle, kerning, leading);
                current.Clear();
                indent = 0;
                available = maxWidth;
                continue;
            }

            var width = Measure(token, kerning);

            if (token.IsSpace)
            {
                if (current.Count == 0)
                {
                    continue;
                }

                current.Add(token);
                continue;
            }

            var currentWidth = current.Sum(t => Measure(t, kerning));

            if (currentWidth + width <= available)
            {
                current.Add(token);
                continue;
            }

            if (current.Count > 0)
            {
                AddLine(lines, current, indent, isLast: false, baseStyle, kerning, leading);
                current.Clear();
                indent = 0;
                available = maxWidth;
            }

            if (width <= available)
            {
                current.Add(token);
            }
            else
            {
                var chunks = SplitToken(token, available, kerning);

                for (var i = 0; i < chunks.Count; i++)
                {
                    if (i > 0)
                    {
                        AddLine(lines, current, indent, isLast: false, baseStyle, kerning, leading);
                        current.Clear();
                        indent = 0;
                    }

                    current.Add(chunks[i]);
                }
            }
        }

        AddLine(lines, current, indent, isLast: true, baseStyle, kerning, leading);

        return lines;
    }

    private static List<FormattedToken> Tokenize(
        FormattedText text,
        ResolvedStyle baseStyle,
        Func<FontFace, IFontMetrics> metrics,
        Func<FontFace, FontFace> resolveFace)
    {
        var tokens = new List<FormattedToken>();

        foreach (var fragment in text.Fragments)
        {
            var style = Resolve(fragment.Style, baseStyle, metrics, resolveFace);
            var word = new StringBuilder();

            void Flush()
            {
                if (word.Length > 0)
                {
                    tokens.Add(new FormattedToken(word.ToString(), style, false, false));
                    word.Clear();
                }
            }

            var fragmentText = fragment.Text;

            for (var i = 0; i < fragmentText.Length;)
            {
                var ch = fragmentText[i];

                if (ch == '\n')
                {
                    Flush();
                    tokens.Add(new FormattedToken("\n", style, false, true));
                    i++;
                    continue;
                }

                if (ch == '\u200B')
                {
                    Flush();
                    i++;
                    continue;
                }

                if (char.IsWhiteSpace(ch))
                {
                    Flush();
                    tokens.Add(new FormattedToken(ch.ToString(), style, true, false));
                    i++;
                    continue;
                }

                if (ch == '-')
                {
                    var start = i;

                    while (i < fragmentText.Length && fragmentText[i] == '-')
                    {
                        i++;
                    }

                    var run = fragmentText[start..i];

                    // Prawn agrupa la racha de guiones con el prefijo y permite cortar
                    // después de ella; una racha inicial no se parte por dentro.
                    if (word.Length > 0)
                    {
                        word.Append(run);
                        Flush();
                    }
                    else
                    {
                        word.Append(run);
                    }

                    continue;
                }

                word.Append(ch);
                i++;
            }

            Flush();
        }

        return tokens;
    }

    private static List<FormattedToken> SplitToken(FormattedToken token, double width, bool kerning)
    {
        var chunks = new List<FormattedToken>();
        var current = new StringBuilder();

        foreach (var ch in token.Text)
        {
            var candidate = current.ToString() + ch;

            if (current.Length > 0 && Measure(candidate, token.Style, kerning) > width)
            {
                chunks.Add(new FormattedToken(current.ToString(), token.Style, false, false));
                current.Clear();
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            chunks.Add(new FormattedToken(current.ToString(), token.Style, false, false));
        }

        return chunks;
    }

    private static void AddLine(
        List<FormattedLine> lines,
        List<FormattedToken> tokens,
        double indent,
        bool isLast,
        ResolvedStyle baseStyle,
        bool kerning,
        double leading)
    {
        // Los espacios al final de la línea no cuentan para el ancho.
        while (tokens.Count > 0 && tokens[^1].IsSpace)
        {
            tokens.RemoveAt(tokens.Count - 1);
        }

        var width = tokens.Sum(t => Measure(t, kerning));
        var spaceCount = tokens.Count(t => t.IsSpace);
        var styles = tokens.Count > 0
            ? tokens.Select(t => t.Style).ToList()
            : [baseStyle];
        var lineHeight = styles.Max(s => s.Font.LineHeightAt(s.Size)) + Math.Max(leading, 0);
        var ascent = styles.Max(s => s.Font.AscentAt(s.Size) + Math.Max(0, s.Rise));
        var descent = styles.Max(s => -s.Font.DescentAt(s.Size) + Math.Max(0, -s.Rise));

        lines.Add(new FormattedLine(
            tokens.ToArray(),
            width,
            spaceCount,
            isLast,
            indent,
            lineHeight,
            ascent,
            descent));
    }
}
