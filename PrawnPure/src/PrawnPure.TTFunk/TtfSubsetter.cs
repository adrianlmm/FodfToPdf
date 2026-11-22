using System.Buffers.Binary;
using System.Text;

namespace PrawnPure.TTFunk;

/// <summary>
/// TrueType Unicode subsetter &amp; binary encoder (port of ttfunk/subset/unicode.rb + ttf_encoder.rb).
/// Assigns sequential subset GIDs on the fly (0=.notdef, 1..K=used glyphs, K+1..M=compound components)
/// and emits a minimal, spec-valid TrueType binary with accurate table checksums and head.checkSumAdjustment.
/// </summary>
public sealed class TtfSubsetter
{
    private static readonly string[] OptimalTableOrder =
    [
        "head", "hhea", "maxp", "OS/2", "hmtx", "LTSH", "VDMX",
        "hdmx", "cmap", "fpgm", "prep", "cvt ", "loca", "glyf",
        "kern", "name", "post", "gasp", "PCLT",
    ];

    public TtfFile Original { get; }

    private readonly Dictionary<int, int> _oldToNew = new() { [0] = 0 };
    private readonly List<int> _newToOld = [0]; // index = newGid, value = oldGid
    private readonly Dictionary<int, int> _newGidToUnicode = [];
    private readonly Dictionary<int, int> _unicodeToNewGid = [];

    public TtfSubsetter(TtfFile original)
    {
        Original = original;
        // Always include space (0x20) like ttfunk/subset/unicode.rb
        UseCodePoint(0x20);
    }

    public IReadOnlyDictionary<int, int> NewGidToUnicode => _newGidToUnicode;
    public IReadOnlyList<int> NewToOldGlyph => _newToOld;

    /// <summary>
    /// Registers a Unicode code point in the subset and returns its deterministic new GID.
    /// </summary>
    public int UseCodePoint(int codePoint)
    {
        if (_unicodeToNewGid.TryGetValue(codePoint, out var existingNewGid))
            return existingNewGid;

        var oldGid = Original.GlyphFor(codePoint);
        if (!_oldToNew.TryGetValue(oldGid, out var newGid))
        {
            newGid = _newToOld.Count;
            _oldToNew[oldGid] = newGid;
            _newToOld.Add(oldGid);
        }

        _unicodeToNewGid[codePoint] = newGid;
        if (newGid != 0 && !_newGidToUnicode.ContainsKey(newGid))
            _newGidToUnicode[newGid] = codePoint;

        return newGid;
    }

    /// <summary>
    /// Pulls in all recursively-referenced compound glyph components and encodes the subset TTF binary.
    /// </summary>
    public byte[] Encode()
    {
        if (!Original.HasGlyf)
            return Original.Bytes;

        // 1. Recursive compound glyph closure (port of ttfunk/subset/base.rb collect_glyphs)
        var outlines = new Dictionary<int, TtfFile.GlyphOutline>();
        var queue = new Queue<int>(_newToOld);
        while (queue.Count > 0)
        {
            var oldGid = queue.Dequeue();
            if (outlines.ContainsKey(oldGid)) continue;

            var outline = Original.GetGlyphOutline(oldGid) ?? new TtfFile.GlyphOutline { GlyphId = oldGid };
            outlines[oldGid] = outline;

            if (outline.IsCompound)
            {
                foreach (var (compGid, _) in outline.Components)
                {
                    if (!_oldToNew.ContainsKey(compGid))
                    {
                        var compNew = _newToOld.Count;
                        _oldToNew[compGid] = compNew;
                        _newToOld.Add(compGid);
                    }
                    if (!outlines.ContainsKey(compGid))
                        queue.Enqueue(compGid);
                }
            }
        }

        var numNewGlyphs = _newToOld.Count;

        // 2. Encode glyf + loca (with compound component GIDs remapped)
        var (glyfTable, locaOffsets) = BuildGlyf(outlines, numNewGlyphs);
        var useLongLoca = locaOffsets[^1] > 0x1FFFE || locaOffsets.Any(o => (o & 1) != 0);
        var locaTable = BuildLoca(locaOffsets, useLongLoca);

        // 3. Encode hmtx, hhea, maxp, head, cmap, post, name, OS/2, hinting tables
        var (hmtxTable, maxAdvanceWidth) = BuildHmtx(numNewGlyphs);
        var hheaTable = BuildHhea(numNewGlyphs, maxAdvanceWidth);
        var maxpTable = BuildMaxp(numNewGlyphs);
        var headTable = BuildHead(useLongLoca);
        var cmapTable = BuildCmap();
        var postTable = BuildPost();

        var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["head"] = headTable,
            ["hhea"] = hheaTable,
            ["maxp"] = maxpTable,
            ["hmtx"] = hmtxTable,
            ["cmap"] = cmapTable,
            ["loca"] = locaTable,
            ["glyf"] = glyfTable,
            ["post"] = postTable,
        };

        CopyOptionalTable(tables, "OS/2");
        CopyOptionalTable(tables, "name");
        CopyOptionalTable(tables, "cvt ");
        CopyOptionalTable(tables, "fpgm");
        CopyOptionalTable(tables, "prep");
        CopyOptionalTable(tables, "gasp");

        // 4. Assemble SFNT binary with sorted directory and optimal table order (ttfunk/ttf_encoder.rb)
        return AssembleSfnt(Original.ScalerType, tables);
    }

    private void CopyOptionalTable(Dictionary<string, byte[]> tables, string tag)
    {
        var raw = Original.GetRawTable(tag);
        if (raw is { Length: > 0 })
            tables[tag] = raw;
    }

    private (byte[] Glyf, int[] Offsets) BuildGlyf(Dictionary<int, TtfFile.GlyphOutline> outlines, int numNewGlyphs)
    {
        using var ms = new MemoryStream();
        var offsets = new int[numNewGlyphs + 1];

        for (var newGid = 0; newGid < numNewGlyphs; newGid++)
        {
            offsets[newGid] = (int)ms.Position;
            var oldGid = _newToOld[newGid];
            if (outlines.TryGetValue(oldGid, out var outline) && outline.Raw.Length > 0)
            {
                var recoded = outline.Recode(_oldToNew);
                ms.Write(recoded, 0, recoded.Length);
                // 4-byte align each glyph so both short and long loca are valid
                var pad = (4 - ((int)ms.Position & 3)) & 3;
                for (var p = 0; p < pad; p++) ms.WriteByte(0);
            }
        }

        offsets[numNewGlyphs] = (int)ms.Position;
        // Empty glyf table is invalid in some viewers; ensure at least 4 zero bytes
        if (ms.Length == 0)
            ms.Write([0, 0, 0, 0]);

        return (ms.ToArray(), offsets);
    }

    private static byte[] BuildLoca(int[] offsets, bool useLongFormat)
    {
        var bytes = new byte[offsets.Length * (useLongFormat ? 4 : 2)];
        if (useLongFormat)
        {
            for (var i = 0; i < offsets.Length; i++)
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * 4, 4), (uint)offsets[i]);
        }
        else
        {
            for (var i = 0; i < offsets.Length; i++)
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2, 2), (ushort)(offsets[i] >> 1));
        }
        return bytes;
    }

    private (byte[] Hmtx, ushort MaxAdvance) BuildHmtx(int numNewGlyphs)
    {
        var bytes = new byte[numNewGlyphs * 4];
        ushort maxAdv = 0;
        for (var newGid = 0; newGid < numNewGlyphs; newGid++)
        {
            var oldGid = _newToOld[newGid];
            var adv = Original.GetAdvanceWidth(oldGid);
            var lsb = Original.GetLeftSideBearing(oldGid);
            if (adv > maxAdv) maxAdv = adv;
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(newGid * 4, 2), adv);
            BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan((newGid * 4) + 2, 2), lsb);
        }
        return (bytes, maxAdv);
    }

    private byte[] BuildHhea(int numNewGlyphs, ushort maxAdvanceWidth)
    {
        var raw = Original.GetRawTable("hhea") ?? new byte[36];
        var copy = (byte[])raw.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(10, 2), maxAdvanceWidth);
        BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(34, 2), (ushort)numNewGlyphs);
        return copy;
    }

    private byte[] BuildMaxp(int numNewGlyphs)
    {
        var raw = Original.GetRawTable("maxp") ?? new byte[6];
        var copy = (byte[])raw.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(4, 2), (ushort)numNewGlyphs);
        return copy;
    }

    private byte[] BuildHead(bool useLongLoca)
    {
        var raw = Original.GetRawTable("head") ?? new byte[54];
        var copy = (byte[])raw.Clone();
        // Zero checkSumAdjustment (filled in after assembling all tables)
        BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(8, 4), 0);
        BinaryPrimitives.WriteInt16BigEndian(copy.AsSpan(50, 2), (short)(useLongLoca ? 1 : 0));
        return copy;
    }

    private byte[] BuildPost()
    {
        // Format 3.0 post table (32 bytes, no glyph names — standard for embedded PDF subsets)
        var post = new byte[32];
        var orig = Original.GetRawTable("post");
        if (orig is { Length: >= 32 })
        {
            Buffer.BlockCopy(orig, 0, post, 0, 32);
        }
        // Version 3.0 = 0x00030000
        BinaryPrimitives.WriteUInt32BigEndian(post.AsSpan(0, 4), 0x00030000);
        return post;
    }

    /// <summary>Builds a minimal Format 4 (Windows Unicode BMP) cmap mapping subset code points to new GIDs.</summary>
    private byte[] BuildCmap()
    {
        var bmp = _unicodeToNewGid
            .Where(kv => kv.Key is >= 0 and <= 0xFFFF && kv.Value != 0)
            .OrderBy(kv => kv.Key)
            .ToList();

        // Build contiguous segments where both codePoint and newGid increase by 1
        var segments = new List<(ushort Start, ushort End, short Delta)>();
        var idx = 0;
        while (idx < bmp.Count)
        {
            var startCode = bmp[idx].Key;
            var startGid = bmp[idx].Value;
            var endCode = startCode;
            var prevGid = startGid;
            idx++;

            while (idx < bmp.Count && bmp[idx].Key == endCode + 1 && bmp[idx].Value == prevGid + 1)
            {
                endCode = bmp[idx].Key;
                prevGid = bmp[idx].Value;
                idx++;
            }

            var delta = unchecked((short)(startGid - startCode));
            segments.Add(((ushort)startCode, (ushort)endCode, delta));
        }

        // Mandatory terminating segment 0xFFFF
        segments.Add((0xFFFF, 0xFFFF, 1));

        var segCount = segments.Count;
        var segCountX2 = segCount * 2;
        var searchRange = 2 * HighestPowerOfTwo(segCount);
        var entrySelector = (int)Math.Log2(searchRange / 2);
        var rangeShift = segCountX2 - searchRange;

        var format4Len = 16 + (segCount * 8);
        var totalLen = 12 + format4Len; // cmap header (4) + 1 record (8) + format4

        var bytes = new byte[totalLen];
        // cmap header: version=0, numTables=1
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(0, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2, 2), 1);
        // Encoding record: platformID=3 (Windows), encodingID=1 (Unicode BMP), offset=12
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4, 2), 3);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6, 2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 12);

        // Format 4 subtable at offset 12
        var o = 12;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(o, 2), 4);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(o + 2, 2), (ushort)format4Len);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(o + 4, 2), 0); // language
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(o + 6, 2), (ushort)segCountX2);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(o + 8, 2), (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(o + 10, 2), (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(o + 12, 2), (ushort)rangeShift);

        var pEnd = o + 14;
        var pStart = pEnd + segCountX2 + 2; // +2 for reservedPad=0
        var pDelta = pStart + segCountX2;
        var pRange = pDelta + segCountX2;

        for (var i = 0; i < segCount; i++)
        {
            var (st, en, dl) = segments[i];
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(pEnd + (i * 2), 2), en);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(pStart + (i * 2), 2), st);
            BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(pDelta + (i * 2), 2), dl);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(pRange + (i * 2), 2), 0);
        }

        return bytes;
    }

    private static byte[] AssembleSfnt(uint scalerType, Dictionary<string, byte[]> tables)
    {
        var numTables = tables.Count;
        var searchRange = HighestPowerOfTwo(numTables) * 16;
        var entrySelector = (int)Math.Log2(searchRange / 16);
        var rangeShift = Math.Max((numTables * 16) - searchRange, 0);

        var sortedTags = tables.Keys.OrderBy(t => t, StringComparer.Ordinal).ToList();
        var dataOrder = OptimalTableOrder.Where(tables.ContainsKey)
            .Concat(sortedTags.Where(t => !OptimalTableOrder.Contains(t)))
            .ToList();

        var headerSize = 12 + (numTables * 16);
        var offsets = new Dictionary<string, int>(StringComparer.Ordinal);
        var curOffset = headerSize;
        foreach (var tag in dataOrder)
        {
            offsets[tag] = curOffset;
            var len = tables[tag].Length;
            curOffset += len + ((4 - (len & 3)) & 3);
        }

        var font = new byte[curOffset];
        BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(0, 4), scalerType == 0 ? 0x00010000u : scalerType);
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(4, 2), (ushort)numTables);
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(6, 2), (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(8, 2), (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(10, 2), (ushort)rangeShift);

        var dirPos = 12;
        foreach (var tag in sortedTags)
        {
            var data = tables[tag];
            var tagBytes = Encoding.ASCII.GetBytes(tag.PadRight(4)[..4]);
            Buffer.BlockCopy(tagBytes, 0, font, dirPos, 4);
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(dirPos + 4, 4), ComputeTableChecksum(data));
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(dirPos + 8, 4), (uint)offsets[tag]);
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(dirPos + 12, 4), (uint)data.Length);
            dirPos += 16;
        }

        foreach (var tag in dataOrder)
        {
            var data = tables[tag];
            Buffer.BlockCopy(data, 0, font, offsets[tag], data.Length);
        }

        // Write head.checkSumAdjustment = 0xB1B0AFBA - fullFontChecksum
        if (offsets.TryGetValue("head", out var headOff) && headOff + 12 <= font.Length)
        {
            var totalSum = ComputeTableChecksum(font);
            var adjustment = unchecked(0xB1B0AFBAu - totalSum);
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(headOff + 8, 4), adjustment);
        }

        return font;
    }

    private static uint ComputeTableChecksum(byte[] data)
    {
        uint sum = 0;
        var fullWords = data.Length / 4;
        for (var i = 0; i < fullWords; i++)
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i * 4, 4)));

        var rem = data.Length & 3;
        if (rem > 0)
        {
            uint tail = 0;
            for (var i = 0; i < rem; i++)
                tail |= (uint)data[(fullWords * 4) + i] << (24 - (i * 8));
            sum = unchecked(sum + tail);
        }
        return sum;
    }

    private static int HighestPowerOfTwo(int n)
    {
        var p = 1;
        while ((p << 1) <= n) p <<= 1;
        return p;
    }
}
