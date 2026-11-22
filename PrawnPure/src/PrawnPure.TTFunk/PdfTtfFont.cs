using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PrawnPure.PdfCore.Document;
using PrawnPure.PdfCore.Objects;

namespace PrawnPure.TTFunk;

/// <summary>
/// Manages a subsetted TrueType font embedded as a PDF Type0 / CIDFontType2 composite font
/// with /Encoding /Identity-H, /CIDToGIDMap /Identity, and /ToUnicode CMap
/// (port of prawn/fonts/ttf.rb).
/// </summary>
public sealed class PdfTtfFont
{
    public string ResourceName { get; }
    public TtfFile File { get; }
    public TtfSubsetter Subsetter { get; }
    public PdfIndirectObject FontObject { get; }

    private bool _embedded;

    public PdfTtfFont(PdfCoreDocument doc, string resourceName, TtfFile ttfFile)
    {
        ResourceName = resourceName.StartsWith('/') ? resourceName : "/" + resourceName;
        File = ttfFile;
        Subsetter = new TtfSubsetter(ttfFile);

        FontObject = doc.Store.Add(new PdfDictionary
        {
            ["Type"] = new PdfName("Font"),
            ["Subtype"] = new PdfName("Type0"),
        });

        doc.OnBeforeRender(_ => EmbedInto(doc));
    }

    /// <summary>
    /// Encodes a Unicode string into a PDF TJ operator argument string (e.g. <c>[&lt;00010002&gt; 35 &lt;0003&gt;] TJ</c>
    /// or <c>&lt;00010002&gt; Tj</c>), recording every glyph in the subset and applying TTF kern pairs.
    /// </summary>
    private readonly Dictionary<int, int> _rawGidToUnicode = [];

    private int MapCodePointToGid(int cp)
    {
        if (File.HasGlyf)
            return Subsetter.UseCodePoint(cp);

        var oldGid = File.GlyphFor(cp);
        if (oldGid != 0 && !_rawGidToUnicode.ContainsKey(oldGid))
            _rawGidToUnicode[oldGid] = cp;
        return oldGid;
    }

    public string EncodePdfShowOperator(string text, bool kerning, double wordSpacingPerMille = 0)
    {
        var codePoints = TtfFile.EnumerateCodePoints(text);
        if (codePoints.Count == 0)
            return string.Empty;

        var applyKern = kerning && File.HasKerning;
        var hasWordSpacing = Math.Abs(wordSpacingPerMille) > 0.0001;
        var sb = new StringBuilder(codePoints.Count * 5 + 8);
        var hasAdjustments = false;

        if (applyKern || hasWordSpacing)
        {
            for (var i = 0; i < codePoints.Count; i++)
            {
                if (hasWordSpacing && codePoints[i] == 0x20)
                {
                    hasAdjustments = true;
                    break;
                }
                if (applyKern && i + 1 < codePoints.Count &&
                    File.GetKerningFUnits(File.GlyphFor(codePoints[i]), File.GlyphFor(codePoints[i + 1])) != 0)
                {
                    hasAdjustments = true;
                    break;
                }
            }
        }

        if (!hasAdjustments)
        {
            sb.Append('<');
            foreach (var cp in codePoints)
            {
                var gid = MapCodePointToGid(cp);
                sb.Append(gid.ToString("X4", CultureInfo.InvariantCulture));
            }
            sb.Append("> Tj");
            return sb.ToString();
        }

        sb.Append("[<");
        for (var i = 0; i < codePoints.Count; i++)
        {
            var cp = codePoints[i];
            var gid = MapCodePointToGid(cp);
            sb.Append(gid.ToString("X4", CultureInfo.InvariantCulture));

            double adjPerMille = 0;
            if (hasWordSpacing && cp == 0x20)
                adjPerMille += wordSpacingPerMille;

            if (applyKern && i + 1 < codePoints.Count)
                adjPerMille += File.KerningPerMille(File.GlyphFor(cp), File.GlyphFor(codePoints[i + 1]));

            if (Math.Abs(adjPerMille) > 0.0001)
            {
                // PDF TJ subtracts the number (positive moves left, negative moves right)
                sb.Append("> ")
                  .Append(PdfObject.FormatReal(-adjPerMille))
                  .Append(" <");
            }
        }
        sb.Append(">] TJ");
        return sb.ToString();
    }

    private void EmbedInto(PdfCoreDocument doc)
    {
        if (_embedded) return;
        _embedded = true;

        var isTrueType = File.HasGlyf;
        var fontBytes = isTrueType ? Subsetter.Encode() : File.Bytes;

        var subsetTag = BuildSubsetTag(Subsetter.NewToOldGlyph);
        var baseName = $"{subsetTag}+{File.PostScriptName}";
        if (baseName.Length > 33) baseName = baseName[..33];

        // 1. FontFile2 (or FontFile3 for CFF/OTF)
        var fontFileDict = new PdfDictionary();
        if (isTrueType)
        {
            fontFileDict["Length1"] = new PdfInt(fontBytes.Length);
        }
        else
        {
            doc.MinVersion(1.6);
            fontFileDict["Subtype"] = new PdfName("OpenType");
        }
        var fontFileObj = doc.Store.Add(fontFileDict);
        fontFileObj.SetStream(fontBytes, compress: doc.Compress);

        // 2. FontDescriptor
        var bbox = File.BBoxPerMille;
        var descriptorDict = new PdfDictionary
        {
            ["Type"] = new PdfName("FontDescriptor"),
            ["FontName"] = new PdfName(baseName),
            ["Flags"] = new PdfInt(File.PdfFlags),
            ["FontBBox"] = PdfArray.OfInts(bbox[0], bbox[1], bbox[2], bbox[3]),
            ["ItalicAngle"] = new PdfReal(File.ItalicAngle),
            ["Ascent"] = new PdfInt(File.AscentPerMille),
            ["Descent"] = new PdfInt(-File.DescentPerMille),
            ["CapHeight"] = new PdfInt(File.CapHeightPerMille),
            ["StemV"] = new PdfInt(80),
        };
        if (File.XHeightPerMille > 0)
            descriptorDict["XHeight"] = new PdfInt(File.XHeightPerMille);

        if (isTrueType)
            descriptorDict["FontFile2"] = fontFileObj.Reference;
        else
            descriptorDict["FontFile3"] = fontFileObj.Reference;

        var descriptorObj = doc.Store.Add(descriptorDict);

        // 3. Widths array /W [0 [w0 w1 w2 ...]] matching TrueTypeFontInfo's exact thousandths-of-em truncation
        var widthsList = new PdfArray();
        if (isTrueType)
        {
            foreach (var oldGid in Subsetter.NewToOldGlyph)
            {
                widthsList.Add(new PdfInt(File.ScaleToThousandths(File.GetAdvanceWidth(oldGid))));
            }
        }
        else
        {
            for (var g = 0; g < File.NumGlyphs; g++)
            {
                widthsList.Add(new PdfInt(File.ScaleToThousandths(File.GetAdvanceWidth(g))));
            }
        }

        // 4. Descendant CIDFontType2 (or CIDFontType0)
        var cidFontDict = new PdfDictionary
        {
            ["Type"] = new PdfName("Font"),
            ["Subtype"] = new PdfName(isTrueType ? "CIDFontType2" : "CIDFontType0"),
            ["BaseFont"] = new PdfName(baseName),
            ["CIDSystemInfo"] = new PdfDictionary
            {
                ["Registry"] = new PdfLiteralString("Adobe"),
                ["Ordering"] = new PdfLiteralString("Identity"),
                ["Supplement"] = new PdfInt(0),
            },
            ["FontDescriptor"] = descriptorObj.Reference,
            ["DW"] = new PdfInt(1000),
            ["W"] = new PdfArray(new PdfInt(0), widthsList),
        };
        if (isTrueType)
            cidFontDict["CIDToGIDMap"] = new PdfName("Identity");

        var cidFontObj = doc.Store.Add(cidFontDict);

        // 5. ToUnicode CMap stream
        var cmapText = ToUnicodeCMap.Generate(isTrueType ? Subsetter.NewGidToUnicode : _rawGidToUnicode);
        var toUnicodeObj = doc.Store.Add(new PdfDictionary());
        toUnicodeObj.SetStream(Encoding.ASCII.GetBytes(cmapText), compress: doc.Compress);

        // 6. Populate top-level Type0 font dictionary
        var top = FontObject.Dict;
        top["Type"] = new PdfName("Font");
        top["Subtype"] = new PdfName("Type0");
        top["BaseFont"] = new PdfName(baseName);
        top["Encoding"] = new PdfName("Identity-H");
        top["DescendantFonts"] = new PdfArray(cidFontObj.Reference);
        top["ToUnicode"] = toUnicodeObj.Reference;
    }

    private static string BuildSubsetTag(IReadOnlyList<int> glyphIds)
    {
        var raw = new byte[glyphIds.Count * 2];
        for (var i = 0; i < glyphIds.Count; i++)
        {
            raw[i * 2] = (byte)(glyphIds[i] & 0xFF);
            raw[(i * 2) + 1] = (byte)((glyphIds[i] >> 8) & 0xFF);
        }
        var hash = MD5.HashData(raw);
        var chars = new char[6];
        for (var i = 0; i < 6; i++)
            chars[i] = (char)('A' + (hash[i] % 26));
        return new string(chars);
    }
}
