using System.Collections.Concurrent;

namespace PrawnSharp.Fonts;

/// <summary>Carga y cachea las métricas AFM incrustadas de las 14 fuentes base-14.</summary>
public static class AfmFonts
{
    private static readonly ConcurrentDictionary<string, AfmFont> Cache = new(StringComparer.Ordinal);

    public static AfmFont Get(string postScriptName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(postScriptName);
        return Cache.GetOrAdd(postScriptName, AfmFont.Load);
    }

    public static bool IsLoaded(string postScriptName) =>
        !string.IsNullOrWhiteSpace(postScriptName) && Cache.ContainsKey(postScriptName);
}
