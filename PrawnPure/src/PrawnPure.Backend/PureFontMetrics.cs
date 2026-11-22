using PrawnPure.TTFunk;
using PrawnSharp;

namespace PrawnPure.Backend;

/// <summary>
/// IFontMetrics implementation backed directly by PrawnPure.TTFunk (zero PDFsharp).
/// Matches Ruby Prawn/ttfunk and TrueTypeFontInfo metrics to the exact thousandth of em.
/// </summary>
public sealed class PureFontMetrics : IFontMetrics
{
    private readonly TtfFile _ttf;

    public PureFontMetrics(string filePath)
    {
        _ttf = TtfFile.Open(filePath);
    }

    public double WidthOf(string text, double fontSize, bool kerning = false)
        => _ttf.WidthOf(text, fontSize, kerning);

    public double ExactWidthOf(string text, double fontSize, bool kerning = false)
        => _ttf.ExactWidthOf(text, fontSize, kerning);

    public double AscentAt(double fontSize) => _ttf.AscentAt(fontSize);

    public double DescentAt(double fontSize) => _ttf.DescentAt(fontSize);

    public double LineHeightAt(double fontSize) => _ttf.LineHeightAt(fontSize);

    public double UnderlinePositionAt(double fontSize) => _ttf.UnderlinePositionAt(fontSize);

    public double UnderlineThicknessAt(double fontSize) => _ttf.UnderlineThicknessAt(fontSize);
}
