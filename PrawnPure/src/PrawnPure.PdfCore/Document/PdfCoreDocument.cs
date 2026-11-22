using System.Globalization;
using PrawnPure.PdfCore.Objects;
using PrawnPure.PdfCore.Security;

namespace PrawnPure.PdfCore.Document;

/// <summary>
/// Core PDF document &amp; renderer (port of PDF::Core::DocumentState + Renderer + OutlineRoot/Item).
/// </summary>
public sealed class PdfCoreDocument
{
    private readonly List<PdfCorePage> _pages = [];
    private readonly List<Action<PdfCoreDocument>> _beforeRenderCallbacks = [];
    private readonly List<(string Title, int PageIndex, double? PdfTop, bool Opened)> _outlines = [];

    public PdfObjectStore Store { get; } = new();
    public double Version { get; private set; } = 1.4;
    public bool Compress { get; set; } = true;
    public PdfEncryption? Encryption { get; set; }

    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? Subject { get; set; }
    public string? Keywords { get; set; }
    public string? Creator { get; set; } = "PrawnPure";
    public string? Producer { get; set; } = "PrawnPure";

    public IReadOnlyList<PdfCorePage> Pages => _pages;
    public int PageCount => _pages.Count;

    public void MinVersion(double version)
    {
        if (version > Version) Version = version;
    }

    public void OnBeforeRender(Action<PdfCoreDocument> callback)
    {
        _beforeRenderCallbacks.Add(callback);
    }

    public PdfCorePage AddPage(double width, double height)
    {
        var page = new PdfCorePage(this, width, height);
        _pages.Add(page);

        var kids = (PdfArray)Store.Pages.Dict["Kids"]!;
        kids.Add(page.PageObject.Reference);
        Store.Pages.Dict["Count"] = new PdfInt(_pages.Count);

        return page;
    }

    public void AddOutline(string title, int pageIndex, double? pdfTop = null, bool opened = true)
    {
        if (pageIndex < 0 || pageIndex >= _pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));

        _outlines.Add((title, pageIndex, pdfTop, opened));
    }

    public byte[] Render()
    {
        using var ms = new MemoryStream();
        Save(ms);
        return ms.ToArray();
    }

    public void Save(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);

        // Execute deferred callbacks (e.g. font subset embedding, deferred links)
        foreach (var cb in _beforeRenderCallbacks)
            cb(this);
        _beforeRenderCallbacks.Clear();

        // Finalize all page content streams
        foreach (var page in _pages)
            page.FinalizeContent(Compress);

        // Populate /Info dictionary
        WriteInfoDictionary();

        // Build /Outlines tree if any
        if (_outlines.Count > 0)
            BuildOutlines();

        // Build /Encrypt object if security is enabled
        PdfIndirectObject? encryptObj = null;
        if (Encryption is not null)
        {
            encryptObj = Store.Add(Encryption.BuildEncryptDictionary());
            encryptObj.Encrypt = false;
        }

        // Render PDF to a seekable buffer so xref byte offsets are always exact even if `output` is non-seekable
        var target = output.CanSeek ? output : new MemoryStream();
        var startPos = target.Position;

        // 1. Header (%PDF-1.x + 4 binary bytes per ISO 32000-1 §7.5.2)
        PdfObject.WriteAscii(target, $"%PDF-{Version.ToString("0.0", CultureInfo.InvariantCulture)}\n");
        target.Write([0x25, 0xFF, 0xFF, 0xFF, 0xFF, 0x0A]);

        // 2. Body
        foreach (var obj in Store.Objects)
        {
            obj.WriteTo(target, Encryption);
        }

        // 3. Cross-reference table (xref)
        var xrefOffset = target.Position - startPos;
        PdfObject.WriteAscii(target, $"xref\n0 {Store.Count + 1}\n");
        PdfObject.WriteAscii(target, "0000000000 65535 f \n");
        foreach (var obj in Store.Objects)
        {
            var relOffset = obj.Offset - startPos;
            PdfObject.WriteAscii(target, $"{relOffset:D10} 00000 n \n");
        }

        // 4. Trailer
        var trailer = new PdfDictionary
        {
            ["Size"] = new PdfInt(Store.Count + 1),
            ["Root"] = Store.Root.Reference,
            ["Info"] = Store.Info.Reference,
        };

        if (encryptObj is not null && Encryption is not null)
        {
            trailer["Encrypt"] = encryptObj.Reference;
            var idHex = new PdfHexString(Encryption.FileId, encryptable: false);
            trailer["ID"] = new PdfArray(idHex, idHex);
        }

        PdfObject.WriteAscii(target, "trailer\n");
        trailer.WriteTo(target, encryption: null);
        PdfObject.WriteAscii(target, $"\nstartxref\n{xrefOffset}\n%%EOF\n");

        if (!ReferenceEquals(target, output))
        {
            target.Position = 0;
            target.CopyTo(output);
            target.Dispose();
        }
    }

    private void WriteInfoDictionary()
    {
        var info = Store.Info.Dict;
        if (!string.IsNullOrEmpty(Title)) info["Title"] = new PdfTextString(Title);
        if (!string.IsNullOrEmpty(Author)) info["Author"] = new PdfTextString(Author);
        if (!string.IsNullOrEmpty(Subject)) info["Subject"] = new PdfTextString(Subject);
        if (!string.IsNullOrEmpty(Keywords)) info["Keywords"] = new PdfTextString(Keywords);
        if (!string.IsNullOrEmpty(Creator)) info["Creator"] = new PdfTextString(Creator);
        if (!string.IsNullOrEmpty(Producer)) info["Producer"] = new PdfTextString(Producer);
    }

    private void BuildOutlines()
    {
        var rootObj = Store.Add(new PdfDictionary
        {
            ["Type"] = new PdfName("Outlines"),
            ["Count"] = new PdfInt(_outlines.Count),
        });

        var itemObjs = new List<PdfIndirectObject>(_outlines.Count);
        for (var i = 0; i < _outlines.Count; i++)
        {
            var (title, pageIndex, pdfTop, _) = _outlines[i];
            var pageRef = _pages[pageIndex].PageObject.Reference;
            var dest = pdfTop.HasValue
                ? new PdfArray(pageRef, new PdfName("XYZ"), PdfNull.Instance, new PdfReal(pdfTop.Value), PdfNull.Instance)
                : new PdfArray(pageRef, new PdfName("Fit"));

            var item = Store.Add(new PdfDictionary
            {
                ["Title"] = new PdfTextString(title),
                ["Parent"] = rootObj.Reference,
                ["Dest"] = dest,
            });
            itemObjs.Add(item);
        }

        for (var i = 0; i < itemObjs.Count; i++)
        {
            if (i > 0) itemObjs[i].Dict["Prev"] = itemObjs[i - 1].Reference;
            if (i + 1 < itemObjs.Count) itemObjs[i].Dict["Next"] = itemObjs[i + 1].Reference;
        }

        rootObj.Dict["First"] = itemObjs[0].Reference;
        rootObj.Dict["Last"] = itemObjs[^1].Reference;
        Store.Root.Dict["Outlines"] = rootObj.Reference;
    }
}
