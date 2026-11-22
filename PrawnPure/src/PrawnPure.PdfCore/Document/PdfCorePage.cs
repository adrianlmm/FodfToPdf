using System.Text;
using PrawnPure.PdfCore.Objects;

namespace PrawnPure.PdfCore.Document;

/// <summary>
/// A PDF page object with its own Resources dictionary, content stream buffer, and annotations
/// (port of PDF::Core::Page).
/// </summary>
public sealed class PdfCorePage
{
    private readonly PdfCoreDocument _document;
    private readonly StringBuilder _content = new(1024);
    private readonly PdfDictionary _resources = new();
    private readonly PdfDictionary _fonts = new();
    private readonly PdfDictionary _xObjects = new();
    private readonly PdfDictionary _extGStates = new();
    private readonly PdfDictionary _shadings = new();
    private readonly PdfArray _annots = new();
    private bool _finalized;

    public double Width { get; }
    public double Height { get; }
    public PdfIndirectObject PageObject { get; }
    public PdfIndirectObject ContentObject { get; }

    internal PdfCorePage(PdfCoreDocument document, double width, double height)
    {
        _document = document;
        Width = width;
        Height = height;

        ContentObject = document.Store.Add(new PdfDictionary());

        _resources["ProcSet"] = new PdfArray(
            new PdfName("PDF"),
            new PdfName("Text"),
            new PdfName("ImageB"),
            new PdfName("ImageC"),
            new PdfName("ImageI"));

        PageObject = document.Store.Add(new PdfDictionary
        {
            ["Type"] = new PdfName("Page"),
            ["Parent"] = document.Store.Pages.Reference,
            ["MediaBox"] = PdfArray.OfReals(0, 0, width, height),
            ["Resources"] = _resources,
            ["Contents"] = ContentObject.Reference,
        });
    }

    public void AppendContent(string operators)
    {
        if (string.IsNullOrEmpty(operators)) return;
        _content.Append(operators);
        if (!operators.EndsWith('\n'))
            _content.Append('\n');
    }

    public void AddFontResource(string resourceName, PdfRef fontRef)
    {
        var key = resourceName.StartsWith('/') ? resourceName[1..] : resourceName;
        _fonts[key] = fontRef;
        if (!_resources.ContainsKey("Font"))
            _resources["Font"] = _fonts;
    }

    public void AddXObjectResource(string resourceName, PdfRef xObjectRef)
    {
        var key = resourceName.StartsWith('/') ? resourceName[1..] : resourceName;
        _xObjects[key] = xObjectRef;
        if (!_resources.ContainsKey("XObject"))
            _resources["XObject"] = _xObjects;
    }

    public void AddExtGStateResource(string resourceName, PdfRef gsRef)
    {
        var key = resourceName.StartsWith('/') ? resourceName[1..] : resourceName;
        _extGStates[key] = gsRef;
        if (!_resources.ContainsKey("ExtGState"))
            _resources["ExtGState"] = _extGStates;
    }

    public void AddShadingResource(string resourceName, PdfRef shadingRef)
    {
        var key = resourceName.StartsWith('/') ? resourceName[1..] : resourceName;
        _shadings[key] = shadingRef;
        if (!_resources.ContainsKey("Shading"))
            _resources["Shading"] = _shadings;
    }

    public void AddAnnotation(PdfDictionary annotDict)
    {
        var annotObj = _document.Store.Add(annotDict);
        _annots.Add(annotObj.Reference);
        if (!PageObject.Dict.ContainsKey("Annots"))
            PageObject.Dict["Annots"] = _annots;
    }

    public void AddUriLink(double x1, double y1, double x2, double y2, string uri)
    {
        var annot = new PdfDictionary
        {
            ["Type"] = new PdfName("Annot"),
            ["Subtype"] = new PdfName("Link"),
            ["Rect"] = PdfArray.OfReals(x1, y1, x2, y2),
            ["Border"] = PdfArray.OfInts(0, 0, 0),
            ["A"] = new PdfDictionary
            {
                ["Type"] = new PdfName("Action"),
                ["S"] = new PdfName("URI"),
                ["URI"] = new PdfLiteralString(Encoding.UTF8.GetBytes(uri)),
            },
        };
        AddAnnotation(annot);
    }

    public void AddPageLink(double x1, double y1, double x2, double y2, PdfRef targetPageRef, double? pdfTop = null)
    {
        var dest = pdfTop.HasValue
            ? new PdfArray(targetPageRef, new PdfName("XYZ"), PdfNull.Instance, new PdfReal(pdfTop.Value), PdfNull.Instance)
            : new PdfArray(targetPageRef, new PdfName("Fit"));

        var annot = new PdfDictionary
        {
            ["Type"] = new PdfName("Annot"),
            ["Subtype"] = new PdfName("Link"),
            ["Rect"] = PdfArray.OfReals(x1, y1, x2, y2),
            ["Border"] = PdfArray.OfInts(0, 0, 0),
            ["Dest"] = dest,
        };
        AddAnnotation(annot);
    }

    internal void FinalizeContent(bool compress)
    {
        if (_finalized) return;
        _finalized = true;

        var bytes = Encoding.ASCII.GetBytes(_content.ToString());
        ContentObject.SetStream(bytes, compress: compress && bytes.Length > 0);
    }
}
