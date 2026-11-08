using Markdig;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Parsers.Inlines;
using Markdig.Renderers;
using Markdig.Syntax.Inlines;

namespace Spark.Notes.Markdown;

public sealed class WikiLinkInline : LeafInline
{
    public WikiLinkInline(WikiLink link) => Link = link;
    public WikiLink Link { get; }
}

/// <summary>Parses [[Target#Heading|Alias]] and ![[Embed]] before standard links get a chance.</summary>
public sealed class WikiLinkParser : InlineParser
{
    public WikiLinkParser() => OpeningCharacters = new[] { '[', '!' };

    public override bool Match(InlineProcessor processor, ref StringSlice slice)
    {
        int start = slice.Start;
        bool embed = slice.CurrentChar == '!';
        int open = embed ? start + 1 : start;
        var text = slice.Text;
        if (open + 1 > slice.End || text[open] != '[' || text[open + 1] != '[') return false;

        int close = text.IndexOf("]]", open + 2, slice.End - (open + 2) + 1, StringComparison.Ordinal);
        if (close < 0) return false;
        var inner = text.Substring(open + 2, close - open - 2);
        if (inner.Contains('\n') || inner.Contains('\r')) return false;
        var link = WikiLink.ParseInner(inner, embed);
        if (link == null) return false;

        processor.Inline = new WikiLinkInline(link)
        {
            Span = new Markdig.Syntax.SourceSpan(processor.GetSourcePosition(start, out int line, out int col), close + 1),
            Line = line,
            Column = col,
        };
        slice.Start = close + 2;
        return true;
    }
}

public sealed class WikiLinkExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        if (!pipeline.InlineParsers.Contains<WikiLinkParser>())
            pipeline.InlineParsers.InsertBefore<LinkInlineParser>(new WikiLinkParser());
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer) { }
}

public static class NoteMarkdown
{
    public static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseYamlFrontMatter()
        .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough)
        .UseTaskLists()
        .UsePipeTables()
        .UsePreciseSourceLocation()
        .Use<WikiLinkExtension>()
        .Build();
}
