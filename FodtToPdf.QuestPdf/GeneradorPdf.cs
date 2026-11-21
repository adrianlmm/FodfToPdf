using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace FodtToPdf;

// Renderiza el modelo con QuestPDF para medir fidelidad contra el PDF de LibreOffice.
//
// NOTA sobre la API: QuestPDF 2026 ya no expone un canvas pÃºblico (el handler de
// ElementExtensions.Canvas recibe 'object'), asÃ­ que las capas con posiciÃ³n absoluta
// se reproducen apilando huecos de altura exacta (spacers) + PaddingLeft.
// El resultado en el PDF es el mismo: cada capa cae en su coordenada.
internal static class GeneradorPdf
{
    // Tablas dentro de celdas: en los REPORTES (orden de compra, entrega de facturas) son
    // la cabecera de columnas y hay que dibujarlas. En las FACTURAS hay tablas anidadas
    // que LibreOffice no muestra: pintarlas cambia el aspecto ya validado por el usuario.
    // Por eso se decide por documento (ver ADocumento), no con una constante.
    static bool _dibujarTablasAnidadas;

    public static void Generar(ModeloPlantilla m, string rutaPdf) => ADocumento(m).GeneratePdf(rutaPdf);

    // Devuelve el documento QuestPDF ya armado: asÃ­ el controlador puede pedir los bytes
    // (GeneratePdf() sin ruta) en vez de escribir a disco.
    //
    // dibujarTablasAnidadas: en los REPORTES (orden de compra) las tablas dentro de celdas
    // son el bloque de cabecera y hay que dibujarlas; en las FACTURAS hay tablas anidadas
    // que LibreOffice no muestra y pintarlas cambia el aspecto ya validado.
    public static QuestPDF.Fluent.Document ADocumento(ModeloPlantilla m, bool dibujarTablasAnidadas = true)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        // NOTA: se probÃ³ a NO desplazar el flujo (dejar el margen negativo sÃ³lo a las
        // tablas) pensando que acercarÃ­a el acomodo al de LibreOffice. EmpeorÃ³ los
        // reportes ya validados (liquidaciones 108035 -> 116262 px de diferencia contra
        // LibreOffice) y en los contratos quedÃ³ igual, asÃ­ que se deja como estaba.
        _desplazamientoGlobal = m.DesplazamientoIzqPt;
        _margenIzqPagina = m.Pagina.MargenIzqPt;
        _altoUtilPagina = Math.Max(m.Pagina.AltoPt - m.Pagina.MargenSupPt - m.Pagina.MargenInfPt, 100);
        _justificarConEncogimiento = m.JustificarConEncogimiento;
        var margenDerBase = Math.Max(m.Pagina.MargenDerPt + m.DesplazamientoIzqPt, 0);
        var inicioFlujo = m.Pagina.MargenIzqPt + m.DesplazamientoIzqPt;
        var anchoBaseFlujo = m.Pagina.AnchoPt - inicioFlujo - margenDerBase;
        var anchoFisicoDisponible = Math.Max(m.Pagina.AnchoPt - inicioFlujo - 4, 0);
        // Expande el margen derecho del flujo cuando alguna tabla con margen izquierdo
        // negativo excede el ancho útil de página (p.ej. -0.191 cm en ComprobanteTraslado
        // o -1.8 cm en reportes). Los párrafos de texto restauran el margen derecho real
        // mediante _compensacionDerFlujo para no alterar sus saltos de línea.
        const double umbralTablaAnchaPt = 4.0;
        var anchoTablaConMargenNegativo = m.Flujo
            .Where(b => b.Tipo == "tabla" && b.Tabla != null &&
                        (b.Tabla.MargenIzqPt <= -umbralTablaAnchaPt ||
                         (b.Tabla.AlineacionTabla == "center" &&
                          (b.Tabla.AnchoDeclaradoPt > 0 ? b.Tabla.AnchoDeclaradoPt : b.Tabla.ColumnasPt.Sum()) > anchoBaseFlujo + 0.5)))
            .Select(b => b.Tabla.AnchoDeclaradoPt > 0 ? b.Tabla.AnchoDeclaradoPt : b.Tabla.ColumnasPt.Sum())
            .DefaultIfEmpty(anchoBaseFlujo)
            .Max();
        var expansionParaTabla = Math.Min(
            Math.Max(anchoTablaConMargenNegativo - anchoBaseFlujo, 0),
            Math.Max(anchoFisicoDisponible - anchoBaseFlujo, 0));
        _margenDerechoFlujo = Math.Max(margenDerBase - expansionParaTabla, 0);
        _compensacionDerFlujo = Math.Max(m.Pagina.MargenDerPt - _margenDerechoFlujo, 0);


        // se anclan al flujo ANTES de armar los lookups de abajo: AsÃ­ se sabe quÃ©
        // capas arrancan por encima de su pÃ¡rrafo ancla (y por lo tanto quÃ© hueco hay
        // que dejar en el flujo). Las coordenadas se ajustan una sola vez.
        foreach (var capaAncla in m.Capas)
            AnclarAlFlujo(capaAncla, m);

        _capasQueReservan = new Dictionary<BloqueModelo, double>();

        foreach (var capaReserva in m.Capas.Where(x => x.ReservarFlujoPt > 0.5 && x.BloqueAncla >= 0 && x.BloqueAncla < m.Flujo.Count))
            _capasQueReservan[m.Flujo[capaReserva.BloqueAncla]] = capaReserva.ReservarFlujoPt;

        _lineasPorBloque = new Dictionary<BloqueModelo, List<CapaModelo>>();

        foreach (var capaLinea in m.Capas.Where(x =>
                     !string.IsNullOrEmpty(x.ImagenBase64) &&
                     !x.EnFlujo && !x.AnclaEnTabla &&
                     x.Ancla == "char" && x.BloqueAncla > 0 && x.BloqueAncla < m.Flujo.Count &&
                     (x.VerticalRel == "paragraph" || x.VerticalRel == "char") && x.YPt >= 0))
        {
            var bloqueLinea = m.Flujo[capaLinea.BloqueAncla];

            // sÃ³lo pÃ¡rrafos VACÃOS: son los que el autor usÃ³ de espaciador; si el ancla
            // fuera un pÃ¡rrafo con texto, al dibujarla en el flujo se perderÃ­a el texto
            if (bloqueLinea.Tipo != "espacio") continue;

            if (!_lineasPorBloque.TryGetValue(bloqueLinea, out var listaLineas))
                _lineasPorBloque[bloqueLinea] = listaLineas = new List<CapaModelo>();

            listaLineas.Add(capaLinea);
        }

        // Marcos flotantes anclados a un párrafo del flujo (text:anchor-type="char"/"paragraph"
        // con style:vertical-rel="paragraph"/"char"): sus svg:x/svg:y son relativos al párrafo
        // ancla, no a la hoja (ODF 1.3 §20.398 / LibreOffice SwToCntntAnchoredObjectPosition).
        // Si se pintan en la capa absoluta de página, caen arriba de la hoja (p.ej. Marco1 del
        // QR en el formato de dividendos tapando el borde superior de "Nacionalidad").
        _marcosFlotantesPorBloque = new Dictionary<BloqueModelo, List<CapaModelo>>();

        foreach (var capaFlotante in m.Capas.Where(x =>
                     !x.EnFlujo && !x.AnclaEnTabla &&
                     !x.Ancla.Contains("calibrada") &&
                     (x.Ancla == "char" || x.Ancla == "paragraph") &&
                     x.BloqueAncla > 0 && x.BloqueAncla < m.Flujo.Count &&
                     (x.VerticalRel == "paragraph" || x.VerticalRel == "paragraph-content" || x.VerticalRel == "char") &&
                     x.YPt >= 0))
        {
            if (_lineasPorBloque.Values.Any(l => l.Contains(capaFlotante))) continue;

            var bloqueFlotante = m.Flujo[capaFlotante.BloqueAncla];

            if (!_marcosFlotantesPorBloque.TryGetValue(bloqueFlotante, out var listaFlotantes))
                _marcosFlotantesPorBloque[bloqueFlotante] = listaFlotantes = new List<CapaModelo>();

            listaFlotantes.Add(capaFlotante);
        }

        _capasPorBloque = new Dictionary<BloqueModelo, List<CapaModelo>>();

        foreach (var capaFlujo in m.Capas.Where(x => x.AnclaEnTabla && x.BloqueAncla >= 0 && x.BloqueAncla < m.Flujo.Count))
        {
            var bloqueAncla = m.Flujo[capaFlujo.BloqueAncla];

            if (!_capasPorBloque.TryGetValue(bloqueAncla, out var lista))
                _capasPorBloque[bloqueAncla] = lista = new List<CapaModelo>();

            lista.Add(capaFlujo);
        }
        _dibujarTablasAnidadas = dibujarTablasAnidadas;

        // PaginaciÃ³n propia: sÃ³lo para documentos de UNA columna. En secciones con
        // columnas el reparto lo sigue haciendo QuestPDF.
        _cortesPropios = new Dictionary<BloqueModelo, bool>();

        var unaColumna = m.Secciones.Count == 0 || m.Secciones.All(s => s.Columnas <= 1);

        if (CortesPropiosActivo && unaColumna && m.Flujo.Count > 0)
        {
            var izqPlan = m.Pagina.MargenIzqPt + m.DesplazamientoIzqPt;
            var derPlan = _margenDerechoFlujo;
            var anchoPlan = m.Pagina.AnchoPt - izqPlan - derPlan;
            var altoPlan = m.Pagina.AltoPt - m.Pagina.MargenSupPt - m.Pagina.MargenInfPt;

            var cortes = PaginarPropio.Cortes(m, altoPlan, anchoPlan);

            // con un solo corte no se gana nada y sÃ­ hay riesgo de desacomodar
            if (cortes.Count > 0) _cortesPropios = cortes;
        }

        var segmentos = SegmentosPorPaginaMaestra(m);

        // QuestPDF 2026 ya no usa las fuentes del sistema por defecto: sin esto,
        // Arial/Calibri lanzan DocumentDrawingException. Para el prototipo usamos
        // las del sistema (en producciÃ³n habrÃ­a que EMBEBER las TTF exactas).
        QuestPDF.Settings.UseSystemFonts = true;

        // Algunas plantillas piden familias que no estÃ¡n instaladas ("Bodoni MT Black",
        // "Batang", "Lucida Sans"...). Desde 2026 QuestPDF lanza DocumentDrawingException
        // en vez de sustituirlas, asÃ­ que se le dice que siga y use la primera disponible:
        // sin esto, las cartas y el pagarÃ© (que ya no tronaban) volvÃ­an a fallar en cuanto
        // se empezÃ³ a pintar el encabezado del master-page.
        QuestPDF.Settings.ThrowOnMissingFontFamilies = false;

        return Document.Create(doc =>
        {
            for (var idxSeg = 0; idxSeg < segmentos.Count; idxSeg++)
            {
                var (maestra, bloquesSeg) = segmentos[idxSeg];
                var pg = maestra.Pagina;
                var esPrimerSegmento = idxSeg == 0;
                var mobiliarioSeg = maestra.Encabezado.Count > 0;

                // Ajusta las variables de página activas para este segmento (ControlDeViudas y
                // DibujarMarcosFlotantes capturan estos valores al componer los elementos).
                _margenIzqPagina = pg.MargenIzqPt;
                _altoUtilPagina = Math.Max(pg.AltoPt - pg.MargenSupPt - pg.MargenInfPt, 100);
                var izqFlujo = pg.MargenIzqPt + m.DesplazamientoIzqPt;
                var margenDerSeg = Math.Max(pg.MargenDerPt + m.DesplazamientoIzqPt, 0);
                var anchoBaseSeg = pg.AnchoPt - izqFlujo - margenDerSeg;
                var anchoFisicoSeg = Math.Max(pg.AnchoPt - izqFlujo - 4, 0);
                var anchoTablaNegSeg = bloquesSeg
                    .Where(b => b.Tipo == "tabla" && b.Tabla != null &&
                                (b.Tabla.MargenIzqPt <= -umbralTablaAnchaPt ||
                                 (b.Tabla.AlineacionTabla == "center" &&
                                  (b.Tabla.AnchoDeclaradoPt > 0 ? b.Tabla.AnchoDeclaradoPt : b.Tabla.ColumnasPt.Sum()) > anchoBaseSeg + 0.5)))
                    .Select(b => b.Tabla.AnchoDeclaradoPt > 0 ? b.Tabla.AnchoDeclaradoPt : b.Tabla.ColumnasPt.Sum())
                    .DefaultIfEmpty(anchoBaseSeg)
                    .Max();
                var expSeg = Math.Min(
                    Math.Max(anchoTablaNegSeg - anchoBaseSeg, 0),
                    Math.Max(anchoFisicoSeg - anchoBaseSeg, 0));
                var derFlujo = Math.Max(margenDerSeg - expSeg, 0);
                _margenDerechoFlujo = derFlujo;
                _compensacionDerFlujo = Math.Max(pg.MargenDerPt - derFlujo, 0);

                doc.Page(pagina =>
                {
                    pagina.Size(new PageSize((float)pg.AnchoPt, (float)pg.AltoPt));
                    pagina.Margin(0);
                    pagina.DefaultTextStyle(t => t.FontSize(9).FontFamily("Arial"));

                    // El pie se declara como Footer de pÃ¡gina, no como una capa mÃ¡s del
                    // contenido: QuestPDF reserva su alto en TODAS las hojas. Antes era una
                    // capa AlignBottom y una tabla con muchas partidas podÃ­a dibujarse encima
                    // de la "Referencia" del footer.
                    if (!mobiliarioSeg && maestra.Pie.Count > 0)
                    {
                        pagina.Footer()
                            .PaddingLeft((float)pg.MargenIzqPt)
                            .PaddingRight((float)pg.MargenDerPt)
                            .PaddingBottom((float)Math.Max(pg.MargenInfBasePt, 0))
                            .Column(c =>
                            {
                                foreach (var p in maestra.Pie) AgregarParrafo(c, p);
                            });
                    }

                    // Todos los fondos (marcas de agua, números de página y mobiliario)
                    // deben compartir UNA sola llamada a Page.Background(): QuestPDF sólo
                    // permite definir esa capa una vez por página.
                    var hayFondo = maestra.MarcasAgua.Count > 0 ||
                                   maestra.MarcasAguaTexto.Count > 0 ||
                                   maestra.NumerosPagina.Count > 0 ||
                                   mobiliarioSeg;

                    if (hayFondo)
                    {
                        pagina.Background().Layers(capasFondo =>
                        {
                            var primera = true;

                            IContainer Siguiente()
                            {
                                var capa = primera ? capasFondo.PrimaryLayer() : capasFondo.Layer();
                                primera = false;
                                return capa;
                            }

                            if (maestra.MarcasAgua.Count > 0)
                            {
                                foreach (var marca in maestra.MarcasAgua)
                                {
                                    if (string.IsNullOrWhiteSpace(marca.ImagenBase64)) continue;
                                    var bytes = Convert.FromBase64String(marca.ImagenBase64);

                                    if (string.Equals(marca.ModoColor, "watermark", StringComparison.OrdinalIgnoreCase))
                                        bytes = AtenuarMarcaAgua(bytes);

                                    var baseIzq = string.Equals(marca.HorizontalRel, "page-content", StringComparison.OrdinalIgnoreCase) ||
                                                  string.Equals(marca.HorizontalRel, "paragraph", StringComparison.OrdinalIgnoreCase) ||
                                                  string.Equals(marca.HorizontalRel, "paragraph-content", StringComparison.OrdinalIgnoreCase);
                                    var izq = baseIzq ? pg.MargenIzqPt : 0;

                                    var baseSup = string.Equals(marca.VerticalRel, "page-content", StringComparison.OrdinalIgnoreCase);
                                    var sup = baseSup ? pg.MargenSupPt : 0;
                                    var anchoArea = pg.AnchoPt - pg.MargenIzqPt - pg.MargenDerPt;
                                    var altoArea = pg.AltoPt - pg.MargenSupPt - pg.MargenInfPt;

                                    double x = string.Equals(marca.HorizontalPos, "center", StringComparison.OrdinalIgnoreCase)
                                        ? izq + (anchoArea - marca.AnchoPt) / 2
                                        : izq + marca.XPt;
                                    double y = string.Equals(marca.VerticalPos, "middle", StringComparison.OrdinalIgnoreCase)
                                        ? sup + (altoArea - marca.AltoPt) / 2
                                        : sup + marca.YPt;

                                    y = Math.Max(y, 0);

                                    Siguiente().Element(cont => cont
                                        .PaddingLeft((float)x)
                                        .PaddingTop((float)y)
                                        .Width((float)marca.AnchoPt)
                                        .Height((float)marca.AltoPt)
                                        .Image(bytes));
                                }
                            }

                            if (maestra.MarcasAguaTexto.Count > 0)
                            {
                                foreach (var marca in maestra.MarcasAguaTexto)
                                {
                                    var anchoCaja = (float)marca.AnchoPt;
                                    var altoCaja = (float)marca.AltoPt;
                                    var imagen = MarcaAguaTextoPng(marca, anchoCaja, altoCaja);

                                    Siguiente().Element(cont => cont
                                        .PaddingLeft((float)marca.XPt)
                                        .PaddingTop((float)marca.YPt)
                                        .Width(anchoCaja)
                                        .Height(altoCaja)
                                        .Image(imagen));
                                }
                            }

                            foreach (var numeroPagina in maestra.NumerosPagina)
                            {
                                var parrafoNumero = numeroPagina.Parrafos.FirstOrDefault();
                                var estiloBase = EstiloDe(parrafoNumero?.Runs ?? new List<RunModelo>(),
                                                          parrafoNumero?.AltoLineaPt ?? 0);
                                var altoNumero = numeroPagina.AltoPt > 0 ? numeroPagina.AltoPt : 14.4;

                                if (numeroPagina.EnPie)
                                {
                                    // Número de página en style:footer (p.ej. Marco2 en Converted2 del CCI)
                                    var yPie = Math.Max(pg.AltoPt - pg.MargenInfBasePt - Math.Max(pg.AltoPiePt, 27.6) + numeroPagina.YPt, 0);
                                    var alineadoDer = string.Equals(numeroPagina.HorizontalPos, "right", StringComparison.OrdinalIgnoreCase);
                                    var conFondo = !string.IsNullOrEmpty(numeroPagina.ColorRelleno);
                                    var estiloPie = conFondo ? estiloBase.FontColor(Colors.White) : estiloBase;

                                    Siguiente().Element(cont =>
                                    {
                                        IContainer caja = cont.PaddingTop((float)yPie);
                                        if (alineadoDer)
                                        {
                                            caja = caja.PaddingLeft((float)pg.MargenIzqPt)
                                                       .PaddingRight((float)pg.MargenDerPt)
                                                       .AlignRight();
                                        }
                                        else
                                        {
                                            caja = caja.PaddingLeft((float)numeroPagina.XPt);
                                        }

                                        if (conFondo)
                                            caja = caja.Background(ColorDe(numeroPagina.ColorRelleno));

                                        caja.DefaultTextStyle(estiloPie)
                                            .Text(t => t.CurrentPageNumber());
                                    });
                                }
                                else
                                {
                                    var estiloNumero = estiloBase.FontColor(Colors.White);

                                    Siguiente().Element(cont => cont
                                        .PaddingLeft((float)numeroPagina.XPt)
                                        .PaddingTop((float)numeroPagina.YPt)
                                        .Width((float)Math.Max(numeroPagina.AnchoPt, 1))
                                        .Height((float)altoNumero)
                                        .Background(ColorDe(numeroPagina.ColorRelleno ?? "#4f81bd"))
                                        .PaddingLeft(7.26f)
                                        .PaddingTop(0.06f)
                                        .DefaultTextStyle(estiloNumero)
                                        .Text(t => t.CurrentPageNumber()));
                                }
                            }

                            if (mobiliarioSeg)
                            {
                                var capaMobiliario = Siguiente();

                                capaMobiliario.Column(c =>
                                {
                                    c.Item()
                                     .PaddingTop((float)Math.Max(pg.MargenSupBasePt - 0.5, 0))
                                     .PaddingRight((float)pg.MargenDerPt)
                                     .Column(cc =>
                                     {
                                         foreach (var p in maestra.Encabezado)
                                             AgregarParrafo(cc, p, pg.MargenIzqPt + p.MargenIzqPt);
                                     });

                                    if (maestra.Pie.Count > 0)
                                        c.Item().ExtendVertical().AlignBottom()
                                         .PaddingRight((float)pg.MargenDerPt)
                                         .PaddingBottom((float)Math.Max(PieAbajo(pg), 0))
                                         .Column(cc =>
                                         {
                                             foreach (var p in maestra.Pie)
                                                 AgregarParrafo(cc, p, pg.MargenIzqPt + p.MargenIzqPt);
                                         });
                                });
                            }
                        });
                    }

                    pagina.Content().Layers(capas =>
                    {
                        // una capa POR CADA caja con posiciÃ³n absoluta: los layers de QuestPDF
                        // son independientes, asÃ­ una imagen alta (el logo) no empuja a las demÃ¡s
                        if (esPrimerSegmento)
                        {
                            foreach (var capa in m.Capas)
                            {
                                // las ancladas as-char se dibujan dentro del flujo: su posiciÃ³n
                                // depende del texto de arriba, no de una coordenada fija
                                // las ancladas dentro de una tabla se dibujan en el flujo, tras su tabla
                                if (capa.EnFlujo || capa.AnclaEnTabla) continue;
                                if (_lineasPorBloque.Values.Any(l => l.Contains(capa))) continue;
                                if (_marcosFlotantesPorBloque.Values.Any(l => l.Contains(capa))) continue;

                                var actual = capa;
                                capas.Layer().Element(cont => DibujarCapa(cont, actual));
                            }
                        }

                        capas.PrimaryLayer()
                             .PaddingLeft((float)izqFlujo)
                             .PaddingRight((float)derFlujo)
                             .PaddingTop((float)pg.MargenSupPt)
                             .PaddingBottom((float)(!mobiliarioSeg && maestra.Pie.Count > 0 ? 0 : pg.MargenInfPt))
                             .Column(col =>
                             {
                                 var anchoUtil = pg.AnchoPt - izqFlujo - derFlujo;

                                 foreach (var grupo in PorSeccion(bloquesSeg))
                                 {
                                     var sec = grupo.Key >= 0 && grupo.Key < m.Secciones.Count ? m.Secciones[grupo.Key] : null;

                                     if (sec == null || sec.Columnas <= 1 || sec.RelAnchos.Count < 2)
                                     {
                                         col.Item().Column(c => AgregarFlujo(c, grupo.Value, anchoUtil));
                                         continue;
                                     }

                                     // secciÃ³n con varias columnas: se reparten los bloques
                                     // por alto estimado y cada columna toma su ancho relativo
                                     var total = sec.RelAnchos.Sum();
                                     var reparto = Repartir(grupo.Value, sec.Columnas);

                                     if (Environment.GetEnvironmentVariable("FODT_DIAG") == "1")
                                     {
                                         for (var d = 0; d < reparto.Count; d++)
                                         {
                                             var muestra = string.Join(" | ", reparto[d]
                                                 .Where(b => b.Tipo == "texto" && b.Parrafo != null)
                                                 .Take(3)
                                                 .Select(b => string.Concat(b.Parrafo.Runs.Select(r => r.Texto)).Trim()));

                                             Console.Error.WriteLine($"DIAG col{d}: {reparto[d].Count} bloques, rotulos={EsColumnaDeRotulos(reparto[d])} :: {muestra}");
                                         }
                                     }

                                     col.Item().Row(row =>
                                     {
                                         for (var i = 0; i < sec.Columnas; i++)
                                         {
                                             var bloques = i < reparto.Count ? reparto[i] : new List<BloqueModelo>();
                                             var peso = (float)(total <= 0 ? 1 : sec.RelAnchos[Math.Min(i, sec.RelAnchos.Count - 1)] / total);
                                             var startCol = i < sec.MargenIzqColPt.Count ? sec.MargenIzqColPt[i] : 0;
                                             var endCol = i < sec.MargenDerColPt.Count ? sec.MargenDerColPt[i] : 0;
                                             var anchoColumna = Math.Max(anchoUtil * peso - startCol - endCol, 10);

                                             // Si esta columna son RÃ“TULOS (lÃ­neas que acaban en ":") y
                                             // la siguiente son sus VALORES, se pintan emparejados:
                                             // asÃ­ cada rÃ³tulo queda en la misma lÃ­nea que su valor,
                                             // que es lo que hace LibreOffice con las 3 columnas.
                                             var siguiente = i + 1 < reparto.Count ? reparto[i + 1] : null;

                                             if (i > 0 && siguiente != null && EsColumnaDeRotulos(bloques))
                                             {
                                                 var pesoValor = (float)(total <= 0 ? 1 : sec.RelAnchos[Math.Min(i + 1, sec.RelAnchos.Count - 1)] / total);
                                                 var startValor = i + 1 < sec.MargenIzqColPt.Count ? sec.MargenIzqColPt[i + 1] : 0;
                                                 var endValor = i + 1 < sec.MargenDerColPt.Count ? sec.MargenDerColPt[i + 1] : 0;

                                                 row.RelativeItem(Math.Max(peso + pesoValor, 0.01f))
                                                    .Column(c => EmparejarRotulosValores(c, bloques, siguiente, peso, pesoValor, startCol, endCol, startValor, endValor));
                                                 i++;   // los valores ya se han pintado
                                                 continue;
                                             }

                                             row.RelativeItem(Math.Max(peso, 0.01f))
                                                .PaddingLeft((float)Math.Max(startCol, 0))
                                                .PaddingRight((float)Math.Max(endCol, 0))
                                                .Column(c => AgregarFlujo(c, bloques, anchoColumna));
                                         }
                                     });
                                 }
                             });
                    });
                });
            }
        });
    }

    // Parte el flujo en segmentos cada vez que un párrafo cambia de style:master-page-name
    // (ODF 1.3 §16.2: implica salto de página y cambio de page-layout, encabezado y pie).
    static List<(PaginaMaestraModelo Maestra, List<BloqueModelo> Bloques)> SegmentosPorPaginaMaestra(ModeloPlantilla m)
    {
        var inicial = !string.IsNullOrEmpty(m.PaginaMaestraInicial) &&
                      m.PaginasMaestras.TryGetValue(m.PaginaMaestraInicial, out var pmIni)
            ? pmIni
            : new PaginaMaestraModelo
            {
                Nombre = "Standard",
                Pagina = m.Pagina,
                Encabezado = m.Encabezado,
                Pie = m.Pie,
                MarcasAgua = m.MarcasAgua,
                NumerosPagina = m.NumerosPagina,
                MarcasAguaTexto = m.MarcasAguaTexto,
            };

        var salida = new List<(PaginaMaestraModelo Maestra, List<BloqueModelo> Bloques)>();
        var actualMaestra = inicial;
        var actualBloques = new List<BloqueModelo>();

        foreach (var bloque in m.Flujo)
        {
            var nombreMaestra = bloque.Parrafo?.PaginaMaestra;
            if (!string.IsNullOrEmpty(nombreMaestra) &&
                m.PaginasMaestras.TryGetValue(nombreMaestra, out var nuevaMaestra))
            {
                if (actualBloques.Count > 0)
                {
                    salida.Add((actualMaestra, actualBloques));
                    actualBloques = new List<BloqueModelo>();
                }
                actualMaestra = nuevaMaestra;
            }

            actualBloques.Add(bloque);
        }

        if (actualBloques.Count > 0 || salida.Count == 0)
            salida.Add((actualMaestra, actualBloques));

        return salida;
    }

    // agrupa el flujo por secciÃ³n conservando el orden de apariciÃ³n
    static List<KeyValuePair<int, List<BloqueModelo>>> PorSeccion(List<BloqueModelo> flujo)
    {
        var salida = new List<KeyValuePair<int, List<BloqueModelo>>>();

        foreach (var bloque in flujo)
        {
            if (salida.Count == 0 || salida[^1].Key != bloque.Seccion)
                salida.Add(new KeyValuePair<int, List<BloqueModelo>>(bloque.Seccion, new List<BloqueModelo>()));

            salida[^1].Value.Add(bloque);
        }

        return salida;
    }

    // Â¿la columna son rÃ³tulos? (la mayorÃ­a de sus lÃ­neas acaban en ":")
    static bool EsColumnaDeRotulos(List<BloqueModelo> bloques)
    {
        var textos = bloques.Where(b => b.Tipo == "texto" && b.Parrafo != null).ToList();
        if (textos.Count == 0) return false;

        var conDosPuntos = textos.Count(t =>
        {
            var s = string.Concat(t.Parrafo.Runs.Select(r => r.Slot != null ? "" : r.Texto)).Trim();
            return s.EndsWith(":");
        });

        return conDosPuntos >= Math.Max(2, textos.Count * 0.6);
    }

    // Pinta rÃ³tulo y valor EMPAREJADOS en la misma fila (lo que hace LibreOffice con las
    // columnas 2 y 3 de la factura). Sin esto, los rÃ³tulos quedan en una columna y los
    // valores en otra, y cada valor cae una o dos lÃ­neas por debajo de su rÃ³tulo.
    static void EmparejarRotulosValores(ColumnDescriptor col, List<BloqueModelo> rotulos,
                                        List<BloqueModelo> valores, float pesoRotulo, float pesoValor,
                                        double startRotuloPt = 0, double endRotuloPt = 0,
                                        double startValorPt = 0, double endValorPt = 0)
    {
        var etiquetas = rotulos.Where(b => b.Tipo == "texto" && b.Parrafo != null).ToList();
        var datos = valores.Where(b => b.Tipo == "texto" && b.Parrafo != null).ToList();

        // El FOLIO (bookmarks tipoDocumento/doctoSAP: "FACTURA" y "12392420") va ARRIBA del
        // bloque en LibreOffice (y=761 con la secciÃ³n empezando arriba), no emparejado con
        // ningÃºn rÃ³tulo. Si se deja en la lista de valores se empareja mal y acaba encima
        // del UUID. Se pinta primero, en su propia lÃ­nea.
        static bool EsFolio(BloqueModelo b) =>
            b.Parrafo != null && b.Parrafo.Runs.Any(r => r.Slot == "doctoSAP" || r.Slot == "tipoDocumento");

        var cabecera = datos.Where(EsFolio).ToList();
        datos = datos.Where(b => !EsFolio(b)).ToList();

        // El bloque no arranca en el borde del Ã¡rea: los espaciadores iniciales del .fodt
        // lo bajan hasta su sitio. Como la cabecera (folio) se pinta antes de la sangría
        // dentro de esta columna combinada, su alto se descuenta de los espaciadores de
        // rótulos (que empiezan desde el tope de la sección, sin cabecera).
        var altoCabecera = cabecera.Sum(AltoEstimado);
        var sangria = Math.Max(Math.Max(AltoInicial(rotulos) - altoCabecera, 0), AltoInicial(valores));
        // El FOLIO va PRIMERO, pegado al borde superior de la secciÃ³n (en LibreOffice estÃ¡
        // en y=761, arriba del todo), y la sangrÃ­a se aplica DESPUÃ‰S: si se aplica antes,
        // el folio baja y se planta encima del UUID.
        foreach (var c in cabecera) col.Item().Column(cc => AgregarParrafo(cc, c.Parrafo));

        if (sangria > 0.5) col.Item().Height((float)sangria);

        var pares = Math.Min(etiquetas.Count, datos.Count);
        var suma = Math.Max(pesoRotulo + pesoValor, 0.01f);

        // ritmo entre pares: en LibreOffice el paso es 11.4 pt con texto de 6.5
        // (linea ~7.2 + ~4.2 de separaciÃ³n)
        var altoLinea = etiquetas.Count > 0 ? Math.Max(etiquetas[0].Parrafo.AltoLineaPt, 6) : 7.2;
        var separacion = (float)Math.Max(11.4 - altoLinea, 0);

        for (var k = 0; k < pares; k++)
        {
            var etiqueta = etiquetas[k];
            var valor = datos[k];

            if (k > 0 && separacion > 0.5) col.Item().Height(separacion);

            col.Item().Row(r =>
            {
                r.RelativeItem(Math.Max(pesoRotulo / suma, 0.01f))
                 .PaddingLeft((float)Math.Max(startRotuloPt, 0))
                 .PaddingRight((float)Math.Max(endRotuloPt, 0))
                 .Column(c => AgregarParrafo(c, etiqueta.Parrafo));
                r.RelativeItem(Math.Max(pesoValor / suma, 0.01f))
                 .PaddingLeft((float)Math.Max(startValorPt, 0))
                 .PaddingRight((float)Math.Max(endValorPt, 0))
                 .Column(c => AgregarParrafo(c, valor.Parrafo));
            });
        }

        // lo que sobre (mÃ¡s rÃ³tulos o mÃ¡s valores) se pinta debajo, sin emparejar
        foreach (var e in etiquetas.Skip(pares)) col.Item().PaddingLeft((float)Math.Max(startRotuloPt, 0)).Column(c => AgregarParrafo(c, e.Parrafo));
        foreach (var d in datos.Skip(pares)) col.Item().PaddingLeft((float)Math.Max(startValorPt, 0)).Column(c => AgregarParrafo(c, d.Parrafo));
    }

    // Alto de los espaciadores iniciales de una columna: sirve para igualar el arranque
    // de todas las columnas de la secciÃ³n.
    static double AltoInicial(List<BloqueModelo> bloques)
    {
        var alto = 0.0;

        foreach (var b in bloques)
        {
            if (b.Tipo != "espacio") break;
            alto += AltoEstimado(b);
        }

        return alto;
    }

    static void AgregarBloque(ColumnDescriptor col, BloqueModelo bloque, double anchoDisponible = 0)
    {
        if (bloque.Tipo == "texto") AgregarParrafo(col, bloque.Parrafo);
        else if (bloque.Tipo == "espacio")
        {
            // un pÃ¡rrafo vacÃ­o puede traer renglones de firma (draw:line de los contratos):
            // el pÃ¡rrafo cae en "espacio" porque no tiene texto, pero la lÃ­nea sÃ­ se pinta
            if (bloque.Parrafo != null && bloque.Parrafo.Lineas.Count > 0)
                DibujarRenglones(col, bloque.Parrafo, bloque.Parrafo.MargenIzqPt - _desplazamientoGlobal, anchoDisponible);

            col.Item().Height((float)Math.Max(bloque.AltoPt, 0.05));
        }
        else AgregarTabla(col.Item(), bloque.Tabla, anchoDisponible);
    }

    static double AltoEstimado(BloqueModelo b)
    {
        if (b.Tipo == "espacio") return b.AltoPt;
        if (b.Tipo == "tabla") return b.Tabla.Filas.Count * 9 + 6;

        // una imagen en el flujo ocupa su propio alto, no el de la lÃ­nea de texto
        if (b.Parrafo != null && !string.IsNullOrEmpty(b.Parrafo.ImagenBase64))
            return Math.Max(b.Parrafo.ImagenAltoPt, b.Parrafo.ImagenAnchoPt);

        return Math.Max(b.Parrafo?.AltoLineaPt ?? 10, 6);
    }

    // reparte los bloques entre las columnas buscando altos parecidos
    static List<List<BloqueModelo>> Repartir(List<BloqueModelo> bloques, int columnas)
    {
        // Cortes explÃ­citos del .fodt (fo:break-before="column"): el autor fijÃ³ dÃ³nde
        // empieza cada columna del encabezado. Es lo que hace LibreOffice, y sin esto el
        // reparto por alturas metÃ­a los espaciadores de la columna 2 en la 1 y el bloque
        // "Folio Fiscalâ€¦" bajaba media pÃ¡gina.
        var explicitos = new List<int>();

        for (var k = 1; k < bloques.Count; k++)
            if (bloques[k].Parrafo?.SaltoColumnaAntes == true) explicitos.Add(k);

        if (explicitos.Count == columnas - 1)
        {
            var porCorte = new List<List<BloqueModelo>>();
            var desde = 0;

            foreach (var corte in explicitos)
            {
                porCorte.Add(bloques.Skip(desde).Take(corte - desde).ToList());
                desde = corte;
            }

            porCorte.Add(bloques.Skip(desde).ToList());

            if (porCorte.All(c => c.Count > 0)) return porCorte;
        }

        // Se probÃ³ una calibraciÃ³n estructural (col1 = cliente, col2 = rÃ³tulos sin bookmark,
        // col3 = valores con bookmark). MEDIDO: empeoraba â€” los valores subÃ­an al principio
        // de la pÃ¡gina (715 en vez de 672) porque el .fodt no agrupa el flujo en ese orden:
        // intercala sellos y cadena entre medias. Se deja el corte por rachas, que es el que
        // deja rÃ³tulos y valores mÃ¡s cerca (16 pt de diferencia frente a 2,5 de LibreOffice).

        // Respaldo: cortar por las RACHAS de pÃ¡rrafos vacÃ­os (el autor posicionÃ³ a mano)
        var rachas = new List<(int inicio, int largo, double alto)>();
        var i = 0;

        while (i < bloques.Count)
        {
            if (bloques[i].Tipo != "espacio") { i++; continue; }

            var inicio = i;
            var alto = 0.0;

            while (i < bloques.Count && bloques[i].Tipo == "espacio")
            {
                alto += AltoEstimado(bloques[i]);
                i++;
            }

            rachas.Add((inicio, i - inicio, alto));
        }

        if (rachas.Count >= columnas - 1)
        {
            // las rachas mÃ¡s altas son los cortes de columna; luego se respeta su orden
            var cortes = rachas.OrderByDescending(r => r.alto).Take(columnas - 1)
                               .Select(r => r.inicio).OrderBy(x => x).ToList();

            var salidaRachas = new List<List<BloqueModelo>>();
            var desde = 0;

            foreach (var corte in cortes)
            {
                salidaRachas.Add(bloques.Skip(desde).Take(corte - desde).ToList());
                desde = corte;
            }

            salidaRachas.Add(bloques.Skip(desde).ToList());

            if (salidaRachas.All(c => c.Count > 0))
            {
                // Las columnas deben ARRANCAR a la misma altura. Si una empieza con pocos
                // espaciadores (< ~20 pt, una o dos lÃ­neas) son residuo del corte y dejan su
                // primer texto por debajo del de la columna anterior: se pasan a la previa.
                // Las rachas grandes sÃ­ son separaciÃ³n intencionada del autor y se respetan.
                const double residuoMaximoPt = 20;

                for (var k = 1; k < salidaRachas.Count; k++)
                {
                    var columna = salidaRachas[k];
                    var n = 0;
                    var alto = 0.0;

                    while (n < columna.Count && columna[n].Tipo == "espacio")
                    {
                        alto += AltoEstimado(columna[n]);
                        n++;
                    }

                    if (n == 0 || alto > residuoMaximoPt) continue;

                    salidaRachas[k - 1].AddRange(columna.Take(n));
                    columna.RemoveRange(0, n);
                }

                if (salidaRachas.All(c => c.Count > 0)) return salidaRachas;
            }
        }

        // Respaldo: reparto por altura (documentos sin rachas claras)
        var total = bloques.Sum(AltoEstimado);
        var objetivo = total / Math.Max(columnas, 1);

        var salida = new List<List<BloqueModelo>>();
        var actual = new List<BloqueModelo>();
        var acumulado = 0.0;

        foreach (var b in bloques)
        {
            var alto = AltoEstimado(b);

            if (actual.Count > 0 && acumulado + alto > objetivo && salida.Count < columnas - 1)
            {
                salida.Add(actual);
                actual = new List<BloqueModelo>();
                acumulado = 0;
            }

            actual.Add(b);
            acumulado += alto;
        }

        salida.Add(actual);
        while (salida.Count < columnas) salida.Add(new List<BloqueModelo>());

        return salida;
    }

    // Una capa con text:anchor-type="char" no trae coordenadas de pÃ¡gina: svg:x/svg:y
    // son RELATIVAS al carÃ¡cter de anclaje. Si ese pÃ¡rrafo es el PRIMER bloque del
    // flujo, la posiciÃ³n del ancla es exactamente el origen del Ã¡rea de texto (margen
    // izquierdo y margen superior), asÃ­ que hay que sumÃ¡rsela. Sin esto la barra
    // naranja que precede a "ORDEN DE COMPRA" salÃ­a ~71 pt a la izquierda y ~49 pt por
    // encima del tÃ­tulo. Las demÃ¡s capas no se tocan: las imÃ¡genes de la factura
    // (logo, QR) viven en celdas de tabla (BloqueAncla = -1) y ya estÃ¡n calibradas por
    // nombre en Calibracion.
    static void AnclarAlFlujo(CapaModelo capa, ModeloPlantilla m)
    {
        if (capa.EnFlujo || capa.AnclaEnTabla || capa.BloqueAncla != 0) return;

        // las que ya tienen un delta medido en Calibracion (logo, QR, lÃ­neas de la
        // factura) no se tocan: su posiciÃ³n final ya estÃ¡ ajustada a mano
        if (capa.Ancla.Contains("calibrada")) return;

        if (capa.Ancla != "char" && capa.Ancla != "paragraph" && capa.Ancla != "") return;

        // anclada al PRIMER bloque del flujo: su posiciÃ³n de anclaje es el origen del
        // texto (margen izquierdo y superior), asÃ­ que hay que sumÃ¡rselo a las coordenadas
        // relativas del marco.
        if (capa.Ancla == "char")
            capa.XPt += m.Pagina.MargenIzqPt;

        // En el ancla "paragraph" sÃ³lo se corrige la Y: la X del marco ya estÃ¡ en
        // coordenadas de la pÃ¡gina (el bloque de la pÃ³liza contable empieza en xâ‰ˆ0 y se
        // sale del Ã¡rea de texto a propÃ³sito).
        // si el marco empieza por ENCIMA del pÃ¡rrafo ancla, el flujo tiene que dejar ese
        // hueco: si no, el texto siguiente se dibuja encima del marco
        if (capa.Ancla == "paragraph" && capa.YPt < 0)
        {
            // sÃ³lo lo que SOBRESALE por debajo del pÃ¡rrafo ancla: el marco empieza |y| pt
            // mÃ¡s arriba, asÃ­ que esa parte ya estÃ¡ cubierta
            var sobra = capa.AltoContenidoPt + capa.YPt;

            if (sobra > 0.5) capa.ReservarFlujoPt = sobra;
        }

        capa.YPt += m.Pagina.MargenSupPt;
    }

    // Dibuja UNA caja absoluta: su hueco vertical desde el borde superior y luego la
    // imagen o el texto. Cada caja se dibuja en su propio layer (ver el llamador).
    // Contenido de una capa anclada a un pÃ¡rrafo dentro de una tabla: se dibuja en el
    // flujo justo despuÃ©s de esa tabla, respetando su x como sangrÃ­a y su ancho.
    static void DibujarCapaEnFlujo(ColumnDescriptor col, CapaModelo capa)
    {
        var sangria = Math.Max(capa.XPt, 0);

        // El svg:y se cuenta desde el pÃ¡rrafo ancla, y esta capa se pinta DESPUÃ‰S de la
        // tabla, o sea por debajo del alto de ese pÃ¡rrafo: se descuenta. Sin esto las
        // firmas de la liquidaciones caÃ­an una lÃ­nea y media mÃ¡s abajo que en LibreOffice.
        var desplazamiento = capa.YPt - capa.AltoParrafoAnclaPt;

        if (desplazamiento > 0.5) col.Item().Height((float)desplazamiento);

        foreach (var tablaCapa in capa.Tablas)
        {
            var tCapa = tablaCapa;

            col.Item()
               .PaddingLeft((float)sangria)
               .Element(e => AgregarTabla(e, tCapa, Math.Max(capa.AnchoPt, 1), compensarMargen: false));
        }

        foreach (var p in capa.Parrafos)
        {
            var texto = string.Concat(p.Runs.Select(r => r.Texto));
            if (string.IsNullOrWhiteSpace(texto)) continue;

            var estilo = EstiloDe(p.Runs, p.AltoLineaPt);

            col.Item()
               .PaddingLeft((float)sangria)
               .Width((float)Math.Max(capa.AnchoPt, 1))
               .Text(t => t.Span(texto.Trim()).Style(estilo));
        }
    }

    // Dibuja las imÃ¡genes-renglÃ³n donde cae su pÃ¡rrafo ancla, como hace LibreOffice: la
    // coordenada y del .fodt es un desplazamiento pequeÃ±o desde el ancla, no una posiciÃ³n
    // de pÃ¡gina. El alto del propio pÃ¡rrafo se reparte entre el desplazamiento, la lÃ­nea y
    // lo que sobre, para no mover el resto del flujo.
    static void AgregarLineasFlujo(ColumnDescriptor col, List<CapaModelo> capas, double altoBloque)
    {
        // El párrafo ancla conserva exactamente su alto declarado (altoBloque): si la imagen
        // flotante lleva svg:y mayor que el fo:line-height del párrafo (p.ej. P7 0.035cm con
        // svg:y=0.102cm en Carta Porte), en Writer desborda sin empujar los bloques siguientes.
        col.Item().Layers(capasDibujo =>
        {
            capasDibujo.PrimaryLayer().Height((float)Math.Max(altoBloque, 0.5));

            foreach (var capa in capas)
            {
                try
                {
                    var bytes = Convert.FromBase64String(capa.ImagenBase64);
                    var alto = (float)Math.Max(capa.AltoPt, 0.6);
                    var ancho = (float)Math.Max(capa.AnchoPt, 1);
                    var x = (float)Math.Max(capa.XPt, 0);
                    var y = (float)Math.Max(capa.YPt, 0);

                    capasDibujo.Layer()
                        .Unconstrained()
                        .PaddingLeft(x)
                        .PaddingTop(y)
                        .Width(ancho)
                        .Height(alto)
                        .Image(bytes)
                        .FitUnproportionally();
                }
                catch { /* imagen ilegible: se ignora */ }
            }
        });
    }

    // Marca de agua de TEXTO (WordArt del encabezado) a PNG del tamaÃ±o exacto de la forma.
    // LibreOffice escala los glifos de forma NO uniforme para llenar la caja: el ancho y el
    // alto se estiran por separado (las mayÃºsculas acaban midiendo los 66,6 pt de la caja).
    // QuestPDF no tiene escala de texto no uniforme, asÃ­ que se rasteriza con Skia.
    static byte[] MarcaAguaTextoPng(MarcaAguaTextoModelo marca, float anchoPt, float altoPt)
    {
        using var tipo = SkiaSharp.SKTypeface.FromFamilyName(marca.Fuente ?? "Arial") ?? SkiaSharp.SKTypeface.Default;
        using var fuente = new SkiaSharp.SKFont(tipo, 100);
        using var pinta = new SkiaSharp.SKPaint { IsAntialias = true, Color = SkColorDe(marca.Color) };

        var ancho100 = fuente.MeasureText(marca.Texto);
        var metricas = fuente.Metrics;
        var alto100 = Math.Abs(metricas.CapHeight);   // el texto del WordArt va en mayÃºsculas

        if (ancho100 <= 0 || alto100 <= 0 || anchoPt <= 0 || altoPt <= 0)
            return Array.Empty<byte>();

        // 4 px por pt: suficiente para 66 pt de alto sin que se note el rasterizado
        const float Escala = 4f;

        var anchoPx = Math.Max((int)Math.Ceiling(anchoPt * Escala), 1);
        var altoPx = Math.Max((int)Math.Ceiling(altoPt * Escala), 1);

        using var mapa = new SkiaSharp.SKBitmap(anchoPx, altoPx, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul);
        mapa.Erase(SkiaSharp.SKColors.Transparent);

        using (var lienzo = new SkiaSharp.SKCanvas(mapa))
        {
            lienzo.Scale(anchoPx / ancho100, altoPx / alto100);

            // la lÃ­nea base va a la altura de las mayÃºsculas para que la tinta empiece en 0
            lienzo.DrawText(marca.Texto, 0, (float)alto100, SkiaSharp.SKTextAlign.Left, fuente, pinta);
        }

        using var imagen = SkiaSharp.SKImage.FromBitmap(mapa);
        using var datos = imagen.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);

        return datos.ToArray();
    }

    // "#a5a5a5" -> SKColor (para el rasterizado de la marca de agua de texto)
    static SkiaSharp.SKColor SkColorDe(string hex)
    {
        var h = (hex ?? "").Trim().TrimStart('#');

        if (h.Length != 6) return new SkiaSharp.SKColor(165, 165, 165);

        return new SkiaSharp.SKColor(
            Convert.ToByte(h.Substring(0, 2), 16),
            Convert.ToByte(h.Substring(2, 2), 16),
            Convert.ToByte(h.Substring(4, 2), 16));
    }

    // Convierte la imagen de la marca de agua al aspecto que le da LibreOffice con
    // draw:color-mode="watermark": escala de grises, aclarada y casi transparente.
    static byte[] AtenuarMarcaAgua(byte[] imagen)
    {
        try
        {
            using var original = SkiaSharp.SKBitmap.Decode(imagen);
            if (original == null) return imagen;

            var info = new SkiaSharp.SKImageInfo(original.Width, original.Height,
                SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul);
            using var salida = new SkiaSharp.SKBitmap(info);

            using (var lienzo = new SkiaSharp.SKCanvas(salida))
            using (var pintura = new SkiaSharp.SKPaint())
            {
                // Medido contra el PDF de LibreOffice: no baja la opacidad, ACLARA el color
                // hacia el blanco (azul 166/187/215 -> 215/233/254). Se hace por canal:
                //   canal' = 0.14 * canal + blanco del canal
                // (en SkiaSharp el desplazamiento de la matriz va en 0..1, no en 0..255)
                var matriz = new float[]
                {
                    0.14f, 0,     0,     0, 219f / 255f,
                    0,     0.14f, 0,     0, 220f / 255f,
                    0,     0,     0.14f, 0, 232f / 255f,
                    0,     0,     0,     1, 0
                };
                pintura.ColorFilter = SkiaSharp.SKColorFilter.CreateColorMatrix(matriz);
                lienzo.DrawBitmap(original, 0, 0, pintura);
            }

            using var imagenSalida = SkiaSharp.SKImage.FromBitmap(salida);
            using var datos = imagenSalida.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            return datos.ToArray();
        }
        catch
        {
            return imagen;   // si algo falla, se dibuja la original
        }
    }

    static void DibujarCapa(IContainer cont, CapaModelo capa)
    {
        cont.Column(col =>
        {
            if (!string.IsNullOrEmpty(capa.ImagenBase64))
            {
                try
                {
                    var bytes = Convert.FromBase64String(capa.ImagenBase64);
                    if (capa.YPt > 0.5) col.Item().Height((float)capa.YPt);
                    // El frame del .fodt ya fija ancho y alto (p.ej. 100x80). QuestPDF,
                    // por omisiÃ³n, escala la imagen conservando su proporciÃ³n y deja de
                    // respetar esa caja. FitUnproportionally estira la imagen al marco,
                    // que es lo que hace LibreOffice.
                    col.Item()
                       .PaddingLeft((float)capa.XPt)
                       .Width((float)capa.AnchoPt)
                       .Height((float)capa.AltoPt)
                       .Image(bytes)
                       .FitUnproportionally();
                    return;
                }
                catch { /* imagen ilegible: se sigue con el texto */ }
            }

            // custom-shape que sÃ³lo es una barra de color (la de antes de "ORDEN DE COMPRA")
            // o marco vacío con relleno/borde: no trae texto ni imagen, así que se dibuja como caja.
            if ((!string.IsNullOrEmpty(capa.ColorRelleno) || capa.BordeCaja) &&
                capa.AnchoPt > 0 && capa.AltoPt > 0 &&
                capa.Tablas.Count == 0 &&
                !capa.Parrafos.Any(p => p.Runs.Any(r => !string.IsNullOrWhiteSpace(r.Texto))))
            {
                if (capa.YPt > 0.5) col.Item().Height((float)capa.YPt);

                IContainer caja = col.Item()
                   .PaddingLeft((float)Math.Max(capa.XPt, 0))
                   .Width((float)capa.AnchoPt)
                   .Height((float)capa.AltoPt);

                if (!string.IsNullOrEmpty(capa.ColorRelleno))
                    caja = caja.Background(ColorDe(capa.ColorRelleno));

                if (capa.BordeCaja)
                    caja.Border((float)Math.Max(capa.GrosorBordePt, 0.5))
                        .BorderColor(ColorDe(capa.ColorBorde ?? "#000000"));
                return;
            }

            var primerTexto = true;

            foreach (var p in capa.Parrafos)
            {
                // los bookmarks marcan la POSICIÃ“N del dato, no son texto: en el
                // documento final no aparecen, asÃ­ que aquÃ­ tampoco se dibujan.
                var texto = string.Concat(p.Runs.Select(r => r.Texto));
                if (string.IsNullOrWhiteSpace(texto)) continue;

                var estilo = EstiloDe(p.Runs, p.AltoLineaPt);
                var ancho = Math.Max(capa.AnchoPt, 1);
                var tamano = p.Runs.Where(x => !string.IsNullOrWhiteSpace(x.Texto)).Select(x => x.TamanoPt).FirstOrDefault(x => x > 0);
                if (tamano <= 0) tamano = 9;

                // La Y del .fodt es el borde superior de la caja, pero el texto se dibuja
                // sobre su lÃ­nea base: se sube el ascendente (~0.9 del tamaÃ±o) para calzar
                // con lo que hace LibreOffice. SÃ³lo aplica a la primera lÃ­nea.
                if (primerTexto)
                {
                    var ajuste = tamano * 0.9;
                    if (capa.YPt - ajuste > 0.5)
                        col.Item().Height((float)(capa.YPt - ajuste));
                    primerTexto = false;
                }

                col.Item()
                   .PaddingLeft((float)capa.XPt)
                   .Width((float)ancho)
                   .Height((float)(tamano * 1.25))
                   .Text(t => t.Span(texto.Trim()).Style(estilo));
            }

            // Tablas propias de la caja: la liquidaciones pone el tÃ­tulo y el bloque de
            // firmas en tablas dentro del cuadro de texto. Antes se aplanaban como
            // pÃ¡rrafos sueltos y el tÃ­tulo y las firmas salÃ­an corridos a la izquierda.
            foreach (var tablaCapa in capa.Tablas)
            {
                var tCapa = tablaCapa;

                // sin texto antes, la tabla tiene que dejar su hueco desde el borde superior
                if (primerTexto)
                {
                    if (capa.YPt > 0.5) col.Item().Height((float)capa.YPt);
                    primerTexto = false;
                }

                col.Item()
                   .PaddingLeft((float)capa.XPt)
                   .Element(e => AgregarTabla(e, tCapa, Math.Max(capa.AnchoPt, 1), compensarMargen: false));
            }
        });
    }

    // Imagen anclada "as-char" dentro de un pÃ¡rrafo del flujo (el QR del final de la
    // orden de compra). Ocupa su propia lÃ­nea, asÃ­ que se pinta con el ancho que declara
    // el svg:width del frame y el alto sale solo, proporcional.
    static void DibujarImagenFlujo(ColumnDescriptor col, ParrafoModelo p, double anchoDisponible)
    {
        try
        {
            var bytes = Convert.FromBase64String(p.ImagenBase64);

            var ancho = p.ImagenAnchoPt > 0 ? p.ImagenAnchoPt : p.ImagenAltoPt;
            var alto = p.ImagenAltoPt;

            if (ancho <= 0) { col.Item().ScaleToFit().Image(bytes); return; }

            // si la imagen no cabe en la columna se encoge: QuestPDF no recorta y falla
            if (anchoDisponible > 0 && ancho > anchoDisponible) ancho = anchoDisponible;

            // El flujo va desplazado a la izquierda por la tabla de margen negativo, pero
            // este pÃ¡rrafo tiene su propio fo:margin-left (el del QR es 0): la diferencia
            // es lo que hay que devolverle para que caiga en el margen izquierdo, como en
            // LibreOffice. Sin esto el QR salÃ­a 44 pt a la izquierda de donde va.
            var sangria = Math.Max(p.MargenIzqPt - _desplazamientoGlobal, 0);

            var contenedor = col.Item()
               .PaddingLeft((float)sangria)
               .Width((float)ancho);

            // El bookmark de imagen puede venir en un frame "as-char" con alto propio.
            // Antes se usaba sÃ³lo Width y QuestPDF conservaba la proporciÃ³n: un logo
            // cuadrado insertado como 100x80 salÃ­a cuadrado. Se fija tambiÃ©n el alto.
            if (alto > 0)
                contenedor = contenedor.Height((float)alto);

            var imagen = contenedor.Image(bytes);

            if (alto > 0)
                imagen.FitUnproportionally();
        }
        catch { /* imagen ilegible */ }
    }

    // sangriaPt: desplazamiento horizontal extra. El flujo va corrido a la izquierda por
    // la tabla de margen negativo mÃ¡s ancha, y cada pÃ¡rrafo tiene su propio fo:margin-left;
    // la diferencia es lo que hay que devolverle para caer donde lo pone LibreOffice.
    // Renglones de firma (draw:line de los contratos): van en pÃ¡rrafos que a veces no
    // llevan texto, asÃ­ que se dibujan aparte del bloque de texto.
    static void DibujarRenglones(ColumnDescriptor col, ParrafoModelo p, double sangriaPt, double anchoDisponible = 0)
    {
        var validas = p.Lineas.Where(l => l.AnchoPt > 0.5).ToList();
        if (validas.Count == 0) return;

        // Se dibujan en capas superpuestas (PrimaryLayer alto 0) para que varias
        // <draw:line> del mismo párrafo (p.ej. firmas lado a lado PATRON / TRABAJADOR)
        // compartan la misma referencia vertical YPt sin empujarse hacia abajo entre sí.
        col.Item().Layers(capasLin =>
        {
            capasLin.PrimaryLayer().Height(0);

            foreach (var linea in validas)
            {
                var izquierda = Math.Max(linea.XPt - _desplazamientoGlobal + Math.Max(sangriaPt, 0), 0);
                var ancho = linea.AnchoPt;

                if (anchoDisponible > 0)
                {
                    var restante = anchoDisponible - izquierda;
                    if (restante < 1) continue;
                    ancho = Math.Min(ancho, restante);
                }

                var x = (float)izquierda;
                var y = (float)Math.Max(linea.YPt, 0);
                var w = (float)ancho;
                var h = (float)Math.Max(linea.GrosorPt, 0.6);
                var color = ColorDe(linea.Color);

                capasLin.Layer()
                    .Unconstrained()
                    .PaddingLeft(x)
                    .PaddingTop(y)
                    .Width(w)
                    .Height(h)
                    .Background(color);
            }
        });
    }
    // Â¿El pÃ¡rrafo ocupa 3 renglones o menos? Se mide con Skia (mismo motor que los
    // tabuladores) contra el ancho disponible del flujo.
    static bool EsParrafoCorto(ParrafoModelo p, double anchoPt)
    {
        if (anchoPt <= 10) return false;

        var texto = string.Concat(p.Runs.Select(x => x.Texto));
        if (string.IsNullOrWhiteSpace(texto)) return false;

        var r = p.Runs.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Texto));
        var tam = r != null && r.TamanoPt > 0 ? r.TamanoPt : (p.TamanoBasePt > 0 ? p.TamanoBasePt : 10);
        var fuente = NormalizarFuente(r?.Fuente);

        double ancho;
        try { ancho = PaginarPropio.Medir(texto, fuente, tam, false); }
        catch { return false; }

        var lineas = Math.Ceiling(ancho / anchoPt);
        return lineas <= 3;
    }

    static void AgregarParrafo(ColumnDescriptor col, ParrafoModelo p, double sangriaPt = 0, double anchoPt = 0,
                               double margenDerExtraPt = 0)
    {
        if (p.Runs.Count == 0)
        {
            // Un párrafo vacío del encabezado sigue teniendo caja de línea en Writer.
            // Reservarla aquí mantiene la separación entre el nombre y los datos de contacto.
            var altoVacio = (p.AltoLineaPt > 0 ? p.AltoLineaPt : 12) + p.MargenSupPt + p.MargenInfPt;
            col.Item().Height((float)Math.Max(altoVacio, 0.1));
            return;
        }

        // Si el flujo expandió su margen derecho para alojar una tabla ancha con margen negativo,
        // los párrafos fuera de tabla deben restaurar el margen derecho real de la página.
        var paddingDer = margenDerExtraPt > 0.5
            ? Math.Max(margenDerExtraPt + p.MargenDerPt, 0)
            : Math.Max(p.MargenDerPt, 0);

        // Inciso de lista ("A) PRIMERA: â€¦"): el marcador va en una columna fija y el
        // texto ocupa el resto. AsÃ­ el hueco despuÃ©s del inciso y la alineaciÃ³n de los
        // renglones siguientes son los del estilo de lista (sangrÃ­a francesa), como en
        // LibreOffice, que ahÃ­ usa un tabulador al 0.635 cm.
        if (p.SangriaColgantePt > 0.5 && p.Runs.Count > 1)
        {
            var marca = p.Runs[0];
            var cuerpo = p.Runs.Skip(1).ToList();

            var itemLista = col.Item();

            // Compensar el desplazamiento global del flujo (tablas con fo:margin-left negativo)
            // sin duplicar p.MargenIzqPt, que ya está incluido en p.MargenIncisoPt.
            var desplazamientoFlujo = Math.Max(sangriaPt - p.MargenIzqPt, 0);
            var paddingIzqLista = Math.Max(p.MargenIncisoPt, 0) + desplazamientoFlujo;

            var anchoCuerpoLista = anchoPt > 10
                ? Math.Max(anchoPt - paddingIzqLista - p.SangriaColgantePt - paddingDer, 10)
                : anchoPt;
            var encogerLista = DebeEncogerEspacios(p, anchoCuerpoLista);

            void PintarLista(IContainer cont)
            {
                cont.Row(fila =>
                {
                    fila.ConstantItem((float)p.SangriaColgantePt)
                        .Text(t => t.Span(marca.Texto.Trim()).Style(EstiloDe(new List<RunModelo> { marca }, p.AltoLineaPt)));

                    fila.RelativeItem().Text(t =>
                    {
                        AlinearTexto(t, p.Alineacion);

                        foreach (var r in cuerpo)
                        {
                            var estiloCuerpo = EstiloDe(new List<RunModelo> { r }, p.AltoLineaPt);
                            if (encogerLista) estiloCuerpo = estiloCuerpo.WordSpacing(FactorEncogimientoJustificado);

                            if (r.CampoDinamico == "page-number")
                                t.CurrentPageNumber().Style(estiloCuerpo);
                            else if (r.CampoDinamico == "page-count")
                                t.TotalPages().Style(estiloCuerpo);
                            else if (!string.IsNullOrWhiteSpace(r.Hipervinculo))
                                t.Hyperlink(r.Texto, r.Hipervinculo).Style(estiloCuerpo);
                            else
                                t.Span(r.Texto).Style(estiloCuerpo);
                        }
                    });
                });
            }

            if (p.Viudas >= 2 || p.Huerfanas >= 2)
            {
                var altoLineaReal = AltoLineaRenderizada(p);
                var contLista = itemLista.PaddingLeft((float)paddingIzqLista);
                if (paddingDer > 0.5) contLista = contLista.PaddingRight((float)paddingDer);
                contLista.Decoration(dec =>
                {
                    dec.Content().ExtendHorizontal().Element(PintarLista);
                    dec.After().Dynamic(new ControlDeViudas(PintarLista, altoLineaReal, p.Huerfanas, p.Viudas, _altoUtilPagina));
                });
            }
            else
            {
                var anchoCuerpo = anchoPt > 10
                    ? Math.Max(anchoPt - paddingIzqLista - p.SangriaColgantePt - paddingDer, 10)
                    : anchoPt;
                if (EsParrafoCorto(p, anchoCuerpo)) itemLista = itemLista.ShowEntire();

                var contLista = itemLista.PaddingLeft((float)paddingIzqLista);
                if (paddingDer > 0.5) contLista = contLista.PaddingRight((float)paddingDer);
                contLista.Element(PintarLista);
            }

            return;
        }

        // NOTA: partir el pÃ¡rrafo en el <text:soft-page-break/> se probÃ³ dos veces
        // (con y sin "continuaciÃ³n") y en ambas el contrato del tractocamiÃ³n ganaba un
        // renglÃ³n en la pÃ¡gina 3 (46 contra 44 de LibreOffice). El desfase de fondo son
        // los cortes de renglÃ³n, no el de pÃ¡gina: se deja sin soporte.
        var item = col.Item();

        var usarControlViudas = (p.Viudas >= 2 || p.Huerfanas >= 2)
            && !p.BordeParrafo && !p.BordeInferiorParrafo && string.IsNullOrEmpty(p.ColorFondoParrafo);

        if (!usarControlViudas)
        {
            // Viudas/huÃ©rfanas (LibreOffice exige dos renglones a cada lado del corte):
            //  - EnsureSpace: no empezar el pÃ¡rrafo si no caben dos renglones (huÃ©rfana);
            //    si no alcanza, el pÃ¡rrafo completo pasa a la pÃ¡gina siguiente.
            //  - ShowEntire: un pÃ¡rrafo de â‰¤3 renglones no se parte (viuda).
            var altoLineaParrafo = p.AltoLineaPt > 0 ? p.AltoLineaPt : 12.5;
            item = item.EnsureSpace((float)(2 * altoLineaParrafo));
            if (EsParrafoCorto(p, anchoPt)) item = item.ShowEntire();
        }

        // En una primera línea centrada, ODF aplica text-indent al ancho efectivo de
        // la línea; eso desplaza su centro la mitad de la sangría. Usar sólo el margen
        // dejaba el título del acuse (P2) ~18 pt a la izquierda de LibreOffice.
        var sangriaCentrada = p.Alineacion == "center" ? Math.Max(p.SangriaPt, 0) : 0;
        item = item.ExtendHorizontal()
                   .PaddingLeft((float)(Math.Max(sangriaPt, 0) + sangriaCentrada));

        if (paddingDer > 0.5)
            item = item.PaddingRight((float)paddingDer);

        // caja del pÃ¡rrafo: el encabezado del contrato CCI va con borde y relleno gris.
        // QuestPDF no tiene borde "double": se dibuja sÃ³lido del mismo grosor y color.
        if (p.BordeParrafo || p.BordeInferiorParrafo || !string.IsNullOrEmpty(p.ColorFondoParrafo))
        {
            if (p.BordeParrafo)
                item = item
                    .Border((float)Math.Max(p.GrosorBordeParrafoPt, 0.5))
                    .BorderColor(ColorDe(p.ColorBordeParrafo));
            else if (p.BordeInferiorParrafo)
                item = item
                    .BorderBottom((float)Math.Max(p.GrosorBordeParrafoPt, 0.5))
                    .BorderColor(ColorDe(p.ColorBordeParrafo ?? "#000000"));

            if (!string.IsNullOrEmpty(p.ColorFondoParrafo))
                item = item.Background(ColorDe(p.ColorFondoParrafo));

            item = item
                .PaddingTop((float)p.PaddingSupParrafoPt)
                .PaddingBottom((float)p.PaddingInfParrafoPt)
                .PaddingLeft((float)p.PaddingIzqParrafoPt)
                .PaddingRight((float)p.PaddingDerParrafoPt);
        }

        var anchoTextoParrafo = anchoPt > 10
            ? Math.Max(anchoPt - Math.Max(sangriaPt, 0) - sangriaCentrada - paddingDer, 0)
            : 0;
        var runsPintar = PartirTokensLargos(p.Runs, anchoTextoParrafo);
        var encogerEspacios = DebeEncogerEspacios(p, anchoTextoParrafo);
        var tieneSuperConBase = runsPintar.Any(r => string.IsNullOrEmpty(r.Posicion) && !string.IsNullOrWhiteSpace(r.Texto))
                             && runsPintar.Any(r => r.Posicion == "super" && !string.IsNullOrWhiteSpace(r.Texto));
        var tamPrincipalFlujo = runsPintar
            .Where(x => !string.IsNullOrWhiteSpace(x.Texto) && x.TamanoPt > 0)
            .Select(x => x.TamanoPt)
            .DefaultIfEmpty(0)
            .Max();

        void PintarTexto(IContainer cont)
        {
            cont.Text(t =>
            {
                AlinearTexto(t, p.Alineacion);
                if (p.SangriaPt > 0.5 && p.Alineacion != "center")
                    t.ParagraphFirstLineIndentation((float)p.SangriaPt);

                foreach (var r in runsPintar)
                {
                    var estiloRun = r;
                    if (tamPrincipalFlujo > 0 && string.IsNullOrWhiteSpace(r.Texto) && r.TamanoPt != tamPrincipalFlujo)
                        estiloRun = new RunModelo
                        {
                            Texto = r.Texto,
                            Slot = r.Slot,
                            Fuente = r.Fuente,
                            TamanoPt = tamPrincipalFlujo,
                            Negrita = r.Negrita,
                            Cursiva = r.Cursiva,
                            Subrayado = r.Subrayado,
                            Color = r.Color,
                            EscalaPosicion = r.EscalaPosicion,
                            Posicion = r.Posicion,
                            Hipervinculo = r.Hipervinculo,
                            CampoDinamico = r.CampoDinamico,
                        };

                    var estilo = EstiloDe(new List<RunModelo> { estiloRun }, p.AltoLineaPt, aplicarSuper: tieneSuperConBase);
                    if (encogerEspacios) estilo = estilo.WordSpacing(FactorEncogimientoJustificado);

                    // text:page-number / text:page-count: números de página dinámicos
                    if (r.CampoDinamico == "page-number")
                        t.CurrentPageNumber().Style(estilo);
                    else if (r.CampoDinamico == "page-count")
                        t.TotalPages().Style(estilo);
                    // text:a del .fodt (el correo de la orden de compra): si trae destino, se
                    // emite como hipervÃ­nculo para que salga subrayado y clicable.
                    else if (!string.IsNullOrWhiteSpace(r.Hipervinculo))
                        t.Hyperlink(r.Texto, r.Hipervinculo).Style(estilo);
                    else
                        t.Span(r.Texto).Style(estilo);
                }
            });
        }

        if (usarControlViudas)
        {
            var altoLineaReal = AltoLineaRenderizada(p);
            item.Decoration(dec =>
            {
                dec.Content().ExtendHorizontal().Element(PintarTexto);
                dec.After().Dynamic(new ControlDeViudas(PintarTexto, altoLineaReal, p.Huerfanas, p.Viudas, _altoUtilPagina));
            });
        }
        else if (tieneSuperConBase && p.AltoLineaPt > 0)
        {
            // Skia SkParagraph calcula el ascenso de .Superscript() con la métrica sin
            // escalar del run más el desplazamiento superior, inflando la caja de línea
            // hacia arriba (~3.7 pt en P24 de Carta Porte) aunque los glifos reducidos al
            // 58% caben dentro del fo:line-height declarado. En Writer el fo:line-height
            // fijo no crece por superíndices: recortamos ese ascenso fantasma si el
            // párrafo cabe en una sola línea.
            var tamBase = runsPintar
                .Where(r => string.IsNullOrEmpty(r.Posicion) && !string.IsNullOrWhiteSpace(r.Texto) && r.TamanoPt > 0)
                .Select(r => r.TamanoPt)
                .DefaultIfEmpty(10).Max();
            var tamSuperOrig = runsPintar
                .Where(r => r.Posicion == "super" && !string.IsNullOrWhiteSpace(r.Texto) && r.TamanoPt > 0)
                .Select(r => r.TamanoPt / EscalaSuperQuestPdf)
                .DefaultIfEmpty(0).Max();
            var excesoSuper = Math.Max(0.90 * tamSuperOrig + 0.35 * tamSuperOrig - 0.90 * tamBase, 0);
            var anchoEstimado = runsPintar.Sum(r =>
                string.IsNullOrEmpty(r.Texto) ? 0
                    : PaginarPropio.Medir(r.Texto, NormalizarFuente(r.Fuente), r.TamanoPt > 0 ? r.TamanoPt : tamBase, r.Negrita));
            var cabeEnUnaLinea = anchoTextoParrafo <= 0 || anchoEstimado <= anchoTextoParrafo + 4;

            if (excesoSuper > 0.5 && cabeEnUnaLinea)
            {
                var altoLineaFijo = Math.Max(p.AltoLineaPt, tamBase * 1.05);
                item.Layers(capas =>
                {
                    capas.PrimaryLayer().Height((float)altoLineaFijo);
                    capas.Layer().Unconstrained().OffsetY((float)(-excesoSuper)).Element(PintarTexto);
                });
            }
            else
            {
                item.Element(PintarTexto);
            }
        }
        else
        {
            item.Element(PintarTexto);
        }
    }

    // LibreOffice Writer (SwTextGuess::Guess + UAX #14 LB21) trata una secuencia de
    // guiones (p.ej. "extraordinario.------...") como un token indivisible: si excede el
    // ancho útil de la línea, primero salta en el espacio previo (justificando la línea
    // anterior) y luego corta el token al margen derecho. Skia SkParagraph, en cambio,
    // empieza tokens mayores al ancho de línea en la misma línea y los parte ahí mismo.
    // Insertar un U+200B justo antes del ancho útil hace que cada segmento quepa en una
    // línea completa y Skia reproduzca exactamente el salto y justificado de LibreOffice.
    static string PartirMarcaPorAncho(string texto, double anchoMaxPt, string fuente, double tamanoPt, bool negrita)
    {
        if (string.IsNullOrEmpty(texto)) return texto;

        var lineas = new List<string>();
        var actual = "";

        foreach (var ch in texto)
        {
            var candidato = actual + ch;
            if (actual.Length > 0 && PaginarPropio.Medir(candidato, fuente, tamanoPt, negrita) > anchoMaxPt)
            {
                lineas.Add(actual);
                actual = ch.ToString();
            }
            else
            {
                actual = candidato;
            }
        }

        if (actual.Length > 0) lineas.Add(actual);
        return string.Join("\n", lineas);
    }

    static List<RunModelo> PartirTokensLargos(List<RunModelo> runs, double anchoLinea)
    {
        if (anchoLinea <= 20 || !runs.Any(r => r.Texto != null && r.Texto.Contains("----", StringComparison.Ordinal)))
            return runs;

        var limite = anchoLinea - 2.0;
        var salida = new List<RunModelo>(runs.Count);

        foreach (var r in runs)
        {
            if (string.IsNullOrEmpty(r.Texto) || !r.Texto.Contains("----", StringComparison.Ordinal))
            {
                salida.Add(r);
                continue;
            }

            var fuente = NormalizarFuente(r.Fuente);
            var tam = r.TamanoPt > 0 ? r.TamanoPt : 9;
            var sb = new System.Text.StringBuilder(r.Texto.Length + 8);
            var i = 0;

            while (i < r.Texto.Length)
            {
                if (char.IsWhiteSpace(r.Texto[i]))
                {
                    sb.Append(r.Texto[i++]);
                    continue;
                }

                var inicio = i;
                while (i < r.Texto.Length && !char.IsWhiteSpace(r.Texto[i])) i++;
                var token = r.Texto.Substring(inicio, i - inicio);

                if (!token.Contains("----", StringComparison.Ordinal))
                {
                    sb.Append(token);
                    continue;
                }

                double anchoToken;
                try { anchoToken = PaginarPropio.Medir(token, fuente, tam, r.Negrita); }
                catch { sb.Append(token); continue; }

                if (anchoToken <= limite)
                {
                    sb.Append(token);
                    continue;
                }

                var resto = token;
                while (resto.Length > 1)
                {
                    double wResto;
                    try { wResto = PaginarPropio.Medir(resto, fuente, tam, r.Negrita); }
                    catch { break; }

                    if (wResto <= limite) break;

                    // Búsqueda binaria del prefijo más largo que cabe en 'limite'
                    int lo = 1, hi = resto.Length - 1, corte = 1;
                    while (lo <= hi)
                    {
                        var mid = (lo + hi) / 2;
                        var wMid = PaginarPropio.Medir(resto[..mid], fuente, tam, r.Negrita);
                        if (wMid <= limite) { corte = mid; lo = mid + 1; }
                        else hi = mid - 1;
                    }

                    sb.Append(resto, 0, corte);
                    sb.Append('\u200B');
                    resto = resto[corte..];
                }

                sb.Append(resto);
            }

            salida.Add(new RunModelo
            {
                Texto = sb.ToString(),
                Fuente = r.Fuente,
                TamanoPt = r.TamanoPt,
                Negrita = r.Negrita,
                Cursiva = r.Cursiva,
                Subrayado = r.Subrayado,
                Color = r.Color,
                Slot = r.Slot,
                EscalaPosicion = r.EscalaPosicion,
                Posicion = r.Posicion,
                Hipervinculo = r.Hipervinculo,
                CampoDinamico = r.CampoDinamico,
            });
        }

        return salida;
    }

    // LibreOffice Writer (SwTextPortion::Format, DocumentSettingId::JUSTIFY_LINES_WITH_SHRINKING)
    // permite encoger el ancho de los espacios hasta 75 % en líneas justificadas de documentos
    // compatibles con Word (-25 % de un espacio Arial ≈ 0.278 em => -0.07 em).
    // Sólo aplica a párrafos justificados multilínea (sin corridas de espacios usadas como columnas).
    const float FactorEncogimientoJustificado = -0.07f;

    static bool DebeEncogerEspacios(ParrafoModelo p, double anchoTexto)
    {
        if (!_justificarConEncogimiento || p.Alineacion != "justify" || anchoTexto <= 20)
            return false;

        var texto = string.Concat(p.Runs.Select(r => r.Texto));
        if (string.IsNullOrWhiteSpace(texto) || texto.Contains("   ", StringComparison.Ordinal))
            return false;

        var ancho = p.SangriaPt > 0.5 ? p.SangriaPt : 0.0;
        foreach (var r in p.Runs)
        {
            if (string.IsNullOrEmpty(r.Texto)) continue;
            var tam = r.TamanoPt > 0 ? r.TamanoPt : (p.TamanoBasePt > 0 ? p.TamanoBasePt : 10);
            try { ancho += PaginarPropio.Medir(r.Texto, NormalizarFuente(r.Fuente), tam, r.Negrita); }
            catch { return false; }
            if (ancho > anchoTexto) return true;
        }
        return false;
    }

    // La alineaciÃ³n horizontal del texto se aplica en el descriptor del bloque
    // (t.AlignCenter()), no en el contenedor: en el contenedor no tenÃ­a efecto.
    static void AlinearTexto(TextDescriptor t, string alineacion)
    {
        switch (alineacion)
        {
            case "center": t.AlignCenter(); break;
            case "end":
            case "right": t.AlignRight(); break;
            // QuestPDF no tiene "justify" sobre IContainer, pero sÃ­ sobre el TEXTO:
            // las cartas de crÃ©dito (AutFirmas/Formato_Firmas) lo declaran en su P6.
            case "justify": t.Justify(); break;
            default: t.AlignLeft(); break;
        }
    }

    // MÃ¡rgenes verticales dentro de una CELDA de tabla. AquÃ­ se siguen ignorando (medido:
    // sumarlos empeora el ajuste de las facturas, los mÃ¡rgenes vienen del modelo de Word
    // y LibreOffice no los aplica igual). En el flujo sÃ­ se aplican, con la regla del
    // estÃ¡ndar: ver HuecoAntes().
    const bool AplicarMargenesVerticales = false;

    // Hueco vertical entre dos bloques del flujo, segÃºn ODF 1.3 parte 3 Â§20.255
    // (style:contextual-spacing): el fo:margin-bottom de un pÃ¡rrafo y el fo:margin-top
    // del siguiente SE SUMAN, salvo que se cumplan las tres condiciones â€”ambos con
    // contextual-spacing="true", en la misma Ã¡rea de contenido (misma secciÃ³n) y con el
    // mismo estilo "comÃºn" de pÃ¡rrafoâ€”, en cuyo caso el espacio entre ellos es cero.
    static double HuecoAntes(BloqueModelo anterior, BloqueModelo actual)
    {
        if (actual == null) return 0;

        var sup = actual.Parrafo?.MargenSupPt ?? 0;

        // El primer bloque no tiene margen inferior previo que colapsar, pero su propio
        // margen superior sÃ­ separa el pÃ¡rrafo del borde del Ã¡rea de texto.
        if (anterior == null) return sup;

        var inf = anterior.Parrafo?.MargenInfPt ?? 0;

        // LibreOffice (sw/source/core/layout/flowfrm.cxx, tdf#125893 y tdf#134463):
        // cada párrafo ignora su propio margen hacia el vecino del mismo estilo común
        // cuando ÉL declara style:contextual-spacing="true", aunque el vecino no lo haga.
        var mismoEstilo = anterior.Parrafo != null && actual.Parrafo != null &&
                          anterior.Seccion == actual.Seccion &&
                          anterior.Parrafo.EstiloComun == actual.Parrafo.EstiloComun;

        if (mismoEstilo && anterior.Parrafo.Contextual) inf = 0;
        if (mismoEstilo && actual.Parrafo.Contextual) sup = 0;

        return sup + inf;
    }

    // Altura a la que se ancla el texto del pie, medida desde el borde inferior de la hoja.
    // El pie del .fodt trae su propia Ã¡rea (style:footer-style): LibreOffice deja el texto
    // apoyado en el margen inferior declarado, no al final de esa Ã¡rea. Si el Ã¡rea es mÃ¡s
    // alta que el margen (la bitÃ¡cora: 1.251cm + 1.251cm), usar MargenInfPt dejaba la
    // fecha flotando 30 pt por encima de donde va.
    static double PieAbajo(PaginaModelo pagina)
    {
        var conArea = pagina.MargenInfPt - 4;
        var enMargen = pagina.MargenInfBasePt;

        return pagina.MargenInfBasePt > 0 ? Math.Min(conArea, enMargen) : conArea;
    }

    static void AgregarFlujo(ColumnDescriptor col, List<BloqueModelo> bloques, double anchoDisponible = 0)
    {
        BloqueModelo anterior = null;
        var pendiente = 0.0;



        foreach (var bloque in bloques)
        {
            // se acumula y se pinta de una vez: si el bloque no dibuja nada (pÃ¡rrafo
            // vacÃ­o) su margen pasa al siguiente, como harÃ­a LibreOffice
            // salto de pagina explicito del .fodt (fo:break-before="page")
            if (bloque.Parrafo != null && bloque.Parrafo.SaltoPaginaAntes)
            {
                col.Item().PageBreak();
                pendiente = 0;
            }

            pendiente += HuecoAntes(anterior, bloque);

            // corte calculado por nosotros: se rompe pÃ¡gina aquÃ­ y NO se arrastra el
            // espacio anterior (LibreOffice lo colapsa al empezar pÃ¡gina).
            // marcador invisible: anota en quÃ© pÃ¡gina cae el bloque (primera pasada)
            if (bloque.IndiceFlujo >= 0)
                col.Item().Dynamic(new MarcaDePagina { Indice = bloque.IndiceFlujo });

            // Si el bloque abre pÃ¡gina y es un pÃ¡rrafo VACÃO, no se dibuja ni arrastra su
            // espacio (LibreOffice no gasta renglones en blanco al inicio de hoja; antes
            // acumulÃ¡bamos hasta quince en el CCI). OJO: el espacio anterior de un bloque
            // CON contenido SÃ se conserva; quitarlo hacÃ­a que cada pÃ¡gina del contrato de
            // tractocamiÃ³n ganara un renglÃ³n y el documento saliera en 10 hojas en vez de 11.
            if (ColapsarEspacios.Contains(bloque.IndiceFlujo) && bloque.Tipo == "espacio")
            {
                pendiente = 0;
                anterior = bloque;
                continue;
            }

            if (_cortesPropios.TryGetValue(bloque, out var cortaAqui) && cortaAqui)
            {
                // Rompe aquÃ­, pero NO omitas el bloque: antes se hacÃ­a "continue" y el
                // primer bloque de cada pÃ¡gina se perdÃ­a, descolocando el resto del flujo.
                col.Item().PageBreak();
                pendiente = 0;
                anterior = bloque;
            }

            // El marco de la pÃ³liza contable estÃ¡ anclado a un pÃ¡rrafo que NO llega a ser
            // bloque (va vacÃ­o), asÃ­ que el hueco se aplica al primer bloque que sÃ­ se
            // dibuja: si no, el "Registro en el diario:" se le encima.
            if (_capasQueReservan.TryGetValue(bloque, out var reservaMarco))
            {
                if (pendiente > 0.5) { col.Item().Height((float)pendiente); pendiente = 0; }
                col.Item().Height((float)reservaMarco);
            }

            if (_marcosFlotantesPorBloque.TryGetValue(bloque, out var marcosFlotantes))
            {
                if (pendiente > 0.5) { col.Item().Height((float)pendiente); pendiente = 0; }
                DibujarMarcosFlotantes(col, marcosFlotantes);
            }

            if (bloque.Tipo == "texto" && bloque.Parrafo != null)
            {
                var p = bloque.Parrafo;

                var tieneTexto = p.Runs.Any(r => !string.IsNullOrWhiteSpace(r.Texto));
                var tieneImagen = !string.IsNullOrEmpty(p.ImagenBase64) && p.ImagenEnFlujo;

                anterior = bloque;

                // sin texto y sin imagen no hay nada que pintar: sÃ³lo el alto de la lÃ­nea
                if (!tieneTexto && !tieneImagen && p.Lineas.Count == 0) continue;

                if (pendiente > 0.5) { col.Item().Height((float)pendiente); pendiente = 0; }

                if (tieneImagen) DibujarImagenFlujo(col, p, anchoDisponible);

                // SangrÃ­a propia del pÃ¡rrafo respecto al desplazamiento global del flujo.
                // El aviso "*ESTE DOCUMENTO NO ES VALIDO..." va con margen 0 y salÃ­a ~50 pt
                // a la izquierda de donde lo pone LibreOffice.
                if (p.Lineas.Count > 0)
                    DibujarRenglones(col, p, p.MargenIzqPt - _desplazamientoGlobal, anchoDisponible);

                if (tieneTexto)
                {
                    AgregarParrafo(col, p, p.MargenIzqPt - _desplazamientoGlobal, anchoDisponible, _compensacionDerFlujo);
                }

                continue;
            }

            if (pendiente > 0.5) { col.Item().Height((float)pendiente); pendiente = 0; }

            if (_lineasPorBloque.TryGetValue(bloque, out var lineasDelBloque))
            {
                AgregarLineasFlujo(col, lineasDelBloque, bloque.AltoPt);
                anterior = bloque;
                continue;
            }

            AgregarBloque(col, bloque, anchoDisponible);

            // marcos anclados a un pÃ¡rrafo de esta tabla (firmas de la liquidaciones)
            if (_capasPorBloque.TryGetValue(bloque, out var capasDelBloque))
                foreach (var capaFlujo in capasDelBloque)
                    DibujarCapaEnFlujo(col, capaFlujo);

            anterior = bloque;
        }

        // el margen inferior del Ãºltimo bloque no separa de nada: no se pinta
    }

    // el .fodt declara la alineaciÃ³n por pÃ¡rrafo (fo:text-align) y hay que respetarla
    // (QuestPDF no tiene AlignJustify sobre IContainer: "justify" cae a izquierda)
    static IContainer Alinear(IContainer cont, string alineacion) => alineacion switch
    {
        "center" => cont.AlignCenter(),
        "end" => cont.AlignRight(),
        "right" => cont.AlignRight(),
        _ => cont.AlignLeft(),
    };

    // desplazamiento global del flujo (el margen negativo mayor del documento); cada tabla
    // se recoloca con la diferencia entre el suyo y Ã©ste, para que cada una caiga donde toca
    static double _desplazamientoGlobal;
    static double _margenIzqPagina;
    static double _altoUtilPagina;
    static double _margenDerechoFlujo;
    static double _compensacionDerFlujo;
    static bool _justificarConEncogimiento;

    static double AltoLineaRenderizada(ParrafoModelo p)
    {
        var tam = p.Runs
            .Where(x => !string.IsNullOrWhiteSpace(x.Texto) && x.TamanoPt > 0)
            .Select(x => x.TamanoPt)
            .DefaultIfEmpty(0)
            .Max();
        if (tam <= 0) tam = p.TamanoBasePt > 0 ? p.TamanoBasePt : 10;
        if (p.AltoLineaPt > 0)
            return Math.Max(p.AltoLineaPt, tam * 1.15) / AltoNaturalFuente;
        return tam * 1.15;
    }

    // Implementa fo:orphans / fo:widows (ODF 1.3 §20.214 / §20.230) sobre el TextBlock de
    // QuestPDF usando Decoration.After(): si el corte natural deja menos de 'widows' líneas
    // en la página siguiente, reserva espacio abajo en esta página para empujar las líneas
    // necesarias; si caben menos de 'orphans' líneas, pasa el párrafo completo a la siguiente.
    sealed class ControlDeViudas : IDynamicComponent
    {
        readonly Action<IContainer> _renderizar;
        readonly double _altoLineaNominal;
        readonly int _huerfanas;
        readonly int _viudas;
        readonly double _altoPagina;

        public ControlDeViudas(Action<IContainer> renderizar, double altoLineaNominal, int huerfanas, int viudas, double altoPagina)
        {
            _renderizar = renderizar;
            _altoLineaNominal = Math.Max(altoLineaNominal, 6);
            _huerfanas = Math.Max(huerfanas, 0);
            _viudas = Math.Max(viudas, 0);
            _altoPagina = altoPagina;
        }

        public DynamicComponentComposeResult Compose(QuestPDF.Elements.DynamicContext context)
        {
            var disponibleH = context.AvailableSize.Height;
            var disponibleW = context.AvailableSize.Width;

            if (disponibleW <= 1 || disponibleH <= 1)
                return SinReserva(context);

            var medido = context.CreateElement(c => c.Width(disponibleW).Element(_renderizar));
            var altoTotal = medido.Size.Height;

            if (altoTotal <= disponibleH + 0.1f)
                return SinReserva(context);

            var totalLineas = Math.Max(1, (int)Math.Round(altoTotal / _altoLineaNominal));
            var altoPorLinea = altoTotal / totalLineas;
            var caben = (int)Math.Floor((disponibleH + 0.05f) / altoPorLinea);

            if (caben >= totalLineas)
                return SinReserva(context);

            var necesitaMoverTodo = caben < _huerfanas || totalLineas < _huerfanas + _viudas;

            if (necesitaMoverTodo)
            {
                if (disponibleH < _altoPagina - 1 && altoTotal <= _altoPagina + 0.5f)
                    return ConReserva(context, disponibleH);

                return SinReserva(context);
            }

            var sobrantes = totalLineas - caben;

            if (sobrantes < _viudas)
            {
                var dibujar = totalLineas - _viudas;
                if (dibujar < _huerfanas)
                {
                    if (disponibleH < _altoPagina - 1 && altoTotal <= _altoPagina + 0.5f)
                        return ConReserva(context, disponibleH);
                    return SinReserva(context);
                }

                var espacioPermitido = (dibujar + 0.5f) * altoPorLinea;
                var reserva = Math.Max(0f, disponibleH - espacioPermitido);
                return ConReserva(context, reserva);
            }

            return SinReserva(context);
        }

        static DynamicComponentComposeResult SinReserva(QuestPDF.Elements.DynamicContext context) =>
            new() { HasMoreContent = false, Content = context.CreateElement(c => c.Height(0)) };

        static DynamicComponentComposeResult ConReserva(QuestPDF.Elements.DynamicContext context, float alto) =>
            new() { HasMoreContent = false, Content = context.CreateElement(c => c.Height(alto)) };
    }
    // capas ancladas a un pÃ¡rrafo dentro de una tabla, por bloque del flujo
    static Dictionary<BloqueModelo, List<CapaModelo>> _capasPorBloque = new();

    // imÃ¡genes-renglÃ³n (222 x 1 pt) ancladas a un pÃ¡rrafo del flujo: las lÃ­neas grises
    // que flanquean el bloque del cliente en la factura y en la solicitud de traslado.
    // En el .fodt van ancladas "char" a un pÃ¡rrafo vacÃ­o; con coordenadas absolutas
    // salÃ­an al principio de la hoja en vez de junto a su bloque.
    static Dictionary<BloqueModelo, List<CapaModelo>> _lineasPorBloque = new();
    static Dictionary<BloqueModelo, List<CapaModelo>> _marcosFlotantesPorBloque = new();
    static Dictionary<BloqueModelo, double> _capasQueReservan = new();

    // Dibuja marcos flotantes (style:vertical-rel="paragraph"/"char") en la posición del
    // párrafo ancla sin consumir alto del flujo (PrimaryLayer de alto 0 + Unconstrained).
    static void DibujarMarcosFlotantes(ColumnDescriptor col, List<CapaModelo> marcos)
    {
        col.Item().Layers(capasFlot =>
        {
            capasFlot.PrimaryLayer().Height(0);

            foreach (var marco in marcos)
            {
                var capa = marco;
                var xBase = string.Equals(capa.HorizontalRel, "page", StringComparison.OrdinalIgnoreCase)
                    ? capa.XPt - _margenIzqPagina - _desplazamientoGlobal
                    : capa.XPt - _desplazamientoGlobal;
                var x = (float)Math.Max(xBase, 0);
                var y = (float)Math.Max(capa.YPt, 0);
                var ancho = (float)Math.Max(capa.AnchoPt, 1);
                var esLineaHorizontal = !string.IsNullOrEmpty(capa.ColorTrazo) && capa.AltoPt < 2 && capa.AnchoPt > 2;
                var alto = esLineaHorizontal
                    ? (float)Math.Max(capa.GrosorTrazoPt, 0.6)
                    : (float)Math.Max(capa.AltoPt, 1);

                capasFlot.Layer()
                    .Unconstrained()
                    .PaddingLeft(x)
                    .PaddingTop(y)
                    .Width(ancho)
                    .Height(alto)
                    .Element(cont =>
                    {
                        if (esLineaHorizontal)
                        {
                            cont.Background(ColorDe(capa.ColorTrazo));
                            return;
                        }

                        IContainer caja = cont;

                        if (!string.IsNullOrEmpty(capa.ColorRelleno))
                            caja = caja.Background(ColorDe(capa.ColorRelleno));

                        if (capa.BordeCaja)
                            caja = caja.Border((float)Math.Max(capa.GrosorBordePt, 0.5))
                                       .BorderColor(ColorDe(capa.ColorBorde ?? "#000000"));

                        if (!string.IsNullOrEmpty(capa.ImagenBase64))
                        {
                            try
                            {
                                var bytes = Convert.FromBase64String(capa.ImagenBase64);
                                caja.Image(bytes).FitArea();
                                return;
                            }
                            catch { /* imagen ilegible */ }
                        }

                        var textos = capa.Parrafos
                            .Where(p => p.Runs.Any(r => !string.IsNullOrWhiteSpace(r.Texto)))
                            .ToList();

                        if (textos.Count > 0)
                        {
                            caja.Column(c =>
                            {
                                foreach (var p in textos)
                                    AgregarParrafo(c, p);
                            });
                        }
                    });
            }
        });
    }

    static void DibujarMarcoEnCelda(IContainer cont, CapaModelo capa)
    {
        IContainer caja = cont;

        if (!string.IsNullOrEmpty(capa.ColorRelleno))
            caja = caja.Background(ColorDe(capa.ColorRelleno));

        if (capa.BordeCaja)
            caja = caja.Border((float)Math.Max(capa.GrosorBordePt, 0.5))
                       .BorderColor(ColorDe(capa.ColorBorde ?? "#000000"));

        if (capa.PaddingIzqPt > 0.5) caja = caja.PaddingLeft((float)capa.PaddingIzqPt);
        if (capa.PaddingDerPt > 0.5) caja = caja.PaddingRight((float)capa.PaddingDerPt);
        if (capa.PaddingSupPt > 0.5) caja = caja.PaddingTop((float)capa.PaddingSupPt);
        if (capa.PaddingInfPt > 0.5) caja = caja.PaddingBottom((float)capa.PaddingInfPt);

        if (!string.IsNullOrEmpty(capa.ImagenBase64))
        {
            try
            {
                var bytes = Convert.FromBase64String(capa.ImagenBase64);
                caja.Image(bytes).FitArea();
                return;
            }
            catch { /* imagen ilegible */ }
        }

        var textos = capa.Parrafos
            .Where(p => p.Runs.Any(r => !string.IsNullOrWhiteSpace(r.Texto)))
            .ToList();

        if (textos.Count > 0)
        {
            var anchoInterior = Math.Max(capa.AnchoPt - capa.PaddingIzqPt - capa.PaddingDerPt, 1);
            caja.Column(c =>
            {
                foreach (var p in textos)
                    AgregarParrafo(c, p, 0, anchoInterior);
            });
        }
    }

    // plan de cortes de pÃ¡gina calculado con nuestra propia mediciÃ³n (PaginarPropio).
    // VacÃ­o = se deja que QuestPDF pagine como antes.
    static Dictionary<BloqueModelo, bool> _cortesPropios = new();

    // bloques que empiezan pÃ¡gina: se les quita el espacio anterior (lo colapsa
    // LibreOffice). Lo llena QuestPdfFodtRenderer tras una primera pasada.
    public static HashSet<int> ColapsarEspacios = new();

    // PaginaciÃ³n propia (PaginarPropio) DESACTIVADA: mide bien el texto, pero el alto
    // estimado de las tablas se queda corto y adelanta los cortes (el contrato de
    // tractocamiÃ³n bajaba a 33-41 renglones por pÃ¡gina contra 44-45 de LibreOffice).
    // Queda el cÃ³digo listo para retomarlo con un estimador de tablas mejor.
    public static bool CortesPropiosActivo = false;

    // alto natural de la fuente respecto al tamaÃ±o de letra: QuestPDF aplica LineHeight
    // sobre ese alto, no sobre el tamaÃ±o, asÃ­ que el factor del .fodt se divide por Ã©l.
    internal const double AltoNaturalFuente = 1.03;

    // Caja de lÃ­nea de un texto dentro de una celda, medida contra LibreOffice: 1,22 em
    // (8 pt al 100 % -> 9,75 pt; 10 pt al 100 % -> 12,15; 11 pt al 115 % -> 15,5). LO
    // redondea el alto a pÃ­xeles enteros, de ahÃ­ que no sea un mÃºltiplo exacto.
    static readonly double AltoNaturalFuenteCelda = 1.22;

    // Alto de la fila cuando el marcador de regiÃ³n cae en una columna sin ancho Ãºtil. Ese
    // texto no se puede maquetar (QuestPDF aborta si un glifo no cabe) y LibreOffice lo parte
    // letra por letra: el alto estÃ¡ MEDIDO contra el PDF de LibreOffice de la misma plantilla
    // (16 renglones de 6,9 pt). Sin esto la tabla de firmas salÃ­a 111 pt mÃ¡s arriba.
    static readonly Dictionary<string, double> _altoMarcadorSinAncho = new()
    {
        ["\u00ABTableEnd:DetalleOrden\u00BB"] = 147.3,
    };

    // Alto de UNA lÃ­nea dentro de una celda. Se respeta el fo:line-height del pÃ¡rrafo,
    // salvo en celdas sin padding, donde LibreOffice usa ~1.95 x el tamaÃ±o de fuente
    // (medido: la cadena original va a 10.0 pt con texto de 5). Para un pÃ¡rrafo vacÃ­o el
    // tamaÃ±o sale del estilo (TamanoBasePt), porque no tiene runs.
    static double AltoLineaCelda(ParrafoModelo p, CeldaModelo celda)
    {
        var tam = p.Runs
            .Where(x => !string.IsNullOrWhiteSpace(x.Texto) && x.TamanoPt > 0)
            .Select(x => x.TamanoPt)
            .DefaultIfEmpty(0)
            .Max();
        if (tam <= 0) tam = p.TamanoBasePt;   // pÃ¡rrafo vacÃ­o: el tamaÃ±o sale del estilo
        if (tam <= 0) tam = 9;

        // LibreOffice usa el alto de lÃ­nea de la FUENTE cuando el estilo no pide mÃ¡s; si el
        // .fodt declara un interlineado generoso (los sellos de la factura, 200 %) manda el
        // declarado. Antes habÃ­a un factor fijo de 1.95 que sÃ³lo valÃ­a para los sellos: en
        // las bitÃ¡coras (9 pt al 100 %) inflaba cada renglÃ³n ~70 % y las filas salÃ­an mÃ¡s
        // altas de lo que pide el documento.
        //
        // LibreOffice arma la caja de lÃ­nea como pct x 1,22 em. Cuando el .fodt NO pide
        // interlineado extra (100 % o menos) la diferencia se nota: los sellos del
        // comprobante de traslado (8 pt al 100 %) daban 8,76 pt de paso contra 9,75.
        // Se aplica SÃ“LO al texto chico (los bloques de sellos y cadena original de los
        // CFDI): con el factor para todo, las celdas de 10 pt de la solicitud de carta
        // porte crecÃ­an ~14 pt y la empujaban a una segunda pÃ¡gina que LibreOffice no
        // tiene. El resto sigue con el 1,15 de siempre.
        //
        // EstiloDe convierte el alto pedido en factor dividiÃ©ndolo por AltoNaturalFuente
        // (1,05), asÃ­ que hay que pedir 1,22 x 1,05 para que el PDF salga a 1,22 em.
        var pct = tam > 0 ? p.AltoLineaPt / tam : 1.0;
        var fuenteCelda = p.Runs
            .Where(x => !string.IsNullOrWhiteSpace(x.Texto) && !string.IsNullOrEmpty(x.Fuente))
            .Select(x => x.Fuente)
            .FirstOrDefault() ?? "";
        var esCalibri = fuenteCelda.StartsWith("Calibri", StringComparison.OrdinalIgnoreCase) ||
                        fuenteCelda.StartsWith("Carlito", StringComparison.OrdinalIgnoreCase);
        var altoNaturalNoCalibri = fuenteCelda.StartsWith("Trebuchet", StringComparison.OrdinalIgnoreCase)
            ? 1.161
            : 1.15;

        // Calibri/Carlito tienen altura natural ~1.22 em (Trebuchet MS ~1.161 em, Arial/Times ~1.15 em).
        // En celdas con fo:line-height en porcentaje (100%, 107%, etc.), ExtractorFodt
        // guarda tam * pct; multiplicarlo por el alto natural * AltoNaturalFuente
        // reproduce exactamente el alto de fila de LibreOffice (p.ej. 10pt al 100% -> 12.2pt,
        // 10pt al 107% -> 13.05pt, 8pt al 100% -> 9.76pt en ComprobanteTraslado).
        double natural;
        if (esCalibri && pct > 0.5 && pct <= 1.20)
            natural = tam * AltoNaturalFuenteCelda * pct * AltoNaturalFuente;
        else if (esCalibri && pct > 1.20 && pct <= 1.35)
            natural = p.AltoLineaPt;
        else if (!esCalibri && p.AltoLineaPct > 0.5 && p.AltoLineaPct <= 1.35)
            natural = tam * altoNaturalNoCalibri * p.AltoLineaPct * AltoNaturalFuente;
        else
            natural = tam * altoNaturalNoCalibri * AltoNaturalFuente;

        return p.AltoLineaPt > tam * 1.15 * 1.3 ? p.AltoLineaPt * AltoNaturalFuente : natural;
    }

    static void AgregarTabla(IContainer contenedor, TablaModelo tabla, double anchoDisponible = 0,
                             bool compensarMargen = true)
    {

        // Cada tabla tiene su propio margen negativo. El flujo ya estÃ¡ desplazado por el
        // mayor; aquÃ­ se compensa la diferencia (una tabla con -5.4 no debe irse a -49.7).
        // El ancho disponible se reduce en la misma medida: si no, la tabla + el padding
        // exceden la caja y QuestPDF lanza DocumentLayoutException (PDF vacÃ­o).
        //
        // compensarMargen = false para las tablas ANIDADAS: esa compensaciÃ³n es del flujo
        // de la pÃ¡gina. Dentro de una celda metÃ­a un PaddingLeft de 76 pt y dejaba la tabla
        // anidada sin ancho (DocumentLayoutException).
        var extra = compensarMargen ? tabla.MargenIzqPt - _desplazamientoGlobal : 0;

        if (extra > 0.5)
        {
            contenedor = contenedor.PaddingLeft((float)extra);
            if (anchoDisponible > 0) anchoDisponible = Math.Max(anchoDisponible - extra, 50);
        }
        else if (extra < -0.5)
        {
            // Margen izquierdo negativo pequeño (p.ej. -0.191 cm en Word/Writer) que no
            // desplazó el flujo global: se alinea la tabla a su coordenada real.
            contenedor = contenedor.OffsetX((float)extra);
        }

        var anchoTabla = tabla.AnchoDeclaradoPt > 0 ? tabla.AnchoDeclaradoPt : tabla.ColumnasPt.Sum();

        // tabla mÃ¡s angosta que el Ã¡rea de texto y declarada al centro (la de sueldo del
        // contrato CCI): se centra igual que LibreOffice. Si es más ancha que el área de
        // texto (p.ej. Tabla3 19.297cm / Tabla4 19.214cm en PlantillaIntereses), sobresale
        // equitativamente hacia ambos márgenes con OffsetX negativo.
        if (tabla.AlineacionTabla == "center" && anchoDisponible > 1 && anchoTabla > 1)
        {
            var anchoCentro = compensarMargen ? Math.Max(anchoDisponible - _compensacionDerFlujo, 1) : anchoDisponible;
            var anchoEfectivo = Math.Min(anchoTabla, anchoDisponible);
            var libre = anchoCentro - anchoEfectivo;

            if (libre > 1)
            {
                contenedor = contenedor.PaddingLeft((float)(libre / 2));
                anchoDisponible = anchoEfectivo;
            }
            else if (libre < -0.5)
            {
                contenedor = contenedor.OffsetX((float)(libre / 2));
            }
        }

        // BorderAlignmentMiddle centra el trazo sobre la arista de cada celda (grosor/2
        // hacia afuera). En Writer (collapsing border model) la mitad exterior de los bordes
        // superior e inferior de la tabla ocupa espacio en el flujo; reservarla evita que las
        // tablas consecutivas suban ~0.5 pt por cada tabla.
        if (compensarMargen && tabla.Filas.Count > 0)
        {
            var bordeExtSup = tabla.Filas[0].Celdas
                .Where(c => c.BordeSup)
                .Select(c => c.GrosorBordePt > 0 ? c.GrosorBordePt : 0.7)
                .DefaultIfEmpty(0).Max() / 2;
            var bordeExtInf = tabla.Filas
                .SelectMany((f, i) => f.Celdas
                    .Where(c => c.BordeInf && i + Math.Max(c.SpanFilas, 1) >= tabla.Filas.Count)
                    .Select(c => c.GrosorBordePt > 0 ? c.GrosorBordePt : 0.7))
                .DefaultIfEmpty(0).Max() / 2;
            if (bordeExtSup > 0) contenedor = contenedor.PaddingTop((float)bordeExtSup);
            if (bordeExtInf > 0) contenedor = contenedor.PaddingBottom((float)bordeExtInf);
        }

        contenedor.Table(t =>
        {
            // Dos modos:
            //  - con ancho conocido (tabla del flujo): columnas fijas escaladas al espacio
            //    real, para que una tabla mÃ¡s ancha que la caja no reviente el documento.
            //  - sin ancho conocido (tabla ANIDADA dentro de una celda): columnas relativas,
            //    asÃ­ se adaptan al ancho de la celda en vez de exigir uno mayor.
            var total = tabla.ColumnasPt.Sum();

            t.ColumnsDefinition(c =>
            {
                for (var i = 0; i < tabla.ColumnasPt.Count; i++)
                {
                    var ancho = tabla.ColumnasPt[i];

                    if (anchoDisponible > 0 && total > 0)
                    {
                        var factor = Math.Min(anchoTabla, anchoDisponible) / total;
                        c.ConstantColumn((float)Math.Max(ancho * factor, 1), Unit.Point);
                    }
                    else
                    {
                        // total == 0: la tabla no declara anchos. Con anchoDisponible > 0 el
                        // factor saldrÃ­a infinito y reventarÃ­a el documento.
                        var peso = total <= 0 ? 1.0 / Math.Max(tabla.ColumnasPt.Count, 1) : ancho / total;
                        c.RelativeColumn((float)Math.Max(peso, 0.01));
                    }
                }
            });

            // factor con el que se escalaron las columnas de ESTA tabla (ver ColumnsDefinition)
            var factorColumnas = (anchoDisponible > 0 && total > 0)
                ? Math.Min(anchoTabla, anchoDisponible) / total
                : 1.0;

            // Si la tabla tiene una columna inservible (en la factura hay una de 1.5 pt a la
            // que, con el padding de 5.5 por lado, no le queda ancho), no se puede pedir
            // "alto mÃ­nimo de fila": QuestPDF aborta el documento entero con "conflicting
            // size constraints". Esas tablas se siguen dibujando como antes, sin el mÃ­nimo.
            var hayColumnaMinuscula = tabla.ColumnasPt.Any(c => c * factorColumnas < 8);

            var indiceFila = 0;
            foreach (var fila in tabla.Filas)
            {
                var columna = 0;

                foreach (var celda in fila.Celdas)
                {
                    // Ancho real de esta celda: es el que hay que darle a la tabla que lleve
                    // dentro. Con su ancho declarado tal cual no cabe (DocumentLayoutException)
                    // y con 0 reparte las columnas a ojo, asÃ­ que "NÃºmero de Documento" partÃ­a
                    // en dos lÃ­neas.
                    var anchoCelda = 0.0;

                    for (var k = 0; k < Math.Max(celda.SpanColumnas, 1) && columna + k < tabla.ColumnasPt.Count; k++)
                        anchoCelda += tabla.ColumnasPt[columna + k];

                    anchoCelda *= factorColumnas;
                    columna += Math.Max(celda.SpanColumnas, 1);

                    var celdaTabla = t.Cell();

                    if (celda.SpanFilas > 1) celdaTabla = celdaTabla.RowSpan((uint)celda.SpanFilas);
                    if (celda.SpanColumnas > 1) celdaTabla = celdaTabla.ColumnSpan((uint)celda.SpanColumnas);

                    // bordes y paddings SOLO los que declara el .fodt: antes se ponÃ­a
                    // un borde gris a todas las celdas y las tablas crecÃ­an de mÃ¡s.
                    IContainer cont = celdaTabla;

                    // Evita partir a la mitad una celda corta entre páginas (p.ej. el recuadro
                    // "Manifiesto..." de SC_PMoral dejando 1 línea en la página 1 y 3 en la 2).
                    var textoCeldaLen = celda.Parrafos.Sum(p => p.Runs.Sum(r => r.Texto?.Length ?? 0));
                    if (celda.SpanFilas == 1 && celda.TablasAnidadas.Count == 0 &&
                        celda.Parrafos.Count <= 6 && textoCeldaLen <= 800)
                        cont = cont.ShowEntire();
                    // grosor declarado en el .fodt (0.5pt en la liquidaciones); 0.7 si no lo dice
                    var grosor = celda.GrosorBordePt > 0.05 ? (float)celda.GrosorBordePt : 0.7f;

                    // fondo de la celda (los tÃ­tulos de tabla de los reportes lo llevan).
                    // Se parsea el hex a mano: Color.FromHex con el '#' lanzaba y el catch
                    // dejaba la celda sin fondo.
                    if (!string.IsNullOrEmpty(celda.ColorFondo))
                        cont = cont.Background(ColorDe(celda.ColorFondo));

                    // Color de borde tal como lo declara el .fodt (fo:border / fo:border-*).
                    // BorderAlignmentMiddle centra el trazo sobre la arista (table:border-model="collapsing"
                    // de ODF), evitando bordes dobles entre celdas vecinas y conservando el alto visual exacto.
                    if (celda.BordeSup) cont = cont.BorderTop(grosor).BorderAlignmentMiddle().BorderColor(ColorDe(celda.ColorBordeSup));
                    if (celda.BordeInf) cont = cont.BorderBottom(grosor).BorderAlignmentMiddle().BorderColor(ColorDe(celda.ColorBordeInf));
                    if (celda.BordeIzq) cont = cont.BorderLeft(grosor).BorderAlignmentMiddle().BorderColor(ColorDe(celda.ColorBordeIzq));
                    if (celda.BordeDer) cont = cont.BorderRight(grosor).BorderAlignmentMiddle().BorderColor(ColorDe(celda.ColorBordeDer));

                    // El aire NO viene del padding de la celda (0.019cm = 0.5 pt): viene de
                    // los mÃ¡rgenes del pÃ¡rrafo (fo:margin-left/right). Sin ellos el texto
                    // izquierdo/derecho queda pegado al borde de la tabla. Se toma el MAYOR
                    // de los pÃ¡rrafos de la celda: la lÃ­nea que da el aire puede no ser la
                    // primera (p.ej. la "Cadena Original..." junto al QR).
                    var margenIzqMax = celda.Parrafos.Count == 0 ? 0 : celda.Parrafos.Max(p => Math.Max(p.MargenIzqPt + Math.Min(p.SangriaPt, 0), 0));
                    var margenDerMax = celda.Parrafos.Count == 0 ? 0 : celda.Parrafos.Max(p => p.MargenDerPt);

                    var padIzq = celda.PaddingIzqPt + margenIzqMax;
                    var padDer = celda.PaddingDerPt + margenDerMax;

                    // Aire lateral mÃ­nimo cuando la celda no declara padding (0.019cm en
                    // el .fodt): LibreOffice deja ~2 pt y por debajo de eso el texto se ve
                    // pegado al borde (y en la tabla del pie, encima del QR).
                    //
                    // Estuvo en 5.5 pt: con ese valor la cabecera del detalle de la orden
                    // de salida partÃ­a "CODIGO" y "CANTIDAD" en dos renglones. Si al bajar
                    // el valor aparece texto pegado en la factura o en la orden de compra,
                    // basta con devolverlo a 5.5f (respaldo en C:\tmp\respaldo_questpdf_20261113).
                    const float insetMinimo = 2.5f;

                    // una celda que lleva imagen (el QR) no puede gastar paddings
                    // laterales: la imagen mide justo el ancho de la columna
                    var celdaConImagen = celda.Parrafos.Any(x => !string.IsNullOrEmpty(x.ImagenBase64));
                    var celdaConMarcaRegion = celda.Parrafos.Any(p =>
                    {
                        var texto = string.Concat(p.Runs.Select(r => r.Texto));
                        return texto.Contains("TableStart:", StringComparison.Ordinal) ||
                               texto.Contains("TableEnd:", StringComparison.Ordinal);
                    });

                    // El padding vertical del .fodt (0.3cm = 8.5 pt) tampoco coincide con lo
                    // que aplica LibreOffice: medido en el bloque de certificados, el paso
                    // entre lÃ­neas real es 11.3 pt = lÃ­nea 7.2 + ~4 de padding. Por encima de
                    // 4 pt las lÃ­neas se separan de mÃ¡s.
                    const float paddingVerticalMaximo = 6f;

                    var esEncabezadoAislado = celda.VerticalAlign == "bottom" && celda.Parrafos.Count >= 2 &&
                        celda.Parrafos[^1].Runs.Count == 0 &&
                        celda.Parrafos[0].Runs.Any(r => !string.IsNullOrWhiteSpace(r.Texto) && r.TamanoPt >= 14);
                    var sinBordeNiPadIzq = !celda.BordeIzq && padIzq <= 0.05;
                    var sinBordeNiPadDer = !celda.BordeDer && padDer <= 0.05;
                    var insetIzqCelda = (esEncabezadoAislado && padIzq <= 0.5) || sinBordeNiPadIzq ? 0f : insetMinimo;
                    var insetDerCelda = sinBordeNiPadDer ? 0f : insetMinimo;
                    var tamMarcaRegion = celdaConMarcaRegion
                        ? celda.Parrafos
                            .SelectMany(p => p.Runs)
                            .Where(r => !string.IsNullOrWhiteSpace(r.Texto))
                            .Select(r => r.TamanoPt > 0 ? r.TamanoPt : 9)
                            .DefaultIfEmpty(9)
                            .Max()
                        : 0;
                    var anchoMinGlifoMarca = tamMarcaRegion * 0.9;
                    var padIzqBase = (float)Math.Max(padIzq, insetIzqCelda);
                    var padDerBase = (float)Math.Max(padDer, insetDerCelda);
                    var anchoTextoReal = anchoCelda - padIzqBase - padDerBase;
                    var necesitaReducirPaddingMarca = celdaConMarcaRegion &&
                        anchoTextoReal < anchoMinGlifoMarca + 0.3;

                    float paddingIzq, paddingDer;
                    if (celdaConImagen)
                    {
                        paddingIzq = 0;
                        paddingDer = 0;
                    }
                    else if (necesitaReducirPaddingMarca)
                    {
                        var anchoSeguro = (float)(anchoMinGlifoMarca + 0.5);
                        paddingIzq = Math.Min(padIzqBase, Math.Max((float)anchoCelda - anchoSeguro, 0f));
                        paddingDer = Math.Min(padDerBase, Math.Max((float)anchoCelda - paddingIzq - anchoSeguro, 0f));
                    }
                    else
                    {
                        paddingIzq = padIzqBase;
                        paddingDer = padDerBase;
                    }

                    // En QuestPDF el borde se dibuja dentro de la caja sin restar espacio al
                    // contenido; en Writer (bordes colapsados) cada borde reserva medio grosor
                    // fuera del padding (tanto en vertical como en horizontal).
                    var grosorIzq = celda.GrosorBordeIzqPt > 0.05 ? (float)celda.GrosorBordeIzqPt : grosor;
                    var grosorDer = celda.GrosorBordeDerPt > 0.05 ? (float)celda.GrosorBordeDerPt : grosor;
                    var aireBordeIzq = !celdaConImagen && !necesitaReducirPaddingMarca && celda.BordeIzq &&
                                       grosorIzq > 0.55f && celda.PaddingIzqPt > 0.5 && anchoCelda > paddingIzq + paddingDer + 10
                        ? grosorIzq / 2f : 0f;
                    var aireBordeDer = !celdaConImagen && !necesitaReducirPaddingMarca && celda.BordeDer &&
                                       grosorDer > 0.55f && celda.PaddingDerPt > 0.5 && anchoCelda > paddingIzq + paddingDer + 10
                        ? grosorDer / 2f : 0f;
                    var aireBordeSup = !celdaConImagen && celda.BordeSup && celda.GrosorBordePt > 0.05 && fila.AltoFijoPt <= 0.5
                        ? grosor / 2f : 0f;
                    var aireBordeInf = !celdaConImagen && celda.BordeInf && celda.GrosorBordePt > 0.05 && fila.AltoFijoPt <= 0.5
                        ? grosor / 2f : 0f;

                    paddingIzq += aireBordeIzq;
                    paddingDer += aireBordeDer;

                    // LibreOffice redondea paddings/anchos en twips (p.ej. 0.191cm = 108 twips =
                    // 5.40pt vs 5.414pt) y aplica kerning del TTF, mientras Skia suma avances sin
                    // kerning. Cuando un texto de una sola línea rebasa el ancho útil por menos de
                    // ~0.6 pt (p.ej. "Excelente(«R35»)" en EntrevistaSalida: 87.30 vs 87.27 pt),
                    // ceder esa fracción mínima del padding lateral evita partir el último glifo.
                    if (!celdaConImagen && !necesitaReducirPaddingMarca && celda.Parrafos.Count == 1 &&
                        anchoCelda > paddingIzq + paddingDer + 10)
                    {
                        var pUnico = celda.Parrafos[0];
                        var anchoUtilPrev = anchoCelda - paddingIzq - paddingDer;
                        var runsConTexto = pUnico.Runs.Where(r => !string.IsNullOrEmpty(r.Texto)).ToList();
                        if (runsConTexto.Count > 0)
                        {
                            var anchoSinKern = runsConTexto.Sum(r =>
                                PaginarPropio.Medir(r.Texto, NormalizarFuente(r.Fuente),
                                    r.TamanoPt > 0 ? r.TamanoPt : 10, r.Negrita, kerning: false));
                            var mismoEstilo = runsConTexto.All(r =>
                                string.Equals(r.Fuente, runsConTexto[0].Fuente, StringComparison.OrdinalIgnoreCase) &&
                                Math.Abs(r.TamanoPt - runsConTexto[0].TamanoPt) < 0.05 &&
                                r.Negrita == runsConTexto[0].Negrita);
                            var anchoConKern = mismoEstilo
                                ? PaginarPropio.Medir(string.Concat(runsConTexto.Select(r => r.Texto)),
                                    NormalizarFuente(runsConTexto[0].Fuente),
                                    runsConTexto[0].TamanoPt > 0 ? runsConTexto[0].TamanoPt : 10,
                                    runsConTexto[0].Negrita, kerning: true)
                                : runsConTexto.Sum(r =>
                                    PaginarPropio.Medir(r.Texto, NormalizarFuente(r.Fuente),
                                        r.TamanoPt > 0 ? r.TamanoPt : 10, r.Negrita, kerning: true));

                            if (anchoSinKern > anchoUtilPrev - 0.2 && anchoConKern <= anchoUtilPrev + 0.08)
                            {
                                var falta = (float)Math.Min(Math.Max(anchoSinKern - anchoUtilPrev + 0.35, 0.35), 1.0);
                                if (pUnico.Alineacion == "center")
                                {
                                    var porLado = Math.Min(falta / 2f, Math.Min(paddingIzq, paddingDer));
                                    paddingIzq -= porLado;
                                    paddingDer -= porLado;
                                }
                                else
                                {
                                    var recorte = Math.Min(falta, paddingDer);
                                    paddingDer -= recorte;
                                }
                            }
                        }
                    }

                    // En el modelo de bordes colapsados de Writer, cuando celdas de una misma
                    // fila comparten borde superior con distinto padding-top (p.ej. Tabla2/3 en
                    // InformacionEmpleado: 0.132 cm en A2/B2 vs 0.026 cm en C2/D2), Writer
                    // aplica el mayor padding superior de la fila para alinear todas las celdas.
                    var padSupDeclarado = Math.Min((float)celda.PaddingSupPt, paddingVerticalMaximo);
                    var padSupFilaMax = !celdaConImagen && celda.BordeSup && celda.SpanFilas == 1 && fila.Celdas.Count > 1
                        ? fila.Celdas
                            .Where(c => c.BordeSup && c.SpanFilas == 1)
                            .Select(c => Math.Min((float)c.PaddingSupPt, paddingVerticalMaximo))
                            .DefaultIfEmpty(padSupDeclarado)
                            .Max()
                        : padSupDeclarado;
                    var padSupCelda = Math.Max(padSupDeclarado, padSupFilaMax) + aireBordeSup;
                    var padInfCelda = Math.Min((float)celda.PaddingInfPt, paddingVerticalMaximo) + aireBordeInf;

                    cont = cont
                        .PaddingLeft(paddingIzq)
                        .PaddingRight(paddingDer)
                        .PaddingTop(padSupCelda)
                        .PaddingBottom(padInfCelda);

                    // Una columna residual (la factura trae una de 1.5 pt) no tiene ancho Ãºtil:
                    // ahÃ­ no se puede meter ningÃºn elemento de alto fijo ni pedir alto mÃ­nimo,
                    // porque QuestPDF aborta el documento entero. Con texto se dibuja igual
                    // (queda recortado, como antes).
                    var anchoTexto = anchoCelda - paddingIzq - paddingDer;

                    // alto mÃ­nimo de la fila (style:min-row-height): es lo que le da aire a cada
                    // grupo de la bitÃ¡cora de pedidos. Se pide en cada celda de la fila y la fila
                    // toma el mayor; en las columnas residuales (anchoTexto <= 0) no se pide
                    // porque QuestPDF aborta el documento entero.
                    // style:row-height fija el alto EXACTO de la fila (asÃ­ lo deja
                    // LibreOffice en la cabecera del auxiliar de proveedores): manda sobre
                    // el mÃ­nimo y los pÃ¡rrafos vacÃ­os no la estiran.
                    var altoFila = fila.AltoFijoPt > 0.5
                        ? Math.Max(fila.AltoFijoPt, fila.AltoMinimoPt)
                        : fila.AltoMinimoPt;
                    // style:row-height (AltoFijoPt) fija el alto EXACTO de la fila en Writer y
                    // no crece por subíndices (p.ej. CartaPorte.Fila = 0.30 cm = 8.50 pt); sólo
                    // style:min-row-height se expande para acomodar el descenso del subíndice.
                    var extensionSubindice = 0.0;
                    if (fila.AltoFijoPt <= 0.5)
                    {
                        var altoConSub = fila.Celdas
                            .Where(c => c.Parrafos.SelectMany(p => p.Runs).Any(r => r.Posicion == "sub" && r.TamanoPt > 0))
                            .Select(c => c.Parrafos.Sum(p =>
                            {
                                var sub = p.Runs.Where(r => r.Posicion == "sub" && r.TamanoPt > 0).ToList();
                                if (sub.Count > 0)
                                    return sub.Max(r => r.TamanoPt * 100 / r.EscalaPosicion.GetValueOrDefault(58)) * 1.15;
                                return p.AltoLineaPt > 0 ? p.AltoLineaPt : (p.TamanoBasePt > 0 ? p.TamanoBasePt : 10) * 1.15;
                            }))
                            .DefaultIfEmpty(0).Max();
                        if (altoConSub > altoFila + 0.5)
                        {
                            extensionSubindice = altoConSub - altoFila;
                            altoFila = altoConSub;
                        }
                    }

                    var runSubindice = celda.Parrafos.SelectMany(p => p.Runs)
                        .FirstOrDefault(r => r.Posicion == "sub" && !string.IsNullOrWhiteSpace(r.Texto));
                    var runSuperindice = runSubindice == null
                        ? celda.Parrafos.SelectMany(p => p.Runs)
                            .FirstOrDefault(r => r.Posicion == "super" && !string.IsNullOrWhiteSpace(r.Texto))
                        : null;

                    // ODF 1.3 §20.340: style:row-height fija el alto EXACTO de la fila en Writer
                    // y recorta verticalmente el contenido que desborda (p.ej. marcadores de
                    // región largos en PlantillaDepositos.fodt). Se aplica cuando la celda no
                    // abarca varias filas ni usa desplazamientos de sub/superíndice.
                    var esAltoFijoRecortado = fila.AltoFijoPt > 0.5 && celda.SpanFilas == 1 &&
                        anchoTexto > 0.5 && celda.TablasAnidadas.Count == 0 &&
                        !celdaConImagen && celda.MarcosFlotantes.Count == 0 &&
                        runSubindice == null && runSuperindice == null;

                    // style:min-row-height / style:row-height de la fila. Estaba calculado
                    // pero NO se aplicaba en ninguna parte: en el comprobante de traslado
                    // cada rÃ³tulo de Â«Sello digital del CFDIÂ» pide 0,557 cm (15,8 pt) y
                    // quedaba pegado al borde (LibreOffice deja 15,75 pt hasta el siguiente
                    // rÃ³tulo, nosotros 9,14). Se pide como alto MÃNIMO: el texto que no cabe
                    // sigue estirando la fila. En las columnas residuales no se pide
                    // (QuestPDF aborta el documento entero si el ancho es 0).
                    if (!esAltoFijoRecortado && altoFila > 0.5 && anchoTexto > 0.5)
                        cont = cont.MinHeight((float)altoFila);


                    if (celda.Parrafos.Count == 0)
                    {
                        if (esAltoFijoRecortado)
                            cont.Height((float)Math.Max(altoFila - padSupCelda - padInfCelda, 0.5)).Text("");
                        else
                            cont.Text("");
                        continue;
                    }

                    // style:vertical-align de la celda ("middle" en las cabeceras de los
                    // reportes): sin esto, en una fila de tres renglones el tÃ­tulo de una
                    // sola lÃ­nea se pegaba arriba en vez de quedar centrado.
                    // SÃ³lo "middle": con "bottom" LibreOffice no baja el contenido como
                    // QuestPDF (la caja de totales de la factura lo declara y allÃ¡ se ve
                    // arriba), asÃ­ que se deja el comportamiento de siempre.
                    // Los encabezados text:h de una celda (Comprobante de traslado) los
                    // centra LibreOffice verticalmente, aunque la celda declare top.
                    var alinearMedioCelda = celda.VerticalAlign == "middle";
                    if (!esAltoFijoRecortado && alinearMedioCelda)
                        cont = cont.AlignMiddle();

                    // "bottom" sÃ³lo en bloques de TRES o mÃ¡s filas: la solicitud de traslado
                    // tiene el "Favor de referenciar en el pagoâ€¦" abarcando tres filas y
                    // LibreOffice lo pinta al fondo (sin esto salÃ­a pegado a
                    // "(IMPORTE_CON_LETRA)"). Con dos filas NO se toca: los rÃ³tulos SUBTOTAL:/
                    // I.V.A. (0%): de la factura tambiÃ©n abarcan dos y allÃ¡ LibreOffice los
                    // deja arriba, como QuestPDF.
                    // "bottom" en LibreOffice: lo MEDIDO es que en las filas normales el
                    // contenido queda CENTRADO (la caja de totales: Â«CANTIDAD CON LETRA:Â» y
                    // Â«(IMPORTE_CON_LETRA)Â» a 11 pt, que es media fila + media lÃ­nea, no el
                    // alto completo ni el fondo), y en bloques de 3+ filas queda al FONDO
                    // (Â«Favor de referenciarâ€¦Â» de la solicitud). Antes se ignoraba y los textos
                    // salÃ­an pegados arriba.
                    var alinearFondoCelda = false;
                    if (celda.VerticalAlign == "bottom" && !tabla.SolapadaPorFondo)
                    {
                        if (celda.SpanFilas >= 3 || fila.Celdas.Count == 1)
                        {
                            alinearFondoCelda = true;
                            if (!esAltoFijoRecortado) cont = cont.AlignBottom();
                        }
                        else
                        {
                            alinearMedioCelda = true;
                            if (!esAltoFijoRecortado) cont = cont.AlignMiddle();
                        }
                    }

                    // alineaciÃ³n de la celda (center / end / start) segÃºn su pÃ¡rrafo
                    if (extensionSubindice > 0.5 && alinearMedioCelda) cont = cont.OffsetY((float)(-extensionSubindice / 2));
                    if (runSubindice != null)
                    {
                        // En filas con alto fijo (style:row-height) Writer aplica el descenso
                        // automático de subíndice (~20% del tamaño original, DFLT_ESC_AUTO_SUB)
                        // sin estirar la fila; en filas con min-row-height se usa 33% (compensado
                        // con -extensionSubindice/2).
                        var escala = runSubindice.EscalaPosicion.GetValueOrDefault(58);
                        var pctDescenso = fila.AltoFijoPt > 0.5 ? 20.0 : 33.0;
                        if (escala > 0)
                        {
                            var tamOrigSub = runSubindice.TamanoPt * 100 / escala;
                            var extraAscenso = fila.AltoFijoPt <= 0.5 && !alinearMedioCelda
                                ? (tamOrigSub - runSubindice.TamanoPt) * 0.38
                                : 0;
                            cont = cont.OffsetY((float)(tamOrigSub * pctDescenso / 100 + extraAscenso));
                        }
                    }
                    else if (runSuperindice != null)
                    {
                        // Elevación automática de superíndice en Writer (~28% del tamaño
                        // original, DFLT_ESC_AUTO_SUPER) sin alterar el alto de la fila
                        // (p.ej. "Tipo Ubicación:" / "PermSCT:" en Carta Porte).
                        var escala = runSuperindice.EscalaPosicion.GetValueOrDefault(58);
                        if (escala > 0)
                            cont = cont.OffsetY((float)(-runSuperindice.TamanoPt * 28 / escala));
                    }
                    else if (celda.VerticalAlign == "bottom" && !tabla.SolapadaPorFondo &&
                             celda.Parrafos.Count >= 2 &&
                             celda.Parrafos[^1].Runs.Count == 0)
                    {
                        var primeroTexto = celda.Parrafos[0].Runs.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.Texto));
                        if (primeroTexto != null && primeroTexto.TamanoPt >= 14)
                        {
                            var altoContenido = celda.Parrafos[0].AltoLineaPt + celda.Parrafos[^1].AltoLineaPt;
                            var subir = Math.Max((altoFila - altoContenido) / 2, 0);
                            if (subir > 0.5) cont = cont.OffsetY((float)-subir);
                            if (esEncabezadoAislado) cont = cont.OffsetY(4f);
                        }
                    }

                    var alineacionCelda = celda.Parrafos
                        .Select(x => x.Alineacion)
                        .FirstOrDefault(a => !string.IsNullOrEmpty(a)) ?? "";

                    if (!string.IsNullOrEmpty(alineacionCelda))
                        cont = Alinear(cont, alineacionCelda);


                    // Si la celda lleva un objeto anclado SIN imagen propia (un hueco), hay que
                    // reservar su alto. Si la imagen se dibuja en la celda, ella misma da el alto.
                    var altoObjeto = celda.Parrafos.Count == 0 ? 0 : celda.Parrafos.Max(p => p.AltoObjetoPt);
                    if (altoObjeto > 0 && !celdaConImagen)
                        cont = cont.PaddingTop((float)(celda.PaddingSupPt + altoObjeto));

                    // mÃ¡rgenes verticales de los pÃ¡rrafos de la celda (fo:margin-top/bottom)
                    // mismo criterio que en el flujo: medido, sumarlos empeora el ajuste
                    if (AplicarMargenesVerticales && celda.Parrafos.Count > 0)
                    {
                        var margenSup = celda.Parrafos[0].MargenSupPt;
                        var margenInf = celda.Parrafos[^1].MargenInfPt;

                        if (margenSup > 0.5)
                            cont = cont.PaddingTop((float)(celda.PaddingSupPt + altoObjeto + margenSup));

                        if (margenInf > 0.5)
                            cont = cont.PaddingBottom((float)(celda.PaddingInfPt + margenInf));
                    }

                    // si la celda lleva una imagen anclada (el QR), se dibuja aquÃ­ dentro:
                    // asÃ­ su posiciÃ³n la manda el flujo y no queda encima del texto
                    var conImagen = celda.Parrafos.FirstOrDefault(x => !string.IsNullOrEmpty(x.ImagenBase64));

                    if (conImagen != null)
                    {
                        cont.Column(c =>
                        {
                            try
                            {
                                var bytes = Convert.FromBase64String(conImagen.ImagenBase64);

                                // Se usa el tamaÃ±o que declara el marco (svg:width). Con
                                // ScaleToFit la imagen se estiraba hasta el ancho de la celda:
                                // el logo de la orden de salida salÃ­a 2.8 veces mÃ¡s grande
                                // (160 pt en vez de 58). Si no cabe en la celda se cae al
                                // ScaleToFit, que es el que evita el DocumentLayoutException.
                                var anchoImagen = conImagen.ImagenAnchoPt;
                                var altoImagen = conImagen.ImagenAltoPt;

                                if (anchoImagen > 0 && (anchoTexto <= 0 || anchoImagen <= anchoTexto))
                                {
                                    var contImagen = c.Item().Width((float)anchoImagen);

                                    if (altoImagen > 0)
                                        contImagen = contImagen.Height((float)altoImagen);

                                    var img = contImagen.Image(bytes);

                                    // El frame del .fodt puede fijar 100x80 aunque el PNG sea
                                    // cuadrado. Sin Height + FitUnproportionally QuestPDF
                                    // conserva la proporciÃ³n y el logo sale 100x100.
                                    if (altoImagen > 0)
                                        img.FitUnproportionally();
                                }
                                else
                                    c.Item().ScaleToFit().Image(bytes);
                            }
                            catch { /* imagen ilegible */ }

                            foreach (var otro in celda.Parrafos.Where(x => !ReferenceEquals(x, conImagen) && x.Runs.Count > 0))
                                c.Item().DefaultTextStyle(EstiloDe(otro.Runs, otro.AltoLineaPt)).Text(otro.Runs[0].Texto);
                        });

                        continue;
                    }

                    // Cada pÃ¡rrafo de la celda va como bloque propio. Antes se unÃ­an con
                    // tt.Span("\n"), y ese salto toma el estilo por defecto de la pÃ¡gina
                    // (Arial 9 -> 10.8 pt) que se SUMABA a la lÃ­nea de 6 pt: el paso salÃ­a
                    // 17.8 pt donde LibreOffice usa 11.3 (medido en el bloque de certificados).
                    void PintarContenidoCelda(IContainer contenedorCelda)
                    {
                    contenedorCelda.Column(cc =>
                    {
                        var primero = true;

                        // El alto de un pÃ¡rrafo vacÃ­o ya incluye sus mÃ¡rgenes (ver altoVacio):
                        // si el ÃšLTIMO pÃ¡rrafo de la celda es vacÃ­o, el margen inferior del
                        // bloque de abajo lo volverÃ­a a sumar y la fila quedaba 10 pt mÃ¡s alta
                        // que en LibreOffice (liquidaciones: 31,5 contra 25,8).
                        var ultimoVacio = false;

                        foreach (var p in celda.Parrafos)
                        {
                            var texto = string.Concat(p.Runs.Select(r => r.Texto));

                            // Nested tables are stored separately from paragraphs. Empty
                            // paragraphs that follow a nested table are therefore rendered
                            // before it and create an artificial gap above the table.
                            if (celda.TablasAnidadas.Count > 0 && string.IsNullOrWhiteSpace(texto))
                            {
                                primero = false;
                                continue;
                            }

                            // Un pÃ¡rrafo vacÃ­o dentro de la celda NO es ruido: ocupa una lÃ­nea y
                            // es lo que le da alto a la fila. La bitÃ¡cora de pedidos mete uno en
                            // la celda de Precio de cada producto (y otro en la de Observaciones);
                            // al descartarlos, cada fila quedaba de una sola lÃ­nea y los grupos
                            // salÃ­an pegados unos con otros.
                            // Un campo de combinaciÃ³n sin rellenar llega como Â«NombreCampoÂ»
                            // (marcador de LibreOffice). En una columna residual de 3 pt ese
                            // texto no cabe y QuestPDF aborta el documento entero, asÃ­ que
                            // en celdas sin ancho Ãºtil se ignora: en un documento relleno esos
                            // marcadores ya no existen. Las marcas de regiÃ³n (Â«TableStart:â€¦Â»,
                            // Â«TableEnd:â€¦Â») SÃ se pintan aunque la columna sea estrecha: son lo
                            // Ãºnico que queda del renglÃ³n en el comparativo de terceros.
                            // Las marcas sÃ³lo se dejan pasar si la fila no declara alto
                            // (sin min-row-height ni row-height): con alto fijo, el texto
                            // envuelto en una columna de 3 pt choca con QuestPDF y aborta el
                            // documento entero (orden de compra, entrega, requisiciÃ³n).
                            var esMarcaRegionTexto = texto.Contains("TableStart:") || texto.Contains("TableEnd:");
                            var filaSinAlto = fila.AltoMinimoPt <= 0.5 && fila.AltoFijoPt <= 0.5;

                            var sinAnchoUtil = (anchoTexto < 20 && !esMarcaRegionTexto || anchoTexto < Math.Max(anchoMinGlifoMarca, 2.5) && esMarcaRegionTexto) &&
                                               texto.TrimStart().StartsWith("\u00AB", StringComparison.Ordinal) &&
                                               texto.TrimEnd().EndsWith("\u00BB", StringComparison.Ordinal);

                            // Marcador de regiÃ³n en una columna SIN ancho Ãºtil (la Ãºltima de la
                            // orden de compra mide 0,489 cm: 3 pt Ãºtiles). LibreOffice lo parte
                            // letra por letra â€”16 renglonesâ€” y la fila crece; QuestPDF aborta el
                            // documento si un glifo no cabe, asÃ­ que aquÃ­ sÃ³lo se reserva el
                            // alto medido contra el PDF de LibreOffice.
                            if (sinAnchoUtil && esMarcaRegionTexto && fila.AltoFijoPt <= 0.5 &&
                                _altoMarcadorSinAncho.TryGetValue(texto.Trim(), out var altoMedido))
                            {
                                cc.Item().Height((float)altoMedido);

                                if (ReferenceEquals(p, celda.Parrafos[^1])) ultimoVacio = true;

                                primero = false;
                                continue;
                            }

                            if (p.Runs.Count == 0 || string.IsNullOrWhiteSpace(texto) || sinAnchoUtil)
                            {
                                // alto del pÃ¡rrafo vacÃ­o = el fo:line-height declarado (9 pt al
                                // 100 %), NO el factor 1.95 de las celdas sin padding: ese
                                // factor es para el texto y aquÃ­ inflaba las filas de
                                // Observaciones 7 px mÃ¡s que LibreOffice.
                                // el pÃ¡rrafo vacÃ­o ocupa su lÃ­nea MÃS sus mÃ¡rgenes: la fila
                                // en blanco de "Fichas de depÃ³sito" declara 0.353cm abajo y sin
                                // ellos salÃ­a la mitad de alta que en LibreOffice
                                var altoVacio = (p.AltoLineaPt > 0 ? p.AltoLineaPt : 10) + p.MargenSupPt + p.MargenInfPt;
                                if (altoVacio > 0.5 && anchoTexto > 0.5 && (fila.AltoFijoPt <= 0.5 || esAltoFijoRecortado)) cc.Item().Height((float)altoVacio);

                                if (ReferenceEquals(p, celda.Parrafos[^1])) ultimoVacio = true;

                                primero = false;
                                continue;
                            }

                            // Sin espacio extra entre pÃ¡rrafos: MEDIDO, el paso en las celdas sin
                            // padding es simplemente 2x el tamaÃ±o de fuente (sellos 11.3 con 6 pt,
                            // cadena original 10.0 con 5 pt). El "extra" que habÃ­a antes compensaba
                            // un interlineado mal calculado; ahora sumarÃ­a de mÃ¡s.
                            // Los encabezados text:h dentro de celda con vertical-align="top"
                            // (p.ej. Heading_20_1 en Tabla2 de ComprobanteTraslado) sí respetan
                            // su fo:margin-top en Writer, alineando todas las cabeceras arriba.
                            var hueco = primero
                                ? (p.EsTitulo && celda.VerticalAlign == "top" ? p.MargenSupPt : 0)
                                : p.MargenSupPt;
                            if (hueco > 0.5) cc.Item().Height((float)hueco);

                            primero = false;

                            // La alineaciÃ³n va en el DESCRIPTOR de texto (t.AlignCenter()),
                            // no en el contenedor: en el contenedor no tenÃ­a efecto y el
                            // texto quedaba pegado a la izquierda de la celda.
                            var item = cc.Item().ExtendHorizontal();

                            // Un párrafo centrado con márgenes laterales negativos asimétricos
                            // dentro de celda (p.ej. firmas P24-P26/P37 en SC_PMoral) desplaza
                            // su eje de centrado en (margin-left - margin-right) / 2.
                            if (p.Alineacion == "center" && (p.MargenIzqPt < -1 || p.MargenDerPt < -1))
                            {
                                var desfaseCentro = (p.MargenIzqPt - p.MargenDerPt) / 2;
                                if (Math.Abs(desfaseCentro) > 0.5)
                                    item = item.OffsetX((float)desfaseCentro);
                            }

                            // El marcador TableEnd ahora se muestra en la columna estrecha.
                            // Mantener el alto de fila medido en LibreOffice evita que el
                            // bloque de firmas y el texto posterior suban al ocupar menos
                            // renglones que el marcador partido por Writer.
                            if (esMarcaRegionTexto && texto.Contains("TableEnd:", StringComparison.Ordinal) &&
                                _altoMarcadorSinAncho.TryGetValue(texto.Trim(), out var altoFilaMarcador))
                                item = item.MinHeight((float)altoFilaMarcador);

                            // Interlineado. En celdas SIN padding declarado LibreOffice usa ~2x el
                            // tamaÃ±o de fuente e ignora el fo:line-height del archivo (que ahÃ­ viene
                            // muy pequeÃ±o): medido, la cadena original va a 10.0 pt con texto de 5
                            // (factor 2.0) y los sellos a 11.3 con 6 (1.88). En celdas con padding
                            // sÃ­ respeta lo declarado.
                            // En las celdas el alto declarado manda mÃ¡s que en el flujo: el
                            // .fodt pide 0.459 cm (13 pt) para texto al 58 % (4,6 pt) y
                            // LibreOffice arma renglones de 12,6 pt. Con el tope de 1,95 el
                            // interlineado se descartaba y las filas de la tabla de
                            // Â«DetalleMercanciaÂ» quedaban pegadas (7,2 pt).
                            const double topeInterlineadoCelda = 3.0;

                            var altoLinea = AltoLineaCelda(p, celda);

                            // Los runs que son sÃ³lo espacios o marcadores no deben dictar el alto
                            // de la lÃ­nea: uno de 12 pt dentro de un texto de 5 pt inflaba su
                            // renglÃ³n y dejaba la lÃ­nea anterior pegada.
                            var tamPrincipal = p.Runs
                                .Where(x => !string.IsNullOrWhiteSpace(x.Texto))
                                .Select(x => x.TamanoPt)
                                .DefaultIfEmpty(0)
                                .Max();

                            // Cuando la celda declara un fo:line-height generoso (p.ej. 0.459 cm = 13 pt
                            // para texto de 5.5 pt en DetalleMercancia / FigurasTransporte), Writer coloca
                            // el interlineado extra arriba de la línea base (~88% arriba / ~12% abajo),
                            // mientras Skia reparte 50/50 (half-leading). El desplazamiento alinea la línea
                            // base con Writer sin cambiar el alto de la fila.
                            var esInterlineadoGeneroso = tamPrincipal > 0 && p.AltoLineaPt > tamPrincipal * 1.15 * 1.3;
                            if (esInterlineadoGeneroso &&
                                (string.IsNullOrEmpty(celda.VerticalAlign) || celda.VerticalAlign == "top") &&
                                p.AltoLineaPt / tamPrincipal <= topeInterlineadoCelda)
                            {
                                var desfaseLeading = (p.AltoLineaPt - tamPrincipal * 1.15) * 0.38;
                                if (desfaseLeading > 0.3)
                                    item = item.OffsetY((float)desfaseLeading);
                            }

                            item.DefaultTextStyle(EstiloDe(p.Runs, altoLinea, topeInterlineadoCelda))
                                .Text(t =>
                                {
                                    AlinearTexto(t, p.Alineacion);

                                    var huboTextoAntes = false;
                                    var huboEspacioAntes = false;
                                    foreach (var r in p.Runs)
                                    {
                                        var estiloRun = r;
                                        var textoRun = r.Texto;

                                        // Los marcadores de región («TableStart»/«TableEnd») en
                                        // columnas estrechas se parten carácter por carácter en
                                        // Writer. Si el padding se redujo para evitar error de
                                        // layout, se parte explícitamente al ancho real declarado
                                        // en vez de dejar que QuestPDF llene la caja ensanchada.
                                        var esRunMarca = textoRun.Contains("TableEnd:", StringComparison.Ordinal) ||
                                                         textoRun.Contains("TableStart:", StringComparison.Ordinal);
                                        if (esMarcaRegionTexto && anchoTexto < 20 && esRunMarca)
                                        {
                                            if (necesitaReducirPaddingMarca)
                                                textoRun = PartirMarcaPorAncho(textoRun, Math.Max(anchoTextoReal, 0.5),
                                                    string.IsNullOrEmpty(r.Fuente) ? "Arial" : r.Fuente,
                                                    r.TamanoPt > 0 ? r.TamanoPt : 6, r.Negrita);
                                            else
                                                textoRun = string.Join("\u200B", textoRun.Select(ch => ch.ToString()));
                                        }
                                        else if (esMarcaRegionTexto && esRunMarca && huboTextoAntes && huboEspacioAntes && anchoTexto >= 20)
                                        {
                                            // En Writer un token largo («TableEnd:Facturas») que no cabe
                                            // en la línea primero salta en el espacio previo; Skia lo
                                            // empieza en la misma línea y lo parte a mitad.
                                            var anchoMarca = PaginarPropio.Medir(textoRun,
                                                string.IsNullOrEmpty(r.Fuente) ? "Arial" : NormalizarFuente(r.Fuente),
                                                r.TamanoPt > 0 ? r.TamanoPt : 8, r.Negrita);
                                            if (anchoMarca > anchoTexto)
                                                textoRun = "\n" + textoRun;
                                        }

                                        if (!string.IsNullOrWhiteSpace(r.Texto))
                                        {
                                            huboTextoAntes = true;
                                            huboEspacioAntes = char.IsWhiteSpace(r.Texto[^1]);
                                        }
                                        else if (!string.IsNullOrEmpty(r.Texto))
                                        {
                                            huboEspacioAntes = true;
                                        }

                                        if (tamPrincipal > 0 && string.IsNullOrWhiteSpace(r.Texto) && r.TamanoPt != tamPrincipal)
                                            estiloRun = new RunModelo
                                            {
                                                Texto = r.Texto,
                                                Slot = r.Slot,
                                                Fuente = r.Fuente,
                                                TamanoPt = tamPrincipal,
                                                Negrita = r.Negrita,
                                                Cursiva = r.Cursiva,
                                                Subrayado = r.Subrayado,
                                                Color = r.Color,
                                                Hipervinculo = r.Hipervinculo,
                                            };

                                        var altoLineaRun = altoLinea;
                                        if (tamPrincipal > 0 && r.TamanoPt > 0 && r.TamanoPt < tamPrincipal && altoLineaRun > 0)
                                            altoLineaRun *= r.TamanoPt / tamPrincipal;

                                        var estiloCelda = EstiloDe(new List<RunModelo> { estiloRun }, altoLineaRun, topeInterlineadoCelda);

                                        if (!string.IsNullOrWhiteSpace(r.Hipervinculo))
                                            t.Hyperlink(textoRun, r.Hipervinculo).Style(estiloCelda);
                                        else
                                            t.Span(textoRun).Style(estiloCelda);
                                    }
                                });

                            // En LibreOffice Writer cada párrafo dentro de una celda respeta su
                            // fo:margin-bottom respecto al siguiente párrafo (p.ej. P24 "Adicionalmente
                            // recibí:" heredando 0.282cm de Standard antes de "208 VALES DESP.", o
                            // P19 "Creo/Autorizo/Recibio" antes de la línea de firma).
                            if (!ReferenceEquals(p, celda.Parrafos[^1]) && p.MargenInfPt > 0.5)
                                cc.Item().Height((float)p.MargenInfPt);
                        }

                        // Margen inferior del Ãºltimo pÃ¡rrafo de la celda: LibreOffice lo
                        // respeta dentro de la celda. El "Fichas de depÃ³sito" de la
                        // liquidaciones usa el estilo Standard (0.353cm) y su texto quedaba
                        // pegado a la lÃ­nea de abajo. Los estilos de celda de las facturas lo
                        // declaran en 0cm, asÃ­ que ahÃ­ no cambia nada.
                        var ultimoParrafoCelda = celda.Parrafos.LastOrDefault();

                        if (ultimoParrafoCelda != null && !ultimoVacio && ultimoParrafoCelda.MargenInfPt > 0.5)
                        {
                            cc.Item().Height((float)ultimoParrafoCelda.MargenInfPt);
                        }

                        // Las tablas anidadas van DESPUÃ‰S de los pÃ¡rrafos de la celda: en el
                        // .fodt van detrÃ¡s (en la orden de compra, "ORDEN DE COMPRA" y luego
                        // la tabla con NÃºmero de Documento / Fecha / PÃ¡gina). Pintarlas antes
                        // empujaba ese tÃ­tulo hacia abajo. En las FACTURAS estÃ¡n apagadas
                        // porque LibreOffice no las muestra (ver ADocumento).
                        //
                        // El ancho NO puede ir en 0 (columnas relativas): asÃ­ los anchos que
                        // declara el .fodt se repartÃ­an a ojo y "NÃºmero de Documento" partÃ­a
                        // en dos lÃ­neas. Se le pasa su propio ancho declarado para que use
                        // ConstantColumn con las medidas del archivo, como LibreOffice.
                        // El ancho que se le da es el de ESTA celda (menos su padding), que es
                        // el hueco real del que dispone.
                        if (_dibujarTablasAnidadas)
                            foreach (var anidada in celda.TablasAnidadas)
                            {
                                var t2 = anidada;

                                // RelativeColumn (ancho 0): con anchos fijos la tabla anidada
                                // revienta el layout (alguna columna queda mÃ¡s estrecha que su
                                // palabra). El reparto por proporciones declaradas sÃ­ renderiza.
                                var anchoAnidada = 0.0;

                                // sin compensaciÃ³n de margen: es del flujo, dentro de la celda
                                // metÃ­a un PaddingLeft grande y dejaba la tabla sin ancho
                                cc.Item().Element(e => AgregarTabla(e, t2, anchoAnidada, compensarMargen: false));
                            }
                    });
                    }

                    if (esAltoFijoRecortado)
                    {
                        var altoCajaFija = (float)Math.Max(altoFila - padSupCelda - padInfCelda, 0.5);
                        var anchoCajaFija = (float)anchoTexto;
                        cont.Height(altoCajaFija).CornerRadius(0.01f).Layers(capasFijo =>
                        {
                            capasFijo.PrimaryLayer().ExtendHorizontal().Height(altoCajaFija);
                            var capaCont = capasFijo.Layer()
                                .Unconstrained()
                                .Width(anchoCajaFija)
                                .MinHeight(altoCajaFija);
                            if (alinearMedioCelda) capaCont = capaCont.AlignMiddle();
                            else if (alinearFondoCelda) capaCont = capaCont.AlignBottom();
                            capaCont.Element(PintarContenidoCelda);
                        });
                    }
                    else if (celda.MarcosFlotantes.Count > 0)
                    {
                        var marcosCelda = celda.MarcosFlotantes;
                        cont.Layers(capasCelda =>
                        {
                            capasCelda.PrimaryLayer().Element(PintarContenidoCelda);

                            foreach (var marco in marcosCelda)
                            {
                                var capa = marco;
                                capasCelda.Layer()
                                    .Unconstrained()
                                    .OffsetX((float)capa.XPt)
                                    .OffsetY((float)capa.YPt)
                                    .Width((float)Math.Max(capa.AnchoPt, 1))
                                    .Height((float)Math.Max(capa.AltoPt, 1))
                                    .Element(caja => DibujarMarcoEnCelda(caja, capa));
                            }
                        });
                    }
                    else
                    {
                        PintarContenidoCelda(cont);
                    }
                }

                indiceFila++;
            }
        });
    }

    // "#a6a6a6" -> Color. Se parsea a mano porque Color.FromHex con el '#' puede lanzar
    // y entonces el catch dejaba la celda sin fondo.
    static Color ColorDe(string hex)
    {
        // sin color declarado, negro (comportamiento anterior)
        if (string.IsNullOrWhiteSpace(hex)) return Colors.Black;

        var h = hex.Trim().TrimStart('#');

        if (h.Length == 3) h = string.Concat(h.Select(c => new string(c, 2)));

        if (h.Length != 6 ||
            !byte.TryParse(h[..2], System.Globalization.NumberStyles.HexNumber, null, out var r) ||
            !byte.TryParse(h[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g) ||
            !byte.TryParse(h[4..], System.Globalization.NumberStyles.HexNumber, null, out var b))
            return Colors.Transparent;

        return Color.FromRGB(r, g, b);
    }

    // Sufijos de ESTILO que los conversores (.docx -> .fodt) meten dentro del nombre de
    // la fuente: "Calibri-Bold", "Arial-BoldMT", "Arial Bold"... Ninguno es una familia
    // real (la familia es "Calibri"/"Arial" y el estilo va aparte). Al pedirlos tal cual,
    // Skia devuelve en silencio la variante REGULAR: por eso el "Alimentos Soles S.A de
    // C.V." de la factura, que en el .fodt es T6 con Calibri-Bold, salia sin negritas.
    static readonly string[] SufijosDeEstilo =
    {
        "-BoldItalicMT", "-BoldItalic", "-BoldMT", "-ItalicMT", "-RegularMT",
        " Bold Italic", " Bold", " Italic", "-Bold", "-Italic", "-Oblique", "-Regular",
    };

    // "Arial1" -> "Arial", "'Arial,Bold'" -> "Arial", "Calibri-Bold" -> "Calibri",
    // "'Liberation Sans'" -> "Liberation Sans", "Times New Roman1" -> "Times New Roman"
    static string NormalizarFuente(string fuente)
    {
        if (string.IsNullOrWhiteSpace(fuente)) return "Arial";

        // algunas plantillas traen la familia entre comillas: fo:font-family="'Liberation Sans'"
        var limpio = fuente.Trim().Trim('\'', '"').Trim();

        // la coma separa alternativas de fuente ("Batang, ë°”íƒ•") o el estilo ("Arial,Bold");
        // la familia es siempre lo de la izquierda
        var coma = limpio.IndexOf(',');
        if (coma > 0) limpio = limpio[..coma].Trim();

        foreach (var sufijo in SufijosDeEstilo)
        {
            if (limpio.Length > sufijo.Length && limpio.EndsWith(sufijo, StringComparison.OrdinalIgnoreCase))
            {
                limpio = limpio[..^sufijo.Length].Trim().TrimEnd('-', '_', ' ');
                break;
            }
        }

        // el sufijo numÃ©rico que aÃ±ade LibreOffice al convertir fuentes
        while (limpio.Length > 1 && char.IsDigit(limpio[^1])) limpio = limpio[..^1];

        return string.IsNullOrWhiteSpace(limpio) ? "Arial" : limpio;
    }

    // Fuentes que las plantillas piden por su nombre "heredado" (el de familia del TTF)
    // pero que Skia sÃ³lo conoce como PESO de la familia base. MEDIDO: LibreOffice sÃ­ ve
    // "Bodoni MT Black" (nombre de familia de BOD_BLAR.TTF), pero SkiaSharp expone
    // "Bodoni MT" y su MatchFamily("Bodoni MT Black") devuelve nulo. Sin esto QuestPDF
    // caÃ­a a Arial: el "NOMBREEMPRESA" de las cartas de RH salÃ­a con otra tipografÃ­a.
    // SÃ³lo se separa el nombre cuando la familia tal cual NO existe, para no romper las
    // familias que de verdad se llaman asÃ­ ("Arial Black", "Arial Narrow"â€¦).
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Familia, int Peso)> _fuentesResueltas = new();

    static int PesoDe(string palabra) => palabra.ToLowerInvariant() switch
    {
        "thin" => 200,
        "light" => 300,
        "book" or "regular" => 400,
        "medium" => 500,
        "semibold" or "demibold" => 600,
        "bold" => 700,
        "extrabold" or "ultrabold" => 800,
        "black" or "heavy" => 900,
        _ => 0,
    };

    static (string Familia, int Peso) ResolverFuente(string fuente)
    {
        var limpio = NormalizarFuente(fuente);

        if (_fuentesResueltas.TryGetValue(limpio, out var cache)) return cache;

        var resuelta = (Familia: limpio, Peso: 0);

        try
        {
            // Open Sans no estÃ¡ instalada en Windows; LibreOffice la sustituye por
            // Segoe UI. QuestPDF, en cambio, caÃ­a a Arial. Se replica la sustituciÃ³n
            // sÃ³lo cuando Open Sans no existe y Segoe UI sÃ­.
            if (SkiaSharp.SKFontManager.Default.MatchFamily(limpio, SkiaSharp.SKFontStyle.Normal) == null &&
                string.Equals(limpio, "Open Sans", StringComparison.OrdinalIgnoreCase) &&
                SkiaSharp.SKFontManager.Default.MatchFamily("Segoe UI", SkiaSharp.SKFontStyle.Normal) != null)
            {
                limpio = "Segoe UI";
                resuelta = (limpio, 0);
            }

            // Aptos tampoco estÃ¡ instalada en este Windows. LibreOffice la sustituye por
            // Liberation Serif; sin esto QuestPDF elegÃ­a Cooper Black para la direcciÃ³n
            // del cliente ("780 Industrial Avenue").
            if (SkiaSharp.SKFontManager.Default.MatchFamily(limpio, SkiaSharp.SKFontStyle.Normal) == null &&
                string.Equals(limpio, "Aptos", StringComparison.OrdinalIgnoreCase) &&
                SkiaSharp.SKFontManager.Default.MatchFamily("Liberation Serif", SkiaSharp.SKFontStyle.Normal) != null)
            {
                limpio = "Liberation Serif";
                resuelta = (limpio, 0);
            }

            if (SkiaSharp.SKFontManager.Default.MatchFamily(limpio, SkiaSharp.SKFontStyle.Normal) == null)
            {
                // "Bodoni MT Black" -> familia "Bodoni MT" + peso 900
                var corte = limpio.LastIndexOf(' ');

                while (corte > 0)
                {
                    var familiaBase = limpio[..corte].Trim();
                    var peso = PesoDe(limpio[(corte + 1)..]);

                    if (peso > 0 && SkiaSharp.SKFontManager.Default.MatchFamily(familiaBase, SkiaSharp.SKFontStyle.Normal) != null)
                    {
                        resuelta = (familiaBase, peso);
                        break;
                    }

                    corte = familiaBase.LastIndexOf(' ');
                }
            }
        }
        catch { /* sin Skia se deja el nombre tal cual (QuestPDF harÃ¡ su respaldo) */ }

        _fuentesResueltas[limpio] = resuelta;
        return resuelta;
    }

    // QuestPDF aplica FontPosition.Superscript/Subscript reduciendo la letra al 60 % del
    // tamaÃ±o pedido. Es interno y no configurable: se compensa al fijar FontSize.
    const double EscalaSuperQuestPdf = 0.6;

    static TextStyle EstiloDe(List<RunModelo> runs, double altoLineaPt = 0, double factorInterlineadoMaximo = 1.95,
                              bool aplicarSuper = false)
    {
        // El estilo lo manda el run que TIENE TEXTO. Antes valÃ­a tambiÃ©n el primero que
        // sÃ³lo marcaba un bookmark, y como casi todos los datos van precedidos de
        // <text:bookmark-start>, el texto se pintaba con el estilo del bookmark (el del
        // pÃ¡rrafo, sin formato): el "Alimentos Soles S.A de C.V." de la factura, que va en
        // T6 (Calibri-Bold 11), salÃ­a en Calibri normal de 10.
        var r = runs.FirstOrDefault(x => !string.IsNullOrEmpty(x.Texto))
             ?? runs.FirstOrDefault(x => x.Slot != null)
             ?? new RunModelo();

        var tamano = r.TamanoPt <= 0 ? 9 : r.TamanoPt;

        // QuestPDF dibuja super/sub al 60 % del tamaÃ±o que se le pasa y ~33 % mÃ¡s arriba.
        // Para que el texto salga al tamaÃ±o que pide LibreOffice (58 % del declarado) se le
        // pasa el tamaÃ±o dividido por ese 0,6; el levantamiento queda en 0,33 x tamaÃ±o real.
        // El interlineado se sigue calculando con este tamaÃ±o: QuestPDF mide el alto de lÃ­nea
        // sobre el FontSize que se le pasa, y con el tamaÃ±o "visible" el renglÃ³n crecÃ­a ~8 pt.
        // sÃ³lo si de verdad se va a aplicar Superscript (ver mÃ¡s abajo): los runs de puros
        // espacios no se levantan, y si se compensara el tamaÃ±o saldrÃ­an al 166 %
        // Se dibuja al tamaÃ±o ya escalado (58 %) y SIN escapement salvo cuando el pÃ¡rrafo
        // mezcla texto en lÃ­nea base normal con superÃ­ndice (p.ej. "Complemento Carta Porte"
        // con "VersiÃ³n: 2.0..."): asÃ­ no infla las celdas de sÃ³lo superÃ­ndice ("UbicaciÃ³n")
        // pero sÃ­ eleva el superÃ­ndice respecto al texto base, igual que LibreOffice.
        var llevaSuper = aplicarSuper && r.Posicion == "super" && !string.IsNullOrWhiteSpace(r.Texto);

        if (llevaSuper) tamano /= EscalaSuperQuestPdf;

        // Los .fodt convertidos arrastran nombres de fuente con sufijo numÃ©rico ("Arial1",
        // "Times New Roman1"): QuestPDF 2026 los da por inexistentes y lanza
        // DocumentDrawingException. Se normaliza el nombre y se deja Arial como respaldo.
        // La familia se resuelve tambiÃ©n por PESO: hay plantillas que piden "Bodoni MT
        // Black" (nombre de familia del TTF) y SkiaSharp sÃ³lo conoce "Bodoni MT"; sin
        // esto QuestPDF caÃ­a a Arial y el encabezado de las cartas salÃ­a con otra letra.
        var (familiaFuente, pesoFuente) = ResolverFuente(r.Fuente);

        var estilo = TextStyle.Default
            .FontFamily(familiaFuente)
            .FontSize((float)tamano);

        // Interlineado: se respeta el fo:line-height del pÃ¡rrafo. Sin esto QuestPDF usa
        // 1.2 x tamaÃ±o y los bloques salen mÃ¡s cortos que en LibreOffice.
        // MEDIDO en el bloque de certificados/sellos: el .fodt declara ~0.62cm (17.6 pt)
        // para texto de 6 pt (factor 2.9), pero LibreOffice comprime la celda y usa 11.3 pt
        // (factor 1.88). Se acota a 1.95 para no separar las lÃ­neas de mÃ¡s.
        // QuestPDF interpreta LineHeight como mÃºltiplo del ALTO NATURAL de la fuente
        // (â‰ˆ1.05 x tamaÃ±o medido contra LibreOffice), no del tamaÃ±o: pasar el factor crudo
        // renglones ~2-3% mÃ¡s altos que LibreOffice y por eso los contratos cortaban
        // una o dos lÃ­neas antes en cada pÃ¡gina.
        if (altoLineaPt > 0)
        {
            // Writer nunca baja del alto natural de la fuente: ata el interlineado como
            // max(declarado, GetTextHeight()) (sw/source/core/text/itrtxt.cxx). Si no,
            // QuestPDF aprieta la caja y empuja la tinta hacia abajo dentro de ella.
                var altoEfectivo = Math.Max(altoLineaPt, tamano * 1.15);

            var factor = altoEfectivo / tamano / AltoNaturalFuente;
            if (factor > 0.6 && factor <= factorInterlineadoMaximo) estilo = estilo.LineHeight((float)factor);
        }

        // Los espacios no se levantan: el superÃ­ndice agranda la caja de la lÃ­nea (Skia sube
        // el glifo) y los renglones de Â«UbicaciÃ³nÂ» quedaban a 31 pt en vez de 13. Como el
        // espacio no se ve, da igual su lÃ­nea base.
        if (llevaSuper && r.Posicion == "super") estilo = estilo.Superscript();
        if (llevaSuper && r.Posicion == "sub") estilo = estilo.Subscript();

        if (r.Negrita) pesoFuente = Math.Max(pesoFuente, 700);

        // QuestPDF no tiene FontWeight(int): el peso se pide con estos ayudantes.
        estilo = pesoFuente switch
        {
            >= 900 => estilo.Black(),
            >= 800 => estilo.ExtraBold(),
            >= 700 => estilo.Bold(),
            >= 600 => estilo.SemiBold(),
            >= 500 => estilo.Medium(),
            >= 300 => estilo.Light(),
            >= 200 => estilo.Thin(),
            // Un run normal debe RESETEAR el peso: el DefaultTextStyle del pÃ¡rrafo de
            // una celda puede venir en negritas (la primera lÃ­nea "Acme" es Cooper
            // Black) y los spans siguientes heredaban esa negrita. AsÃ­ "Building 4"
            // salÃ­a como Segoe UI Black y la direcciÃ³n como una fuente Type3.
            _ => estilo.NormalWeight(),
        };
        estilo = estilo.Italic(r.Cursiva);
        if (r.Subrayado) estilo = estilo.Underline(true);

        try
        {
            if (!string.IsNullOrEmpty(r.Color) && r.Color.StartsWith("#") && r.Color.Length == 7)
                estilo = estilo.FontColor(Color.FromHex(r.Color));
            else
                estilo = estilo.FontColor(Colors.Black);
        }
        catch { }

        // Sin compensaciÃ³n de avance global: medido palabra por palabra contra LibreOffice
        // (574 palabras del contrato), la diferencia media es de sÃ³lo 0.044 pt (mediana
        // 0.02), pero al aplicarla como tracking (-0.0015 em) el contrato pasÃ³ de 3 a 1
        // pÃ¡ginas idÃ©nticas y el pagarÃ© dejÃ³ de cortar bien. La diferencia es por palabra
        // (kerning/avances), no un sesgo constante: se ajusta por plantilla cuando hace
        // falta (p. ej. el margen derecho del pÃ¡rrafo del pagarÃ©).

        return estilo;
    }
}





























