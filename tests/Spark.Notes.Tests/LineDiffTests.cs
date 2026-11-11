namespace Spark.Notes.Tests;

public class LineDiffTests
{
    private static string Kinds(IEnumerable<DiffRow> rows) =>
        string.Concat(rows.Select(r => r.Kind switch { DiffKind.Same => '=', DiffKind.Changed => '~', DiffKind.Removed => '-', _ => '+' }));

    [Fact]
    public void Identical_texts_are_all_same()
    {
        var rows = LineDiff.Compute("a\nb\nc", "a\nb\nc");
        Assert.Equal("===", Kinds(rows));
    }

    [Fact]
    public void Edited_line_pairs_up_side_by_side()
    {
        var rows = LineDiff.Compute("# Title\nold line\nend", "# Title\nnew line\nend");
        Assert.Equal("=~=", Kinds(rows));
        Assert.Equal(("old line", "new line"), (rows[1].Left, rows[1].Right));
        Assert.Equal((2, 2), (rows[1].LeftNumber, rows[1].RightNumber));
    }

    [Fact]
    public void Insertions_and_deletions_keep_line_numbers_aligned()
    {
        var rows = LineDiff.Compute("a\nb\nc\nd", "a\nc\nd\ne");
        Assert.Equal("=-==+", Kinds(rows));
        Assert.Equal((2, null), (rows[1].LeftNumber, rows[1].RightNumber));
        Assert.Equal((null, 4), (rows[4].LeftNumber, rows[4].RightNumber));
        Assert.Equal("e", rows[4].Right);
    }

    [Fact]
    public void Line_endings_do_not_count_as_differences()
    {
        Assert.Equal("==", Kinds(LineDiff.Compute("a\r\nb", "a\nb")));
    }

    [Fact]
    public void Both_sides_reconstruct_their_text()
    {
        const string mine = "one\ntwo\nthree\nfour\nfive";
        const string theirs = "zero\none\n2\nthree\nfive\nsix";
        var rows = LineDiff.Compute(mine, theirs);
        Assert.Equal(mine, string.Join("\n", rows.Where(r => r.Left != null).Select(r => r.Left)));
        Assert.Equal(theirs, string.Join("\n", rows.Where(r => r.Right != null).Select(r => r.Right)));
    }
}
