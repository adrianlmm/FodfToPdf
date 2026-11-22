using System.Text;

namespace PrawnSharp;

/// <summary>Una línea ya cortada, con su ancho AFM y el espacio para justificar.</summary>
internal sealed record TextLine(
    string Text,
    double Width,
    int SpaceCount,
    bool IsLastLine,
    double Indent);

/// <summary>
/// Corte de línea por ancho. Rompe en espacios, tabuladores, guiones y zero-width
/// spaces; si una palabra no cabe, la parte por caracteres. No conoce PDF ni QuestPDF.
/// </summary>
internal static class TextLayout
{
    public static IReadOnlyList<TextLine> Wrap(
        string text,
        double maxWidth,
        IFontMetrics font,
        double size,
        bool kerning,
        double firstLineIndent = 0,
        double characterSpacing = 0)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(text);

        if (maxWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxWidth), "El ancho de la caja debe ser positivo.");
        }

        if (firstLineIndent < 0 || firstLineIndent >= maxWidth)
        {
            throw new ArgumentOutOfRangeException(nameof(firstLineIndent), "La sangría debe caber en el ancho.");
        }

        var result = new List<TextLine>();
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                             .Replace('\r', '\n');
        var paragraphs = normalized.Split('\n');

        foreach (var paragraph in paragraphs)
        {
            var lines = WrapParagraph(
                paragraph,
                maxWidth,
                font,
                size,
                kerning,
                firstLineIndent,
                characterSpacing);

            if (lines.Count == 0)
            {
                lines.Add(new TextLine(string.Empty, 0, 0, true, 0));
            }
            else
            {
                lines[^1] = lines[^1] with { IsLastLine = true };
            }

            result.AddRange(lines);
        }

        return result;
    }

    private static List<TextLine> WrapParagraph(
        string paragraph,
        double maxWidth,
        IFontMetrics font,
        double size,
        bool kerning,
        double firstLineIndent,
        double characterSpacing)
    {
        var lines = new List<TextLine>();
        var tokens = Tokenize(paragraph);
        var line = new StringBuilder();
        var indent = firstLineIndent;
        var available = maxWidth - indent;
        var lineWidth = 0.0;

        double Measure(string value) =>
            font.WidthOf(value, size, kerning) + (characterSpacing * value.Length);

        foreach (var token in tokens)
        {
            // Un token compuesto sólo de espacios no abre línea; se pega si ya hay texto.
            if (token.AsSpan().Trim().Length == 0)
            {
                if (line.Length > 0)
                {
                    line.Append(token);
                    lineWidth += Measure(token);
                }

                continue;
            }

            var tokenWidth = Measure(token);

            if (lineWidth + tokenWidth <= available)
            {
                line.Append(token);
                lineWidth += tokenWidth;
                continue;
            }

            if (line.Length == 0)
            {
                AppendLongToken(
                    lines,
                    line,
                    token,
                    available,
                    maxWidth,
                    font,
                    size,
                    kerning,
                    characterSpacing,
                    ref indent);
                lineWidth = Measure(line.ToString());
                available = maxWidth - indent;
                continue;
            }

            AddLine(lines, line, indent, font, size, kerning, characterSpacing);
            line.Clear();
            lineWidth = 0;
            indent = 0;
            available = maxWidth;

            if (tokenWidth <= available)
            {
                line.Append(token);
                lineWidth = tokenWidth;
            }
            else
            {
                AppendLongToken(
                    lines,
                    line,
                    token,
                    available,
                    maxWidth,
                    font,
                    size,
                    kerning,
                    characterSpacing,
                    ref indent);
                lineWidth = Measure(line.ToString());
                available = maxWidth - indent;
            }
        }

        if (line.Length > 0 || lines.Count == 0)
        {
            AddLine(lines, line, indent, font, size, kerning, characterSpacing);
        }

        return lines;
    }

    private static void AppendLongToken(
        List<TextLine> lines,
        StringBuilder line,
        string token,
        double available,
        double maxWidth,
        IFontMetrics font,
        double size,
        bool kerning,
        double characterSpacing,
        ref double indent)
    {
        foreach (var chunk in SplitLongToken(token, available, font, size, kerning, characterSpacing))
        {
            if (line.Length > 0)
            {
                AddLine(lines, line, indent, font, size, kerning, characterSpacing);
                line.Clear();
                indent = 0;
                available = maxWidth;
            }

            line.Append(chunk);
        }
    }

    private static IEnumerable<string> SplitLongToken(
        string token,
        double width,
        IFontMetrics font,
        double size,
        bool kerning,
        double characterSpacing)
    {
        var text = token.TrimEnd();
        var current = new StringBuilder();

        foreach (var ch in text)
        {
            var candidate = current.ToString() + ch;

            if (current.Length > 0 &&
                Measure(candidate, font, size, kerning, characterSpacing) > width)
            {
                yield return current.ToString();
                current.Clear();
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static void AddLine(
        List<TextLine> lines,
        StringBuilder line,
        double indent,
        IFontMetrics font,
        double size,
        bool kerning,
        double characterSpacing)
    {
        var text = line.ToString().TrimEnd();
        var width = Measure(text, font, size, kerning, characterSpacing);
        var spaces = text.Count(ch => ch == ' ');

        lines.Add(new TextLine(text, width, spaces, false, indent));
    }

    private static double Measure(string text, IFontMetrics font, double size, bool kerning, double characterSpacing) =>
        font.WidthOf(text, size, kerning) + (characterSpacing * text.Length);

    /// <summary>
    /// Divide en unidades que terminan en un punto de corte: espacio, tabulador, guion
    /// o zero-width space. El zero-width space no se conserva.
    /// </summary>
    private static List<string> Tokenize(string paragraph)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        for (var i = 0; i < paragraph.Length;)
        {
            var ch = paragraph[i];

            if (ch == '\u200B')
            {
                Flush();
                i++;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                Flush();
                tokens.Add(ch.ToString());
                i++;
                continue;
            }

            if (ch == '-')
            {
                var start = i;

                while (i < paragraph.Length && paragraph[i] == '-')
                {
                    i++;
                }

                var run = paragraph[start..i];

                // Prawn agrupa el guion (o la racha de guiones) con el prefijo:
                // "foo---" es un punto de corte, "---foo" no se parte por dentro.
                if (current.Length > 0)
                {
                    current.Append(run);
                    Flush();
                }
                else
                {
                    current.Append(run);
                }

                continue;
            }

            current.Append(ch);
            i++;
        }

        Flush();
        return tokens;
    }
}
