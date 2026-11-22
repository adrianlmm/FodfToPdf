using System.Text.Json;
using System.Text.Json.Serialization;
using IOPath = System.IO.Path;
using PrawnSharp;
using Pig = UglyToad.PdfPig;

namespace PrawnSharp.Conformance;

public sealed class Corpus
{
    public int Version { get; init; } = 1;

    public string Generated { get; init; } = string.Empty;

    public string SourceRoot { get; init; } = string.Empty;

    [JsonPropertyName("items")]
    public IReadOnlyList<CorpusItem> Items { get; init; } = Array.Empty<CorpusItem>();
}

public sealed class CorpusItem
{
    public string Id { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;

    public double Width { get; init; }

    public string Font { get; init; } = "Helvetica";

    /// <summary>Archivo TTF/OTF opcional; si está, se registra la familia con ese archivo.</summary>
    public string? FontPath { get; init; }

    public double Size { get; init; } = 12;

    public string Format { get; init; } = "plain";
}

public sealed record ConformanceFailure(string Id, string Source, string Kind, string Message);

public sealed record ConformanceResult(
    int Total,
    int Passed,
    int LineMismatches,
    int PositionFailures,
    double MaxPositionDelta,
    IReadOnlyList<ConformanceFailure> Failures);

/// <summary>Localiza la raíz del repo subiendo desde bin/ hasta conformance/corpus.json.</summary>
public static class ConformancePaths
{
    public static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(IOPath.Combine(directory.FullName, "conformance", "corpus.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "No se encontró 'conformance/corpus.json' subiendo desde " + AppContext.BaseDirectory);
    }
}

/// <summary>
/// Ejecuta el corpus: genera cada PDF con PrawnSharp, lo compara con el PDF de Prawn
/// (líneas y posiciones de palabras) y devuelve un resumen.
/// </summary>
public static class ConformanceRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static Corpus LoadCorpus(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Corpus>(json, JsonOptions)
               ?? throw new InvalidOperationException("No se pudo leer el corpus.");
    }

    public static ConformanceResult Run(
        string corpusPath,
        string referenceDirectory,
        double tolerance = 0.05,
        int? limit = null)
    {
        var corpus = LoadCorpus(corpusPath);
        var items = limit is { } max ? corpus.Items.Take(max).ToList() : corpus.Items.ToList();
        var failures = new List<ConformanceFailure>();
        var lineMismatches = 0;
        var positionFailures = 0;
        var passed = 0;
        var maxDelta = 0.0;

        foreach (var item in items)
        {
            try
            {
                var actualPdf = Render(item);
                var referencePath = IOPath.Combine(referenceDirectory, item.Id + ".pdf");

                if (!File.Exists(referencePath))
                {
                    failures.Add(new ConformanceFailure(item.Id, item.Source, "reference", "Falta " + referencePath));
                    lineMismatches++;
                    continue;
                }

                var referencePdf = File.ReadAllBytes(referencePath);
                var actualLines = ExtractLines(actualPdf);
                var referenceLines = ExtractLines(referencePdf);

                var lineFailure = CompareLines(item, referenceLines, actualLines, tolerance, out var itemDelta);
                maxDelta = Math.Max(maxDelta, itemDelta);

                if (lineFailure is null)
                {
                    passed++;
                }
                else
                {
                    failures.Add(lineFailure);

                    if (lineFailure.Kind == "position")
                    {
                        positionFailures++;
                    }
                    else
                    {
                        lineMismatches++;
                    }
                }
            }
            catch (Exception exception)
            {
                failures.Add(new ConformanceFailure(item.Id, item.Source, "exception", exception.Message));
                lineMismatches++;
            }
        }

        return new ConformanceResult(
            items.Count,
            passed,
            lineMismatches,
            positionFailures,
            maxDelta,
            failures);
    }

    private static byte[] Render(CorpusItem item)
    {
        using var document = new PrawnDocument(new DocumentOptions
        {
            PageSize = new Size(item.Width + 20, 5000),
            Margin = new Thickness(10),
        });

        if (!string.IsNullOrWhiteSpace(item.FontPath))
        {
            var fontPath = IOPath.IsPathRooted(item.FontPath)
                ? item.FontPath
                : IOPath.Combine(ConformancePaths.FindRoot(), item.FontPath);

            document.RegisterFontFamily(item.Font, fontPath);
        }

        document.Font(item.Font, item.Size);

        if (string.Equals(item.Format, "inline", StringComparison.OrdinalIgnoreCase))
        {
            document.FormattedText(item.Text, new TextOptions { Width = item.Width });
        }
        else
        {
            document.Text(item.Text, new TextOptions { Width = item.Width });
        }

        return document.RenderBytes();
    }

    private static List<Line> ExtractLines(byte[] pdf)
    {
        using var document = Pig.PdfDocument.Open(new MemoryStream(pdf));
        var page = document.GetPage(1);

        return page.GetWords()
            .GroupBy(word => Math.Round(word.BoundingBox.Bottom, 1))
            .OrderByDescending(group => group.Key)
            .Select(group =>
            {
                var words = group
                    .OrderBy(word => word.BoundingBox.Left)
                    .Select(word => new WordInfo(word.Text, word.BoundingBox.Left, word.BoundingBox.Bottom))
                    .ToList();

                return new Line(group.Key, string.Join(' ', words.Select(word => word.Text)), words);
            })
            .ToList();
    }

    private static ConformanceFailure? CompareLines(
        CorpusItem item,
        IReadOnlyList<Line> reference,
        IReadOnlyList<Line> actual,
        double tolerance,
        out double maxDelta)
    {
        maxDelta = 0;

        if (reference.Count != actual.Count)
        {
            return new ConformanceFailure(
                item.Id,
                item.Source,
                "lines",
                $"Prawn={reference.Count} líneas, PrawnSharp={actual.Count}. " +
                $"Primera Prawn: '{Cut(reference.Count > 0 ? reference[0].Text : null)}'. " +
                $"Primera PrawnSharp: '{Cut(actual.Count > 0 ? actual[0].Text : null)}'.");
        }

        for (var i = 0; i < reference.Count; i++)
        {
            if (!string.Equals(reference[i].Text, actual[i].Text, StringComparison.Ordinal))
            {
                return new ConformanceFailure(
                    item.Id,
                    item.Source,
                    "text",
                    $"Línea {i + 1}: Prawn='{Cut(reference[i].Text)}' PrawnSharp='{Cut(actual[i].Text)}'.");
            }

            if (reference[i].Words.Count != actual[i].Words.Count)
            {
                return new ConformanceFailure(
                    item.Id,
                    item.Source,
                    "words",
                    $"Línea {i + 1}: Prawn={reference[i].Words.Count} palabras, " +
                    $"PrawnSharp={actual[i].Words.Count}.");
            }

            for (var w = 0; w < reference[i].Words.Count; w++)
            {
                var expected = reference[i].Words[w];
                var observed = actual[i].Words[w];
                var delta = Math.Max(
                    Math.Abs(expected.Left - observed.Left),
                    Math.Abs(expected.Bottom - observed.Bottom));

                maxDelta = Math.Max(maxDelta, delta);
            }
        }

        if (maxDelta > tolerance)
        {
            return new ConformanceFailure(
                item.Id,
                item.Source,
                "position",
                $"Desviación máxima {maxDelta:F3} pt (tolerancia {tolerance:F3}).");
        }

        return null;
    }

    private static string Cut(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= 160 ? value : value[..160] + "…";
    }

    private sealed record Line(double Y, string Text, IReadOnlyList<WordInfo> Words);

    private sealed record WordInfo(string Text, double Left, double Bottom);
}
