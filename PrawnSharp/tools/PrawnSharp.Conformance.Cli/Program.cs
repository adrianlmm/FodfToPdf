using PrawnSharp.Conformance;

var root = ConformancePaths.FindRoot();
var corpusPath = args.Length > 0 ? args[0] : Path.Combine(root, "conformance", "corpus.json");
var referenceDirectory = args.Length > 1 ? args[1] : Path.Combine(root, "conformance", "reference");

var result = ConformanceRunner.Run(corpusPath, referenceDirectory);

Console.WriteLine(
    $"Corpus: {result.Total}  " +
    $"OK: {result.Passed}  " +
    $"Cortes: {result.LineMismatches}  " +
    $"Posición: {result.PositionFailures}  " +
    $"Delta máx: {result.MaxPositionDelta:F3} pt");

foreach (var failure in result.Failures.Take(30))
{
    Console.WriteLine($"[{failure.Id}] {failure.Kind} ({failure.Source}): {failure.Message}");
}

return result.LineMismatches == 0 && result.PositionFailures == 0 ? 0 : 1;
