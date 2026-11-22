using System.Text;

namespace PrawnPure.TTFunk;

/// <summary>
/// Generates a PDF /ToUnicode CMap stream (port of prawn/fonts/to_unicode_cmap.rb)
/// mapping 2-byte Identity-H subset glyph IDs back to UTF-16BE Unicode code points.
/// </summary>
public static class ToUnicodeCMap
{
    public static string Generate(IReadOnlyDictionary<int, int> gidToUnicode)
    {
        var sb = new StringBuilder(1024);
        sb.AppendLine("/CIDInit /ProcSet findresource begin");
        sb.AppendLine("12 dict begin");
        sb.AppendLine("begincmap");
        sb.AppendLine("/CIDSystemInfo 3 dict dup begin");
        sb.AppendLine("  /Registry (Adobe) def");
        sb.AppendLine("  /Ordering (UCS) def");
        sb.AppendLine("  /Supplement 0 def");
        sb.AppendLine("end def");
        sb.AppendLine("/CMapName /Adobe-Identity-UCS def");
        sb.AppendLine("/CMapType 2 def");
        sb.AppendLine("1 begincodespacerange");
        sb.AppendLine("<0000><FFFF>");
        sb.AppendLine("endcodespacerange");

        var entries = gidToUnicode
            .Where(kv => kv.Key > 0 && kv.Key <= 0xFFFF && kv.Value >= 0 && (kv.Value < 0xD800 || kv.Value > 0xDFFF))
            .OrderBy(kv => kv.Key)
            .ToList();

        // Partition into contiguous runs where both GID and Unicode code point increment by 1 (bfrange),
        // and isolated mappings (bfchar), emitted in chunks of at most 100 entries per PDF spec.
        var ranges = new List<(int StartGid, int EndGid, int StartUnicode)>();
        var singles = new List<(int Gid, int Unicode)>();

        var i = 0;
        while (i < entries.Count)
        {
            var startGid = entries[i].Key;
            var startUni = entries[i].Value;
            var endGid = startGid;
            var prevUni = startUni;
            var j = i + 1;

            // Keep bfrange within the same high byte and within BMP so target doesn't cross surrogate boundaries
            while (j < entries.Count &&
                   entries[j].Key == endGid + 1 &&
                   entries[j].Value == prevUni + 1 &&
                   (entries[j].Key & 0xFF00) == (startGid & 0xFF00) &&
                   entries[j].Value <= 0xFFFF &&
                   startUni <= 0xFFFF)
            {
                endGid = entries[j].Key;
                prevUni = entries[j].Value;
                j++;
            }

            if (endGid > startGid)
                ranges.Add((startGid, endGid, startUni));
            else
                singles.Add((startGid, startUni));

            i = j;
        }

        for (var r = 0; r < ranges.Count; r += 100)
        {
            var count = Math.Min(100, ranges.Count - r);
            sb.Append(count).AppendLine(" beginbfrange");
            for (var k = 0; k < count; k++)
            {
                var (sGid, eGid, sUni) = ranges[r + k];
                sb.Append('<').Append(sGid.ToString("X4")).Append("><")
                  .Append(eGid.ToString("X4")).Append("><")
                  .Append(EncodeUtf16BeHex(sUni)).AppendLine(">");
            }
            sb.AppendLine("endbfrange");
        }

        for (var s = 0; s < singles.Count; s += 100)
        {
            var count = Math.Min(100, singles.Count - s);
            sb.Append(count).AppendLine(" beginbfchar");
            for (var k = 0; k < count; k++)
            {
                var (gid, uni) = singles[s + k];
                sb.Append('<').Append(gid.ToString("X4")).Append("><")
                  .Append(EncodeUtf16BeHex(uni)).AppendLine(">");
            }
            sb.AppendLine("endbfchar");
        }

        sb.AppendLine("endcmap");
        sb.AppendLine("CMapName currentdict /CMap defineresource pop");
        sb.AppendLine("end");
        sb.AppendLine("end");
        return sb.ToString();
    }

    private static string EncodeUtf16BeHex(int codePoint)
    {
        var str = char.ConvertFromUtf32(codePoint);
        var bytes = Encoding.BigEndianUnicode.GetBytes(str);
        return Convert.ToHexString(bytes);
    }
}
