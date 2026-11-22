namespace PrawnSharp;

/// <summary>
/// Caja de coordenadas anidada. Se crea con <see cref="PrawnDocument.BoundingBox"/> y
/// sólo existe durante el bloque: al salir se restauran los límites y el cursor.
/// </summary>
public sealed class BoundingBox
{
    private readonly PrawnDocument _document;

    internal BoundingBox(PrawnDocument document, Rect rect)
    {
        _document = document;
        Rect = rect;
    }

    public Rect Rect { get; }

    public double Left => Rect.Left;

    public double Top => Rect.Top;

    public double Right => Rect.Right;

    public double Bottom => Rect.Bottom;

    public double Width => Rect.Width;

    public double Height => Rect.Height;

    public double CursorY => _document.CursorY;

    public void Text(string text, TextOptions? options = null) => _document.Text(text, options);

    public void MoveDown(double points) => _document.MoveDown(points);

    public void MoveUp(double points) => _document.MoveUp(points);
}
