using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PrawnSharp.Fonts;

namespace PrawnSharp.Backend.PdfSharp;

/// <summary>Backend PDFsharp 6.2.4 (MIT). No actualizar a 6.5+ (AGPL).</summary>
public sealed class PdfSharpBackend : IPdfBackend
{
    private readonly PdfDocument _document = new();
    private readonly List<PdfSharpPage> _pages = [];

    public PdfSharpBackend()
    {
        _document.Info.Creator = "PrawnSharp";
    }

    public int PageCount => _pages.Count;

    public IFontMetrics GetFontMetrics(FontFace face)
    {
        ArgumentNullException.ThrowIfNull(face);

        if (face.FilePath is not null)
        {
            return new PdfSharpFontMetrics(face);
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

    public PdfMetadata Metadata { get; set; } = new();

    public PdfSecurityOptions? Security { get; set; }

    public bool Compress { get; set; } = true;

    public IPdfPage AddPage(PageSetup setup)
    {
        if (setup.Width <= 0 || setup.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(setup), "El tamaño de página debe ser positivo.");
        }

        var page = _document.AddPage();
        page.Width = XUnit.FromPoint(setup.Width);
        page.Height = XUnit.FromPoint(setup.Height);

        var wrapper = new PdfSharpPage(_document, page);
        _pages.Add(wrapper);
        return wrapper;
    }

    public void AddOutline(string title, int pageIndex, double? top = null, bool opened = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        if (pageIndex < 0 || pageIndex >= _document.PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        var page = _document.Pages[pageIndex];
        var outline = _document.Outlines.Add(title, page, opened);

        if (top is { } value)
        {
            outline.Top = page.Height.Point - value;
        }
    }

    public void Save(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);

        _document.Options.CompressContentStreams = Compress;
        _document.Info.Title = Metadata.Title ?? string.Empty;
        _document.Info.Author = Metadata.Author ?? string.Empty;
        _document.Info.Subject = Metadata.Subject ?? string.Empty;
        _document.Info.Keywords = Metadata.Keywords ?? string.Empty;
        _document.Info.Creator = Metadata.Creator ?? "PrawnSharp";

        if (Security is { } security)
        {
            var settings = _document.SecuritySettings;

            if (!string.IsNullOrEmpty(security.UserPassword))
            {
                settings.UserPassword = security.UserPassword;
            }

            if (!string.IsNullOrEmpty(security.OwnerPassword))
            {
                settings.OwnerPassword = security.OwnerPassword;
            }

            settings.PermitPrint = security.PermitPrint;
            settings.PermitFullQualityPrint = security.PermitFullQualityPrint;
            settings.PermitModifyDocument = security.PermitModifyDocument;
            settings.PermitExtractContent = security.PermitExtractContent;
            settings.PermitAnnotations = security.PermitAnnotations;
            settings.PermitFormsFill = security.PermitFormsFill;
            settings.PermitAssembleDocument = security.PermitAssembleDocument;
        }

        _document.Save(output, closeStream: false);
    }

    public void Dispose() => _document.Dispose();
}
