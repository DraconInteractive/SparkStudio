using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Spark.Notes.Rich;

/// <summary>
/// Folding (collapsing) parts of a note: the content under a heading, or a callout's body.
/// <para>
/// A RichTextBox can't hide part of its document, so folded content is taken out and replaced by a small
/// placeholder block (a "stash") that holds the content as Markdown. The serializer writes the stash's Markdown in
/// its place, so saving, links and search see the full note whatever is folded; unfolding converts it back.
/// </para>
/// <para>
/// The Markdown lives (Base64-encoded) in the placeholder's Tag string: WPF's undo recreates embedded elements from
/// XAML, which keeps string Tags but would mangle newlines in attribute values. Because the stash is part of the
/// document, folding and unfolding are ordinary edits that undo/redo consistently.
/// </para>
/// </summary>
public static class NoteFolding
{
    private const string Prefix = "spark-fold:";

    // ---------- Stash blocks ----------

    public static bool IsStash(Block b) => StashTag(b) != null;

    private static string? StashTag(Block b) =>
        b is BlockUIContainer { Child: FrameworkElement { Tag: string t } } && t.StartsWith(Prefix, StringComparison.Ordinal) ? t : null;

    /// <summary>The Markdown a stash stands for, or null if the block isn't a stash.</summary>
    public static string? MarkdownOf(Block b)
    {
        var tag = StashTag(b);
        if (tag == null) return null;
        try { return Encoding.UTF8.GetString(System.Convert.FromBase64String(tag[Prefix.Length..])); }
        catch (FormatException) { return null; }
    }

    /// <summary>A placeholder standing in for <paramref name="markdown"/>: a small "⋯ N lines" pill.</summary>
    public static BlockUIContainer NewStash(string markdown)
    {
        int lines = markdown.Split('\n').Count(l => l.Trim().Trim('>').Trim().Length > 0);
        var pill = new Border
        {
            Tag = Prefix + System.Convert.ToBase64String(Encoding.UTF8.GetBytes(markdown)),
            Background = NoteDocument.CodeBackground,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 1, 8, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Folded. Click to show",
            Child = new TextBlock
            {
                Text = $"⋯  {lines} line{(lines == 1 ? "" : "s")} folded",
                FontSize = NoteDocument.BodySize - 2.5,
                Foreground = NoteDocument.Muted,
            },
        };
        return new BlockUIContainer(pill) { Margin = new Thickness(0, 0, 0, 10) };
    }

    /// <summary>The stash (placeholder) containing a visual element, if the element is part of one.</summary>
    public static BlockUIContainer? StashFromVisual(DependencyObject? o, FlowDocument doc)
    {
        // The placeholder is made of visuals (Border, TextBlock); clicks on text report document elements, which can't be one.
        for (; o is System.Windows.Media.Visual; o = System.Windows.Media.VisualTreeHelper.GetParent(o))
        {
            if (o is FrameworkElement { Tag: string t } fe && t.StartsWith(Prefix, StringComparison.Ordinal))
                return AllStashes(doc).FirstOrDefault(s => ReferenceEquals(s.Child, fe));
            if (o is RichTextBox) return null;
        }
        return null;
    }

    /// <summary>Every stash in the document, in order.</summary>
    public static List<BlockUIContainer> AllStashes(FlowDocument doc)
    {
        var list = new List<BlockUIContainer>();
        void Walk(IEnumerable<Block> blocks)
        {
            foreach (var b in blocks)
            {
                switch (b)
                {
                    case BlockUIContainer c when IsStash(c): list.Add(c); break;
                    case Section s: Walk(s.Blocks); break;
                    case List l: foreach (var li in l.ListItems) Walk(li.Blocks); break;
                }
            }
        }
        Walk(doc.Blocks);
        return list;
    }

    // ---------- Headings ----------

    /// <summary>Headings fold when they sit directly in the document or in a quote/callout (not in lists or tables).</summary>
    public static bool IsFoldableHeading(Paragraph p) =>
        NoteDocument.InfoOf(p).Kind == BlockKind.Heading && (p.Parent is FlowDocument || p.Parent is Section s && NoteCallouts.IsQuote(s));

    /// <summary>The blocks under a heading: its following siblings up to the next heading of the same or a higher level.</summary>
    public static List<Block> HeadingRange(Paragraph heading)
    {
        var level = NoteDocument.InfoOf(heading).Level;
        var range = new List<Block>();
        for (var b = heading.NextBlock; b != null; b = b.NextBlock)
        {
            if (b is Paragraph p && NoteDocument.InfoOf(p) is { Kind: BlockKind.Heading } info && info.Level <= level) break;
            range.Add(b);
        }
        return range;
    }

    /// <summary>The stash directly after a folded heading, or null if it isn't folded.</summary>
    public static BlockUIContainer? HeadingStash(Paragraph heading) =>
        heading.NextBlock is BlockUIContainer c && IsStash(c) ? c : null;

    public static bool CanFoldHeading(Paragraph heading) =>
        IsFoldableHeading(heading) && HeadingStash(heading) == null && HeadingRange(heading).Count > 0;

    /// <summary>Folds the content under a heading. Returns the stash, or null if there was nothing to fold.</summary>
    public static BlockUIContainer? FoldHeading(Paragraph heading)
    {
        if (!CanFoldHeading(heading)) return HeadingStash(heading);
        return Replace(HeadingRange(heading));
    }

    // ---------- Callouts ----------

    /// <summary>The stash holding a folded callout's body, or null if it isn't folded.</summary>
    public static BlockUIContainer? CalloutStash(Section callout) =>
        callout.Blocks.Count == 2 && callout.Blocks.LastBlock is BlockUIContainer c && IsStash(c) ? c : null;

    public static bool CanFoldCallout(Section callout) =>
        NoteCallouts.InfoOf(callout) != null && CalloutStash(callout) == null && callout.Blocks.Count > 1;

    public static BlockUIContainer? FoldCallout(Section callout)
    {
        if (!CanFoldCallout(callout)) return CalloutStash(callout);
        return Replace(callout.Blocks.Skip(1).ToList());
    }

    /// <summary>Folds every callout marked "-" (collapsed by default) among <paramref name="blocks"/>, innermost first.</summary>
    public static int ApplyDefaultFolds(IEnumerable<Block> blocks)
    {
        int n = 0;
        foreach (var b in blocks.ToList())
        {
            switch (b)
            {
                case Section s:
                    n += ApplyDefaultFolds(s.Blocks);
                    if (NoteCallouts.InfoOf(s) is { StartsCollapsed: true } && FoldCallout(s) != null) n++;
                    break;
                case List l:
                    foreach (var li in l.ListItems.ToList()) n += ApplyDefaultFolds(li.Blocks);
                    break;
            }
        }
        return n;
    }

    // ---------- Fold / unfold ----------

    private static BlockUIContainer? Replace(List<Block> range)
    {
        if (range.Count == 0) return null;
        var md = FlowToMarkdown.BlocksToMarkdown(range);
        var stash = NewStash(md);
        // SiblingBlocks returns a new wrapper each time, and a reused wrapper can silently fail to remove; fetch per call.
        range[0].SiblingBlocks!.InsertBefore(range[0], stash);
        foreach (var b in range) Detach(b);
        return stash;
    }

    /// <summary>
    /// Puts a stash's content back, converted with <paramref name="convert"/> (Markdown → document). Callouts inside
    /// it that are collapsed by default fold again. Returns the restored blocks.
    /// </summary>
    public static List<Block> Unfold(BlockUIContainer stash, Func<string, FlowDocument> convert)
    {
        var md = MarkdownOf(stash) ?? "";
        var restored = new List<Block>();
        if (md.Trim().Length > 0)
        {
            var doc = convert(md);
            restored = doc.Blocks.ToList();
            foreach (var b in restored)
            {
                doc.Blocks.Remove(b);
                NoteDocument.SetSourceLine(b, -1); // lines are relative to the stash, not the file
                stash.SiblingBlocks!.InsertBefore(stash, b);
            }
        }
        Detach(stash);
        ApplyDefaultFolds(restored);
        return restored;
    }

    private static void Detach(Block b)
    {
        if (b.SiblingBlocks is { } siblings && !siblings.Remove(b))
            throw new InvalidOperationException("Couldn't remove a block from the document.");
    }
}
