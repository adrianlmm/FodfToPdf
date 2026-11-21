namespace FodtToPdf;

// Calibración del anclaje de las cajas e imágenes.
//
// En el .fodt, las cajas con text:anchor-type="char" (o "as-char") tienen svg:x/svg:y
// RELATIVOS al carácter de anclaje, que lo coloca el flujo. Por eso sus coordenadas no
// se pueden usar tal cual en una capa absoluta: hay que sumarles dónde cae el ancla.
//
// Los deltas se miden UNA vez comparando el PDF de LibreOffice de la misma factura
// contra las coordenadas del .fodt (herramientas: C:\tmp\deltas.js e C:\tmp\imagenes.js).
//
// Ojo con el signo: en el .fodt el eje Y crece hacia abajo y en el PDF hacia arriba,
// así que el delta de Y se aplica restando sobre la coordenada del modelo.
internal static class Calibracion
{
    static readonly Dictionary<string, (double dx, double dy)> Deltas = new()
    {
        // deltas medidos sobre la PLANTILLA vacía (PlantillaFactura_V3-3.fodt)
        ["Text Box 15"] = (35.31, -34.59),
        ["Text Box 8"] = (35.31, -35.59),
        ["Text Box 9"] = (293.20, -48.55),

        // deltas medidos sobre una FACTURA YA GENERADA (Factura_12390705.fodt):
        // el logo acaba en x=35.5 y=691 (desde abajo) y el QR en x=33.7 y=212.
        ["logotipo"] = (35.16, -24.84),
        ["QRCode"] = (33.70, -470.10),

        // las tres líneas grises del encabezado (222x1 pt): en la factura real están en
        // x=28.4 con y desde arriba 160.0 (Imagen 4) y 215.4 (Imagen 5); Imagen 2 ya
        // caía bien sin corrección (y=674 en coordenadas de PDF).
        ["Imagen 4"] = (28.10, -157.10),
        ["Imagen 5"] = (28.10, -204.20),
    };

    public static void Aplicar(ModeloPlantilla m)
    {
        // Marca de agua de TEXTO del formato de la orden de compra inválida («COPIA NO VALIDA»).
        // El shape declara svg:x=0 svg:y=10,926 cm, pero el encabezado del master-page lo
        // coloca en otra parte: MEDIDO contra el PDF de LibreOffice (sus contornos vectoriales),
        // el texto estirado ocupa x=70,9..555,1 e y=358,9..425,5 (484,3 x 66,6 pt).
        foreach (var marca in m.MarcasAguaTexto)
        {
            marca.XPt += 70.9;
            marca.YPt += 49.2;
        }

        foreach (var capa in m.Capas)
        {
            // Fondo gris con los recuadros del formato de dividendos. Es la única capa
            // conocida que arranca POR ENCIMA de su párrafo ancla (svg:y = -3,717 cm): su
            // coordenada relativa no sirve como coordenada de página y se dibujaba desde el
            // borde superior de la hoja. MEDIDO contra el PDF de LibreOffice de la misma
            // plantilla. Se identifica por nombre Y tamaño porque otras 15 plantillas
            // también tienen una capa llamada "Imagen1".
            if (capa.Nombre == "Imagen1" && capa.YPt < 0 &&
                Math.Abs(capa.AnchoPt - 573.99) < 1 && Math.Abs(capa.AltoPt - 137.00) < 1)
            {
                capa.XPt = 19.10;
                capa.YPt = 50.15;
                capa.Ancla = (capa.Ancla + " + calibrada").Trim();

                continue;
            }

            if (Deltas.TryGetValue(capa.Nombre, out var d))
            {
                if (capa.Nombre == "Text Box 9" && capa.BloqueAncla > 0 && capa.BloqueAncla < m.Flujo.Count)
                {
                    // Suma los espaciadores de su columna desde el inicio de columna
                    // (fo:break-before="column") hasta el párrafo ancla: así funciona tanto con
                    // 2 renglones vacíos (PlantillaFactura) como con 3 (Carta Porte).
                    var yColumna = 0.0;
                    var seccionAncla = m.Flujo[capa.BloqueAncla].Seccion;
                    for (var k = capa.BloqueAncla - 1; k >= 0; k--)
                    {
                        var b = m.Flujo[k];
                        if (b.Seccion != seccionAncla) break;
                        if (b.Tipo == "espacio") yColumna += b.AltoPt;
                        if (b.Parrafo?.SaltoColumnaAntes == true) break;
                    }

                    var primerTam = capa.Parrafos
                        .SelectMany(p => p.Runs)
                        .Where(r => !string.IsNullOrWhiteSpace(r.Texto) && r.TamanoPt > 0)
                        .Select(r => r.TamanoPt)
                        .DefaultIfEmpty(8.0)
                        .First();

                    capa.XPt += d.dx;
                    capa.YPt = m.Pagina.MargenSupPt + yColumna + capa.YPt + capa.PaddingSupPt + primerTam * 0.9;
                    capa.Ancla = (capa.Ancla + " + calibrada").Trim();
                    continue;
                }

                capa.XPt += d.dx;
                capa.YPt -= d.dy;     // dy negativo = bajar la caja
                capa.Ancla = (capa.Ancla + " + calibrada").Trim();
            }
        }
    }
}
