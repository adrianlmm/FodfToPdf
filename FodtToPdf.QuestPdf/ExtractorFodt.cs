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

    public const double CM_PT = 28.3464567;

    // estilos por nombre: propiedades de texto y de párrafo ya resueltas con herencia
    class Estilo
    {
        public string Fuente, Color, Alineacion;
        public double? TamanoPt;
        public bool Negrita, Cursiva, Subrayado;
        public double? MargenIzqPt, MargenDerPt, MargenSupPt, MargenInfPt, SangriaPt, ColumnWidthCm;
        public string Padre;

        // style:contextual-spacing (null = no declarado; se hereda por la cadena)
        public bool? Contextual;

        // style:min-row-height de un estilo de fila de tabla (null = no declarado)
        public double? AltoMinimoFilaPt;

        // style:row-height de un estilo de fila de tabla (null = no declarado)
        public double? AltoFijoFilaPt;


        // alto de línea (fo:line-height en cm, o en % del tamaño)
        public double? AltoLineaPt, AltoLineaPct;

        // style:text-position (null = no declarado; se hereda por la cadena).
        // Posicion = "super" | "sub" sólo cuando el .fodt usa la palabra clave.
        public double? EscalaPosicion;
        public string Posicion;

        // celda: bordes declarados y paddings
        public bool BordeIzq, BordeDer, BordeSup, BordeInf;
        public string ColorBordeIzq, ColorBordeDer, ColorBordeSup, ColorBordeInf;
        public double GrosorBordePt;
        public string VerticalAlign;
        public double PaddingIzqPt, PaddingDerPt, PaddingSupPt, PaddingInfPt;

        // base de las coordenadas de un marco/imagen (style:vertical-rel del estilo gráfico)
        public string VerticalRel, HorizontalRel;

        // draw:color-mode del estilo gráfico ("watermark" en las marcas de agua)
        public string ModoColor;

        // style:vertical-pos / horizontal-pos ("middle", "center", "from-top"...)
        public string VerticalPos, HorizontalPos;

        // style:tab-stops declaradas por el párrafo (posiciones en pt desde su sangría);
        // null = no declara (LibreOffice usa paradas por omisión cada 1.25 cm)
        public List<double> TabsPt;

        // color de fondo de la celda (fo:background-color); "transparent" o vacío = ninguno
        // en estilos gráficos (family="graphic") guarda el draw:fill-color: es el relleno de
        // las draw:custom-shape, como la barra que va antes de "ORDEN DE COMPRA".
        public string ColorFondo;

        // párrafo: borde y fondo declarados en style:paragraph-properties (el encabezado
        // del contrato CCI va dentro de una caja con borde y relleno gris)
        public string SaltoPagina;
        public bool ParBorde, ParSoloBordeInf;
        public string ParColorBorde, ParFondo;
        public double ParGrosorBordePt, ParPaddingSupPt, ParPaddingInfPt, ParPaddingIzqPt, ParPaddingDerPt;
    }

    public static ModeloPlantilla Extraer(string rutaFodt)
        => Extraer(XDocument.Load(rutaFodt, LoadOptions.PreserveWhitespace), Path.GetFileName(rutaFodt));

    // Sobrecarga para el controlador: el .fodt relleno llega en memoria, no en disco.
    public static ModeloPlantilla Extraer(byte[] archivoFodt, string nombre = "factura.fodt")
        => Extraer(XDocument.Load(new MemoryStream(archivoFodt), LoadOptions.PreserveWhitespace), nombre);

    // trazo de los estilos gráficos (draw:style-name de los draw:line): grosor y color.
    // Los contratos firman con draw:line y sin esto los renglones no se pintaban.
    static readonly Dictionary<string, (double Grosor, string Color)> _trazo = new();

    static readonly Dictionary<string, Dictionary<int, (string Formato, string Plantilla, string Sufijo, string Vineta, double Margen, double Indent)>> _listas = new();
    static double _sangriaPendiente;
    static double _margenPendiente;
    // alto natural de una fuente respecto a su tamaño (ascendente + descendente +
    // interlineado interno). Arial: ~1.15 em; es la base del fo:line-height en %.
    const double AltoNaturalTexto = 1.15;

    // Mismo valor que GeneradorPdf.AltoNaturalFuente: EstiloDe pide el LineHeight dividiendo
    // por él, así que el alto que se le entrega al generador tiene que venir multiplicado.
    const double FactorInterlineadoQuestPdf = 1.05;

    // Alto de línea natural de la fuente, en em: es la base sobre la que LibreOffice aplica
    // el fo:line-height en %. MEDIDO en la tabla hhea de los TTF: Arial y Times 1,1499;
    // Calibri y Carlito 1,2207. El motor usaba 1,15 para todo y las plantillas en Calibri
    // salían con los renglones ~8 % más apretados que LibreOffice.
    static double NaturalDeFuente(string fuente)
    {
        var f = (fuente ?? "").Trim();

        if (f.StartsWith("Calibri", StringComparison.OrdinalIgnoreCase) ||
            f.StartsWith("Carlito", StringComparison.OrdinalIgnoreCase))
            return 1.22;

        return AltoNaturalTexto;
    }

    // fuente del primer run con texto (la que LibreOffice usa para el renglón)
    static string FuenteDelRun(ParrafoModelo p)
        => p.Runs.Select(x => x.Fuente).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

    // Caja de línea de un párrafo VACÍO dentro de una celda: LibreOffice no tiene glifos
    // que posicionar y usa la línea completa de la fuente. MEDIDO en las filas en blanco
    // de las liquidaciones (Calibri 11 pt al 115 %): 15,8 pt de línea y fila de 25,8 con
    // el margen inferior de 0,353 cm del estilo Standard. Contrastado con más casos
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
            var niveles = new Dictionary<int, (string Formato, string Plantilla, string Sufijo, string Vineta, double Margen, double Indent)>();

            foreach (var nivel in estiloLista.Elements())
            {
                var num = int.TryParse((string)nivel.Attribute(TEXT + "level"), out var lv) ? lv : 1;

                var alineacionNivel = nivel.Descendants(STYLE + "list-level-label-alignment").FirstOrDefault();

                var margenNivel = APt((string)alineacionNivel?.Attribute(FO + "margin-left"));
                var indentNivel = APt((string)alineacionNivel?.Attribute(FO + "text-indent"));

                if (nivel.Name == TEXT + "list-level-style-number")
                    niveles[num] = (
                        (string)nivel.Attribute(STYLE + "num-format") ?? "",
                        (string)nivel.Attribute(STYLE + "num-list-format") ?? "",
                        (string)nivel.Attribute(STYLE + "num-suffix") ?? ".",
                        null,
                        margenNivel,
                        indentNivel);
                else if (nivel.Name == TEXT + "list-level-style-bullet")
                    niveles[num] = ("", "", "", (string)nivel.Attribute(TEXT + "bullet-char") ?? "•", margenNivel, indentNivel);
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

        // <style:default-style style:family="paragraph"> es el ÚLTIMO eslabón de la cadena
        // de estilos de párrafo, y muchas plantillas dejan ahí el nombre de la fuente: la
        // orden de compra no lo declara en Standard ni en sus estilos, sólo en el default
        // (Calibri). Sin esto el texto salía en Arial y LibreOffice lo pone en Calibri.
        // Sólo se enlazan los estilos de PÁRRAFO: los de texto (span) no heredan el default
        // de párrafo en ODF y colgarían su tamaño (10 pt) sobre el del párrafo.
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

        // metadatos de secciones (número de columnas y anchos relativos)
        var seccionesPorEstilo = new Dictionary<string, SeccionModelo>();
        foreach (var st in doc.Descendants(STYLE + "style").Where(e => (string)e.Attribute(STYLE + "family") == "section"))
        {
            var nombre = (string)st.Attribute(STYLE + "name") ?? "";
            seccionesPorEstilo[nombre] = LeerSeccion(st, nombre);
        }

        // Las tablas de los reportes se salen del área de texto con fo:margin-left negativo
        // (la bitácora lo hace: -1.501cm). Se guarda el mayor para desplazar el flujo.
        foreach (var st in doc.Descendants(STYLE + "style").Where(e => (string)e.Attribute(STYLE + "family") == "table"))
        {
            var props = st.Element(STYLE + "table-properties");
            var margen = props?.Attribute(FO + "margin-left")?.Value is string ml ? APt(ml) : 0;

            // LibreOffice NO desplaza el flujo por un margen negativo pequeño (la tabla
            // estándar, -0.191 cm): sólo mueve la tabla. Aplicarlo movía todo el texto
            // ~5.8 pt a la izquierda y cambiaba los cortes de renglón (se notaba en el
            // justificado de las cartas de crédito). Se ignoran los menores de 0.5 cm.
            const double umbralDesplazamientoPt = 14.17;   // 0.5 cm
            if (margen <= -umbralDesplazamientoPt && margen < modelo.DesplazamientoIzqPt)
                modelo.DesplazamientoIzqPt = margen;
        }

        var cuerpo = doc.Descendants(OFFICE + "text").FirstOrDefault();
        if (cuerpo == null) return modelo;

        // 1) capas: cualquier draw:frame / draw:custom-shape, en cualquier nivel.
        //    Se guardan también por elemento para saber después qué párrafo las ancla.
        var capaPorElemento = new Dictionary<XElement, CapaModelo>();

        foreach (var el in cuerpo.Descendants().Where(e => e.Name == DRAW + "frame" || e.Name == DRAW + "custom-shape"))
        {
            var capa = LeerCapa(el, estilos);
            modelo.Capas.Add(capa);
            capaPorElemento[el] = capa;
        }

        // 2) flujo: párrafos y tablas en orden. El contenido del .fodt va dentro de
        //    <text:section> (y puede haber varias), así que hay que descender.
        foreach (var hijo in cuerpo.Elements())
            RecorrerFlujo(hijo, modelo, estilos, seccionesPorEstilo, -1, capaPorElemento);

        modelo.Bookmarks = cuerpo.Descendants(TEXT + "bookmark-start")
            .Select(b => (string)b.Attribute(TEXT + "name"))
            .Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();

        // 3) pie de página: vive en el master-page (style:footer), NO en office:text.
        //    Es donde va la línea "Referencia" de la factura.
        var pie = doc.Descendants(STYLE + "footer").FirstOrDefault();

        if (pie != null)
            foreach (var p in pie.Elements(TEXT + "p"))
            {
                var parrafo = LeerParrafo(p, estilos);
                if (parrafo.Runs.Count > 0 && parrafo.Runs.Any(r => !string.IsNullOrWhiteSpace(r.Texto) || r.Slot != null))
                    modelo.Pie.Add(parrafo);
            }

        // 4) encabezado del master-page (style:header). La bitácora de pedidos pone ahí el
        //    título, la ruta y la fecha de entrega. Sólo párrafos con texto: el marco de la
        //    marca de agua de la factura no se dibuja (queda como diferencia conocida).
        var encabezado = doc.Descendants(STYLE + "header").FirstOrDefault();

        if (encabezado != null)
        {
            foreach (var p in encabezado.Elements(TEXT + "p"))
            {
                var parrafo = LeerParrafo(p, estilos);
                if (parrafo.Runs.Count > 0 && parrafo.Runs.Any(r => !string.IsNullOrWhiteSpace(r.Texto) || r.Slot != null))
                    modelo.Encabezado.Add(parrafo);
            }

            // marcos con imagen del encabezado (la marca de agua de la factura)
            foreach (var marco in encabezado.Descendants(DRAW + "frame"))
            {
                var imagen = marco.Element(DRAW + "image");
                var datos = imagen?.Element(OFFICE + "binary-data")?.Value;
                if (imagen == null || string.IsNullOrWhiteSpace(datos)) continue;

                var capa = LeerCapa(marco, estilos);
                capa.ImagenBase64 = datos.Trim();
                capa.ImagenTipo = (string)imagen.Attribute(DRAW + "mime-type") ?? "image/png";
                modelo.MarcasAgua.Add(capa);
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

                modelo.MarcasAguaTexto.Add(new MarcaAguaTextoModelo
                {
                    Texto = texto,
                    XPt = APt((string)forma.Attribute(SVG + "x")),
                    YPt = APt((string)forma.Attribute(SVG + "y")),
                    AnchoPt = APt((string)forma.Attribute(SVG + "width")),
                    AltoPt = APt((string)forma.Attribute(SVG + "height")),
                    Color = estilos.TryGetValue(estiloForma, out var ef) ? ef.ColorFondo : null,
                    Fuente = estilos.TryGetValue(estiloTexto, out var et) ? et.Fuente : null,
                });
            }
        }

        for (var i = 0; i < modelo.Flujo.Count; i++) modelo.Flujo[i].IndiceFlujo = i;

        // Reportes cuyo cuerpo empieza directamente con una tabla y tienen encabezado
        // (Balanza de comprobación): QuestPDF arranca la tabla ~2 pt más arriba que
        // LibreOffice y la primera fila se encima al «Fecha» del encabezado.
        // Se reserva ese aire sólo en ese caso; el encabezado sigue en MargenSupBasePt.
        if (modelo.Encabezado.Count > 0 && modelo.Flujo.Count > 0 && modelo.Flujo[0].Tipo == "tabla")
        {
            modelo.Pagina.MargenSupPt += 2.0;

            // El reporte de compras usa encabezado de 4 líneas de 9 pt; LibreOffice
            // deja la tabla 1.8 pt más abajo que la Balanza (cuyo último renglón es
            // de 10 pt). El cálculo por alto de línea no distingue ese caso.
            var ultimoTamano = modelo.Encabezado[^1].Runs
                .Select(r => r.TamanoPt)
                .FirstOrDefault(t => t > 0);

            if (ultimoTamano > 0 && ultimoTamano <= 9.5)
                modelo.Pagina.MargenSupPt += 1.8;
        }

        return modelo;
    }

    // ancho reservado al marcador: el fo:text-indent negativo del nivel (0.635 cm típico).
    // En las listas con fo:margin-left grande (las obligaciones: 6.985 cm) el marcador va
    // pegado al texto, no al margen de la hoja.
    static double AnchoDeMarca(string estilo, int nivel)
    {
        if (!_listas.TryGetValue(estilo, out var niveles)) return 0;
        if (!niveles.TryGetValue(nivel, out var def)) return 0;

        return def.Indent < -0.5 ? -def.Indent : 0;
    }

    // sangría del bloque: fo:margin-left + fo:text-indent (el renglón sangrado arranca ahí)
    static double MargenDeInciso(string estilo, int nivel)
    {
        if (!_listas.TryGetValue(estilo, out var niveles)) return 0;
        if (!niveles.TryGetValue(nivel, out var def)) return 0;

        return Math.Max(def.Margen + def.Indent, 0);
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
            // en una sección de varias columnas los párrafos vacíos son los espaciadores
            // que el autor puso a mano; su alto debe salir del tamaño de su estilo
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
                p.SangriaColgantePt = _sangriaPendiente;
                p.MargenIncisoPt = _margenPendiente;

                _sangriaPendiente = 0;
                _margenPendiente = 0;

                _marcaPendiente = null;
            }

            if (p.Runs.Count > 0 && p.Runs.Any(r => !string.IsNullOrWhiteSpace(r.Texto) || r.Slot != null))
            {
                modelo.Flujo.Add(new BloqueModelo { Tipo = "texto", Parrafo = p, Seccion = seccion });
            }
            else if (!string.IsNullOrWhiteSpace(p.ImagenBase64) && p.ImagenEnFlujo)
            {
                // Párrafo que sólo lleva una imagen anclada "as-char": el QR del final de
                // la orden de compra. Se manda como bloque de texto (sin runs) para que
                // el generador la pinte; antes caía en "espacio" y la imagen se perdía,
                // porque sólo se dibujaban las imágenes metidas en celdas de tabla.
                modelo.Flujo.Add(new BloqueModelo { Tipo = "texto", Parrafo = p, Seccion = seccion });
            }
            else
            {
                // Los párrafos vacíos SON el espacio vertical del documento: si se
                // descartan, todo el flujo se colapsa contra el margen superior.
                // El alto sale del fo:line-height real del estilo, no de una estimación.
                modelo.Flujo.Add(new BloqueModelo
                {
                    Tipo = "espacio",
                    AltoPt = p.AltoLineaPt,
                    Parrafo = p,
                    Seccion = seccion,
                });
            }

            // Las capas ancladas a un carácter DE ESTE párrafo traen svg:x/svg:y relativos
            // a esa posición, no a la página: se anota qué bloque las ancla para que el
            // generador les sume dónde cae el flujo (ver GeneradorPdf.AnclarAlFlujo).
            var posicion = modelo.Flujo.Count - 1;

            foreach (var dibujo in el.Elements().Where(x => x.Name == DRAW + "frame" || x.Name == DRAW + "custom-shape"))
                if (capaPorElemento != null && capaPorElemento.TryGetValue(dibujo, out var anclada))
                    anclada.BloqueAncla = posicion;
        }
        else if (el.Name == TABLE + "table")
        {
            modelo.Flujo.Add(new BloqueModelo { Tipo = "tabla", Tabla = LeerTabla(el, estilos), Seccion = seccion });

            // Marcos anclados a un párrafo de ESTA tabla (la liquidaciones pone ahí el
            // bloque de firmas): su y es relativo al párrafo ancla, que no sabemos dónde
            // cae; se dibujan en el flujo justo después de la tabla.
            var posicionTabla = modelo.Flujo.Count - 1;

            foreach (var marco in el.Descendants(DRAW + "frame"))
            {
                var anclaMarco = (string)marco.Attribute(TEXT + "anchor-type") ?? "";
                if (anclaMarco != "paragraph" && anclaMarco != "") continue;

                if (capaPorElemento != null && capaPorElemento.TryGetValue(marco, out var capaAnclada))
                {
                    capaAnclada.BloqueAncla = posicionTabla;
                    capaAnclada.AnclaEnTabla = true;

                    // el svg:y se cuenta desde el párrafo ancla, no desde el fin de la tabla
                    var parrafoAncla = marco.Ancestors(TEXT + "p").FirstOrDefault();

                    if (parrafoAncla != null)
                        capaAnclada.AltoParrafoAnclaPt = AltoParrafoAncla(parrafoAncla, estilos);
                }
            }
        }
        else if (el.Name == TEXT + "section")
        {
            // la sección define cuántas columnas tiene el contenido que la sigue
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
                contador = 0;

            foreach (var elemento in el.Elements(TEXT + "list-item"))
            {
                contador++;

                _marcaPendiente = MarcaDeLista(estiloDeLista, 1, contador);
                _sangriaPendiente = AnchoDeMarca(estiloDeLista, 1);
                _margenPendiente = MargenDeInciso(estiloDeLista, 1);

                foreach (var hijo in elemento.Elements())
                    RecorrerFlujo(hijo, modelo, estilos, seccionesPorEstilo, seccion, capaPorElemento);

                _marcaPendiente = null;
                _sangriaPendiente = 0;
            }

            _contadoresLista[estiloDeLista] = contador;
        }
        else if (el.Name == TEXT + "tracked-changes" || el.Name == TEXT + "list-item")
        {
            foreach (var hijo in el.Elements())
                RecorrerFlujo(hijo, modelo, estilos, seccionesPorEstilo, seccion, capaPorElemento);
        }
    }

    // Lee la definición de columnas de una section: cuántas y con qué ancho relativo
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
        }

        if (sec.RelAnchos.Count == 0 && sec.Columnas > 1)
            for (var i = 0; i < sec.Columnas; i++) sec.RelAnchos.Add(1);

        return sec;
    }

    // Alto del TEXTO que lleva el encabezado del master-page. NO cuenta los marcos de
    // imagen: la marca de agua de la factura mide 16 cm y LibreOffice no la usa para
    // separar el cuerpo. 0 si el encabezado no existe o no tiene párrafos.
    static double AltoTextoEncabezado(XDocument doc, Dictionary<string, Estilo> estilos)
    {
        var encabezado = doc.Descendants(STYLE + "header").FirstOrDefault();
        if (encabezado == null) return 0;

        var alto = 0.0;

        foreach (var p in encabezado.Elements(TEXT + "p"))
        {
            var actual = (string)p.Attribute(TEXT + "style-name") ?? "";
            double tamano = 0, lineaPt = 0;
            double? pct = null;
            var vistos = 0;

            while (!string.IsNullOrEmpty(actual) && estilos.TryGetValue(actual, out var e) && vistos++ < 20)
            {
                if (tamano <= 0 && e.TamanoPt is double t && t > 0) tamano = t;
                if (lineaPt <= 0 && e.AltoLineaPt is double l && l > 0) lineaPt = l;
                if (pct == null && e.AltoLineaPct is double pc && pc > 0) pct = pc;
                actual = e.Padre;
            }

            if (lineaPt > 0) { alto += lineaPt; continue; }

            // fo:line-height en % se aplica sobre el alto de línea de la FUENTE, que en
            // Arial/Times ronda 1.15 x el tamaño: 12 pt al 100% = 13.8 pt (medido en la
            // orden de compra contra el PDF de LibreOffice).
            alto += (tamano <= 0 ? 12 : tamano) * (pct ?? 1.15) * 1.15;
        }

        return alto;
    }

    static PaginaModelo LeerPagina(XDocument doc, Dictionary<string, Estilo> estilos)
    {
        var pg = new PaginaModelo();

        // OJO: el .fodt puede traer VARIOS style:page-layout-properties. Los .docx
        // convertidos empiezan con un <style:default-page-layout> vacío (sólo la rejilla)
        // y el bueno —el que usan las páginas de verdad— vive dentro del style:page-layout
        // que referencia el master-page. Tomar el primero dejaba la orden de compra en A4
        // cuando la plantilla es Carta (21.59 x 27.94 cm): el título y todo el encabezado
        // salían corridos. Primero se busca el del master-page; si no aparece, se combinan
        // todas las declaraciones (gana la primera que defina cada medida).
        var props = PaginaDelMasterPage(doc);

        if (props == null)
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

        // El encabezado y el pie OCUPAN espacio aunque no tengan texto: lo que empuja el
        // cuerpo es el alto del encabezado (aquí el margen superior declarado es 0 cm).
        // OJO con style:dynamic-spacing="true": LibreOffice colapsa la separación del
        // encabezado, así que sumarla dejaría el cuerpo ~18 pt más abajo de lo real.
        var header = doc.Descendants(STYLE + "header-style").Elements(STYLE + "header-footer-properties").FirstOrDefault();
        if (header != null)
        {
            var minimo = header.Attribute(FO + "min-height")?.Value is string h ? APt(h) : 0;

            // LibreOffice usa el MAYOR entre la altura mínima y el alto del TEXTO del
            // encabezado: en la orden de compra el encabezado es un párrafo vacío con el
            // estilo "Header" de 12 pt, que ocupa ~13.8 pt aunque no tenga nada escrito.
            // Sin contarlo, todo el cuerpo subía ~11 pt y el título quedaba más alto que
            // en el PDF de LibreOffice. Si el encabezado sólo lleva un marco (la marca de
            // agua de la factura, 16 cm) no hay texto que contar y manda la altura mínima.
            var alto = Math.Max(minimo, AltoTextoEncabezado(doc, estilos));

            var dinamico = ((string)header.Attribute(STYLE + "dynamic-spacing")) == "true";
            var separacion = dinamico ? 0 : header.Attribute(FO + "margin-bottom")?.Value is string mb ? APt(mb) : 0;

            // el encabezado arranca en el margen declarado; el cuerpo, después del alto
            pg.MargenSupBasePt = pg.MargenSupPt;
            pg.AltoEncabezadoPt = alto;

            pg.MargenSupPt += alto + separacion;
        }

        var footer = doc.Descendants(STYLE + "footer-style").Elements(STYLE + "header-footer-properties").FirstOrDefault();
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

    // Devuelve las propiedades del style:page-layout que usa el master-page (el que
    // LibreOffice aplica de verdad). Si el documento no las declara ahí, null.
    static XElement PaginaDelMasterPage(XDocument doc)
    {
        foreach (var master in doc.Descendants(STYLE + "master-page"))
        {
            var nombre = (string)master.Attribute(STYLE + "page-layout-name");
            if (string.IsNullOrEmpty(nombre)) continue;

            var layout = doc.Descendants(STYLE + "page-layout")
                             .FirstOrDefault(pl => (string)pl.Attribute(STYLE + "name") == nombre);

            var props = layout?.Element(STYLE + "page-layout-properties");
            if (props != null && props.HasAttributes) return props;
        }

        return null;
    }

    // style:text-position: "super 58%", "sub 58%" o "-6% 100%". El primer valor es el
    // desplazamiento de la línea base (en % del tamaño) y el segundo, opcional, el tamaño
    // relativo. "super" equivale a "33% 58%" y "sub" a "-33% 58%" (DFLT_ESC_AUTO_SUPER /
    // DFLT_ESC_AUTO_SUB de LibreOffice); sin porcentaje, el tamaño relativo es 58 %.
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

        // los 370 "-6% 100%" de los contratos caen aquí: escala 100 % y sólo se mueve la
        // línea base 6 %, un desplazamiento que QuestPDF no sabe expresar (mínimo ~33 %).
        return (partes.Length > 1 ? Pct(partes[1]) ?? 100 : 100, null);
    }

    // Alto que ocupa el párrafo que ancla un marco dibujado en el flujo tras su tabla
    // (las firmas de la liquidaciones). Es el mismo cálculo que la celda hace para un
    // párrafo VACÍO: línea del estilo + sus márgenes. Si el párrafo trae texto se
    // aproxima igual (la diferencia sería de una línea), que es lo que basta para
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

        // style:text-position se interpreta una sola vez: escala y posición vienen juntas
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
            Negrita = ((string)tp?.Attribute(FO + "font-weight")) is string w && (w == "bold" || w == "600" || w == "700" || w == "800" || w == "900"),
            Cursiva = ((string)tp?.Attribute(FO + "font-style")) == "italic",
            Subrayado = ((string)tp?.Attribute(STYLE + "text-underline-style")) is string u && u != "none" && u != "",
            Color = (string)tp?.Attribute(FO + "color"),
            Alineacion = (string)pp?.Attribute(FO + "text-align"),
            MargenIzqPt = pp?.Attribute(FO + "margin-left")?.Value is string mi ? APt(mi) : null,
            MargenDerPt = pp?.Attribute(FO + "margin-right")?.Value is string md ? APt(md) : null,
            MargenSupPt = pp?.Attribute(FO + "margin-top")?.Value is string ms ? APt(ms) : null,
            MargenInfPt = pp?.Attribute(FO + "margin-bottom")?.Value is string mbo ? APt(mbo) : null,
            SangriaPt = pp?.Attribute(FO + "text-indent")?.Value is string ti ? APt(ti) : null,

            // §20.255: si es "true", el margin-bottom de este párrafo y el margin-top del
            // siguiente se ignoran cuando además comparten estilo y área de contenido
            Contextual = ((string)pp?.Attribute(STYLE + "contextual-spacing")) is string cs
                ? cs == "true" : (bool?)null,
            ColumnWidthCm = cp?.Attribute(STYLE + "column-width")?.Value is string cw ? ACm(cw) : null,
            AltoMinimoFilaPt = fila?.Attribute(STYLE + "min-row-height")?.Value is string amh ? APt(amh) : null,
            AltoFijoFilaPt = fila?.Attribute(STYLE + "row-height")?.Value is string rh ? APt(rh) : null,

            // alto de línea real del párrafo (fo:line-height puede venir en cm o en %)
            AltoLineaPt = pp?.Attribute(FO + "line-height")?.Value is string lh && !lh.EndsWith("%") ? APt(lh) : null,
            AltoLineaPct = pp?.Attribute(FO + "line-height")?.Value is string lhp && lhp.EndsWith("%")
                ? (double.TryParse(lhp[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) ? pct / 100 : (double?)null)
                : null,

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
            GrosorBordePt = GrosorBorde(bordeGeneral ?? (string)cel?.Attribute(FO + "border-top") ?? (string)cel?.Attribute(FO + "border-bottom")),

            PaddingIzqPt = cel?.Attribute(FO + "padding-left")?.Value is string pi ? APt(pi)
                         : cel?.Attribute(FO + "padding")?.Value is string pg2 ? APt(pg2) : 0,
            PaddingDerPt = cel?.Attribute(FO + "padding-right")?.Value is string pdr ? APt(pdr)
                         : cel?.Attribute(FO + "padding")?.Value is string pg3 ? APt(pg3) : 0,
            PaddingSupPt = cel?.Attribute(FO + "padding-top")?.Value is string psu ? APt(psu)
                         : cel?.Attribute(FO + "padding")?.Value is string pg4 ? APt(pg4) : 0,
            PaddingInfPt = cel?.Attribute(FO + "padding-bottom")?.Value is string pin ? APt(pin)
                         : cel?.Attribute(FO + "padding")?.Value is string pg5 ? APt(pg5) : 0,

            // borde y fondo del PÁRRAFO (style:paragraph-properties)
            SaltoPagina = (string)pp?.Attribute(FO + "break-before"),
            ParBorde = HayBorde((string)pp?.Attribute(FO + "border") ?? (string)pp?.Attribute(FO + "border-top")),

            // sólo el renglón de abajo (P24 de la solicitud): no hay borde ni arriba ni a los lados
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

            // color de fondo de la celda (los títulos de las tablas de los reportes lo llevan)
            TabsPt = paradas != null && paradas.Count > 0
                ? paradas.Select(x => APt((string)x.Attribute(STYLE + "position"))).Where(x => x > 0).ToList()
                : null,

            VerticalRel = (string)gp?.Attribute(STYLE + "vertical-rel"),
            HorizontalRel = (string)gp?.Attribute(STYLE + "horizontal-rel"),
            ModoColor = (string)gp?.Attribute(DRAW + "color-mode"),
            VerticalPos = (string)gp?.Attribute(STYLE + "vertical-pos"),
            HorizontalPos = (string)gp?.Attribute(STYLE + "horizontal-pos"),

            ColorFondo = (string)cel?.Attribute(FO + "background-color") is string fondo &&
                         fondo != "transparent" && fondo.StartsWith("#") ? fondo
                       : (string)st.Element(STYLE + "graphic-properties")?.Attribute(DRAW + "fill-color") is string relleno &&
                         relleno.StartsWith("#") ? relleno : null,
        };
    }

    // aplica una cadena de estilos (de raíz a hijo: gana el más específico)
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

            // style:text-position="super 58%": se acumulan escala y elevación y se aplican
            // al final de Resolver, porque el tamaño puede venir de otro estilo de la cadena.
            if (e.EscalaPosicion.HasValue) run.EscalaPosicion = e.EscalaPosicion.Value;
            if (e.Posicion != null) run.Posicion = e.Posicion;
            if (e.Color != null) run.Color = e.Color;
            if (e.Negrita) run.Negrita = true;
            if (e.Cursiva) run.Cursiva = true;
            if (e.Subrayado) run.Subrayado = true;
        }
    }

    // Resuelve el formato de un run.
    // IMPORTANTE: primero el estilo del PÁRRAFO y después el del SPAN. Un span suele
    // traer sólo algún atributo (o ninguno): si se resuelve aislado, el tamaño de fuente
    // se pierde y cae al valor por defecto (10 pt) en vez de heredar los 6.5 pt del párrafo.
    // Los SPAN anidados también se acumulan: en la orden de compra el correo va como
    // <text:a style="Internet_20_link"><span "Internet_20_link"><span "T11">…</text:a>.
    // T11 sólo declara Arial 7 pt negrita, así que el azul y el subrayado del enlace
    // viven en el span de FUERA; resolver únicamente el más interno los perdía y el
    // correo salía en negro y sin subrayar (parecía texto normal, no un hipervínculo).
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

        // style:text-position: LibreOffice dibuja "super 58%" al 58 % del tamaño declarado
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

    // Estilo "común" del párrafo para la comparación de §20.255: si text:style-name
    // apunta a un estilo automático, se toma su style:parent-style-name.
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

    // parada por omisión de LibreOffice cuando el estilo no declara ninguna
    const double ParadaPorOmisionPt = 1.25 * CM_PT;

    static List<double> TabuladoresDe(Dictionary<string, Estilo> estilos, string nombre)
    {
        var actual = nombre;

        for (var vistos = 0; !string.IsNullOrEmpty(actual) && vistos < 20; vistos++)
        {
            if (!estilos.TryGetValue(actual, out var e)) return null;
            if (e.TabsPt != null && e.TabsPt.Count > 0) return e.TabsPt;
            actual = e.Padre;
        }

        return null;
    }

    static double SiguienteParada(List<double> paradas, double x)
    {
        if (paradas != null)
            foreach (var p in paradas)
                if (p > x + 0.5) return p;

        // por omisión (y después de la última declarada): múltiplos de 1.25 cm
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
        Slot = r.Slot,
        Hipervinculo = r.Hipervinculo,
        Posicion = r.Posicion,
        EscalaPosicion = r.EscalaPosicion,
    };

    // Cambia cada tabulador por los espacios que lo llevan a la parada. La x se lleva
    // midiendo el texto con Skia (mismo motor que la paginación propia).
    static void ExpandirTabuladores(List<RunModelo> runs, List<double> paradas)
    {
        if (!runs.Any(r => r.Texto != null && r.Texto.Contains('	'))) return;

        var salida = new List<RunModelo>(runs.Count + 6);
        var x = 0.0;

        static double Ancho(string texto, RunModelo r)
            => PaginarPropio.Medir(texto, string.IsNullOrEmpty(r.Fuente) ? "Arial" : r.Fuente,
                                   r.TamanoPt, r.Negrita);

        foreach (var r in runs)
        {
            if (r.Texto == null || !r.Texto.Contains('	')) { salida.Add(r); x += Ancho(r.Texto ?? "", r); continue; }

            var partes = r.Texto.Split('	');

            for (var k = 0; k < partes.Length; k++)
            {
                if (k > 0)
                {
                    var destino = SiguienteParada(paradas, x);
                    var espacio = Ancho(" ", r);
                    var n = espacio > 0.1 ? (int)Math.Round((destino - x) / espacio) : 1;

                    if (n < 1) n = 1;

                    salida.Add(CopiaRun(r, new string(' ', n)));
                    x += n * espacio;
                }

                if (partes[k].Length == 0) continue;

                salida.Add(CopiaRun(r, partes[k]));
                x += Ancho(partes[k], r);
            }
        }

        runs.Clear();
        runs.AddRange(salida);
    }

    static ParrafoModelo LeerParrafo(XElement p, Dictionary<string, Estilo> estilos, bool enCelda = false,
                                     bool enColumnas = false)
    {
        var modelo = new ParrafoModelo { EsTitulo = p.Name == TEXT + "h" };
        var nombreEstilo = (string)p.Attribute(TEXT + "style-name") ?? "";

        // Alineación, márgenes y sangría SE HEREDAN por la cadena de estilos: los estilos
        // automáticos de LibreOffice normalmente sólo declaran lo que cambia y el margen
        // vive en el padre (el párrafo vacío P19 no declara nada y hereda el
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

        // caja del párrafo: borde, fondo y paddings
        modelo.BordeParrafo = Cadena<bool>(estilos, nombreEstilo, x => x.ParBorde ? (bool?)true : null) ?? false;
        modelo.BordeInferiorParrafo = Cadena<bool>(estilos, nombreEstilo, x => x.ParSoloBordeInf ? (bool?)true : null) ?? false;
        modelo.ColorBordeParrafo = CadenaTexto(estilos, nombreEstilo, x => x.ParColorBorde);
        modelo.GrosorBordeParrafoPt = Cadena<double>(estilos, nombreEstilo, x => x.ParGrosorBordePt > 0 ? (double?)x.ParGrosorBordePt : null) ?? 0;
        modelo.ColorFondoParrafo = CadenaTexto(estilos, nombreEstilo, x => x.ParFondo);
        modelo.PaddingSupParrafoPt = Cadena<double>(estilos, nombreEstilo, x => (double?)x.ParPaddingSupPt) ?? 0;
        modelo.PaddingInfParrafoPt = Cadena<double>(estilos, nombreEstilo, x => (double?)x.ParPaddingInfPt) ?? 0;
        modelo.PaddingIzqParrafoPt = Cadena<double>(estilos, nombreEstilo, x => (double?)x.ParPaddingIzqPt) ?? 0;
        modelo.PaddingDerParrafoPt = Cadena<double>(estilos, nombreEstilo, x => (double?)x.ParPaddingDerPt) ?? 0;

        // §20.255: contextual-spacing y el estilo "común" (el padre, si el estilo es
        // automático) hacen falta para saber si se colapsa el espacio entre párrafos.
        modelo.EstiloComun = EstiloComun(estilos, nombreEstilo);
        modelo.Contextual = ContextualDe(estilos, nombreEstilo);

        AgregarRuns(p, new List<string>(), modelo.Runs, estilos, nombreEstilo);

        // <text:tab/> avanza hasta la siguiente parada de tabulador. QuestPDF no tiene
        // tabuladores: se convierten en espacios contados (el avance lo da la parada, no
        // el número de espacios). Sin esto la segunda línea del bloque «Ubicación» quedaba
        // 35 pt a la izquierda (el ancho de la parada por omisión).
        ExpandirTabuladores(modelo.Runs, TabuladoresDe(estilos, nombreEstilo));

        // alto de línea: fo:line-height del estilo (en cm o en %), con herencia
        var actual = nombreEstilo;
        var vistos = 0;

        while (!string.IsNullOrEmpty(actual) && estilos.TryGetValue(actual, out var estiloCadena) && vistos++ < 20)
        {
            if (modelo.TamanoBasePt <= 0 && estiloCadena.TamanoPt is double tamBase && tamBase > 0)
                modelo.TamanoBasePt = tamBase;

            if (estiloCadena.AltoLineaPt is double alto && alto > 0) { modelo.AltoLineaPt = alto; break; }
            if (estiloCadena.AltoLineaPct is double pct && pct > 0)
            {
                var tamano = modelo.Runs.Select(r => r.TamanoPt).FirstOrDefault(t => t > 0);

                // El porcentaje se aplica sobre el ALTO NATURAL de la fuente, no sobre el
                // tamaño de letra: en LibreOffice es AttrSetToLineHeight() ->
                // GetTextHeight() (ascendente + descendente + interlineado interno,
                // sw/source/core/txtnode/swfont.cxx). Con Arial son ~1.15 em.
                // En las CELDAS se sigue con el factor anterior: ahí el alto lo gobiernan
                // las reglas de tabla y cambiar el interlineado movía los reportes ya validados.
                //
                // Excepción MEDIDA: un párrafo VACÍO dentro de una celda. Ahí no hay texto
                // que posicionar y LibreOffice arma la caja completa de la línea
                // (AltoNaturalFuenteCelda), con el tamaño del ESTILO y no con el respaldo
                // de 10 pt: la fila en blanco de las liquidaciones daba 11,5 pt de línea y
                // LibreOffice mide 15,8 (fila de 25,8 con el margen inferior de 0,353 cm
                // del estilo Standard).
                if (tamano > 0)
                {
                    // En el FLUJO se usa el alto natural de la FUENTE del run (Calibri 1,22 em,
                    // Arial 1,15) y se multiplica por AltoNaturalFuente (1,05) porque EstiloDe
                    // divide por ese factor al pedirle el interlineado a QuestPDF. Antes se
                    // usaba 1,15 para todo: el aviso de privacidad (Calibri 10 pt al 115 %)
                    // daba 12,6 pt por renglón contra 14,0 de LibreOffice y salía en 2 hojas
                    // en vez de 3.
                    modelo.AltoLineaPt = enCelda
                        ? tamano * pct
                        : tamano * NaturalDeFuente(FuenteDelRun(modelo)) * pct * FactorInterlineadoQuestPdf;
                }
                else if (enCelda)
                {
                    var tamEstilo = modelo.TamanoBasePt > 0 ? modelo.TamanoBasePt : 10;
                    modelo.AltoLineaPt = tamEstilo * AltoNaturalFuenteCelda * pct;
                }
                else
                {
                    modelo.AltoLineaPt = 10 * AltoNaturalTexto * pct;
                }

                break;
            }
            actual = estiloCadena.Padre;
        }

        if (modelo.AltoLineaPt <= 0)
        {
            var tamano = modelo.Runs.Select(r => r.TamanoPt).FirstOrDefault(t => t > 0);

            // MEDIDO contra LibreOffice (caso controlado de 5 párrafos vacíos): un párrafo
            // vacío sin fo:line-height mide el alto natural de su fuente, 1,15 x tamaño
            // (7 pt -> 8,04 pt; 12 pt -> 13,8 pt). Aquí sólo se corrige en las secciones
            // de varias columnas: son los espaciadores del encabezado de la factura y de
            // la solicitud. En el resto se deja el 10 pt de siempre porque de esos altos
            // dependen los cortes de página ya validados (el CCI cambiaba de 8 a 9 hojas).
            if (tamano <= 0 && enColumnas) tamano = modelo.TamanoBasePt;

            modelo.AltoLineaPt = (tamano <= 0 ? 10 : tamano) * (enColumnas ? AltoNaturalTexto : 1.2);
        }

        // Un draw:frame anclado "as-char" o "char" ocupa espacio en la línea: la línea
        // crece hasta el alto del objeto (así el QR empuja el texto que viene debajo).
        // Si además trae imagen, se guarda aquí para dibujarla DENTRO del flujo: su
        // posición depende del texto de arriba y no se puede fijar en una capa absoluta.
        foreach (var marco in p.Descendants().Where(x => x.Name == DRAW + "frame" || x.Name == DRAW + "custom-shape"))
        {
            var ancla = (string)marco.Attribute(TEXT + "anchor-type") ?? "";
            if (ancla != "as-char" && ancla != "char") continue;

            var altoObjeto = APt((string)marco.Attribute(SVG + "height"));

            // Sólo el ancla "as-char" ocupa espacio en la línea. Los de ancla "char" se
            // dibujan como capa con posición propia (LibreOffice hace lo mismo: el marco
            // «SOLICITUD DE CREDITO» de la solicitud de crédito PF va en svg:x=-6.7 cm,
            // fuera de la celda). Reservarles alto dejaba esa fila ~33 pt más alta.
            if (ancla == "as-char" && altoObjeto > modelo.AltoObjetoPt)
                modelo.AltoObjetoPt = altoObjeto;

            var imagen = marco.Descendants(DRAW + "image").FirstOrDefault();
            var binario = imagen?.Element(OFFICE + "binary-data")?.Value;

            // sólo "as-char" se dibuja en el flujo: las de ancla "char" ya salen como
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
        // Van anclados al carácter, dentro de un párrafo, con coordenadas propias.
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

        // Última pasada: quitar los glifos del área privada (viñetas Symbol) que hacen
        // abortar a QuestPDF. Se hace aquí para cubrir también los textos que se pegan
        // directamente a un run (text:s, text:tab, marcas de combinación).
        foreach (var runParrafo in modelo.Runs)
            runParrafo.Texto = LimpiarGlifos(runParrafo.Texto);

        return modelo;
    }

    // estilosSpan: estilos de carácter que ENVUELVEN al contenido, de fuera hacia dentro.
    // Se acumulan al bajar por los <text:span> para que un span interno herede lo que
    // declara el de fuera (si no, el enlace del correo perdía color y subrayado).
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
                    // hipervínculo.
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
                    // Campos de combinación de los reportes: al convertir el .doc a .fodt
                    // quedan como <text:database-display> con el valor YA calculado como
                    // texto («Titulo», «Fecha», «Empresa»...). No se leían, así que en el
                    // Auxiliar de Proveedores el título, la fecha y la empresa no salían y
                    // el único renglón que quedaba ("Página: 1") subía de más.
                    // Las marcas de región («TableStart:Balanza» / «TableEnd:Balanza»)
                    // TAMBIÉN se pintan: LibreOffice las imprime tal cual en el documento
                    // de plantilla. En los documentos ya combinados no aparecen —
                    // ExecuteRegion consume las regiones —, así que no ensucian el PDF final.
                    var valor = Limpiar(el.Value);

                    if (valor.Length > 0)
                        runs.Add(Resolver(estilos, estilosSpan, valor, null, estiloParrafo));
                }
                else if (el.Name == TEXT + "date" || el.Name == TEXT + "time")
                {
                    // text:date / text:time: LibreOffice guarda el valor ya formateado como
                    // contenido (el "12/11/2026 14:01 O11/p11" del pie de la bitácora).
                    // No se leía, así que la línea salía vacía.
                    var valor = Limpiar(el.Value);
                    if (valor.Length > 0) runs.Add(Resolver(estilos, estilosSpan, valor, null, estiloParrafo));
                }
                else if (el.Name == TEXT + "page-number" || el.Name == TEXT + "page-count")
                {
                    // text:page-number / text:page-count: LibreOffice deja el valor ya
                    // calculado como texto del elemento (el "1" y el "1" de "Página 1/1").
                    // No se leían, así que el campo salía vacío y sólo quedaba la "/".
                    var valor = Limpiar(el.Value);
                    if (valor.Length > 0) runs.Add(Resolver(estilos, estilosSpan, valor, null, estiloParrafo));
                }
                // draw:* dentro de un párrafo: ya se tomó como capa; no se mezcla con el texto
            }
        }
    }

    static bool MismoEstilo(RunModelo a, RunModelo b) =>
        a.Fuente == b.Fuente && a.TamanoPt == b.TamanoPt && a.Negrita == b.Negrita &&
        a.Cursiva == b.Cursiva && a.Subrayado == b.Subrayado && a.Color == b.Color &&
        a.Hipervinculo == b.Hipervinculo;

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

        // relleno de la caja (draw:fill-color del estilo gráfico): la barra de color que va
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
        }

        // Sólo cuenta lo que está DENTRO de la caja: el frame puede vivir en la celda de
        // otra tabla (la liquidaciones mete el bloque de firmas en la tabla de fichas) y
        // esa tabla de fuera no es suya.
        bool DentroDeTablaDeLaCapa(XElement nodo) =>
            nodo.Ancestors().TakeWhile(a => a != el).Any(a => a.Name == TABLE + "table");

        foreach (var p in el.Descendants(TEXT + "p"))
        {
            if (DentroDeTablaDeLaCapa(p)) continue;   // los pinta la tabla, no sueltos
            var parrafoCapa = LeerParrafo(p, estilos);

            // Los draw:line dentro de una caja con posición fija (Pagaré, formatos de
            // solicitud) no se dibujan: la caja tiene alto fijo y agregar el renglón
            // revienta el acomodo ("conflicting size constraints").
            parrafoCapa.Lineas.Clear();

            capa.Parrafos.Add(parrafoCapa);
        }

        foreach (var t in el.Descendants(TABLE + "table"))
        {
            if (DentroDeTablaDeLaCapa(t)) continue;   // tablas anidadas dentro de otra tabla de la capa
            capa.Tablas.Add(LeerTabla(t, estilos));
        }

        // alto aproximado del contenido: cada párrafo con su interlineado y márgenes
        foreach (var parrafoCapa in capa.Parrafos)
            capa.AltoContenidoPt += Math.Max(parrafoCapa.AltoLineaPt, 8) + parrafoCapa.MargenSupPt + parrafoCapa.MargenInfPt;

        foreach (var tablaCapa2 in capa.Tablas)
            capa.AltoContenidoPt += tablaCapa2.Filas.Count * 12;

        // imagen embebida: en un .fodt (plano) viene como
        //   <draw:image draw:mime-type="image/jpeg"><office:binary-data>BASE64</office:binary-data></draw:image>
        // (también se admite el formato data:image/...;base64 en xlink:href)
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

        // margen izquierdo propio de la tabla (normalmente negativo: se sale del área de texto)
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

        // table:align del estilo (o del propio elemento): las tablas más angostas que el
        // área de texto pueden ir al centro (la de sueldo del contrato CCI).
        modelo.AlineacionTabla = (string)estiloTabla?.Element(STYLE + "table-properties")?.Attribute(TABLE + "align")
                                 ?? (string)tabla.Attribute(TABLE + "align")
                                 ?? "";

        foreach (var col in tabla.Elements(TABLE + "table-column"))
        {
            var nombre = (string)col.Attribute(TABLE + "style-name") ?? "";
            var anchoCm = estilos.TryGetValue(nombre, out var e) ? e.ColumnWidthCm : null;

            // una columna puede declararse repetida (table:number-columns-repeated="2"):
            // si se cuenta una sola, el contenido no tiene dónde repartirse
            var repeticiones = int.TryParse((string)col.Attribute(TABLE + "number-columns-repeated"), out var rep) ? rep : 1;

            for (var k = 0; k < Math.Max(repeticiones, 1); k++)
                modelo.ColumnasPt.Add((anchoCm ?? 2.0) * CM_PT);
        }

        foreach (var fila in tabla.Elements(TABLE + "table-row"))
        {
            var mfila = new FilaModelo();

            // alto mínimo declarado por el estilo de la fila (style:min-row-height): en la
            // bitácora de pedidos la fila del producto pide 1.335cm y es lo que separa un
            // grupo del siguiente. Sin esto las filas quedaban de una sola línea de texto.
            var nombreEstiloFila = (string)fila.Attribute(TABLE + "style-name") ?? "";

            if (estilos.TryGetValue(nombreEstiloFila, out var estiloFila) && estiloFila.AltoMinimoFilaPt is double altoFila)
                mfila.AltoMinimoPt = altoFila;

            if (estilos.TryGetValue(nombreEstiloFila, out var estiloFila2) && estiloFila2.AltoFijoFilaPt is double altoFijo)
                mfila.AltoFijoPt = altoFijo;
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
                    mcelda.VerticalAlign = estiloCelda.VerticalAlign ?? "";
                    mcelda.PaddingIzqPt = estiloCelda.PaddingIzqPt;
                    mcelda.PaddingDerPt = estiloCelda.PaddingDerPt;
                    mcelda.PaddingSupPt = estiloCelda.PaddingSupPt;
                    mcelda.PaddingInfPt = estiloCelda.PaddingInfPt;
                    mcelda.ColorFondo = estiloCelda.ColorFondo;
                }

                // Las celdas pueden usar text:p o text:h. El comprobante de traslado
                // pone los encabezados de la tabla como text:h; si sólo se leían text:p,
                // "Clave del producto...", "Cantidad", "Importe", etc. desaparecían.
                foreach (var p in celda.Elements().Where(x => x.Name == TEXT + "p" || x.Name == TEXT + "h"))
                    mcelda.Parrafos.Add(LeerParrafo(p, estilos, true));

                // tablas anidadas dentro de la celda (la cabecera de columnas de estos
                // reportes va así: si no se recorren, esa parte no aparece en el PDF)
                foreach (var anidada in celda.Elements(TABLE + "table"))
                    mcelda.TablasAnidadas.Add(LeerTabla(anidada, estilos));

                mfila.Celdas.Add(mcelda);
            }

            // Borde entre celdas vecinas: en LibreOffice la línea se pinta si CUALQUIERA
            // de las dos la declara, y la pinta la de la derecha (su fondo se dibuja
            // después y taparía la del vecino). El "Debito" del auxiliar de proveedores
            // declara border-left="none" y se quedaba sin línea.
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

        return modelo;
    }

    static double APt(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return 0;
        v = v.Trim();

        static double Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

        if (v.EndsWith("cm")) return Num(v[..^2]) * CM_PT;
        if (v.EndsWith("mm")) return Num(v[..^2]) * CM_PT / 10;
        if (v.EndsWith("in")) return Num(v[..^2]) * 72;
        if (v.EndsWith("pt")) return Num(v[..^2]);
        if (v.EndsWith("px")) return Num(v[..^2]) * 0.75;
        return Num(v);
    }

    static double ACm(string v) => APt(v) / CM_PT;

    // Los .doc convertidos traen viñetas en el área de uso privado (Symbol/Wingdings):
    // U+F0B7 es la "•" del Symbol. Si se pasan tal cual, QuestPDF aborta el documento
    // entero ("1 glyph(s) that are not available"). Se traducen las conocidas y se
    // descartan las demás: una viñeta invisible es mejor que no generar el PDF.
    static string LimpiarGlifos(string texto)
    {
        if (string.IsNullOrEmpty(texto)) return texto;

        if (!texto.Any(c => c >= '\uE000' && c <= '\uF8FF')) return texto;

        var sb = new System.Text.StringBuilder(texto.Length);

        foreach (var c in texto)
        {
            switch (c)
            {
                case '\uF0B7': sb.Append('•'); break;   // Symbol: viñeta
                case '\uF0A7': sb.Append('§'); break;   // Symbol: parágrafo
                case '\uF0D8': sb.Append('→'); break;   // Symbol: flecha
                default:
                    if (c < '\uE000' || c > '\uF8FF') sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }
    static string Limpiar(string s) => s.Replace("\r", " ").Replace("\n", " ");
}
