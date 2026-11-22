using PrawnSharp;
using Xunit;

namespace PrawnSharp.Tests;

public sealed class GeometryTests
{
    [Fact]
    public void ConvertsUnitsToPoints()
    {
        Assert.Equal(72.0, 1.In(), 6);
        Assert.Equal(28.3464567, 1.Cm(), 5);
        Assert.Equal(2.83464567, 1.Mm(), 5);
        Assert.Equal(10.0, 10.Pt(), 6);
    }

    [Fact]
    public void PageSizesAreInPoints()
    {
        Assert.Equal(612.00, PageSizes.Letter.Width, 2);
        Assert.Equal(792.00, PageSizes.Letter.Height, 2);
        Assert.Equal(595.28, PageSizes.A4.Width, 2);
    }

    [Fact]
    public void RectComputesEdges()
    {
        var rect = new Rect(10, 20, 100, 50);

        Assert.Equal(10, rect.Left);
        Assert.Equal(20, rect.Top);
        Assert.Equal(110, rect.Right);
        Assert.Equal(70, rect.Bottom);
        Assert.Equal(new Point(10, 20), rect.TopLeft);
        Assert.Equal(new Point(110, 70), rect.BottomRight);
    }

    [Fact]
    public void ThicknessSupportsSingleValue()
    {
        Thickness margin = 32;

        Assert.Equal(32, margin.Left);
        Assert.Equal(32, margin.Top);
        Assert.Equal(32, margin.Right);
        Assert.Equal(32, margin.Bottom);
        Assert.Equal(64, margin.Horizontal);
    }
}
