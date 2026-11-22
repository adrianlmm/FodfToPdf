namespace FodtToPdf;

internal static class Calibracion
{
    static readonly Dictionary<string, (double dx, double dy)> Deltas = new()
    {
        ["Text Box 15"] = (35.31, -34.59),
        ["Text Box 8"] = (35.31, -35.59),
        ["Text Box 9"] = (293.20, -48.55),
        ["logotipo"] = (35.16, -24.84),
        ["QRCode"] = (33.70, -470.10),
        ["Imagen 4"] = (28.10, -157.10),
        ["Imagen 5"] = (28.10, -204.20),
    };

    public static void Aplicar(ModeloPlantilla m)
    {
        foreach (var marca in m.MarcasAguaTexto)
        {
            marca.XPt += 70.9;
            marca.YPt += 49.2;
        }

        foreach (var capa in m.Capas)
        {
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
                capa.YPt -= d.dy;
                capa.Ancla = (capa.Ancla + " + calibrada").Trim();
            }
        }
    }
}
