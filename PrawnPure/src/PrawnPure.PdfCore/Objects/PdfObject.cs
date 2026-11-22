using System.Globalization;
using System.Text;
using PrawnPure.PdfCore.Security;

namespace PrawnPure.PdfCore.Objects;

/// <summary>
/// Base class for PDF primitive and composite objects (port of Ruby pdf-core pdf_object.rb).
/// </summary>
public abstract class PdfObject
{
    public abstract void WriteTo(Stream output, PdfEncryption? encryption = null, int objId = 0, int gen = 0);

    public static string FormatReal(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return "0";
        if (Math.Abs(value) < 0.000005)
            return "0";

        var s = value.ToString("0.#####", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }

    internal static void WriteAscii(Stream output, string text)
    {
        foreach (var ch in text)
            output.WriteByte((byte)ch);
    }
}

public sealed class PdfNull : PdfObject
{
    public static readonly PdfNull Instance = new();
    private PdfNull() { }

    public override void WriteTo(Stream output, PdfEncryption? encryption = null, int objId = 0, int gen = 0)
        => WriteAscii(output, "null");
}

public sealed class PdfBool(bool value) : PdfObject
{
    public static readonly PdfBool True = new(true);
    public static readonly PdfBool False = new(false);

    public bool Value { get; } = value;

    public static PdfBool From(bool value) => value ? True : False;

    public override void WriteTo(Stream output, PdfEncryption? encryption = null, int objId = 0, int gen = 0)
        => WriteAscii(output, Value ? "true" : "false");
}

public sealed class PdfInt(long value) : PdfObject
{
    public long Value { get; } = value;

    public override void WriteTo(Stream output, PdfEncryption? encryption = null, int objId = 0, int gen = 0)
        => WriteAscii(output, Value.ToString(CultureInfo.InvariantCulture));
}

public sealed class PdfReal(double value) : PdfObject
{
    public double Value { get; } = value;

    public override void WriteTo(Stream output, PdfEncryption? encryption = null, int objId = 0, int gen = 0)
        => WriteAscii(output, FormatReal(Value));
}

/// <summary>PDF Name object (e.g. /Type, /Font). Escapes restricted characters using #XX.</summary>
public sealed class PdfName : PdfObject
{
    public string Value { get; }

    public PdfName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Value = name.StartsWith('/') ? name[1..] : name;
    }

    public override void WriteTo(Stream output, PdfEncryption? encryption = null, int objId = 0, int gen = 0)
    {
        output.WriteByte((byte)'/');
        var bytes = Encoding.UTF8.GetBytes(Value);
        foreach (var b in bytes)
        {
            if (b is <= 32 or >= 127 or (byte)'#' or (byte)'(' or (byte)')' or (byte)'/' or (byte)'<' or (byte)'>'
                or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'%')
            {
                WriteAscii(output, $"#{b:X2}");
            }
            else
            {
                output.WriteByte(b);
            }
        }
    }

    public override string ToString() => "/" + Value;
}

/// <summary>PDF Literal String (e.g. (Hello)). Escapes \, (, ), and CR.</summary>
public sealed class PdfLiteralString(byte[] bytes) : PdfObject
{
    public byte[] Bytes { get; } = bytes;

    public PdfLiteralString(string asciiOrLatin1)
        : this(Encoding.Latin1.GetBytes(asciiOrLatin1))
    {
    }

    public override void WriteTo(Stream output, PdfEncryption? encryption = null, int objId = 0, int gen = 0)
    {
        var data = encryption is not null ? encryption.Encrypt(Bytes, objId, gen) : Bytes;
        output.WriteByte((byte)'(');
        foreach (var b in data)
        {
            switch (b)
            {
                case (byte)'\\':
                case (byte)'(':
                case (byte)')':
                    output.WriteByte((byte)'\\');
                    output.WriteByte(b);
                    break;
                case (byte)'\r':
                    output.WriteByte((byte)'\\');
                    output.WriteByte((byte)'r');
                    break;
                case (byte)'\n':
                    output.WriteByte((byte)'\\');
                    output.WriteByte((byte)'n');
                    break;
                default:
                    output.WriteByte(b);
                    break;
            }
        }
        output.WriteByte((byte)')');
    }
}

/// <summary>PDF Hex String (e.g. &lt;FEFF0041&gt;). Port of PDF::Core::ByteString.</summary>
public sealed class PdfHexString(byte[] bytes, bool encryptable = true) : PdfObject
{
    public byte[] Bytes { get; } = bytes;
    public bool Encryptable { get; } = encryptable;

    public override void WriteTo(Stream output, PdfEncryption? encryption = null, int objId = 0, int gen = 0)
    {
        var data = (encryption is not null && Encryptable) ? encryption.Encrypt(Bytes, objId, gen) : Bytes;
        output.WriteByte((byte)'<');
        WriteAscii(output, Convert.ToHexString(data));
        output.WriteByte((byte)'>');
    }
}

/// <summary>PDF Text String (UTF-16BE with BOM when non-ASCII, or PDFDocEncoding/ASCII when pure ASCII).</summary>
public sealed class PdfTextString(string text) : PdfObject
{
    public string Text { get; } = text ?? string.Empty;

    public override void WriteTo(Stream output, PdfEncryption? encryption = null, int objId = 0, int gen = 0)
    {
        var isAscii = true;
        foreach (var ch in Text)
        {
            if (ch < 32 || ch > 126)
            {
                isAscii = false;
                break;
            }
        }

        if (isAscii)
        {
            new PdfLiteralString(Encoding.ASCII.GetBytes(Text)).WriteTo(output, encryption, objId, gen);
            return;
        }

        var utf16 = Encoding.BigEndianUnicode.GetBytes(Text);
        var withBom = new byte[utf16.Length + 2];
        withBom[0] = 0xFE;
        withBom[1] = 0xFF;
        Buffer.BlockCopy(utf16, 0, withBom, 2, utf16.Length);
        new PdfHexString(withBom).WriteTo(output, encryption, objId, gen);
    }
}

public sealed class PdfArray : PdfObject
{
    public List<PdfObject> Items { get; } = [];

    public PdfArray() { }

    public PdfArray(params PdfObject[] items)
    {
        Items.AddRange(items);
    }

    public static PdfArray OfReals(params double[] values)
    {
        var arr = new PdfArray();
        foreach (var v in values) arr.Items.Add(new PdfReal(v));
        return arr;
    }

    public static PdfArray OfInts(params long[] values)
    {
        var arr = new PdfArray();
        foreach (var v in values) arr.Items.Add(new PdfInt(v));
        return arr;
    }

    public void Add(PdfObject item) => Items.Add(item);

    public override void WriteTo(Stream output, PdfEncryption? encryption = null, int objId = 0, int gen = 0)
    {
        output.WriteByte((byte)'[');
        for (var i = 0; i < Items.Count; i++)
        {
            if (i > 0) output.WriteByte((byte)' ');
            Items[i].WriteTo(output, encryption, objId, gen);
        }
        output.WriteByte((byte)']');
    }
}

public sealed class PdfDictionary : PdfObject
{
    private readonly Dictionary<string, PdfObject> _entries = new(StringComparer.Ordinal);

    public PdfObject? this[string key]
    {
        get => _entries.TryGetValue(NormalizeKey(key), out var v) ? v : null;
        set
        {
            var k = NormalizeKey(key);
            if (value is null) _entries.Remove(k);
            else _entries[k] = value;
        }
    }

    public int Count => _entries.Count;

    public bool ContainsKey(string key) => _entries.ContainsKey(NormalizeKey(key));

    public IEnumerable<KeyValuePair<string, PdfObject>> Entries => _entries;

    private static string NormalizeKey(string key) => key.StartsWith('/') ? key[1..] : key;

    public override void WriteTo(Stream output, PdfEncryption? encryption = null, int objId = 0, int gen = 0)
    {
        WriteAscii(output, "<<\n");
        foreach (var (k, v) in _entries.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            new PdfName(k).WriteTo(output);
            output.WriteByte((byte)' ');
            v.WriteTo(output, encryption, objId, gen);
            output.WriteByte((byte)'\n');
        }
        WriteAscii(output, ">>");
    }
}

/// <summary>Indirect object reference (<c>id gen R</c>).</summary>
public sealed class PdfRef(int id, int gen = 0) : PdfObject
{
    public int Id { get; } = id;
    public int Gen { get; } = gen;

    public override void WriteTo(Stream output, PdfEncryption? encryption = null, int objId = 0, int gen = 0)
        => WriteAscii(output, $"{Id} {Gen} R");

    public override string ToString() => $"{Id} {Gen} R";
}
