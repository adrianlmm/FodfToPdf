using SkiaSharp;

namespace FodtToPdf;

// Paginación propia, al estilo de SwFlowFrame en Writer: mide el texto y decide dónde
// cortar la página, en vez de dejar que QuestPDF reparta a ciegas. Sirve para dos cosas
// que QuestPDF no permite: colapsar el espacio al principio de una página (como hace
// LibreOffice) y respetar el corte por bloque.
internal static class PaginarPropio
{
    static readonly Dictionary<string, SKTypeface> _tipos = new();

    static SKTypeface Tipo(string familia, bool negrita)
    {
        var clave = (familia ?? "Arial") + (negrita ? "-b" : "-n");

        if (_tipos.TryGetValue(clave, out var tipo)) return tipo;

        tipo = SKTypeface.FromFamilyName(familia ?? "Arial", negrita ? SKFontStyle.Bold : SKFontStyle.Normal)
               ?? SKTypeface.Default;

        _tipos[clave] = tipo;

        return tipo;
    }

    public static double Medir(string texto, string familia, double tamano, bool negrita, bool kerning = true)
    {
        if (string.IsNullOrEmpty(texto) || tamano <= 0) return 0;

        using var fuente = new SKFont(Tipo(familia, negrita), (float)tamano);

        var ancho = (double)fuente.MeasureText(texto);

        // Skia mide los avances pero NO aplica la tabla kern del TTF.
        // El ajuste sale del mismo parser que usa PrawnSharp.
        if (kerning)
            ancho += KerningMetrics.Ajuste(texto, string.IsNullOrWhiteSpace(familia) ? "Arial" : familia, negrita, tamano);

        return ancho;
    }

    // nº de líneas del párrafo al ancho disponible: corte por palabras, como el motor de Writer
    static int Lineas(ParrafoModelo p, double ancho)
    {
        var partes = new List<(string Texto, string Fuente, double Tamano, bool Negrita, bool EsEspacio)>();

        foreach (var r in p.Runs)
        {
            if (string.IsNullOrEmpty(r.Texto)) continue;

            var trozos = r.Texto.Split(' ');

            for (var k = 0; k < trozos.Length; k++)
            {
                if (k > 0) partes.Add((" ", r.Fuente, r.TamanoPt, r.Negrita, true));
                partes.Add((trozos[k], r.Fuente, r.TamanoPt, r.Negrita, false));
            }
        }

        if (partes.Count == 0) return 1;
        if (ancho <= 1) return partes.Count;

        var lineas = 1;
        var usado = 0.0;
        var espacio = 0.0;

        foreach (var w in partes)
        {
            if (w.EsEspacio)
            {
                espacio = Medir(" ", w.Fuente, w.Tamano, w.Negrita);
                continue;
            }

            var anchoPalabra = Medir(w.Texto, w.Fuente, w.Tamano, w.Negrita);
            var necesario = usado > 0 ? espacio + anchoPalabra : anchoPalabra;

            if (usado > 0 && usado + necesario > ancho)
            {
                lineas++;
                usado = anchoPalabra;
            }
            else
            {
                usado += necesario;
            }
        }

        return lineas;
    }

    static double AltoLinea(ParrafoModelo p)
        => p.AltoLineaPt > 0 ? p.AltoLineaPt : (p.TamanoBasePt > 0 ? p.TamanoBasePt * 1.15 : 12);

    public static double AltoParrafo(ParrafoModelo p, double ancho)
    {
        if (p == null) return 0;

        var util = Math.Max(ancho - p.MargenIzqPt, 20);

        var alto = Lineas(p, util) * AltoLinea(p) + p.MargenSupPt + p.MargenInfPt;

        if (p.AltoObjetoPt > alto) alto = p.AltoObjetoPt;

        return alto;
    }

    public static double AltoCelda(CeldaModelo c, double ancho)
    {
        var alto = c.PaddingSupPt + c.PaddingInfPt;

        foreach (var p in c.Parrafos)
            alto += AltoParrafo(p, ancho - c.PaddingIzqPt - c.PaddingDerPt);

        return alto;
    }

    public static double AltoTabla(TablaModelo t, double ancho)
    {
        var alto = 0.0;
        var columnas = t.ColumnasPt.Count > 0 ? t.ColumnasPt : new List<double> { ancho };

        var totalColumnas = columnas.Sum();

        if (t.AnchoDeclaradoPt > 0 && totalColumnas > 0 && ancho > 0)
        {
            var factor = Math.Min(t.AnchoDeclaradoPt, ancho) / totalColumnas;
            columnas = columnas.Select(c => c * factor).ToList();
        }

        foreach (var fila in t.Filas)
        {
            var altoFila = fila.AltoFijoPt > 0 ? fila.AltoFijoPt : fila.AltoMinimoPt;
            var columna = 0;

            foreach (var celda in fila.Celdas)
            {
                var span = Math.Max(1, celda.SpanColumnas);
                var anchoCelda = 0.0;

                for (var k = 0; k < span && columna + k < columnas.Count; k++)
                    anchoCelda += columnas[columna + k];

                if (anchoCelda <= 0 && columna < columnas.Count)
                    anchoCelda = columnas[columna];

                var contenido = AltoCelda(celda, anchoCelda);

                if (contenido > altoFila) altoFila = contenido;

                columna += span;
            }

            if (altoFila <= 0) altoFila = 12;

            alto += altoFila;
        }

        return alto;
    }

    public static double Alto(BloqueModelo b, double ancho)
    {
        if (b.Tipo == "espacio") return b.AltoPt > 0 ? b.AltoPt : 12;
        if (b.Tipo == "texto") return AltoParrafo(b.Parrafo, ancho);
        if (b.Tipo == "tabla") return AltoTabla(b.Tabla, ancho);

        return 0;
    }

    // Marca qué bloques deben empezar página
    public static Dictionary<BloqueModelo, bool> Cortes(ModeloPlantilla m, double altoPagina, double ancho)
    {
        var cortes = new Dictionary<BloqueModelo, bool>();
        var y = 0.0;

        foreach (var b in m.Flujo)
        {
            var alto = Alto(b, ancho);

            if (y > 0.5 && y + alto > altoPagina)
            {
                cortes[b] = true;
                y = alto;
            }
            else
            {
                y += alto;
            }
        }

        return cortes;
    }
}
