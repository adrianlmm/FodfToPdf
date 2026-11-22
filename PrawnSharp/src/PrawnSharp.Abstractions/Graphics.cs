using System.Numerics;

namespace PrawnSharp;

/// <summary>Regla de relleno de un path.</summary>
public enum FillRule
{
    NonZero = 0,
    EvenOdd = 1,
}

/// <summary>Terminación de las líneas de un trazo.</summary>
public enum LineCap
{
    Butt = 0,
    Round = 1,
    Square = 2,
}

/// <summary>Unión entre segmentos de un trazo.</summary>
public enum LineJoin
{
    Miter = 0,
    Round = 1,
    Bevel = 2,
}

/// <summary>Modos de mezcla PDF (ExtGState /BM).</summary>
public enum BlendMode
{
    Normal,
    Multiply,
    Screen,
    Overlay,
    Darken,
    Lighten,
    ColorDodge,
    ColorBurn,
    HardLight,
    SoftLight,
    Difference,
    Exclusion,
    Hue,
    Saturation,
    Color,
    Luminosity,
}

public enum PathCommandKind
{
    MoveTo,
    LineTo,
    CurveTo,
    Close,
}

/// <summary>Un comando de path en coordenadas de Prawn.</summary>
public readonly record struct PathCommand(
    PathCommandKind Kind,
    Point P1 = default,
    Point P2 = default,
    Point P3 = default);

/// <summary>
/// Path de dibujo en coordenadas de Prawn (origen arriba-izquierda, Y hacia abajo).
/// Es mutable y se puede reutilizar; las coordenadas se transforman al dibujar.
/// </summary>
public sealed class Path
{
    private readonly List<PathCommand> _commands = [];

    public IReadOnlyList<PathCommand> Commands => _commands;

    public static Path Create() => new();

    public Path MoveTo(double x, double y)
    {
        _commands.Add(new PathCommand(PathCommandKind.MoveTo, new Point(x, y)));
        return this;
    }

    public Path MoveTo(Point point) => MoveTo(point.X, point.Y);

    public Path LineTo(double x, double y)
    {
        _commands.Add(new PathCommand(PathCommandKind.LineTo, new Point(x, y)));
        return this;
    }

    public Path LineTo(Point point) => LineTo(point.X, point.Y);

    public Path CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
    {
        _commands.Add(new PathCommand(
            PathCommandKind.CurveTo,
            new Point(x1, y1),
            new Point(x2, y2),
            new Point(x3, y3)));
        return this;
    }

    public Path CurveTo(Point control1, Point control2, Point end) =>
        CurveTo(control1.X, control1.Y, control2.X, control2.Y, end.X, end.Y);

    public Path Close()
    {
        _commands.Add(new PathCommand(PathCommandKind.Close));
        return this;
    }

    public Path Rectangle(double x, double y, double width, double height)
    {
        MoveTo(x, y);
        LineTo(x + width, y);
        LineTo(x + width, y + height);
        LineTo(x, y + height);
        return Close();
    }

    public Path Rectangle(Rect rect) => Rectangle(rect.X, rect.Y, rect.Width, rect.Height);

    public Path Circle(double centerX, double centerY, double radius) =>
        Ellipse(centerX, centerY, radius, radius);

    public Path Ellipse(double centerX, double centerY, double radiusX, double radiusY)
    {
        // Aproximación cúbica estándar de una elipse.
        const double k = 0.5522847498307936;
        var kx = radiusX * k;
        var ky = radiusY * k;

        MoveTo(centerX + radiusX, centerY);
        CurveTo(centerX + radiusX, centerY + ky, centerX + kx, centerY + radiusY, centerX, centerY + radiusY);
        CurveTo(centerX - kx, centerY + radiusY, centerX - radiusX, centerY + ky, centerX - radiusX, centerY);
        CurveTo(centerX - radiusX, centerY - ky, centerX - kx, centerY - radiusY, centerX, centerY - radiusY);
        CurveTo(centerX + kx, centerY - radiusY, centerX + radiusX, centerY - ky, centerX + radiusX, centerY);
        return Close();
    }

    public Path Polygon(params Point[] points)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (points.Length == 0)
        {
            return this;
        }

        MoveTo(points[0]);

        for (var i = 1; i < points.Length; i++)
        {
            LineTo(points[i]);
        }

        return Close();
    }

    public Path Append(Path other)
    {
        ArgumentNullException.ThrowIfNull(other);
        _commands.AddRange(other._commands);
        return this;
    }
}

public enum GradientKind
{
    Linear,
    Radial,
}

public readonly record struct GradientStop(double Offset, PdfColor Color);

/// <summary>Gradiente lineal o radial en coordenadas de Prawn.</summary>
public sealed record Gradient(
    GradientKind Kind,
    Point Start,
    Point End,
    double Radius,
    IReadOnlyList<GradientStop> Stops)
{
    public static Gradient Linear(Point start, Point end, params GradientStop[] stops) =>
        new(GradientKind.Linear, start, end, 0, stops);

    public static Gradient Radial(Point center, double radius, params GradientStop[] stops) =>
        new(GradientKind.Radial, center, center, radius, stops);
}
