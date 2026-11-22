using System.Globalization;
using System.Xml.Linq;

namespace FodtToPdf;

// Lee un .fodt (ODF plano: XML sin comprimir) y construye el modelo intermedio.
internal static class ExtractorFodt
{
    static readonly XNamespace OFFICE = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    static readonly XNamespace STYLE = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    static readonly XNamespace TEXT = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    static readonly XNamespace TABLE = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    static readonly XNamespace DRAW = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    static readonly XNamespace FO = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    static readonly XNamespace SVG = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";
    static readonly XNamespace XLINK = "http://www.w3.org/1999/xlink";
    static readonly XNamespace CONFIG = "urn:oasis:names:tc:opendocument:xmlns:config:1.0";
    static readonly XNamespace LOEXT = "urn:org:documentfoundation:names:experimental:office:xmlns:loext:1.0";

    public const double CM_PT = 28.3464567;

    // estilos por nombre: propiedades de texto y de pÃ¡rrafo ya resueltas con herencia
    class Estilo
    {
        public string Fuente, Color, Alineacion;
        public double? TamanoPt;
        public bool? Negrita, Cursiva, Subrayado, Kerning;
        public double? EspaciadoCaracteresPt;
        public double? MargenIzqPt, MargenDerPt, MargenSupPt, MargenInfPt, SangriaPt, ColumnWidthCm;
        public string Padre;

        // style:contextual-spacing (null = no declarado; se hereda por la cadena)
        public bool? Contextual;

        // fo:orphans / fo:widows (null = no declarado; se hereda por la cadena)
        public int? Huerfanas, Viudas;

        // fo:keep-together / fo:keep-with-next en style:paragraph-properties
        public bool? MantenerParrafoJunto, MantenerConSiguiente;

        // style:min-row-height de un estilo de fila de tabla (null = no declarado)
        public double? AltoMinimoFilaPt;

        // style:row-height de un estilo de fila de tabla (null = no declarado)
        public double? AltoFijoFilaPt;

        // fo:keep-together="always" de un estilo de fila de tabla (null = no declarado)
        public bool? MantenerFilaJunta;


        // alto de línea (fo:line-height en cm o en %, o style:line-height-at-least en cm)
        public double? AltoLineaPt, AltoLineaPct, AltoLineaMinimoPt;

        // style:text-position (null = no declarado; se hereda por la cadena).
        // Posicion = "super" | "sub" sÃ³lo cuando el .fodt usa la palabra clave.
        public double? EscalaPosicion;
        public string Posicion;

        // celda: bordes declarados y paddings
        public bool BordeIzq, BordeDer, BordeSup, BordeInf;
        public string ColorBordeIzq, ColorBordeDer, ColorBordeSup, ColorBordeInf;
        public double GrosorBordePt;
        public double GrosorBordeIzqPt, GrosorBordeDerPt, GrosorBordeSupPt, GrosorBordeInfPt;
        public string VerticalAlign;
        public double PaddingIzqPt, PaddingDerPt, PaddingSupPt, PaddingInfPt;

        // base de las coordenadas de un marco/imagen (style:vertical-rel del estilo grÃ¡fico)
        public string VerticalRel, HorizontalRel;

        // draw:color-mode del estilo grÃ¡fico ("watermark" en las marcas de agua)
        public string ModoColor;

        // style:vertical-pos / horizontal-pos ("middle", "center", "from-top"...)
        public string VerticalPos, HorizontalPos;

        // style:wrap, style:run-through y fo:border del estilo gráfico
        public string Wrap;
        public string RunThrough;
        public bool GrafBorde;
        public string GrafColorBorde;
        public double GrafGrosorBordePt;
        public string ColorTrazo;
        public double GrosorTrazoPt;

        // style:tab-stops declaradas por el párrafo (posición en pt y alineación left/center/right);
        // null = no declara (LibreOffice usa paradas por omisión cada 1.25 cm)
        public List<TabStopModelo> Tabs;

        // color de fondo de la celda (fo:background-color); "transparent" o vacÃ­o = ninguno
        // en estilos grÃ¡ficos (family="graphic") guarda el draw:fill-color: es el relleno de
        // las draw:custom-shape, como la barra que va antes de "ORDEN DE COMPRA".
        public string ColorFondo;

        // pÃ¡rrafo: borde y fondo declarados en style:paragraph-properties (el encabezado
        // del contrato CCI va dentro de una caja con borde y relleno gris)
        public string SaltoPagina;
        public string PaginaMaestra;
        public bool ParBorde, ParSoloBordeInf;
        public string ParColorBorde, ParFondo;
        public double ParGrosorBordePt, ParPaddingSupPt, ParPaddingInfPt, ParPaddingIzqPt, ParPaddingDerPt;
    }

    public static ModeloPlantilla Extraer(string rutaFodt)
        => Extraer(XDocument.Load(rutaFodt, LoadOptions.PreserveWhitespace), Path.GetFileName(rutaFodt));

    // Sobrecarga para el controlador: el .fodt relleno llega en memoria, no en disco.
    public static ModeloPlantilla Extraer(byte[] archivoFodt, string nombre = "factura.fodt")
        => Extraer(XDocument.Load(new MemoryStream(archivoFodt), LoadOptions.PreserveWhitespace), nombre);

    // trazo de los estilos grÃ¡ficos (draw:style-name de los draw:line): grosor y color.
    // Los contratos firman con draw:line y sin esto los renglones no se pintaban.
    static readonly Dictionary<string, (double Grosor, string Color)> _trazo = new();

    static readonly Dictionary<string, Dictionary<int, (string Formato, string Plantilla, string Sufijo, string Vineta, double Margen, double Indent, int Inicio, string Fuente)>> _listas = new();
    static double _sangriaPendiente;
    static double _margenPendiente;
    static string _fuenteMarcaPendiente;
    // alto natural de una fuente respecto a su tamaÃ±o (ascendente + descendente +
    // interlineado interno). Arial: ~1.15 em; es la base del fo:line-height en %.
    const double AltoNaturalTexto = 1.15;

    // En PrawnSharp el interlineado se controla directamente en puntos reales (sin factor artificial).
    const double FactorInterlineadoQuestPdf = 1.0;

    internal static double NaturalDeFuente(string fuente)
    {
        if (string.IsNullOrWhiteSpace(fuente)) return AltoNaturalTexto;
        return SystemFontCatalog.NaturalHeightRatio(fuente);
    }

    internal static double ATwipsPt(double pt) => Math.Round(pt * 20.0, MidpointRounding.AwayFromZero) / 20.0;

    // fuente del primer run con texto (la que LibreOffice usa para el renglÃ³n)
    static string FuenteDelRun(ParrafoModelo p)
        => p.Runs.Select(x => x.Fuente).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

    // Caja de lÃ­nea de un pÃ¡rrafo VACÃO dentro de una celda: LibreOffice no tiene glifos
    // que posicionar y usa la lÃ­nea completa de la fuente. MEDIDO en las filas en blanco
    // de las liquidaciones (Calibri 11 pt al 115 %): 15,8 pt de lÃ­nea y fila de 25,8 con
    // el margen inferior de 0,353 cm del estilo Standard. Contrastado con mÃ¡s casos
    // (11 pt al 100 % -> 13,75; 9 pt al 115 % -> 12,94): el factor es 1,25 em, no 1,15.
    const double AltoNaturalFuenteCelda = 1.25;

    static readonly Dictionary<string, int> _contadoresLista = new();
    static string _marcaPendiente;

    static void LeerEstilosDeLista(XDocument doc)
    {
        _listas.Clear();
        _contadoresLista.Clear();

        foreach (var estiloLista in doc.Descendants(TEXT + "list-style"))
        {
            var niveles = new Dictionary<int, (string Formato, string Plantilla, string Sufijo, string Vineta, double Margen, double Indent, int Inicio, string Fuente)>();

            foreach (var nivel in estiloLista.Elements())
            {
                var num = int.TryParse((string)nivel.Attribute(TEXT + "level"), out var lv) ? lv : 1;

                var alineacionNivel = nivel.Descendants(STYLE + "list-level-label-alignment").FirstOrDefault();

                var margenNivel = APt((string)alineacionNivel?.Attribute(FO + "margin-left"));
                var indentNivel = APt((string)alineacionNivel?.Attribute(FO + "text-indent"));
                var inicioNivel = int.TryParse((string)nivel.Attribute(TEXT + "start-value"), out var sv) && sv > 0 ? sv : 1;
                var tpNivel = nivel.Element(STYLE + "text-properties");
                var fuenteNivel = (string)tpNivel?.Attribute(STYLE + "font-name")
                               ?? (string)tpNivel?.Attribute(FO + "font-family");

                if (nivel.Name == TEXT + "list-level-style-number")
                    niveles[num] = (
                        (string)nivel.Attribute(STYLE + "num-format") ?? "",
                        (string)nivel.Attribute(STYLE + "num-list-format") ?? "",
                        (string)nivel.Attribute(STYLE + "num-suffix") ?? ".",
                        null,
                        margenNivel,
                        indentNivel,
                        inicioNivel,
                        fuenteNivel);
                else if (nivel.Name == TEXT + "list-level-style-bullet")
                    niveles[num] = ("", "", "", (string)nivel.Attribute(TEXT + "bullet-char") ?? "•", margenNivel, indentNivel, 1, fuenteNivel);
            }

            _listas[((string)estiloLista.Attribute(STYLE + "name")) ?? ""] = niveles;
        }
    }

    static ModeloPlantilla Extraer(XDocument doc, string nombreArchivo)
    {
        var modelo = new ModeloPlantilla { Archivo = nombreArchivo };

        var estilos = new Dictionary<string, Estilo>();
        var estilosDeParrafo = new HashSet<string>();

        foreach (var cont in doc.Descendants().Where(e => e.Name == OFFICE + "styles" || e.Name == OFFICE + "automatic-styles"))
            foreach (var st in cont.Elements(STYLE + "style"))
            {
                var nombreEstilo = ((string)st.Attribute(STYLE + "name")) ?? "";

                estilos[nombreEstilo] = LeerEstilo(st);

                if (((string)st.Attribute(STYLE + "family")) == "paragraph")
                    estilosDeParrafo.Add(nombreEstilo);
            }

        // <style:default-style style:family="paragraph"> es el ÃšLTIMO eslabÃ³n de la cadena
        // de estilos de pÃ¡rrafo, y muchas plantillas dejan ahÃ­ el nombre de la fuente: la
        // orden de compra no lo declara en Standard ni en sus estilos, sÃ³lo en el default
        // (Calibri). Sin esto el texto salÃ­a en Arial y LibreOffice lo pone en Calibri.
        // SÃ³lo se enlazan los estilos de PÃRRAFO: los de texto (span) no heredan el default
        // de pÃ¡rrafo en ODF y colgarÃ­an su tamaÃ±o (10 pt) sobre el del pÃ¡rrafo.
        var estiloPorDefecto = doc.Descendants(STYLE + "default-style")
                                  .FirstOrDefault(x => ((string)x.Attribute(STYLE + "family")) == "paragraph");

        if (estiloPorDefecto != null)
        {
            const string nombrePorDefecto = "__estilo_por_defecto__";

            estilos[nombrePorDefecto] = LeerEstilo(estiloPorDefecto);

            foreach (var nombreEstilo in estilosDeParrafo)
                if (string.IsNullOrEmpty(estilos[nombreEstilo].Padre))
                    estilos[nombreEstilo].Padre = nombrePorDefecto;
        }

        _trazo.Clear();

        modelo.JustificarConEncogimiento = doc.Descendants(CONFIG + "config-item")
            .Any(e => (string)e.Attribute(CONFIG + "name") == "JustifyLinesWithShrinking"
                   && string.Equals(e.Value?.Trim(), "true", StringComparison.OrdinalIgnoreCase));

        modelo.TabsRelativosASangria = doc.Descendants(CONFIG + "config-item")
            .Any(e => (string)e.Attribute(CONFIG + "name") == "TabsRelativeToIndent"
                   && string.Equals(e.Value?.Trim(), "true", StringComparison.OrdinalIgnoreCase));

        LeerEstilosDeLista(doc);

        foreach (var st in doc.Descendants(STYLE + "style").Where(x => (string)x.Attribute(STYLE + "family") == "graphic"))
        {
            var props = st.Element(STYLE + "graphic-properties");
            if (props == null) continue;

            var grosor = APt((string)props.Attribute(SVG + "stroke-width"));
            var color = (string)props.Attribute(SVG + "stroke-color") ?? "#000000";

            _trazo[((string)st.Attribute(STYLE + "name")) ?? ""] = (grosor > 0 ? grosor : 0.75, color);
        }

        modelo.Pagina = LeerPagina(doc, estilos);

        // metadatos de secciones (nÃºmero de columnas y anchos relativos)
        var seccionesPorEstilo = new Dictionary<string, SeccionModelo>();
        foreach (var st in doc.Descendants(STYLE + "style").Where(e => (string)e.Attribute(STYLE + "family") == "section"))
        {
            var nombre = (string)st.Attribute(STYLE + "name") ?? "";
            seccionesPorEstilo[nombre] = LeerSeccion(st, nombre);
        }

        // Las tablas de los reportes se salen del Ã¡rea de texto con fo:margin-left negativo
        // (la bitÃ¡cora lo hace: -1.501cm). Se guarda el mayor para desplazar el flujo.
        foreach (var st in doc.Descendants(STYLE + "style").Where(e => (string)e.Attribute(STYLE + "family") == "table"))
        {
            var props = st.Element(STYLE + "table-properties");
            var margen = props?.Attribute(FO + "margin-left")?.Value is string ml ? APt(ml) : 0;

            // LibreOffice NO desplaza el flujo por un margen negativo pequeÃ±o (la tabla
            // estÃ¡ndar, -0.191 cm): sÃ³lo mueve la tabla. Aplicarlo movÃ­a todo el texto
            // ~5.8 pt a la izquierda y cambiaba los cortes de renglÃ³n (se notaba en el
            // justificado de las cartas de crÃ©dito). Se ignoran los menores de 0.5 cm.
            const double umbralDesplazamientoPt = 14.17;   // 0.5 cm
            if (margen <= -umbralDesplazamientoPt && margen < modelo.DesplazamientoIzqPt)
                modelo.DesplazamientoIzqPt = margen;
        }

        var cuerpo = doc.Descendants(OFFICE + "text").FirstOrDefault();
        if (cuerpo == null) return modelo;

        // 1) capas: cualquier draw:frame / draw:custom-shape, en cualquier nivel.
        //    Se guardan tambiÃ©n por elemento para saber despuÃ©s quÃ© pÃ¡rrafo las ancla.
        var capaPorElemento = new Dictionary<XElement, CapaModelo>();

        foreach (var el in cuerpo.Descendants().Where(e => e.Name == DRAW + "frame" || e.Name == DRAW + "custom-shape"))
        {
            var capa = LeerCapa(el, estilos);
            modelo.Capas.Add(capa);
            capaPorElemento[el] = capa;
        }

        // 2) flujo: pÃ¡rrafos y tablas en orden. El contenido del .fodt va dentro de
        //    <text:section> (y puede haber varias), asÃ­ que hay que descender.
        foreach (var hijo in cuerpo.Elements())
            RecorrerFlujo(hijo, modelo, estilos, seccionesPorEstilo, -1, capaPorElemento);

        modelo.Bookmarks = cuerpo.Descendants(TEXT + "bookmark-start")
            .Select(b => (string)b.Attribute(TEXT + "name"))
            .Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();

        // 3) Páginas maestras (style:master-page): cada una tiene su propio page-layout,
        //    encabezado y pie. Las propiedades de nivel superior de ModeloPlantilla reflejan
        //    la primera página maestra (retrocompatibilidad con documentos de 1 sola página maestra).
        LeerPaginasMaestras(doc, modelo, estilos);

        for (var i = 0; i < modelo.Flujo.Count; i++) modelo.Flujo[i].IndiceFlujo = i;

        // En LibreOffice Writer (SwCellFrame::Format), cuando un marco/imagen flotante
        // anclado tras una tabla sube con svg:y negativo y se solapa sobre ella (p.ej.
        // Imagen1 en la constancia de dividendos), las celdas con vertical-align="bottom"
        // quedan alineadas arriba.
        foreach (var capaSolape in modelo.Capas)
        {
            if (capaSolape.YPt < -0.5 && !capaSolape.EnFlujo && !capaSolape.AnclaEnTabla &&
                capaSolape.BloqueAncla > 0 && capaSolape.BloqueAncla < modelo.Flujo.Count &&
                modelo.Flujo[capaSolape.BloqueAncla - 1].Tipo == "tabla" &&
                modelo.Flujo[capaSolape.BloqueAncla - 1].Tabla != null)
            {
                modelo.Flujo[capaSolape.BloqueAncla - 1].Tabla.SolapadaPorFondo = true;
            }
        }

        return modelo;
    }

    static void LeerPaginasMaestras(XDocument doc, ModeloPlantilla modelo, Dictionary<string, Estilo> estilos)
    {
        var maestras = doc.Descendants(STYLE + "master-page").ToList();

        if (maestras.Count == 0)
        {
            var pie = doc.Descendants(STYLE + "footer").FirstOrDefault();
            var encabezado = doc.Descendants(STYLE + "header").FirstOrDefault();
            var fallback = new PaginaMaestraModelo
            {
                Nombre = "Standard",
                Pagina = modelo.Pagina,
            };
            LeerContenidoPie(pie, fallback, estilos);
            LeerContenidoEncabezado(encabezado, fallback, estilos);
            CopiarMaestraAModelo(fallback, modelo);
            return;
        }

        foreach (var master in maestras)
        {
            var nombre = (string)master.Attribute(STYLE + "name") ?? "";
            var nombreLayout = (string)master.Attribute(STYLE + "page-layout-name") ?? "";
            var layout = doc.Descendants(STYLE + "page-layout")
                            .FirstOrDefault(pl => (string)pl.Attribute(STYLE + "name") == nombreLayout);

            var encabezado = master.Element(STYLE + "header");
            var pie = master.Element(STYLE + "footer");

            var pm = new PaginaMaestraModelo
            {
                Nombre = nombre,
                Pagina = LeerPaginaDeLayout(layout, encabezado, doc, estilos),
            };

            LeerContenidoPie(pie, pm, estilos);
            LeerContenidoEncabezado(encabezado, pm, estilos);

            if (!string.IsNullOrEmpty(nombre))
                modelo.PaginasMaestras[nombre] = pm;

            if (string.IsNullOrEmpty(modelo.PaginaMaestraInicial))
            {
                modelo.PaginaMaestraInicial = nombre;
                CopiarMaestraAModelo(pm, modelo);
            }
        }
    }

    static void CopiarMaestraAModelo(PaginaMaestraModelo pm, ModeloPlantilla modelo)
    {
        modelo.Pagina = pm.Pagina;
        modelo.Encabezado = pm.Encabezado;
        modelo.Pie = pm.Pie;
        modelo.MarcasAgua = pm.MarcasAgua;
        modelo.NumerosPagina = pm.NumerosPagina;
        modelo.MarcasAguaTexto = pm.MarcasAguaTexto;
    }

    static void LeerContenidoPie(XElement pie, PaginaMaestraModelo destino, Dictionary<string, Estilo> estilos)
    {
        if (pie == null) return;

        var parrafosPie = new List<ParrafoModelo>();
        var hayTextoEnPie = false;

        foreach (var p in pie.Elements(TEXT + "p"))
        {
            var parrafo = LeerParrafo(p, estilos);
            parrafosPie.Add(parrafo);
            if (parrafo.Runs.Count > 0 && parrafo.Runs.Any(r => !string.IsNullOrWhiteSpace(r.Texto) || r.Slot != null || r.CampoDinamico != null))
                hayTextoEnPie = true;
        }

        // Igual que en el encabezado: si el pie contiene texto, los párrafos vacíos
        // posteriores/espaciadores también ocupan su altura real en Writer (p.ej. CIT18).
        if (hayTextoEnPie)
            destino.Pie.AddRange(parrafosPie);

        foreach (var marco in pie.Descendants(DRAW + "frame"))
        {
            if (marco.Descendants(TEXT + "page-number").Any())
            {
                var numero = LeerCapa(marco, estilos);
                numero.EsNumeroPaginaActual = true;
                numero.EnPie = true;
                if (numero.AltoPt <= 0) numero.AltoPt = 14.4;
                destino.NumerosPagina.Add(numero);
            }
        }
    }

    static void LeerContenidoEncabezado(XElement encabezado, PaginaMaestraModelo destino, Dictionary<string, Estilo> estilos)
    {
        if (encabezado == null) return;

        foreach (var p in encabezado.Elements(TEXT + "p"))
        {
            var parrafo = LeerParrafo(p, estilos);
            // Los párrafos vacíos del encabezado también ocupan su altura en Writer.
            // Algunos (como P2 de la carta de guardería) separan el nombre del domicilio.
            destino.Encabezado.Add(parrafo);
        }

        // marcos con imagen del encabezado (la marca de agua de la factura)
        foreach (var marco in encabezado.Descendants(DRAW + "frame"))
        {
            var imagen = marco.Element(DRAW + "image");
            var datos = imagen?.Element(OFFICE + "binary-data")?.Value;
            if (imagen == null || string.IsNullOrWhiteSpace(datos))
            {
                // Los números de página del formato de entrevista viven en un
                // draw:frame del encabezado, no como texto directo del master-page.
                // El valor del XML ("1") es sólo una muestra: el generador escribe
                // el número actual en cada hoja.
                if (marco.Descendants(TEXT + "page-number").Any() && destino.NumerosPagina.Count == 0)
                {
                    var numero = LeerCapa(marco, estilos);
                    numero.EsNumeroPaginaActual = true;
                    if (numero.AltoPt <= 0) numero.AltoPt = 14.4;
                    destino.NumerosPagina.Add(numero);
                }

                continue;
            }

            var capa = LeerCapa(marco, estilos);
            capa.ImagenBase64 = datos.Trim();
            capa.ImagenTipo = (string)imagen.Attribute(DRAW + "mime-type") ?? "image/png";
            destino.MarcasAgua.Add(capa);
        }

        // marcas de agua de TEXTO: draw:custom-shape con draw:text-path="true"
        // («COPIA NO VALIDA» de la orden de compra inválida). LibreOffice estira el texto
        // hasta llenar la forma, así que se guarda la caja y el color del relleno.
        foreach (var forma in encabezado.Descendants(DRAW + "custom-shape"))
        {
            // draw:text-path va en el <draw:enhanced-geometry> de dentro, no en la forma
            var geometria = forma.Element(DRAW + "enhanced-geometry");

            if (geometria == null || ((string)geometria.Attribute(DRAW + "text-path")) != "true") continue;

            var texto = string.Concat(forma.Elements(TEXT + "p")
                                           .Select(x => string.Concat(x.Nodes().OfType<XText>().Select(t => t.Value)))).Trim();

            if (texto.Length == 0) continue;

            var estiloForma = (string)forma.Attribute(DRAW + "style-name") ?? "";
            var estiloTexto = (string)forma.Elements(TEXT + "p").FirstOrDefault()?.Attribute(TEXT + "style-name") ?? "";
            estilos.TryGetValue(estiloForma, out var ef);

            destino.MarcasAguaTexto.Add(new MarcaAguaTextoModelo
            {
                Texto = texto,
                XPt = APt((string)forma.Attribute(SVG + "x")),
                YPt = APt((string)forma.Attribute(SVG + "y")),
                AnchoPt = APt((string)forma.Attribute(SVG + "width")),
                AltoPt = APt((string)forma.Attribute(SVG + "height")),
                Color = ef?.ColorFondo,
                Fuente = CadenaTexto(estilos, estiloTexto, e => e.Fuente),
                VerticalPos = ef?.VerticalPos ?? "",
                HorizontalPos = ef?.HorizontalPos ?? "",
                VerticalRel = ef?.VerticalRel ?? "",
                HorizontalRel = ef?.HorizontalRel ?? "",
            });
        }
    }

    // ancho reservado al marcador: el fo:text-indent negativo del nivel (0.635 cm tÃ­pico).
    // En las listas con fo:margin-left grande (las obligaciones: 6.985 cm) el marcador va
    // pegado al texto, no al margen de la hoja.
    static double AnchoDeMarca(string estilo, int nivel)
    {
        if (!_listas.TryGetValue(estilo, out var niveles)) return 0;
        if (!niveles.TryGetValue(nivel, out var def)) return 0;

        return def.Indent < -0.5 ? -def.Indent : 0;
    }

    // sangrÃ­a del bloque: fo:margin-left + fo:text-indent (el renglÃ³n sangrado arranca ahÃ­)
    static double MargenDeInciso(string estilo, int nivel)
    {
        if (!_listas.TryGetValue(estilo, out var niveles)) return 0;
        if (!niveles.TryGetValue(nivel, out var def)) return 0;

        return Math.Max(def.Margen + def.Indent, 0);
    }

    static int InicioDeLista(string estilo, int nivel)
    {
        if (!_listas.TryGetValue(estilo, out var niveles)) return 1;
        if (!niveles.TryGetValue(nivel, out var def)) return 1;

        return def.Inicio > 0 ? def.Inicio : 1;
    }

    static string FuenteDeLista(string estilo, int nivel)
    {
        if (!_listas.TryGetValue(estilo, out var niveles)) return null;
        if (!niveles.TryGetValue(nivel, out var def)) return null;
        return def.Fuente;
    }

    static string MarcaDeLista(string estilo, int nivel, int numero)
    {
        if (!_listas.TryGetValue(estilo, out var niveles)) return null;
        if (!niveles.TryGetValue(nivel, out var def)) return null;

        if (!string.IsNullOrEmpty(def.Vineta))
        {
            var vineta = LimpiarGlifos(def.Vineta);

            return string.IsNullOrEmpty(vineta) ? null : vineta;
        }

        var texto = FormatearNumero(def.Formato, numero);

        if (string.IsNullOrEmpty(texto)) return null;

        if (!string.IsNullOrEmpty(def.Plantilla))
            return def.Plantilla.Replace("%1%", texto).Replace("%" + nivel + "%", texto);

        return texto + def.Sufijo;
    }

    static string FormatearNumero(string formato, int numero)
    {
        if (formato == "A" || formato == "a")
        {
            var letra = (char)((int)Char.Parse("A") + ((numero - 1) % 26));

            return formato == "a" ? char.ToLowerInvariant(letra).ToString() : letra.ToString();
        }

        if (formato == "I" || formato == "i")
        {
            var romano = Romano(numero);

            return formato == "i" ? romano.ToLowerInvariant() : romano;
        }

        if (string.IsNullOrEmpty(formato)) return null;

        return numero.ToString(CultureInfo.InvariantCulture);
    }

    static string Romano(int numero)
    {
        int[] valores = { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
        string[] letras = { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };

        var sb = new System.Text.StringBuilder();

        for (var i = 0; i < valores.Length && numero > 0; i++)
        {
            while (numero >= valores[i])
            {
                sb.Append(letras[i]);
                numero -= valores[i];
            }
        }

        return sb.ToString();
    }

    static void RecorrerFlujo(XElement el, ModeloPlantilla modelo, Dictionary<string, Estilo> estilos,
                              Dictionary<string, SeccionModelo> seccionesPorEstilo, int seccion,
                              Dictionary<XElement, CapaModelo> capaPorElemento)
    {
        if (el.Name == TEXT + "p" || el.Name == TEXT + "h")
        {
            // en una secciÃ³n de varias columnas los pÃ¡rrafos vacÃ­os son los espaciadores
            // que el autor puso a mano; su alto debe salir del tamaÃ±o de su estilo
            var enColumnas = seccion >= 0 && seccion < modelo.Secciones.Count &&
                             modelo.Secciones[seccion].Columnas > 1;

            var p = LeerParrafo(el, estilos, enColumnas: enColumnas);

            if (!string.IsNullOrEmpty(_marcaPendiente))
            {
                // la marca hereda el formato del primer run: en los contratos el inciso
                // ("A) PRIMERA") va en negritas, igual que el texto que encabeza
                var primerRun = p.Runs.FirstOrDefault();

                var runMarca = primerRun == null
                    ? new RunModelo { Texto = LimpiarGlifos(_marcaPendiente), TamanoPt = p.TamanoBasePt }
                    : new RunModelo
                    {
                        Texto = LimpiarGlifos(_marcaPendiente) + " ",
                        Fuente = primerRun.Fuente,
                        TamanoPt = primerRun.TamanoPt,
                        Negrita = primerRun.Negrita,
                        Cursiva = primerRun.Cursiva,
                        Subrayado = primerRun.Subrayado,
                        Color = primerRun.Color,
                    };

                p.Runs.Insert(0, runMarca);
                if (!string.IsNullOrEmpty(_fuenteMarcaPendiente))
                {
                    var tamMarca = runMarca.TamanoPt > 0 ? runMarca.TamanoPt : (p.TamanoBasePt > 0 ? p.TamanoBasePt : 10);
                    var pctMarca = p.AltoLineaPct > 0 ? p.AltoLineaPct : 1.0;
                    var altoMarca = ATwipsPt(tamMarca * NaturalDeFuente(_fuenteMarcaPendiente) * pctMarca);
                    if (altoMarca > p.AltoLineaPt) p.AltoMarcaPt = altoMarca;
                }
                // ODF 1.3 / Writer (SwTextNode::GetLeftMarginWithNum): si el estilo del
                // párrafo declara su propia sangría francesa (fo:margin-left + fo:text-indent < 0),
                // prevalece sobre la geometría por omisión del text:list-style.
                if (p.SangriaPt < -0.5 && p.MargenIzqPt > 0.5)
                {
                    p.SangriaColgantePt = Math.Min(-p.SangriaPt, p.MargenIzqPt);
                    p.MargenIncisoPt = Math.Max(p.MargenIzqPt + p.SangriaPt, 0);
                }
                else
                {
                    p.SangriaColgantePt = _sangriaPendiente;
                    p.MargenIncisoPt = _margenPendiente;
                }

                _sangriaPendiente = 0;
                _margenPendiente = 0;

                _marcaPendiente = null;
            }

            if (p.Runs.Count > 0 && p.Runs.Any(r => !string.IsNullOrWhiteSpace(r.Texto) || r.Slot != null))
            {
                modelo.Flujo.Add(new BloqueModelo { Tipo = "texto", Parrafo = p, Seccion = seccion });
            }
            else if ((!string.IsNullOrWhiteSpace(p.ImagenBase64) && p.ImagenEnFlujo) || p.Lineas.Count > 0)
            {
                // Párrafo que sólo lleva una imagen anclada "as-char" (p.ej. QR de orden de compra)
                // o líneas gráficas (<draw:line> de firmas en CIT18). Se manda como bloque de texto
                // para que el generador pinte la imagen o las líneas en su coordenada exacta.
                modelo.Flujo.Add(new BloqueModelo { Tipo = "texto", Parrafo = p, Seccion = seccion });
            }
            else
            {
                // Los pÃ¡rrafos vacÃ­os SON el espacio vertical del documento: si se
                // descartan, todo el flujo se colapsa contra el margen superior.
                // El alto sale del fo:line-height real del estilo, no de una estimaciÃ³n.
                modelo.Flujo.Add(new BloqueModelo
                {
                    Tipo = "espacio",
                    AltoPt = p.AltoLineaPt,
                    Parrafo = p,
                    Seccion = seccion,
                });
            }

            // Las capas ancladas a un carÃ¡cter DE ESTE pÃ¡rrafo traen svg:x/svg:y relativos
            // a esa posiciÃ³n, no a la pÃ¡gina: se anota quÃ© bloque las ancla para que el
            // generador les sume dÃ³nde cae el flujo (ver GeneradorPdf.AnclarAlFlujo).
            var posicion = modelo.Flujo.Count - 1;

            foreach (var dibujo in el.Elements().Where(x => x.Name == DRAW + "frame" || x.Name == DRAW + "custom-shape")
                .Concat(el.Elements(DRAW + "g").Descendants().Where(x => x.Name == DRAW + "frame" || x.Name == DRAW + "custom-shape")))
                if (capaPorElemento != null && capaPorElemento.TryGetValue(dibujo, out var anclada))
                    anclada.BloqueAncla = posicion;

        }
        else if (el.Name == TABLE + "table")
        {
            modelo.Flujo.Add(new BloqueModelo { Tipo = "tabla", Tabla = LeerTabla(el, estilos), Seccion = seccion });

            // Marcos anclados a un pÃ¡rrafo de ESTA tabla (la liquidaciones pone ahÃ­ el
            // bloque de firmas): su y es relativo al pÃ¡rrafo ancla, que no sabemos dÃ³nde
            // cae; se dibujan en el flujo justo despuÃ©s de la tabla.
            var posicionTabla = modelo.Flujo.Count - 1;

            foreach (var marco in el.Descendants(DRAW + "frame"))
            {
                var anclaMarco = (string)marco.Attribute(TEXT + "anchor-type") ?? "";
                if (anclaMarco == "char")
                {
                    if (capaPorElemento != null && capaPorElemento.TryGetValue(marco, out var capaChar))
                        capaChar.EnFlujo = true;
                    continue;
                }
                if (anclaMarco != "paragraph" && anclaMarco != "") continue;

                if (capaPorElemento != null && capaPorElemento.TryGetValue(marco, out var capaAnclada))
                {
                    capaAnclada.BloqueAncla = posicionTabla;
                    capaAnclada.AnclaEnTabla = true;

                    // el svg:y se cuenta desde el pÃ¡rrafo ancla, no desde el fin de la tabla
                    var parrafoAncla = marco.Ancestors(TEXT + "p").FirstOrDefault();

                    if (parrafoAncla != null)
                        capaAnclada.AltoParrafoAnclaPt = AltoParrafoAncla(parrafoAncla, estilos);
                }
            }
        }
        else if (el.Name == TEXT + "section")
        {
            // la secciÃ³n define cuÃ¡ntas columnas tiene el contenido que la sigue
            var nombreEstilo = (string)el.Attribute(TEXT + "style-name") ?? "";
            var meta = seccionesPorEstilo.TryGetValue(nombreEstilo, out var s)
                ? s
                : new SeccionModelo { Nombre = (string)el.Attribute(TEXT + "name") ?? nombreEstilo };

            meta.Nombre = (string)el.Attribute(TEXT + "name") ?? meta.Nombre;
            modelo.Secciones.Add(meta);

            var indice = modelo.Secciones.Count - 1;
            foreach (var hijo in el.Elements())
                RecorrerFlujo(hijo, modelo, estilos, seccionesPorEstilo, indice, capaPorElemento);
        }
        else if (el.Name == TEXT + "list")
        {
            var estiloDeLista = (string)el.Attribute(TEXT + "style-name") ?? "";
            var continuar = (string)el.Attribute(TEXT + "continue-numbering") == "true";

            if (!continuar || !_contadoresLista.TryGetValue(estiloDeLista, out var contador))
                contador = InicioDeLista(estiloDeLista, 1) - 1;

            foreach (var elemento in el.Elements(TEXT + "list-item"))
            {
                if (int.TryParse((string)elemento.Attribute(TEXT + "start-value"), out var inicioItem) && inicioItem > 0)
                    contador = inicioItem;
                else
                    contador++;

                _marcaPendiente = MarcaDeLista(estiloDeLista, 1, contador);
                _sangriaPendiente = AnchoDeMarca(estiloDeLista, 1);
                _margenPendiente = MargenDeInciso(estiloDeLista, 1);
                _fuenteMarcaPendiente = FuenteDeLista(estiloDeLista, 1);

                foreach (var hijo in elemento.Elements())
                    RecorrerFlujo(hijo, modelo, estilos, seccionesPorEstilo, seccion, capaPorElemento);

                _marcaPendiente = null;
                _sangriaPendiente = 0;
                _fuenteMarcaPendiente = null;
            }

            _contadoresLista[estiloDeLista] = contador;
        }
        else if (el.Name == TEXT + "tracked-changes" || el.Name == TEXT + "list-item")
        {
            foreach (var hijo in el.Elements())
                RecorrerFlujo(hijo, modelo, estilos, seccionesPorEstilo, seccion, capaPorElemento);
        }
    }

    // Lee la definiciÃ³n de columnas de una section: cuÃ¡ntas y con quÃ© ancho relativo
    static SeccionModelo LeerSeccion(XElement estilo, string nombre)
    {
        var sec = new SeccionModelo { Nombre = nombre };
        var cols = estilo.Descendants(STYLE + "columns").FirstOrDefault();

        if (cols == null) return sec;

        sec.Columnas = int.TryParse((string)cols.Attribute(FO + "column-count"), out var n) ? n : 1;

        var gap = cols.Attribute(FO + "column-gap")?.Value is string g ? APt(g) : 0;

        foreach (var col in cols.Elements(STYLE + "column"))
        {
            var rel = (string)col.Attribute(STYLE + "rel-width") ?? "";
            sec.RelAnchos.Add(double.TryParse(rel.TrimEnd('*'), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : 0);
            sec.SeparacionPt.Add(gap);
            sec.MargenIzqColPt.Add(col.Attribute(FO + "start-indent")?.Value is string si ? APt(si) : 0);
            sec.MargenDerColPt.Add(col.Attribute(FO + "end-indent")?.Value is string ei ? APt(ei) : 0);
        }

        if (sec.RelAnchos.Count == 0 && sec.Columnas > 1)
            for (var i = 0; i < sec.Columnas; i++) sec.RelAnchos.Add(1);

        return sec;
    }

    // Alto del TEXTO que lleva el encabezado del master-page. NO cuenta los marcos de
    // imagen: la marca de agua de la factura mide 16 cm y LibreOffice no la usa para
    // separar el cuerpo. 0 si el encabezado no existe o no tiene párrafos.
    static double AltoTextoEncabezado(XElement encabezado, Dictionary<string, Estilo> estilos)
    {
        if (encabezado == null) return 0;

        var parrafos = encabezado.Elements(TEXT + "p")
            .Select(p => LeerParrafo(p, estilos))
            .ToList();

        var alto = 0.0;
        for (var i = 0; i < parrafos.Count; i++)
        {
            var cur = parrafos[i];
            var prev = i > 0 ? parrafos[i - 1] : null;
            var sig = i + 1 < parrafos.Count ? parrafos[i + 1] : null;

            var mismoPrev = prev != null && cur.Contextual && prev.Contextual &&
                !string.IsNullOrEmpty(cur.EstiloComun) &&
                string.Equals(cur.EstiloComun, prev.EstiloComun, StringComparison.Ordinal);
            var mismoSig = sig != null && cur.Contextual && sig.Contextual &&
                !string.IsNullOrEmpty(cur.EstiloComun) &&
                string.Equals(cur.EstiloComun, sig.EstiloComun, StringComparison.Ordinal);

            var mSup = mismoPrev ? 0 : Math.Max(cur.MargenSupPt, 0);
            var mInf = mismoSig ? 0 : Math.Max(cur.MargenInfPt, 0);

            // Usar el alto de línea real calculado por LeerParrafo (que considera los runs/spans
            // como T1/T2 en 8pt/10pt y no el tamaño por omisión de 12pt heredado de Header/Standard).
            alto += mSup + cur.AltoLineaPt + mInf;
        }

        return alto;
    }

    static PaginaModelo LeerPaginaDeLayout(XElement layout, XElement encabezado, XDocument doc, Dictionary<string, Estilo> estilos)
    {
        var pg = new PaginaModelo();
        var props = layout?.Element(STYLE + "page-layout-properties");

        if (props == null || !props.HasAttributes)
        {
            var declaradas = doc.Descendants(STYLE + "page-layout-properties")
                                 .Where(p => p.HasAttributes).ToList();

            if (declaradas.Count == 0) return pg;

            props = new XElement(STYLE + "page-layout-properties",
                declaradas.SelectMany(p => p.Attributes())
                          .GroupBy(a => a.Name)
                          .Select(g => g.First()));
        }

        double? Leer(XName a) => props.Attribute(a)?.Value is string v ? APt(v) : (double?)null;

        pg.AnchoPt = Leer(FO + "page-width") ?? pg.AnchoPt;
        pg.AltoPt = Leer(FO + "page-height") ?? pg.AltoPt;
        pg.MargenIzqPt = Leer(FO + "margin-left") ?? pg.MargenIzqPt;
        pg.MargenDerPt = Leer(FO + "margin-right") ?? pg.MargenDerPt;
        pg.MargenSupPt = Leer(FO + "margin-top") ?? pg.MargenSupPt;
        pg.MargenInfPt = Leer(FO + "margin-bottom") ?? pg.MargenInfPt;

        pg.MargenSupBasePt = pg.MargenSupPt;
        pg.MargenInfBasePt = pg.MargenInfPt;

        // Lee SOLO el header-style / footer-style de ESTE page-layout (no de todo el
        // documento: en plantillas con varias páginas maestras como CCI, tomar el primer
        // footer-style global le robaba 0.73 cm de margen inferior a pm1 desde pm3).
        var header = layout != null
            ? layout.Elements(STYLE + "header-style").Elements(STYLE + "header-footer-properties").FirstOrDefault()
            : doc.Descendants(STYLE + "header-style").Elements(STYLE + "header-footer-properties").FirstOrDefault();

        if (header != null)
        {
            var minimo = header.Attribute(FO + "min-height")?.Value is string h ? APt(h) : 0;
            var alto = Math.Max(minimo, AltoTextoEncabezado(encabezado, estilos));

            var dinamico = ((string)header.Attribute(STYLE + "dynamic-spacing")) == "true";
            var separacion = dinamico ? 0 : header.Attribute(FO + "margin-bottom")?.Value is string mb ? APt(mb) : 0;

            pg.MargenSupBasePt = pg.MargenSupPt;
            pg.AltoEncabezadoPt = alto;
            pg.MargenSupPt += alto + separacion;
        }

        var footer = layout != null
            ? layout.Elements(STYLE + "footer-style").Elements(STYLE + "header-footer-properties").FirstOrDefault()
            : doc.Descendants(STYLE + "footer-style").Elements(STYLE + "header-footer-properties").FirstOrDefault();

        if (footer != null)
        {
            var alto = footer.Attribute(FO + "min-height")?.Value is string h ? APt(h) : 0;
            var dinamico = ((string)footer.Attribute(STYLE + "dynamic-spacing")) == "true";
            var separacion = dinamico ? 0 : footer.Attribute(FO + "margin-top")?.Value is string mt ? APt(mt) : 0;

            pg.MargenInfBasePt = pg.MargenInfPt;
            pg.AltoPiePt = alto;
            pg.MargenInfPt += alto + separacion;
        }

        return pg;
    }

    static PaginaModelo LeerPagina(XDocument doc, Dictionary<string, Estilo> estilos)
    {
        var master = doc.Descendants(STYLE + "master-page").FirstOrDefault();
        var nombreLayout = (string)master?.Attribute(STYLE + "page-layout-name");
        var layout = !string.IsNullOrEmpty(nombreLayout)
            ? doc.Descendants(STYLE + "page-layout").FirstOrDefault(pl => (string)pl.Attribute(STYLE + "name") == nombreLayout)
            : null;
        var encabezado = master?.Element(STYLE + "header") ?? doc.Descendants(STYLE + "header").FirstOrDefault();

        return LeerPaginaDeLayout(layout, encabezado, doc, estilos);
    }

    // style:text-position: "super 58%", "sub 58%" o "-6% 100%". El primer valor es el
    // desplazamiento de la lÃ­nea base (en % del tamaÃ±o) y el segundo, opcional, el tamaÃ±o
    // relativo. "super" equivale a "33% 58%" y "sub" a "-33% 58%" (DFLT_ESC_AUTO_SUPER /
    // DFLT_ESC_AUTO_SUB de LibreOffice); sin porcentaje, el tamaÃ±o relativo es 58 %.
    static (double Escala, string Posicion)? PosicionTexto(string valor)
    {
        if (string.IsNullOrWhiteSpace(valor)) return null;

        var partes = valor.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (partes.Length == 0) return null;

        static double? Pct(string t)
            => t != null && t.EndsWith("%") && double.TryParse(t[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? v : (double?)null;

        switch (partes[0])
        {
            case "super":
                return (Pct(partes.Length > 1 ? partes[1] : null) ?? 58, "super");
            case "sub":
                return (Pct(partes.Length > 1 ? partes[1] : null) ?? 58, "sub");
        }

        if (Pct(partes[0]) == null) return null;

        // los 370 "-6% 100%" de los contratos caen aquÃ­: escala 100 % y sÃ³lo se mueve la
        // lÃ­nea base 6 %, un desplazamiento que QuestPDF no sabe expresar (mÃ­nimo ~33 %).
        return (partes.Length > 1 ? Pct(partes[1]) ?? 100 : 100, null);
    }

    // Alto que ocupa el pÃ¡rrafo que ancla un marco dibujado en el flujo tras su tabla
    // (las firmas de la liquidaciones). Es el mismo cÃ¡lculo que la celda hace para un
    // pÃ¡rrafo VACÃO: lÃ­nea del estilo + sus mÃ¡rgenes. Si el pÃ¡rrafo trae texto se
    // aproxima igual (la diferencia serÃ­a de una lÃ­nea), que es lo que basta para
    // descontar el svg:y.
    static double AltoParrafoAncla(XElement p, Dictionary<string, Estilo> estilos)
    {
        var nombre = (string)p.Attribute(TEXT + "style-name") ?? "";

        var margenSup = Cadena(estilos, nombre, x => x.MargenSupPt) ?? 0;
        var margenInf = Cadena(estilos, nombre, x => x.MargenInfPt) ?? 0;

        var linea = Cadena(estilos, nombre, x => x.AltoLineaPt) ?? 0;

        if (linea <= 0)
        {
            var pct = Cadena(estilos, nombre, x => x.AltoLineaPct) ?? 0;
            var tamEstilo = Cadena(estilos, nombre, x => x.TamanoPt) ?? 0;

            // mismos factores que LeerParrafo (rama AltoLineaPct y respaldo de celda)
            linea = pct > 0
                ? (tamEstilo > 0 ? tamEstilo : 10) * AltoNaturalFuenteCelda * pct
                : 10 * AltoNaturalFuenteCelda;
        }

        return linea + margenSup + margenInf;
    }

    static Estilo LeerEstilo(XElement st)
    {
        var tp = st.Element(STYLE + "text-properties");
        var pp = st.Element(STYLE + "paragraph-properties");
        var cp = st.Element(STYLE + "table-column-properties");
        var cel = st.Element(STYLE + "table-cell-properties");
        var fila = st.Element(STYLE + "table-row-properties");

        // style:text-position se interpreta una sola vez: escala y posiciÃ³n vienen juntas
        var posicion = PosicionTexto((string)tp?.Attribute(STYLE + "text-position"));

        static bool HayBorde(string v) => !string.IsNullOrWhiteSpace(v) && v != "none";

        // "0.5pt solid #aeaaaa" -> "#aeaaaa" (null si no declara color)
        static string ColorBorde(string v)
        {
            if (!HayBorde(v)) return null;

            foreach (var parte in v.Split(' '))
                if (parte.StartsWith("#")) return parte;

            return "#000000";
        }

        // "0.5pt solid #aeaaaa" -> 0.5
        static double GrosorBorde(string v)
        {
            if (!HayBorde(v)) return 0;

            foreach (var parte in v.Split(' '))
                if (parte.EndsWith("pt") && double.TryParse(parte[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var pt))
                    return pt;

            return 0;
        }

        var gp = st.Element(STYLE + "graphic-properties");

        var paradas = pp?.Element(STYLE + "tab-stops")?.Elements(STYLE + "tab-stop").ToList();
        var bordeGeneral = (string)cel?.Attribute(FO + "border");

        return new Estilo
        {
            Padre = (string)st.Attribute(STYLE + "parent-style-name"),
            Fuente = (string)tp?.Attribute(STYLE + "font-name") ?? (string)tp?.Attribute(FO + "font-family"),
            TamanoPt = tp?.Attribute(FO + "font-size")?.Value is string s ? APt(s) : null,
            EscalaPosicion = posicion?.Escala,
            Posicion = posicion?.Posicion,
            Negrita = ((string)tp?.Attribute(FO + "font-weight")) is string w
                ? (w == "bold" || w == "600" || w == "700" || w == "800" || w == "900")
                : (bool?)null,
            Cursiva = ((string)tp?.Attribute(FO + "font-style")) is string fs
                ? fs == "italic"
                : (bool?)null,
            Subrayado = ((string)tp?.Attribute(STYLE + "text-underline-style")) is string u
                ? (u != "none" && u != "")
                : (bool?)null,
            Kerning = ((string)tp?.Attribute(STYLE + "letter-kerning")) is string lk
                ? !string.Equals(lk, "false", StringComparison.OrdinalIgnoreCase)
                : (bool?)null,
            EspaciadoCaracteresPt = ((string)tp?.Attribute(FO + "letter-spacing")) is string ls && !string.IsNullOrWhiteSpace(ls)
                ? (string.Equals(ls.Trim(), "normal", StringComparison.OrdinalIgnoreCase) ? 0.0 : APt(ls))
                : (double?)null,
            Color = (string)tp?.Attribute(FO + "color"),
            Alineacion = (string)pp?.Attribute(FO + "text-align"),
            MargenIzqPt = pp?.Attribute(FO + "margin-left")?.Value is string mi ? APt(mi) : null,
            MargenDerPt = pp?.Attribute(FO + "margin-right")?.Value is string md ? APt(md) : null,
            MargenSupPt = pp?.Attribute(FO + "margin-top")?.Value is string ms ? APt(ms) : null,
            MargenInfPt = pp?.Attribute(FO + "margin-bottom")?.Value is string mbo ? APt(mbo) : null,
            SangriaPt = pp?.Attribute(FO + "text-indent")?.Value is string ti ? APt(ti) : null,

            // Â§20.255: si es "true", el margin-bottom de este pÃ¡rrafo y el margin-top del
            // siguiente se ignoran cuando ademÃ¡s comparten estilo y Ã¡rea de contenido
            Contextual = ((string)pp?.Attribute(STYLE + "contextual-spacing")) is string cs
                ? cs == "true" : (bool?)null,
            Huerfanas = int.TryParse((string)pp?.Attribute(FO + "orphans"), out var orp) ? orp : (int?)null,
            Viudas = int.TryParse((string)pp?.Attribute(FO + "widows"), out var wid) ? wid : (int?)null,
            MantenerParrafoJunto = ((string)pp?.Attribute(FO + "keep-together")) is string ktp ? ktp == "always" : (bool?)null,
            MantenerConSiguiente = ((string)pp?.Attribute(FO + "keep-with-next")) is string kwn ? kwn == "always" : (bool?)null,
            ColumnWidthCm = cp?.Attribute(STYLE + "column-width")?.Value is string cw ? ACm(cw) : null,
            AltoMinimoFilaPt = fila?.Attribute(STYLE + "min-row-height")?.Value is string amh ? APt(amh) : null,
            AltoFijoFilaPt = fila?.Attribute(STYLE + "row-height")?.Value is string rh ? APt(rh) : null,
            MantenerFilaJunta = ((string)fila?.Attribute(FO + "keep-together")) is string kt
                ? kt == "always" : (bool?)null,

            // alto de línea real del párrafo (fo:line-height en cm o %, o style:line-height-at-least en cm)
            AltoLineaPt = pp?.Attribute(FO + "line-height")?.Value is string lh && !lh.EndsWith("%") ? APt(lh) : null,
            AltoLineaPct = pp?.Attribute(FO + "line-height")?.Value is string lhp && lhp.EndsWith("%")
                ? (double.TryParse(lhp[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) ? pct / 100 : (double?)null)
                : null,
            AltoLineaMinimoPt = pp?.Attribute(STYLE + "line-height-at-least")?.Value is string lhm ? APt(lhm) : null,

            // bordes de celda: fo:border general y los cuatro lados
            BordeIzq = HayBorde((string)cel?.Attribute(FO + "border-left")) || HayBorde(bordeGeneral),
            BordeDer = HayBorde((string)cel?.Attribute(FO + "border-right")) || HayBorde(bordeGeneral),
            BordeSup = HayBorde((string)cel?.Attribute(FO + "border-top")) || HayBorde(bordeGeneral),
            BordeInf = HayBorde((string)cel?.Attribute(FO + "border-bottom")) || HayBorde(bordeGeneral),

            // color de cada borde: el del lado y, si no lo declara, el general
            ColorBordeSup = ColorBorde((string)cel?.Attribute(FO + "border-top") ?? bordeGeneral),
            ColorBordeInf = ColorBorde((string)cel?.Attribute(FO + "border-bottom") ?? bordeGeneral),
            ColorBordeIzq = ColorBorde((string)cel?.Attribute(FO + "border-left") ?? bordeGeneral),
            ColorBordeDer = ColorBorde((string)cel?.Attribute(FO + "border-right") ?? bordeGeneral),

            VerticalAlign = (string)cel?.Attribute(STYLE + "vertical-align"),
            GrosorBordePt = new[]
            {
                bordeGeneral,
                (string)cel?.Attribute(FO + "border-top"),
                (string)cel?.Attribute(FO + "border-bottom"),
                (string)cel?.Attribute(FO + "border-left"),
                (string)cel?.Attribute(FO + "border-right"),
            }.Select(GrosorBorde).FirstOrDefault(g => g > 0.05),
            GrosorBordeIzqPt = GrosorBorde((string)cel?.Attribute(FO + "border-left") ?? bordeGeneral),
            GrosorBordeDerPt = GrosorBorde((string)cel?.Attribute(FO + "border-right") ?? bordeGeneral),
            GrosorBordeSupPt = GrosorBorde((string)cel?.Attribute(FO + "border-top") ?? bordeGeneral),
            GrosorBordeInfPt = GrosorBorde((string)cel?.Attribute(FO + "border-bottom") ?? bordeGeneral),

            PaddingIzqPt = cel?.Attribute(FO + "padding-left")?.Value is string pi ? APt(pi)
                         : cel?.Attribute(FO + "padding")?.Value is string pg2 ? APt(pg2)
                         : gp?.Attribute(FO + "padding-left")?.Value is string gpi ? APt(gpi)
                         : gp?.Attribute(FO + "padding")?.Value is string gpg2 ? APt(gpg2) : 0,
            PaddingDerPt = cel?.Attribute(FO + "padding-right")?.Value is string pdr ? APt(pdr)
                         : cel?.Attribute(FO + "padding")?.Value is string pg3 ? APt(pg3)
                         : gp?.Attribute(FO + "padding-right")?.Value is string gpdr ? APt(gpdr)
                         : gp?.Attribute(FO + "padding")?.Value is string gpg3 ? APt(gpg3) : 0,
            PaddingSupPt = cel?.Attribute(FO + "padding-top")?.Value is string psu ? APt(psu)
                         : cel?.Attribute(FO + "padding")?.Value is string pg4 ? APt(pg4)
                         : gp?.Attribute(FO + "padding-top")?.Value is string gpsu ? APt(gpsu)
                         : gp?.Attribute(FO + "padding")?.Value is string gpg4 ? APt(gpg4) : 0,
            PaddingInfPt = cel?.Attribute(FO + "padding-bottom")?.Value is string pin ? APt(pin)
                         : cel?.Attribute(FO + "padding")?.Value is string pg5 ? APt(pg5)
                         : gp?.Attribute(FO + "padding-bottom")?.Value is string gpin ? APt(gpin)
                         : gp?.Attribute(FO + "padding")?.Value is string gpg5 ? APt(gpg5) : 0,

            // borde y fondo del PÃRRAFO (style:paragraph-properties)
            SaltoPagina = (string)pp?.Attribute(FO + "break-before"),
            PaginaMaestra = (string)st.Attribute(STYLE + "master-page-name"),
            ParBorde = HayBorde((string)pp?.Attribute(FO + "border") ?? (string)pp?.Attribute(FO + "border-top")),

            // sÃ³lo el renglÃ³n de abajo (P24 de la solicitud): no hay borde ni arriba ni a los lados
            ParSoloBordeInf = !HayBorde((string)pp?.Attribute(FO + "border")) &&
                              !HayBorde((string)pp?.Attribute(FO + "border-top")) &&
                              HayBorde((string)pp?.Attribute(FO + "border-bottom")),

            ParColorBorde = ColorBorde((string)pp?.Attribute(FO + "border") ?? (string)pp?.Attribute(FO + "border-top")
                                       ?? (string)pp?.Attribute(FO + "border-bottom")),
            ParGrosorBordePt = GrosorBorde((string)pp?.Attribute(FO + "border") ?? (string)pp?.Attribute(FO + "border-top")
                                           ?? (string)pp?.Attribute(FO + "border-bottom")),
            ParFondo = (string)pp?.Attribute(FO + "background-color") is string fondoPar &&
                       fondoPar != "transparent" && fondoPar.StartsWith("#") ? fondoPar : null,
            ParPaddingSupPt = pp?.Attribute(FO + "padding-top")?.Value is string pts ? APt(pts) : 0,
            ParPaddingInfPt = pp?.Attribute(FO + "padding-bottom")?.Value is string pti ? APt(pti) : 0,
            ParPaddingIzqPt = pp?.Attribute(FO + "padding-left")?.Value is string ptl ? APt(ptl) : 0,
            ParPaddingDerPt = pp?.Attribute(FO + "padding-right")?.Value is string ptr ? APt(ptr) : 0,

            Tabs = paradas != null && paradas.Count > 0
                ? paradas.Select(x => new TabStopModelo
                  {
                      PosicionPt = APt((string)x.Attribute(STYLE + "position")),
                      Tipo = ((string)x.Attribute(STYLE + "type") ?? "left").ToLowerInvariant(),
                  }).Where(x => x.PosicionPt > 0).OrderBy(x => x.PosicionPt).ToList()
                : null,

            VerticalRel = (string)gp?.Attribute(STYLE + "vertical-rel"),
            HorizontalRel = (string)gp?.Attribute(STYLE + "horizontal-rel"),
            ModoColor = (string)gp?.Attribute(DRAW + "color-mode"),
            VerticalPos = (string)gp?.Attribute(STYLE + "vertical-pos"),
            HorizontalPos = (string)gp?.Attribute(STYLE + "horizontal-pos"),
            Wrap = (string)gp?.Attribute(STYLE + "wrap"),
            RunThrough = (string)gp?.Attribute(STYLE + "run-through"),
            GrafBorde = HayBorde((string)gp?.Attribute(FO + "border")),
            GrafColorBorde = ColorBorde((string)gp?.Attribute(FO + "border")),
            GrafGrosorBordePt = GrosorBorde((string)gp?.Attribute(FO + "border")),
            ColorTrazo = !string.Equals((string)gp?.Attribute(DRAW + "stroke"), "none", StringComparison.OrdinalIgnoreCase)
                ? (string)gp?.Attribute(SVG + "stroke-color")
                : null,
            GrosorTrazoPt = !string.Equals((string)gp?.Attribute(DRAW + "stroke"), "none", StringComparison.OrdinalIgnoreCase)
                ? APt((string)gp?.Attribute(SVG + "stroke-width"))
                : 0,

            ColorFondo = (string)cel?.Attribute(FO + "background-color") is string fondo &&
                         fondo != "transparent" && fondo.StartsWith("#") ? fondo
                       : !string.Equals((string)gp?.Attribute(DRAW + "fill"), "none", StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals((string)gp?.Attribute(DRAW + "opacity"), "0%", StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals((string)gp?.Attribute(STYLE + "background-transparency"), "100%", StringComparison.OrdinalIgnoreCase) &&
                         (string)gp?.Attribute(DRAW + "fill-color") is string relleno &&
                         relleno.StartsWith("#") ? relleno : null,
        };
    }

    // aplica una cadena de estilos (de raÃ­z a hijo: gana el mÃ¡s especÃ­fico)
    static void AplicarCadena(Dictionary<string, Estilo> estilos, string nombre, RunModelo run)
    {
        var cadena = new List<Estilo>();
        var actual = nombre;
        var vistos = 0;

        while (!string.IsNullOrEmpty(actual) && estilos.TryGetValue(actual, out var e) && vistos++ < 20)
        {
            cadena.Add(e);
            actual = e.Padre;
        }

        cadena.Reverse();

        foreach (var e in cadena)
        {
            if (!string.IsNullOrEmpty(e.Fuente)) run.Fuente = e.Fuente;
            if (e.TamanoPt.HasValue) run.TamanoPt = e.TamanoPt.Value;

            // style:text-position="super 58%": se acumulan escala y elevaciÃ³n y se aplican
            // al final de Resolver, porque el tamaÃ±o puede venir de otro estilo de la cadena.
            if (e.EscalaPosicion.HasValue) run.EscalaPosicion = e.EscalaPosicion.Value;
            if (e.Posicion != null) run.Posicion = e.Posicion;
            if (e.Color != null)
            {
                run.Color = e.Color;
                run.ColorAutomatico = false;
            }
            if (e.Kerning.HasValue) run.Kerning = e.Kerning.Value;
            if (e.EspaciadoCaracteresPt.HasValue) run.EspaciadoCaracteresPt = e.EspaciadoCaracteresPt.Value;
            if (e.Negrita.HasValue) run.Negrita = e.Negrita.Value;
            if (e.Cursiva.HasValue) run.Cursiva = e.Cursiva.Value;
            if (e.Subrayado.HasValue) run.Subrayado = e.Subrayado.Value;
        }
    }

    // Resuelve el formato de un run.
    // IMPORTANTE: primero el estilo del PÃRRAFO y despuÃ©s el del SPAN. Un span suele
    // traer sÃ³lo algÃºn atributo (o ninguno): si se resuelve aislado, el tamaÃ±o de fuente
    // se pierde y cae al valor por defecto (10 pt) en vez de heredar los 6.5 pt del pÃ¡rrafo.
    // Los SPAN anidados tambiÃ©n se acumulan: en la orden de compra el correo va como
    // <text:a style="Internet_20_link"><span "Internet_20_link"><span "T11">â€¦</text:a>.
    // T11 sÃ³lo declara Arial 7 pt negrita, asÃ­ que el azul y el subrayado del enlace
    // viven en el span de FUERA; resolver Ãºnicamente el mÃ¡s interno los perdÃ­a y el
    // correo salÃ­a en negro y sin subrayar (parecÃ­a texto normal, no un hipervÃ­nculo).
    static RunModelo Resolver(Dictionary<string, Estilo> estilos, IReadOnlyList<string> estilosSpan,
                              string texto = "", string slot = null, string nombreParrafo = null)
    {
        var run = new RunModelo { Texto = LimpiarGlifos(texto), Slot = slot };

        if (!string.IsNullOrEmpty(nombreParrafo))
            AplicarCadena(estilos, nombreParrafo, run);

        foreach (var span in estilosSpan)
            if (!string.IsNullOrEmpty(span) && span != nombreParrafo)
                AplicarCadena(estilos, span, run);

        if (string.IsNullOrEmpty(run.Fuente)) run.Fuente = "Arial";

        // style:text-position: LibreOffice dibuja "super 58%" al 58 % del tamaÃ±o declarado
        // (medido en la solicitud de traslado: T11 dice 12 pt y LO pinta 7 pt; 12 x 0,583).
        if (run.EscalaPosicion is double escala)
            run.TamanoPt = run.TamanoPt * escala / 100;

        return run;
    }

    // Primer valor NO nulo de una propiedad a lo largo de la cadena de estilos.
    static T? Cadena<T>(Dictionary<string, Estilo> estilos, string nombre, Func<Estilo, T?> leer) where T : struct
    {
        var actual = nombre;
        var vistos = 0;

        while (!string.IsNullOrEmpty(actual) && estilos.TryGetValue(actual, out var e) && vistos++ < 20)
        {
            var valor = leer(e);
            if (valor.HasValue) return valor;
            actual = e.Padre;
        }

        return null;
    }

    static string CadenaTexto(Dictionary<string, Estilo> estilos, string nombre, Func<Estilo, string> leer)
    {
        var actual = nombre;
        var vistos = 0;

        while (!string.IsNullOrEmpty(actual) && estilos.TryGetValue(actual, out var e) && vistos++ < 20)
        {
            var valor = leer(e);
            if (!string.IsNullOrEmpty(valor)) return valor;
            actual = e.Padre;
        }

        return null;
    }

    // Estilo "comÃºn" del pÃ¡rrafo para la comparaciÃ³n de Â§20.255: si text:style-name
    // apunta a un estilo automÃ¡tico, se toma su style:parent-style-name.
    static string EstiloComun(Dictionary<string, Estilo> estilos, string nombre)
    {
        if (string.IsNullOrEmpty(nombre) || !estilos.TryGetValue(nombre, out var e)) return nombre ?? "";
        return string.IsNullOrEmpty(e.Padre) ? nombre : e.Padre;
    }

    // style:contextual-spacing con herencia por la cadena de estilos (por defecto false).
    static bool ContextualDe(Dictionary<string, Estilo> estilos, string nombre)
    {
        var actual = nombre;
        var vistos = 0;

        while (!string.IsNullOrEmpty(actual) && estilos.TryGetValue(actual, out var e) && vistos++ < 20)
        {
            if (e.Contextual.HasValue) return e.Contextual.Value;
            actual = e.Padre;
        }

        return false;
    }

    // parada por omisiÃ³n de LibreOffice cuando el estilo no declara ninguna
    const double ParadaPorOmisionPt = 1.25 * CM_PT;

    static List<TabStopModelo> TabuladoresDe(Dictionary<string, Estilo> estilos, string nombre)
    {
        var actual = nombre;

        for (var vistos = 0; !string.IsNullOrEmpty(actual) && vistos < 20; vistos++)
        {
            if (!estilos.TryGetValue(actual, out var e)) return null;
            if (e.Tabs != null && e.Tabs.Count > 0) return e.Tabs;
            actual = e.Padre;
        }

        return null;
    }

    static double SiguienteParada(List<double> paradas, double x)
    {
        if (paradas != null)
            foreach (var p in paradas)
                if (p > x + 0.5) return p;

        // por omisiÃ³n (y despuÃ©s de la Ãºltima declarada): mÃºltiplos de 1.25 cm
        var desde = paradas != null && paradas.Count > 0 ? Math.Max(x, paradas.Max()) : x;
        return (Math.Floor(desde / ParadaPorOmisionPt) + 1) * ParadaPorOmisionPt;
    }

    static RunModelo CopiaRun(RunModelo r, string texto) => new()
    {
        Texto = texto,
        Fuente = r.Fuente,
        TamanoPt = r.TamanoPt,
        Negrita = r.Negrita,
        Cursiva = r.Cursiva,
        Subrayado = r.Subrayado,
        Color = r.Color,
        ColorAutomatico = r.ColorAutomatico,
        Kerning = r.Kerning,
        EspaciadoCaracteresPt = r.EspaciadoCaracteresPt,
        Slot = r.Slot,
        Hipervinculo = r.Hipervinculo,
        CampoDinamico = r.CampoDinamico,
        Posicion = r.Posicion,
        EscalaPosicion = r.EscalaPosicion,
    };

    // En PrawnSharp los tabuladores (\t) se posicionan directamente en la parada exacta (p.TabsPt).

    static ParrafoModelo LeerParrafo(XElement p, Dictionary<string, Estilo> estilos, bool enCelda = false,
                                     bool enColumnas = false)
    {
        var modelo = new ParrafoModelo { EsTitulo = p.Name == TEXT + "h" };
        var nombreEstilo = (string)p.Attribute(TEXT + "style-name") ?? "";

        // AlineaciÃ³n, mÃ¡rgenes y sangrÃ­a SE HEREDAN por la cadena de estilos: los estilos
        // automÃ¡ticos de LibreOffice normalmente sÃ³lo declaran lo que cambia y el margen
        // vive en el padre (el pÃ¡rrafo vacÃ­o P19 no declara nada y hereda el
        // fo:margin-bottom="0.353cm" de Standard, que es el hueco que faltaba antes del QR).
        modelo.Alineacion = CadenaTexto(estilos, nombreEstilo, x => x.Alineacion) ?? "";
        modelo.MargenIzqPt = Cadena(estilos, nombreEstilo, x => x.MargenIzqPt) ?? 0;
        modelo.MargenDerPt = Cadena(estilos, nombreEstilo, x => x.MargenDerPt) ?? 0;
        modelo.MargenSupPt = Cadena(estilos, nombreEstilo, x => x.MargenSupPt) ?? 0;
        modelo.MargenInfPt = Cadena(estilos, nombreEstilo, x => x.MargenInfPt) ?? 0;
        modelo.SangriaPt = Cadena(estilos, nombreEstilo, x => x.SangriaPt) ?? 0;

        // salto de pagina/columna explicito del documento (fo:break-before)
        var salto = CadenaTexto(estilos, nombreEstilo, x => x.SaltoPagina);
        modelo.SaltoPaginaAntes = salto == "page";
        modelo.SaltoColumnaAntes = salto == "column";
        modelo.PaginaMaestra = CadenaTexto(estilos, nombreEstilo, x => x.PaginaMaestra);

        // caja del pÃ¡rrafo: borde, fondo y paddings
        modelo.BordeParrafo = Cadena<bool>(estilos, nombreEstilo, x => x.ParBorde ? (bool?)true : null) ?? false;
        modelo.BordeInferiorParrafo = Cadena<bool>(estilos, nombreEstilo, x => x.ParSoloBordeInf ? (bool?)true : null) ?? false;
        modelo.ColorBordeParrafo = CadenaTexto(estilos, nombreEstilo, x => x.ParColorBorde);
        modelo.GrosorBordeParrafoPt = Cadena<double>(estilos, nombreEstilo, x => x.ParGrosorBordePt > 0 ? (double?)x.ParGrosorBordePt : null) ?? 0;
        modelo.ColorFondoParrafo = CadenaTexto(estilos, nombreEstilo, x => x.ParFondo);
        modelo.PaddingSupParrafoPt = Cadena<double>(estilos, nombreEstilo, x => (double?)x.ParPaddingSupPt) ?? 0;
        modelo.PaddingInfParrafoPt = Cadena<double>(estilos, nombreEstilo, x => (double?)x.ParPaddingInfPt) ?? 0;
        modelo.PaddingIzqParrafoPt = Cadena<double>(estilos, nombreEstilo, x => (double?)x.ParPaddingIzqPt) ?? 0;
        modelo.PaddingDerParrafoPt = Cadena<double>(estilos, nombreEstilo, x => (double?)x.ParPaddingDerPt) ?? 0;

        // Â§20.255: contextual-spacing y el estilo "comÃºn" (el padre, si el estilo es
        // automÃ¡tico) hacen falta para saber si se colapsa el espacio entre pÃ¡rrafos.
        modelo.EstiloComun = EstiloComun(estilos, nombreEstilo);
        modelo.Contextual = ContextualDe(estilos, nombreEstilo);
        modelo.Huerfanas = Cadena(estilos, nombreEstilo, x => x.Huerfanas) ?? 0;
        modelo.Viudas = Cadena(estilos, nombreEstilo, x => x.Viudas) ?? 0;

        AgregarRuns(p, new List<string>(), modelo.Runs, estilos, nombreEstilo);

        // Sangría francesa manual (fo:margin-left > 0, fo:text-indent < 0) con tabulador
        // tras un marcador inicial (p.ej. "•\tLos documentos..." en P88/P90 del CCI):
        // se modela igual que un inciso de lista para que el marcador quede en
        // margin-left + text-indent y los renglones siguientes continúen en margin-left.
        var paradasTab = TabuladoresDe(estilos, nombreEstilo);

        if (!enCelda && modelo.SangriaPt < -0.5 && modelo.MargenIzqPt > 0.5 &&
            modelo.Runs.Any(r => r.Texto != null && r.Texto.Contains('\t')))
        {
            var idxTab = modelo.Runs.FindIndex(r => r.Texto != null && r.Texto.Contains('\t'));
            if (idxTab >= 0)
            {
                var prefijo = new System.Text.StringBuilder();
                for (var k = 0; k < idxTab; k++) prefijo.Append(modelo.Runs[k].Texto);
                var posTabEnRun = modelo.Runs[idxTab].Texto.IndexOf('\t');
                prefijo.Append(modelo.Runs[idxTab].Texto.AsSpan(0, posTabEnRun));
                var textoMarca = prefijo.ToString().Trim();

                if (textoMarca.Length > 0 && textoMarca.Length <= 8)
                {
                    var runBaseMarca = modelo.Runs.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.Texto)) ?? modelo.Runs[0];
                    var nuevosRuns = new List<RunModelo> { CopiaRun(runBaseMarca, textoMarca + " ") };

                    var restoPrimer = modelo.Runs[idxTab].Texto[(posTabEnRun + 1)..];
                    if (restoPrimer.Length > 0)
                        nuevosRuns.Add(CopiaRun(modelo.Runs[idxTab], restoPrimer));

                    for (var k = idxTab + 1; k < modelo.Runs.Count; k++)
                        nuevosRuns.Add(modelo.Runs[k]);

                    if (nuevosRuns.Count > 1)
                    {
                        modelo.Runs.Clear();
                        modelo.Runs.AddRange(nuevosRuns);
                        modelo.MargenIncisoPt = Math.Max(modelo.MargenIzqPt + modelo.SangriaPt, 0);
                        modelo.SangriaColgantePt = Math.Min(-modelo.SangriaPt, modelo.MargenIzqPt);
                    }
                }
            }
        }

        // <text:tab/> avanza hasta la siguiente parada de tabulador. QuestPDF no tiene
        // tabuladores: se convierten en espacios contados (el avance lo da la parada, no
        // el nÃºmero de espacios). Sin esto la segunda lÃ­nea del bloque Â«UbicaciÃ³nÂ» quedaba
        // 35 pt a la izquierda (el ancho de la parada por omisiÃ³n).
        if (paradasTab != null && paradasTab.Count > 0)
            modelo.Tabs = paradasTab;

        // alto de línea: fo:line-height del estilo (en cm o en %), con herencia.
        // Si el párrafo está vacío o sólo contiene espacios en un <text:span> (p.ej. P3 con
        // <span T2><text:s text:c="17"/></span> o loext:marker-style-name="T2"), Writer usa
        // el formato de ese span/marcador de fin de párrafo para calcular el alto de la línea.
        modelo.TamanoBasePt = Cadena(estilos, nombreEstilo, e => e.TamanoPt) ?? 0;
        var fuenteBase = CadenaTexto(estilos, nombreEstilo, e => e.Fuente);

        var tieneTextoVisible = modelo.Runs.Any(r => !string.IsNullOrWhiteSpace(r.Texto));
        if (!tieneTextoVisible)
        {
            var runEspacio = modelo.Runs.FirstOrDefault(r => !string.IsNullOrEmpty(r.Texto) && r.TamanoPt > 0);
            if (runEspacio != null)
            {
                modelo.TamanoBasePt = runEspacio.TamanoPt;
                if (!string.IsNullOrWhiteSpace(runEspacio.Fuente)) fuenteBase = runEspacio.Fuente;
            }
            else if ((string)p.Attribute(LOEXT + "marker-style-name") is string marcador && !string.IsNullOrEmpty(marcador))
            {
                if (Cadena(estilos, marcador, e => e.TamanoPt) is double tMarc && tMarc > 0)
                    modelo.TamanoBasePt = tMarc;
                if (CadenaTexto(estilos, marcador, e => e.Fuente) is string fMarc && !string.IsNullOrWhiteSpace(fMarc))
                    fuenteBase = fMarc;
            }
        }

        modelo.FuenteBase = string.IsNullOrWhiteSpace(fuenteBase) ? "Arial" : fuenteBase;
        modelo.MantenerJunto = Cadena(estilos, nombreEstilo, e => e.MantenerParrafoJunto) ?? false;
        modelo.MantenerConSiguiente = Cadena(estilos, nombreEstilo, e => e.MantenerConSiguiente) ?? false;

        var actual = nombreEstilo;
        var vistos = 0;

        while (!string.IsNullOrEmpty(actual) && estilos.TryGetValue(actual, out var estiloCadena) && vistos++ < 20)
        {
            if (estiloCadena.AltoLineaPt is double alto && alto > 0)
            {
                modelo.AltoLineaPt = ATwipsPt(alto);
                modelo.AltoLineaFijo = true;
                break;
            }
            if (estiloCadena.AltoLineaMinimoPt is double altoMin)
            {
                var tamanoMin = modelo.Runs
                    .Where(r => !string.IsNullOrWhiteSpace(r.Texto) && r.TamanoPt > 0)
                    .Select(r => r.TamanoPt)
                    .DefaultIfEmpty(0)
                    .Max();
                var tamEf = tamanoMin > 0 ? tamanoMin : (modelo.TamanoBasePt > 0 ? modelo.TamanoBasePt : 10);
                var famEf = tamanoMin > 0 ? FuenteDelRun(modelo) : fuenteBase;
                modelo.AltoLineaPt = ATwipsPt(Math.Max(altoMin, tamEf * NaturalDeFuente(famEf)));
                break;
            }
            if (estiloCadena.AltoLineaPct is double pct && pct > 0)
            {
                modelo.AltoLineaPct = pct;
                var tamano = modelo.Runs
                    .Where(r => !string.IsNullOrWhiteSpace(r.Texto) && r.TamanoPt > 0)
                    .Select(r => r.TamanoPt)
                    .DefaultIfEmpty(0)
                    .Max();
                var tamEf = tamano > 0 ? tamano : (modelo.TamanoBasePt > 0 ? modelo.TamanoBasePt : 10);
                var famEf = tamano > 0 ? FuenteDelRun(modelo) : fuenteBase;
                modelo.AltoLineaPt = ATwipsPt(tamEf * NaturalDeFuente(famEf) * pct);
                break;
            }
            actual = estiloCadena.Padre;
        }

        if (modelo.AltoLineaPt <= 0)
        {
            var tamano = modelo.Runs
                .Where(r => !string.IsNullOrWhiteSpace(r.Texto) && r.TamanoPt > 0)
                .Select(r => r.TamanoPt)
                .DefaultIfEmpty(0)
                .Max();
            var tamEf = tamano > 0 ? tamano : (modelo.TamanoBasePt > 0 ? modelo.TamanoBasePt : 10);
            var famEf = tamano > 0 ? FuenteDelRun(modelo) : fuenteBase;
            modelo.AltoLineaPt = ATwipsPt(tamEf * NaturalDeFuente(famEf));
        }

        // Un draw:frame anclado "as-char" o "char" ocupa espacio en la lÃ­nea: la lÃ­nea
        // crece hasta el alto del objeto (asÃ­ el QR empuja el texto que viene debajo).
        // Si ademÃ¡s trae imagen, se guarda aquÃ­ para dibujarla DENTRO del flujo: su
        // posiciÃ³n depende del texto de arriba y no se puede fijar en una capa absoluta.
        foreach (var marco in p.Descendants().Where(x => x.Name == DRAW + "frame" || x.Name == DRAW + "custom-shape"))
        {
            var ancla = (string)marco.Attribute(TEXT + "anchor-type") ?? "";
            if (ancla != "as-char" && ancla != "char") continue;

            var altoObjeto = APt((string)marco.Attribute(SVG + "height"));

            // SÃ³lo el ancla "as-char" ocupa espacio en la lÃ­nea. Los de ancla "char" se
            // dibujan como capa con posiciÃ³n propia (LibreOffice hace lo mismo: el marco
            // Â«SOLICITUD DE CREDITOÂ» de la solicitud de crÃ©dito PF va en svg:x=-6.7 cm,
            // fuera de la celda). Reservarles alto dejaba esa fila ~33 pt mÃ¡s alta.
            if (ancla == "as-char" && altoObjeto > modelo.AltoObjetoPt)
                modelo.AltoObjetoPt = altoObjeto;

            var imagen = marco.Descendants(DRAW + "image").FirstOrDefault();
            var binario = imagen?.Element(OFFICE + "binary-data")?.Value;

            // sÃ³lo "as-char" se dibuja en el flujo: las de ancla "char" ya salen como
            // capa absoluta (ver ImagenEnFlujo en el modelo)
            if (ancla == "as-char") modelo.ImagenEnFlujo = true;

            if (!string.IsNullOrWhiteSpace(binario))
            {
                modelo.ImagenBase64 = binario.Trim();
                modelo.ImagenTipo = ((string)imagen.Attribute(DRAW + "mime-type") ?? "").Replace("image/", "");
                modelo.ImagenAnchoPt = APt((string)marco.Attribute(SVG + "width"));
                modelo.ImagenAltoPt = altoObjeto;
            }
        }

        // draw:line: los renglones de firma de los contratos ("PATRON / TRABAJADOR").
        // Van anclados al carÃ¡cter, dentro de un pÃ¡rrafo, con coordenadas propias.
        foreach (var lineaFodt in p.Elements(DRAW + "line"))
        {
            var x1 = APt((string)lineaFodt.Attribute(SVG + "x1"));
            var x2 = APt((string)lineaFodt.Attribute(SVG + "x2"));
            var y1 = APt((string)lineaFodt.Attribute(SVG + "y1"));
            var y2 = APt((string)lineaFodt.Attribute(SVG + "y2"));

            var trazo = _trazo.TryGetValue((string)lineaFodt.Attribute(DRAW + "style-name") ?? "", out var t)
                ? t
                : (Grosor: 0.75, Color: "#000000");

            modelo.Lineas.Add(new LineaModelo
            {
                XPt = Math.Min(x1, x2),
                AnchoPt = Math.Abs(x2 - x1),
                YPt = Math.Min(y1, y2),
                GrosorPt = trazo.Grosor,
                Color = trazo.Color,
            });
        }

        // Ãšltima pasada: quitar los glifos del Ã¡rea privada (viÃ±etas Symbol) que hacen
        // abortar a QuestPDF. Se hace aquÃ­ para cubrir tambiÃ©n los textos que se pegan
        // directamente a un run (text:s, text:tab, marcas de combinaciÃ³n).
        foreach (var runParrafo in modelo.Runs)
            runParrafo.Texto = LimpiarGlifos(runParrafo.Texto);

        return modelo;
    }

    // estilosSpan: estilos de carÃ¡cter que ENVUELVEN al contenido, de fuera hacia dentro.
    // Se acumulan al bajar por los <text:span> para que un span interno herede lo que
    // declara el de fuera (si no, el enlace del correo perdÃ­a color y subrayado).
    static void AgregarRuns(XElement contenedor, List<string> estilosSpan, List<RunModelo> runs,
                            Dictionary<string, Estilo> estilos, string estiloParrafo,
                            string hipervinculo = null)
    {
        foreach (var nodo in contenedor.Nodes())
        {
            if (nodo is XText t)
            {
                var limpio = Limpiar(t.Value);
                if (limpio.Length == 0) continue;

                var run = Resolver(estilos, estilosSpan, limpio, null, estiloParrafo);
                run.Hipervinculo = hipervinculo;

                var ultimo = runs.Count > 0 ? runs[^1] : null;
                if (ultimo != null && ultimo.Slot == null && MismoEstilo(ultimo, run)) ultimo.Texto += limpio;
                else runs.Add(run);
            }
            else if (nodo is XElement el)
            {
                if (el.Name == TEXT + "span" || el.Name == TEXT + "a")
                {
                    // text:a: el correo de la orden de compra es un mailto. Aparte del
                    // estilo, se arrastra el destino para que el generador lo pinte como
                    // hipervÃ­nculo.
                    var estiloSpan = (string)el.Attribute(TEXT + "style-name");
                    var cadena = string.IsNullOrEmpty(estiloSpan)
                        ? estilosSpan
                        : new List<string>(estilosSpan) { estiloSpan };

                    var destino = hipervinculo;

                    if (el.Name == TEXT + "a")
                    {
                        var href = (string)el.Attribute(XLINK + "href");
                        if (!string.IsNullOrWhiteSpace(href)) destino = href;
                    }

                    AgregarRuns(el, cadena, runs, estilos, estiloParrafo, destino);
                }
                else if (el.Name == TEXT + "bookmark-start")
                {
                    runs.Add(Resolver(estilos, estilosSpan, "", (string)el.Attribute(TEXT + "name"), estiloParrafo));
                }
                else if (el.Name == TEXT + "s")
                {
                    var n = int.TryParse((string)el.Attribute(TEXT + "c"), out var k) ? k : 1;
                    runs.Add(Resolver(estilos, estilosSpan, new string(' ', n), null, estiloParrafo));
                }
                else if (el.Name == TEXT + "tab")
                {
                    runs.Add(Resolver(estilos, estilosSpan, "\t", null, estiloParrafo));
                }
                else if (el.Name == TEXT + "line-break")
                {
                    runs.Add(Resolver(estilos, estilosSpan, "\n", null, estiloParrafo));
                }
                else if (el.Name == TEXT + "database-display")
                {
                    // Campos de combinaciÃ³n de los reportes: al convertir el .doc a .fodt
                    // quedan como <text:database-display> con el valor YA calculado como
                    // texto (Â«TituloÂ», Â«FechaÂ», Â«EmpresaÂ»...). No se leÃ­an, asÃ­ que en el
                    // Auxiliar de Proveedores el tÃ­tulo, la fecha y la empresa no salÃ­an y
                    // el Ãºnico renglÃ³n que quedaba ("PÃ¡gina: 1") subÃ­a de mÃ¡s.
                    // Las marcas de regiÃ³n (Â«TableStart:BalanzaÂ» / Â«TableEnd:BalanzaÂ»)
                    // TAMBIÃ‰N se pintan: LibreOffice las imprime tal cual en el documento
                    // de plantilla. En los documentos ya combinados no aparecen â€”
                    // ExecuteRegion consume las regiones â€”, asÃ­ que no ensucian el PDF final.
                    var valor = Limpiar(el.Value);

                    if (valor.Length > 0)
                        runs.Add(Resolver(estilos, estilosSpan, valor, null, estiloParrafo));
                }
                else if (el.Name == TEXT + "date" || el.Name == TEXT + "time")
                {
                    // text:date / text:time: LibreOffice guarda el valor ya formateado como
                    // contenido (el "12/11/2026 14:01 O11/p11" del pie de la bitÃ¡cora).
                    // No se leÃ­a, asÃ­ que la lÃ­nea salÃ­a vacÃ­a.
                    var valor = Limpiar(el.Value);
                    if (valor.Length > 0) runs.Add(Resolver(estilos, estilosSpan, valor, null, estiloParrafo));
                }
                else if (el.Name == TEXT + "page-number" || el.Name == TEXT + "page-count")
                {
                    var valor = Limpiar(el.Value);
                    var runCampo = Resolver(estilos, estilosSpan, valor.Length > 0 ? valor : "1", null, estiloParrafo);
                    runCampo.CampoDinamico = el.Name == TEXT + "page-number" ? "page-number" : "page-count";
                    runs.Add(runCampo);
                }
                // draw:* dentro de un pÃ¡rrafo: ya se tomÃ³ como capa; no se mezcla con el texto
            }
        }
    }

    static bool MismoEstilo(RunModelo a, RunModelo b) =>
        a.Fuente == b.Fuente && a.TamanoPt == b.TamanoPt && a.Negrita == b.Negrita &&
        a.Cursiva == b.Cursiva && a.Subrayado == b.Subrayado && a.Color == b.Color &&
        a.ColorAutomatico == b.ColorAutomatico && a.Kerning == b.Kerning &&
        Math.Abs(a.EspaciadoCaracteresPt - b.EspaciadoCaracteresPt) < 1e-4 &&
        a.Hipervinculo == b.Hipervinculo && a.CampoDinamico == b.CampoDinamico;

    static CapaModelo LeerCapa(XElement el, Dictionary<string, Estilo> estilos)
    {
        var capa = new CapaModelo
        {
            Nombre = (string)el.Attribute(DRAW + "name") ?? "(sin nombre)",
            Tipo = el.Name == DRAW + "frame" ? "frame" : "custom-shape",
            Ancla = (string)el.Attribute(TEXT + "anchor-type") ?? "",
            XPt = APt((string)el.Attribute(SVG + "x")),
            YPt = APt((string)el.Attribute(SVG + "y")),
            AnchoPt = APt((string)el.Attribute(SVG + "width")),
            AltoPt = APt((string)el.Attribute(SVG + "height")),
            EnFlujo = ((string)el.Attribute(TEXT + "anchor-type")) == "as-char",
        };

        // relleno de la caja (draw:fill-color del estilo grÃ¡fico): la barra de color que va
        // antes de "ORDEN DE COMPRA" es un custom-shape sin texto ni imagen.
        var nombreEstiloGrafico = (string)el.Attribute(DRAW + "style-name");

        if (!string.IsNullOrEmpty(nombreEstiloGrafico) &&
            estilos.TryGetValue(nombreEstiloGrafico, out var estiloGrafico))
        {
            capa.ColorRelleno = estiloGrafico.ColorFondo;
            capa.VerticalRel = estiloGrafico.VerticalRel;
            capa.HorizontalRel = estiloGrafico.HorizontalRel;
            capa.ModoColor = estiloGrafico.ModoColor;
            capa.VerticalPos = estiloGrafico.VerticalPos;
            capa.HorizontalPos = estiloGrafico.HorizontalPos;
            capa.Wrap = estiloGrafico.Wrap ?? "";
            capa.RunThrough = estiloGrafico.RunThrough ?? "";
            capa.BordeCaja = estiloGrafico.GrafBorde;
            capa.ColorBorde = estiloGrafico.GrafColorBorde;
            capa.GrosorBordePt = estiloGrafico.GrafGrosorBordePt;
            capa.ColorTrazo = estiloGrafico.ColorTrazo;
            capa.GrosorTrazoPt = estiloGrafico.GrosorTrazoPt;
            capa.PaddingIzqPt = estiloGrafico.PaddingIzqPt;
            capa.PaddingDerPt = estiloGrafico.PaddingDerPt;
            capa.PaddingSupPt = estiloGrafico.PaddingSupPt;
            capa.PaddingInfPt = estiloGrafico.PaddingInfPt;
        }

        // Las formas dentro de <draw:g> heredan el anclaje y la posición relativa del grupo
        // (p.ej. la línea de firma "Freeform 3" dentro de "Grupo 1" en CCI).
        if (el.Parent?.Name == DRAW + "g")
        {
            if (string.IsNullOrEmpty(capa.Ancla))
            {
                capa.Ancla = (string)el.Parent.Attribute(TEXT + "anchor-type") ?? "";
                capa.EnFlujo = capa.Ancla == "as-char";
            }

            var estiloGrupoNombre = (string)el.Parent.Attribute(DRAW + "style-name");
            if (!string.IsNullOrEmpty(estiloGrupoNombre) &&
                estilos.TryGetValue(estiloGrupoNombre, out var estiloGrupo))
            {
                if (string.IsNullOrEmpty(capa.VerticalRel)) capa.VerticalRel = estiloGrupo.VerticalRel;
                if (string.IsNullOrEmpty(capa.HorizontalRel)) capa.HorizontalRel = estiloGrupo.HorizontalRel;
                if (string.IsNullOrEmpty(capa.VerticalPos)) capa.VerticalPos = estiloGrupo.VerticalPos;
                if (string.IsNullOrEmpty(capa.HorizontalPos)) capa.HorizontalPos = estiloGrupo.HorizontalPos;
                if (string.IsNullOrEmpty(capa.Wrap)) capa.Wrap = estiloGrupo.Wrap ?? "";
            }
        }

        // SÃ³lo cuenta lo que estÃ¡ DENTRO de la caja: el frame puede vivir en la celda de
        // otra tabla (la liquidaciones mete el bloque de firmas en la tabla de fichas) y
        // esa tabla de fuera no es suya.
        bool DentroDeTablaDeLaCapa(XElement nodo) =>
            nodo.Ancestors().TakeWhile(a => a != el).Any(a => a.Name == TABLE + "table");

        foreach (var p in el.Descendants(TEXT + "p"))
        {
            if (DentroDeTablaDeLaCapa(p)) continue;   // los pinta la tabla, no sueltos
            var parrafoCapa = LeerParrafo(p, estilos);

            // Los draw:line dentro de una caja con posiciÃ³n fija (PagarÃ©, formatos de
            // solicitud) no se dibujan: la caja tiene alto fijo y agregar el renglÃ³n
            // revienta el acomodo ("conflicting size constraints").
            parrafoCapa.Lineas.Clear();

            capa.Parrafos.Add(parrafoCapa);
        }

        foreach (var t in el.Descendants(TABLE + "table"))
        {
            if (DentroDeTablaDeLaCapa(t)) continue;   // tablas anidadas dentro de otra tabla de la capa
            capa.Tablas.Add(LeerTabla(t, estilos));
        }

        // alto aproximado del contenido: cada pÃ¡rrafo con su interlineado y mÃ¡rgenes
        foreach (var parrafoCapa in capa.Parrafos)
            capa.AltoContenidoPt += Math.Max(parrafoCapa.AltoLineaPt, 8) + parrafoCapa.MargenSupPt + parrafoCapa.MargenInfPt;

        foreach (var tablaCapa2 in capa.Tablas)
            capa.AltoContenidoPt += tablaCapa2.Filas.Count * 12;

        // imagen embebida: en un .fodt (plano) viene como
        //   <draw:image draw:mime-type="image/jpeg"><office:binary-data>BASE64</office:binary-data></draw:image>
        // (tambiÃ©n se admite el formato data:image/...;base64 en xlink:href)
        var img = el.Descendants(DRAW + "image").FirstOrDefault();

        if (img != null)
        {
            var mime = (string)img.Attribute(DRAW + "mime-type") ?? "";
            var binario = img.Element(OFFICE + "binary-data")?.Value;

            if (!string.IsNullOrWhiteSpace(binario))
            {
                capa.ImagenBase64 = binario.Trim();
                capa.ImagenTipo = mime.Replace("image/", "");
            }
            else
            {
                var href = (string)img.Attribute(XLINK + "href");
                if (href != null && href.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                {
                    var coma = href.IndexOf(',');
                    var cabecera = href.Substring(5, href.IndexOf(';') - 5);   // image/png
                    capa.ImagenTipo = cabecera.Replace("image/", "");
                    capa.ImagenBase64 = href.Substring(coma + 1);
                }
            }
        }

        return capa;
    }

    static TablaModelo LeerTabla(XElement tabla, Dictionary<string, Estilo> estilos)
    {
        var modelo = new TablaModelo { Nombre = (string)tabla.Attribute(TABLE + "name") ?? "" };

        // margen izquierdo propio de la tabla (normalmente negativo: se sale del Ã¡rea de texto)
        var nombreEstiloTabla = (string)tabla.Attribute(TABLE + "style-name") ?? "";
        var estiloTabla = tabla.Document?.Descendants(STYLE + "style")
            .FirstOrDefault(e => (string)e.Attribute(STYLE + "name") == nombreEstiloTabla);

        var propsTabla = estiloTabla?.Element(STYLE + "table-properties");

        if (propsTabla?.Attribute(FO + "margin-left")?.Value is string ml)
            modelo.MargenIzqPt = APt(ml);

        // Ancho total declarado por la tabla (style:width). Es independiente de la suma
        // de columnas: si la tabla pide 15.24 cm y las columnas suman 12.192 cm,
        // LibreOffice estira las columnas hasta 15.24; el generador debe hacer lo mismo.
        var anchoTabla = propsTabla?.Attribute(STYLE + "width")?.Value
                         ?? propsTabla?.Attribute(FO + "width")?.Value;
        if (!string.IsNullOrWhiteSpace(anchoTabla))
            modelo.AnchoDeclaradoPt = APt(anchoTabla);

        // table:align del estilo (o del propio elemento): las tablas mÃ¡s angostas que el
        // Ã¡rea de texto pueden ir al centro (la de sueldo del contrato CCI).
        modelo.AlineacionTabla = (string)estiloTabla?.Element(STYLE + "table-properties")?.Attribute(TABLE + "align")
                                 ?? (string)tabla.Attribute(TABLE + "align")
                                 ?? "";

        foreach (var col in tabla.Elements(TABLE + "table-column"))
        {
            var nombre = (string)col.Attribute(TABLE + "style-name") ?? "";
            var anchoCm = estilos.TryGetValue(nombre, out var e) ? e.ColumnWidthCm : null;

            // una columna puede declararse repetida (table:number-columns-repeated="2"):
            // si se cuenta una sola, el contenido no tiene dÃ³nde repartirse
            var repeticiones = int.TryParse((string)col.Attribute(TABLE + "number-columns-repeated"), out var rep) ? rep : 1;

            for (var k = 0; k < Math.Max(repeticiones, 1); k++)
                modelo.ColumnasPt.Add((anchoCm ?? 2.0) * CM_PT);
        }

        foreach (var fila in tabla.Elements(TABLE + "table-row"))
        {
            var mfila = new FilaModelo();

            // alto mÃ­nimo declarado por el estilo de la fila (style:min-row-height): en la
            // bitÃ¡cora de pedidos la fila del producto pide 1.335cm y es lo que separa un
            // grupo del siguiente. Sin esto las filas quedaban de una sola lÃ­nea de texto.
            var nombreEstiloFila = (string)fila.Attribute(TABLE + "style-name") ?? "";

            if (estilos.TryGetValue(nombreEstiloFila, out var estiloFila))
            {
                if (estiloFila.AltoMinimoFilaPt is double altoFila)
                    mfila.AltoMinimoPt = altoFila;
                if (estiloFila.AltoFijoFilaPt is double altoFijo)
                    mfila.AltoFijoPt = altoFijo;
                if (estiloFila.MantenerFilaJunta == true)
                    mfila.MantenerJunta = true;
            }
            foreach (var celda in fila.Elements(TABLE + "table-cell"))
            {
                var mcelda = new CeldaModelo
                {
                    RepetirColumnas = int.TryParse((string)celda.Attribute(TABLE + "number-columns-repeated"), out var rep) ? rep : 1,
                    SpanFilas = int.TryParse((string)celda.Attribute(TABLE + "number-rows-spanned"), out var sf) ? sf : 1,
                    SpanColumnas = int.TryParse((string)celda.Attribute(TABLE + "number-columns-spanned"), out var sc) ? sc : 1,
                };

                // bordes y paddings tal como los declara el estilo de la celda
                var nombreEstiloCelda = (string)celda.Attribute(TABLE + "style-name") ?? "";
                if (estilos.TryGetValue(nombreEstiloCelda, out var estiloCelda))
                {
                    mcelda.BordeIzq = estiloCelda.BordeIzq;
                    mcelda.BordeDer = estiloCelda.BordeDer;
                    mcelda.BordeSup = estiloCelda.BordeSup;
                    mcelda.BordeInf = estiloCelda.BordeInf;
                    mcelda.ColorBordeSup = estiloCelda.ColorBordeSup;
                    mcelda.ColorBordeInf = estiloCelda.ColorBordeInf;
                    mcelda.ColorBordeIzq = estiloCelda.ColorBordeIzq;
                    mcelda.ColorBordeDer = estiloCelda.ColorBordeDer;
                    mcelda.GrosorBordePt = estiloCelda.GrosorBordePt;
                    mcelda.GrosorBordeIzqPt = estiloCelda.GrosorBordeIzqPt;
                    mcelda.GrosorBordeDerPt = estiloCelda.GrosorBordeDerPt;
                    mcelda.GrosorBordeSupPt = estiloCelda.GrosorBordeSupPt;
                    mcelda.GrosorBordeInfPt = estiloCelda.GrosorBordeInfPt;
                    mcelda.VerticalAlign = estiloCelda.VerticalAlign ?? "";
                    mcelda.PaddingIzqPt = estiloCelda.PaddingIzqPt;
                    mcelda.PaddingDerPt = estiloCelda.PaddingDerPt;
                    mcelda.PaddingSupPt = estiloCelda.PaddingSupPt;
                    mcelda.PaddingInfPt = estiloCelda.PaddingInfPt;
                    mcelda.ColorFondo = estiloCelda.ColorFondo;
                }

                // Recorrer hijos de la celda en orden de documento (párrafos y tablas anidadas),
                // conservando OrdenContenido para que un párrafo posterior a una tabla anidada no
                // se dibuje antes de ella (p.ej. Tabla1 dentro de Tabla2 en OrdenDeCompra.fodt).
                foreach (var hijoCelda in celda.Elements())
                {
                    if (hijoCelda.Name == TEXT + "p" || hijoCelda.Name == TEXT + "h")
                    {
                        mcelda.OrdenContenido.Add((false, mcelda.Parrafos.Count));
                        mcelda.Parrafos.Add(LeerParrafo(hijoCelda, estilos, true));

                        foreach (var marco in hijoCelda.Descendants(DRAW + "frame"))
                        {
                            if ((string)marco.Attribute(TEXT + "anchor-type") != "char") continue;
                            if (marco.Ancestors().TakeWhile(a => a != hijoCelda).Any(a => a.Name == TABLE + "table")) continue;
                            mcelda.MarcosFlotantes.Add(LeerCapa(marco, estilos));
                        }
                    }
                    else if (hijoCelda.Name == TABLE + "table")
                    {
                        mcelda.OrdenContenido.Add((true, mcelda.TablasAnidadas.Count));
                        mcelda.TablasAnidadas.Add(LeerTabla(hijoCelda, estilos));
                    }
                }

                mfila.Celdas.Add(mcelda);
            }

            // Borde entre celdas vecinas: en LibreOffice la lÃ­nea se pinta si CUALQUIERA
            // de las dos la declara, y la pinta la de la derecha (su fondo se dibuja
            // despuÃ©s y taparÃ­a la del vecino). El "Debito" del auxiliar de proveedores
            // declara border-left="none" y se quedaba sin lÃ­nea.
            for (var i = 1; i < mfila.Celdas.Count; i++)
            {
                var celdaIzq = mfila.Celdas[i - 1];
                var celdaDer = mfila.Celdas[i];

                if (!celdaIzq.BordeDer && !celdaDer.BordeIzq) continue;

                celdaDer.BordeIzq = true;
                celdaDer.ColorBordeIzq ??= celdaIzq.ColorBordeDer;

                if (celdaDer.GrosorBordePt <= 0) celdaDer.GrosorBordePt = celdaIzq.GrosorBordePt;
            }

            modelo.Filas.Add(mfila);
        }

        // Borde superior redundante en filas espaciadoras vacías (sin bordes laterales ni
        // inferior) inmediatamente después de una fila que ya declara borde inferior en todas
        // sus celdas: en el modelo colapsado de ODF es la misma línea, pero en QuestPDF dibuja
        // un borde doble o deja una línea huérfana arriba de la página siguiente si el salto cae
        // entre ambas filas.
        for (var r = 1; r < modelo.Filas.Count; r++)
        {
            var filaPrev = modelo.Filas[r - 1];
            var filaAct = modelo.Filas[r];

            if (filaPrev.Celdas.Count == 0 || filaAct.Celdas.Count == 0) continue;
            if (!filaPrev.Celdas.All(c => c.BordeInf)) continue;

            var esEspaciadoraConSoloBordeSup = filaAct.Celdas.All(c =>
                c.BordeSup && !c.BordeInf && !c.BordeIzq && !c.BordeDer &&
                c.TablasAnidadas.Count == 0 && c.MarcosFlotantes.Count == 0 &&
                c.Parrafos.All(p => string.IsNullOrEmpty(p.ImagenBase64) &&
                                    !p.Runs.Any(x => !string.IsNullOrWhiteSpace(x.Texto))));

            if (esEspaciadoraConSoloBordeSup)
                foreach (var c in filaAct.Celdas)
                    c.BordeSup = false;
        }

        // Colapso vertical de bordes (table:border-model="collapsing" en ODF/Writer):
        // cuando dos celdas vecinas en vertical comparten una arista (p.ej. border-bottom="1pt"
        // arriba y border-top="0.75pt" abajo), el borde resultante toma el mayor grosor y
        // ambos lados reservan la mitad de ese grosor.
        if (modelo.Filas.Count >= 2)
        {
            var numFilas = modelo.Filas.Count;
            var numCols = Math.Max(
                modelo.ColumnasPt.Count,
                modelo.Filas.Max(f => f.Celdas.Sum(c => Math.Max(c.SpanColumnas, 1))));

            if (numCols > 0)
            {
                var ocupado = new bool[numFilas, numCols];
                var inicioFila = new CeldaModelo[numFilas, numCols];
                var finFila = new CeldaModelo[numFilas, numCols];

                for (var r = 0; r < numFilas; r++)
                {
                    var col = 0;
                    foreach (var c in modelo.Filas[r].Celdas)
                    {
                        while (col < numCols && ocupado[r, col]) col++;
                        var spanC = Math.Max(c.SpanColumnas, 1);
                        var spanR = Math.Max(c.SpanFilas, 1);
                        var rFin = Math.Min(r + spanR - 1, numFilas - 1);
                        var cFin = Math.Min(col + spanC, numCols);

                        for (var rr = r; rr <= rFin; rr++)
                            for (var cc = col; cc < cFin; cc++)
                                ocupado[rr, cc] = true;

                        for (var cc = col; cc < cFin; cc++)
                        {
                            inicioFila[r, cc] = c;
                            finFila[rFin, cc] = c;
                        }

                        col += spanC;
                    }
                }

                for (var r = 1; r < numFilas; r++)
                {
                    for (var cc = 0; cc < numCols; cc++)
                    {
                        var sup = finFila[r - 1, cc];
                        var inf = inicioFila[r, cc];
                        if (sup == null || inf == null || !sup.BordeInf || !inf.BordeSup) continue;

                        var gSup = sup.GrosorBordeInfPt > 0.05 ? sup.GrosorBordeInfPt : sup.GrosorBordePt;
                        var gInf = inf.GrosorBordeSupPt > 0.05 ? inf.GrosorBordeSupPt : inf.GrosorBordePt;
                        var gMax = Math.Max(gSup, gInf);
                        if (gMax <= 0.05) continue;

                        if (gMax > sup.GrosorBordeInfPt) sup.GrosorBordeInfPt = gMax;
                        if (gMax > inf.GrosorBordeSupPt) inf.GrosorBordeSupPt = gMax;
                    }
                }
            }
        }

        return modelo;
    }

    static double APt(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return 0;
        v = v.Trim();

        static double Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

        double pt;
        if (v.EndsWith("cm")) pt = Num(v[..^2]) * CM_PT;
        else if (v.EndsWith("mm")) pt = Num(v[..^2]) * CM_PT / 10;
        else if (v.EndsWith("in")) pt = Num(v[..^2]) * 72;
        else if (v.EndsWith("pt")) pt = Num(v[..^2]);
        else if (v.EndsWith("px")) pt = Num(v[..^2]) * 0.75;
        else pt = Num(v);

        return ATwipsPt(pt);
    }

    static double ACm(string v) => APt(v) / CM_PT;

    // Los .doc convertidos traen viÃ±etas en el Ã¡rea de uso privado (Symbol/Wingdings):
    // U+F0B7 es la "â€¢" del Symbol. Si se pasan tal cual, QuestPDF aborta el documento
    // entero ("1 glyph(s) that are not available"). Se traducen las conocidas y se
    // descartan las demÃ¡s: una viÃ±eta invisible es mejor que no generar el PDF.
    static string LimpiarGlifos(string texto)
    {
        if (string.IsNullOrEmpty(texto)) return texto;

        if (!texto.Any(c => c >= '\uE000' && c <= '\uF8FF')) return texto;

        var sb = new System.Text.StringBuilder(texto.Length);

        foreach (var c in texto)
        {
            switch (c)
            {
                case '\uF0B7': sb.Append((char)0x2022); break;   // Symbol: viñeta
                case '\uF0A7': sb.Append((char)0x00A7); break;   // Symbol: parágrafo
                case '\uF0D8': sb.Append((char)0x2192); break;   // Symbol: flecha
                default:
                    if (c < '\uE000' || c > '\uF8FF') sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }
    static string Limpiar(string s) => s.Replace("\r", " ").Replace("\n", " ");
}


