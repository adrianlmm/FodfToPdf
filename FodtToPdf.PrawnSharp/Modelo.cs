namespace FodtToPdf;

internal class ModeloPlantilla
{
    public string Archivo { get; set; } = "";
    public PaginaModelo Pagina { get; set; } = new();
    public List<BloqueModelo> Flujo { get; set; } = new();
    public List<SeccionModelo> Secciones { get; set; } = new();
    public List<CapaModelo> Capas { get; set; } = new();
    public List<CapaModelo> MarcasAgua { get; set; } = new();
    public List<CapaModelo> NumerosPagina { get; set; } = new();
    public List<MarcaAguaTextoModelo> MarcasAguaTexto { get; set; } = new();
    public List<ParrafoModelo> Encabezado { get; set; } = new();
    public List<ParrafoModelo> Pie { get; set; } = new();
    public double DesplazamientoIzqPt { get; set; }
    public List<string> Bookmarks { get; set; } = new();
    public Dictionary<string, PaginaMaestraModelo> PaginasMaestras { get; set; } = new(StringComparer.Ordinal);
    public string PaginaMaestraInicial { get; set; } = "";
    public bool JustificarConEncogimiento { get; set; }
    public bool TabsRelativosASangria { get; set; }
}

internal class PaginaMaestraModelo
{
    public string Nombre { get; set; } = "";
    public PaginaModelo Pagina { get; set; } = new();
    public List<ParrafoModelo> Encabezado { get; set; } = new();
    public List<ParrafoModelo> Pie { get; set; } = new();
    public List<CapaModelo> MarcasAgua { get; set; } = new();
    public List<CapaModelo> NumerosPagina { get; set; } = new();
    public List<MarcaAguaTextoModelo> MarcasAguaTexto { get; set; } = new();
}

internal class SeccionModelo
{
    public string Nombre { get; set; } = "";
    public int Columnas { get; set; } = 1;
    public List<double> RelAnchos { get; set; } = new();
    public List<double> SeparacionPt { get; set; } = new();
    public List<double> MargenIzqColPt { get; set; } = new();
    public List<double> MargenDerColPt { get; set; } = new();
}

internal class PaginaModelo
{
    public double AnchoPt { get; set; } = 612;
    public double AltoPt { get; set; } = 792;
    public double MargenSupPt { get; set; } = 56.69;
    public double MargenInfPt { get; set; } = 56.69;
    public double MargenIzqPt { get; set; } = 56.69;
    public double MargenDerPt { get; set; } = 56.69;
    public double AltoEncabezadoPt { get; set; }
    public double AltoPiePt { get; set; }
    public double MargenSupBasePt { get; set; } = 56.69;
    public double MargenInfBasePt { get; set; } = 56.69;
}

internal class BloqueModelo
{
    public string Tipo { get; set; } = "texto";   // "texto" | "tabla" | "espacio"
    public ParrafoModelo Parrafo { get; set; }
    public TablaModelo Tabla { get; set; }
    public double AltoPt { get; set; }
    public int Seccion { get; set; }
    public int IndiceFlujo { get; set; } = -1;
}

internal class LineaModelo
{
    public double XPt { get; set; }
    public double AnchoPt { get; set; }
    public double YPt { get; set; }
    public double GrosorPt { get; set; }
    public string Color { get; set; }
}

internal class TabStopModelo
{
    public double PosicionPt { get; set; }
    public string Tipo { get; set; } = "left"; // left | center | right
}

internal class ParrafoModelo
{
    public List<RunModelo> Runs { get; set; } = new();
    public bool EsTitulo { get; set; }
    public List<LineaModelo> Lineas { get; set; } = new();
    public double SangriaColgantePt { get; set; }
    public double MargenIncisoPt { get; set; }
    public bool BordeParrafo { get; set; }
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
    public double MargenDerPt { get; set; }
    public double MargenSupPt { get; set; }
    public double MargenInfPt { get; set; }
    public double SangriaPt { get; set; }
    public bool SaltoPaginaAntes { get; set; }
    public string PaginaMaestra { get; set; }
    public bool SaltoColumnaAntes { get; set; }

    // Alto de línea REAL en puntos PDF (sin factor artificial de QuestPDF).
    public double AltoLineaPt { get; set; }
    public double AltoLineaPct { get; set; }
    public double AltoMarcaPt { get; set; }
    public bool AltoLineaFijo { get; set; }

    public double TamanoBasePt { get; set; }
    public string FuenteBase { get; set; } = "Arial";
    public bool Contextual { get; set; }
    public string EstiloComun { get; set; } = "";
    public int Huerfanas { get; set; }
    public int Viudas { get; set; }
    public bool MantenerJunto { get; set; }
    public bool MantenerConSiguiente { get; set; }
    public List<TabStopModelo> Tabs { get; set; } = new();
    public double AltoObjetoPt { get; set; }
    public string ImagenBase64 { get; set; }
    public string ImagenTipo { get; set; }
    public double ImagenAnchoPt { get; set; }
    public double ImagenAltoPt { get; set; }
    public bool ImagenEnFlujo { get; set; }
}

internal class RunModelo
{
    public string Texto { get; set; } = "";
    public string Fuente { get; set; } = "Arial";
    public double TamanoPt { get; set; } = 10;
    public bool Negrita { get; set; }
    public bool Cursiva { get; set; }
    public bool Subrayado { get; set; }
    public string Color { get; set; } = "#000000";
    public bool ColorAutomatico { get; set; } = true;
    public bool Kerning { get; set; } = true;
    public double EspaciadoCaracteresPt { get; set; }
    public string Slot { get; set; }
    public double? EscalaPosicion { get; set; }
    public string Posicion { get; set; }
    public string Hipervinculo { get; set; }
    public string CampoDinamico { get; set; }
}

internal class CapaModelo
{
    public string Nombre { get; set; } = "";
    public string Tipo { get; set; } = "custom-shape";
    public double XPt { get; set; }
    public double YPt { get; set; }
    public double AnchoPt { get; set; }
    public double AltoPt { get; set; }
    public string Ancla { get; set; } = "";
    public string VerticalRel { get; set; } = "";
    public string HorizontalRel { get; set; } = "";
    public List<TablaModelo> Tablas { get; set; } = new();
    public List<ParrafoModelo> Parrafos { get; set; } = new();
    public string ImagenBase64 { get; set; }
    public string ImagenTipo { get; set; }
    public string ModoColor { get; set; } = "";
    public string VerticalPos { get; set; } = "";
    public string HorizontalPos { get; set; } = "";
    public bool EnFlujo { get; set; }
    public bool EsNumeroPaginaActual { get; set; }
    public bool EnPie { get; set; }
    public string ColorTrazo { get; set; }
    public double GrosorTrazoPt { get; set; }
    public int BloqueAncla { get; set; } = -1;
    public bool AnclaEnTabla { get; set; }
    public double AltoParrafoAnclaPt { get; set; }
    public double ReservarFlujoPt { get; set; }
    public double AltoContenidoPt { get; set; }
    public string ColorRelleno { get; set; }
    public string Wrap { get; set; } = "";
    public string RunThrough { get; set; } = "";
    public bool BordeCaja { get; set; }
    public string ColorBorde { get; set; }
    public double GrosorBordePt { get; set; }
    public double PaddingIzqPt { get; set; }
    public double PaddingDerPt { get; set; }
    public double PaddingSupPt { get; set; }
    public double PaddingInfPt { get; set; }
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
    public string VerticalPos { get; set; } = "";
    public string HorizontalPos { get; set; } = "";
    public string VerticalRel { get; set; } = "";
    public string HorizontalRel { get; set; } = "";
}

internal class TablaModelo
{
    public string Nombre { get; set; } = "";
    public List<double> ColumnasPt { get; set; } = new();
    public List<FilaModelo> Filas { get; set; } = new();
    public double MargenIzqPt { get; set; }
    public string AlineacionTabla { get; set; } = "";
    public double AnchoDeclaradoPt { get; set; }
    public bool SolapadaPorFondo { get; set; }
    public double AnchoTotalPt => AnchoDeclaradoPt > 0 ? AnchoDeclaradoPt : ColumnasPt.Sum();
}

internal class FilaModelo
{
    public List<CeldaModelo> Celdas { get; set; } = new();
    public double AltoFijoPt { get; set; }
    public double AltoMinimoPt { get; set; }
    public bool MantenerJunta { get; set; }
}

internal class CeldaModelo
{
    public int RepetirColumnas { get; set; } = 1;
    public int SpanFilas { get; set; } = 1;
    public int SpanColumnas { get; set; } = 1;
    public List<ParrafoModelo> Parrafos { get; set; } = new();
    public List<TablaModelo> TablasAnidadas { get; set; } = new();
    public List<(bool EsTabla, int Indice)> OrdenContenido { get; set; } = new();
    public List<CapaModelo> MarcosFlotantes { get; set; } = new();
    public bool BordeIzq { get; set; }
    public string VerticalAlign { get; set; } = "";
    public double GrosorBordePt { get; set; }
    public double GrosorBordeIzqPt { get; set; }
    public double GrosorBordeDerPt { get; set; }
    public double GrosorBordeSupPt { get; set; }
    public double GrosorBordeInfPt { get; set; }
    public string ColorBordeIzq { get; set; }
    public bool BordeDer { get; set; }
    public string ColorBordeDer { get; set; }
    public bool BordeSup { get; set; }
    public string ColorBordeSup { get; set; }
    public bool BordeInf { get; set; }
    public string ColorBordeInf { get; set; }
    public double PaddingIzqPt { get; set; }
    public double PaddingDerPt { get; set; }
    public double PaddingSupPt { get; set; }
    public double PaddingInfPt { get; set; }
    public string ColorFondo { get; set; }
}
