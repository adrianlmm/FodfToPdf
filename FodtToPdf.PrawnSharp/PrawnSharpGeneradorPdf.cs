using System.Globalization;
using System.Numerics;
using PrawnPure.Backend;
using PrawnSharp;

namespace FodtToPdf;

/// <summary>
/// Motor de maquetación y renderizado ODF sobre PrawnSharp + PrawnPure (sin PDFsharp ni SkiaSharp).
/// Controla directamente la cuantización en twips, la posición exacta de la línea base,
/// el corte de línea con kerning TTF, los bordes colapsados y la división de filas entre páginas.
/// </summary>
internal sealed class PrawnSharpGeneradorPdf
{
    private readonly ModeloPlantilla _modelo;
    private readonly bool _dibujarTablasAnidadas;
    private readonly PurePdfBackend _backend;
    private readonly Dictionary<FontFace, IFontMetrics> _metricsCache = new();
    private readonly Dictionary<string, ImageData> _imageCache = new(StringComparer.Ordinal);
    private readonly List<PaginaEstado> _paginas = [];
    private readonly Dictionary<int, (PaginaEstado Pagina, double X, double Y)> _anclasBloque = new();
    private readonly HashSet<CapaModelo> _capasFondoDibujadas = [];

    private PaginaEstado _paginaActual;
    private double _cursorY;
    private double _areaIzq;
    private double _areaDer;
    private double _areaSup;
    private double _areaInf;
    private string _maestraActual = "";
    private bool _saltoExplicitoPagina;

    private sealed class PaginaEstado
    {
        public IPdfPage Page { get; init; }
        public PaginaModelo Layout { get; init; }
        public string Maestra { get; init; } = "";
        public int NumeroPagina { get; init; }
    }

    private sealed class SegmentoLinea
    {
        public RunModelo Run { get; init; }
        public string Texto { get; init; } = "";
        public ResolvedFont Fuente { get; init; }
        public double TamanoPt { get; init; }
        public double AnchoPt { get; init; }
        public bool EsEspacio { get; init; }
        public bool EsTab { get; init; }
        public bool PermiteCorteDespues { get; init; }
        public double TabDestinoX { get; init; }
    }

    private sealed class LineaFormateada
    {
        public List<SegmentoLinea> Segmentos { get; } = [];
        public double AnchoPt { get; set; }
        public double AltoPt { get; set; }
        public double AltoNaturalPt { get; set; }
        public double BaselineOffsetPt { get; set; }
        public double SangriaIzqPt { get; set; }
        public bool EsUltimaDeParrafo { get; set; }
        public bool EsPrimeraDeParrafo { get; set; }
    }

    private sealed class ParrafoMedido
    {
        public ParrafoModelo Parrafo { get; init; }
        public List<LineaFormateada> Lineas { get; } = [];
        public SegmentoLinea MarcaLista { get; set; }
        public double AltoMarcaPt { get; set; }
        public double MargenSupPt { get; set; }
        public double MargenInfPt { get; set; }
        public double AltoImagenPt { get; set; }
        public double DescensoImagenPt { get; set; }
        public double AnchoImagenPt { get; set; }
        public ImageData Imagen { get; set; }
        public double BordeVertPt => Parrafo.BordeParrafo ? Math.Max(Parrafo.GrosorBordeParrafoPt, 0.5) : 0;
        public double AltoContenidoPt =>
            Lineas.Sum(l => l.AltoPt) + AltoImagenPt + DescensoImagenPt +
            Parrafo.PaddingSupParrafoPt + Parrafo.PaddingInfParrafoPt + 2.0 * BordeVertPt;
        public double AltoTotalPt => MargenSupPt + AltoContenidoPt + MargenInfPt;
    }

    private PrawnSharpGeneradorPdf(ModeloPlantilla modelo, bool dibujarTablasAnidadas)
    {
        _modelo = modelo;
        _dibujarTablasAnidadas = dibujarTablasAnidadas;
        _backend = new PurePdfBackend { Compress = true };
    }

    public static byte[] Generar(ModeloPlantilla modelo, bool dibujarTablasAnidadas = true)
    {
        var gen = new PrawnSharpGeneradorPdf(modelo, dibujarTablasAnidadas);
        return gen.Renderizar();
    }

    private byte[] Renderizar()
    {
        try
        {
            AbrirPagina(_modelo.Pagina, "");

            var i = 0;
            while (i < _modelo.Flujo.Count)
            {
                var bloque = _modelo.Flujo[i];
                var sec = (bloque.Seccion >= 0 && bloque.Seccion < _modelo.Secciones.Count)
                    ? _modelo.Secciones[bloque.Seccion]
                    : null;

                if (sec != null && sec.Columnas > 1)
                {
                    var inicio = i;
                    var idSec = bloque.Seccion;
                    while (i < _modelo.Flujo.Count && _modelo.Flujo[i].Seccion == idSec)
                        i++;
                    RenderizarSeccionMulticolumna(sec, inicio, i);
                    continue;
                }

                RenderizarBloqueFlujo(i, _areaIzq, _areaDer - _areaIzq, permitirSaltoPagina: true);
                i++;
            }

            // Encabezados, pies (con total de páginas exacto) y capas flotantes de primer plano
            DibujarEncabezadosYPies();
            DibujarCapasPrimerPlano();

            using var ms = new MemoryStream();
            _backend.Save(ms);
            return ms.ToArray();
        }
        finally
        {
            _backend.Dispose();
        }
    }

    // -------------------------------------------------------------------------
    // Gestión de páginas, encabezados, pies y capas de fondo
    // -------------------------------------------------------------------------

    private PaginaMaestraModelo MaestraDe(string maestra)
    {
        if (!string.IsNullOrEmpty(maestra) && _modelo.PaginasMaestras.TryGetValue(maestra, out var pm))
            return pm;
        return null;
    }

    private PaginaModelo LayoutDeMaestra(string maestra) => MaestraDe(maestra)?.Pagina ?? _modelo.Pagina;

    private void AbrirPagina(PaginaModelo layout, string maestra)
    {
        var page = _backend.AddPage(new PageSetup(new Size(layout.AnchoPt, layout.AltoPt)));
        var estado = new PaginaEstado
        {
            Page = page,
            Layout = layout,
            Maestra = maestra ?? "",
            NumeroPagina = _paginas.Count + 1,
        };
        _paginas.Add(estado);
        _paginaActual = estado;
        _maestraActual = estado.Maestra;
        _areaIzq = layout.MargenIzqPt;
        _areaDer = layout.AnchoPt - layout.MargenDerPt;
        var altoEncReal = CalcularAltoEncabezadoReal(layout, estado.Maestra);
        var reservaEnc = Math.Max(layout.MargenSupPt, layout.MargenSupBasePt + altoEncReal);
        _areaSup = reservaEnc;
        var altoPieReal = CalcularAltoPieReal(layout, estado.Maestra);
        var reservaPie = Math.Max(layout.MargenInfPt, layout.MargenInfBasePt + altoPieReal);
        _areaInf = layout.AltoPt - reservaPie;
        _cursorY = _areaSup;

        // Capas de fondo (marcas de agua, imágenes detrás del texto) en cada página nueva
        DibujarFondoPagina(estado);
    }

    private double CalcularAltoEncabezadoReal(PaginaModelo layout, string nombreMaestra)
    {
        var maestra = MaestraDe(nombreMaestra);
        var enc = maestra?.Encabezado is { Count: > 0 } encM ? encM : _modelo.Encabezado;
        if (enc == null || enc.Count == 0) return 0;

        var ancho = Math.Max(layout.AnchoPt - layout.MargenIzqPt - layout.MargenDerPt, 20);
        double total = 0;
        for (var i = 0; i < enc.Count; i++)
        {
            var prev = i > 0 ? enc[i - 1] : null;
            var sig = i + 1 < enc.Count ? enc[i + 1] : null;
            total += MedirParrafo(enc[i], ancho, prev, sig, enCelda: false, 1, 1).AltoTotalPt;
        }
        return total;
    }

    private double CalcularAltoPieReal(PaginaModelo layout, string nombreMaestra)
    {
        var maestra = MaestraDe(nombreMaestra);
        var pie = maestra?.Pie is { Count: > 0 } pieM ? pieM : _modelo.Pie;
        if (pie == null || pie.Count == 0) return 0;

        var ancho = Math.Max(layout.AnchoPt - layout.MargenIzqPt - layout.MargenDerPt, 20);
        double total = 0;
        for (var i = 0; i < pie.Count; i++)
        {
            var prev = i > 0 ? pie[i - 1] : null;
            var sig = i + 1 < pie.Count ? pie[i + 1] : null;
            total += MedirParrafo(pie[i], ancho, prev, sig, enCelda: false, 1, 1).AltoTotalPt;
        }
        return total;
    }

    private void NuevaPagina(string maestraOpcional = null)
    {
        var maestra = !string.IsNullOrEmpty(maestraOpcional) ? maestraOpcional : _maestraActual;
        AbrirPagina(LayoutDeMaestra(maestra), maestra);
    }

    private void DibujarFondoPagina(PaginaEstado estado)
    {
        var maestra = MaestraDe(estado.Maestra);
        var marcasTexto = maestra?.MarcasAguaTexto is { Count: > 0 } mt ? mt : _modelo.MarcasAguaTexto;
        var marcasImg = maestra?.MarcasAgua is { Count: > 0 } mi ? mi : _modelo.MarcasAgua;

        // Marcas de agua de texto (draw:text-path="true" / Fontwork, p.ej. "COPIA NO VALIDA"):
        // LibreOffice estira los glifos (en peso normal) para llenar exactamente la caja (AnchoPt x AltoPt)
        // posicionada según HorizontalPos/VerticalPos y HorizontalRel/VerticalRel.
        var layout = estado.Layout;
        foreach (var marca in marcasTexto)
        {
            if (string.IsNullOrWhiteSpace(marca.Texto)) continue;

            var contenidoAncho = Math.Max(layout.AnchoPt - layout.MargenIzqPt - layout.MargenDerPt, 10);
            var contenidoAlto = Math.Max(layout.AltoPt - layout.MargenSupBasePt - layout.MargenInfBasePt, 10);
            var wCaja = marca.AnchoPt > 1 ? marca.AnchoPt : contenidoAncho;
            var hCaja = marca.AltoPt > 1 ? marca.AltoPt : 66;

            double x;
            if (string.Equals(marca.HorizontalPos, "center", StringComparison.OrdinalIgnoreCase))
            {
                var relPage = string.Equals(marca.HorizontalRel, "page", StringComparison.OrdinalIgnoreCase);
                x = relPage
                    ? (layout.AnchoPt - wCaja) / 2.0
                    : layout.MargenIzqPt + (contenidoAncho - wCaja) / 2.0;
            }
            else if (string.Equals(marca.HorizontalPos, "right", StringComparison.OrdinalIgnoreCase))
            {
                x = layout.AnchoPt - layout.MargenDerPt - wCaja;
            }
            else
            {
                x = marca.XPt;
                if (marca.HorizontalRel is "page-content" or "paragraph" or "paragraph-content")
                    x += layout.MargenIzqPt;
            }

            double y;
            if (string.Equals(marca.VerticalPos, "middle", StringComparison.OrdinalIgnoreCase))
            {
                var relPage = string.Equals(marca.VerticalRel, "page", StringComparison.OrdinalIgnoreCase);
                y = relPage
                    ? (layout.AltoPt - hCaja) / 2.0
                    : layout.MargenSupBasePt + (contenidoAlto - hCaja) / 2.0;
            }
            else if (string.Equals(marca.VerticalPos, "bottom", StringComparison.OrdinalIgnoreCase))
            {
                y = layout.AltoPt - layout.MargenInfBasePt - hCaja;
            }
            else
            {
                y = marca.YPt;
                if (marca.VerticalRel is "page-content" or "paragraph" or "paragraph-content")
                    y += layout.MargenSupBasePt;
            }

            // Helvetica Base-14 (peso regular) permite emitir HorizontalScaling (Tz) vectorial exacto
            // en el flujo PDF para estirar el texto al ancho y alto exactos del Fontwork.
            var baseFace = FontFace.Base14("Helvetica", FontStyle.Regular);
            var handle = estado.Page.RegisterFont(baseFace);
            var color = ParseColor(marca.Color ?? "#a5a5a5");
            var met = GetMetrics(baseFace);

            // Cap-height de Helvetica es 0.718 * em; así el trazo llena exactamente hCaja.
            var tam = Math.Max(hCaja / 0.718, 12);
            var anchoNatural = met.WidthOf(marca.Texto, tam, kerning: false);
            var escalaX = anchoNatural > 0.1 ? wCaja / anchoNatural : 1.0;
            var baseline = y + hCaja;

            estado.Page.DrawText(
                new TextRun(marca.Texto, handle, tam, color, HorizontalScaling: escalaX, Kerning: false),
                new Point(x, baseline));
        }

        // Marcas de agua de imagen del encabezado del master-page
        foreach (var marca in marcasImg)
            DibujarCapa(estado, marca, esFondo: true);

        // Imágenes de marca de agua en el fondo de cada página
        foreach (var capa in _modelo.Capas)
        {
            if (capa.EnFlujo || capa.AnclaEnTabla) continue;
            if (capa.EsNumeroPaginaActual) continue;
            if (!string.Equals(capa.ModoColor, "watermark", StringComparison.OrdinalIgnoreCase)) continue;

            DibujarCapa(estado, capa, esFondo: true);
        }
    }

    private void DibujarCapasPrimerPlano()
    {
        if (_paginas.Count == 0) return;

        foreach (var capa in _modelo.Capas)
        {
            if (capa.EnFlujo || capa.AnclaEnTabla) continue;
            if (capa.EsNumeroPaginaActual) continue;
            if (_capasFondoDibujadas.Contains(capa)) continue;
            if (string.Equals(capa.ModoColor, "watermark", StringComparison.OrdinalIgnoreCase)) continue;

            var tieneTexto = capa.Parrafos.Any(p => p.Runs.Any(r => !string.IsNullOrWhiteSpace(r.Texto)));
            var tieneContenido = tieneTexto || !string.IsNullOrEmpty(capa.ImagenBase64) ||
                                 capa.Tablas.Count > 0 || !string.IsNullOrEmpty(capa.ColorRelleno) ||
                                 (!string.IsNullOrEmpty(capa.ColorTrazo) && capa.AltoPt <= 2);

            // Omitir cajas de texto vacías que sólo contienen un bookmark (p.ej. Marco1 QR vacío en dividendos)
            if (!tieneContenido) continue;

            var destino = _paginas[0];
            var capaEfectiva = capa;
            if (!capa.Ancla.Contains("calibrada") && capa.BloqueAncla >= 0 &&
                _anclasBloque.TryGetValue(capa.BloqueAncla, out var ancla))
            {
                destino = ancla.Pagina;
                capaEfectiva = new CapaModelo
                {
                    Nombre = capa.Nombre,
                    Tipo = capa.Tipo,
                    XPt = capa.HorizontalRel is "paragraph" or "paragraph-content"
                        ? ancla.X + capa.XPt
                        : capa.HorizontalRel == "page-content"
                            ? ancla.Pagina.Layout.MargenIzqPt + capa.XPt
                            : capa.XPt,
                    YPt = ancla.Y + capa.YPt,
                    AnchoPt = capa.AnchoPt,
                    AltoPt = capa.AltoPt,
                    Ancla = capa.Ancla + " calibrada",
                    Parrafos = capa.Parrafos,
                    Tablas = capa.Tablas,
                    ImagenBase64 = capa.ImagenBase64,
                    ColorRelleno = capa.ColorRelleno,
                    ColorTrazo = capa.ColorTrazo,
                    GrosorTrazoPt = capa.GrosorTrazoPt,
                    BordeCaja = capa.BordeCaja,
                    ColorBorde = capa.ColorBorde,
                    GrosorBordePt = capa.GrosorBordePt,
                    PaddingIzqPt = capa.PaddingIzqPt,
                    PaddingDerPt = capa.PaddingDerPt,
                    PaddingSupPt = capa.PaddingSupPt,
                    PaddingInfPt = capa.PaddingInfPt,
                };
            }

            DibujarCapa(destino, capaEfectiva, esFondo: false);
        }
    }

    private void DibujarCapa(PaginaEstado estado, CapaModelo capa, bool esFondo)
    {
        var layout = estado.Layout;
        var x = capa.XPt;
        var y = capa.YPt;

        var xPosExplicit = false;
        var yPosExplicit = false;

        var wCaja = capa.AnchoPt;
        var hCaja = capa.AltoPt;
        // Marcos de ancho automático (p.ej. número de página en pie con fo:min-width sin svg:width):
        // calcular un ancho mínimo basado en su contenido para que horizontal-pos="right"/"center" funcione.
        if (wCaja < 1 && capa.Parrafos.Count > 0)
        {
            var maxW = 0.0;
            foreach (var pAuto in capa.Parrafos)
            {
                var pmAuto = MedirParrafo(pAuto, Math.Max(layout.AnchoPt - layout.MargenIzqPt - layout.MargenDerPt, 20),
                    previo: null, siguiente: null, enCelda: false, estado.NumeroPagina, _paginas.Count);
                foreach (var lin in pmAuto.Lineas)
                    if (lin.AnchoPt > maxW) maxW = lin.AnchoPt;
            }
            wCaja = Math.Max(maxW + capa.PaddingIzqPt + capa.PaddingDerPt + 2.0, 12.0);
        }

        if (string.Equals(capa.HorizontalPos, "center", StringComparison.OrdinalIgnoreCase))
        {
            var relPage = string.Equals(capa.HorizontalRel, "page", StringComparison.OrdinalIgnoreCase);
            x = relPage
                ? (layout.AnchoPt - wCaja) / 2.0
                : layout.MargenIzqPt + (layout.AnchoPt - layout.MargenIzqPt - layout.MargenDerPt - wCaja) / 2.0;
            xPosExplicit = true;
        }
        else if (string.Equals(capa.HorizontalPos, "right", StringComparison.OrdinalIgnoreCase))
        {
            var relPage = string.Equals(capa.HorizontalRel, "page", StringComparison.OrdinalIgnoreCase);
            x = relPage
                ? layout.AnchoPt - wCaja
                : layout.AnchoPt - layout.MargenDerPt - wCaja;
            xPosExplicit = true;
        }
        if (string.Equals(capa.VerticalPos, "middle", StringComparison.OrdinalIgnoreCase))
        {
            var relPage = string.Equals(capa.VerticalRel, "page", StringComparison.OrdinalIgnoreCase);
            y = relPage
                ? (layout.AltoPt - hCaja) / 2.0
                : layout.MargenSupBasePt + (layout.AltoPt - layout.MargenSupBasePt - layout.MargenInfBasePt - hCaja) / 2.0;
            yPosExplicit = true;
        }

        // Si es relativo a paragraph/char y no calibrada (y no se centró/alineó explícitamente), trasladar al área de texto o pie
        if (!xPosExplicit && !capa.Ancla.Contains("calibrada") &&
            (capa.HorizontalRel is "paragraph" or "paragraph-content" or "page-content"))
            x += layout.MargenIzqPt;
        if (!yPosExplicit && !capa.Ancla.Contains("calibrada"))
        {
            if (capa.EnPie && (capa.VerticalRel is "paragraph" or "paragraph-content" or "page-content" or ""))
            {
                y += layout.AltoPt - layout.MargenInfBasePt - Math.Max(layout.AltoPiePt, hCaja);
            }
            else if ((capa.VerticalRel is "paragraph" or "paragraph-content" or "page-content") &&
                     y < layout.MargenSupPt)
            {
                y += layout.MargenSupPt;
            }
        }

        if (wCaja <= 0.1 && hCaja <= 0.1) return;

        var rect = new Rect(x, y, Math.Max(wCaja, 0.5), Math.Max(hCaja, 0.5));

        if (!string.IsNullOrEmpty(capa.ColorRelleno))
        {
            estado.Page.DrawRectangle(rect, Paint.Fill(ParseColor(capa.ColorRelleno)), null);
        }

        if (!string.IsNullOrEmpty(capa.ImagenBase64))
        {
            var img = TryDecodeImage(capa.ImagenBase64);
            if (img != null)
            {
                estado.Page.DrawImage(img, rect);
                // draw:color-mode="watermark": LibreOffice aplica washout (~78 % hacia blanco).
                // XGraphics.DrawImage no hereda SetAlpha externo, así que se aplica un velo blanco
                // semitransparente vía DrawPath (/ExtGState /ca) sobre el fondo antes del texto.
                if (esFondo && string.Equals(capa.ModoColor, "watermark", StringComparison.OrdinalIgnoreCase))
                {
                    var velo = PrawnSharp.Path.Create().Rectangle(rect.X, rect.Y, rect.Width, rect.Height);
                    estado.Page.DrawPath(velo, Paint.Solid(PdfColor.White, 0.82), null);
                }
            }
        }

        if (capa.BordeCaja)
        {
            var stroke = StrokeStyle.Create(ParseColor(capa.ColorBorde ?? "#000000"), Math.Max(capa.GrosorBordePt, 0.5));
            estado.Page.DrawRectangle(rect, null, stroke);
        }

        if (!string.IsNullOrEmpty(capa.ColorTrazo) && capa.AltoPt <= 2)
        {
            var stroke = StrokeStyle.Create(ParseColor(capa.ColorTrazo), Math.Max(capa.GrosorTrazoPt, 0.5));
            estado.Page.DrawLine(new Point(x, y), new Point(x + capa.AnchoPt, y), stroke);
        }

        var innerX = x + capa.PaddingIzqPt;
        var innerW = Math.Max(wCaja - capa.PaddingIzqPt - capa.PaddingDerPt, 10);
        var innerY = y + capa.PaddingSupPt;

        foreach (var p in capa.Parrafos)
        {
            var pm = MedirParrafo(p, innerW, previo: null, siguiente: null, enCelda: false, estado.NumeroPagina, _paginas.Count);
            innerY += pm.MargenSupPt;
            DibujarParrafoEnPagina(estado.Page, pm, innerX, innerW, innerY, estado.NumeroPagina, _paginas.Count, capa.ColorRelleno);
            innerY += pm.AltoContenidoPt + pm.MargenInfPt;
        }

        foreach (var t in capa.Tablas)
        {
            innerY = DibujarTablaFijaEnPagina(estado.Page, t, innerX, innerY, innerW, estado.NumeroPagina, _paginas.Count);
        }
    }

    private void DibujarEncabezadosYPies()
    {
        var totalPaginas = _paginas.Count;
        foreach (var estado in _paginas)
        {
            var layout = estado.Layout;
            var ancho = layout.AnchoPt - layout.MargenIzqPt - layout.MargenDerPt;
            var maestra = MaestraDe(estado.Maestra);

            var encabezado = maestra?.Encabezado is { Count: > 0 } encM ? encM : _modelo.Encabezado;

            if (encabezado != null && encabezado.Count > 0)
            {
                var y = layout.MargenSupBasePt;
                for (var ei = 0; ei < encabezado.Count; ei++)
                {
                    var prev = ei > 0 ? encabezado[ei - 1] : null;
                    var sig = ei + 1 < encabezado.Count ? encabezado[ei + 1] : null;
                    var pm = MedirParrafo(encabezado[ei], ancho, prev, sig, enCelda: false, estado.NumeroPagina, totalPaginas);
                    y += pm.MargenSupPt;
                    DibujarParrafoEnPagina(estado.Page, pm, layout.MargenIzqPt, ancho, y, estado.NumeroPagina, totalPaginas);
                    y += pm.AltoContenidoPt + pm.MargenInfPt;
                }
            }

            var pie = maestra?.Pie is { Count: > 0 } pieM ? pieM : _modelo.Pie;

            if (pie != null && pie.Count > 0)
            {
                var medidos = new List<ParrafoMedido>();
                for (var pi = 0; pi < pie.Count; pi++)
                {
                    var prev = pi > 0 ? pie[pi - 1] : null;
                    var sig = pi + 1 < pie.Count ? pie[pi + 1] : null;
                    medidos.Add(MedirParrafo(pie[pi], ancho, prev, sig, enCelda: false, estado.NumeroPagina, totalPaginas));
                }
                var altoPie = medidos.Sum(m => m.AltoTotalPt);
                var y = layout.AltoPt - layout.MargenInfBasePt - altoPie;
                foreach (var pm in medidos)
                {
                    y += pm.MargenSupPt;
                    DibujarParrafoEnPagina(estado.Page, pm, layout.MargenIzqPt, ancho, y, estado.NumeroPagina, totalPaginas);
                    y += pm.AltoContenidoPt + pm.MargenInfPt;
                }
            }

            var numerosPagina = maestra?.NumerosPagina is { Count: > 0 } npM ? npM : _modelo.NumerosPagina;
            foreach (var np in numerosPagina)
            {
                DibujarCapa(estado, np, esFondo: false);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Flujo principal y secciones multicolumna
    // -------------------------------------------------------------------------

    private void RenderizarSeccionMulticolumna(SeccionModelo sec, int inicio, int fin)
    {
        var anchoTotal = _areaDer - _areaIzq;
        var relSum = sec.RelAnchos.Sum();
        if (relSum <= 0) relSum = sec.Columnas;

        var colX = new double[sec.Columnas];
        var colW = new double[sec.Columnas];
        var xAct = _areaIzq;
        for (var c = 0; c < sec.Columnas; c++)
        {
            var rel = c < sec.RelAnchos.Count && sec.RelAnchos[c] > 0 ? sec.RelAnchos[c] : 1;
            var gap = c < sec.SeparacionPt.Count ? sec.SeparacionPt[c] : 0;
            var mIzq = c < sec.MargenIzqColPt.Count ? sec.MargenIzqColPt[c] : 0;
            var mDer = c < sec.MargenDerColPt.Count ? sec.MargenDerColPt[c] : 0;
            var wBruto = anchoTotal * (rel / relSum);
            colX[c] = xAct + mIzq;
            colW[c] = Math.Max(wBruto - mIzq - mDer - (c < sec.Columnas - 1 ? gap / 2 : 0), 10);
            xAct += wBruto;
        }

        // Repartir bloques entre columnas según fo:break-before="column"
        var yInicioSec = _cursorY;
        var yMaxSec = yInicioSec;
        var colActual = 0;

        for (var idx = inicio; idx < fin; idx++)
        {
            var b = _modelo.Flujo[idx];
            if (b.Parrafo?.SaltoColumnaAntes == true && colActual + 1 < sec.Columnas)
            {
                yMaxSec = Math.Max(yMaxSec, _cursorY);
                colActual++;
                _cursorY = yInicioSec;
            }

            RenderizarBloqueFlujo(idx, colX[colActual], colW[colActual], permitirSaltoPagina: false);
            yMaxSec = Math.Max(yMaxSec, _cursorY);
        }

        _cursorY = yMaxSec;
    }

    private void RenderizarBloqueFlujo(int indice, double xBase, double ancho, bool permitirSaltoPagina)
    {
        var bloque = _modelo.Flujo[indice];

        // Cambio de página maestra o salto explícito antes del bloque
        if (permitirSaltoPagina && bloque.Parrafo != null)
        {
            var cambiaMaestra = !string.IsNullOrEmpty(bloque.Parrafo.PaginaMaestra) &&
                                !string.Equals(bloque.Parrafo.PaginaMaestra, _maestraActual, StringComparison.Ordinal);

            if ((bloque.Parrafo.SaltoPaginaAntes || cambiaMaestra) && _cursorY > _areaSup + 0.5)
            {
                NuevaPagina(cambiaMaestra ? bloque.Parrafo.PaginaMaestra : null);
                _saltoExplicitoPagina = true;
                if (cambiaMaestra)
                {
                    xBase = _areaIzq;
                    ancho = _areaDer - _areaIzq;
                }
            }
            else if (cambiaMaestra && _paginas.Count == 1 && _cursorY <= _areaSup + 0.5)
            {
                // Primera página cambia a maestra explícita si aún está vacía
                _maestraActual = bloque.Parrafo.PaginaMaestra;
            }
        }

        // Capas ancladas a este bloque con style:run-through="background" (p.ej. escudo SHCP en AcuseCancelacion):
        // se pintan ANTES del texto del bloque para quedar detrás del flujo, nunca tapando las letras.
        foreach (var capaBg in _modelo.Capas)
        {
            if (capaBg.EnFlujo || capaBg.AnclaEnTabla || capaBg.EsNumeroPaginaActual) continue;
            if (_capasFondoDibujadas.Contains(capaBg)) continue;
            if (string.Equals(capaBg.ModoColor, "watermark", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(capaBg.RunThrough, "background", StringComparison.OrdinalIgnoreCase)) continue;
            if (capaBg.BloqueAncla != indice) continue;

            var capaEf = new CapaModelo
            {
                Nombre = capaBg.Nombre,
                Tipo = capaBg.Tipo,
                XPt = capaBg.HorizontalRel is "paragraph" or "paragraph-content" or "page-content"
                    ? _paginaActual.Layout.MargenIzqPt + capaBg.XPt
                    : capaBg.XPt,
                YPt = _cursorY + capaBg.YPt,
                AnchoPt = capaBg.AnchoPt,
                AltoPt = capaBg.AltoPt,
                Ancla = capaBg.Ancla + " calibrada",
                Parrafos = capaBg.Parrafos,
                Tablas = capaBg.Tablas,
                ImagenBase64 = capaBg.ImagenBase64,
                ColorRelleno = capaBg.ColorRelleno,
                ColorTrazo = capaBg.ColorTrazo,
                GrosorTrazoPt = capaBg.GrosorTrazoPt,
                BordeCaja = capaBg.BordeCaja,
                ColorBorde = capaBg.ColorBorde,
                GrosorBordePt = capaBg.GrosorBordePt,
                PaddingIzqPt = capaBg.PaddingIzqPt,
                PaddingDerPt = capaBg.PaddingDerPt,
                PaddingSupPt = capaBg.PaddingSupPt,
                PaddingInfPt = capaBg.PaddingInfPt,
            };
            DibujarCapa(_paginaActual, capaEf, esFondo: true);
            _capasFondoDibujadas.Add(capaBg);
        }

        if (bloque.Tipo == "espacio")
        {
            _anclasBloque[indice] = (_paginaActual, xBase, _cursorY);
            var altoLinea = Math.Max(bloque.AltoPt, 0.05);
            double mSup = 0, mInf = 0;
            if (bloque.Parrafo is ParrafoModelo pe)
            {
                var prevP = indice > 0 ? _modelo.Flujo[indice - 1].Parrafo : null;
                var sigP = indice + 1 < _modelo.Flujo.Count ? _modelo.Flujo[indice + 1].Parrafo : null;
                var mismoPrev = prevP != null && pe.Contextual && prevP.Contextual &&
                    !string.IsNullOrEmpty(pe.EstiloComun) &&
                    string.Equals(pe.EstiloComun, prevP.EstiloComun, StringComparison.Ordinal);
                var mismoSig = sigP != null && pe.Contextual && sigP.Contextual &&
                    !string.IsNullOrEmpty(pe.EstiloComun) &&
                    string.Equals(pe.EstiloComun, sigP.EstiloComun, StringComparison.Ordinal);
                mSup = mismoPrev ? 0 : Math.Max(pe.MargenSupPt, 0);
                mInf = mismoSig ? 0 : Math.Max(pe.MargenInfPt, 0);
                altoLinea += pe.PaddingSupParrafoPt + pe.PaddingInfParrafoPt;
                if (pe.BordeParrafo)
                    altoLinea += 2.0 * Math.Max(pe.GrosorBordeParrafoPt, 0.5);
                else if (pe.BordeInferiorParrafo)
                    altoLinea += Math.Max(pe.GrosorBordeParrafoPt, 0.5);
            }
            var mSupEf = (_paginas.Count == 1 || _cursorY > _areaSup + 0.5 || _saltoExplicitoPagina) ? mSup : 0;
            _saltoExplicitoPagina = false;
            var alto = mSupEf + altoLinea + mInf;
            if (permitirSaltoPagina && _cursorY + mSupEf + altoLinea > _areaInf + 0.05 && _cursorY > _areaSup + 0.5)
            {
                NuevaPagina();
                // En Writer, un párrafo vacío que no cabe al pie de página pasa al inicio
                // de la página siguiente (colapsando su margin-top, pero conservando su alto y margin-bottom).
                _cursorY += altoLinea + mInf;
            }
            else
            {
                _cursorY += alto;
            }
            return;
        }

        if (bloque.Tipo == "tabla" && bloque.Tabla != null)
        {
            // Capas de fondo (style:run-through="background") ancladas justo después de esta tabla con
            // svg:y negativo (p.ej. Imagen1 en formatoDividendos): suben por detrás de la tabla, así que
            // se calcula la altura de la tabla y se pintan ANTES para que queden debajo del texto.
            var capasFondoSobreTabla = _modelo.Capas
                .Where(c => !c.EnFlujo && !c.AnclaEnTabla && !c.EsNumeroPaginaActual &&
                            !_capasFondoDibujadas.Contains(c) &&
                            !string.Equals(c.ModoColor, "watermark", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(c.RunThrough, "background", StringComparison.OrdinalIgnoreCase) &&
                            c.YPt < -0.5 && c.BloqueAncla == indice + 1)
                .ToList();

            if (capasFondoSobreTabla.Count > 0 && bloque.Tabla.Filas.Count > 0)
            {
                var (_, _, _, rhPre, extSupPre, extInfPre) =
                    PrepararTabla(bloque.Tabla, xBase, ancho, esFlujo: true, _paginaActual.NumeroPagina, 0);
                var yAnclaSig = _cursorY + extSupPre + rhPre.Sum() + extInfPre;

                foreach (var capaBg in capasFondoSobreTabla)
                {
                    var capaEf = new CapaModelo
                    {
                        Nombre = capaBg.Nombre,
                        Tipo = capaBg.Tipo,
                        XPt = capaBg.HorizontalRel is "paragraph" or "paragraph-content"
                            ? xBase + capaBg.XPt
                            : capaBg.HorizontalRel == "page-content"
                                ? _paginaActual.Layout.MargenIzqPt + capaBg.XPt
                                : capaBg.XPt,
                        YPt = yAnclaSig + capaBg.YPt,
                        AnchoPt = capaBg.AnchoPt,
                        AltoPt = capaBg.AltoPt,
                        Ancla = capaBg.Ancla + " calibrada",
                        Parrafos = capaBg.Parrafos,
                        Tablas = capaBg.Tablas,
                        ImagenBase64 = capaBg.ImagenBase64,
                        ColorRelleno = capaBg.ColorRelleno,
                        ColorTrazo = capaBg.ColorTrazo,
                        GrosorTrazoPt = capaBg.GrosorTrazoPt,
                        BordeCaja = capaBg.BordeCaja,
                        ColorBorde = capaBg.ColorBorde,
                        GrosorBordePt = capaBg.GrosorBordePt,
                        PaddingIzqPt = capaBg.PaddingIzqPt,
                        PaddingDerPt = capaBg.PaddingDerPt,
                        PaddingSupPt = capaBg.PaddingSupPt,
                        PaddingInfPt = capaBg.PaddingInfPt,
                    };
                    DibujarCapa(_paginaActual, capaEf, esFondo: true);
                    _capasFondoDibujadas.Add(capaBg);
                }
            }

            RenderizarTablaFlujo(bloque.Tabla, xBase, ancho, permitirSaltoPagina);

            // Capas ancladas dentro de esta tabla: dibujarlas justo después
            foreach (var capa in _modelo.Capas.Where(c => c.AnclaEnTabla && c.BloqueAncla == indice))
            {
                var yCapa = Math.Max(_cursorY + capa.YPt - capa.AltoParrafoAnclaPt, _areaSup);
                var copia = new CapaModelo
                {
                    Nombre = capa.Nombre,
                    Tipo = capa.Tipo,
                    XPt = capa.XPt,
                    YPt = yCapa,
                    AnchoPt = capa.AnchoPt,
                    AltoPt = capa.AltoPt,
                    Ancla = capa.Ancla + " calibrada",
                    Parrafos = capa.Parrafos,
                    Tablas = capa.Tablas,
                    ImagenBase64 = capa.ImagenBase64,
                    ColorRelleno = capa.ColorRelleno,
                    BordeCaja = capa.BordeCaja,
                    ColorBorde = capa.ColorBorde,
                    GrosorBordePt = capa.GrosorBordePt,
                    PaddingIzqPt = capa.PaddingIzqPt,
                    PaddingDerPt = capa.PaddingDerPt,
                    PaddingSupPt = capa.PaddingSupPt,
                    PaddingInfPt = capa.PaddingInfPt,
                };
                DibujarCapa(_paginaActual, copia, esFondo: false);
                _cursorY = Math.Max(_cursorY, yCapa + Math.Max(capa.AltoPt, capa.AltoContenidoPt));
            }
            return;
        }

        if (bloque.Parrafo != null)
        {
            var previo = indice > 0 ? _modelo.Flujo[indice - 1].Parrafo : null;
            var siguiente = indice + 1 < _modelo.Flujo.Count ? _modelo.Flujo[indice + 1].Parrafo : null;
            var pm = MedirParrafo(bloque.Parrafo, ancho, previo, siguiente, enCelda: false, _paginaActual.NumeroPagina, 0);

            // fo:keep-with-next="always": verificar si cabe este párrafo + al menos la primera línea/fila del siguiente
            if (permitirSaltoPagina && bloque.Parrafo.MantenerConSiguiente && _cursorY > _areaSup + 0.5)
            {
                var altoSig = EstimarMinimoSiguienteBloque(indice + 1, ancho);
                if (_cursorY + pm.AltoTotalPt + altoSig > _areaInf + 0.05)
                    NuevaPagina();
            }

            _anclasBloque[indice] = (_paginaActual, xBase, _cursorY);
            RenderizarParrafoPaginado(pm, xBase, ancho, permitirSaltoPagina);
        }
    }

    private double EstimarMinimoSiguienteBloque(int indiceSig, double ancho)
    {
        if (indiceSig >= _modelo.Flujo.Count) return 0;
        var b = _modelo.Flujo[indiceSig];
        if (b.Tipo == "espacio") return b.AltoPt;
        if (b.Tipo == "texto" && b.Parrafo != null)
        {
            var pm = MedirParrafo(b.Parrafo, ancho, previo: null, siguiente: null, enCelda: false, _paginaActual.NumeroPagina, 0);
            var h = pm.MargenSupPt + (pm.Lineas.Count > 0 ? pm.Lineas[0].AltoPt : pm.AltoContenidoPt);
            return h;
        }
        if (b.Tipo == "tabla" && b.Tabla != null && b.Tabla.Filas.Count > 0)
        {
            var primeraFila = b.Tabla.Filas[0];
            var h = Math.Max(primeraFila.AltoFijoPt, primeraFila.AltoMinimoPt);
            if (h <= 0)
            {
                var p = primeraFila.Celdas.SelectMany(c => c.Parrafos).FirstOrDefault();
                h = (p?.AltoLineaPt > 0 ? p.AltoLineaPt : 12.65) +
                    primeraFila.Celdas.Select(c => c.PaddingSupPt + c.PaddingInfPt).DefaultIfEmpty(4).Max();
            }
            return h;
        }
        return 0;
    }

    // -------------------------------------------------------------------------
    // Medición y renderizado de párrafos (texto multi-run, listas, tabs, imágenes)
    // -------------------------------------------------------------------------

    private ParrafoMedido MedirParrafo(ParrafoModelo p, double anchoDisponible, ParrafoModelo previo,
                                       ParrafoModelo siguiente, bool enCelda, int numPagina, int totalPaginas)
    {
        var pm = new ParrafoMedido { Parrafo = p };

        // ODF 1.3 §20.255 (style:contextual-spacing="true"): cuando dos párrafos consecutivos
        // comparten EstiloComun y ambos declaran contextual-spacing="true", se suprimen tanto
        // el margin-bottom del primero como el margin-top del segundo.
        var mismoPrevContextual = previo != null && p.Contextual && previo.Contextual &&
            !string.IsNullOrEmpty(p.EstiloComun) &&
            string.Equals(p.EstiloComun, previo.EstiloComun, StringComparison.Ordinal);

        var mismoSigContextual = siguiente != null && p.Contextual && siguiente.Contextual &&
            !string.IsNullOrEmpty(p.EstiloComun) &&
            string.Equals(p.EstiloComun, siguiente.EstiloComun, StringComparison.Ordinal);

        pm.MargenSupPt = mismoPrevContextual ? 0 : Math.Max(p.MargenSupPt, 0);
        pm.MargenInfPt = mismoSigContextual ? 0 : Math.Max(p.MargenInfPt, 0);

        // Imagen inline (as-char)
        if (p.ImagenEnFlujo && !string.IsNullOrEmpty(p.ImagenBase64))
        {
            var img = TryDecodeImage(p.ImagenBase64);
            if (img != null)
            {
                var w = p.ImagenAnchoPt > 0 ? p.ImagenAnchoPt : img.WidthAt();
                var h = p.ImagenAltoPt > 0 ? p.ImagenAltoPt : img.HeightAt();
                if (w > anchoDisponible && anchoDisponible > 5)
                {
                    var esc = anchoDisponible / w;
                    w *= esc;
                    h *= esc;
                }
                pm.Imagen = img;
                pm.AnchoImagenPt = w;
                pm.AltoImagenPt = h;
            }
        }

        // Preparar lista con sangría francesa (marca en columna fija izquierda)
        var esLista = p.SangriaColgantePt > 0.5 && p.Runs.Count > 1;
        IEnumerable<RunModelo> runsCuerpo = p.Runs;

        var padIzq = p.PaddingIzqParrafoPt;
        var padDer = p.PaddingDerParrafoPt;
        double anchoTexto;

        if (esLista)
        {
            var rMarca = p.Runs[0];
            var tamMarca = rMarca.TamanoPt > 0 ? rMarca.TamanoPt : (p.TamanoBasePt > 0 ? p.TamanoBasePt : 10);
            var fMarca = SystemFontCatalog.Resolve(rMarca.Fuente, rMarca.Negrita, rMarca.Cursiva);
            var txtMarca = (rMarca.Texto ?? "").Trim();
            pm.MarcaLista = new SegmentoLinea
            {
                Run = rMarca,
                Texto = txtMarca,
                Fuente = fMarca,
                TamanoPt = tamMarca,
                AnchoPt = MedirTexto(fMarca, txtMarca, tamMarca, rMarca.Kerning, rMarca.EspaciadoCaracteresPt),
            };
            pm.AltoMarcaPt = p.AltoMarcaPt > 0 ? p.AltoMarcaPt : p.AltoLineaPt;
            runsCuerpo = p.Runs.Skip(1);
            var izqLista = Math.Max(p.MargenIncisoPt, 0) + p.SangriaColgantePt;
            anchoTexto = Math.Max(anchoDisponible - izqLista - Math.Max(p.MargenDerPt, 0) - padIzq - padDer, 10);
        }
        else
        {
            anchoTexto = Math.Max(anchoDisponible - p.MargenIzqPt - p.MargenDerPt - padIzq - padDer, 1.0);
        }

        var sangriaPrimera = esLista ? 0 : p.SangriaPt;
        var lineas = EnvolverRuns(p, runsCuerpo, anchoTexto, sangriaPrimera, esLista, numPagina, totalPaginas);

        if (lineas.Count == 0 && pm.Imagen != null)
        {
            // En Writer (SwTextIter::CalcAscentAndHeight), una imagen inline (as-char) en párrafo
            // con interlineado proporcional > 100 % (p.ej. InvoiceSample P12 fo:line-height="116%")
            // reserva el descenso de la fuente base debajo de la imagen (~2.2 pt). En párrafos con
            // interlineado fijo/mínimo o 100 % (p.ej. ComparativoTerceros P4), ocupa sólo su altura.
            if (p.AltoLineaPct > 1.001)
            {
                var tamBase = p.TamanoBasePt > 0 ? p.TamanoBasePt : 10;
                var fBase = SystemFontCatalog.Resolve(p.FuenteBase, false, false);
                pm.DescensoImagenPt = ExtractorFodt.ATwipsPt(tamBase * fBase.Metrics.DescentRatio);
            }
        }
        else if (lineas.Count == 0 && pm.Imagen == null)
        {
            // Párrafo vacío: una línea con la altura base
            var tamBase = p.TamanoBasePt > 0 ? p.TamanoBasePt : 10;
            var fBase = SystemFontCatalog.Resolve(p.FuenteBase, false, false);
            var altoVacio = p.AltoLineaPt > 0 ? p.AltoLineaPt : ExtractorFodt.ATwipsPt(tamBase * fBase.Metrics.NaturalHeightRatio);
            lineas.Add(new LineaFormateada
            {
                AltoPt = altoVacio,
                BaselineOffsetPt = tamBase * fBase.Metrics.BaselineOffsetRatio,
                EsPrimeraDeParrafo = true,
                EsUltimaDeParrafo = true,
            });
        }

        // Si es ítem de lista cuya viñeta (p.ej. Symbol) es más alta que el texto,
        // esa altura extra se suma SOLO a la primera línea (igual que Writer).
        if (esLista && lineas.Count > 0 && pm.AltoMarcaPt > lineas[0].AltoPt + 0.01)
        {
            var extra = pm.AltoMarcaPt - lineas[0].AltoPt;
            lineas[0].AltoPt = pm.AltoMarcaPt;
            lineas[0].BaselineOffsetPt += extra;
        }

        pm.Lineas.AddRange(lineas);
        return pm;
    }

    private List<LineaFormateada> EnvolverRuns(ParrafoModelo p, IEnumerable<RunModelo> runs, double anchoTexto,
                                               double sangriaPrimera, bool esLista, int numPagina, int totalPaginas)
    {
        var tokens = new List<SegmentoLinea>();
        foreach (var r in runs)
        {
            var texto = r.CampoDinamico switch
            {
                "page-number" => numPagina > 0 ? numPagina.ToString(CultureInfo.InvariantCulture) : "1",
                "page-count" => totalPaginas > 0 ? totalPaginas.ToString(CultureInfo.InvariantCulture) : "1",
                _ => r.Texto ?? "",
            };
            if (texto.Length == 0) continue;

            var tam = r.TamanoPt > 0 ? r.TamanoPt : (p.TamanoBasePt > 0 ? p.TamanoBasePt : 10);
            var fuente = SystemFontCatalog.Resolve(r.Fuente, r.Negrita, r.Cursiva);

            // Tokenizar conservando saltos explícitos (\n), tabs (\t), espacios y palabras
            var idx = 0;
            while (idx < texto.Length)
            {
                var ch = texto[idx];
                if (ch == '\n')
                {
                    tokens.Add(new SegmentoLinea { Run = r, Texto = "\n", Fuente = fuente, TamanoPt = tam });
                    idx++;
                    continue;
                }
                if (ch == '\t')
                {
                    tokens.Add(new SegmentoLinea { Run = r, Texto = "\t", Fuente = fuente, TamanoPt = tam, EsTab = true });
                    idx++;
                    continue;
                }
                if (ch == ' ')
                {
                    var start = idx;
                    while (idx < texto.Length && texto[idx] == ' ') idx++;
                    var sp = texto[start..idx];
                    tokens.Add(new SegmentoLinea
                    {
                        Run = r,
                        Texto = sp,
                        Fuente = fuente,
                        TamanoPt = tam,
                        AnchoPt = MedirTexto(fuente, sp, tam, r.Kerning, r.EspaciadoCaracteresPt),
                        EsEspacio = true,
                    });
                    continue;
                }

                var wStart = idx;
                var corteTrasSep = false;
                while (idx < texto.Length && texto[idx] != ' ' && texto[idx] != '\n' && texto[idx] != '\t')
                {
                    idx++;
                    // Permitir corte tras guion simple o barra (p.ej. PROV-00482, 39-B),
                    // pero NO dividir dentro de secuencias consecutivas de guiones (---) usadas
                    // como relleno legal (UAX #14 / Writer: extraord.------ se mantiene unido).
                    var prevCh = texto[idx - 1];
                    var esGuionSimple = prevCh == '-' &&
                        (idx >= texto.Length || texto[idx] != '-') &&
                        (idx - 2 < wStart || texto[idx - 2] != '-');
                    if (idx < texto.Length && (esGuionSimple || prevCh == '/') &&
                        texto[idx] != ' ' && texto[idx] != '\n' && texto[idx] != '\t')
                    {
                        corteTrasSep = true;
                        break;
                    }
                }
                var word = texto[wStart..idx];
                var terminaGuionSimple = word.EndsWith('-') && !word.EndsWith("--", StringComparison.Ordinal);
                tokens.Add(new SegmentoLinea
                {
                    Run = r,
                    Texto = word,
                    Fuente = fuente,
                    TamanoPt = tam,
                    AnchoPt = MedirTexto(fuente, word, tam, r.Kerning, r.EspaciadoCaracteresPt),
                    PermiteCorteDespues = corteTrasSep || terminaGuionSimple || word.EndsWith('/'),
                });
            }
        }

        var lineas = new List<LineaFormateada>();
        if (tokens.Count == 0) return lineas;

        var actual = new LineaFormateada { EsPrimeraDeParrafo = true, SangriaIzqPt = sangriaPrimera };
        var anchoDispLinea = Math.Max(anchoTexto - sangriaPrimera, 1.0);

        void CerrarLinea(bool esFinParrafo, SegmentoLinea saltoFuente = null)
        {
            // Recortar espacios al final de línea
            while (actual.Segmentos.Count > 0 && actual.Segmentos[^1].EsEspacio)
            {
                actual.AnchoPt -= actual.Segmentos[^1].AnchoPt;
                actual.Segmentos.RemoveAt(actual.Segmentos.Count - 1);
            }
            actual.EsUltimaDeParrafo = esFinParrafo;
            CalcularMetricasLinea(p, actual, saltoFuente);
            lineas.Add(actual);

            actual = new LineaFormateada { EsPrimeraDeParrafo = false, SangriaIzqPt = 0 };
            anchoDispLinea = anchoTexto;
        }

        var ti = 0;
        while (ti < tokens.Count)
        {
            var tok = tokens[ti];
            if (tok.Texto == "\n")
            {
                CerrarLinea(esFinParrafo: true, saltoFuente: tok);
                ti++;
                continue;
            }

            if (tok.EsTab)
            {
                // ODF TabsRelativeToIndent="false" (default de Writer/Word): las paradas de tabulador
                // se miden desde el borde izquierdo del área/celda, no desde fo:margin-left.
                var baseTabX = !_modelo.TabsRelativosASangria && !esLista ? Math.Max(p.MargenIzqPt, 0) : 0;
                var xActual = baseTabX + actual.SangriaIzqPt + actual.AnchoPt;
                var (destino, tipoTab) = SiguienteTab(p.Tabs, xActual);
                double avance;
                if (tipoTab is "right" or "center")
                {
                    // Medir el contenido que sigue al tab hasta el siguiente tab o salto de línea
                    var anchoSeg = 0.0;
                    var ultimoNoEspacio = 0.0;
                    for (var k = ti + 1; k < tokens.Count; k++)
                    {
                        if (tokens[k].EsTab || tokens[k].Texto == "\n") break;
                        anchoSeg += tokens[k].AnchoPt;
                        if (!tokens[k].EsEspacio) ultimoNoEspacio = anchoSeg;
                    }
                    var anchoAlineado = ultimoNoEspacio;
                    avance = tipoTab == "right"
                        ? Math.Max(destino - xActual - anchoAlineado, 2.0)
                        : Math.Max(destino - xActual - anchoAlineado / 2.0, 2.0);
                }
                else
                {
                    avance = Math.Max(destino - xActual, 4.0);
                }
                actual.Segmentos.Add(new SegmentoLinea
                {
                    Run = tok.Run,
                    Texto = "",
                    Fuente = tok.Fuente,
                    TamanoPt = tok.TamanoPt,
                    AnchoPt = avance,
                    EsTab = true,
                    TabDestinoX = destino,
                });
                actual.AnchoPt += avance;
                ti++;
                continue;
            }

            if (tok.EsEspacio)
            {
                ti++;
                // ODF/Writer: sólo se descartan espacios al inicio de líneas envueltas de continuación;
                // en la primera línea del párrafo los espacios explícitos (<text:s/>) son sangría real.
                if (actual.Segmentos.Count == 0 && !actual.EsPrimeraDeParrafo) continue;
                actual.Segmentos.Add(tok);
                actual.AnchoPt += tok.AnchoPt;
                continue;
            }

            // Agrupar runs adyacentes sin separador (p.ej. dos <text:span> pegados dentro de la misma
            // palabra) como una sola unidad indivisible; Writer no corta líneas en fronteras de span.
            var cluster = new List<SegmentoLinea> { tok };
            var anchoCluster = tok.AnchoPt;
            var permiteCorte = tok.PermiteCorteDespues;
            ti++;
            while (!permiteCorte && ti < tokens.Count)
            {
                var sig = tokens[ti];
                if (sig.EsEspacio || sig.EsTab || sig.Texto == "\n") break;
                cluster.Add(sig);
                anchoCluster += sig.AnchoPt;
                permiteCorte = sig.PermiteCorteDespues;
                ti++;
            }

            // En LibreOffice Writer (SwLineLayout) el ancho se compara en twips enteros sin holgura extra;
            // usar sólo una tolerancia mínima de punto flotante (0.01 pt) evita que líneas que exceden
            // el ancho útil de la celda (p.ej. "¿QUE NECESITAS?" en Tabla8.C2) dejen de envolver.
            // Si el documento habilita JustifyLinesWithShrinking (ODF/Word, LO 25.2 DEFAULT_MIN_WORD_SPACING = 80 %),
            // los espacios de una línea justificada pueden encogerse hasta 20 % para acomodar una palabra más.
            var holguraEncogimiento = (_modelo.JustificarConEncogimiento && p.Alineacion == "justify")
                ? actual.Segmentos.Where(s => s.EsEspacio).Sum(s => s.AnchoPt) * 0.20
                : 0.0;

            if (actual.AnchoPt - holguraEncogimiento + anchoCluster <= anchoDispLinea + 0.01)
            {
                foreach (var seg in cluster)
                {
                    actual.Segmentos.Add(seg);
                    actual.AnchoPt += seg.AnchoPt;
                }
                continue;
            }

            if (actual.Segmentos.Count > 0)
            {
                CerrarLinea(esFinParrafo: false);
            }

            // Si la unidad entera aún excede el ancho completo, partir por caracteres conservando cada span
            if (anchoCluster > anchoDispLinea + 0.01)
            {
                PartirClusterLargo(cluster, () => anchoDispLinea, ref actual, b => CerrarLinea(b));
            }
            else
            {
                foreach (var seg in cluster)
                {
                    actual.Segmentos.Add(seg);
                    actual.AnchoPt += seg.AnchoPt;
                }
            }
        }

        if (actual.Segmentos.Count > 0 || lineas.Count == 0)
        {
            CerrarLinea(esFinParrafo: true);
        }

        return lineas;
    }

    private void PartirClusterLargo(List<SegmentoLinea> cluster, Func<double> obtenerAnchoMax,
                                    ref LineaFormateada actual, Action<bool> cerrarLinea)
    {
        for (var si = 0; si < cluster.Count; si++)
        {
            var tok = cluster[si];
            var s = tok.Texto;
            var pos = 0;

            while (pos < s.Length)
            {
                var restante = obtenerAnchoMax() - actual.AnchoPt;
                var remText = pos == 0 ? s : s[pos..];
                var remWidth = pos == 0 ? tok.AnchoPt : MedirTexto(tok.Fuente, remText, tok.TamanoPt, tok.Run.Kerning, tok.Run.EspaciadoCaracteresPt);

                if (remWidth <= restante + 0.01)
                {
                    actual.Segmentos.Add(new SegmentoLinea
                    {
                        Run = tok.Run,
                        Texto = remText,
                        Fuente = tok.Fuente,
                        TamanoPt = tok.TamanoPt,
                        AnchoPt = remWidth,
                    });
                    actual.AnchoPt += remWidth;
                    break;
                }

                // Buscar el prefijo más largo que cabe en el espacio restante de la línea actual
                var len = 0;
                double w = 0;
                while (pos + len < s.Length)
                {
                    var nextW = MedirTexto(tok.Fuente, s.Substring(pos, len + 1), tok.TamanoPt, tok.Run.Kerning, tok.Run.EspaciadoCaracteresPt);
                    if (nextW > restante + 0.01) break;
                    w = nextW;
                    len++;
                }

                if (len == 0)
                {
                    if (actual.Segmentos.Count > 0)
                    {
                        cerrarLinea(false);
                        continue;
                    }
                    // Incluso un solo carácter excede la línea vacía: forzar 1 carácter para avanzar
                    len = 1;
                    w = MedirTexto(tok.Fuente, s.Substring(pos, 1), tok.TamanoPt, tok.Run.Kerning, tok.Run.EspaciadoCaracteresPt);
                }

                actual.Segmentos.Add(new SegmentoLinea
                {
                    Run = tok.Run,
                    Texto = s.Substring(pos, len),
                    Fuente = tok.Fuente,
                    TamanoPt = tok.TamanoPt,
                    AnchoPt = w,
                });
                actual.AnchoPt += w;
                pos += len;

                if (pos < s.Length || si + 1 < cluster.Count)
                {
                    cerrarLinea(false);
                }
            }
        }
    }

    private static (double PosicionPt, string Tipo) SiguienteTab(List<TabStopModelo> tabs, double xActual)
    {
        if (tabs != null && tabs.Count > 0)
        {
            foreach (var t in tabs)
                if (t.PosicionPt > xActual + 0.5) return (t.PosicionPt, t.Tipo);
        }
        const double pasoDefault = 1.25 * 72.0 / 2.54; // 35.433 pt (default de Writer)
        return ((Math.Floor(xActual / pasoDefault) + 1) * pasoDefault, "left");
    }

    private void CalcularMetricasLinea(ParrafoModelo p, LineaFormateada linea, SegmentoLinea saltoFuente = null)
    {
        var conTexto = linea.Segmentos.Where(s => !string.IsNullOrWhiteSpace(s.Texto)).ToList();
        double maxNatural = 0;
        double maxBaseline = 0;

        if (conTexto.Count > 0)
        {
            foreach (var seg in conTexto)
            {
                // En Writer (SwTextPortion::Format), un run con style:text-position="sub/super XX%"
                // siempre reserva su caja de línea y línea base con el tamaño SIN escalar (tanto si
                // convive con texto normal como si toda la línea es super/sub, p.ej. CartaPorte).
                var tamMetrica = (seg.Run.EscalaPosicion is double esc && esc > 0 && esc < 100)
                    ? seg.TamanoPt * 100.0 / esc
                    : seg.TamanoPt;
                var nat = tamMetrica * seg.Fuente.Metrics.NaturalHeightRatio;
                var baseOff = tamMetrica * seg.Fuente.Metrics.BaselineOffsetRatio;
                if (nat > maxNatural) maxNatural = nat;
                if (baseOff > maxBaseline) maxBaseline = baseOff;
            }
        }
        else if (saltoFuente != null && saltoFuente.Fuente != null && saltoFuente.TamanoPt > 0)
        {
            // En Writer (SwBreakPortion), un <text:line-break/> envuelto en <text:span>
            // usa la fuente y tamaño del span para la línea vacía que cierra (p.ej. T6 9pt dentro de P4 11pt).
            maxNatural = saltoFuente.TamanoPt * saltoFuente.Fuente.Metrics.NaturalHeightRatio;
            maxBaseline = saltoFuente.TamanoPt * saltoFuente.Fuente.Metrics.BaselineOffsetRatio;
        }
        else
        {
            var tam = p.TamanoBasePt > 0 ? p.TamanoBasePt : 10;
            var f = SystemFontCatalog.Resolve(p.FuenteBase, false, false);
            maxNatural = tam * f.Metrics.NaturalHeightRatio;
            maxBaseline = tam * f.Metrics.BaselineOffsetRatio;
        }

        double alto;
        if (p.AltoLineaFijo && p.AltoLineaPt > 0)
        {
            alto = p.AltoLineaPt;
        }
        else if (p.AltoLineaPct > 0)
        {
            alto = ExtractorFodt.ATwipsPt(maxNatural * p.AltoLineaPct);
        }
        else if (p.AltoLineaPt > 0)
        {
            alto = Math.Max(p.AltoLineaPt, ExtractorFodt.ATwipsPt(maxNatural));
        }
        else
        {
            alto = ExtractorFodt.ATwipsPt(maxNatural);
        }

        // En LibreOffice Writer:
        // - Con fo:line-height=100% (alto ≈ natural): baseline = ascent + lineGap.
        // - Con interlineado proporcional > 100%, el espacio extra va debajo de la línea base.
        // - Con alto fijo menor que el natural, la línea base se ajusta proporcionalmente.
        // - Con alto fijo mayor que el natural (p.ej. 0.353cm sobre Arial 5-6pt en CartaPorte),
        //   Writer distribuye el interlineado extra encima de la línea base (~75% sobre la base).
        double baseline;
        if (alto < maxNatural - 0.1 && maxNatural > 0)
            baseline = maxBaseline * (alto / maxNatural);
        else if (p.AltoLineaFijo && alto > maxNatural + 0.2 && maxNatural > 0)
            baseline = maxBaseline + (alto - maxNatural) * 0.75;
        else
            baseline = maxBaseline;

        linea.AltoPt = alto;
        linea.AltoNaturalPt = (!p.AltoLineaFijo && p.AltoLineaPct > 1.001)
            ? Math.Min(alto, ExtractorFodt.ATwipsPt(maxNatural))
            : alto;
        linea.BaselineOffsetPt = baseline;
    }

    private void RenderizarParrafoPaginado(ParrafoMedido pm, double xBase, double ancho, bool permitirSaltoPagina)
    {
        var p = pm.Parrafo;

        // Margen superior: en LibreOffice Writer (SwFlowFrame::MoveBwd) el primer párrafo
        // del documento (página 1) o el primero tras un salto explícito / cambio de página maestra
        // SÍ respeta su fo:margin-top; sólo se colapsa tras un salto automático por desborde.
        if (_paginas.Count == 1 || _cursorY > _areaSup + 0.5 || _saltoExplicitoPagina)
            _cursorY += pm.MargenSupPt;
        _saltoExplicitoPagina = false;

        // Si el párrafo entero debe mantenerse junto (fo:keep-together="always" o con borde/fondo o ≤ 3 líneas con viudas/huérfanas)
        var mantenerTodo = p.MantenerJunto || p.BordeParrafo || p.BordeInferiorParrafo ||
                           !string.IsNullOrEmpty(p.ColorFondoParrafo) ||
                           (pm.Lineas.Count <= 3 && (p.Huerfanas >= 2 || p.Viudas >= 2));

        // En Writer (SwTextFormatter::CalcRealHeight), con interlineado proporcional > 100%
        // (p.ej. fo:line-height="115%"), el espacio extra debajo de la última línea de la página
        // no impide que la línea quepa sobre el pie de página; basta con su altura natural.
        var ajusteUltimaLinea = pm.Lineas.Count > 0
            ? Math.Max(0, pm.Lineas[^1].AltoPt - pm.Lineas[^1].AltoNaturalPt)
            : 0;
        var altoNecesarioTodo = pm.AltoContenidoPt - ajusteUltimaLinea;

        if (permitirSaltoPagina && mantenerTodo &&
            _cursorY + altoNecesarioTodo > _areaInf + 0.05 &&
            _cursorY > _areaSup + 0.5)
        {
            NuevaPagina();
        }

        // Si cabe completo en la página actual (o no se permite saltar), dibujar directo
        if (!permitirSaltoPagina || _cursorY + altoNecesarioTodo <= _areaInf + 0.05)
        {
            DibujarParrafoEnPagina(_paginaActual.Page, pm, xBase, ancho, _cursorY, _paginaActual.NumeroPagina, 0);
            _cursorY += pm.AltoContenidoPt + pm.MargenInfPt;
            return;
        }

        // Paginar línea por línea respetando huérfanas y viudas
        var huerfanas = Math.Max(p.Huerfanas, 1);
        var viudas = Math.Max(p.Viudas, 1);
        var idx = 0;

        while (idx < pm.Lineas.Count)
        {
            var disponibles = _areaInf - _cursorY;
            var caben = 0;
            var acum = 0.0;
            while (idx + caben < pm.Lineas.Count)
            {
                var lin = pm.Lineas[idx + caben];
                if (acum + lin.AltoNaturalPt > disponibles + 0.05) break;
                acum += lin.AltoPt;
                caben++;
            }

            if (caben == 0)
            {
                if (_cursorY > _areaSup + 0.5)
                {
                    NuevaPagina();
                    continue;
                }
                caben = 1; // forzar al menos 1 línea en página vacía
            }

            // Control de huérfanas (inicio de párrafo)
            if (idx == 0 && caben < huerfanas && caben < pm.Lineas.Count && _cursorY > _areaSup + 0.5)
            {
                NuevaPagina();
                continue;
            }

            // Control de viudas (fin de párrafo)
            var restantes = pm.Lineas.Count - (idx + caben);
            if (restantes > 0 && restantes < viudas && caben > huerfanas)
            {
                var mover = viudas - restantes;
                if (caben - mover >= huerfanas)
                    caben -= mover;
            }

            DibujarRangoLineas(_paginaActual.Page, pm, xBase, ancho, _cursorY, idx, caben, _paginaActual.NumeroPagina, 0);
            for (var k = 0; k < caben; k++)
                _cursorY += pm.Lineas[idx + k].AltoPt;

            idx += caben;
            if (idx < pm.Lineas.Count)
                NuevaPagina();
        }

        if (pm.Imagen != null)
        {
            if (_cursorY + pm.AltoImagenPt > _areaInf + 0.05 && _cursorY > _areaSup + 0.5)
                NuevaPagina();
            DibujarImagenParrafo(_paginaActual.Page, pm, xBase, ancho, _cursorY);
            _cursorY += pm.AltoImagenPt;
        }

        _cursorY += pm.MargenInfPt;
    }

    private void DibujarParrafoEnPagina(IPdfPage page, ParrafoMedido pm, double xBase, double ancho, double yTop,
                                        int numPagina, int totalPaginas, string fondoContenedor = null)
    {
        var p = pm.Parrafo;
        var altoCaja = pm.AltoContenidoPt;
        var fondoEfectivo = !string.IsNullOrEmpty(p.ColorFondoParrafo) ? p.ColorFondoParrafo : fondoContenedor;

        // Fondo y bordes de caja de párrafo
        if (!string.IsNullOrEmpty(p.ColorFondoParrafo) || p.BordeParrafo)
        {
            var boxRect = new Rect(xBase + p.MargenIzqPt, yTop,
                Math.Max(ancho - p.MargenIzqPt - p.MargenDerPt, 1), altoCaja);
            var fill = !string.IsNullOrEmpty(p.ColorFondoParrafo) ? Paint.Fill(ParseColor(p.ColorFondoParrafo)) : (Paint?)null;
            var stroke = p.BordeParrafo ? StrokeStyle.Create(ParseColor(p.ColorBordeParrafo ?? "#000000"), Math.Max(p.GrosorBordeParrafoPt, 0.5)) : (StrokeStyle?)null;
            page.DrawRectangle(boxRect, fill, stroke);
        }
        else if (p.BordeInferiorParrafo)
        {
            var x1 = xBase + p.MargenIzqPt;
            var x2 = xBase + ancho - p.MargenDerPt;
            var yLine = yTop + altoCaja;
            page.DrawLine(new Point(x1, yLine), new Point(x2, yLine),
                StrokeStyle.Create(ParseColor(p.ColorBordeParrafo ?? "#000000"), Math.Max(p.GrosorBordeParrafoPt, 0.5)));
        }

        var yCursor = yTop + pm.BordeVertPt + p.PaddingSupParrafoPt;
        DibujarRangoLineas(page, pm, xBase, ancho, yCursor, 0, pm.Lineas.Count, numPagina, totalPaginas, fondoEfectivo);
        yCursor += pm.Lineas.Sum(l => l.AltoPt);

        if (pm.Imagen != null)
            DibujarImagenParrafo(page, pm, xBase, ancho, yCursor);

        // Líneas de firma (draw:line) ancladas al párrafo
        foreach (var l in p.Lineas)
        {
            var stroke = StrokeStyle.Create(ParseColor(l.Color ?? "#000000"), Math.Max(l.GrosorPt, 0.5));
            page.DrawLine(new Point(xBase + l.XPt, yTop + l.YPt),
                          new Point(xBase + l.XPt + l.AnchoPt, yTop + l.YPt), stroke);
        }
    }

    private void DibujarImagenParrafo(IPdfPage page, ParrafoMedido pm, double xBase, double ancho, double yTop)
    {
        var p = pm.Parrafo;
        var x = p.Alineacion switch
        {
            "center" => xBase + Math.Max((ancho - pm.AnchoImagenPt) / 2, 0),
            "end" or "right" => xBase + Math.Max(ancho - pm.AnchoImagenPt, 0),
            _ => xBase + p.MargenIzqPt,
        };
        page.DrawImage(pm.Imagen, new Rect(x, yTop, pm.AnchoImagenPt, pm.AltoImagenPt));
    }

    private void DibujarRangoLineas(IPdfPage page, ParrafoMedido pm, double xBase, double ancho, double yInicio,
                                    int desde, int cantidad, int numPagina, int totalPaginas, string fondoContenedor = null)
    {
        var p = pm.Parrafo;
        var fondoOscuro = EsFondoOscuro(!string.IsNullOrEmpty(p.ColorFondoParrafo) ? p.ColorFondoParrafo : fondoContenedor);
        var esLista = pm.MarcaLista != null;
        var xContenido = esLista
            ? xBase + Math.Max(p.MargenIncisoPt, 0) + p.SangriaColgantePt + p.PaddingIzqParrafoPt
            : xBase + p.MargenIzqPt + p.PaddingIzqParrafoPt;
        var anchoContenido = esLista
            ? Math.Max(ancho - Math.Max(p.MargenIncisoPt, 0) - p.SangriaColgantePt - Math.Max(p.MargenDerPt, 0) - p.PaddingIzqParrafoPt - p.PaddingDerParrafoPt, 10)
            : Math.Max(ancho - p.MargenIzqPt - p.MargenDerPt - p.PaddingIzqParrafoPt - p.PaddingDerParrafoPt, 1.0);

        var y = yInicio;
        for (var i = desde; i < desde + cantidad && i < pm.Lineas.Count; i++)
        {
            var linea = pm.Lineas[i];
            var baselineY = y + linea.BaselineOffsetPt;

            // Dibujar marcador de lista en la primera línea
            if (esLista && i == 0 && !string.IsNullOrEmpty(pm.MarcaLista.Texto))
            {
                var xMarca = xBase + Math.Max(p.MargenIncisoPt, 0);
                var handleM = page.RegisterFont(pm.MarcaLista.Fuente.Face);
                var colM = (pm.MarcaLista.Run.ColorAutomatico && fondoOscuro)
                    ? PdfColor.White
                    : ParseColor(pm.MarcaLista.Run.Color);
                page.DrawText(new TextRun(pm.MarcaLista.Texto, handleM, pm.MarcaLista.TamanoPt, colM,
                                          CharacterSpacing: pm.MarcaLista.Run.EspaciadoCaracteresPt,
                                          Kerning: pm.MarcaLista.Run.Kerning),
                              new Point(xMarca, baselineY));
            }

            if (linea.Segmentos.Count > 0)
            {
                var anchoUtil = Math.Max(anchoContenido - linea.SangriaIzqPt, 1);
                var sangriaCentrada = p.Alineacion == "center" && !esLista ? Math.Max(p.SangriaPt, 0) / 2.0 : 0;

                double xStart;
                double extraEspacio = 0;

                if (p.Alineacion == "center")
                {
                    xStart = xContenido + sangriaCentrada + Math.Max((anchoContenido - linea.AnchoPt) / 2.0, 0);
                }
                else if (p.Alineacion is "end" or "right")
                {
                    xStart = xContenido + Math.Max(anchoContenido - linea.AnchoPt, 0);
                }
                else if (p.Alineacion == "justify" && !linea.EsUltimaDeParrafo)
                {
                    xStart = xContenido + linea.SangriaIzqPt;
                    var numEspacios = linea.Segmentos.Count(s => s.EsEspacio);
                    if (numEspacios > 0 && Math.Abs(anchoUtil - linea.AnchoPt) > 0.05)
                        extraEspacio = (anchoUtil - linea.AnchoPt) / numEspacios;
                }
                else
                {
                    xStart = xContenido + linea.SangriaIzqPt;
                }

                var x = xStart;
                foreach (var seg in linea.Segmentos)
                {
                    if (seg.EsTab)
                    {
                        x += seg.AnchoPt;
                        continue;
                    }
                    if (seg.EsEspacio)
                    {
                        x += seg.AnchoPt + extraEspacio;
                        continue;
                    }

                    var txt = seg.Texto;
                    if (seg.Run.CampoDinamico == "page-number" && numPagina > 0)
                        txt = numPagina.ToString(CultureInfo.InvariantCulture);
                    else if (seg.Run.CampoDinamico == "page-count" && totalPaginas > 0)
                        txt = totalPaginas.ToString(CultureInfo.InvariantCulture);

                    var handle = page.RegisterFont(seg.Fuente.Face);
                    var color = (seg.Run.ColorAutomatico && fondoOscuro)
                        ? PdfColor.White
                        : ParseColor(seg.Run.Color);
                    // En LibreOffice Writer (DFLT_ESC_AUTO_SUPER / DFLT_ESC_AUTO_SUB),
                    // super/subíndice desplaza la línea base en proporción al tamaño SIN escalar
                    // incluso cuando toda la línea es super/sub (p.ej. CartaPorte T14 super vs T15 sub).
                    var tamSinEscalar = (seg.Run.EscalaPosicion is double escPos && escPos > 0 && escPos < 100)
                        ? seg.TamanoPt * 100.0 / escPos
                        : seg.TamanoPt;
                    var textRise = seg.Run.Posicion switch
                    {
                        "super" => tamSinEscalar * 0.35,
                        "sub" => -tamSinEscalar * 0.14,
                        _ => 0,
                    };

                    page.DrawText(new TextRun(txt, handle, seg.TamanoPt, color,
                                              CharacterSpacing: seg.Run.EspaciadoCaracteresPt,
                                              TextRise: textRise, Kerning: seg.Run.Kerning),
                                  new Point(x, baselineY));

                    if (seg.Run.Subrayado)
                    {
                        var met = GetMetrics(seg.Fuente.Face);
                        var uPos = met.UnderlinePositionAt(seg.TamanoPt);
                        var uThick = Math.Max(met.UnderlineThicknessAt(seg.TamanoPt), 0.45);
                        var ySub = baselineY - uPos - textRise;
                        page.DrawLine(new Point(x, ySub), new Point(x + seg.AnchoPt, ySub), StrokeStyle.Create(color, uThick));
                    }

                    if (!string.IsNullOrWhiteSpace(seg.Run.Hipervinculo))
                    {
                        page.AddLink(new Rect(x, y, seg.AnchoPt, linea.AltoPt), seg.Run.Hipervinculo);
                    }

                    x += seg.AnchoPt;
                }
            }

            y += linea.AltoPt;
        }
    }

    // -------------------------------------------------------------------------
    // Motor de tablas ODF (colspan, rowspan, bordes colapsados, división de filas, anidadas)
    // -------------------------------------------------------------------------

    private sealed class CeldaPosicionada
    {
        public int Fila { get; init; }
        public int Col { get; init; }
        public int SpanFilas { get; init; }
        public int SpanCols { get; init; }
        public CeldaModelo Celda { get; init; }
        public double XRel { get; init; }
        public double Ancho { get; init; }
        public double PadIzq { get; init; }
        public double PadDer { get; init; }
        public double PadSup { get; set; }
        public double PadInf { get; set; }
        public double PadSupBase { get; init; }
        public double PadInfBase { get; init; }
        public bool CeldaConImagen { get; init; }
        public double GrosorSup { get; init; }
        public double GrosorInf { get; init; }
        public double GrosorIzq { get; init; }
        public double GrosorDer { get; init; }
        public List<ParrafoMedido> Parrafos { get; } = [];
        public double AltoContenido { get; set; }
        public double AltoTotalRequerido => AltoContenido + PadSup + PadInf;
    }

    private (double TableX, double[] ColWidths, List<CeldaPosicionada> Celdas, double[] RowHeights, double ExtSup, double ExtInf)
        PrepararTabla(TablaModelo tabla, double xBase, double anchoDisponible, bool esFlujo, int numPagina, int totalPaginas)
    {
        var numFilas = tabla.Filas.Count;
        var anchoTabla = tabla.AnchoDeclaradoPt > 0 ? tabla.AnchoDeclaradoPt : tabla.ColumnasPt.Sum();
        if (anchoTabla <= 0) anchoTabla = anchoDisponible;

        // Posición horizontal de la tabla
        var tableX = xBase;
        if (esFlujo)
        {
            if (tabla.AlineacionTabla == "center" && anchoDisponible > 1 && anchoTabla > 1)
            {
                var libre = anchoDisponible - anchoTabla;
                tableX = xBase + libre / 2.0;
            }
            else if (Math.Abs(tabla.MargenIzqPt) > 0.05)
            {
                tableX = xBase + tabla.MargenIzqPt;
            }
        }
        else if (anchoTabla > anchoDisponible && anchoDisponible > 1)
        {
            anchoTabla = anchoDisponible;
        }

        var numCols = Math.Max(
            tabla.ColumnasPt.Count,
            numFilas > 0 ? tabla.Filas.Max(f => f.Celdas.Sum(c => Math.Max(c.SpanColumnas, 1))) : 1);

        var colWidths = new double[numCols];
        var sumDecl = tabla.ColumnasPt.Sum();
        if (sumDecl > 0 && tabla.ColumnasPt.Count > 0)
        {
            var factor = anchoTabla / sumDecl;
            for (var c = 0; c < numCols; c++)
                colWidths[c] = (c < tabla.ColumnasPt.Count ? tabla.ColumnasPt[c] : (sumDecl / numCols)) * factor;
        }
        else
        {
            for (var c = 0; c < numCols; c++)
                colWidths[c] = anchoTabla / numCols;
        }

        // Reservas exteriores de borde (collapsing border model)
        double extSup = 0, extInf = 0;
        if (esFlujo && numFilas > 0)
        {
            extSup = tabla.Filas[0].Celdas
                .Where(c => c.BordeSup)
                .Select(c => c.GrosorBordeSupPt > 0.05 ? c.GrosorBordeSupPt : (c.GrosorBordePt > 0.05 ? c.GrosorBordePt : 0.7))
                .DefaultIfEmpty(0).Max() / 2.0;

            extInf = tabla.Filas
                .SelectMany((f, r) => f.Celdas
                    .Where(c => c.BordeInf && r + Math.Max(c.SpanFilas, 1) >= numFilas)
                    .Select(c => c.GrosorBordeInfPt > 0.05 ? c.GrosorBordeInfPt : (c.GrosorBordePt > 0.05 ? c.GrosorBordePt : 0.7)))
                .DefaultIfEmpty(0).Max() / 2.0;
        }

        // Ubicar celdas respetando colspan y rowspan
        var ocupado = new bool[numFilas, numCols];
        var posicionadas = new List<CeldaPosicionada>();

        for (var r = 0; r < numFilas; r++)
        {
            var fila = tabla.Filas[r];

            // Propagar padding-top máximo entre celdas de la misma fila con borde superior colapsado
            var padSupFilaMax = fila.Celdas.Count > 1
                ? fila.Celdas
                    .Where(c => c.BordeSup && c.SpanFilas == 1)
                    .Select(c => Math.Min(c.PaddingSupPt, 6.0))
                    .DefaultIfEmpty(0)
                    .Max()
                : 0.0;

            var col = 0;
            foreach (var celda in fila.Celdas)
            {
                while (col < numCols && ocupado[r, col]) col++;
                var spanC = Math.Min(Math.Max(celda.SpanColumnas, 1), Math.Max(numCols - col, 1));
                var spanR = Math.Min(Math.Max(celda.SpanFilas, 1), numFilas - r);

                for (var rr = r; rr < r + spanR; rr++)
                    for (var cc = col; cc < col + spanC; cc++)
                        ocupado[rr, cc] = true;

                double xRel = 0;
                for (var cc = 0; cc < col; cc++) xRel += colWidths[cc];
                double anchoCelda = 0;
                for (var cc = col; cc < col + spanC; cc++) anchoCelda += colWidths[cc];

                var grosorDef = celda.GrosorBordePt > 0.05 ? celda.GrosorBordePt : 0.7;
                var gSup = celda.BordeSup ? (celda.GrosorBordeSupPt > 0.05 ? celda.GrosorBordeSupPt : grosorDef) : 0;
                var gInf = celda.BordeInf ? (celda.GrosorBordeInfPt > 0.05 ? celda.GrosorBordeInfPt : grosorDef) : 0;
                var gIzq = celda.BordeIzq ? (celda.GrosorBordeIzqPt > 0.05 ? celda.GrosorBordeIzqPt : grosorDef) : 0;
                var gDer = celda.BordeDer ? (celda.GrosorBordeDerPt > 0.05 ? celda.GrosorBordeDerPt : grosorDef) : 0;

                var celdaConImagen = celda.Parrafos.Any(p => p.ImagenEnFlujo && !string.IsNullOrEmpty(p.ImagenBase64));
                var maxAnchoImg = celdaConImagen
                    ? celda.Parrafos.Where(p => p.ImagenEnFlujo && !string.IsNullOrEmpty(p.ImagenBase64)).Max(p => p.ImagenAnchoPt)
                    : 0;
                // Conservar el padding horizontal de celdas con imagen si la imagen cabe dentro del
                // área útil (p.ej. logo de ComparativoTerceros); sólo suprimirlo cuando la imagen
                // ocupa el ancho completo de la celda (QR ajustado al borde).
                var omitirPadHorizImg = celdaConImagen && maxAnchoImg > 0 &&
                                        maxAnchoImg + celda.PaddingIzqPt + celda.PaddingDerPt > anchoCelda + 0.5;
                var aireSup = !celdaConImagen && gSup > 0.05 && fila.AltoFijoPt <= 0.5 ? gSup / 2.0 : 0;
                var aireInf = !celdaConImagen && gInf > 0.05 && fila.AltoFijoPt <= 0.5 ? gInf / 2.0 : 0;
                var aireIzq = !celdaConImagen && gIzq > 0.55 && celda.PaddingIzqPt > 0.5 ? gIzq / 2.0 : 0;
                var aireDer = !celdaConImagen && gDer > 0.55 && celda.PaddingDerPt > 0.5 ? gDer / 2.0 : 0;

                var padIzq = omitirPadHorizImg ? 0 : celda.PaddingIzqPt + aireIzq;
                var padDer = omitirPadHorizImg ? 0 : celda.PaddingDerPt + aireDer;
                var padSupDecl = Math.Min(celda.PaddingSupPt, 6.0);
                var padSupBase = celdaConImagen ? 0 : (celda.BordeSup && spanR == 1 ? Math.Max(padSupDecl, padSupFilaMax) : padSupDecl);
                var padInfBase = celdaConImagen ? 0 : Math.Min(celda.PaddingInfPt, 6.0);
                var padSup = padSupBase + aireSup;
                var padInf = padInfBase + aireInf;

                var cp = new CeldaPosicionada
                {
                    Fila = r,
                    Col = col,
                    SpanFilas = spanR,
                    SpanCols = spanC,
                    Celda = celda,
                    XRel = xRel,
                    Ancho = anchoCelda,
                    PadIzq = padIzq,
                    PadDer = padDer,
                    PadSup = padSup,
                    PadInf = padInf,
                    PadSupBase = padSupBase,
                    PadInfBase = padInfBase,
                    CeldaConImagen = celdaConImagen,
                    GrosorSup = gSup,
                    GrosorInf = gInf,
                    GrosorIzq = gIzq,
                    GrosorDer = gDer,
                };

                var anchoTexto = Math.Max(anchoCelda - padIzq - padDer, 1.0);
                // En LibreOffice Writer (CollapseEmptyCellPara / SwTextFrame::IsCollapse), el párrafo
                // vacío obligatorio que queda justo después de una tabla anidada al final de una celda
                // colapsa a altura 0 para que la tabla anidada ajuste contra el borde inferior de la celda.
                var idxParrafoColapsado = -1;
                if (_dibujarTablasAnidadas && celda.TablasAnidadas.Count > 0 && celda.OrdenContenido.Count >= 2)
                {
                    var ult = celda.OrdenContenido[^1];
                    var penult = celda.OrdenContenido[^2];
                    if (!ult.EsTabla && penult.EsTabla && ult.Indice < celda.Parrafos.Count)
                    {
                        var pUlt = celda.Parrafos[ult.Indice];
                        var esVacio = !pUlt.Runs.Any(x => !string.IsNullOrWhiteSpace(x.Texto)) &&
                                      string.IsNullOrEmpty(pUlt.ImagenBase64) &&
                                      pUlt.Lineas.Count == 0;
                        if (esVacio) idxParrafoColapsado = ult.Indice;
                    }
                }

                var altoCont = 0.0;
                for (var pi = 0; pi < celda.Parrafos.Count; pi++)
                {
                    var p = celda.Parrafos[pi];
                    var prevP = pi > 0 ? celda.Parrafos[pi - 1] : null;
                    var sigP = pi + 1 < celda.Parrafos.Count ? celda.Parrafos[pi + 1] : null;
                    var pm = MedirParrafo(p, anchoTexto, prevP, sigP, enCelda: true, numPagina, totalPaginas);
                    if (pi == idxParrafoColapsado)
                    {
                        pm.Lineas.Clear();
                        pm.MargenSupPt = 0;
                        pm.MargenInfPt = 0;
                    }
                    cp.Parrafos.Add(pm);
                    altoCont += pm.AltoTotalPt;
                }

                if (_dibujarTablasAnidadas && celda.TablasAnidadas.Count > 0)
                {
                    foreach (var an in celda.TablasAnidadas)
                    {
                        var (_, _, _, rhAn, extSupAn, extInfAn) = PrepararTabla(an, 0, anchoTexto, esFlujo: true, numPagina, totalPaginas);
                        altoCont += extSupAn + rhAn.Sum() + extInfAn;
                    }
                }

                cp.AltoContenido = altoCont;
                posicionadas.Add(cp);
                col += spanC;
            }
        }

        // Modelo unificado de bordes horizontales colapsados (table:border-model="collapsing" en ODF/Writer):
        // bordeHoriz[k] es el grosor del borde compartido entre la fila k-1 y la fila k (0 <= k <= numFilas),
        // considerando celdas reales, el borde inferior propagado por celdas con rowspan > 1 a sus subfilas
        // y los estilos propios conservados en <table:covered-table-cell>. Cada fila reserva la mitad del
        // borde superior (bordeHoriz[r]/2) y la mitad del inferior (bordeHoriz[r+1]/2).
        var bordeHoriz = new double[numFilas + 1];
        for (var k = 0; k <= numFilas; k++)
        {
            double g = 0;
            if (k > 0)
            {
                if (tabla.Filas[k - 1].MaxBordeInfCubiertoPt > g)
                    g = tabla.Filas[k - 1].MaxBordeInfCubiertoPt;
                foreach (var c in posicionadas)
                {
                    if (c.Fila <= k - 1 && k - 1 < c.Fila + c.SpanFilas && c.GrosorInf > g)
                        g = c.GrosorInf;
                }
            }
            if (k < numFilas)
            {
                if (tabla.Filas[k].MaxBordeSupCubiertoPt > g)
                    g = tabla.Filas[k].MaxBordeSupCubiertoPt;
                foreach (var c in posicionadas)
                {
                    if (c.Fila == k && c.GrosorSup > g)
                        g = c.GrosorSup;
                }
            }
            bordeHoriz[k] = g > 0.05 ? g : 0;
        }

        // Propagar las reservas verticales del borde colapsado de cada fila al padding superior/inferior
        // de sus celdas (en Writer el contenido de todas las celdas de la fila baja por bordeHoriz[r]/2).
        foreach (var cp in posicionadas)
        {
            if (cp.CeldaConImagen || tabla.Filas[cp.Fila].AltoFijoPt > 0.5) continue;
            cp.PadSup = cp.PadSupBase + bordeHoriz[cp.Fila] / 2.0;
            cp.PadInf = cp.PadInfBase + bordeHoriz[cp.Fila + cp.SpanFilas] / 2.0;
        }

        // Calcular alturas de fila
        var rowHeights = new double[numFilas];
        for (var r = 0; r < numFilas; r++)
        {
            var fila = tabla.Filas[r];
            if (fila.AltoFijoPt > 0.5)
            {
                rowHeights[r] = Math.Max(fila.AltoFijoPt, fila.AltoMinimoPt);
                continue;
            }

            var celdasFila = posicionadas.Where(c => c.Fila == r && c.SpanFilas == 1).ToList();
            var bordeFilaVert = (bordeHoriz[r] + bordeHoriz[r + 1]) / 2.0;
            // En LibreOffice Writer (SwRowFrame + InvertBorderSpacing), style:min-row-height suma
            // únicamente el grosor del borde superior declarado explícitamente en la propia fila
            // (no el borde inferior ni el propagado desde la fila anterior por colapso).
            var bordeSupFilaDecl = fila.MaxBordeSupCubiertoPt;
            foreach (var cp in posicionadas)
            {
                if (cp.Fila == r && cp.GrosorSup > bordeSupFilaDecl)
                    bordeSupFilaDecl = cp.GrosorSup;
            }
            var h = fila.AltoMinimoPt > 0.5
                ? fila.AltoMinimoPt + (bordeSupFilaDecl > 0.05 ? bordeSupFilaDecl : 0)
                : 0;
            foreach (var cp in celdasFila)
            {
                var bordeCp = cp.CeldaConImagen ? 0 : bordeFilaVert;
                var extraBorde = Math.Max(bordeFilaVert - bordeCp, 0);
                h = Math.Max(h, cp.AltoTotalRequerido + extraBorde);
            }

            rowHeights[r] = h > 0.05 ? h : 4.0;
        }

        // Expandir filas si alguna celda con rowspan > 1 necesita más alto
        foreach (var cp in posicionadas.Where(c => c.SpanFilas > 1))
        {
            double suma = 0;
            for (var r = cp.Fila; r < cp.Fila + cp.SpanFilas; r++) suma += rowHeights[r];
            if (cp.AltoTotalRequerido > suma)
                rowHeights[cp.Fila + cp.SpanFilas - 1] += cp.AltoTotalRequerido - suma;
        }

        return (tableX, colWidths, posicionadas, rowHeights, extSup, extInf);
    }

    private void RenderizarTablaFlujo(TablaModelo tabla, double xBase, double anchoDisponible, bool permitirSaltoPagina)
    {
        if (tabla.Filas.Count == 0) return;

        var (tableX, _, celdas, rowHeights, extSup, extInf) =
            PrepararTabla(tabla, xBase, anchoDisponible, esFlujo: true, _paginaActual.NumeroPagina, 0);

        _cursorY += extSup;

        int FinGrupoDe(int filaInicio)
        {
            var fin = filaInicio;
            var cambio = true;
            while (cambio)
            {
                cambio = false;
                foreach (var cp in celdas.Where(c => c.Fila >= filaInicio && c.Fila <= fin))
                {
                    if (cp.Fila + cp.SpanFilas - 1 > fin)
                    {
                        fin = cp.Fila + cp.SpanFilas - 1;
                        cambio = true;
                    }
                }
            }
            return fin;
        }

        var r = 0;
        while (r < tabla.Filas.Count)
        {
            // Agrupar filas unidas por rowspan para mantener integridad vertical
            var finGrupo = FinGrupoDe(r);

            double altoGrupo = 0;
            for (var rr = r; rr <= finGrupo; rr++) altoGrupo += rowHeights[rr];

            var esFilaUnica = finGrupo == r;
            var fila = tabla.Filas[r];

            // Caso 1: cabe entero -> agrupar todos los grupos consecutivos que quepan en esta
            // página en una sola llamada a DibujarGrupoFilas (así TODOS los fondos se pintan antes
            // que TODOS los bordes y el fondo de una fila no tapa la mitad del borde de la fila previa).
            if (!permitirSaltoPagina || _cursorY + altoGrupo <= _areaInf + 0.05)
            {
                var finLote = finGrupo;
                var altoLote = altoGrupo;
                var next = finGrupo + 1;
                while (next < tabla.Filas.Count)
                {
                    var nextFin = FinGrupoDe(next);
                    double nextAlto = 0;
                    for (var rr = next; rr <= nextFin; rr++) nextAlto += rowHeights[rr];
                    if (permitirSaltoPagina && _cursorY + altoLote + nextAlto > _areaInf + 0.05)
                        break;
                    finLote = nextFin;
                    altoLote += nextAlto;
                    next = nextFin + 1;
                }

                DibujarGrupoFilas(_paginaActual.Page, tabla, tableX, _cursorY, r, finLote, celdas, rowHeights,
                                  _paginaActual.NumeroPagina, 0);
                _cursorY += altoLote;
                r = finLote + 1;
                continue;
            }

            // Caso 2: fila única con fo:keep-together="auto" -> intentar dividir la fila entre páginas
            if (esFilaUnica && !fila.MantenerJunta && fila.AltoFijoPt <= 0.5)
            {
                var celdasFila = celdas.Where(c => c.Fila == r).ToList();
                var tieneAnidadas = celdasFila.Any(c => _dibujarTablasAnidadas && c.Celda.TablasAnidadas.Count > 0);
                var disponible = _areaInf - _cursorY;
                var minUnaLinea = celdasFila.Select(c =>
                {
                    var primera = c.Parrafos.FirstOrDefault()?.Lineas.FirstOrDefault()?.AltoPt ?? 0;
                    return c.PadSup + primera + c.PadInf;
                }).DefaultIfEmpty(0).Max();

                if (!tieneAnidadas && minUnaLinea > 0 && disponible >= minUnaLinea - 0.05)
                {
                    DibujarFilaDividida(tableX, celdasFila);
                    r++;
                    continue;
                }
            }

            // Caso 3: no cabe -> abrir nueva página (si no estamos ya al tope)
            if (_cursorY > _areaSup + 0.5)
            {
                NuevaPagina();
                _cursorY += extSup;
                continue;
            }

            // Incluso en página vacía no cabe entero: si es fila única sin keep-together, dividir; si no, dibujar
            if (esFilaUnica && fila.AltoFijoPt <= 0.5)
            {
                DibujarFilaDividida(tableX, celdas.Where(c => c.Fila == r).ToList());
                r++;
            }
            else
            {
                DibujarGrupoFilas(_paginaActual.Page, tabla, tableX, _cursorY, r, finGrupo, celdas, rowHeights,
                                  _paginaActual.NumeroPagina, 0);
                _cursorY += altoGrupo;
                r = finGrupo + 1;
            }
        }

        _cursorY += extInf;
    }

    /// <summary>
    /// Divide una fila con fo:keep-together="auto" a través de una o más fronteras de página,
    /// cerrando bordes en el corte inferior y abriendo bordes en la continuación superior (igual que Writer).
    /// </summary>
    private void DibujarFilaDividida(double tableX, List<CeldaPosicionada> celdasFila)
    {
        // Aplanar las líneas (con su párrafo padre) de cada celda para avanzar sincronizadamente por páginas
        var colas = celdasFila.Select(cp =>
        {
            var lista = new List<(ParrafoMedido Pm, int IndiceLinea)>();
            foreach (var pm in cp.Parrafos)
                for (var li = 0; li < pm.Lineas.Count; li++)
                    lista.Add((pm, li));
            return (Cp: cp, Items: lista, Pos: 0);
        }).ToList();

        var primeraPorcion = true;
        while (true)
        {
            var quedan = colas.Any(q => q.Pos < q.Items.Count);
            if (!quedan && !primeraPorcion) break;

            var disponible = _areaInf - _cursorY;
            var tomas = new int[colas.Count];
            var altoPorcion = 0.0;

            for (var ci = 0; ci < colas.Count; ci++)
            {
                var (cp, items, pos) = colas[ci];
                var padSup = cp.PadSup;
                var padInf = cp.PadInf;
                var hUtil = Math.Max(disponible - padSup - padInf, 0);

                var tomados = 0;
                var hCont = 0.0;
                while (pos + tomados < items.Count)
                {
                    var lh = items[pos + tomados].Pm.Lineas[items[pos + tomados].IndiceLinea].AltoPt;
                    if (hCont + lh > hUtil + 0.05) break;
                    hCont += lh;
                    tomados++;
                }

                // Si estamos en página vacía y no cupo ninguna línea, forzar 1 para avanzar
                if (tomados == 0 && pos < items.Count && _cursorY <= _areaSup + 0.5)
                {
                    hCont = items[pos].Pm.Lineas[items[pos].IndiceLinea].AltoPt;
                    tomados = 1;
                }

                tomas[ci] = tomados;
                if (tomados > 0)
                    altoPorcion = Math.Max(altoPorcion, padSup + hCont + padInf);
            }

            if (altoPorcion <= 0.1)
            {
                if (_cursorY > _areaSup + 0.5)
                {
                    NuevaPagina();
                    continue;
                }
                break;
            }

            var page = _paginaActual.Page;

            // 1. Fondos de la porción
            for (var ci = 0; ci < colas.Count; ci++)
            {
                var cp = colas[ci].Cp;
                if (!string.IsNullOrEmpty(cp.Celda.ColorFondo))
                {
                    page.DrawRectangle(
                        new Rect(tableX + cp.XRel, _cursorY, cp.Ancho, altoPorcion),
                        Paint.Fill(ParseColor(cp.Celda.ColorFondo)), null);
                }
            }

            // 2. Contenido de la porción
            for (var ci = 0; ci < colas.Count; ci++)
            {
                var (cp, items, pos) = colas[ci];
                var tomados = tomas[ci];
                var padSup = cp.PadSup;
                var yLinea = _cursorY + padSup;
                var anchoTexto = Math.Max(cp.Ancho - cp.PadIzq - cp.PadDer, 4);

                for (var k = 0; k < tomados; k++)
                {
                    var item = items[pos + k];
                    DibujarRangoLineas(page, item.Pm, tableX + cp.XRel + cp.PadIzq, anchoTexto, yLinea,
                                       item.IndiceLinea, 1, _paginaActual.NumeroPagina, 0);
                    yLinea += item.Pm.Lineas[item.IndiceLinea].AltoPt;
                }

                colas[ci] = (cp, items, pos + tomados);
            }

            // 3. Bordes de la porción (cerrados arriba y abajo en cada mitad, como LibreOffice)
            for (var ci = 0; ci < colas.Count; ci++)
            {
                var cp = colas[ci].Cp;
                DibujarBordesCelda(page, cp, tableX + cp.XRel, _cursorY, cp.Ancho, altoPorcion);
            }

            _cursorY += altoPorcion;
            primeraPorcion = false;

            if (colas.Any(q => q.Pos < q.Items.Count))
            {
                NuevaPagina();
                var extCont = celdasFila.Where(c => c.Celda.BordeSup).Select(c => c.GrosorSup).DefaultIfEmpty(0).Max() / 2.0;
                _cursorY += extCont;
            }
            else
                break;
        }
    }

    private double DibujarTablaFijaEnPagina(IPdfPage page, TablaModelo tabla, double xBase, double yInicio,
                                            double anchoDisponible, int numPagina, int totalPaginas)
    {
        if (tabla.Filas.Count == 0) return yInicio;

        var (tableX, _, celdas, rowHeights, extSup, extInf) =
            PrepararTabla(tabla, xBase, anchoDisponible, esFlujo: true, numPagina, totalPaginas);

        DibujarGrupoFilas(page, tabla, tableX, yInicio + extSup, 0, tabla.Filas.Count - 1, celdas, rowHeights, numPagina, totalPaginas);
        return yInicio + extSup + rowHeights.Sum() + extInf;
    }

    private void DibujarGrupoFilas(IPdfPage page, TablaModelo tabla, double tableX, double yGrupo,
                                   int filaInicio, int filaFin, List<CeldaPosicionada> todasCeldas,
                                   double[] rowHeights, int numPagina, int totalPaginas)
    {
        var yPorFila = new double[filaFin - filaInicio + 1];
        double acum = 0;
        for (var r = filaInicio; r <= filaFin; r++)
        {
            yPorFila[r - filaInicio] = yGrupo + acum;
            acum += rowHeights[r];
        }

        var delGrupo = todasCeldas.Where(c => c.Fila >= filaInicio && c.Fila <= filaFin).ToList();

        // 1. Fondos primero
        foreach (var cp in delGrupo)
        {
            if (string.IsNullOrEmpty(cp.Celda.ColorFondo)) continue;
            var yCell = yPorFila[cp.Fila - filaInicio];
            double hCell = 0;
            for (var rr = cp.Fila; rr < cp.Fila + cp.SpanFilas; rr++) hCell += rowHeights[rr];
            page.DrawRectangle(new Rect(tableX + cp.XRel, yCell, cp.Ancho, hCell),
                               Paint.Fill(ParseColor(cp.Celda.ColorFondo)), null);
        }

        // 2. Contenido (párrafos + tablas anidadas + marcos flotantes)
        foreach (var cp in delGrupo)
        {
            var yCell = yPorFila[cp.Fila - filaInicio];
            double hCell = 0;
            for (var rr = cp.Fila; rr < cp.Fila + cp.SpanFilas; rr++) hCell += rowHeights[rr];

            var fila = tabla.Filas[cp.Fila];
            // Sólo recortar celdas de alto fijo cuando el contenido realmente excede el alto de la fila;
            // si el contenido cabe, no recortar ascendentes/superíndices (p.ej. CartaPorte T14 super 58%).
            var recortar = fila.AltoFijoPt > 0.5 && cp.SpanFilas == 1 && cp.AltoTotalRequerido > hCell + 0.5;

            if (recortar)
            {
                page.SaveState();
                page.SetClip(PrawnSharp.Path.Create().Rectangle(tableX + cp.XRel, yCell, cp.Ancho, hCell));
            }

            var altoDisponibleCont = Math.Max(hCell - cp.PadSup - cp.PadInf, 0);
            var offsetVert = cp.Celda.VerticalAlign switch
            {
                "middle" => Math.Max((altoDisponibleCont - cp.AltoContenido) / 2.0, 0),
                "bottom" when !tabla.SolapadaPorFondo => Math.Max(altoDisponibleCont - cp.AltoContenido, 0),
                _ => 0,
            };

            var xCont = tableX + cp.XRel + cp.PadIzq;
            var wCont = Math.Max(cp.Ancho - cp.PadIzq - cp.PadDer, 1.0);
            var yCont = yCell + cp.PadSup + offsetVert;

            if (cp.Celda.OrdenContenido.Count > 0)
            {
                foreach (var (esTabla, idxItem) in cp.Celda.OrdenContenido)
                {
                    if (!esTabla)
                    {
                        if (idxItem < cp.Parrafos.Count)
                        {
                            var pm = cp.Parrafos[idxItem];
                            yCont += pm.MargenSupPt;
                            DibujarParrafoEnPagina(page, pm, xCont, wCont, yCont, numPagina, totalPaginas, cp.Celda.ColorFondo);
                            yCont += pm.AltoContenidoPt + pm.MargenInfPt;
                        }
                    }
                    else if (_dibujarTablasAnidadas && idxItem < cp.Celda.TablasAnidadas.Count)
                    {
                        yCont = DibujarTablaFijaEnPagina(page, cp.Celda.TablasAnidadas[idxItem], xCont, yCont, wCont, numPagina, totalPaginas);
                    }
                }
            }
            else
            {
                foreach (var pm in cp.Parrafos)
                {
                    yCont += pm.MargenSupPt;
                    DibujarParrafoEnPagina(page, pm, xCont, wCont, yCont, numPagina, totalPaginas, cp.Celda.ColorFondo);
                    yCont += pm.AltoContenidoPt + pm.MargenInfPt;
                }

                if (_dibujarTablasAnidadas && cp.Celda.TablasAnidadas.Count > 0)
                {
                    foreach (var an in cp.Celda.TablasAnidadas)
                        yCont = DibujarTablaFijaEnPagina(page, an, xCont, yCont, wCont, numPagina, totalPaginas);
                }
            }

            foreach (var marco in cp.Celda.MarcosFlotantes)
            {
                var copia = new CapaModelo
                {
                    Nombre = marco.Nombre,
                    Tipo = marco.Tipo,
                    XPt = tableX + cp.XRel + marco.XPt,
                    YPt = yCell + marco.YPt,
                    AnchoPt = marco.AnchoPt,
                    AltoPt = marco.AltoPt,
                    Ancla = "calibrada",
                    Parrafos = marco.Parrafos,
                    Tablas = marco.Tablas,
                    ImagenBase64 = marco.ImagenBase64,
                    ColorRelleno = marco.ColorRelleno,
                    BordeCaja = marco.BordeCaja,
                    ColorBorde = marco.ColorBorde,
                    GrosorBordePt = marco.GrosorBordePt,
                    PaddingIzqPt = marco.PaddingIzqPt,
                    PaddingDerPt = marco.PaddingDerPt,
                    PaddingSupPt = marco.PaddingSupPt,
                    PaddingInfPt = marco.PaddingInfPt,
                };
                DibujarCapa(_paginaActual, copia, esFondo: false);
            }

            if (recortar) page.RestoreState();
        }

        // 3. Bordes encima de fondos y contenido
        foreach (var cp in delGrupo)
        {
            var yCell = yPorFila[cp.Fila - filaInicio];
            double hCell = 0;
            for (var rr = cp.Fila; rr < cp.Fila + cp.SpanFilas; rr++) hCell += rowHeights[rr];
            DibujarBordesCelda(page, cp, tableX + cp.XRel, yCell, cp.Ancho, hCell);
        }
    }

    private static void DibujarBordesCelda(IPdfPage page, CeldaPosicionada cp, double x, double y, double w, double h)
    {
        if (cp.Celda.BordeSup && cp.GrosorSup > 0.05)
            page.DrawLine(new Point(x, y), new Point(x + w, y),
                StrokeStyle.Create(ParseColor(cp.Celda.ColorBordeSup ?? "#000000"), cp.GrosorSup));

        if (cp.Celda.BordeInf && cp.GrosorInf > 0.05)
            page.DrawLine(new Point(x, y + h), new Point(x + w, y + h),
                StrokeStyle.Create(ParseColor(cp.Celda.ColorBordeInf ?? "#000000"), cp.GrosorInf));

        if (cp.Celda.BordeIzq && cp.GrosorIzq > 0.05)
            page.DrawLine(new Point(x, y), new Point(x, y + h),
                StrokeStyle.Create(ParseColor(cp.Celda.ColorBordeIzq ?? "#000000"), cp.GrosorIzq));

        if (cp.Celda.BordeDer && cp.GrosorDer > 0.05)
            page.DrawLine(new Point(x + w, y), new Point(x + w, y + h),
                StrokeStyle.Create(ParseColor(cp.Celda.ColorBordeDer ?? "#000000"), cp.GrosorDer));
    }

    // -------------------------------------------------------------------------
    // Utilidades de métricas, color e imágenes
    // -------------------------------------------------------------------------

    private IFontMetrics GetMetrics(FontFace face)
    {
        if (!_metricsCache.TryGetValue(face, out var met))
        {
            met = _backend.GetFontMetrics(face);
            _metricsCache[face] = met;
        }
        return met;
    }

    private double MedirTexto(ResolvedFont resolved, string texto, double tamanoPt,
                              bool kerning = true, double espaciadoCaracteresPt = 0)
    {
        if (string.IsNullOrEmpty(texto) || tamanoPt <= 0) return 0;
        var met = GetMetrics(resolved.Face);
        var w = met is PureFontMetrics pfm
            ? pfm.ExactWidthOf(texto, tamanoPt, kerning: kerning)
            : met.WidthOf(texto, tamanoPt, kerning: kerning);
        if (Math.Abs(espaciadoCaracteresPt) > 1e-6)
            w += espaciadoCaracteresPt * texto.Length;
        return Math.Max(0, w);
    }

    /// <summary>
    /// Contraste automático ODF (§20.385 style:use-window-font-color="true"): en LibreOffice Writer,
    /// el texto de color automático se pinta blanco cuando su contenedor tiene un fondo oscuro.
    /// </summary>
    private static bool EsFondoOscuro(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim().TrimStart('#');
        if (s.Length != 6) return false;
        if (!byte.TryParse(s[0..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) ||
            !byte.TryParse(s[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) ||
            !byte.TryParse(s[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            return false;
        var lum = 0.299 * r + 0.587 * g + 0.114 * b;
        return lum < 128.0;
    }

    private ImageData TryDecodeImage(string base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        if (_imageCache.TryGetValue(base64, out var cached)) return cached;

        try
        {
            var bytes = Convert.FromBase64String(base64);
            var img = ImageData.FromBytes(bytes);
            _imageCache[base64] = img;
            return img;
        }
        catch
        {
            return null;
        }
    }

    private static PdfColor ParseColor(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return PdfColor.Black;
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 6 &&
            byte.TryParse(s[0..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) &&
            byte.TryParse(s[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) &&
            byte.TryParse(s[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return new PdfColor(r, g, b);
        }
        return PdfColor.Black;
    }
}
