using System.Buffers.Binary;
using PrawnPure.PdfCore.Document;
using PrawnPure.PdfCore.Objects;

namespace PrawnPure.PdfCore.Images;

/// <summary>
/// Native JPEG image embedder (port of prawn/images/jpg.rb).
/// Embeds raw JPEG bytes directly with /DCTDecode (zero re-encoding, zero external libraries).
/// </summary>
public sealed class JpegImage
{
    public int Width { get; }
    public int Height { get; }
    public int BitsPerComponent { get; }
    public int Channels { get; }
    public byte[] RawData { get; }

    public JpegImage(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!CanRender(data))
            throw new InvalidDataException("Not a valid JPEG image.");

        RawData = data;
        var offset = 2;
        var found = false;

        while (offset + 4 <= data.Length)
        {
            while (offset < data.Length && data[offset] != 0xFF)
                offset++;
            if (offset + 1 >= data.Length) break;

            // Skip fill 0xFF bytes
            while (offset + 1 < data.Length && data[offset + 1] == 0xFF)
                offset++;
            if (offset + 1 >= data.Length) break;

            var code = data[offset + 1];
            offset += 2;

            // Standalone markers without length
            if (code is 0xD8 or 0xD9 or 0x01 || code is >= 0xD0 and <= 0xD7)
                continue;

            if (offset + 2 > data.Length) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
            if (length < 2 || offset + length > data.Length) break;

            // SOF0..SOF15 excluding DHT(C4), JPG(C8), DAC(CC)
            if (code is >= 0xC0 and <= 0xCF && code is not (0xC4 or 0xC8 or 0xCC))
            {
                if (length < 8)
                    throw new InvalidDataException("Truncated JPEG SOF marker.");

                BitsPerComponent = data[offset + 2];
                Height = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 3));
                Width = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 5));
                Channels = data[offset + 7];
                found = true;
                break;
            }

            offset += length;
        }

        if (!found || Width <= 0 || Height <= 0)
            throw new InvalidDataException("JPEG image missing valid SOF marker.");
    }

    public static bool CanRender(byte[] data) =>
        data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    public PdfIndirectObject BuildPdfObject(PdfCoreDocument doc)
    {
        var colorSpace = Channels switch
        {
            1 => "DeviceGray",
            3 => "DeviceRGB",
            4 => "DeviceCMYK",
            _ => throw new NotSupportedException($"JPEG uses unsupported channel count: {Channels}"),
        };

        var dict = new PdfDictionary
        {
            ["Type"] = new PdfName("XObject"),
            ["Subtype"] = new PdfName("Image"),
            ["ColorSpace"] = new PdfName(colorSpace),
            ["BitsPerComponent"] = new PdfInt(BitsPerComponent),
            ["Width"] = new PdfInt(Width),
            ["Height"] = new PdfInt(Height),
            ["Filter"] = new PdfName("DCTDecode"),
        };

        // Adobe CMYK JPEGs store inverted channels (PDF Reference §4.8.4)
        if (Channels == 4)
        {
            dict["Decode"] = PdfArray.OfReals(1, 0, 1, 0, 1, 0, 1, 0);
        }

        var obj = doc.Store.Add(dict);
        obj.SetStream(RawData, compress: false);
        return obj;
    }
}
