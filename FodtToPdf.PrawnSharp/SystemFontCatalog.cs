using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using PrawnSharp;
using Path = System.IO.Path;

namespace FodtToPdf;

internal sealed class FontMetricRecord
{
    public string Family { get; init; } = "";
    public string Subfamily { get; init; } = "";
    public string FullName { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public int TtcIndex { get; init; } = -1;
    public int Weight { get; init; } = 400;
    public bool Italic { get; init; }
    public int WidthClass { get; init; } = 5; // 5 = Normal, 3 = Condensed
    public int UnitsPerEm { get; init; } = 2048;

    // Métricas exactas estilo LibreOffice Writer (hhea / OS/2)
    public double AscentRatio { get; init; } = 0.905;
    public double DescentRatio { get; init; } = 0.212;
    public double LineGapRatio { get; init; } = 0.033;
    public double NaturalHeightRatio => AscentRatio + DescentRatio + LineGapRatio;
    public double BaselineOffsetRatio => AscentRatio + LineGapRatio;
}

internal sealed record ResolvedFont(FontFace Face, FontMetricRecord Metrics);

/// <summary>
/// Catálogo automático de fuentes TTF/OTF/TTC instaladas en el sistema.
/// Lee directamente las tablas OpenType (head, hhea, OS/2, name) para resolver
/// familias, pesos, anchos (Condensed/Narrow) y métricas verticales idénticas a Writer.
/// Para colecciones .ttc (p.ej. Microsoft YaHei, Cambria), extrae en caché el .ttf
/// individual en %TEMP% para que PDFsharp 6.2.4 pueda incrustarlo y subsetearlo.
/// </summary>
internal static class SystemFontCatalog
{
    private static readonly Lazy<List<FontMetricRecord>> Catalog = new(ScanSystemFonts, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly ConcurrentDictionary<string, ResolvedFont> ResolveCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> ExtractedTtcCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex TrailingDigits = new(@"\d+$", RegexOptions.Compiled);

    private static readonly Dictionary<string, string[]> FontFallbacks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Liberation Sans"] = ["Arial", "Helvetica", "DejaVu Sans"],
        ["Liberation Serif"] = ["Times New Roman", "Georgia", "DejaVu Serif"],
        ["Liberation Mono"] = ["Courier New", "Consolas", "DejaVu Sans Mono"],
        ["Carlito"] = ["Calibri", "Arial"],
        ["Caladea"] = ["Cambria", "Times New Roman"],
        ["Helvetica"] = ["Arial", "Liberation Sans"],
        ["Times"] = ["Times New Roman", "Liberation Serif"],
        ["Courier"] = ["Courier New", "Liberation Mono"],
        ["Thorndale"] = ["Times New Roman"],
        ["Albany"] = ["Arial"],
        ["Cumberland"] = ["Courier New"],
        ["Symbol"] = ["Arial", "Segoe UI Symbol"],
    };

    public static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Arial";
        var s = name.Trim().Trim('\'', '"');
        if (s.Contains(';')) s = s.Split(';')[0].Trim().Trim('\'', '"');
        if (s.Contains(',')) s = s.Split(',')[0].Trim().Trim('\'', '"');
        s = TrailingDigits.Replace(s, "").Trim();
        return string.IsNullOrWhiteSpace(s) ? "Arial" : s;
    }

    /// <summary>
    /// Alto natural de línea (en em) tal como lo calcula LibreOffice Writer
    /// a partir de las tablas hhea/OS/2 de la fuente instalada.
    /// </summary>
    public static double NaturalHeightRatio(string familyName)
    {
        var norm = NormalizeName(familyName);
        var match = FindRecord(norm, bold: false, italic: false, allowSymbolMetrics: true);
        return match?.NaturalHeightRatio ?? 1.15;
    }

    public static ResolvedFont Resolve(string familyName, bool bold, bool italic)
    {
        var norm = NormalizeName(familyName);
        var key = $"{norm}|{(bold ? 1 : 0)}|{(italic ? 1 : 0)}";
        return ResolveCache.GetOrAdd(key, _ => ResolveCore(norm, bold, italic));
    }

    private static ResolvedFont ResolveCore(string norm, bool bold, bool italic)
    {
        // Symbol tiene codificación propia en TTF (PUA F000-F0FF); para dibujar texto/viñetas Unicode
        // usamos Arial (u otra fuente Unicode) conservando su Face válido para PDFsharp.
        var lookupName = norm.Equals("Symbol", StringComparison.OrdinalIgnoreCase) ? "Arial" : norm;

        var rec = FindRecord(lookupName, bold, italic, allowSymbolMetrics: false);
        if (rec == null && FontFallbacks.TryGetValue(lookupName, out var alts))
        {
            foreach (var alt in alts)
            {
                rec = FindRecord(alt, bold, italic, allowSymbolMetrics: false);
                if (rec != null) break;
            }
        }

        rec ??= FindRecord("Arial", bold, italic, allowSymbolMetrics: false)
             ?? FindRecord("Segoe UI", bold, italic, allowSymbolMetrics: false)
             ?? Catalog.Value.FirstOrDefault();

        if (rec == null)
        {
            var style = ToStyle(bold, italic);
            return new ResolvedFont(FontFace.Base14("Helvetica", style), new FontMetricRecord { Family = "Helvetica" });
        }

        var path = EnsureStandaloneFontPath(rec);
        var faceStyle = ToStyle(bold || rec.Weight >= 600, italic || rec.Italic);
        return new ResolvedFont(new FontFace(rec.Family, faceStyle, path), rec);
    }

    private static FontStyle ToStyle(bool bold, bool italic) => (bold, italic) switch
    {
        (true, true) => FontStyle.BoldItalic,
        (true, false) => FontStyle.Bold,
        (false, true) => FontStyle.Italic,
        _ => FontStyle.Regular,
    };

    private static FontMetricRecord FindRecord(string name, bool bold, bool italic, bool allowSymbolMetrics)
    {
        var all = Catalog.Value;
        if (all.Count == 0) return null;

        // 1. Coincidencia exacta por Family o FullName
        var exact = all.Where(r =>
            (allowSymbolMetrics || !r.Family.Equals("Symbol", StringComparison.OrdinalIgnoreCase)) &&
            (r.Family.Equals(name, StringComparison.OrdinalIgnoreCase) ||
             r.FullName.Equals(name, StringComparison.OrdinalIgnoreCase))).ToList();

        if (exact.Count > 0)
            return PickBestStyle(exact, bold ? 700 : 400, italic, targetWidth: 0);

        // 2. Separar sufijos de peso/ancho ("Bodoni MT Black", "Bernard MT Condensed", "Calibri Light")
        var (baseFam, parsedWeight, parsedWidth) = ParseStyleSuffixes(name);
        if (!baseFam.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            var candidates = all.Where(r =>
                r.Family.Equals(baseFam, StringComparison.OrdinalIgnoreCase) ||
                r.FullName.StartsWith(baseFam, StringComparison.OrdinalIgnoreCase)).ToList();

            if (candidates.Count > 0)
            {
                var targetWeight = parsedWeight > 0 ? (bold ? Math.Max(parsedWeight, 700) : parsedWeight) : (bold ? 700 : 400);
                return PickBestStyle(candidates, targetWeight, italic, parsedWidth);
            }
        }

        // 3. Coincidencia por prefijo de familia ("Microsoft YaHei UI" / "Microsoft YaHei")
        var prefix = all.Where(r =>
            r.Family.StartsWith(name, StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(r.Family, StringComparison.OrdinalIgnoreCase)).ToList();

        if (prefix.Count > 0)
            return PickBestStyle(prefix, bold ? 700 : 400, italic, targetWidth: 0);

        return null;
    }

    private static FontMetricRecord PickBestStyle(List<FontMetricRecord> candidates, int targetWeight, bool targetItalic, int targetWidth)
    {
        return candidates
            .OrderBy(r => r.Italic == targetItalic ? 0 : 1)
            .ThenBy(r => targetWidth > 0 ? Math.Abs(r.WidthClass - targetWidth) : (r.WidthClass == 5 ? 0 : 1))
            .ThenBy(r => Math.Abs(r.Weight - targetWeight))
            .ThenBy(r => r.TtcIndex < 0 ? 0 : 1) // preferir .ttf directo si empata con .ttc
            .First();
    }

    private static (string BaseFamily, int Weight, int Width) ParseStyleSuffixes(string name)
    {
        var actual = name.Trim();
        var weight = 0;
        var width = 0;

        while (true)
        {
            var idx = actual.LastIndexOf(' ');
            if (idx <= 0) break;

            var suffix = actual[(idx + 1)..].ToLowerInvariant();
            var w = suffix switch
            {
                "thin" or "hairline" => 100,
                "extralight" or "ultralight" => 200,
                "light" => 300,
                "regular" or "normal" => 400,
                "medium" => 500,
                "semibold" or "demibold" => 600,
                "bold" => 700,
                "extrabold" or "ultrabold" => 800,
                "black" or "heavy" => 900,
                _ => 0,
            };
            var wd = suffix switch
            {
                "ultracondensed" or "compressed" => 1,
                "extracondensed" => 2,
                "condensed" or "narrow" => 3,
                "semicondensed" => 4,
                "semiexpanded" => 6,
                "expanded" or "extended" => 7,
                "extraexpanded" => 8,
                "ultraexpanded" => 9,
                _ => 0,
            };

            if (w == 0 && wd == 0) break;
            if (w > 0 && weight == 0) weight = w;
            if (wd > 0 && width == 0) width = wd;
            actual = actual[..idx].Trim();
        }

        return (actual, weight, width);
    }

    private static List<FontMetricRecord> ScanSystemFonts()
    {
        var dirs = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.Fonts));
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
            if (Directory.Exists(local)) dirs.Add(local);
        }
        else if (OperatingSystem.IsLinux())
        {
            dirs.Add("/usr/share/fonts");
            dirs.Add("/usr/local/share/fonts");
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home)) dirs.Add(Path.Combine(home, ".local", "share", "fonts"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            dirs.Add("/System/Library/Fonts");
            dirs.Add("/Library/Fonts");
        }

        var result = new List<FontMetricRecord>(512);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in dirs)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                var ext = Path.GetExtension(file);
                if (!ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".otf", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".ttc", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!seen.Add(file)) continue;

                try
                {
                    ParseFontFile(file, result);
                }
                catch
                {
                    // Ignorar fuentes corruptas o no estándar
                }
            }
        }

        return result;
    }

    private static void ParseFontFile(string path, List<FontMetricRecord> output)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 12) return;

        var tag = Encoding.ASCII.GetString(bytes, 0, 4);
        if (tag == "ttcf")
        {
            if (bytes.Length < 12) return;
            var numFonts = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8));
            for (var i = 0; i < numFonts && 12 + (i + 1) * 4 <= bytes.Length; i++)
            {
                var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(12 + i * 4));
                var rec = ParseSfntFace(bytes, offset, path, i);
                if (rec != null) output.Add(rec);
            }
        }
        else
        {
            var rec = ParseSfntFace(bytes, 0, path, -1);
            if (rec != null) output.Add(rec);
        }
    }

    private static FontMetricRecord ParseSfntFace(byte[] data, int sfntOffset, string path, int ttcIndex)
    {
        if (sfntOffset < 0 || sfntOffset + 12 > data.Length) return null;

        var sfntVersion = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(sfntOffset));
        // TrueType (0x00010000 / 'true') o OpenType CFF ('OTTO')
        if (sfntVersion != 0x00010000 && sfntVersion != 0x74727565 && sfntVersion != 0x4F54544F)
            return null;

        var numTables = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(sfntOffset + 4));
        if (sfntOffset + 12 + numTables * 16 > data.Length) return null;

        var tables = new Dictionary<string, (int Offset, int Length)>(numTables, StringComparer.Ordinal);
        for (var i = 0; i < numTables; i++)
        {
            var pos = sfntOffset + 12 + i * 16;
            var tTag = Encoding.ASCII.GetString(data, pos, 4);
            var tOff = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 8));
            var tLen = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 12));
            if (tOff >= 0 && tLen > 0 && tOff + tLen <= data.Length)
                tables[tTag] = (tOff, tLen);
        }

        if (!tables.TryGetValue("head", out var head) || head.Length < 20) return null;
        var unitsPerEm = (int)BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(head.Offset + 18));
        if (unitsPerEm <= 0) return null;

        var (family, subfamily, fullName) = ReadNames(data, tables);
        if (string.IsNullOrWhiteSpace(family))
            family = Path.GetFileNameWithoutExtension(path);

        // Leer hhea
        int hheaAsc = 0, hheaDesc = 0, hheaGap = 0;
        if (tables.TryGetValue("hhea", out var hhea) && hhea.Length >= 10)
        {
            hheaAsc = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(hhea.Offset + 4));
            hheaDesc = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(hhea.Offset + 6));
            hheaGap = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(hhea.Offset + 8));
        }

        // Leer OS/2
        var weight = 400;
        var widthClass = 5;
        var italic = false;
        var useTypoMetrics = false;
        int typoAsc = 0, typoDesc = 0, typoGap = 0, winAsc = 0, winDesc = 0;

        if (tables.TryGetValue("OS/2", out var os2) && os2.Length >= 78)
        {
            weight = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(os2.Offset + 4));
            widthClass = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(os2.Offset + 6));
            var fsSelection = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(os2.Offset + 62));
            italic = (fsSelection & 0x01) != 0 || (fsSelection & 0x200) != 0;
            useTypoMetrics = (fsSelection & 0x80) != 0;
            typoAsc = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(os2.Offset + 68));
            typoDesc = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(os2.Offset + 70));
            typoGap = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(os2.Offset + 72));
            winAsc = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(os2.Offset + 74));
            winDesc = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(os2.Offset + 76));
        }
        else if (subfamily != null)
        {
            var sf = subfamily.ToLowerInvariant();
            if (sf.Contains("bold")) weight = 700;
            if (sf.Contains("italic") || sf.Contains("oblique")) italic = true;
        }

        // Cálculo de métricas verticales equivalente a LibreOffice Writer (VCL DirectWrite / ImplCalcLineSpacing):
        // Si OS/2 tiene USE_TYPO_METRICS (bit 7), usa sTypoAscender/Descender/LineGap;
        // si no, en Windows/DirectWrite usa usWinAscent/usWinDescent + external leading derivado de hhea/OS/2.
        double asc, desc, gap;
        if (useTypoMetrics && typoAsc > 0)
        {
            asc = typoAsc;
            desc = Math.Abs(typoDesc);
            gap = Math.Max(typoGap, 0);
        }
        else if (hheaAsc > 0)
        {
            asc = winAsc > 0 ? winAsc : hheaAsc;
            desc = winDesc > 0 ? winDesc : Math.Abs(hheaDesc);
            // En Writer (WinFontInstance / ImplCalcLineSpacing), usWinAscent+usWinDescent ya
            // incluye el interlineado interno (p.ej. Calibri: winTotal=2500 == hheaTotal=2500 -> gap=0;
            // Arial: winTotal=2288 < hheaTotal=2355 -> gap=67).
            var hheaTotal = hheaAsc + Math.Abs(hheaDesc) + Math.Max(hheaGap, 0);
            var winTotal = asc + desc;
            gap = Math.Max(hheaTotal - winTotal, 0);
        }
        else
        {
            asc = unitsPerEm * 0.905;
            desc = unitsPerEm * 0.212;
            gap = unitsPerEm * 0.033;
        }

        return new FontMetricRecord
        {
            Family = family.Trim(),
            Subfamily = (subfamily ?? "").Trim(),
            FullName = (fullName ?? family).Trim(),
            SourcePath = path,
            TtcIndex = ttcIndex,
            Weight = weight > 0 ? weight : 400,
            Italic = italic,
            WidthClass = widthClass is >= 1 and <= 9 ? widthClass : 5,
            UnitsPerEm = unitsPerEm,
            AscentRatio = asc / unitsPerEm,
            DescentRatio = desc / unitsPerEm,
            LineGapRatio = gap / unitsPerEm,
        };
    }

    private static (string Family, string Subfamily, string FullName) ReadNames(byte[] data, Dictionary<string, (int Offset, int Length)> tables)
    {
        if (!tables.TryGetValue("name", out var nameTbl) || nameTbl.Length < 6)
            return (null, null, null);

        var count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(nameTbl.Offset + 2));
        var storageOffset = nameTbl.Offset + BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(nameTbl.Offset + 4));

        string fam1 = null, sub2 = null, full4 = null, typoFam16 = null, typoSub17 = null;

        for (var i = 0; i < count; i++)
        {
            var recOff = nameTbl.Offset + 6 + i * 12;
            if (recOff + 12 > data.Length) break;

            var platformId = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(recOff));
            var encodingId = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(recOff + 2));
            var languageId = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(recOff + 4));
            var nameId = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(recOff + 6));
            var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(recOff + 8));
            var strOffset = storageOffset + BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(recOff + 10));

            if (nameId is not (1 or 2 or 4 or 16 or 17)) continue;
            if (strOffset < 0 || strOffset + length > data.Length) continue;

            // Preferir Windows Unicode English (platform=3, lang=0x0409) o Mac Roman (platform=1, lang=0)
            var isPreferred = (platformId == 3 && languageId == 0x0409) || (platformId == 0);
            var isFallback = (platformId == 3) || (platformId == 1 && languageId == 0);
            if (!isPreferred && !isFallback) continue;

            string text;
            if (platformId == 3 || platformId == 0)
                text = Encoding.BigEndianUnicode.GetString(data, strOffset, length);
            else
                text = Encoding.Latin1.GetString(data, strOffset, length);

            text = text.Trim('\0', ' ');
            if (string.IsNullOrWhiteSpace(text)) continue;

            switch (nameId)
            {
                case 1: if (fam1 == null || isPreferred) fam1 = text; break;
                case 2: if (sub2 == null || isPreferred) sub2 = text; break;
                case 4: if (full4 == null || isPreferred) full4 = text; break;
                case 16: if (typoFam16 == null || isPreferred) typoFam16 = text; break;
                case 17: if (typoSub17 == null || isPreferred) typoSub17 = text; break;
            }
        }

        // Usar nameId 1 (Win32 family) o 16 (Typographic family): si la familia 16 difiere (p.ej. "Bodoni MT" vs "Bodoni MT Black"),
        // conservamos FullName para resolver ambos nombres sin conflicto.
        return (typoFam16 ?? fam1, typoSub17 ?? sub2, full4 ?? fam1);
    }

    /// <summary>
    /// Para archivos .ttf/.otf devuelve su ruta tal cual. Para colecciones .ttc extrae la cara
    /// seleccionada a un archivo .ttf independiente en %TEMP% (cacheado) para PDFsharp 6.2.4.
    /// </summary>
    private static string EnsureStandaloneFontPath(FontMetricRecord rec)
    {
        if (rec.TtcIndex < 0) return rec.SourcePath;

        var key = $"{rec.SourcePath}#{rec.TtcIndex}";
        return ExtractedTtcCache.GetOrAdd(key, _ => ExtractTtcFaceToTemp(rec.SourcePath, rec.TtcIndex));
    }

    private static string ExtractTtcFaceToTemp(string ttcPath, int faceIndex)
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "FodtToPdf.PrawnSharp", "ttc-cache");
        Directory.CreateDirectory(cacheDir);

        var safeName = $"{Path.GetFileNameWithoutExtension(ttcPath)}_{faceIndex}.ttf";
        var outPath = Path.Combine(cacheDir, safeName);
        if (File.Exists(outPath) && new FileInfo(outPath).Length > 0)
            return outPath;

        var src = File.ReadAllBytes(ttcPath);
        var sfntOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(src.AsSpan(12 + faceIndex * 4));
        var numTables = BinaryPrimitives.ReadUInt16BigEndian(src.AsSpan(sfntOffset + 4));

        var entries = new List<(byte[] Tag, uint Checksum, int SrcOffset, int Length)>(numTables);
        for (var i = 0; i < numTables; i++)
        {
            var pos = sfntOffset + 12 + i * 16;
            var tag = src.AsSpan(pos, 4).ToArray();
            var csum = BinaryPrimitives.ReadUInt32BigEndian(src.AsSpan(pos + 4));
            var off = (int)BinaryPrimitives.ReadUInt32BigEndian(src.AsSpan(pos + 8));
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(src.AsSpan(pos + 12));
            entries.Add((tag, csum, off, len));
        }

        var headerSize = 12 + numTables * 16;
        var totalSize = headerSize;
        foreach (var e in entries)
            totalSize += (e.Length + 3) & ~3;

        var dst = new byte[totalSize];
        // Copiar cabecera SFNT (12 bytes)
        Buffer.BlockCopy(src, sfntOffset, dst, 0, 12);

        var dataCursor = headerSize;
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var dirPos = 12 + i * 16;
            Buffer.BlockCopy(e.Tag, 0, dst, dirPos, 4);
            BinaryPrimitives.WriteUInt32BigEndian(dst.AsSpan(dirPos + 4), e.Checksum);
            BinaryPrimitives.WriteUInt32BigEndian(dst.AsSpan(dirPos + 8), (uint)dataCursor);
            BinaryPrimitives.WriteUInt32BigEndian(dst.AsSpan(dirPos + 12), (uint)e.Length);

            Buffer.BlockCopy(src, e.SrcOffset, dst, dataCursor, e.Length);
            dataCursor += (e.Length + 3) & ~3;
        }

        // Escribir atómicamente vía archivo temporal para evitar colisiones entre hilos
        var tmp = outPath + "." + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(tmp, dst);
        try { File.Move(tmp, outPath, overwrite: true); }
        catch { try { File.Delete(tmp); } catch { } }

        return outPath;
    }
}
