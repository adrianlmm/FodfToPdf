using PrawnSharp.Fonts;
using Xunit;

namespace PrawnSharp.Tests;

public sealed class AfmTests
{
    [Fact]
    public void LoadsHelveticaMetrics()
    {
        var afm = AfmFonts.Get("Helvetica");

        Assert.Equal("Helvetica", afm.PostScriptName);
        Assert.Equal(667, afm.GetGlyphWidth("A"));
        Assert.Equal(278, afm.GetGlyphWidth("space"));
        Assert.Equal(556, afm.GetGlyphWidth("aacute"));
        Assert.Equal(556, afm.GetGlyphWidth("Euro"));
        Assert.Equal(718, afm.Ascender);
        Assert.Equal(-207, afm.Descender);
    }

    [Fact]
    public void HolaMundoMatchesPrawn()
    {
        // Prawn: width_of("Hola mundo", size: 12) = 64.692
        var width = AfmFonts.Get("Helvetica").WidthOf("Hola mundo", 12);

        Assert.Equal(64.692, width, 3);
    }

    [Theory]
    [InlineData("Courier", 72.0)]
    [InlineData("Times-Roman", 59.664)]
    [InlineData("Helvetica-Bold", 69.336)]
    public void WidthOfMatchesPrawnForOtherFonts(string postScriptName, double expected)
    {
        Assert.Equal(expected, AfmFonts.Get(postScriptName).WidthOf("Hola mundo", 12), 3);
    }

    [Fact]
    public void LineHeightMatchesPrawn()
    {
        var afm = AfmFonts.Get("Helvetica");

        Assert.Equal(231, afm.LineGap, 3);
        Assert.Equal(13.872, afm.LineHeightAt(12), 3);
    }

    [Fact]
    public void KerningMatchesAfmPairs()
    {
        var afm = AfmFonts.Get("Helvetica");

        // A=667, V=667, KPX A V = -70  ->  (667+667)*12/1000 = 16.008
        Assert.Equal(16.008, afm.WidthOf("AV", 12, kerning: false), 3);

        // (667+667-70)*12/1000 = 15.168
        Assert.Equal(15.168, afm.WidthOf("AV", 12, kerning: true), 3);
    }

    [Fact]
    public void WidthsForAccentsAndEuro()
    {
        var afm = AfmFonts.Get("Helvetica");

        Assert.Equal(6.672, afm.WidthOf("á", 12), 3);
        Assert.Equal(6.672, afm.WidthOf("€", 12), 3);
    }

    [Theory]
    [InlineData("Helvetica")]
    [InlineData("Helvetica-Bold")]
    [InlineData("Helvetica-Oblique")]
    [InlineData("Helvetica-BoldOblique")]
    [InlineData("Times-Roman")]
    [InlineData("Times-Bold")]
    [InlineData("Times-Italic")]
    [InlineData("Times-BoldItalic")]
    [InlineData("Courier")]
    [InlineData("Courier-Bold")]
    [InlineData("Courier-Oblique")]
    [InlineData("Courier-BoldOblique")]
    [InlineData("Symbol")]
    [InlineData("ZapfDingbats")]
    public void LoadsAllBase14Metrics(string postScriptName)
    {
        var afm = AfmFonts.Get(postScriptName);

        Assert.Equal(postScriptName, afm.PostScriptName);
    }
}
