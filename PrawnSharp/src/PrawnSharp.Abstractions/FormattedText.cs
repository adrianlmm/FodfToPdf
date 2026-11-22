using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PrawnSharp;

/// <summary>Posición de subíndice/superíndice.</summary>
public enum ScriptPosition
{
    None = 0,
    Subscript = 1,
    Superscript = 2,
}

/// <summary>Estilo de un fragmento de texto formateado.</summary>
public sealed record TextStyle
{
    public string? FontFamily { get; init; }
    public FontStyle? FontStyle { get; init; }
    public double? Size { get; init; }
    public PdfColor? Color { get; init; }
    public string? Link { get; init; }
    public bool? Underline { get; init; }
    public bool? Strikethrough { get; init; }
    public ScriptPosition? Script { get; init; }
    public double? CharacterSpacing { get; init; }
}

/// <summary>Un trozo de texto con su estilo.</summary>
public sealed record TextFragment(string Text, TextStyle? Style = null);

/// <summary>
/// Texto con varios estilos. Se puede construir con fragmentos o parsear el marcado
/// inline de Prawn (<c>&lt;b&gt;</c>, <c>&lt;i&gt;</c>, <c>&lt;color&gt;</c>...).
/// </summary>
public sealed class FormattedText
{
    public TextStyle BaseStyle { get; init; } = new();

    public IReadOnlyList<TextFragment> Fragments { get; init; } = Array.Empty<TextFragment>();

    public static FormattedText FromFragments(params TextFragment[] fragments) =>
        new() { Fragments = fragments };

    public static FormattedText FromFragments(IEnumerable<TextFragment> fragments) =>
        new() { Fragments = fragments.ToArray() };

    /// <summary>Parsea el marcado inline al estilo de Prawn.</summary>
    public static FormattedText Parse(string markup, TextStyle? baseStyle = null) =>
        FormattedTextParser.Parse(markup, baseStyle ?? new TextStyle());

    /// <summary>Une fragmentos consecutivos con el mismo estilo.</summary>
    internal static IReadOnlyList<TextFragment> Merge(IEnumerable<TextFragment> fragments)
    {
        var result = new List<TextFragment>();

        foreach (var fragment in fragments)
        {
            if (string.IsNullOrEmpty(fragment.Text))
            {
                continue;
            }

            if (result.Count > 0 && Equals(result[^1].Style, fragment.Style))
            {
                result[^1] = result[^1] with { Text = result[^1].Text + fragment.Text };
            }
            else
            {
                result.Add(fragment);
            }
        }

        return result;
    }
}

/// <summary>Parser del marcado inline de Prawn.</summary>
internal static class FormattedTextParser
{
    private static readonly Regex TagRegex = new(
        @"^(?<close>/?)(?<name>[a-zA-Z][a-zA-Z0-9]*)(?<attrs>[^>]*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AttributeRegex = new(
        @"(?<name>[a-zA-Z_][a-zA-Z0-9_-]*)\s*=\s*(?<quote>['""])(?<value>.*?)\k<quote>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    public static FormattedText Parse(string markup, TextStyle baseStyle)
    {
        ArgumentNullException.ThrowIfNull(markup);

        var fragments = new List<TextFragment>();
        var stack = new Stack<(string Name, TextStyle Style)>();
        stack.Push((string.Empty, baseStyle));

        var text = new StringBuilder();
        var index = 0;

        while (index < markup.Length)
        {
            var open = markup.IndexOf('<', index);

            if (open < 0)
            {
                text.Append(markup, index, markup.Length - index);
                break;
            }

            if (open > index)
            {
                text.Append(markup, index, open - index);
            }

            var close = markup.IndexOf('>', open + 1);

            if (close < 0)
            {
                text.Append(markup, open, markup.Length - open);
                break;
            }

            var tag = markup.Substring(open + 1, close - open - 1);
            var match = TagRegex.Match(tag);

            if (!match.Success)
            {
                text.Append('<').Append(tag).Append('>');
                index = close + 1;
                continue;
            }

            Flush(fragments, text, stack.Peek().Style);

            var isClosing = match.Groups["close"].Value == "/";
            var name = match.Groups["name"].Value.ToLowerInvariant();
            var attrs = match.Groups["attrs"].Value;

            if (isClosing)
            {
                if (stack.Count > 1)
                {
                    stack.Pop();
                }
            }
            else if (name == "br")
            {
                text.Append('\n');
                Flush(fragments, text, stack.Peek().Style);
            }
            else
            {
                var next = ApplyTag(stack.Peek().Style, name, attrs);

                if (next is not null)
                {
                    stack.Push((name, next));
                }
                else
                {
                    // Etiqueta desconocida: se conserva como texto literal.
                    text.Append('<').Append(tag).Append('>');
                }
            }

            index = close + 1;
        }

        Flush(fragments, text, stack.Peek().Style);

        return new FormattedText
        {
            BaseStyle = baseStyle,
            Fragments = FormattedText.Merge(fragments),
        };
    }

    private static TextStyle? ApplyTag(TextStyle current, string name, string attrs)
    {
        switch (name)
        {
            case "b":
                return current with { FontStyle = AddStyle(current.FontStyle ?? FontStyle.Regular, bold: true) };
            case "i":
                return current with { FontStyle = AddStyle(current.FontStyle ?? FontStyle.Regular, italic: true) };
            case "u":
                return current with { Underline = true };
            case "strikethrough":
                return current with { Strikethrough = true };
            case "sub":
                return current with { Script = ScriptPosition.Subscript };
            case "sup":
                return current with { Script = ScriptPosition.Superscript };
            case "font":
            {
                var values = Attributes(attrs);
                var result = current;

                if (values.TryGetValue("name", out var family) && !string.IsNullOrWhiteSpace(family))
                {
                    result = result with { FontFamily = family };
                }

                if (values.TryGetValue("size", out var size) &&
                    double.TryParse(size, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSize))
                {
                    result = result with { Size = parsedSize };
                }

                if (values.TryGetValue("character_spacing", out var spacing) &&
                    double.TryParse(spacing, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSpacing))
                {
                    result = result with { CharacterSpacing = parsedSpacing };
                }

                return result;
            }
            case "color":
            {
                var values = Attributes(attrs);

                if (values.TryGetValue("rgb", out var rgb))
                {
                    return current with { Color = PdfColor.FromHex(rgb) };
                }

                if (values.TryGetValue("c", out var cText) &&
                    values.TryGetValue("m", out var mText) &&
                    values.TryGetValue("y", out var yText) &&
                    values.TryGetValue("k", out var kText))
                {
                    var c = ParsePercent(cText);
                    var m = ParsePercent(mText);
                    var y = ParsePercent(yText);
                    var k = ParsePercent(kText);

                    var r = (byte)Math.Round(255 * (1 - c) * (1 - k));
                    var g = (byte)Math.Round(255 * (1 - m) * (1 - k));
                    var b = (byte)Math.Round(255 * (1 - y) * (1 - k));

                    return current with { Color = new PdfColor(r, g, b) };
                }

                return current;
            }
            case "link":
            {
                var values = Attributes(attrs);
                return values.TryGetValue("href", out var href)
                    ? current with { Link = href }
                    : current;
            }
            default:
                return null;
        }
    }

    private static void Flush(List<TextFragment> fragments, StringBuilder text, TextStyle style)
    {
        if (text.Length == 0)
        {
            return;
        }

        var value = WebUtility.HtmlDecode(text.ToString());
        text.Clear();

        if (value.Length > 0)
        {
            fragments.Add(new TextFragment(value, style));
        }
    }

    private static Dictionary<string, string> Attributes(string attrs)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in AttributeRegex.Matches(attrs))
        {
            result[match.Groups["name"].Value] = match.Groups["value"].Value;
        }

        return result;
    }

    private static FontStyle AddStyle(FontStyle current, bool bold = false, bool italic = false)
    {
        var isBold = current is FontStyle.Bold or FontStyle.BoldItalic;
        var isItalic = current is FontStyle.Italic or FontStyle.BoldItalic;

        isBold |= bold;
        isItalic |= italic;

        return (isBold, isItalic) switch
        {
            (true, true) => FontStyle.BoldItalic,
            (true, false) => FontStyle.Bold,
            (false, true) => FontStyle.Italic,
            _ => FontStyle.Regular,
        };
    }

    private static double ParsePercent(string value) =>
        double.TryParse(value.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? Math.Clamp(number / 100.0, 0, 1)
            : 0;
}
