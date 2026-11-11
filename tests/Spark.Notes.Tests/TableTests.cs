using System.Windows;
using System.Windows.Documents;
using Spark.Notes.Rich;
using static Spark.Notes.Tests.RoundTripTests;

namespace Spark.Notes.Tests;

public class TableTests
{
    private static string Md(Table t)
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(t);
        return FlowToMarkdown.Convert(doc, null);
    }

    private static void Type(Table t, int row, int col, string text) =>
        NoteTables.CellParagraph(NoteTables.CellAt(t, row, col)!).Inlines.Add(new Run(text));

    private static Table Load(string md)
    {
        var doc = new MarkdownToFlow().Convert(md);
        return doc.Blocks.OfType<Table>().Single();
    }

    /// <summary>Markdown -> table -> edit -> Markdown, checking the result also parses back to the same text.</summary>
    private static string EditAndSave(string md, Action<Table> edit) => Sta(() =>
    {
        var t = Load(md);
        edit(t);
        var once = Md(t);
        Assert.Equal(once, Md(Load(once)));
        return once;
    });

    [Fact]
    public void New_table_with_empty_cells_is_a_valid_markdown_table()
    {
        var md = Sta(() => Md(NoteTables.Create(3, 2)));
        Assert.Equal("|  |  |  |\n| --- | --- | --- |\n|  |  |  |\n|  |  |  |\n", md);
        var reloaded = Sta(() => { var t = Load(md); return (NoteTables.Rows(t).Count, NoteTables.ColumnCount(t)); });
        Assert.Equal((3, 3), reloaded);
    }

    [Fact]
    public void Typed_cells_serialise_with_plain_header_text()
    {
        var md = Sta(() =>
        {
            var t = NoteTables.Create(2, 1, new[] { "Name", "Value" });
            Type(t, 1, 0, "a");
            Type(t, 1, 1, "1");
            return Md(t);
        });
        Assert.Equal("| Name | Value |\n| --- | --- |\n| a | 1 |\n", md);
    }

    private const string Sample = "| A | B |\n| --- | ---: |\n| a1 | b1 |\n| a2 | b2 |\n";

    [Fact]
    public void Insert_row_keeps_column_alignment()
    {
        var md = EditAndSave(Sample, t =>
        {
            var row = NoteTables.InsertRow(t, 2);
            NoteTables.CellParagraph(row.Cells[1]).Inlines.Add(new Run("new"));
            Assert.Equal(TextAlignment.Right, NoteTables.CellParagraph(row.Cells[1]).TextAlignment);
        });
        Assert.Equal("| A | B |\n| --- | ---: |\n| a1 | b1 |\n|  | new |\n| a2 | b2 |\n", md);
    }

    [Fact]
    public void Insert_row_never_goes_above_the_header()
    {
        var md = EditAndSave(Sample, t => NoteTables.InsertRow(t, 0));
        Assert.StartsWith("| A | B |\n| --- | ---: |\n|  |  |\n| a1 | b1 |", md);
    }

    [Fact]
    public void Insert_and_delete_columns()
    {
        var md = EditAndSave(Sample, t =>
        {
            NoteTables.InsertColumn(t, 1);
            Type(t, 0, 1, "Mid");
        });
        Assert.Equal("| A | Mid | B |\n| --- | --- | ---: |\n| a1 |  | b1 |\n| a2 |  | b2 |\n", md);

        md = EditAndSave(Sample, t => Assert.True(NoteTables.DeleteColumn(t, 0)));
        Assert.Equal("| B |\n| ---: |\n| b1 |\n| b2 |\n", md);

        Sta(() =>
        {
            var t = Load("| Only |\n| --- |\n| x |\n");
            Assert.False(NoteTables.DeleteColumn(t, 0));
            return 0;
        });
    }

    [Fact]
    public void Delete_row_protects_the_header()
    {
        var md = EditAndSave(Sample, t =>
        {
            Assert.False(NoteTables.DeleteRow(t, 0));
            Assert.True(NoteTables.DeleteRow(t, 1));
        });
        Assert.Equal("| A | B |\n| --- | ---: |\n| a2 | b2 |\n", md);
    }

    [Fact]
    public void Header_only_table_round_trips()
    {
        var md = EditAndSave(Sample, t =>
        {
            NoteTables.DeleteRow(t, 2);
            NoteTables.DeleteRow(t, 1);
        });
        Assert.Equal("| A | B |\n| --- | ---: |\n", md);
    }

    [Fact]
    public void Alignment_applies_to_the_whole_column()
    {
        var md = EditAndSave(Sample, t =>
        {
            NoteTables.SetAlignment(t, 0, TextAlignment.Center);
            NoteTables.SetAlignment(t, 1, TextAlignment.Left);
        });
        Assert.Equal("| A | B |\n| :---: | --- |\n| a1 | b1 |\n| a2 | b2 |\n", md);
    }

    [Fact]
    public void Cell_text_with_pipes_and_links_survives()
    {
        var md = EditAndSave(Sample, t =>
        {
            Type(t, 1, 0, " x|y");
        });
        Assert.Contains("| a1 x\\|y |", md);
    }

    [Theory]
    [InlineData("| Name | Value |", new[] { "Name", "Value" })]
    [InlineData("|a|b|c|", new[] { "a", "b", "c" })]
    [InlineData("| Single |", new[] { "Single" })]
    [InlineData("| a \\| b | c |", new[] { "a | b", "c" })]
    [InlineData("| x |  |", new[] { "x", "" })]
    public void Typed_header_lines_are_recognised(string line, string[] cells)
    {
        Assert.Equal(cells, NoteTables.ParseHeaderLine(line));
    }

    [Theory]
    [InlineData("no pipes")]
    [InlineData("| --- | --- |")]
    [InlineData("|  |")]
    [InlineData("a | b")]
    [InlineData("| unterminated")]
    public void Other_lines_are_not_table_headers(string line)
    {
        Assert.Null(NoteTables.ParseHeaderLine(line));
    }
}
