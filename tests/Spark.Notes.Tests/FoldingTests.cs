using System.Windows.Controls;
using System.Windows.Documents;
using Spark.Notes.Rich;
using static Spark.Notes.Tests.RoundTripTests;

namespace Spark.Notes.Tests;

public class FoldingTests
{
    private static FlowDocument Load(string md) => new MarkdownToFlow().Convert(md);
    private static string Save(FlowDocument doc) => FlowToMarkdown.Convert(doc, null);
    private static FlowDocument Convert(string md) => new MarkdownToFlow().Convert(md);

    private static Paragraph Heading(FlowDocument doc, string text) =>
        FlowSearch.Paragraphs(doc.Blocks).First(p => NoteDocument.InfoOf(p).Kind == BlockKind.Heading &&
                                              new TextRange(p.ContentStart, p.ContentEnd).Text.Trim() == text);

    private static List<Section> Callouts(FlowDocument doc)
    {
        var list = new List<Section>();
        void Walk(IEnumerable<Block> blocks)
        {
            foreach (var b in blocks)
                if (b is Section s)
                {
                    if (NoteCallouts.InfoOf(s) != null) list.Add(s);
                    Walk(s.Blocks);
                }
        }
        Walk(doc.Blocks);
        return list;
    }

    // ---------- Callouts ----------

    [Theory]
    [InlineData("> [!note] Title\n>\n> Body text.\n")]
    [InlineData("> [!warning]- Collapsed by default\n>\n> Hidden at first.\n")]
    [InlineData("> [!tip]+ Expanded\n>\n> - a list\n> - inside\n")]
    [InlineData("> [!question]\n>\n> No title.\n")]
    [InlineData("> [!note]- Outer\n>\n> Outer body.\n>\n> > [!tip]- Inner\n> >\n> > Inner body.\n> >\n> > > [!warning]+ Deepest\n> > >\n> > > Run these first.\n")]
    [InlineData("> Plain quote stays a quote.\n")]
    [InlineData("> [!info] Title with **bold** and [[Link]]\n>\n> Body.\n")]
    public void Callouts_round_trip(string md)
    {
        Assert.Equal(md, Sta(() => Save(Load(md))));
    }

    [Fact]
    public void Title_and_body_on_consecutive_lines_are_split()
    {
        var md = Sta(() => Save(Load("> [!note] Title\n> body line\n")));
        Assert.Equal("> [!note] Title\n>\n> body line\n", md);
    }

    [Fact]
    public void Callout_types_aliases_and_fold_markers_parse()
    {
        Assert.Equal(new CalloutInfo("warning", '-', 11), NoteCallouts.Parse("[!warning]- Title"));
        Assert.Equal(new CalloutInfo("NOTE", null, 7), NoteCallouts.Parse("[!NOTE]"));
        Assert.Equal('+', NoteCallouts.Parse("[!tip]+ x")!.Fold);
        Assert.Null(NoteCallouts.Parse("[!note]x"));
        Assert.Null(NoteCallouts.Parse("[note] x"));
        Assert.Null(NoteCallouts.Parse("text [!note]"));
        Assert.Equal("tip", NoteCallouts.Canonical("hint"));
        Assert.Equal("danger", NoteCallouts.Canonical("ERROR"));
        Assert.Equal("note", NoteCallouts.Canonical("custom-type"));
    }

    [Fact]
    public void Nested_callouts_are_detected_at_each_level()
    {
        var md = "> [!note] A\n>\n> > [!tip] B\n> >\n> > > [!bug] C\n> > >\n> > > text\n";
        var types = Sta(() => Callouts(Load(md)).Select(s => NoteCallouts.InfoOf(s)!.Type).ToList());
        Assert.Equal(new[] { "note", "tip", "bug" }, types);
    }

    [Fact]
    public void Callouts_are_styled_and_revert_to_quotes_when_the_marker_goes()
    {
        Sta(() =>
        {
            var doc = Load("> [!danger] Careful\n>\n> body\n");
            var s = Callouts(doc).Single();
            Assert.NotNull(s.Background);
            var title = (Paragraph)s.Blocks.FirstBlock;
            Assert.Equal(NoteCallouts.EdgeBrush("danger"), title.Foreground);

            ((Run)title.Inlines.FirstInline).Text = "Careful";
            Assert.True(NoteCallouts.ApplyStyle(s));
            Assert.Null(s.Background);
            Assert.Equal(System.Windows.DependencyProperty.UnsetValue, title.ReadLocalValue(TextElement.ForegroundProperty));
            Assert.False(NoteCallouts.ApplyStyle(s)); // nothing left to change
            Assert.Equal("> Careful\n>\n> body\n", Save(doc));
            return 0;
        });
    }

    [Fact]
    public void Set_marker_changes_type_and_fold_default()
    {
        var md = Sta(() =>
        {
            var doc = Load("> [!note] Title\n>\n> body\n");
            Assert.True(NoteCallouts.SetMarker(Callouts(doc).Single(), "warning", '-'));
            return Save(doc);
        });
        Assert.Equal("> [!warning]- Title\n>\n> body\n", md);
    }

    [Fact]
    public void Created_callout_serialises_with_its_marker()
    {
        var md = Sta(() =>
        {
            var doc = new FlowDocument();
            var s = NoteCallouts.Create("example", "Demo");
            ((Paragraph)s.Blocks.LastBlock).Inlines.Add(new Run("content"));
            doc.Blocks.Add(s);
            return Save(doc);
        });
        Assert.Equal("> [!example] Demo\n>\n> content\n", md);
    }

    [Fact]
    public void Escaped_marker_text_in_a_normal_paragraph_stays_escaped()
    {
        Assert.Equal("\\[!note\\] not a callout\n", Sta(() => Save(Load("\\[!note\\] not a callout"))));
    }

    // ---------- Folding ----------

    private const string Doc =
        "# Top\n\nIntro.\n\n## Section A\n\nA text.\n\n- a list\n\n### Sub A\n\nSub text.\n\n## Section B\n\nB text.\n\n# Next top\n\nEnd.\n";

    [Fact]
    public void Folding_a_heading_hides_its_range_but_saves_everything()
    {
        Sta(() =>
        {
            var doc = Load(Doc);
            var a = Heading(doc, "Section A");
            Assert.Equal(4, NoteFolding.HeadingRange(a).Count); // A text, list, Sub A, Sub text
            var stash = NoteFolding.FoldHeading(a);
            Assert.NotNull(stash);
            Assert.Same(stash, NoteFolding.HeadingStash(a));
            Assert.False(NoteFolding.CanFoldHeading(a));
            Assert.Equal(Doc, Save(doc));
            Assert.DoesNotContain(FlowSearch.Paragraphs(doc.Blocks), p => new TextRange(p.ContentStart, p.ContentEnd).Text.Contains("Sub text"));

            // Section B is a sibling, not part of A's range.
            Assert.Contains(FlowSearch.Paragraphs(doc.Blocks), p => new TextRange(p.ContentStart, p.ContentEnd).Text.Contains("B text"));

            NoteFolding.Unfold(stash!, Convert);
            Assert.Null(NoteFolding.HeadingStash(a));
            Assert.Equal(Doc, Save(doc));
            return 0;
        });
    }

    [Fact]
    public void Top_level_heading_folds_its_subsections_and_nested_folds_survive()
    {
        Sta(() =>
        {
            var doc = Load(Doc);
            NoteFolding.FoldHeading(Heading(doc, "Sub A"));
            var top = NoteFolding.FoldHeading(Heading(doc, "Top"))!;
            Assert.Equal(Doc, Save(doc));
            Assert.Equal(new[] { "Top", "Next top", "End." },
                FlowSearch.Paragraphs(doc.Blocks).Select(p => new TextRange(p.ContentStart, p.ContentEnd).Text.Trim()).ToArray());
            NoteFolding.Unfold(top, Convert);
            Assert.Equal(Doc, Save(doc));
            return 0;
        });
    }

    [Fact]
    public void Heading_without_content_cannot_fold()
    {
        Sta(() =>
        {
            var doc = Load("## Empty\n\n## Next\n\ntext\n");
            Assert.False(NoteFolding.CanFoldHeading(Heading(doc, "Empty")));
            Assert.Null(NoteFolding.FoldHeading(Heading(doc, "Empty")));
            Assert.True(NoteFolding.CanFoldHeading(Heading(doc, "Next")));
            return 0;
        });
    }

    private const string Nested =
        "> [!note]- Outer\n>\n> Outer body.\n>\n> > [!tip]- Inner\n> >\n> > Inner body.\n\nAfter.\n";

    [Fact]
    public void Default_folds_collapse_callouts_marked_minus_innermost_first()
    {
        Sta(() =>
        {
            var doc = Load(Nested);
            Assert.Equal(2, NoteFolding.ApplyDefaultFolds(doc.Blocks));
            var outer = Callouts(doc).Single(); // the inner one is hidden inside the outer stash
            Assert.NotNull(NoteFolding.CalloutStash(outer));
            Assert.Equal(Nested, Save(doc));

            // Unfolding the outer one shows its body, with the inner callout collapsed again (its default).
            var outerStash = NoteFolding.CalloutStash(outer)!;
            NoteFolding.Unfold(outerStash, Convert);
            var all = Callouts(doc);
            Assert.Equal(2, all.Count);
            Assert.Null(NoteFolding.CalloutStash(all[0]));
            Assert.NotNull(NoteFolding.CalloutStash(all[1]));
            Assert.Equal(Nested, Save(doc));
            return 0;
        });
    }

    [Fact]
    public void Callout_folding_keeps_the_title_and_round_trips()
    {
        Sta(() =>
        {
            const string md = "> [!info]+ Shown\n>\n> One.\n>\n> Two.\n";
            var doc = Load(md);
            Assert.Equal(0, NoteFolding.ApplyDefaultFolds(doc.Blocks)); // "+" stays open
            var s = Callouts(doc).Single();
            Assert.True(NoteFolding.CanFoldCallout(s));
            var stash = NoteFolding.FoldCallout(s)!;
            Assert.Equal(2, s.Blocks.Count);
            Assert.Equal(md, Save(doc));
            NoteFolding.Unfold(stash, Convert);
            Assert.Equal(3, s.Blocks.Count);
            Assert.Equal(md, Save(doc));
            return 0;
        });
    }

    [Fact]
    public void Folded_content_inside_lists_and_quotes_saves_in_place()
    {
        Sta(() =>
        {
            const string md = "> ## Quoted heading\n>\n> quoted text\n\n- item\n\n  > [!note]- In a list\n  >\n  > list callout body\n";
            var doc = Load(md);
            NoteFolding.FoldHeading(Heading(doc, "Quoted heading"));
            NoteFolding.ApplyDefaultFolds(doc.Blocks);
            Assert.Equal(2, NoteFolding.AllStashes(doc).Count);
            Assert.Equal(md, Save(doc));
            return 0;
        });
    }

    [Fact]
    public void Stash_markdown_survives_special_characters()
    {
        Sta(() =>
        {
            const string md = "## H\n\nLine with \"quotes\", <angle> & ampersand, ünïcödé and `code`.\n\n```\nfenced\n\n  indented\n```\n";
            var doc = Load(md);
            var stash = NoteFolding.FoldHeading(Heading(doc, "H"))!;
            Assert.Equal(md[(md.IndexOf('\n') + 2)..].TrimEnd('\n'), NoteFolding.MarkdownOf(stash));
            Assert.Equal(md, Save(doc));
            return 0;
        });
    }
}
