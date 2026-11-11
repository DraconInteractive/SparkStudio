using System.Windows.Documents;
using Spark.Api;
using Spark.Notes.Rich;

namespace Spark.Notes.Tests;

public class SearchTests
{
    private static T Sta<T>(Func<T> f)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { result = f(); } catch (Exception e) { error = e; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new Exception("STA failure", error);
        return result;
    }

    private static readonly SparkFile Note = new() { Id = "n", Name = "Trip.md", Directory = "/p" };

    [Fact]
    public void Finds_case_insensitively_by_default_and_honours_options()
    {
        const string text = "Tent, tent and TENT. Contents: attention";
        Assert.Equal(new[] { 0, 6, 15, 24, 33 }, NoteSearch.FindAll(text, "tent", new SearchOptions())); // incl. "Con-tent-s", "at-tent-ion"
        Assert.Equal(new[] { 6, 24, 33 }, NoteSearch.FindAll(text, "tent", new SearchOptions(MatchCase: true)));
        Assert.Equal(new[] { 0, 6, 15 }, NoteSearch.FindAll(text, "tent", new SearchOptions(WholeWord: true)));
        Assert.Empty(NoteSearch.FindAll(text, "", new SearchOptions()));
    }

    [Fact]
    public void Matches_do_not_overlap()
    {
        Assert.Equal(new[] { 0, 2 }, NoteSearch.FindAll("aaaa", "aa", new SearchOptions()));
    }

    [Fact]
    public void Global_search_reports_one_hit_per_line_with_1_based_line_numbers()
    {
        var hits = NoteSearch.Search(Note, "# Trip\n\nPack the tent\r\nno match\ntent, tent", "tent", new SearchOptions());
        Assert.Equal(new[] { 3, 5 }, hits.Select(h => h.Line));
        Assert.Equal(9, hits[0].Column);
        Assert.Equal("Pack the tent", hits[0].LineText);
        Assert.Equal(2, hits[1].MatchesOnLine);
    }

    [Fact]
    public void Occurrence_index_counts_matches_before_the_hit()
    {
        const string src = "tent\nnone\ntent tent\ntent";
        // Third line, second match (column 5): two before it counting from line 0, one counting from line 2.
        Assert.Equal(2, NoteSearch.OccurrenceIndex(src, 0, 2, 5, "tent", new SearchOptions()));
        Assert.Equal(1, NoteSearch.OccurrenceIndex(src, 2, 2, 5, "tent", new SearchOptions()));
    }

    [Fact]
    public void Rendered_note_search_spans_formatting_and_finds_text_in_lists_and_tables() => Sta(() =>
    {
        var doc = new MarkdownToFlow().Convert(
            "# Packing\n\nBring the **te**nt.\n\n- [ ] tent pegs\n- stove\n\n| Item | Note |\n| --- | --- |\n| tent | big |\n");
        var ranges = FlowSearch.FindAll(doc, "tent", new SearchOptions());
        Assert.Equal(3, ranges.Count);
        Assert.All(ranges, r => Assert.Equal("tent", r.Text, ignoreCase: true));
        return 0;
    });

    [Fact]
    public void Blocks_remember_their_markdown_line() => Sta(() =>
    {
        const string md = "# Title\n\nFirst paragraph.\n\n- one\n- two tent\n\n```\ncode\ntent here\n```\n";
        var doc = new MarkdownToFlow().Convert(md);
        var block = FlowSearch.BlockForSourceLine(doc, 5)!; // "- two tent"
        Assert.Contains("two tent", new TextRange(block.ContentStart, block.ContentEnd).Text);

        var code = FlowSearch.BlockForSourceLine(doc, 9)!; // second line inside the fence
        Assert.Equal(7, NoteDocument.GetSourceLine(code));
        var inCode = FlowSearch.FindIn(FlowSearch.ParagraphsOf(code), "tent", new SearchOptions());
        Assert.Single(inCode);
        return 0;
    });
}
