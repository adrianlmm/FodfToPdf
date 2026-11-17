using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace FodtToPdf
{
    /// <summary>
    /// Fills a flat LibreOffice .fodt template without Aspose or unzipping: a .fodt is a
    /// single XML document.
    ///
    /// Supported template features:
    ///  - Simple fields stored between text:bookmark-start and text:bookmark-end.
    ///  - Repeating table regions delimited by TableStart:/TableEnd: markers, either as
    ///    text:database-display fields or as plain text markers.
    ///  - Images inserted as embedded draw:frame elements with base64 data.
    /// </summary>
    public class FodtTemplate : IPrintTemplate
    {
        private static readonly XNamespace OFFICE = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
        private static readonly XNamespace TEXT = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
        private static readonly XNamespace TABLE = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
        private static readonly XNamespace DRAW = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
        private static readonly XNamespace SVG = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";
        private static readonly XNamespace STYLE = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";

        private const string PrefijoStart = "TableStart:";
        private const string PrefijoEnd = "TableEnd:";

        private readonly XDocument _documento;

        private FodtTemplate(XDocument documento)
        {
            _documento = documento;
        }

        /// <summary>Loads a .fodt template from a file.</summary>
        public static FodtTemplate Load(string rutaArchivo)
        {
            if (string.IsNullOrWhiteSpace(rutaArchivo) || !File.Exists(rutaArchivo))
            {
                throw new FileNotFoundException("No se encontró la plantilla .fodt", rutaArchivo);
            }

            return Load(File.ReadAllBytes(rutaArchivo));
        }

        /// <summary>Loads a .fodt template from memory.</summary>
        public static FodtTemplate Load(byte[] contenido)
        {
            if (contenido == null || contenido.Length == 0)
            {
                throw new ArgumentException("La plantilla .fodt está vacía", nameof(contenido));
            }

            using (var pStream = new MemoryStream(contenido))
            {
                // PreserveWhitespace: el .fodt trae el espaciado de LibreOffice y hay que respetarlo.
                var pDocumento = XDocument.Load(pStream, LoadOptions.PreserveWhitespace);
                return new FodtTemplate(pDocumento);
            }
        }

        /// <summary>Returns the filled document as a .fodt byte array.</summary>
        public byte[] ToBytes()
        {
            var pAjustes = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                Indent = false,
                OmitXmlDeclaration = false
            };

            using (var pStream = new MemoryStream())
            {
                using (var pEscritor = XmlWriter.Create(pStream, pAjustes))
                {
                    _documento.Save(pEscritor);
                }

                return pStream.ToArray();
            }
        }

        // ------------------------------------------------------------------
        //  Clone + AppendDoc (para el patrón maestro + detalle + total)
        // ------------------------------------------------------------------

        /// <summary>Creates an independent copy of the template.</summary>
        public FodtTemplate Clone()
        {
            // El constructor de copia de XDocument hace un clon profundo del árbol.
            return new FodtTemplate(new XDocument(_documento));
        }

        /// <summary>Appends another .fodt template to the end of this template.</summary>
        public void AppendDoc(FodtTemplate otro)
        {
            if (otro == null)
            {
                throw new ArgumentNullException(nameof(otro));
            }

            var pTextoDestino = ObtenerTexto(_documento.Root);
            var pTextoOrigen = ObtenerTexto(otro._documento.Root);

            if (pTextoDestino == null || pTextoOrigen == null)
            {
                throw new InvalidOperationException("AppendDoc requiere que ambas plantillas tengan office:body/office:text.");
            }

            // Los estilos automáticos del origen chocan con los del destino (los dos tienen
            // "Tabla1.1", "Tabla1.A1", "P1", "T1"...). Si se pegan sin renombrar, las filas del
            // detalle toman el alto/ancho/alineación del maestro, el texto se parte y el fondo de
            // la fila siguiente lo tapa: el detalle "desaparece". Se renombran por bloque.
            var pMapaEstilos = RenombrarEstilosDeOrigen(otro._documento.Root);
            AgregarEstilosRenombrados(otro._documento.Root, pMapaEstilos);

            // El maestro (destino) también termina en un párrafo "Standard" vacío. Sin quitarlo,
            // el hueco aparecía sólo antes del PRIMER grupo (los demás ya van pegados a su tabla).
            while (pTextoDestino.Nodes().LastOrDefault() is XNode pUltimo && EsParrafoVacio(pUltimo))
            {
                pUltimo.Remove();
            }

            // Los nodos se clonan antes de añadirlos: un nodo no puede colgar de dos árboles.
            // Se saltan los nodos de nivel documento (office:forms, text:sequence-decls): ya los
            // tiene el destino y repetirlos alarga el XML sin aportar nada.
            var pNodos = pTextoOrigen.Nodes()
                .Where(n => !(n is XElement e && (e.Name == OFFICE + "forms" || e.Name == TEXT + "sequence-decls")))
                .ToList();

            // Las plantillas de Word traen párrafos vacíos al principio y al final. Pegados uno
            // por proveedor dejaban un hueco enorme entre grupos, así que se recortan.
            while (pNodos.Count > 0 && EsParrafoVacio(pNodos[0]))
            {
                pNodos.RemoveAt(0);
            }

            while (pNodos.Count > 0 && EsParrafoVacio(pNodos[pNodos.Count - 1]))
            {
                pNodos.RemoveAt(pNodos.Count - 1);
            }

            foreach (var pNodo in pNodos)
            {
                var pClon = ClonarNodo(pNodo);

                // ODF exige que los nombres de tabla sean únicos dentro del documento.
                RenombrarTablas(pClon);

                // Y las referencias a estilos tienen que apuntar a los estilos renombrados.
                AplicarMapaEstilos(pClon, pMapaEstilos);

                pTextoDestino.Add(pClon);
            }
        }

        /// <summary>
        /// Un párrafo sin texto ni objetos (sólo <text:s/> o espacios) que las plantillas de Word
        /// dejan como relleno y que al pegar bloques se convierte en un hueco visible.
        /// </summary>
        private static bool EsParrafoVacio(XNode pNodo)
        {
            // Los saltos de línea entre elementos también son relleno al pegar bloques.
            if (pNodo is XText pNodoTexto)
            {
                return string.IsNullOrWhiteSpace(pNodoTexto.Value);
            }

            if (!(pNodo is XElement pElemento) || pElemento.Name != TEXT + "p")
            {
                return false;
            }

            var pTexto = string.Concat(pElemento.DescendantNodes().OfType<XText>().Select(t => t.Value)).Trim();
            if (pTexto.Length > 0)
            {
                return false;
            }

            // Si trae imagen, campo o marcador no es relleno aunque no tenga texto.
            return !pElemento.Descendants().Any(e =>
                e.Name == DRAW + "frame" ||
                e.Name == DRAW + "custom-shape" ||
                e.Name == TEXT + "database-display" ||
                e.Name == TEXT + "bookmark-start" ||
                e.Name == TEXT + "a");
        }

        private static int _contadorTablas;
        private static int _contadorEstilos;

        /// <summary>
        /// Copia al destino los estilos automáticos del origen con un nombre único
        /// ("Tabla1.1" -&gt; "Tabla1.1_7") y devuelve el mapa nombreViejo -&gt; nombreNuevo.
        /// </summary>
        private static Dictionary<string, string> RenombrarEstilosDeOrigen(XElement pOrigen)
        {
            var pMapa = new Dictionary<string, string>(StringComparer.Ordinal);

            var pAutOrigen = pOrigen.Element(OFFICE + "automatic-styles");
            if (pAutOrigen == null)
            {
                return pMapa;
            }

            var pEstilos = pAutOrigen.Elements()
                .Where(e => e.Name == STYLE + "style" || e.Name == TEXT + "list-style")
                .ToList();

            foreach (var pEstilo in pEstilos)
            {
                var pNombre = (string)pEstilo.Attribute(STYLE + "name");
                if (string.IsNullOrEmpty(pNombre))
                {
                    continue;
                }

                _contadorEstilos++;
                pMapa[pNombre] = pNombre + "_" + _contadorEstilos;
            }

            return pMapa;
        }

        /// <summary>
        /// Añade al destino una copia renombrada de cada estilo del origen y deja en el clon
        /// las referencias apuntando a los nombres nuevos.
        /// </summary>
        private void AgregarEstilosRenombrados(XElement pOrigen, Dictionary<string, string> pMapa)
        {
            var pAutDestino = _documento.Root.Element(OFFICE + "automatic-styles");
            var pAutOrigen = pOrigen.Element(OFFICE + "automatic-styles");

            if (pAutDestino == null || pAutOrigen == null || pMapa.Count == 0)
            {
                return;
            }

            var pEstilos = pAutOrigen.Elements()
                .Where(e => e.Name == STYLE + "style" || e.Name == TEXT + "list-style")
                .ToList();

            foreach (var pEstilo in pEstilos)
            {
                var pNombre = (string)pEstilo.Attribute(STYLE + "name");
                if (string.IsNullOrEmpty(pNombre) || !pMapa.TryGetValue(pNombre, out var pNuevo))
                {
                    continue;
                }

                var pCopia = new XElement(pEstilo);
                pCopia.SetAttributeValue(STYLE + "name", pNuevo);

                // El padre/tipo siguiente también puede ser un estilo renombrado.
                foreach (var pAtributo in new[] { "parent-style-name", "next-style-name" })
                {
                    var pRef = (string)pCopia.Attribute(STYLE + pAtributo);
                    if (pRef != null && pMapa.TryGetValue(pRef, out var pRefNueva))
                    {
                        pCopia.SetAttributeValue(STYLE + pAtributo, pRefNueva);
                    }
                }

                pAutDestino.Add(pCopia);
            }
        }

        private static readonly HashSet<string> AtributosDeEstilo = new HashSet<string>(StringComparer.Ordinal)
        {
            "style-name",
            "default-cell-style-name",
            "parent-style-name",
            "next-style-name",
            "list-style-name",
            "master-page-name",
            "page-layout-name",
            "data-style-name"
        };

        /// <summary>Reescribe en el nodo las referencias a estilos según el mapa.</summary>
        private static void AplicarMapaEstilos(XNode pNodo, Dictionary<string, string> pMapa)
        {
            if (pMapa.Count == 0 || !(pNodo is XElement pRaiz))
            {
                return;
            }

            foreach (var pElemento in pRaiz.DescendantsAndSelf())
            {
                foreach (var pAtributo in pElemento.Attributes().ToList())
                {
                    if (!AtributosDeEstilo.Contains(pAtributo.Name.LocalName))
                    {
                        continue;
                    }

                    if (pMapa.TryGetValue(pAtributo.Value, out var pNuevo))
                    {
                        pAtributo.Value = pNuevo;
                    }
                }
            }
        }

        /// <summary>
        /// Da un nombre único a cada tabla que se pega (Tabla1 -> Tabla1_2, Tabla1_3, ...).
        /// El nombre de tabla es local al documento; nada más lo referencia.
        /// </summary>
        private static void RenombrarTablas(XNode nodo)
        {
            var pElementos = nodo is XElement pRaiz
                ? pRaiz.DescendantsAndSelf(TABLE + "table")
                : Enumerable.Empty<XElement>();

            foreach (var pTabla in pElementos)
            {
                var pNombre = (string)pTabla.Attribute(TABLE + "name");

                if (string.IsNullOrEmpty(pNombre))
                {
                    continue;
                }

                _contadorTablas++;
                pTabla.SetAttributeValue(TABLE + "name", pNombre + "_" + _contadorTablas);
            }
        }

        private static XElement ObtenerTexto(XElement raiz)
        {
            var pCuerpo = raiz.Element(OFFICE + "body");
            if (pCuerpo == null)
            {
                return null;
            }

            return pCuerpo.Element(OFFICE + "text");
        }

        private static XNode ClonarNodo(XNode nodo)
        {
            if (nodo is XElement pElemento)
            {
                return new XElement(pElemento);
            }

            if (nodo is XText pTexto)
            {
                return new XText(pTexto);
            }

            if (nodo is XComment pComentario)
            {
                return new XComment(pComentario);
            }

            // No esperado en un .fodt, pero que no se pierda contenido.
            return new XText(nodo.ToString());
        }

        private static void FusionarEstilos(XElement destino, XElement origen)
        {
            var pAutDestino = destino.Element(OFFICE + "automatic-styles");
            var pAutOrigen = origen.Element(OFFICE + "automatic-styles");

            if (pAutDestino == null || pAutOrigen == null)
            {
                return;
            }

            var pNombres = new HashSet<string>(
                pAutDestino.Elements(STYLE + "style")
                           .Select(s => (string)s.Attribute(STYLE + "name"))
                           .Where(n => !string.IsNullOrEmpty(n)),
                StringComparer.Ordinal);

            foreach (var pEstilo in pAutOrigen.Elements(STYLE + "style").ToList())
            {
                var pNombre = (string)pEstilo.Attribute(STYLE + "name");

                if (!string.IsNullOrEmpty(pNombre) && pNombres.Add(pNombre))
                {
                    pAutDestino.Add(new XElement(pEstilo));
                }
            }
        }

        // ------------------------------------------------------------------
        //  Campos simples (bookmarks)
        // ------------------------------------------------------------------

        /// <summary>Replaces the content of a bookmark when it exists.</summary>
        public bool SetBookmarkIfExists(string nombre, string valor)
        {
            var pInicio = BuscarBookmarkStart(nombre);

            if (pInicio == null)
            {
                return false;
            }

            ReemplazarContenidoBookmark(pInicio, valor ?? string.Empty);
            return true;
        }

        /// <summary>Returns the bookmark names defined in the template.</summary>
        public List<string> Bookmarks()
        {
            return _documento.Root
                .Descendants(TEXT + "bookmark-start")
                .Select(e => (string)e.Attribute(TEXT + "name"))
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct()
                .OrderBy(n => n)
                .ToList();
        }

        /// <summary>Fills loose header fields from a record and returns how many were filled.</summary>
        public int ExecuteMailMerge(DataRow registro)
        {
            if (registro == null)
            {
                return 0;
            }

            var pFilasDeRegion = new HashSet<XElement>(
                _documento.Root
                    .Descendants(TEXT + "database-display")
                    .Where(e => ((string)e.Attribute(TEXT + "column-name") ?? "").StartsWith(PrefijoStart, StringComparison.Ordinal))
                    .Select(e => e.Ancestors(TABLE + "table-row").FirstOrDefault())
                    .Where(f => f != null));

            int pRellenados = 0;

            foreach (var pCampo in _documento.Root.Descendants(TEXT + "database-display").ToList())
            {
                var pColumna = (string)pCampo.Attribute(TEXT + "column-name");

                if (string.IsNullOrEmpty(pColumna)) continue;
                if (pColumna.StartsWith(PrefijoStart, StringComparison.Ordinal) ||
                    pColumna.StartsWith(PrefijoEnd, StringComparison.Ordinal)) continue;

                if (!registro.Table.Columns.Contains(pColumna)) continue;

                // Si está dentro de la fila de la región, lo rellena ExecuteRegion.
                if (pCampo.Ancestors(TABLE + "table-row").Any(f => pFilasDeRegion.Contains(f))) continue;

                pCampo.ReplaceWith(CrearSpan(pCampo, ValorDeColumna(registro.Table, registro, pColumna)));
                pRellenados++;
            }

            return pRellenados;
        }

        // ------------------------------------------------------------------
        //  Reacomodo de las cajas de posición fija
        // ------------------------------------------------------------------

        /// <summary>Ancho medio de carácter, en cm. Calibrado con el PDF real: el bloque de la
        /// dirección (3.927 cm de ancho) parte "CHIHUAHUA 125 NTE", 16 caracteres.</summary>
        private const double AnchoCaracterCm = 0.2454;

        /// <summary>Alto de renglón, en cm. Los renglones de esas cajas salen a 12.2 pt.</summary>
        private const double AltoRenglonCm = 0.4304;

        /// <summary>Reflows fixed-position text boxes so their content does not overlap.</summary>
        public int ReflowTextBoxes()
        {
            // Las cajas de datos pasan a fluir como texto normal: crecen solas y empujan lo que
            // venga detrás, igual que en el .docx. Es la única forma de que no se encimen, porque
            // ancladas a un carácter con desplazamiento fijo LibreOffice no reserva su alto y el
            // texto se sale encima del siguiente.
            int pConvertidas = FlattenDataTextBoxes();

            if (pConvertidas > 0)
            {
                return pConvertidas;
            }

            // Respaldo: si no había cajas que aplanar, se intenta el empuje por estimación.
            int pMovidas = 0;

            foreach (var pGrupo in GruposDeCajas())
            {
                double pEmpuje = 0;

                for (int i = 0; i < pGrupo.Count; i++)
                {
                    var pCaja = pGrupo[i];

                    if (pEmpuje > 0)
                    {
                        DesplazarCaja(pCaja, pEmpuje);
                        pMovidas++;
                    }

                    double pAltoActual = MedidaCm(pCaja, SVG + "height");
                    double pNecesario = AltoNecesarioCm(pCaja);

                    if (pNecesario <= pAltoActual + 0.001)
                    {
                        continue;
                    }

                    // La caja crece hasta lo que necesita el texto...
                    pCaja.SetAttributeValue(SVG + "height", Cm(pNecesario));

                    // ...y lo que se pasa se le empuja a las siguientes del grupo.
                    pEmpuje += pNecesario - pAltoActual;
                }
            }

            return pMovidas;
        }

        /// <summary>
        /// Grupos de cajas apiladas: se juntan las cajas de datos que están en la MISMA COLUMNA
        /// (misma x), conservando el orden del documento.
        ///
        /// No se agrupa por "párrafos seguidos" porque entre una caja y la siguiente hay otros
        /// párrafos de la página y el grupo se cortaba; ni por estilo, porque el bloque de la
        /// empresa mezcla P15, P9 y P19 y las últimas cajas se quedaban sin empujar.
        /// Sólo entran los párrafos que NO están dentro de una caja (los internos de un
        /// draw:custom-shape también son text:p) y cuya caja lleva un bookmark, que son los
        /// huecos de datos; así el logo, que ya no lleva bookmark, no se mueve.
        /// </summary>
        private List<List<XElement>> GruposDeCajas()
        {
            var pGrupos = new List<List<XElement>>();

            foreach (var pParrafo in _documento.Root.Descendants(TEXT + "p"))
            {
                if (pParrafo.Ancestors().Any(a => a.Name == DRAW + "custom-shape" || a.Name == DRAW + "frame"))
                {
                    continue;
                }

                var pCaja = pParrafo.Elements()
                    .FirstOrDefault(e => (e.Name == DRAW + "custom-shape" || e.Name == DRAW + "frame") &&
                                         e.Attribute(SVG + "y") != null &&
                                         e.Attribute(SVG + "height") != null &&
                                         e.Descendants(TEXT + "bookmark-start").Any());

                if (pCaja == null)
                {
                    continue;
                }

                double pX = MedidaCm(pCaja, SVG + "x");

                var pGrupo = pGrupos.FirstOrDefault(g => Math.Abs(MedidaCm(g[0], SVG + "x") - pX) <= 0.5);

                if (pGrupo == null)
                {
                    pGrupo = new List<XElement>();
                    pGrupos.Add(pGrupo);
                }

                pGrupo.Add(pCaja);
            }

            return pGrupos;
        }

        /// <summary>Converts stacked data text boxes into in-flow frames.</summary>
        public int FlattenDataTextBoxes()
        {
            int pConvertidas = 0;

            var pCajas = GruposDeCajas()
                .Where(g => g.Count > 1)
                .SelectMany(g => g)
                .Where(c => !c.Descendants(DRAW + "image").Any())
                .ToList();

            foreach (var pCaja in pCajas)
            {
                // Se deja el cuadro original tal cual (tipo, estilos, geometría y contenido) y sólo
                // se le cambia el anclaje: de "char" con desplazamiento a "as-char" sin
                // desplazamiento. Así pasa a ocupar su sitio en el flujo del texto: el párrafo crece
                // con él y empuja lo de abajo, que es lo que hace el .docx.
                //
                // Probado antes creando un draw:frame nuevo con el contenido dentro: LibreOffice
                // descartaba el marco y el bloque de la empresa desaparecía del PDF. Con el cuadro
                // original no hay nada que reconstruir y se pinta como siempre.
                pCaja.SetAttributeValue(TEXT + "anchor-type", "as-char");

                var pX = pCaja.Attribute(SVG + "x");
                if (pX != null) pX.Remove();

                var pY = pCaja.Attribute(SVG + "y");
                if (pY != null) pY.Remove();

                pCaja.SetAttributeValue(DRAW + "auto-grow-height", "true");

                pConvertidas++;
            }

            return pConvertidas;
        }

        /// <summary>
        /// Las cajas que sólo guardan un dato: llevan un bookmark dentro y ninguna imagen. Se dejan
        /// fuera las del logo y el QR, que son marcos de imagen y no texto.
        /// </summary>
        private IEnumerable<XElement> CajasDeDatos()
        {
            foreach (var pParrafo in _documento.Root.Descendants(TEXT + "p"))
            {
                if (pParrafo.Ancestors().Any(a => a.Name == DRAW + "custom-shape" || a.Name == DRAW + "frame"))
                {
                    continue;
                }

                var pCaja = pParrafo.Elements()
                    .FirstOrDefault(e => (e.Name == DRAW + "custom-shape" || e.Name == DRAW + "frame") &&
                                         e.Attribute(SVG + "y") != null &&
                                         e.Descendants(TEXT + "bookmark-start").Any() &&
                                         !e.Descendants(DRAW + "image").Any());

                if (pCaja != null)
                {
                    yield return pCaja;
                }
            }
        }

        private static void DesplazarCaja(XElement caja, double deltaCm)
        {
            double pY = MedidaCm(caja, SVG + "y");

            caja.SetAttributeValue(SVG + "y", Cm(pY + deltaCm));
        }

        private static double MedidaCm(XElement elemento, XName atributo)
        {
            var pValor = (string)elemento.Attribute(atributo);

            if (string.IsNullOrEmpty(pValor)) return 0;

            pValor = pValor.Replace("cm", "").Replace("pt", "").Replace("mm", "").Trim();

            double pNumero;
            if (!double.TryParse(pValor, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out pNumero))
            {
                return 0;
            }

            // sólo se usan cm en esta plantilla; si viniera en pt/mm se convierte
            var pOriginal = (string)elemento.Attribute(atributo);
            if (pOriginal.EndsWith("pt")) return pNumero * 0.0352778;
            if (pOriginal.EndsWith("mm")) return pNumero / 10.0;

            return pNumero;
        }

        private static string Cm(double valor)
        {
            return valor.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + "cm";
        }

        /// <summary>
        /// El texto real de la caja. Importante: el .fodt se carga con PreserveWhitespace, así que
        /// entre las etiquetas hay nodos de texto que sólo son sangría (saltos de línea y espacios).
        /// Si se cuentan como contenido, cada salto de línea del XML se vuelve un renglón y las
        /// cajas crecen sin sentido (el RFC de 17 caracteres salía como 11 renglones).
        /// </summary>
        private static string TextoDeCaja(XElement caja)
        {
            var pTexto = new System.Text.StringBuilder();

            foreach (var pNodo in caja.DescendantNodes())
            {
                if (pNodo is XText pXText)
                {
                    var pValor = pXText.Value;

                    // Sangría del XML: se descarta.
                    if (pValor.Trim().Length == 0)
                    {
                        continue;
                    }

                    if (pTexto.Length > 0 && !pTexto.ToString().EndsWith("\n"))
                    {
                        pTexto.Append(' ');
                    }

                    pTexto.Append(pValor.Trim());
                }
                else if (pNodo is XElement pElemento && pElemento.Name == TEXT + "line-break")
                {
                    pTexto.Append('\n');
                }
                else if (pNodo is XElement pS && pS.Name == TEXT + "s")
                {
                    pTexto.Append(' ');
                }
            }

            return pTexto.ToString();
        }

        /// <summary>Returns diagnostic information about the measured text boxes.</summary>
        public List<string> DiagnoseTextBoxes()
        {
            var pLineas = new List<string>();

            foreach (var pGrupo in GruposDeCajas())
            {
                foreach (var pCaja in pGrupo)
                {
                    double pAncho = MedidaCm(pCaja, SVG + "width");
                    int pChars = Math.Max(1, (int)(pAncho / AnchoCaracterCm));
                    var pTexto = TextoDeCaja(pCaja);

                    pLineas.Add($"{(string)pCaja.Attribute(DRAW + "name")}: ancho={pAncho} chars/renglon={pChars} " +
                                $"renglones={(AltoNecesarioCm(pCaja) / AltoRenglonCm):0.#} " +
                                $"alto={MedidaCm(pCaja, SVG + "height")} texto=[{pTexto.Substring(0, Math.Min(60, pTexto.Length))}]");
                }
            }

            return pLineas;
        }

        /// <summary>
        /// Alto que necesita el texto de la caja: cuenta los renglones explícitos (text:line-break)
        /// y además los que salgan de partir el texto por el ancho de la caja.
        /// </summary>
        private static double AltoNecesarioCm(XElement caja)
        {
            var pTexto = TextoDeCaja(caja);

            var pLineas = pTexto.Replace("\r\n", "\n").Split('\n');
            int pRenglones = 0;

            double pAncho = MedidaCm(caja, SVG + "width");
            int pCaracteresPorLinea = Math.Max(1, (int)(pAncho / AnchoCaracterCm));

            foreach (var pLinea in pLineas)
            {
                pRenglones += RenglonesDeLinea(pLinea, pCaracteresPorLinea);
            }

            if (pRenglones < 1) pRenglones = 1;

            return pRenglones * AltoRenglonCm;
        }

        /// <summary>Renglones que ocupa una línea al partirla por palabras.</summary>
        private static int RenglonesDeLinea(string linea, int caracteresPorLinea)
        {
            if (linea.Length <= caracteresPorLinea)
            {
                return 1;
            }

            int pRenglones = 1;
            int pUsados = 0;

            foreach (var pPalabra in linea.Split(' '))
            {
                if (pPalabra.Length == 0) continue;

                if (pUsados == 0)
                {
                    pUsados = pPalabra.Length;
                }
                else if (pUsados + 1 + pPalabra.Length <= caracteresPorLinea)
                {
                    pUsados += 1 + pPalabra.Length;
                }
                else
                {
                    pRenglones++;
                    pUsados = pPalabra.Length;
                }
            }

            return pRenglones;
        }

        private XElement BuscarBookmarkStart(string nombre)
        {
            // Sin distinguir mayúsculas: en la plantilla el bookmark del centro de costo se llama
            // "cc" y el código pide "CC". PlantillaDocx ya lo compara con OrdinalIgnoreCase, así que
            // los dos motores tienen que comportarse igual.
            return _documento.Root
                .Descendants(TEXT + "bookmark-start")
                .FirstOrDefault(e => string.Equals((string)e.Attribute(TEXT + "name"), nombre,
                    StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Borra lo que hay entre bookmark-start y bookmark-end y escribe el valor.
        /// Un salto de línea va como text:line-break (el equivalente al w:br del docx), que es
        /// lo que necesita el "Fax" de la empresa.
        ///
        /// Antes de borrar se guarda el ESTILO DE CARÁCTER del relleno (text:style-name del primer
        /// span) y se aplica al valor. Sin eso, lo escrito hereda la letra del párrafo y no la del
        /// marcador: la cadena original salía en tamaño 6 cuando en la plantilla está en 5.
        /// </summary>
        private static void ReemplazarContenidoBookmark(XElement inicio, string valor)
        {
            string pEstilo = null;

            for (var pNodo = inicio.NextNode; pNodo != null; pNodo = pNodo.NextNode)
            {
                if (pNodo is XElement fin && fin.Name == TEXT + "bookmark-end")
                {
                    break;
                }

                if (pEstilo == null && pNodo is XElement span && span.Name == TEXT + "span")
                {
                    pEstilo = (string)span.Attribute(TEXT + "style-name");
                }
            }

            var pSiguiente = inicio.NextNode;

            while (pSiguiente != null)
            {
                var pNodo = pSiguiente;
                pSiguiente = pSiguiente.NextNode;

                if (pNodo is XElement elemento && elemento.Name == TEXT + "bookmark-end")
                {
                    break;
                }

                pNodo.Remove();
            }

            XNode pAncla = inicio;

            var pLineas = valor.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            for (int i = 0; i < pLineas.Length; i++)
            {
                if (i > 0)
                {
                    pAncla = InsertarDespues(pAncla, new XElement(TEXT + "line-break"));
                }

                if (pLineas[i].Length > 0)
                {
                    var pSpan = new XElement(TEXT + "span", new XText(pLineas[i]));

                    if (!string.IsNullOrEmpty(pEstilo))
                    {
                        pSpan.SetAttributeValue(TEXT + "style-name", pEstilo);
                    }

                    pAncla = InsertarDespues(pAncla, pSpan);
                }
            }
        }

        private static XNode InsertarDespues(XNode ancla, XNode nuevo)
        {
            if (ancla is XElement elemento)
            {
                elemento.AddAfterSelf(nuevo);
            }
            else
            {
                ancla.AddAfterSelf(nuevo);
            }

            return nuevo;
        }

        // ------------------------------------------------------------------
        //  Región de repetición (tabla de partidas)
        // ------------------------------------------------------------------

        /// <summary>Expands a repeating table region and returns the number of rendered rows.</summary>
        public int ExecuteRegion(string nombreRegion, DataTable datos)
        {
            if (datos == null)
            {
                return 0;
            }

            var pCampoInicio = BuscarCampoRegion(PrefijoStart + nombreRegion);
            var pCampoFin = BuscarCampoRegion(PrefijoEnd + nombreRegion);

            if (pCampoInicio == null || pCampoFin == null)
            {
                // La plantilla se volvió a guardar desde LibreOffice y los campos de mail-merge
                // quedaron como TEXTO PLANO («Cantidad», «TableStart:DetalleInvoice»...). Se intenta
                // por esa vía antes de darse por vencido.
                return ExecuteRegionTextoPlano(nombreRegion, datos);
            }

            // La fila que hay que repetir es la que contiene el marcador de inicio
            // (en esta plantilla el TableEnd cae en la misma fila, en la última celda).
            var pFila = pCampoInicio.Ancestors(TABLE + "table-row").FirstOrDefault();

            if (pFila == null)
            {
                return -1;
            }

            if (datos.Rows.Count == 0)
            {
                pFila.Remove();
                return 0;
            }

            var pFilasNuevas = new List<XElement>();

            foreach (DataRow pRegistro in datos.Rows)
            {
                var pClon = new XElement(pFila);

                // 1) fuera los dos marcadores de la región
                foreach (var pMarca in pClon.Descendants(TEXT + "database-display").ToList())
                {
                    var pColumna = (string)pMarca.Attribute(TEXT + "column-name");

                    if (pColumna != null &&
                        (pColumna.StartsWith(PrefijoStart, StringComparison.Ordinal) ||
                         pColumna.StartsWith(PrefijoEnd, StringComparison.Ordinal)))
                    {
                        pMarca.Remove();
                    }
                }

                // 2) cada campo se cambia por su valor
                foreach (var pCampo in pClon.Descendants(TEXT + "database-display").ToList())
                {
                    var pColumna = (string)pCampo.Attribute(TEXT + "column-name");
                    var pValor = ValorDeColumna(datos, pRegistro, pColumna);

                    pCampo.ReplaceWith(CrearSpan(pCampo, pValor));
                }

                pFilasNuevas.Add(pClon);
            }

            pFila.ReplaceWith(pFilasNuevas);

            return datos.Rows.Count;
        }

        private XElement BuscarCampoRegion(string nombreColumna)
        {
            return _documento.Root
                .Descendants(TEXT + "database-display")
                .FirstOrDefault(e => string.Equals((string)e.Attribute(TEXT + "column-name"), nombreColumna,
                    StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Región cuando los campos de mail-merge quedaron como TEXTO PLANO. Pasa en cuanto la
        /// plantilla se guarda desde LibreOffice: los text:database-display se convierten en texto
        /// y la factura salía SIN partidas. Aquí los marcadores son las palabras
        /// «TableStart:DetalleInvoice» / «TableEnd:DetalleInvoice» y cada columna se reconoce por su
        /// nombre entre comillas angulares: «Cantidad», «Precio», «Descripcion»...
        /// </summary>
        private int ExecuteRegionTextoPlano(string nombreRegion, DataTable datos)
        {
            var pMarca = BuscarTexto(PrefijoStart + nombreRegion);

            // En .fodt guardados desde LibreOffice el marcador puede quedar partido en
            // varios text:span ("«TableStart:" + "TablaPrueba" + "»"). Buscar solo en
            // un XText no lo encuentra; se busca también en el texto concatenado de la fila.
            var pFila = pMarca?.Parent?.AncestorsAndSelf(TABLE + "table-row").FirstOrDefault();

            pFila ??= _documento.Root
                .Descendants(TABLE + "table-row")
                .FirstOrDefault(f => string.Concat(f.DescendantNodes().OfType<XText>().Select(t => t.Value))
                    .Contains(PrefijoStart + nombreRegion, StringComparison.Ordinal));

            if (pFila == null)
            {
                return -1;
            }

            if (datos.Rows.Count == 0)
            {
                pFila.Remove();
                return 0;
            }

            var pFilasNuevas = new List<XElement>();

            foreach (DataRow pRegistro in datos.Rows)
            {
                var pClon = new XElement(pFila);
                ReemplazarMarcasDeTexto(pClon, pRegistro, datos);
                pFilasNuevas.Add(pClon);
            }

            pFila.ReplaceWith(pFilasNuevas);

            return datos.Rows.Count;
        }

        /// <summary>El nodo de texto del documento que contiene esa cadena.</summary>
        private XText BuscarTexto(string contenido)
        {
            return _documento.Root
                .DescendantNodes()
                .OfType<XText>()
                .FirstOrDefault(t => t.Value.Contains(contenido));
        }

        /// <summary>
        /// Dentro de la fila clonada: quita los marcadores de la región y cambia cada «Columna» por
        /// el valor de esa partida.
        /// </summary>
        private static void ReemplazarMarcasDeTexto(XElement fila, DataRow registro, DataTable datos)
        {
            // Se procesa por PÁRRAFO dentro de cada celda, no por XText: LibreOffice
            // parte «TableStart:» / «Columna» en varios spans. No se puede usar sólo
            // el primer <text:p> de la celda: la descripción de la factura tiene tres
            // párrafos (Descripcion, claveProductoServicio, claveUM) y antes se
            // reemplazaba el primero y los otros dos quedaban con sus «placeholders».
            foreach (var celda in fila.Descendants(TABLE + "table-cell").ToList())
            {
                foreach (var parrafo in celda.Descendants(TEXT + "p").ToList())
                {
                    var textos = parrafo.DescendantNodes().OfType<XText>().ToList();

                    if (textos.Count == 0) continue;

                    var original = string.Concat(textos.Select(t => t.Value));

                    if (!original.Contains('«')) continue;

                    var nuevo = ReemplazarMarcadores(original, registro, datos);

                    // No reemplazar el párrafo por texto "pelón": el estilo de letra
                    // (por ejemplo T21 = 5.5 pt en la columna Descripción) vive en el
                    // text:span. Si se pierde, el dato hereda el tamaño por omisión y
                    // se ve más grande que en el .fodt original.
                    var estiloSpan = parrafo.Elements(TEXT + "span")
                        .Select(e => (string)e.Attribute(TEXT + "style-name"))
                        .FirstOrDefault(n => !string.IsNullOrEmpty(n));

                    var nuevoSpan = new XElement(TEXT + "span", new XText(nuevo));

                    if (!string.IsNullOrEmpty(estiloSpan))
                        nuevoSpan.SetAttributeValue(TEXT + "style-name", estiloSpan);

                    parrafo.ReplaceNodes(nuevoSpan);
                }
            }
        }

        private static string ReemplazarMarcadores(string texto, DataRow registro, DataTable datos)
        {
            var salida = new StringBuilder(texto.Length);
            var i = 0;

            while (i < texto.Length)
            {
                var inicio = texto.IndexOf('«', i);

                if (inicio < 0)
                {
                    salida.Append(texto, i, texto.Length - i);
                    break;
                }

                salida.Append(texto, i, inicio - i);
                var fin = texto.IndexOf('»', inicio + 1);

                if (fin < 0)
                {
                    salida.Append(texto, inicio, texto.Length - inicio);
                    break;
                }

                var token = texto.Substring(inicio + 1, fin - inicio - 1);

                if (!token.StartsWith(PrefijoStart, StringComparison.Ordinal) &&
                    !token.StartsWith(PrefijoEnd, StringComparison.Ordinal))
                {
                    salida.Append(ValorDeColumna(datos, registro, token));
                }

                i = fin + 1;
            }

            return salida.ToString();
        }

        private static string ValorDeColumna(DataTable datos, DataRow registro, string columna)
        {
            if (string.IsNullOrEmpty(columna) || !datos.Columns.Contains(columna))
            {
                return string.Empty;
            }

            var pValor = registro[columna];

            if (pValor == null || pValor == DBNull.Value)
            {
                return string.Empty;
            }

            return Convert.ToString(pValor, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }

        /// <summary>
        /// Sustituye el campo por un span normal, heredando el estilo del span que lo contenía,
        /// para que el dato salga con la misma letra que el marcador.
        /// </summary>
        private static XElement CrearSpan(XElement campo, string valor)
        {
            var pSpan = new XElement(TEXT + "span", new XText(valor ?? string.Empty));

            var pEstilo = campo.Parent == null ? null : (string)campo.Parent.Attribute(TEXT + "style-name");

            if (!string.IsNullOrEmpty(pEstilo))
            {
                pSpan.SetAttributeValue(TEXT + "style-name", pEstilo);
            }

            return pSpan;
        }

        // ------------------------------------------------------------------
        //  Imagen (logo)
        // ------------------------------------------------------------------

        /// <summary>Inserts an image into a bookmark with an explicit size in points.</summary>
        public bool InsertImage(string nombreBookmark, byte[] imagen, double anchoPt, double altoPt)
        {
            return InsertImageEnCm(nombreBookmark, imagen, PtACm(anchoPt), PtACm(altoPt));
        }

        /// <summary>Inserts an image into a bookmark using its natural size.</summary>
        public bool InsertImageNatural(string nombreBookmark, byte[] imagen)
        {
            return InsertImage(nombreBookmark, imagen, 130.5, 130.5);
        }

        /// <summary>Los puntos de Word a centímetros de ODF (1 pt = 0.0352778 cm).</summary>
        private static string PtACm(double pt)
        {
            return (pt * 0.0352778).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "cm";
        }

        /// <summary>
        /// Mete la imagen en el hueco del bookmark. En el .fodt no hay que tocar el paquete:
        /// la imagen va incrustada como base64 en office:binary-data.
        ///
        /// OJO con el POSICIONAMIENTO: en esta plantilla los dos bookmarks de imagen (logotipo y
        /// QRCode) viven dentro de un draw:custom-shape que sólo reserva el hueco. Si la imagen se
        /// metiera DENTRO de esa caja, LibreOffice no la agranda y la dibuja desbordada encima de
        /// lo de abajo (el QR está en una caja de 0.003 cm de alto: se come el encabezado entero).
        /// Por eso se sustituye la caja completa por el marco de la imagen.
        /// </summary>
        private bool InsertImageEnCm(string nombreBookmark, byte[] imagen, string ancho, string alto)
        {
            var pInicio = BuscarBookmarkStart(nombreBookmark);

            if (pInicio == null || imagen == null || imagen.Length == 0)
            {
                return false;
            }

            var pFrame = new XElement(DRAW + "frame",
                new XAttribute(DRAW + "name", nombreBookmark),
                new XAttribute(TEXT + "anchor-type", "as-char"),
                new XAttribute(SVG + "width", ancho),
                new XAttribute(SVG + "height", alto),
                new XElement(DRAW + "image",
                    new XAttribute(DRAW + "mime-type", MimeDe(imagen)),
                    new XElement(OFFICE + "binary-data", Convert.ToBase64String(imagen))));

            // La caja que reserva el hueco (si la hay) se cambia entera por el marco.
            var pCaja = pInicio.Ancestors(DRAW + "custom-shape").FirstOrDefault();

            if (pCaja != null)
            {
                // El marco tiene que quedar FLOTANTE donde estaba la caja, no como contenido en
                // línea: si se deja en línea, cambia el alto de la línea del párrafo y arrastra a
                // todas las cajas ancladas a carácter de ese párrafo (el bloque de la empresa son
                // 6 cajas de texto una por renglón: se apilan y sale todo encimado).
                //
                // El draw:style-name es imprescindible: es donde vive style:wrap="run-through" y
                // style:flow-with-text="false", o sea "no ocupes sitio". Sin él, el marco se lleva
                // el ancho de la imagen en la línea y empuja a la derecha el carácter al que está
                // anclada la caja del nombre largo de la empresa (se veía desplazado en el PDF).
                foreach (var pNombre in new[] { DRAW + "style-name", TEXT + "anchor-type", SVG + "x", SVG + "y", DRAW + "z-index" })
                {
                    var pAtributo = pCaja.Attribute(pNombre);

                    if (pAtributo != null)
                    {
                        pFrame.SetAttributeValue(pNombre, pAtributo.Value);
                    }
                }

                pCaja.ReplaceWith(pFrame);

                return true;
            }

            // Sin caja: la imagen va en línea, en el mismo párrafo del bookmark.
            ReemplazarContenidoBookmark(pInicio, string.Empty);

            InsertarDespues(pInicio, pFrame);

            return true;
        }

        private static string MimeDe(byte[] imagen)
        {
            // PNG o JPEG según la cabecera; el logo del sistema es PNG.
            if (imagen.Length > 1 && imagen[0] == 0x89 && imagen[1] == 0x50)
            {
                return "image/png";
            }

            if (imagen.Length > 1 && imagen[0] == 0xFF && imagen[1] == 0xD8)
            {
                return "image/jpeg";
            }

            return "image/png";
        }
    }
}
