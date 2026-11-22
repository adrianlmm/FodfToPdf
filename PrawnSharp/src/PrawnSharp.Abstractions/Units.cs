namespace PrawnSharp;

/// <summary>
/// Conversiones de unidades. En C# no se puede reabrir <see cref="double"/>, así que
/// se usan métodos de extensión: <c>2.Cm()</c> equivale a <c>2.cm</c> en Prawn.
/// </summary>
public static class UnitExtensions
{
    public const double PointsPerInch = 72.0;
    public const double PointsPerMillimeter = PointsPerInch / 25.4;
    public const double PointsPerCentimeter = PointsPerInch / 2.54;

    public static double Pt(this double value) => value;

    public static double Mm(this double value) => value * PointsPerMillimeter;

    public static double Cm(this double value) => value * PointsPerCentimeter;

    public static double In(this double value) => value * PointsPerInch;

    // Sobrecargas para que `2.Cm()` funcione con literales enteros, como en Prawn.
    public static double Pt(this int value) => value;

    public static double Mm(this int value) => ((double)value).Mm();

    public static double Cm(this int value) => ((double)value).Cm();

    public static double In(this int value) => ((double)value).In();
}
