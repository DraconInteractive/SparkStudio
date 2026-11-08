using System.Windows;
using System.Windows.Documents;
using Spark.Notes.Rich;

namespace Spark.Notes.Tests;

public class RoundTripTests
{
    internal static T Sta<T>(Func<T> f)
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

    private static string RoundTrip(string md) => Sta(() =>
    {
        var conv = new MarkdownToFlow();
        var doc = conv.Convert(md);
        return FlowToMarkdown.Convert(doc, conv.FrontMatter);
    });

    [Theory]
    [InlineData("# Title\n")]
    [InlineData("### Third level\n")]
    [InlineData("Plain paragraph.\n")]
    [InlineData("Some **bold**, *italic*, ~~struck~~ and `code`.\n")]
    [InlineData("Line one\nLine two\n")]
    [InlineData("See [[Timer view]] and [[Specs/Watch#Battery|battery notes]].\n")]
    [InlineData("A [[Note#Heading]] link.\n")]
    [InlineData("Embed: ![[diagram.png]]\n")]
    [InlineData("An [external](https://example.com) link and <https://spark.local/x>.\n")]
    [InlineData("- one\n- two\n  - nested\n- three\n")]
    [InlineData("1. first\n2. second\n")]
    [InlineData("- [x] done\n- [ ] todo\n")]
    [InlineData("> quoted\n> lines\n")]
    [InlineData("```csharp\nvar x = 1;\n\nreturn x;\n```\n")]
    [InlineData("Above\n\n---\n\nBelow\n")]
    [InlineData("| Name | Value |\n| --- | ---: |\n| a | 1 |\n| b | 2 |\n")]
    [InlineData("---\ntags: [spark]\n---\n\n# Body\n")]
    [InlineData("<div align=\"center\">raw html</div>\n")]
    [InlineData("snake_case_name stays readable\n")]
    [InlineData("Escaped \\*not bold\\* and \\[\\[not a link\\]\\]\n")]
    public void Canonical_markdown_round_trips_unchanged(string md)
    {
        Assert.Equal(md, RoundTrip(md));
    }

    [Theory]
    [InlineData("**bold**_italic_ next")]
    [InlineData("***both*** then plain")]
    [InlineData("**bold *and italic* inside**")]
    [InlineData("word*italic*word")]
    [InlineData("1. looks like a list\n# looks like heading\n- looks like bullet")]
    [InlineData("**[[Linked]]** inside bold")]
    [InlineData("- item\n\n  second paragraph\n- next")]
    [InlineData("1. step\n   ```\n   code\n   ```\n2. after")]
    public void Serialisation_is_stable(string md)
    {
        var once = RoundTrip(md);
        var twice = RoundTrip(once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Text_typed_in_editor_that_looks_like_markdown_is_escaped()
    {
        var text = Sta(() =>
        {
            var doc = NoteDocument.NewDocument();
            var p = new Paragraph();
            p.Inlines.Add(new Run("1. not a list *nor emphasis* [[nor link]] <tag> # hash"));
            doc.Blocks.Add(p);
            var md = FlowToMarkdown.Convert(doc);
            var back = new MarkdownToFlow().Convert(md);
            return new TextRange(back.ContentStart, back.ContentEnd).Text.TrimEnd();
        });
        Assert.Equal("1. not a list *nor emphasis* [[nor link]] <tag> # hash", text);
    }

    [Fact]
    public void Formatting_built_in_editor_serialises()
    {
        var md = Sta(() =>
        {
            var doc = NoteDocument.NewDocument();
            var p = new Paragraph();
            p.Inlines.Add(new Run("Hello "));
            p.Inlines.Add(new Run("bold ") { FontWeight = FontWeights.Bold });
            p.Inlines.Add(new Run("world") { FontWeight = FontWeights.Bold, FontStyle = FontStyles.Italic });
            p.Inlines.Add(new Run(" end"));
            doc.Blocks.Add(p);
            return FlowToMarkdown.Convert(doc);
        });
        Assert.Equal("Hello **bold *world*** end\n", md);
        Assert.Equal(md, RoundTrip(md));
    }

    [Fact]
    public void Heading_text_is_not_marked_bold()
    {
        Assert.Equal("## Plain heading\n", RoundTrip("## Plain heading"));
    }

    [Fact]
    public void Wikilink_alias_follows_edited_text()
    {
        var md = Sta(() =>
        {
            var doc = new MarkdownToFlow().Convert("See [[Timer view]].");
            var link = doc.Blocks.OfType<Paragraph>().First().Inlines.OfType<Hyperlink>().First();
            ((Run)link.Inlines.FirstInline).Text = "the timer";
            return FlowToMarkdown.Convert(doc);
        });
        Assert.Equal("See [[Timer view|the timer]].\n", md);
    }

    [Fact]
    public void Unresolved_links_are_reported_to_the_resolver()
    {
        var seen = Sta(() =>
        {
            var targets = new List<string>();
            new MarkdownToFlow(l => { targets.Add(l.Target); return false; }).Convert("[[A]] and [[B|b]]");
            return targets;
        });
        Assert.Equal(new[] { "A", "B" }, seen);
    }
}
