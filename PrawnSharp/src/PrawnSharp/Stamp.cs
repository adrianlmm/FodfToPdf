namespace PrawnSharp;

/// <summary>
/// Contenido reutilizable que se dibuja en una caja (stamp). En esta fase es un
/// callback; el backend lo ejecuta cada vez que se coloca. No es todavía un XObject
/// compartido, pero permite reutilizar logos/footers sin duplicar código.
/// </summary>
public sealed class Stamp
{
    internal Stamp(double width, double height, Action<BoundingBox> draw)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "El stamp debe tener dimensiones positivas.");
        }

        Width = width;
        Height = height;
        Draw = draw ?? throw new ArgumentNullException(nameof(draw));
    }

    public double Width { get; }

    public double Height { get; }

    internal Action<BoundingBox> Draw { get; }
}
