namespace PrawnSharp;

/// <summary>
/// Punto en coordenadas de Prawn: origen arriba-izquierda, Y crece hacia abajo.
/// El backend convierte a coordenadas PDF (origen abajo-izquierda) al dibujar.
/// </summary>
public readonly record struct Point(double X, double Y)
{
    public static readonly Point Origin = new(0, 0);

    public override string ToString() => $"({X:0.###}, {Y:0.###})";
}

/// <summary>Tamaño en puntos PDF (1 pt = 1/72 de pulgada).</summary>
public readonly record struct Size(double Width, double Height)
{
    public override string ToString() => $"{Width:0.###} x {Height:0.###}";
}

/// <summary>
/// Rectángulo en coordenadas de Prawn: <see cref="X"/>/<see cref="Y"/> son la esquina
/// superior izquierda y el alto crece hacia abajo.
/// </summary>
public readonly record struct Rect(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public Point TopLeft => new(X, Y);
    public Point TopRight => new(Right, Y);
    public Point BottomLeft => new(X, Bottom);
    public Point BottomRight => new(Right, Bottom);

    public Size Size => new(Width, Height);

    public static Rect FromLtrb(double left, double top, double right, double bottom) =>
        new(left, top, right - left, bottom - top);

    public override string ToString() => $"({X:0.###}, {Y:0.###}) {Width:0.###} x {Height:0.###}";
}

/// <summary>Márgenes en puntos. <c>new Thickness(32)</c> aplica 32 pt a los cuatro lados.</summary>
public readonly record struct Thickness(double Left, double Top, double Right, double Bottom)
{
    public Thickness(double all) : this(all, all, all, all)
    {
    }

    public Thickness(double horizontal, double vertical) : this(horizontal, vertical, horizontal, vertical)
    {
    }

    public double Horizontal => Left + Right;
    public double Vertical => Top + Bottom;

    public static implicit operator Thickness(double all) => new(all);

    public override string ToString() => $"L={Left:0.###} T={Top:0.###} R={Right:0.###} B={Bottom:0.###}";
}

/// <summary>Tamaño de página con el que se crea una hoja.</summary>
public readonly record struct PageSetup(double Width, double Height)
{
    public PageSetup(Size size) : this(size.Width, size.Height)
    {
    }

    public Size Size => new(Width, Height);
}
