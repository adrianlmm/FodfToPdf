using PrawnSharp;
using PrawnSharp.Fonts;
using Xunit;

namespace PrawnSharp.Tests;

public sealed class TextLayoutTests
{
    private const string Paragraph =
        "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor " +
        "incididunt ut labore et dolore magna aliqua.";

    [Fact]
    public void WrapsPrawnParagraphAtSamePoints()
    {
        var font = AfmFonts.Get("Helvetica");
        var lines = TextLayout.Wrap(Paragraph, 150, font, 12, kerning: true);

        Assert.Collection(
            lines,
            l => Assert.Equal("Lorem ipsum dolor sit amet,", l.Text),
            l => Assert.Equal("consectetur adipiscing elit,", l.Text),
            l => Assert.Equal("sed do eiusmod tempor", l.Text),
            l => Assert.Equal("incididunt ut labore et", l.Text),
            l => Assert.Equal("dolore magna aliqua.", l.Text));
    }

    [Fact]
    public void WrapsAtSpaces()
    {
        var font = AfmFonts.Get("Helvetica");
        var lines = TextLayout.Wrap("Hello world", 30, font, 12, kerning: true);

        Assert.Collection(
            lines,
            l => Assert.Equal("Hello", l.Text),
            l => Assert.Equal("world", l.Text));
    }

    [Fact]
    public void KeepsHyphenatedWordTogetherWhenItFits()
    {
        var font = AfmFonts.Get("Helvetica");
        var lines = TextLayout.Wrap("foo-bar-baz", 70, font, 12, kerning: true);

        Assert.Single(lines);
        Assert.Equal("foo-bar-baz", lines[0].Text);
    }

    [Fact]
    public void SplitsHyphenatedWordWhenItDoesNotFit()
    {
        var font = AfmFonts.Get("Helvetica");
        var lines = TextLayout.Wrap("foo-bar-baz", 30, font, 12, kerning: true);

        Assert.Equal("foo-bar-baz", string.Concat(lines.Select(l => l.Text)));
        Assert.All(lines, line => Assert.True(line.Width <= 30.001, $"'{line.Text}' mide {line.Width}"));
    }

    [Fact]
    public void HonorsZeroWidthSpace()
    {
        var font = AfmFonts.Get("Helvetica");
        var lines = TextLayout.Wrap("foo\u200Bbar", 20, font, 12, kerning: true);

        Assert.Collection(
            lines,
            l => Assert.Equal("foo", l.Text),
            l => Assert.Equal("bar", l.Text));
    }

    [Fact]
    public void SplitsWordsLongerThanTheLine()
    {
        var font = AfmFonts.Get("Helvetica");
        var lines = TextLayout.Wrap("abcdefghij", 20, font, 12, kerning: true);

        Assert.All(lines, line => Assert.True(line.Width <= 20.001, $"'{line.Text}' mide {line.Width}"));
        Assert.Equal("abcdefghij", string.Concat(lines.Select(l => l.Text)));
    }

    [Fact]
    public void MarksLastLineOfEachParagraph()
    {
        var font = AfmFonts.Get("Helvetica");
        var lines = TextLayout.Wrap("Hello\nworld", 100, font, 12, kerning: true);

        Assert.Equal(2, lines.Count);
        Assert.True(lines[0].IsLastLine);
        Assert.True(lines[1].IsLastLine);
    }

    [Fact]
    public void AppliesFirstLineIndent()
    {
        var font = AfmFonts.Get("Helvetica");
        var lines = TextLayout.Wrap("one two three", 50, font, 12, kerning: true, firstLineIndent: 10);

        Assert.Equal(10, lines[0].Indent);
        Assert.All(lines.Skip(1), line => Assert.Equal(0, line.Indent));
    }

    [Fact]
    public void HeightOfCountsWrappedLines()
    {
        using var doc = new PrawnDocument();
        doc.Font("Helvetica", 12);

        var height = doc.HeightOf("Hello world", new TextOptions { Width = 30 });

        Assert.Equal(27.744, height, 3);
    }

    [Fact]
    public void FinalGapRemovesSpaceBelowLastLine()
    {
        using var doc = new PrawnDocument();
        doc.Font("Helvetica", 12);

        var withGap = doc.HeightOf("Hello world", new TextOptions { Width = 30 });
        var withoutGap = doc.HeightOf("Hello world", new TextOptions { Width = 30, FinalGap = false });

        Assert.True(withoutGap < withGap);
        Assert.InRange(withGap - withoutGap, 2.5, 3.0);
    }
}
