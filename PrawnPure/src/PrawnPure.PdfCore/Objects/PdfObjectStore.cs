namespace PrawnPure.PdfCore.Objects;

/// <summary>
/// Indirect object store (port of PDF::Core::ObjectStore).
/// Holds Info, Catalog (Root), Pages tree, and every indirect object in allocation order.
/// </summary>
public sealed class PdfObjectStore
{
    private readonly List<PdfIndirectObject> _objects = [];

    public PdfIndirectObject Info { get; }
    public PdfIndirectObject Root { get; }
    public PdfIndirectObject Pages { get; }

    public IReadOnlyList<PdfIndirectObject> Objects => _objects;

    public int Count => _objects.Count;

    public PdfObjectStore()
    {
        Info = Add(new PdfDictionary());
        Pages = Add(new PdfDictionary
        {
            ["Type"] = new PdfName("Pages"),
            ["Kids"] = new PdfArray(),
            ["Count"] = new PdfInt(0),
        });
        Root = Add(new PdfDictionary
        {
            ["Type"] = new PdfName("Catalog"),
            ["Pages"] = Pages.Reference,
        });
    }

    public PdfIndirectObject Add(PdfObject data)
    {
        var obj = new PdfIndirectObject(_objects.Count + 1, data);
        _objects.Add(obj);
        return obj;
    }

    public PdfIndirectObject AddDictionary(Action<PdfDictionary>? configure = null)
    {
        var dict = new PdfDictionary();
        configure?.Invoke(dict);
        return Add(dict);
    }
}
