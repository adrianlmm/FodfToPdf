namespace PrawnSharp;

/// <summary>
/// Métricas de una fuente para el layout: anchos, ascenso/descenso y altura de línea.
/// La implementan las fuentes base-14 (AFM) y las TTF/OTF del backend.
/// </summary>
public interface IFontMetrics
{
    /// <summary>Ancho de una cadena en puntos.</summary>
    double WidthOf(string text, double fontSize, bool kerning);

    /// <summary>Altura sobre la línea base, en puntos.</summary>
    double AscentAt(double fontSize);

    /// <summary>Profundidad bajo la línea base (negativa), en puntos.</summary>
    double DescentAt(double fontSize);

    /// <summary>Altura de línea de la fuente, en puntos.</summary>
    double LineHeightAt(double fontSize);

    /// <summary>Posición del subrayado respecto a la línea base, en puntos (negativa = abajo).</summary>
    double UnderlinePositionAt(double fontSize);

    /// <summary>Grosor del subrayado, en puntos.</summary>
    double UnderlineThicknessAt(double fontSize);
}
