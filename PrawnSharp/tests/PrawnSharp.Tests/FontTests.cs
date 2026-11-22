using PrawnSharp.Fonts;
using Xunit;

namespace PrawnSharp.Tests;

public sealed class FontTests
{
    [Theory]
    [InlineData("Helvetica")]
    [InlineData("Times")]
    [InlineData("Times New Roman")]
    [InlineData("Courier")]
    [InlineData("Courier New")]
    [InlineData("Symbol")]
    [InlineData("ZapfDingbats")]
    public void RecognizesBase14Families(string family)
    {
        Assert.True(Base14Fonts.IsBase14Family(family));
    }

    [Theory]
    [InlineData("Helvetica", FontStyle.Regular, "Helvetica")]
    [InlineData("Helvetica", FontStyle.Bold, "Helvetica-Bold")]
    [InlineData("Helvetica", FontStyle.Italic, "Helvetica-Oblique")]
    [InlineData("Helvetica", FontStyle.BoldItalic, "Helvetica-BoldOblique")]
    [InlineData("Times", FontStyle.Regular, "Times-Roman")]
    [InlineData("Times", FontStyle.Bold, "Times-Bold")]
    [InlineData("Courier", FontStyle.Regular, "Courier")]
    public void ResolvesPostScriptNames(string family, FontStyle style, string expected)
    {
        Assert.True(Base14Fonts.TryGetPostScriptName(new FontFace(family, style), out var name));
        Assert.Equal(expected, name);
    }

    [Fact]
    public void RejectsNonBase14()
    {
        Assert.False(Base14Fonts.TryGetPostScriptName(new FontFace("Arial"), out _));
    }
}
