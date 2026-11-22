using PrawnSharp.Conformance;
using Xunit;

namespace PrawnSharp.Tests;

public sealed class ConformanceTests
{
    [Fact]
    public void CorpusMatchesPrawnReference()
    {
        var root = ConformancePaths.FindRoot();
        var result = ConformanceRunner.Run(
            System.IO.Path.Combine(root, "conformance", "corpus.json"),
            System.IO.Path.Combine(root, "conformance", "reference"));

        Assert.True(
            result.LineMismatches == 0,
            $"Cortes distintos en {result.LineMismatches}/{result.Total}: " +
            string.Join(" | ", result.Failures.Take(5).Select(f => f.Id + ": " + f.Message)));

        Assert.True(
            result.PositionFailures == 0,
            $"Posiciones fuera de tolerancia en {result.PositionFailures}/{result.Total}: " +
            string.Join(" | ", result.Failures.Take(5).Select(f => f.Id + ": " + f.Message)));
    }

    [Fact]
    public void TtfCorpusMatchesPrawnReference()
    {
        var root = ConformancePaths.FindRoot();
        var result = ConformanceRunner.Run(
            System.IO.Path.Combine(root, "conformance", "ttf-corpus.json"),
            System.IO.Path.Combine(root, "conformance", "reference-ttf"));

        Assert.True(
            result.LineMismatches == 0,
            $"Cortes TTF distintos en {result.LineMismatches}/{result.Total}: " +
            string.Join(" | ", result.Failures.Take(5).Select(f => f.Id + ": " + f.Message)));

        Assert.True(
            result.PositionFailures == 0,
            $"Posiciones TTF fuera de tolerancia en {result.PositionFailures}/{result.Total}: " +
            string.Join(" | ", result.Failures.Take(5).Select(f => f.Id + ": " + f.Message)));
    }
}
