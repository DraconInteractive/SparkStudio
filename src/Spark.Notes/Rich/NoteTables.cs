using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;

namespace Spark.Notes.Rich;

/// <summary>
/// Markdown (pipe) tables in a note's FlowDocument: construction and structural edits. The first row is always the
/// header row; column alignment lives on every cell paragraph of the column (the serializer reads the header's).
/// </summary>
public static class NoteTables
{
    /// <summary>A new table with a header row and <paramref name="bodyRows"/> empty rows.</summary>
    public static Table Create(int columns, int bodyRows, IReadOnlyList<string>? headers = null)
    {
        columns = Math.Max(1, columns);
        var table = NewShell(columns);
        var group = table.RowGroups[0];
        group.Rows.Add(NewRow(columns, header: true, _ => TextAlignment.Left));
        for (int r = 0; r < Math.Max(0, bodyRows); r++) group.Rows.Add(NewRow(columns, header: false, _ => TextAlignment.Left));
        if (headers != null)
            for (int c = 0; c < columns && c < headers.Count; c++)
                CellParagraph(group.Rows[0].Cells[c]).Inlines.Add(new Run(headers[c]));
        return table;
    }

    /// <summary>An empty table with its columns and one row group (used by the Markdown converter too).</summary>
    public static Table NewShell(int columns)
    {
        var table = new Table
        {
            Tag = new BlockInfo(BlockKind.Table),
            CellSpacing = 0,
            BorderBrush = NoteDocument.RuleBrush,
            BorderThickness = new Thickness(1, 1, 0, 0),
            Margin = new Thickness(0, 0, 0, 10),
        };
        for (int i = 0; i < columns; i++) table.Columns.Add(new TableColumn());
        table.RowGroups.Add(new TableRowGroup());
        return table;
    }

    public static TableCell NewCell(bool header, TextAlignment align, Paragraph? content = null)
    {
        var p = content ?? new Paragraph();
        NoteDocument.Apply(p, new BlockInfo(BlockKind.Paragraph));
        p.Margin = new Thickness(0);
        p.TextAlignment = align;
        if (header) p.FontWeight = FontWeights.SemiBold;
        return new TableCell(p)
        {
            BorderBrush = NoteDocument.RuleBrush,
            BorderThickness = new Thickness(0, 0, 1, 1),
            Padding = new Thickness(8, 4, 8, 4),
            Background = header ? NoteDocument.CodeBackground : null,
        };
    }

    private static TableRow NewRow(int columns, bool header, Func<int, TextAlignment> align)
    {
        var row = new TableRow();
        for (int c = 0; c < columns; c++) row.Cells.Add(NewCell(header, align(c)));
        return row;
    }

    // ---------- Locating ----------

    /// <summary>The table cell containing a position, if any.</summary>
    public static TableCell? CellAt(TextPointer? pos)
    {
        for (DependencyObject? o = pos?.Parent; o != null; o = (o as FrameworkContentElement)?.Parent)
            if (o is TableCell c) return c;
        return null;
    }

    public static TableRow RowOf(TableCell cell) => (TableRow)cell.Parent;
    public static Table TableOf(TableCell cell) => (Table)((TableRowGroup)RowOf(cell).Parent).Parent;
    public static List<TableRow> Rows(Table t) => t.RowGroups.SelectMany(g => g.Rows).ToList();
    public static int ColumnCount(Table t) => Math.Max(t.Columns.Count, Rows(t).Select(r => r.Cells.Count).DefaultIfEmpty(0).Max());
    public static (int Row, int Column) IndexOf(TableCell cell) =>
        (Rows(TableOf(cell)).IndexOf(RowOf(cell)), RowOf(cell).Cells.IndexOf(cell));

    public static TableCell? CellAt(Table t, int row, int column)
    {
        var rows = Rows(t);
        if (row < 0 || row >= rows.Count) return null;
        var cells = rows[row].Cells;
        return column >= 0 && column < cells.Count ? cells[column] : null;
    }

    /// <summary>The cell's paragraph (cells always hold exactly one; extra blocks are merged lazily by the serializer).</summary>
    public static Paragraph CellParagraph(TableCell cell)
    {
        if (cell.Blocks.FirstBlock is Paragraph p) return p;
        var np = new Paragraph { Margin = new Thickness(0) };
        cell.Blocks.Add(np);
        return np;
    }

    /// <summary>Where the caret goes to type in a cell (the end of its text).</summary>
    public static TextPointer CaretIn(TableCell cell) => CellParagraph(cell).ContentEnd;

    // ---------- Structure ----------

    public static TextAlignment AlignmentOf(Table t, int column) =>
        CellAt(t, 0, column) is { } c ? CellParagraph(c).TextAlignment : TextAlignment.Left;

    /// <summary>Inserts an empty body row at <paramref name="index"/> (at least 1: the header stays first).</summary>
    public static TableRow InsertRow(Table t, int index)
    {
        var rows = Rows(t);
        index = Math.Clamp(index, 1, rows.Count);
        var row = NewRow(ColumnCount(t), header: false, c => AlignmentOf(t, c));
        var group = t.RowGroups[0];
        if (index >= rows.Count) group.Rows.Add(row);
        else
        {
            var before = rows[index];
            ((TableRowGroup)before.Parent).Rows.Insert(((TableRowGroup)before.Parent).Rows.IndexOf(before), row);
        }
        return row;
    }

    /// <summary>Inserts an empty column at <paramref name="index"/>.</summary>
    public static void InsertColumn(Table t, int index)
    {
        index = Math.Clamp(index, 0, ColumnCount(t));
        t.Columns.Insert(Math.Min(index, t.Columns.Count), new TableColumn());
        var rows = Rows(t);
        for (int r = 0; r < rows.Count; r++)
        {
            var cell = NewCell(header: r == 0, TextAlignment.Left);
            var cells = rows[r].Cells;
            if (index >= cells.Count) cells.Add(cell);
            else cells.Insert(index, cell);
        }
    }

    /// <summary>Deletes a body row. The header row can't be deleted (delete the table instead).</summary>
    public static bool DeleteRow(Table t, int index)
    {
        var rows = Rows(t);
        if (index < 1 || index >= rows.Count) return false;
        ((TableRowGroup)rows[index].Parent).Rows.Remove(rows[index]);
        return true;
    }

    /// <summary>Deletes a column; returns false if it is the only one (delete the table instead).</summary>
    public static bool DeleteColumn(Table t, int index)
    {
        if (ColumnCount(t) <= 1 || index < 0) return false;
        foreach (var row in Rows(t))
            if (index < row.Cells.Count) row.Cells.RemoveAt(index);
        if (index < t.Columns.Count) t.Columns.RemoveAt(index);
        return true;
    }

    public static void SetAlignment(Table t, int column, TextAlignment align)
    {
        foreach (var row in Rows(t))
            if (column < row.Cells.Count) CellParagraph(row.Cells[column]).TextAlignment = align;
    }

    // ---------- Typing shortcut ----------

    private static readonly Regex HeaderLine = new(@"^\s*\|(.*)\|\s*$", RegexOptions.Compiled);

    /// <summary>
    /// The header cells of a typed table line such as "| Name | Value |", or null if the text isn't one.
    /// Escaped pipes (\|) stay in the cell text.
    /// </summary>
    public static List<string>? ParseHeaderLine(string text)
    {
        var m = HeaderLine.Match(text);
        if (!m.Success) return null;
        var cells = Regex.Split(m.Groups[1].Value, @"(?<!\\)\|").Select(c => c.Trim().Replace("\\|", "|")).ToList();
        if (cells.Count == 0 || cells.All(c => Regex.IsMatch(c, @"^:?-*:?$"))) return null; // a delimiter row, or nothing
        return cells;
    }
}
