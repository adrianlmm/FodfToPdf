namespace FodtToPdf;

// Modelo intermedio: es lo que sale del .fodt y lo que consume tanto el
// generador de esqueleto C# como el renderizador de prueba con QuestPDF.
// Todas las medidas en puntos (1 pt = 1/72 pulg, 1 cm = 28.3465 pt).

internal class ModeloPlantilla
{
    public string Archivo { get; set; } = "";
    public PaginaModelo Pagina { get; set; } = new();
    public List<CapaModelo> Capas { get; set; } = new();      // cajas y frames con posición absoluta
    public List<BloqueModelo> Flujo { get; set; } = new();    // párrafos y tablas en orden de documento
    public List<SeccionModelo> Secciones { get; set; } = new(); // secciones con su número de columnas
    public List<ParrafoModelo> Pie { get; set; } = new();       // pie de página (style:footer del master-page)

    // encabezado del master-page (style:header). En la bitácora de pedidos el título, la
    // ruta y la fecha de entrega viven aquí, no en el cuerpo: sin esto el PDF salía sin
    // encabezado. Sólo se toman los párrafos con texto (el marco de la marca de agua de
    // la factura se sigue ignorando).
    public List<ParrafoModelo> Encabezado { get; set; } = new();

    // Marcos con imagen que viven en el encabezado del master-page: la marca de agua
    // (el patrón «TELÉFONOS / E-MAIL / OFICINAS» de la factura). Se dibujan en el fondo
    // de TODAS las páginas, con las coordenadas absolutas del marco.
    public List<CapaModelo> MarcasAgua { get; set; } = new();

    // Marcas de agua de TEXTO: el encabezado del master-page trae un draw:custom-shape con
    // draw:text-path="true" (WordArt/fontwork) en vez de una imagen. La orden de compra
    // inválida escribe «COPIA NO VALIDA» y no se dibujaba (el extractor sólo leía marcos
    // con imagen). LibreOffice estira el texto hasta llenar la forma.
    public List<MarcaAguaTextoModelo> MarcasAguaTexto { get; set; } = new();

    // Las tablas de los reportes llevan fo:margin-left NEGATIVO y un ancho mayor que el
    // área de texto: están diseñadas para desbordar hacia los márgenes. Esto guarda el
    // mayor desplazamiento hacia la izquierda (0 si ninguna lo pide).
    public double DesplazamientoIzqPt { get; set; }

    public List<string> Bookmarks { get; set; } = new();
}

// Una text:section puede tener varias columnas (la del encabezado de la factura tiene 3).
// Eso es lo que hace que los campos de la derecha caigan a la derecha y no debajo del logo.
internal class SeccionModelo
{
    public string Nombre { get; set; } = "";
    public int Columnas { get; set; } = 1;
    public List<double> RelAnchos { get; set; } = new();   // style:rel-width de cada columna
    public List<double> SeparacionPt { get; set; } = new(); // fo:column-gap entre columnas
}

internal class PaginaModelo
{
    public double AnchoPt { get; set; } = 595.28;   // A4
    public double AltoPt { get; set; } = 841.89;
    public double MargenIzqPt { get; set; } = 56.69;  // 2 cm
    public double MargenDerPt { get; set; } = 56.69;
    public double MargenSupPt { get; set; } = 56.69;
    public double MargenInfPt { get; set; } = 56.69;

    // margen superior TAL COMO lo declara el .fodt, sin el alto del encabezado: es donde
    // arranca el texto del encabezado (MargenSupPt ya lo incluye y es donde arranca el cuerpo).
    public double MargenSupBasePt { get; set; } = 56.69;

    // alto que aporta el encabezado del master-page (0 si no tiene)
    public double AltoEncabezadoPt { get; set; }

    // alto que aporta el pie del master-page (0 si no tiene)
    public double AltoPiePt { get; set; }

    // margen inferior TAL COMO lo declara el .fodt, sin el alto del pie: es la línea donde
    // debe quedar el texto del pie (MargenInfPt ya lo incluye).
    public double MargenInfBasePt { get; set; } = 56.69;
}

// Un bloque del flujo: o un párrafo, o una tabla
internal class BloqueModelo
{
    public string Tipo { get; set; } = "texto";   // "texto" | "tabla" | "espacio"
    public ParrafoModelo Parrafo { get; set; }
    public TablaModelo Tabla { get; set; }
    public double AltoPt { get; set; }            // para "espacio": alto del párrafo vacío
    public int Seccion { get; set; }              // índice en ModeloPlantilla.Secciones
    public int IndiceFlujo { get; set; } = -1;    // posicion en Flujo (paginacion en 2 pasadas)
}

// Línea suelta dibujada dentro de un párrafo (draw:line). Los contratos firman con
// estas: "PATRON / TRABAJADOR" llevan un renglón debajo que antes no se pintaba.
internal class LineaModelo
{
    public double XPt { get; set; }

    public double AnchoPt { get; set; }

    public double YPt { get; set; }

    public double GrosorPt { get; set; }

    public string Color { get; set; }
}

internal class ParrafoModelo
{
    public List<RunModelo> Runs { get; set; } = new();





    public List<LineaModelo> Lineas { get; set; } = new();

    // inciso de lista: ancho reservado para el marcador ("A)"). El texto y los
    // renglones siguientes se alinean a esa distancia (sangría francesa).
    public double SangriaColgantePt { get; set; }

    // sangría del bloque del inciso: margen del nivel + text-indent (puede ser 0)
    public double MargenIncisoPt { get; set; }

    // caja del párrafo: el encabezado del contrato CCI va con borde y fondo gris
    public bool BordeParrafo { get; set; }

    // El .fodt declara SOLO fo:border-bottom (el renglón bajo «Complemento Carta Porte» de
    // la solicitud): se pinta únicamente esa línea, no la caja completa.
    public bool BordeInferiorParrafo { get; set; }
    public string ColorBordeParrafo { get; set; }
    public double GrosorBordeParrafoPt { get; set; }
    public string ColorFondoParrafo { get; set; }
    public double PaddingSupParrafoPt { get; set; }
    public double PaddingInfParrafoPt { get; set; }
    public double PaddingIzqParrafoPt { get; set; }
    public double PaddingDerParrafoPt { get; set; }
    public string Alineacion { get; set; } = "";   // start|center|end|justify
    public double MargenIzqPt { get; set; }
    public double MargenDerPt { get; set; }        // fo:margin-right: es el aire que separa el texto del borde
    public double MargenSupPt { get; set; }        // fo:margin-top: espacio sobre el párrafo
    public double MargenInfPt { get; set; }        // fo:margin-bottom: espacio bajo el párrafo
    public double SangriaPt { get; set; }
    // fo:break-before="page": el parrafo abre hoja (lo declara el estilo P61 del CCI)
    public bool SaltoPaginaAntes { get; set; }

    // fo:break-before="column": el parrafo abre la COLUMNA siguiente de la sección. Los
    // .fodt convertidos de Word lo usan para fijar el encabezado de la factura/solicitud
    // (cada columna empieza donde el autor puso el corte, no donde toque por altura).
    public bool SaltoColumnaAntes { get; set; }
    public double AltoLineaPt { get; set; }        // fo:line-height real del estilo (0 = usar tamaño*1.2)

    // tamaño de fuente que declara el estilo del párrafo (0 si no declara). Sirve para
    // calcular el alto de un párrafo vacío dentro de una celda, que no tiene runs de
    // donde sacarlo y en LibreOffice ocupa una línea completa.
    public double TamanoBasePt { get; set; }

    // style:contextual-spacing del estilo de párrafo (por defecto false). Junto con
    // EstiloComun decide si el fo:margin-bottom de este párrafo y el fo:margin-top del
    // siguiente se ignoran (ODF 1.3 parte 3, §20.255).
    public bool Contextual { get; set; }

    // estilo "común" del párrafo para esa comparación: si text:style-name apunta a un
    // estilo automático se usa su style:parent-style-name, como dice §20.255.
    public string EstiloComun { get; set; } = "";
    public double AltoObjetoPt { get; set; }       // draw:frame as-char/char dentro del párrafo: la línea crece a su alto

    // si el párrafo lleva una imagen anclada (as-char), se dibuja DENTRO del flujo:
    // su posición depende del texto de arriba, no de una coordenada fija
    public string ImagenBase64 { get; set; }
    public string ImagenTipo { get; set; }
    public double ImagenAnchoPt { get; set; }
    public double ImagenAltoPt { get; set; }

    // true si el frame va anclado "as-char": entonces forma parte del flujo y hay que
    // dibujarlo ahí (el QR de la orden de compra). Con ancla "char" la imagen ya se
    // dibuja como capa absoluta (logo y líneas grises de la factura) y pintarla otra vez
    // en el flujo la duplicaría y empujaría todo el documento hacia abajo.
    public bool ImagenEnFlujo { get; set; }
}

internal class RunModelo
{
    public string Texto { get; set; } = "";
    public string Fuente { get; set; } = "";
    public double TamanoPt { get; set; } = 10;
    public bool Negrita { get; set; }
    public bool Cursiva { get; set; }
    public bool Subrayado { get; set; }
    public string Color { get; set; } = "#000000";
    public string Slot { get; set; }              // nombre del bookmark si este run marca un punto de datos

    // style:text-position="super 58%": LibreOffice NO usa el tamaño declarado, lo reduce
    // al 58 % (12 pt -> 7 pt) y lo levanta sobre la línea base. TamanoPt ya viene escalado;
    // esto guarda el porcentaje declarado y si la posición es "super"/"sub".
    public double? EscalaPosicion { get; set; }
    public string Posicion { get; set; }

    // text:a xlink:href: el correo de la orden de compra va como enlace (mailto:).
    // Sin esto se quedaba en texto plano y no salía como hipervínculo.
    public string Hipervinculo { get; set; }

    // (sin soporte de soft-page-break: ver la nota en GeneradorPdf.AgregarParrafo)
}

internal class CapaModelo
{
    public string Nombre { get; set; } = "";
    public string Tipo { get; set; } = "custom-shape";  // "custom-shape" | "frame"
    public double XPt { get; set; }
    public double YPt { get; set; }
    public double AnchoPt { get; set; }
    public double AltoPt { get; set; }
    public string Ancla { get; set; } = "";       // char | paragraph | page ...

    // style:vertical-rel / horizontal-rel del estilo gráfico: a qué se refieren las
    // coordenadas. "page" = absolutas a la hoja (las imágenes grises de la factura);
    // "paragraph"/"char" = relativas al párrafo/carácter de anclaje (las líneas del
    // encabezado de la solicitud de traslado). Ignorarlas mandaba esas líneas al margen.
    public string VerticalRel { get; set; } = "";
    public string HorizontalRel { get; set; } = "";
    public List<TablaModelo> Tablas { get; set; } = new();   // tablas propias de la caja
                                                             // (título y firmas de la liquidaciones)
    public List<ParrafoModelo> Parrafos { get; set; } = new();
    public string ImagenBase64 { get; set; }      // si el frame lleva una imagen embebida
    public string ImagenTipo { get; set; }        // png | jpeg | ...

    // draw:color-mode del estilo gráfico: "watermark" = LibreOffice la pinta gris y muy
    // transparente (las marcas de agua de los formatos de crédito). "standard" = tal cual.
    public string ModoColor { get; set; } = "";

    // style:vertical-pos / horizontal-pos del estilo gráfico ("middle"/"center"/"from-top").
    // La marca de agua de los formatos de crédito va centrada en el área de texto.
    public string VerticalPos { get; set; } = "";
    public string HorizontalPos { get; set; } = "";
    public bool EnFlujo { get; set; }             // ancla as-char: se dibuja dentro del flujo, no en capa absoluta

    // índice del bloque del flujo cuyo párrafo la ancla (-1 si no está anclada a un
    // párrafo del flujo, p.ej. si vive dentro de una celda de tabla). Con anchor-type
    // "char" sus coordenadas son relativas al carácter de anclaje, no a la página.
    public int BloqueAncla { get; set; } = -1;

    // la capa está anclada a un párrafo que vive DENTRO de una tabla: no se puede
    // posicionar en coordenadas de página (no sabemos dónde cae esa fila), así que su
    // contenido se dibuja en el flujo justo después de esa tabla.
    public bool AnclaEnTabla { get; set; }

    // Alto del párrafo al que está anclada (sólo AnclaEnTabla). El svg:y del .fodt se
    // cuenta desde ESE párrafo, y como la capa se dibuja después de la tabla, hay que
    // descontarlo: el bloque de firmas de la liquidaciones bajaba de más (los 25,8 pt
    // del párrafo ancla) por contarlo dos veces.
    public double AltoParrafoAnclaPt { get; set; }

    // posición en el flujo: identifica el bloque entre pasadas de paginación

    // marco anclado a un párrafo con y NEGATIVA (empieza por encima del párrafo): el
    // flujo tiene que bajar para no encimarse con él (la póliza contable: el bloque de
    // "Fecha de contabilización" tapa el "Registro en el diario:")
    public double ReservarFlujoPt { get; set; }

    // alto aproximado del contenido de la caja (suma de sus párrafos): sirve para
    // saber cuánto tiene que bajar el flujo cuando el marco arranca por encima de él
    public double AltoContenidoPt { get; set; }

    // draw:fill-color del estilo gráfico: las draw:custom-shape que sólo son una barra de
    // color (la que va antes de "ORDEN DE COMPRA" en la orden de compra) no traen texto ni
    // imagen, así que sin esto no se dibuja nada.
    public string ColorRelleno { get; set; }
}

internal class MarcaAguaTextoModelo
{
    public string Texto { get; set; } = "";
    public double XPt { get; set; }
    public double YPt { get; set; }
    public double AnchoPt { get; set; }
    public double AltoPt { get; set; }
    public string Color { get; set; }
    public string Fuente { get; set; }
}

internal class TablaModelo
{
    public string Nombre { get; set; } = "";
    public List<double> ColumnasPt { get; set; } = new();
    public List<FilaModelo> Filas { get; set; } = new();

    // fo:margin-left declarado por la tabla (suele ser NEGATIVO: la tabla se sale del
    // área de texto). Cada tabla tiene el suyo; no hay que aplicar el mayor a todas.
    public double MargenIzqPt { get; set; }

    // table:align del estilo de tabla: "left" (default), "center" o "right". Las tablas
    // más angostas que el área de texto se centran (la de sueldo del contrato CCI).
    public string AlineacionTabla { get; set; } = "";

    public double AnchoTotalPt => ColumnasPt.Sum();
}

internal class FilaModelo
{
    public List<CeldaModelo> Celdas { get; set; } = new();


    // style:row-height del estilo de la fila: alto EXACTO que fija el documento (0 = no fija)
    public double AltoFijoPt { get; set; }

    // style:min-row-height del estilo de la fila (0 = sin mínimo declarado)
    public double AltoMinimoPt { get; set; }
}

internal class CeldaModelo
{
    public int RepetirColumnas { get; set; } = 1;
    public int SpanFilas { get; set; } = 1;
    public int SpanColumnas { get; set; } = 1;
    public List<ParrafoModelo> Parrafos { get; set; } = new();

    // las tablas de estos reportes llevan tablas DENTRO de una celda (la cabecera de
    // columnas, por ejemplo). Si no se extraen, ese contenido queda fuera del PDF.
    public List<TablaModelo> TablasAnidadas { get; set; } = new();

    // bordes tal como los declara el estilo de la celda (fo:border-*)
    public bool BordeIzq { get; set; }
    // style:vertical-align de la celda: top | middle | bottom ("" = no declarado)
    public string VerticalAlign { get; set; } = "";
    public double GrosorBordePt { get; set; }    // fo:border "0.5pt ...": grosor en puntos (0 = 0.7 por omisión)
    public string ColorBordeIzq { get; set; }
    public bool BordeDer { get; set; }
    public string ColorBordeDer { get; set; }
    public bool BordeSup { get; set; }
    public string ColorBordeSup { get; set; }   // "#rrggbb" tal como lo declara fo:border-*
    public bool BordeInf { get; set; }
    public string ColorBordeInf { get; set; }

    public double PaddingIzqPt { get; set; }
    public double PaddingDerPt { get; set; }
    public double PaddingSupPt { get; set; }
    public double PaddingInfPt { get; set; }

    public string ColorFondo { get; set; }   // fo:background-color de la celda (#rrggbb)
}
