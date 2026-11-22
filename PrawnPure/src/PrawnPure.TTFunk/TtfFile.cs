using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;

namespace PrawnPure.TTFunk;

/// <summary>
/// Pure C# TrueType / OpenType parser (port of Ruby ttfunk).
/// Reads SFNT directory (TTF &amp; TTC), head, hhea, maxp, OS/2, post, name, hmtx, cmap, kern, loca, and glyf.
/// </summary>
public sealed class TtfFile
{
    private static readonly ConcurrentDictionary<string, TtfFile> Cache = new(StringComparer.OrdinalIgnoreCase);

    public sealed record TableRecord(string Tag, uint Checksum, int Offset, int Length);

    public sealed class GlyphOutline
    {
        public int GlyphId { get; init; }
        public byte[] Raw { get; init; } = [];
        public short NumberOfContours { get; init; }
        public short XMin { get; init; }
        public short YMin { get; init; }
        public short XMax { get; init; }
        public short YMax { get; init; }
        public bool IsCompound => NumberOfContours < 0;
        public List<(int ComponentGlyphId, int OffsetInRaw)> Components { get; } = [];

        /// <summary>Rewrites component glyph IDs using old->new mapping (port of ttfunk/table/glyf/compound.rb).</summary>
        public byte[] Recode(IReadOnlyDictionary<int, int> oldToNew)
        {
            if (!IsCompound || Components.Count == 0)
                return Raw;

            var copy = (byte[])Raw.Clone();
            foreach (var (oldId, offset) in Components)
            {
                var newId = oldToNew.TryGetValue(oldId, out var mapped) ? mapped : 0;
                BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(offset, 2), (ushort)newId);
            }
            return copy;
        }
    }

    public byte[] Bytes { get; }
    public uint ScalerType { get; }
    public IReadOnlyDictionary<string, TableRecord> Tables => _tables;

    // head
    public int UnitsPerEm { get; }
    public short XMin { get; }
    public short YMin { get; }
    public short XMax { get; }
    public short YMax { get; }
    public short IndexToLocFormat { get; }

    // hhea
    public short HheaAscent { get; }
    public short HheaDescent { get; }
    public short HheaLineGap { get; }
    public int NumberOfHMetrics { get; }

    // maxp
    public int NumGlyphs { get; }

    // OS/2
    public bool HasOs2 { get; }
    public short TypoAscender { get; }
    public short TypoDescender { get; }
    public short TypoLineGap { get; }
    public short CapHeight { get; }
    public short XHeight { get; }
    public ushort FamilyClass { get; }

    // post
    public double ItalicAngle { get; }
    public short UnderlinePosition { get; }
    public short UnderlineThickness { get; }
    public bool IsFixedPitch { get; }

    // name
    public string PostScriptName { get; }

    // Computed vertical metrics in thousandths of em (exact ttfunk / TrueTypeFontInfo convention)
    public int AscentPerMille { get; }
    public int DescentPerMille { get; }
    public int LineGapPerMille { get; }
    public int UnderlinePositionPerMille { get; }
    public int UnderlineThicknessPerMille { get; }
    public int CapHeightPerMille { get; }
    public int XHeightPerMille { get; }

    public bool HasGlyf => _tables.ContainsKey("glyf") && _tables.ContainsKey("loca");
    public bool HasCff => _tables.ContainsKey("CFF ");
    public bool HasKerning => _kernPairs.Count > 0;
    public bool HasWidths => _advanceWidths.Length > 0;

    private readonly Dictionary<string, TableRecord> _tables = new(StringComparer.Ordinal);
    private readonly Dictionary<int, int> _cmap = [];
    private readonly List<(int Start, int End, int GlyphStart)> _cmapRanges = [];
    private readonly Dictionary<long, short> _kernPairs = [];
    private readonly ushort[] _advanceWidths;
    private readonly short[] _leftSideBearings;
    private readonly int[] _locaOffsets;

    public static TtfFile Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        return Cache.GetOrAdd(full, p => new TtfFile(File.ReadAllBytes(p)));
    }

    public TtfFile(byte[] bytes, int ttcFaceIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        Bytes = bytes;

        var tableDirOffset = 0;
        if (bytes.Length >= 12 &&
            bytes[0] == 't' && bytes[1] == 't' && bytes[2] == 'c' && bytes[3] == 'f')
        {
            var numFonts = (int)ReadUInt32(bytes, 8);
            if (ttcFaceIndex < 0 || ttcFaceIndex >= numFonts) ttcFaceIndex = 0;
            tableDirOffset = (int)ReadUInt32(bytes, 12 + (ttcFaceIndex * 4));
        }

        if (tableDirOffset + 12 > bytes.Length)
            throw new InvalidDataException("Invalid TrueType/OpenType font header.");

        ScalerType = ReadUInt32(bytes, tableDirOffset);
        var numTables = ReadUInt16(bytes, tableDirOffset + 4);
        var pos = tableDirOffset + 12;

        for (var i = 0; i < numTables && pos + 16 <= bytes.Length; i++, pos += 16)
        {
            var tag = Encoding.ASCII.GetString(bytes, pos, 4);
            var checksum = ReadUInt32(bytes, pos + 4);
            var offset = (int)ReadUInt32(bytes, pos + 8);
            var length = (int)ReadUInt32(bytes, pos + 12);
            if (offset >= 0 && length >= 0 && offset + length <= bytes.Length)
                _tables[tag] = new TableRecord(tag, checksum, offset, length);
        }

        // head
        if (!_tables.TryGetValue("head", out var head) || head.Length < 54)
            throw new InvalidDataException("Font missing required 'head' table.");
        UnitsPerEm = ReadUInt16(bytes, head.Offset + 18);
        if (UnitsPerEm <= 0) UnitsPerEm = 1000;
        XMin = ReadInt16(bytes, head.Offset + 36);
        YMin = ReadInt16(bytes, head.Offset + 38);
        XMax = ReadInt16(bytes, head.Offset + 40);
        YMax = ReadInt16(bytes, head.Offset + 42);
        IndexToLocFormat = ReadInt16(bytes, head.Offset + 50);

        // hhea
        if (!_tables.TryGetValue("hhea", out var hhea) || hhea.Length < 36)
            throw new InvalidDataException("Font missing required 'hhea' table.");
        HheaAscent = ReadInt16(bytes, hhea.Offset + 4);
        HheaDescent = ReadInt16(bytes, hhea.Offset + 6);
        HheaLineGap = ReadInt16(bytes, hhea.Offset + 8);
        NumberOfHMetrics = ReadUInt16(bytes, hhea.Offset + 34);

        // maxp
        if (_tables.TryGetValue("maxp", out var maxp) && maxp.Length >= 6)
            NumGlyphs = ReadUInt16(bytes, maxp.Offset + 4);
        else
            NumGlyphs = Math.Max(NumberOfHMetrics, 1);

        // OS/2
        if (_tables.TryGetValue("OS/2", out var os2) && os2.Length >= 74)
        {
            HasOs2 = true;
            var os2Version = ReadUInt16(bytes, os2.Offset);
            FamilyClass = ReadUInt16(bytes, os2.Offset + 30);
            TypoAscender = ReadInt16(bytes, os2.Offset + 68);
            TypoDescender = ReadInt16(bytes, os2.Offset + 70);
            TypoLineGap = ReadInt16(bytes, os2.Offset + 72);
            if (os2Version >= 2 && os2.Length >= 90)
            {
                XHeight = ReadInt16(bytes, os2.Offset + 86);
                CapHeight = ReadInt16(bytes, os2.Offset + 88);
            }
        }

        // post
        if (_tables.TryGetValue("post", out var post) && post.Length >= 16)
        {
            var rawAngle = ReadInt32(bytes, post.Offset + 4);
            ItalicAngle = rawAngle / 65536.0;
            UnderlinePosition = ReadInt16(bytes, post.Offset + 8);
            UnderlineThickness = ReadInt16(bytes, post.Offset + 10);
            IsFixedPitch = ReadUInt32(bytes, post.Offset + 12) != 0;
        }

        // name (PostScript name, ID 6)
        PostScriptName = ParsePostScriptName();

        // Compute normalized thousandths-of-em metrics (identical to ttfunk & TrueTypeFontInfo)
        var ascent = (HasOs2 && TypoAscender != 0) ? TypoAscender : HheaAscent;
        var descent = (HasOs2 && TypoDescender != 0) ? Math.Abs((int)TypoDescender) : Math.Abs((int)HheaDescent);
        var lineGap = (HasOs2 && (TypoAscender != 0 || TypoDescender != 0)) ? Math.Max((int)TypoLineGap, 0) : Math.Max((int)HheaLineGap, 0);

        AscentPerMille = ScaleToThousandths(ascent);
        DescentPerMille = ScaleToThousandths(descent);
        LineGapPerMille = ScaleToThousandths(lineGap);
        UnderlinePositionPerMille = ScaleToThousandths(UnderlinePosition);
        UnderlineThicknessPerMille = ScaleToThousandths(Math.Max((int)UnderlineThickness, 1));
        CapHeightPerMille = CapHeight > 0 ? ScaleToThousandths(CapHeight) : AscentPerMille;
        XHeightPerMille = XHeight > 0 ? ScaleToThousandths(XHeight) : 0;

        // hmtx
        (_advanceWidths, _leftSideBearings) = ParseHmtx();

        // cmap
        ParseCmap();

        // kern
        ParseKern();

        // loca
        _locaOffsets = ParseLoca();
    }

    public int ScaleToThousandths(int fUnits) => (int)(fUnits * 1000.0 / UnitsPerEm);

    public int[] BBoxPerMille =>
    [
        ScaleToThousandths(XMin),
        ScaleToThousandths(YMin),
        ScaleToThousandths(XMax),
        ScaleToThousandths(YMax),
    ];

    public int PdfFlags
    {
        get
        {
            var flags = 0x0004; // Symbolic (non-standard encoding)
            if (IsFixedPitch) flags |= 0x0001;
            var familyClassHi = FamilyClass >> 8;
            if (familyClassHi is 1 or 2 or 3 or 4 or 5 or 7) flags |= 0x0002; // Serif
            if (familyClassHi == 10) flags |= 0x0008; // Script
            if (Math.Abs(ItalicAngle) > 0.001) flags |= 0x0040; // Italic
            return flags;
        }
    }

    public byte[]? GetRawTable(string tag)
    {
        if (!_tables.TryGetValue(tag, out var rec)) return null;
        var data = new byte[rec.Length];
        Buffer.BlockCopy(Bytes, rec.Offset, data, 0, rec.Length);
        return data;
    }

    public ushort GetAdvanceWidth(int glyphId)
    {
        if (_advanceWidths.Length == 0) return 0;
        return glyphId >= 0 && glyphId < _advanceWidths.Length
            ? _advanceWidths[glyphId]
            : _advanceWidths[^1];
    }

    public short GetLeftSideBearing(int glyphId)
    {
        if (_leftSideBearings.Length == 0) return 0;
        return glyphId >= 0 && glyphId < _leftSideBearings.Length
            ? _leftSideBearings[glyphId]
            : (short)0;
    }

    public int GlyphFor(int codePoint)
    {
        if (_cmap.TryGetValue(codePoint, out var gid))
            return gid;

        foreach (var (start, end, glyphStart) in _cmapRanges)
        {
            if (codePoint >= start && codePoint <= end)
                return glyphStart + (codePoint - start);
        }

        return 0;
    }

    public short GetKerningFUnits(int leftGlyphId, int rightGlyphId)
    {
        if (leftGlyphId == 0 || rightGlyphId == 0) return 0;
        var key = ((long)leftGlyphId << 16) | (uint)(rightGlyphId & 0xFFFF);
        return _kernPairs.TryGetValue(key, out var val) ? val : (short)0;
    }

    public double AdvanceAt(int codePoint, double fontSize)
    {
        if (!HasWidths || codePoint == 10) return 0;
        var gid = GlyphFor(codePoint);
        var widthPerMille = ScaleToThousandths(GetAdvanceWidth(gid));
        return widthPerMille / 1000.0 * fontSize;
    }

    public double KerningAt(int leftCodePoint, int rightCodePoint, double fontSize)
    {
        if (!HasKerning) return 0;
        var kern = GetKerningFUnits(GlyphFor(leftCodePoint), GlyphFor(rightCodePoint));
        if (kern == 0) return 0;
        return (kern * 1000.0 / UnitsPerEm) / 1000.0 * fontSize;
    }

    public double KerningPerMille(int leftGlyphId, int rightGlyphId)
    {
        var kern = GetKerningFUnits(leftGlyphId, rightGlyphId);
        return kern == 0 ? 0 : kern * 1000.0 / UnitsPerEm;
    }

    public double AscentAt(double size) => AscentPerMille / 1000.0 * size;
    public double DescentAt(double size) => -DescentPerMille / 1000.0 * size;
    public double LineHeightAt(double size) => (AscentPerMille + DescentPerMille + LineGapPerMille) / 1000.0 * size;
    public double UnderlinePositionAt(double size) => UnderlinePositionPerMille / 1000.0 * size;
    public double UnderlineThicknessAt(double size) => UnderlineThicknessPerMille / 1000.0 * size;

    /// <summary>
    /// Computes exact string width matching Ruby Prawn/ttfunk and TrueTypeFontInfo:
    /// sums integer-truncated thousandths-of-em advances and adds kern adjustments.
    /// </summary>
    public double WidthOf(string text, double fontSize, bool kerning)
    {
        if (string.IsNullOrEmpty(text) || fontSize <= 0 || !HasWidths)
            return 0;

        var codePoints = EnumerateCodePoints(text);
        double totalPerMille = 0;

        for (var i = 0; i < codePoints.Count; i++)
        {
            var cp = codePoints[i];
            if (cp == 10) continue;
            var gid = GlyphFor(cp);
            totalPerMille += ScaleToThousandths(GetAdvanceWidth(gid));

            if (kerning && HasKerning && i + 1 < codePoints.Count)
            {
                var nextGid = GlyphFor(codePoints[i + 1]);
                var kern = GetKerningFUnits(gid, nextGid);
                if (kern != 0)
                    totalPerMille += kern * 1000.0 / UnitsPerEm;
            }
        }

        return totalPerMille * (fontSize / 1000.0);
    }

    /// <summary>
    /// Computes exact unrounded TrueType string width in points directly from FUnits
    /// (matching HarfBuzz / LibreOffice Writer without per-glyph integer per-mille truncation).
    /// </summary>
    public double ExactWidthOf(string text, double fontSize, bool kerning)
    {
        if (string.IsNullOrEmpty(text) || fontSize <= 0 || !HasWidths)
            return 0;

        var codePoints = EnumerateCodePoints(text);
        double totalFUnits = 0;

        for (var i = 0; i < codePoints.Count; i++)
        {
            var cp = codePoints[i];
            if (cp == 10) continue;
            var gid = GlyphFor(cp);
            totalFUnits += GetAdvanceWidth(gid);

            if (kerning && HasKerning && i + 1 < codePoints.Count)
            {
                var nextGid = GlyphFor(codePoints[i + 1]);
                totalFUnits += GetKerningFUnits(gid, nextGid);
            }
        }

        return totalFUnits * fontSize / UnitsPerEm;
    }

    public GlyphOutline? GetGlyphOutline(int glyphId)
    {
        if (!HasGlyf || glyphId < 0 || glyphId + 1 >= _locaOffsets.Length)
            return null;

        var start = _locaOffsets[glyphId];
        var end = _locaOffsets[glyphId + 1];
        if (end <= start)
            return new GlyphOutline { GlyphId = glyphId, Raw = [] };

        var glyf = _tables["glyf"];
        var absStart = glyf.Offset + start;
        var length = end - start;
        if (absStart < 0 || absStart + length > Bytes.Length)
            return new GlyphOutline { GlyphId = glyphId, Raw = [] };

        var raw = new byte[length];
        Buffer.BlockCopy(Bytes, absStart, raw, 0, length);

        if (raw.Length < 10)
            return new GlyphOutline { GlyphId = glyphId, Raw = raw };

        var contours = ReadInt16(raw, 0);
        var outline = new GlyphOutline
        {
            GlyphId = glyphId,
            Raw = raw,
            NumberOfContours = contours,
            XMin = ReadInt16(raw, 2),
            YMin = ReadInt16(raw, 4),
            XMax = ReadInt16(raw, 6),
            YMax = ReadInt16(raw, 8),
        };

        if (contours < 0)
        {
            // Parse composite glyph component references (ttfunk/table/glyf/compound.rb)
            const ushort Arg1And2AreWords = 0x0001;
            const ushort WeHaveAScale = 0x0008;
            const ushort MoreComponents = 0x0020;
            const ushort WeHaveAnXAndYScale = 0x0040;
            const ushort WeHaveATwoByTwo = 0x0080;

            var off = 10;
            while (off + 4 <= raw.Length)
            {
                var flags = ReadUInt16(raw, off);
                var compGid = ReadUInt16(raw, off + 2);
                outline.Components.Add((compGid, off + 2));

                if ((flags & MoreComponents) == 0)
                    break;

                off += 4;
                off += (flags & Arg1And2AreWords) != 0 ? 4 : 2;
                if ((flags & WeHaveATwoByTwo) != 0) off += 8;
                else if ((flags & WeHaveAnXAndYScale) != 0) off += 4;
                else if ((flags & WeHaveAScale) != 0) off += 2;
            }
        }

        return outline;
    }

    public static List<int> EnumerateCodePoints(string text)
    {
        var result = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                result.Add(char.ConvertToUtf32(text[i], text[i + 1]));
                i++;
            }
            else
            {
                result.Add(text[i]);
            }
        }
        return result;
    }

    private (ushort[] Advances, short[] Lsbs) ParseHmtx()
    {
        if (!_tables.TryGetValue("hmtx", out var hmtx) || NumGlyphs <= 0)
            return ([], []);

        var advances = new ushort[NumGlyphs];
        var lsbs = new short[NumGlyphs];
        var nMetrics = Math.Min(NumberOfHMetrics, NumGlyphs);
        var pos = hmtx.Offset;
        ushort lastAdvance = 0;

        for (var i = 0; i < nMetrics && pos + 4 <= hmtx.Offset + hmtx.Length; i++, pos += 4)
        {
            lastAdvance = ReadUInt16(Bytes, pos);
            advances[i] = lastAdvance;
            lsbs[i] = ReadInt16(Bytes, pos + 2);
        }

        for (var i = nMetrics; i < NumGlyphs; i++)
        {
            advances[i] = lastAdvance;
            if (pos + 2 <= hmtx.Offset + hmtx.Length)
            {
                lsbs[i] = ReadInt16(Bytes, pos);
                pos += 2;
            }
        }

        return (advances, lsbs);
    }

    private void ParseCmap()
    {
        if (!_tables.TryGetValue("cmap", out var cmap) || cmap.Length < 4)
            return;

        var numSubtables = ReadUInt16(Bytes, cmap.Offset + 2);
        int bestOffset = -1;
        int bestPriority = -1;

        for (var i = 0; i < numSubtables; i++)
        {
            var recOff = cmap.Offset + 4 + (i * 8);
            if (recOff + 8 > cmap.Offset + cmap.Length) break;

            var platformId = ReadUInt16(Bytes, recOff);
            var encodingId = ReadUInt16(Bytes, recOff + 2);
            var subOffset = cmap.Offset + (int)ReadUInt32(Bytes, recOff + 4);
            if (subOffset + 2 > Bytes.Length) continue;

            var format = ReadUInt16(Bytes, subOffset);
            var priority = (platformId, encodingId, format) switch
            {
                (3, 10, 12) => 100,
                (0, 4, 12) => 95,
                (3, 1, 4) => 90,
                (0, 3, 4) => 85,
                (0, _, 4) => 80,
                (3, 0, 4) => 70, // Windows Symbol
                (1, 0, 0) => 50,
                _ => -1,
            };

            if (priority > bestPriority)
            {
                bestPriority = priority;
                bestOffset = subOffset;
            }
        }

        if (bestOffset < 0) return;
        var chosenFormat = ReadUInt16(Bytes, bestOffset);

        if (chosenFormat == 12)
            ParseCmapFormat12(bestOffset);
        else if (chosenFormat == 4)
            ParseCmapFormat4(bestOffset);
        else if (chosenFormat == 0)
            ParseCmapFormat0(bestOffset);
    }

    private void ParseCmapFormat0(int offset)
    {
        if (offset + 262 > Bytes.Length) return;
        for (var c = 0; c < 256; c++)
        {
            var g = Bytes[offset + 6 + c];
            if (g != 0) _cmap[c] = g;
        }
    }

    private void ParseCmapFormat4(int offset)
    {
        if (offset + 14 > Bytes.Length) return;
        var segCount = ReadUInt16(Bytes, offset + 6) / 2;
        var endCodesOff = offset + 14;
        var startCodesOff = endCodesOff + (segCount * 2) + 2;
        var idDeltaOff = startCodesOff + (segCount * 2);
        var idRangeOff = idDeltaOff + (segCount * 2);

        if (idRangeOff + (segCount * 2) > Bytes.Length) return;

        for (var s = 0; s < segCount; s++)
        {
            var endCode = ReadUInt16(Bytes, endCodesOff + (s * 2));
            var startCode = ReadUInt16(Bytes, startCodesOff + (s * 2));
            var idDelta = ReadInt16(Bytes, idDeltaOff + (s * 2));
            var rangeOffPos = idRangeOff + (s * 2);
            var idRangeOffset = ReadUInt16(Bytes, rangeOffPos);

            if (startCode == 0xFFFF && endCode == 0xFFFF) break;

            for (var c = startCode; c <= endCode; c++)
            {
                int gid;
                if (idRangeOffset == 0)
                {
                    gid = (c + idDelta) & 0xFFFF;
                }
                else
                {
                    var glyphIdxOff = rangeOffPos + idRangeOffset + ((c - startCode) * 2);
                    if (glyphIdxOff + 2 > Bytes.Length) continue;
                    var rawGid = ReadUInt16(Bytes, glyphIdxOff);
                    gid = rawGid == 0 ? 0 : (rawGid + idDelta) & 0xFFFF;
                }

                if (gid != 0)
                    _cmap[c] = gid;
            }
        }
    }

    private void ParseCmapFormat12(int offset)
    {
        if (offset + 16 > Bytes.Length) return;
        var numGroups = (int)ReadUInt32(Bytes, offset + 12);
        var pos = offset + 16;

        for (var i = 0; i < numGroups && pos + 12 <= Bytes.Length; i++, pos += 12)
        {
            var startChar = (int)ReadUInt32(Bytes, pos);
            var endChar = (int)ReadUInt32(Bytes, pos + 4);
            var startGlyph = (int)ReadUInt32(Bytes, pos + 8);
            _cmapRanges.Add((startChar, endChar, startGlyph));
        }
    }

    private void ParseKern()
    {
        if (!_tables.TryGetValue("kern", out var kern) || kern.Length < 4)
            return;

        var version = ReadUInt16(Bytes, kern.Offset);
        int nTables;
        int pos;
        if (version == 0)
        {
            nTables = ReadUInt16(Bytes, kern.Offset + 2);
            pos = kern.Offset + 4;
        }
        else if (kern.Length >= 8)
        {
            // Apple kern version 1.0
            nTables = (int)ReadUInt32(Bytes, kern.Offset + 4);
            pos = kern.Offset + 8;
        }
        else return;

        var tableEnd = kern.Offset + kern.Length;
        for (var t = 0; t < nTables && pos + 6 <= tableEnd; t++)
        {
            int subLen, format;
            bool horizontal;
            int headerSize;

            if (version == 0)
            {
                subLen = ReadUInt16(Bytes, pos + 2);
                var coverage = ReadUInt16(Bytes, pos + 4);
                format = coverage >> 8;
                horizontal = (coverage & 0x01) != 0 && (coverage & 0x02) == 0 && (coverage & 0x04) == 0;
                headerSize = 6;
            }
            else
            {
                if (pos + 8 > tableEnd) break;
                subLen = (int)ReadUInt32(Bytes, pos);
                var coverage = ReadUInt16(Bytes, pos + 4);
                format = coverage & 0xFF;
                horizontal = (coverage & 0x8000) == 0 && (coverage & 0x4000) == 0;
                headerSize = 8;
            }

            // Note: in large Windows kern tables (e.g. Arial/Times), uint16 subLen can wrap modulo 65536,
            // so bound pair reading by tableEnd rather than pos + subLen (matching ttfunk & TrueTypeFontInfo).
            if (format == 0 && horizontal && pos + headerSize + 8 <= tableEnd)
            {
                var nPairs = ReadUInt16(Bytes, pos + headerSize);
                var p = pos + headerSize + 8;
                for (var i = 0; i < nPairs && p + 6 <= tableEnd; i++, p += 6)
                {
                    var left = ReadUInt16(Bytes, p);
                    var right = ReadUInt16(Bytes, p + 2);
                    var value = ReadInt16(Bytes, p + 4);
                    if (value != 0)
                    {
                        var key = ((long)left << 16) | right;
                        _kernPairs[key] = value;
                    }
                }
            }

            if (subLen <= 0) break;
            pos += subLen;
        }
    }

    private int[] ParseLoca()
    {
        if (!_tables.TryGetValue("loca", out var loca) || NumGlyphs <= 0)
            return [];

        var offsets = new int[NumGlyphs + 1];
        if (IndexToLocFormat == 0)
        {
            for (var i = 0; i <= NumGlyphs && (i * 2) + 2 <= loca.Length; i++)
                offsets[i] = ReadUInt16(Bytes, loca.Offset + (i * 2)) * 2;
        }
        else
        {
            for (var i = 0; i <= NumGlyphs && (i * 4) + 4 <= loca.Length; i++)
                offsets[i] = (int)ReadUInt32(Bytes, loca.Offset + (i * 4));
        }
        return offsets;
    }

    private string ParsePostScriptName()
    {
        if (!_tables.TryGetValue("name", out var name) || name.Length < 6)
            return "PrawnPureFont";

        var count = ReadUInt16(Bytes, name.Offset + 2);
        var stringOffset = name.Offset + ReadUInt16(Bytes, name.Offset + 4);
        string? macFallback = null;

        for (var i = 0; i < count; i++)
        {
            var rec = name.Offset + 6 + (i * 12);
            if (rec + 12 > name.Offset + name.Length) break;

            var platformId = ReadUInt16(Bytes, rec);
            var nameId = ReadUInt16(Bytes, rec + 6);
            if (nameId != 6) continue; // PostScript name

            var len = ReadUInt16(Bytes, rec + 8);
            var strOff = stringOffset + ReadUInt16(Bytes, rec + 10);
            if (strOff + len > Bytes.Length) continue;

            if (platformId == 3)
            {
                var s = SanitizePostScriptName(Encoding.BigEndianUnicode.GetString(Bytes, strOff, len));
                if (!string.IsNullOrEmpty(s)) return s;
            }
            else if (platformId == 1 && macFallback is null)
            {
                macFallback = SanitizePostScriptName(Encoding.Latin1.GetString(Bytes, strOff, len));
            }
        }

        return !string.IsNullOrEmpty(macFallback) ? macFallback : "PrawnPureFont";
    }

    private static string SanitizePostScriptName(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch > 32 && ch < 127 && ch is not ('/' or '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '%' or '#'))
                sb.Append(ch);
        }
        return sb.Length > 26 ? sb.ToString(0, 26) : sb.ToString();
    }

    internal static ushort ReadUInt16(byte[] b, int o) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o, 2));
    internal static short ReadInt16(byte[] b, int o) => BinaryPrimitives.ReadInt16BigEndian(b.AsSpan(o, 2));
    internal static uint ReadUInt32(byte[] b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o, 4));
    internal static int ReadInt32(byte[] b, int o) => BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(o, 4));
}
