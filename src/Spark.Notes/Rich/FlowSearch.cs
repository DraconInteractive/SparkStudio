using System.Text;
using System.Windows.Documents;
using WpfBlock = System.Windows.Documents.Block;

namespace Spark.Notes.Rich;

/// <summary>Finds text in a rendered note (FlowDocument), returning ranges that can be selected.</summary>
public static class FlowSearch
{
    /// <summary>All matches in the document, in reading order. Matches never span paragraphs.</summary>
    public static List<TextRange> FindAll(FlowDocument doc, string query, SearchOptions options) =>
        FindIn(Paragraphs(doc.Blocks), query, options);

    public static List<TextRange> FindIn(IEnumerable<Paragraph> paragraphs, string query, SearchOptions options)
    {
        var result = new List<TextRange>();
        if (string.IsNullOrEmpty(query)) return result;
        foreach (var p in paragraphs)
        {
            var (text, segments) = TextOf(p);
            foreach (var at in NoteSearch.FindAll(text, query, options))
            {
                var start = PointerAt(segments, at, isEnd: false);
                var end = PointerAt(segments, at + query.Length, isEnd: true);
                if (start != null && end != null) result.Add(new TextRange(start, end));
            }
        }
        return result;
    }

    /// <summary>Paragraphs in document order, including those inside lists, quotes and tables.</summary>
    public static IEnumerable<Paragraph> Paragraphs(IEnumerable<WpfBlock> blocks)
    {
        foreach (var b in blocks)
        {
            switch (b)
            {
                case Paragraph p:
                    yield return p;
                    break;
                case Section s:
                    foreach (var x in Paragraphs(s.Blocks)) yield return x;
                    break;
                case List l:
                    foreach (var item in l.ListItems)
                        foreach (var x in Paragraphs(item.Blocks)) yield return x;
                    break;
                case Table t:
                    foreach (var group in t.RowGroups)
                        foreach (var row in group.Rows)
                            foreach (var cell in row.Cells)
                                foreach (var x in Paragraphs(cell.Blocks)) yield return x;
                    break;
            }
        }
    }

    /// <summary>
    /// The block that Markdown line <paramref name="line0"/> (0-based) was converted into: the last block, in
    /// document order and at any depth, whose source line is at or before it. Null if no block has line info.
    /// </summary>
    public static WpfBlock? BlockForSourceLine(FlowDocument doc, int line0)
    {
        WpfBlock? best = null;
        foreach (var b in AllBlocks(doc.Blocks))
        {
            var line = NoteDocument.GetSourceLine(b);
            if (line < 0) continue;
            if (line > line0) break;
            best = b;
        }
        return best;
    }

    public static IEnumerable<Paragraph> ParagraphsOf(WpfBlock block) =>
        block is Paragraph p ? new[] { p } : Paragraphs(new[] { block });

    private static IEnumerable<WpfBlock> AllBlocks(IEnumerable<WpfBlock> blocks)
    {
        foreach (var b in blocks)
        {
            yield return b;
            IEnumerable<WpfBlock> children = b switch
            {
                Section s => s.Blocks,
                List l => l.ListItems.SelectMany(i => i.Blocks),
                Table t => t.RowGroups.SelectMany(g => g.Rows).SelectMany(r => r.Cells).SelectMany(c => c.Blocks),
                _ => Array.Empty<WpfBlock>(),
            };
            foreach (var c in AllBlocks(children)) yield return c;
        }
    }

    private readonly record struct Segment(int Offset, int Length, TextPointer Start);

    /// <summary>The paragraph's visible text, with a map from text offsets back to document positions.</summary>
    private static (string Text, List<Segment> Segments) TextOf(Paragraph p)
    {
        var sb = new StringBuilder();
        var segments = new List<Segment>();
        for (var tp = p.ContentStart; tp != null && tp.CompareTo(p.ContentEnd) < 0; tp = tp.GetNextContextPosition(LogicalDirection.Forward))
        {
            switch (tp.GetPointerContext(LogicalDirection.Forward))
            {
                case TextPointerContext.Text:
                    var run = tp.GetTextInRun(LogicalDirection.Forward);
                    segments.Add(new Segment(sb.Length, run.Length, tp));
                    sb.Append(run);
                    break;
                case TextPointerContext.ElementStart when tp.GetAdjacentElement(LogicalDirection.Forward) is LineBreak:
                    sb.Append('\n'); // code blocks: keeps matches from joining across lines
                    break;
                case TextPointerContext.EmbeddedElement:
                    sb.Append('￼'); // e.g. a task checkbox
                    break;
            }
        }
        return (sb.ToString(), segments);
    }

    private static TextPointer? PointerAt(List<Segment> segments, int offset, bool isEnd)
    {
        foreach (var s in segments)
        {
            bool inside = isEnd ? offset > s.Offset && offset <= s.Offset + s.Length
                                : offset >= s.Offset && offset < s.Offset + s.Length;
            if (inside) return s.Start.GetPositionAtOffset(offset - s.Offset, LogicalDirection.Forward);
        }
        return null;
    }
}
