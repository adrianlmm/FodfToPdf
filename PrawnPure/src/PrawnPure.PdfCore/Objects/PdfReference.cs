using System.IO.Compression;
using PrawnPure.PdfCore.Security;

namespace PrawnPure.PdfCore.Objects;

/// <summary>
/// Indirect PDF object stored in the object table (port of PDF::Core::Reference + Stream).
/// </summary>
public sealed class PdfIndirectObject
{
    public int Id { get; }
    public int Gen { get; }
    public PdfObject Data { get; set; }
    public byte[]? StreamData { get; set; }
    public bool CompressStream { get; set; }
    public bool Encrypt { get; set; } = true;
    public long Offset { get; internal set; }

    /// <summary>Optional hook invoked right before serialization (port of Reference#on_encode).</summary>
    public Action<PdfIndirectObject>? OnEncode { get; set; }

    public PdfRef Reference => new(Id, Gen);

    public PdfIndirectObject(int id, PdfObject data, int gen = 0)
    {
        Id = id;
        Gen = gen;
        Data = data;
    }

    public PdfDictionary Dict =>
        Data as PdfDictionary ?? throw new InvalidOperationException($"Object {Id} is not a PdfDictionary.");

    public void SetStream(byte[] rawBytes, bool compress = false)
    {
        StreamData = rawBytes;
        CompressStream = compress;
    }

    public void WriteTo(Stream output, PdfEncryption? encryption)
    {
        OnEncode?.Invoke(this);

        var activeEnc = Encrypt ? encryption : null;
        Offset = output.Position;
        PdfObject.WriteAscii(output, $"{Id} {Gen} obj\n");

        if (StreamData is null)
        {
            Data.WriteTo(output, activeEnc, Id, Gen);
            output.WriteByte((byte)'\n');
            PdfObject.WriteAscii(output, "endobj\n");
            return;
        }

        var dict = Data as PdfDictionary ?? new PdfDictionary();
        var payload = StreamData;

        if (CompressStream)
        {
            payload = DeflateZlib(payload);
            AddFlateFilter(dict);
        }

        if (activeEnc is not null)
        {
            payload = activeEnc.Encrypt(payload, Id, Gen);
        }

        dict["Length"] = new PdfInt(payload.Length);
        dict.WriteTo(output, activeEnc, Id, Gen);
        PdfObject.WriteAscii(output, "\nstream\n");
        output.Write(payload, 0, payload.Length);
        PdfObject.WriteAscii(output, "\nendstream\nendobj\n");
    }

    private static void AddFlateFilter(PdfDictionary dict)
    {
        var existing = dict["Filter"];
        var flate = new PdfName("FlateDecode");
        if (existing is null)
        {
            dict["Filter"] = flate;
        }
        else if (existing is PdfName existingName)
        {
            if (existingName.Value != "FlateDecode")
            {
                dict["Filter"] = new PdfArray(flate, existingName);
            }
        }
        else if (existing is PdfArray existingArr)
        {
            var hasFlate = existingArr.Items.OfType<PdfName>().Any(n => n.Value == "FlateDecode");
            if (!hasFlate)
            {
                existingArr.Items.Insert(0, flate);
            }
        }
    }

    public static byte[] DeflateZlib(byte[] input)
    {
        using var ms = new MemoryStream();
        using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(input, 0, input.Length);
        }
        return ms.ToArray();
    }

    public static byte[] InflateZlib(byte[] input)
    {
        using var src = new MemoryStream(input);
        using var zlib = new ZLibStream(src, CompressionMode.Decompress);
        using var dst = new MemoryStream();
        zlib.CopyTo(dst);
        return dst.ToArray();
    }
}
