using System.Windows;
using System.Windows.Documents;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Spark.Notes.Markdown;
using MdBlock = Markdig.Syntax.Block;
using MdTable = Markdig.Extensions.Tables.Table;
using WpfBlock = System.Windows.Documents.Block;
using WpfTable = System.Windows.Documents.Table;
using MdRow = Markdig.Extensions.Tables.TableRow;
using MdCell = Markdig.Extensions.Tables.TableCell;
using MdInline = Markdig.Syntax.Inlines.Inline;
using WpfInline = System.Windows.Documents.Inline;

namespace Spark.Notes.Rich;

/// <summary>Converts note Markdown into an editable <see cref="FlowDocument"/>.</summary>
public sealed class MarkdownToFlow
{
    private readonly Func<WikiLink, bool> _isResolved;
    private string _source = "";

    public MarkdownToFlow(Func<WikiLink, bool>? isResolved = null) => _isResolved = isResolved ?? (_ => true);

    /// <summary>YAML front matter (including the --- fences) removed from the document, to be restored on save.</summary>
    public string? FrontMatter { get; private set; }

    public FlowDocument Convert(string markdown)
    {
        _source = markdown.Replace("\r\n", "\n");
        FrontMatter = null;
        var md = Markdig.Markdown.Parse(_source, NoteMarkdown.Pipeline);
        var doc = NoteDocument.NewDocument();
        AddBlocks(md, doc.Blocks);
        return doc;
    }

    private void AddBlocks(ContainerBlock container, BlockCollection target)
    {
        foreach (var b in container)
        {
            var wpf = ConvertBlock(b);
            if (wpf != null) target.Add(wpf);
        }
    }

    private WpfBlock? ConvertBlock(MdBlock b)
    {
        switch (b)
        {
            case YamlFrontMatterBlock:
                FrontMatter = Slice(b);
                return null;
            case HeadingBlock h:
            {
                var p = new Paragraph();
                NoteDocument.Apply(p, new BlockInfo(BlockKind.Heading, h.Level));
                AddInlines(h.Inline, p.Inlines);
                return p;
            }
            case ParagraphBlock pb:
            {
                var p = new Paragraph();
                NoteDocument.Apply(p, new BlockInfo(BlockKind.Paragraph));
                AddInlines(pb.Inline, p.Inlines);
                return p;
            }
            case FencedCodeBlock fc:
                return CodeParagraph(fc.Lines.ToString(), string.IsNullOrWhiteSpace(fc.Info) ? null : fc.Info);
            case CodeBlock cb when cb is not FencedCodeBlock && cb.GetType() == typeof(CodeBlock):
                return CodeParagraph(cb.Lines.ToString(), null);
            case QuoteBlock q:
            {
                var s = NoteDocument.NewQuote();
                AddBlocks(q, s.Blocks);
                return s;
            }
            case ThematicBreakBlock:
                return NoteDocument.NewRule();
            case ListBlock lb:
                return ConvertList(lb);
            case MdTable t:
                return ConvertTable(t);
            case LinkReferenceDefinitionGroup:
                return null; // reference links are inlined as [text](url) on save
            case BlankLineBlock:
                return null;
            default:
                return RawParagraph(Slice(b));
        }
    }

    private Paragraph CodeParagraph(string code, string? language)
    {
        var p = new Paragraph();
        NoteDocument.Apply(p, new BlockInfo(BlockKind.CodeBlock, Language: language?.Trim()));
        AddPlainLines(code.Replace("\r\n", "\n").TrimEnd('\n'), p.Inlines);
        return p;
    }

    private static Paragraph RawParagraph(string raw)
    {
        var p = new Paragraph();
        NoteDocument.Apply(p, new BlockInfo(BlockKind.Raw));
        AddPlainLines(raw.TrimEnd('\n'), p.Inlines);
        return p;
    }

    private static void AddPlainLines(string text, InlineCollection inlines)
    {
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) inlines.Add(new LineBreak());
            if (lines[i].Length > 0) inlines.Add(new Run(lines[i]));
        }
    }

    private List ConvertList(ListBlock lb)
    {
        int start = 1;
        if (lb.IsOrdered && int.TryParse(lb.OrderedStart, out var s)) start = s;
        var list = NoteDocument.NewList(lb.IsOrdered, start);
        bool allTasks = lb.Count > 0;
        foreach (var child in lb)
        {
            if (child is not ListItemBlock item) continue;
            var li = new ListItem();
            AddBlocks(item, li.Blocks);
            if (li.Blocks.Count == 0)
            {
                var empty = new Paragraph();
                NoteDocument.Apply(empty, new BlockInfo(BlockKind.Paragraph));
                li.Blocks.Add(empty);
            }
            foreach (var p in li.Blocks.OfType<Paragraph>()) p.Margin = new Thickness(0, 0, 0, 2);
            if (li.Blocks.FirstBlock is not Paragraph fp || NoteDocument.TaskState(fp) == null) allTasks = false;
            list.ListItems.Add(li);
        }
        if (allTasks) list.MarkerStyle = TextMarkerStyle.None;
        return list;
    }

    private WpfTable ConvertTable(MdTable t)
    {
        var table = new WpfTable
        {
            Tag = new BlockInfo(BlockKind.Table),
            CellSpacing = 0,
            BorderBrush = NoteDocument.RuleBrush,
            BorderThickness = new Thickness(1, 1, 0, 0),
            Margin = new Thickness(0, 0, 0, 10),
        };
        int cols = t.ColumnDefinitions.Count;
        foreach (var row in t.OfType<MdRow>()) cols = Math.Max(cols, row.Count);
        for (int i = 0; i < cols; i++) table.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        table.RowGroups.Add(group);

        foreach (var row in t.OfType<MdRow>())
        {
            var tr = new System.Windows.Documents.TableRow();
            for (int i = 0; i < cols; i++)
            {
                var p = new Paragraph { Margin = new Thickness(0) };
                NoteDocument.Apply(p, new BlockInfo(BlockKind.Paragraph));
                p.Margin = new Thickness(0);
                if (i < row.Count && row[i] is MdCell cell)
                    foreach (var pb in cell.OfType<ParagraphBlock>()) AddInlines(pb.Inline, p.Inlines);
                if (row.IsHeader) p.FontWeight = FontWeights.SemiBold;
                var align = i < t.ColumnDefinitions.Count ? t.ColumnDefinitions[i].Alignment : null;
                p.TextAlignment = align switch
                {
                    TableColumnAlign.Center => TextAlignment.Center,
                    TableColumnAlign.Right => TextAlignment.Right,
                    _ => TextAlignment.Left,
                };
                tr.Cells.Add(new System.Windows.Documents.TableCell(p)
                {
                    BorderBrush = NoteDocument.RuleBrush,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(8, 4, 8, 4),
                    Background = row.IsHeader ? NoteDocument.CodeBackground : null,
                });
            }
            group.Rows.Add(tr);
        }
        return table;
    }

    // ---------- Inlines ----------

    private readonly record struct Fmt(bool Bold, bool Italic, bool Strike);

    private void AddInlines(ContainerInline? container, InlineCollection target)
    {
        if (container == null) return;
        foreach (var inline in container) AddInline(inline, target, default);
    }

    private void AddInline(MdInline inline, InlineCollection target, Fmt fmt)
    {
        switch (inline)
        {
            case TaskList task:
                target.Add(NoteDocument.NewTaskBox(task.Checked));
                break;
            case WikiLinkInline wl:
                target.Add(NoteDocument.NewWikiLink(wl.Link, _isResolved(wl.Link)));
                break;
            case LiteralInline lit:
                target.Add(Styled(new Run(lit.Content.ToString()), fmt));
                break;
            case CodeInline code:
                target.Add(Styled(NoteDocument.CodeRun(code.Content), fmt));
                break;
            case LineBreakInline:
                target.Add(new LineBreak()); // soft and hard breaks both render as line breaks, like Obsidian
                break;
            case EmphasisInline em:
            {
                var inner = em.DelimiterChar == '~' ? fmt with { Strike = true }
                    : em.DelimiterCount >= 2 ? fmt with { Bold = true }
                    : fmt with { Italic = true };
                if (em.DelimiterCount == 3) inner = inner with { Bold = true, Italic = true };
                foreach (var child in em) AddInline(child, target, inner);
                break;
            }
            case LinkInline link when !link.IsImage:
            {
                var text = PlainText(link);
                var h = NoteDocument.NewExternalLink(text.Length > 0 ? text : link.Url ?? "", link.Url ?? "");
                ApplyFmt(h, fmt);
                target.Add(h);
                break;
            }
            case AutolinkInline auto:
            {
                var h = NoteDocument.NewExternalLink(auto.Url, auto.IsEmail ? "mailto:" + auto.Url : auto.Url);
                target.Add(h);
                break;
            }
            case HtmlEntityInline ent:
                target.Add(Styled(new Run(ent.Transcoded.ToString()), fmt));
                break;
            default:
            {
                // Images, inline HTML and anything else: keep the original source verbatim, shown as code.
                var raw = Slice(inline);
                if (raw.Length > 0) target.Add(new Run(raw) { FontFamily = NoteDocument.CodeFont, Foreground = NoteDocument.Muted, Tag = "raw" });
                break;
            }
        }
    }

    private static string PlainText(ContainerInline c)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var i in c.Descendants<MdInline>())
        {
            if (i is LiteralInline l) sb.Append(l.Content.ToString());
            else if (i is CodeInline ci) sb.Append(ci.Content);
        }
        return sb.ToString();
    }

    private static WpfInline Styled(WpfInline i, Fmt f)
    {
        ApplyFmt(i, f);
        return i;
    }

    private static void ApplyFmt(WpfInline i, Fmt f)
    {
        if (f.Bold) i.FontWeight = FontWeights.Bold;
        if (f.Italic) i.FontStyle = FontStyles.Italic;
        if (f.Strike) i.TextDecorations = TextDecorations.Strikethrough;
    }

    private string Slice(MarkdownObject o)
    {
        var span = o.Span;
        if (span.IsEmpty || span.Start < 0 || span.End >= _source.Length + 1) return "";
        int end = Math.Min(span.End, _source.Length - 1);
        return _source.Substring(span.Start, end - span.Start + 1);
    }
}
