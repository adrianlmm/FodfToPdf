namespace PrawnSharp;

/// <summary>
/// Layout de tablas. Calcula una cuadrícula con colspan/rowspan, anchos automáticos,
/// altos por fila, saltos de página y repetición de encabezados.
/// </summary>
internal static class TableLayout
{
    private sealed record Placement(int Row, int Column, int RowSpan, int ColSpan, TableCell Cell);

    private sealed record Layout(
        Table Table,
        int Columns,
        double[] Widths,
        double[] Heights,
        List<Placement> Placements,
        int HeaderRows);

    public static void Render(PrawnDocument document, Table table, TableOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(table);

        if (table.Rows.Count == 0)
        {
            return;
        }

        var layout = Build(document, table, options);
        var x = options?.AtX ?? document.Bounds.Left;
        var y = options?.AtY ?? document.CursorY;
        var pageBottom = document.Bounds.Bottom;
        var headerRows = layout.HeaderRows;

        for (var row = 0; row < headerRows; row++)
        {
            DrawRow(document, layout, row, x, y, options);
            y += layout.Heights[row];
        }

        for (var row = headerRows; row < table.Rows.Count;)
        {
            if (y + layout.Heights[row] > pageBottom && y > document.Bounds.Top + 0.5)
            {
                document.StartNewPage();
                x = options?.AtX ?? document.Bounds.Left;
                y = document.Bounds.Top;
                pageBottom = document.Bounds.Bottom;

                for (var header = 0; header < headerRows; header++)
                {
                    DrawRow(document, layout, header, x, y, options);
                    y += layout.Heights[header];
                }
            }

            DrawRow(document, layout, row, x, y, options);
            y += layout.Heights[row];
            row++;
        }

        document.MoveTo(y);
    }

    private static Layout Build(PrawnDocument document, Table table, TableOptions? options)
    {
        var rows = table.Rows;
        var occupied = new HashSet<(int Row, int Column)>();
        var placements = new List<Placement>();
        var columns = 0;

        for (var row = 0; row < rows.Count; row++)
        {
            var column = 0;

            foreach (var cell in rows[row].Cells)
            {
                while (occupied.Contains((row, column)))
                {
                    column++;
                }

                var colSpan = Math.Max(1, cell.ColSpan);
                var rowSpan = Math.Min(Math.Max(1, cell.RowSpan), rows.Count - row);

                for (var r = row; r < row + rowSpan; r++)
                {
                    for (var c = column; c < column + colSpan; c++)
                    {
                        occupied.Add((r, c));
                    }
                }

                placements.Add(new Placement(row, column, rowSpan, colSpan, cell));
                columns = Math.Max(columns, column + colSpan);
                column += colSpan;
            }
        }

        if (columns == 0)
        {
            columns = 1;
        }

        var available = options?.Width ?? table.Width ?? document.Bounds.Width;
        var widths = ResolveWidths(document, table, options, placements, columns, available);

        var heights = new double[rows.Count];

        foreach (var placement in placements.Where(p => p.RowSpan == 1))
        {
            var style = ResolveStyle(table, options, placement.Cell);
            var width = Sum(widths, placement.Column, placement.ColSpan) - style.Padding.Horizontal;
            var height = MeasureHeight(document, placement.Cell, style, width) + style.Padding.Vertical;
            heights[placement.Row] = Math.Max(heights[placement.Row], height);
        }

        foreach (var placement in placements.Where(p => p.RowSpan > 1))
        {
            var style = ResolveStyle(table, options, placement.Cell);
            var width = Sum(widths, placement.Column, placement.ColSpan) - style.Padding.Horizontal;
            var needed = MeasureHeight(document, placement.Cell, style, width) + style.Padding.Vertical;
            var current = heights.Skip(placement.Row).Take(placement.RowSpan).Sum();

            if (current < needed)
            {
                heights[placement.Row + placement.RowSpan - 1] += needed - current;
            }
        }

        for (var i = 0; i < heights.Length; i++)
        {
            heights[i] = Math.Max(heights[i], rows[i].MinHeight ?? 1);
        }

        var headerRows = options?.HeaderRows ?? table.HeaderRows;

        if (headerRows <= 0)
        {
            headerRows = 0;

            while (headerRows < rows.Count && rows[headerRows].IsHeader)
            {
                headerRows++;
            }
        }

        headerRows = Math.Clamp(headerRows, 0, rows.Count);
        return new Layout(table, columns, widths, heights, placements, headerRows);
    }

    private static double[] ResolveWidths(
        PrawnDocument document,
        Table table,
        TableOptions? options,
        List<Placement> placements,
        int columns,
        double available)
    {
        var widths = new double[columns];

        if (table.ColumnWidths is { Count: > 0 } explicitWidths && explicitWidths.Count == columns)
        {
            for (var i = 0; i < columns; i++)
            {
                widths[i] = Math.Max(explicitWidths[i], 1);
            }

            return widths;
        }

        var needed = new double[columns];

        foreach (var placement in placements)
        {
            var style = ResolveStyle(table, options, placement.Cell);
            var intrinsic = MeasureWidth(document, placement.Cell, style) + style.Padding.Horizontal;
            var share = intrinsic / placement.ColSpan;

            for (var column = placement.Column; column < placement.Column + placement.ColSpan; column++)
            {
                needed[column] = Math.Max(needed[column], share);
            }
        }

        var total = needed.Sum();

        if (total <= 0)
        {
            total = columns;
            Array.Fill(needed, 1);
        }

        var scale = available / total;

        for (var i = 0; i < columns; i++)
        {
            widths[i] = Math.Max(needed[i] * scale, 10);
        }

        return widths;
    }

    private static void DrawRow(
        PrawnDocument document,
        Layout layout,
        int row,
        double x,
        double y,
        TableOptions? options)
    {
        foreach (var placement in layout.Placements.Where(p => p.Row == row))
        {
            var style = ResolveStyle(layout.Table, options, placement.Cell);
            var cellX = x + Sum(layout.Widths, 0, placement.Column);
            var cellWidth = Sum(layout.Widths, placement.Column, placement.ColSpan);
            var cellHeight = Sum(layout.Heights, row, placement.RowSpan);

            if (style.Background is not null)
            {
                document.Rectangle(cellX, y, cellWidth, cellHeight, style.Background, null, 0);
            }

            if (style.BorderColor is { } border)
            {
                if (style.Borders == BorderSides.All)
                {
                    document.Rectangle(cellX, y, cellWidth, cellHeight, null, border, style.BorderWidth);
                }
                else
                {
                    if ((style.Borders & BorderSides.Left) != 0)
                        document.Line(cellX, y, cellX, y + cellHeight, style.BorderWidth, border);
                    if ((style.Borders & BorderSides.Top) != 0)
                        document.Line(cellX, y, cellX + cellWidth, y, style.BorderWidth, border);
                    if ((style.Borders & BorderSides.Right) != 0)
                        document.Line(cellX + cellWidth, y, cellX + cellWidth, y + cellHeight, style.BorderWidth, border);
                    if ((style.Borders & BorderSides.Bottom) != 0)
                        document.Line(cellX, y + cellHeight, cellX + cellWidth, y + cellHeight, style.BorderWidth, border);
                }
            }

            var contentWidth = Math.Max(cellWidth - style.Padding.Horizontal, 1);
            var contentHeight = Math.Max(cellHeight - style.Padding.Vertical, 1);
            var textOptions = new TextOptions
            {
                Size = style.FontSize ?? document.FontSize,
                Width = contentWidth,
                Height = contentHeight,
                Align = style.Align,
                Valign = style.Valign,
                Color = style.TextColor,
            };

            var family = document.FontFamily;
            var size = document.FontSize;

            if (style.FontSize is not null || style.FontStyle is not null)
            {
                document.Font(family, style.FontSize ?? size, style.FontStyle ?? FontStyle.Regular);
            }

            document.BoundingBox(
                new Point(
                    cellX + style.Padding.Left - document.Bounds.Left,
                    y + style.Padding.Top - document.Bounds.Top),
                contentWidth,
                contentHeight,
                _ =>
                {
                    if (placement.Cell.Formatted is not null)
                    {
                        document.FormattedText(placement.Cell.Formatted, textOptions);
                    }
                    else if (placement.Cell.Text is not null)
                    {
                        document.Text(placement.Cell.Text, textOptions);
                    }
                });

            if (style.FontSize is not null || style.FontStyle is not null)
            {
                document.Font(family, size);
            }
        }
    }

    private static TableCellStyle ResolveStyle(Table table, TableOptions? options, TableCell cell)
    {
        if (cell.Style != new TableCellStyle())
        {
            return cell.Style;
        }

        return options?.DefaultCellStyle ?? table.DefaultCellStyle;
    }

    private static double MeasureWidth(PrawnDocument document, TableCell cell, TableCellStyle style)
    {
        var text = cell.Text ?? string.Concat(cell.Formatted?.Fragments.Select(f => f.Text) ?? []);
        return document.MeasureText(text, new TextOptions { Size = style.FontSize ?? document.FontSize });
    }

    private static double MeasureHeight(PrawnDocument document, TableCell cell, TableCellStyle style, double width)
    {
        var options = new TextOptions
        {
            Size = style.FontSize ?? document.FontSize,
            Width = Math.Max(width, 1),
        };

        if (cell.Formatted is not null)
        {
            return document.HeightOf(cell.Formatted, options);
        }

        return cell.Text is null ? 0 : document.HeightOf(cell.Text, options);
    }

    private static double Sum(double[] values, int start, int count) =>
        values.Skip(start).Take(count).Sum();
}
