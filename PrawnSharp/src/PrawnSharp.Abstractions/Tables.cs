using System.Numerics;

namespace PrawnSharp;

[Flags]
public enum BorderSides
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8,
    All = Left | Top | Right | Bottom,
}

/// <summary>Estilo de una celda de tabla.</summary>
public sealed record TableCellStyle
{
    public Thickness Padding { get; init; } = new(4);

    public PdfColor? Background { get; init; }

    public PdfColor? BorderColor { get; init; }

    public double BorderWidth { get; init; } = 0.5;

    /// <summary>Lados que se dibujan cuando <see cref="BorderColor"/> no es nulo.</summary>
    public BorderSides Borders { get; init; } = BorderSides.All;

    public TextAlign Align { get; init; } = TextAlign.Left;

    public TextValign Valign { get; init; } = TextValign.Top;

    public double? FontSize { get; init; }

    public FontStyle? FontStyle { get; init; }

    public PdfColor? TextColor { get; init; }
}

/// <summary>Contenido y estilo de una celda.</summary>
public sealed record TableCell
{
    public string? Text { get; init; }

    public FormattedText? Formatted { get; init; }

    public int ColSpan { get; init; } = 1;

    public int RowSpan { get; init; } = 1;

    public TableCellStyle Style { get; init; } = new();

    public static TableCell From(string text, TableCellStyle? style = null) =>
        new() { Text = text, Style = style ?? new TableCellStyle() };

    public static TableCell From(FormattedText text, TableCellStyle? style = null) =>
        new() { Formatted = text, Style = style ?? new TableCellStyle() };

    public static implicit operator TableCell(string text) => From(text);
}

/// <summary>Una fila de tabla.</summary>
public sealed class TableRow
{
    public TableRow(IEnumerable<TableCell> cells, bool isHeader = false)
    {
        ArgumentNullException.ThrowIfNull(cells);
        Cells = cells.ToArray();
        IsHeader = isHeader;
    }

    public TableRow(params TableCell[] cells) : this(cells, false)
    {
    }

    public IReadOnlyList<TableCell> Cells { get; }

    public bool IsHeader { get; }

    /// <summary>Alto mínimo o fijo de la fila en puntos; null = sólo contenido.</summary>
    public double? MinHeight { get; init; }

    public static TableRow Header(params TableCell[] cells) => new(cells, true);
}

/// <summary>Tabla simple basada en filas y celdas.</summary>
public sealed class Table
{
    public Table(IEnumerable<TableRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Rows = rows.ToArray();
    }

    public Table(params TableRow[] rows) : this((IEnumerable<TableRow>)rows)
    {
    }

    public IReadOnlyList<TableRow> Rows { get; }

    /// <summary>Anchos de columna absolutos; si es nulo se calculan automáticamente.</summary>
    public IReadOnlyList<double>? ColumnWidths { get; init; }

    /// <summary>Ancho total de la tabla; si es nulo usa el ancho disponible.</summary>
    public double? Width { get; init; }

    /// <summary>Número de filas iniciales que se repiten al cambiar de página.</summary>
    public int HeaderRows { get; init; }

    public TableCellStyle DefaultCellStyle { get; init; } = new();

    public static Table FromStrings(params string[][] rows) =>
        new(rows.Select(row => new TableRow(row.Select(text => TableCell.From(text)).ToArray())));
}

/// <summary>Opciones de colocación de una tabla.</summary>
public sealed record TableOptions
{
    public double? Width { get; init; }

    public double? AtX { get; init; }

    public double? AtY { get; init; }

    public int? HeaderRows { get; init; }

    public TableCellStyle? DefaultCellStyle { get; init; }
}
