using System.Reflection;
using PdfSharp.Pdf;
using Xunit;

namespace PrawnSharp.Tests;

public sealed class PdfSharpVersionTests
{
    [Fact]
    public void UsesPinnedMitVersion()
    {
        var assembly = typeof(PdfDocument).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var version = informational ?? assembly.GetName().Version?.ToString() ?? string.Empty;

        Assert.Contains("6.2.4", version);
        Assert.DoesNotContain("6.5", version);
    }
}
