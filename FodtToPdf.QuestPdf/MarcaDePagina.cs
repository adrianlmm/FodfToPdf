using QuestPDF.Elements;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace FodtToPdf;

// Marcador invisible: no dibuja nada (alto 0) y sólo anota en qué página cayó cada bloque
// del flujo. Sirve para descubrir, en una primera pasada, cuáles bloques empiezan página;
// en la segunda pasada se les colapsa el espacio anterior, como hace LibreOffice.
internal sealed class MarcaDePagina : IDynamicComponent
{
    public int Indice { get; init; }

    public DynamicComponentComposeResult Compose(DynamicContext context)
    {
        PaginasDeBloques.Anotar(Indice, context.PageNumber);

        return new DynamicComponentComposeResult
        {
            HasMoreContent = false,
            Content = context.CreateElement(c => c.Height(0)),
        };
    }
}

internal static class PaginasDeBloques
{
    public static readonly Dictionary<int, int> Paginas = new();

    public static void Limpiar() => Paginas.Clear();

    public static void Anotar(int indice, int pagina) => Paginas[indice] = pagina;

    // Bloques que arrancan en una pagina distinta a la del bloque anterior. Si el que
    // abre pagina es un parrafo vacio, se marcan TAMBIEN los vacios que le siguen:
    // LibreOffice no gasta una racha de renglones en blanco al principio de hoja y
    // nosotros acumulabamos hasta quince (180 pt de hueco en el contrato CCI).
    public static HashSet<int> QueEmpiezanPagina(IReadOnlyList<BloqueModelo> flujo)
    {
        var salida = new HashSet<int>();

        foreach (var par in Paginas)
        {
            var anterior = Paginas.TryGetValue(par.Key - 1, out var p) ? p : par.Value;

            if (anterior == par.Value) continue;

            salida.Add(par.Key);

            for (var j = par.Key + 1; j < flujo.Count && j < par.Key + 40; j++)
            {
                if (flujo[j].Tipo != "espacio") break;
                if (Paginas.TryGetValue(j, out var pj) && pj != par.Value) break;

                salida.Add(j);
            }
        }

        return salida;
    }
}
