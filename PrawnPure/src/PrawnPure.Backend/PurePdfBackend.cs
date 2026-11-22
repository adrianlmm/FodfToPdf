using System.Security.Cryptography;
using PrawnPure.PdfCore.Document;
using PrawnPure.PdfCore.Images;
using PrawnPure.PdfCore.Objects;
using PrawnPure.PdfCore.Security;
using PrawnPure.TTFunk;
using PrawnSharp;
using PrawnSharp.Fonts;

namespace PrawnPure.Backend;

/// <summary>
/// Pure C# IPdfBackend for PrawnSharp backed by PrawnPure.PdfCore + PrawnPure.TTFunk
/// (zero PDFsharp, zero SkiaSharp, zero external PDF dependencies).
/// </summary>
public sealed class PurePdfBackend : IPdfBackend
{
    private readonly PdfCoreDocument _document = new();
    private readonly List<PurePdfPage> _pages = [];
    private readonly Dictionary<string, (string ResourceName, PdfIndirectObject FontObj, PdfTtfFont? TtfFont)> _sharedFonts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string ResourceName, PdfIndirectObject ImageObj)> _sharedImages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string ResourceName, PdfIndirectObject GsObj)> _sharedExtGStates = new(StringComparer.Ordinal);
    private int _fontSequence;
    private int _imageSequence;
    private int _gsSequence;
    private int _shadingSequence;

    public PurePdfBackend()
    {
        _document.Creator = "PrawnSharp";
        _document.Producer = "PrawnPure";
    }

    public int PageCount => _pages.Count;

    public PdfMetadata Metadata { get; set; } = new();

    public PdfSecurityOptions? Security { get; set; }

    public bool Compress { get; set; } = true;

    public IFontMetrics GetFontMetrics(FontFace face)
    {
        ArgumentNullException.ThrowIfNull(face);

        if (face.FilePath is not null)
        {
            if (!File.Exists(face.FilePath))
                throw new FileNotFoundException($"No existe el archivo de fuente '{face.FilePath}'.", face.FilePath);

            return new PureFontMetrics(face.FilePath);
        }

        if (!Base14Fonts.TryGetPostScriptName(face, out var postScriptName))
        {
            throw new UnsupportedFontException(
                $"La fuente '{face}' no es base-14 ni tiene un archivo TTF/OTF. " +
                "Regístrala con RegisterFontFamily antes de usarla.");
        }

        if (postScriptName is "Symbol" or "ZapfDingbats")
        {
            throw new UnsupportedFeatureException(
                $"Las métricas de '{postScriptName}' usan una codificación propia.");
        }

        return AfmFonts.Get(postScriptName);
    }

    public IPdfPage AddPage(PageSetup setup)
    {
        if (setup.Width <= 0 || setup.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(setup), "El tamaño de página debe ser positivo.");

        var corePage = _document.AddPage(setup.Width, setup.Height);
        var wrapper = new PurePdfPage(this, _document, corePage);
        _pages.Add(wrapper);
        return wrapper;
    }

    public void AddOutline(string title, int pageIndex, double? top = null, bool opened = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (pageIndex < 0 || pageIndex >= _pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));

        double? pdfTop = top is { } value ? _pages[pageIndex].Height - value : null;
        _document.AddOutline(title, pageIndex, pdfTop, opened);
    }

    public void Save(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);

        _document.Compress = Compress;
        _document.Title = Metadata.Title;
        _document.Author = Metadata.Author;
        _document.Subject = Metadata.Subject;
        _document.Keywords = Metadata.Keywords;
        _document.Creator = Metadata.Creator ?? "PrawnSharp";

        if (Security is { } sec)
        {
            var perms = PdfEncryption.BuildPermissions(
                permitPrint: sec.PermitPrint,
                permitFullQualityPrint: sec.PermitFullQualityPrint,
                permitModifyDocument: sec.PermitModifyDocument,
                permitExtractContent: sec.PermitExtractContent,
                permitAnnotations: sec.PermitAnnotations,
                permitFormsFill: sec.PermitFormsFill,
                permitAssembleDocument: sec.PermitAssembleDocument);

            _document.Encryption = new PdfEncryption(sec.UserPassword, sec.OwnerPassword, perms);
        }

        _document.Save(output);
    }

    public void Dispose()
    {
        // Pure managed resources; nothing unmanaged to release.
    }

    internal (string ResourceName, PdfIndirectObject FontObj, PdfTtfFont? TtfFont) GetOrRegisterFont(FontFace face)
    {
        var key = face.FilePath is not null
            ? "TTF:" + System.IO.Path.GetFullPath(face.FilePath)
            : "B14:" + face.Family + ":" + face.Style;

        if (_sharedFonts.TryGetValue(key, out var existing))
            return existing;

        var resName = $"/F{++_fontSequence}";

        if (face.FilePath is not null)
        {
            var ttfFile = TtfFile.Open(face.FilePath);
            var pdfTtf = new PdfTtfFont(_document, resName, ttfFile);
            var entry = (resName, pdfTtf.FontObject, (PdfTtfFont?)pdfTtf);
            _sharedFonts[key] = entry;
            return entry;
        }

        if (!Base14Fonts.TryGetPostScriptName(face, out var postScriptName))
        {
            throw new UnsupportedFontException(
                $"La fuente '{face}' no es base-14 ni tiene un archivo TTF/OTF.");
        }

        var fontObj = _document.Store.Add(new PdfDictionary
        {
            ["Type"] = new PdfName("Font"),
            ["Subtype"] = new PdfName("Type1"),
            ["BaseFont"] = new PdfName(postScriptName),
            ["Encoding"] = new PdfName("WinAnsiEncoding"),
        });

        var b14Entry = (resName, fontObj, (PdfTtfFont?)null);
        _sharedFonts[key] = b14Entry;
        return b14Entry;
    }

    internal (string ResourceName, PdfIndirectObject ImageObj) GetOrEmbedImage(ImageData image)
    {
        var hash = Convert.ToHexString(SHA256.HashData(image.Data));
        if (_sharedImages.TryGetValue(hash, out var existing))
            return existing;

        PdfIndirectObject imgObj;
        if (JpegImage.CanRender(image.Data))
        {
            imgObj = new JpegImage(image.Data).BuildPdfObject(_document);
        }
        else if (PngImage.CanRender(image.Data))
        {
            imgObj = new PngImage(image.Data).BuildPdfObject(_document);
        }
        else
        {
            throw new UnsupportedFeatureException("Sólo se soportan imágenes PNG y JPEG.");
        }

        var resName = $"/Im{++_imageSequence}";
        var entry = (resName, imgObj);
        _sharedImages[hash] = entry;
        return entry;
    }

    internal (string ResourceName, PdfIndirectObject GsObj) GetOrCreateExtGState(double fillAlpha, double strokeAlpha, BlendMode blendMode)
    {
        var fa = Math.Clamp(fillAlpha, 0, 1);
        var sa = Math.Clamp(strokeAlpha, 0, 1);
        var key = $"{fa:F4}:{sa:F4}:{blendMode}";
        if (_sharedExtGStates.TryGetValue(key, out var existing))
            return existing;

        var dict = new PdfDictionary
        {
            ["Type"] = new PdfName("ExtGState"),
            ["ca"] = new PdfReal(fa),
            ["CA"] = new PdfReal(sa),
        };
        if (blendMode != BlendMode.Normal)
            dict["BM"] = new PdfName(blendMode.ToString());

        var gsObj = _document.Store.Add(dict);
        var resName = $"/GS{++_gsSequence}";
        var entry = (resName, gsObj);
        _sharedExtGStates[key] = entry;
        return entry;
    }

    internal string NextShadingName() => $"/SH{++_shadingSequence}";

    internal PdfCorePage GetCorePage(int pageIndex) => _pages[pageIndex].CorePage;
}
