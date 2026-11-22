using System.Buffers.Binary;
using System.Text;
using PrawnPure.PdfCore.Document;
using PrawnPure.PdfCore.Objects;

namespace PrawnPure.PdfCore.Images;

/// <summary>
/// Pure C# PNG decoder &amp; PDF XObject builder (port of prawn/images/png.rb, extended with
/// proper PNG scanline unfiltering and Adam7 de-interlacing so every PNG renders artifact-free).
/// </summary>
public sealed class PngImage
{
    public int Width { get; }
    public int Height { get; }
    public int BitsPerComponent { get; }
    public int ColorType { get; }
    public int InterlaceMethod { get; }

    private readonly byte[]? _palette;
    private readonly byte[]? _trnsPalette;
    private readonly ushort? _trnsGray;
    private readonly (ushort R, ushort G, ushort B)? _trnsRgb;
    private readonly byte[] _inflatedIdat;

    public PngImage(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!CanRender(data))
            throw new InvalidDataException("Not a valid PNG image.");

        var offset = 8;
        using var idat = new MemoryStream();
        using var plte = new MemoryStream();
        var ihdrFound = false;

        while (offset + 12 <= data.Length)
        {
            var chunkLen = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
            var chunkType = Encoding.ASCII.GetString(data, offset + 4, 4);
            var payload = offset + 8;
            if (chunkLen < 0 || payload + chunkLen > data.Length)
                break;

            switch (chunkType)
            {
                case "IHDR":
                    if (chunkLen < 13) throw new InvalidDataException("Invalid PNG IHDR.");
                    Width = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(payload));
                    Height = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(payload + 4));
                    BitsPerComponent = data[payload + 8];
                    ColorType = data[payload + 9];
                    var compression = data[payload + 10];
                    var filter = data[payload + 11];
                    InterlaceMethod = data[payload + 12];
                    if (compression != 0 || filter != 0)
                        throw new NotSupportedException("Unsupported PNG compression or filter method.");
                    if (InterlaceMethod is not (0 or 1))
                        throw new NotSupportedException($"Unsupported PNG interlace method: {InterlaceMethod}.");
                    ihdrFound = true;
                    break;

                case "PLTE":
                    plte.Write(data, payload, chunkLen);
                    break;

                case "IDAT":
                    idat.Write(data, payload, chunkLen);
                    break;

                case "tRNS":
                    if (ColorType == 3)
                    {
                        _trnsPalette = new byte[chunkLen];
                        Buffer.BlockCopy(data, payload, _trnsPalette, 0, chunkLen);
                    }
                    else if (ColorType == 0 && chunkLen >= 2)
                    {
                        _trnsGray = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(payload));
                    }
                    else if (ColorType == 2 && chunkLen >= 6)
                    {
                        _trnsRgb = (
                            BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(payload)),
                            BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(payload + 2)),
                            BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(payload + 4)));
                    }
                    break;

                case "IEND":
                    offset = data.Length;
                    break;
            }

            offset = payload + chunkLen + 4; // skip CRC
        }

        if (!ihdrFound || Width <= 0 || Height <= 0)
            throw new InvalidDataException("PNG missing valid IHDR.");

        if (plte.Length > 0)
            _palette = plte.ToArray();

        _inflatedIdat = PdfIndirectObject.InflateZlib(idat.ToArray());
    }

    public static bool CanRender(byte[] data) =>
        data.Length >= 8 &&
        data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 &&
        data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A;

    private int TotalChannels => ColorType switch
    {
        0 => 1, // Gray
        2 => 3, // RGB
        3 => 1, // Indexed
        4 => 2, // Gray + Alpha
        6 => 4, // RGBA
        _ => throw new NotSupportedException($"Unsupported PNG color type {ColorType}."),
    };

    private int ColorChannels => ColorType switch
    {
        0 or 3 or 4 => 1,
        2 or 6 => 3,
        _ => throw new NotSupportedException($"Unsupported PNG color type {ColorType}."),
    };

    public PdfIndirectObject BuildPdfObject(PdfCoreDocument doc)
    {
        if (BitsPerComponent > 8)
            doc.MinVersion(1.5);

        // Decode raw unfiltered pixels (handles both non-interlaced and Adam7 interlaced)
        var rawPixels = InterlaceMethod == 0
            ? UnfilterImage(_inflatedIdat, Width, Height, TotalChannels, BitsPerComponent)
            : DeinterlaceAdam7(_inflatedIdat, Width, Height, TotalChannels, BitsPerComponent);

        byte[] colorStream;
        byte[]? alphaStream = null;
        var outBits = BitsPerComponent;

        if (ColorType is 4 or 6)
        {
            // Split interleaved color + alpha into separate streams (PDF requires /SMask for alpha)
            var bytesPerSample = BitsPerComponent / 8;
            var colorBytesPerPixel = ColorChannels * bytesPerSample;
            var pixelStride = colorBytesPerPixel + bytesPerSample;
            var totalPixels = Width * Height;

            colorStream = new byte[totalPixels * colorBytesPerPixel];
            alphaStream = new byte[totalPixels * bytesPerSample];

            for (var i = 0; i < totalPixels; i++)
            {
                var src = i * pixelStride;
                Buffer.BlockCopy(rawPixels, src, colorStream, i * colorBytesPerPixel, colorBytesPerPixel);
                Buffer.BlockCopy(rawPixels, src + colorBytesPerPixel, alphaStream, i * bytesPerSample, bytesPerSample);
            }
        }
        else if (ColorType == 3 && _trnsPalette is { Length: > 0 })
        {
            // Indexed palette with per-entry alpha in tRNS -> keep indexed image + 8-bit SMask
            colorStream = rawPixels;
            alphaStream = BuildPaletteAlphaMask(rawPixels, Width, Height, BitsPerComponent, _trnsPalette);
        }
        else
        {
            colorStream = rawPixels;
        }

        var imgDict = new PdfDictionary
        {
            ["Type"] = new PdfName("XObject"),
            ["Subtype"] = new PdfName("Image"),
            ["Width"] = new PdfInt(Width),
            ["Height"] = new PdfInt(Height),
            ["BitsPerComponent"] = new PdfInt(outBits),
        };

        if (ColorType == 3 && _palette is { Length: > 0 })
        {
            var palObj = doc.Store.Add(new PdfDictionary());
            palObj.SetStream(_palette, compress: doc.Compress);
            imgDict["ColorSpace"] = new PdfArray(
                new PdfName("Indexed"),
                new PdfName("DeviceRGB"),
                new PdfInt((_palette.Length / 3) - 1),
                palObj.Reference);
        }
        else
        {
            imgDict["ColorSpace"] = new PdfName(ColorChannels == 1 ? "DeviceGray" : "DeviceRGB");
        }

        if (_trnsGray.HasValue)
        {
            imgDict["Mask"] = PdfArray.OfInts(_trnsGray.Value, _trnsGray.Value);
        }
        else if (_trnsRgb.HasValue)
        {
            var (r, g, b) = _trnsRgb.Value;
            imgDict["Mask"] = PdfArray.OfInts(r, r, g, g, b, b);
        }

        if (alphaStream is not null)
        {
            doc.MinVersion(1.4);
            var alphaBits = ColorType == 3 ? 8 : BitsPerComponent;
            var smaskDict = new PdfDictionary
            {
                ["Type"] = new PdfName("XObject"),
                ["Subtype"] = new PdfName("Image"),
                ["Width"] = new PdfInt(Width),
                ["Height"] = new PdfInt(Height),
                ["BitsPerComponent"] = new PdfInt(alphaBits),
                ["ColorSpace"] = new PdfName("DeviceGray"),
                ["Decode"] = PdfArray.OfInts(0, 1),
            };
            var smaskObj = doc.Store.Add(smaskDict);
            smaskObj.SetStream(alphaStream, compress: true);
            imgDict["SMask"] = smaskObj.Reference;
        }

        var imgObj = doc.Store.Add(imgDict);
        imgObj.SetStream(colorStream, compress: true);
        return imgObj;
    }

    private static byte[] BuildPaletteAlphaMask(byte[] indexedPixels, int width, int height, int bits, byte[] trns)
    {
        var alpha = new byte[width * height];
        var rowBytes = ((width * bits) + 7) / 8;
        var mask = (1 << bits) - 1;

        for (var y = 0; y < height; y++)
        {
            var rowOff = y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                int idx;
                if (bits == 8)
                {
                    idx = indexedPixels[rowOff + x];
                }
                else
                {
                    var bitPos = x * bits;
                    var b = indexedPixels[rowOff + (bitPos >> 3)];
                    var shift = 8 - bits - (bitPos & 7);
                    idx = (b >> shift) & mask;
                }
                alpha[(y * width) + x] = idx < trns.Length ? trns[idx] : (byte)0xFF;
            }
        }
        return alpha;
    }

    private static byte[] UnfilterImage(byte[] src, int width, int height, int channels, int bits)
    {
        var rowBytes = ((width * channels * bits) + 7) / 8;
        var bpp = Math.Max(1, (channels * bits) / 8);
        var dst = new byte[height * rowBytes];
        var srcOffset = 0;

        for (var y = 0; y < height; y++)
        {
            if (srcOffset >= src.Length) break;
            var filterType = src[srcOffset++];
            var dstRow = y * rowBytes;
            var prevRow = (y - 1) * rowBytes;
            var avail = Math.Min(rowBytes, src.Length - srcOffset);

            for (var x = 0; x < avail; x++)
            {
                var raw = src[srcOffset + x];
                var a = x >= bpp ? dst[dstRow + x - bpp] : 0;
                var b = y > 0 ? dst[prevRow + x] : 0;
                var c = (x >= bpp && y > 0) ? dst[prevRow + x - bpp] : 0;

                dst[dstRow + x] = filterType switch
                {
                    0 => raw,
                    1 => (byte)(raw + a),
                    2 => (byte)(raw + b),
                    3 => (byte)(raw + ((a + b) >> 1)),
                    4 => (byte)(raw + PaethPredictor(a, b, c)),
                    _ => throw new InvalidDataException($"Unknown PNG filter type {filterType}."),
                };
            }

            srcOffset += rowBytes;
        }

        return dst;
    }

    private static byte[] DeinterlaceAdam7(byte[] src, int width, int height, int channels, int bits)
    {
        int[] startX = [0, 4, 0, 2, 0, 1, 0];
        int[] startY = [0, 0, 4, 0, 2, 0, 1];
        int[] stepX = [8, 8, 4, 4, 2, 2, 1];
        int[] stepY = [8, 8, 8, 4, 4, 2, 2];

        var outRowBytes = ((width * channels * bits) + 7) / 8;
        var output = new byte[height * outRowBytes];
        var srcPos = 0;
        var bpp = Math.Max(1, (channels * bits) / 8);

        for (var pass = 0; pass < 7; pass++)
        {
            var pw = (width - startX[pass] + stepX[pass] - 1) / stepX[pass];
            var ph = (height - startY[pass] + stepY[pass] - 1) / stepY[pass];
            if (pw <= 0 || ph <= 0) continue;

            var passRowBytes = ((pw * channels * bits) + 7) / 8;
            var passTotal = ph * (passRowBytes + 1);
            var passSlice = new byte[Math.Min(passTotal, Math.Max(0, src.Length - srcPos))];
            Buffer.BlockCopy(src, srcPos, passSlice, 0, passSlice.Length);
            srcPos += passTotal;

            var passPixels = UnfilterImage(passSlice, pw, ph, channels, bits);

            if (bits >= 8)
            {
                var pixelBytes = (channels * bits) / 8;
                for (var py = 0; py < ph; py++)
                {
                    var dy = startY[pass] + (py * stepY[pass]);
                    for (var px = 0; px < pw; px++)
                    {
                        var dx = startX[pass] + (px * stepX[pass]);
                        Buffer.BlockCopy(
                            passPixels, (py * passRowBytes) + (px * pixelBytes),
                            output, (dy * outRowBytes) + (dx * pixelBytes),
                            pixelBytes);
                    }
                }
            }
            else
            {
                // Sub-byte bit depths (1, 2, 4 bits — single channel gray or indexed)
                var mask = (1 << bits) - 1;
                for (var py = 0; py < ph; py++)
                {
                    var dy = startY[pass] + (py * stepY[pass]);
                    for (var px = 0; px < pw; px++)
                    {
                        var dx = startX[pass] + (px * stepX[pass]);
                        var srcBit = px * bits;
                        var val = (passPixels[(py * passRowBytes) + (srcBit >> 3)] >> (8 - bits - (srcBit & 7))) & mask;
                        var dstBit = dx * bits;
                        output[(dy * outRowBytes) + (dstBit >> 3)] |= (byte)(val << (8 - bits - (dstBit & 7)));
                    }
                }
            }
        }

        return output;
    }

    private static int PaethPredictor(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }
}
