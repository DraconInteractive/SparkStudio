using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Spark.Notes.Rich;

/// <summary>A callout's marker: <c>[!type]</c> plus an optional fold default (<c>-</c> collapsed, <c>+</c> expanded).</summary>
/// <param name="MarkerLength">Length of the marker text ("[!type]-") at the start of the title line.</param>
public sealed record CalloutInfo(string Type, char? Fold, int MarkerLength)
{
    public bool StartsCollapsed => Fold == '-';
    public string Marker => $"[!{Type}]{Fold}";
}

/// <summary>
/// Obsidian-style callouts: a quote whose first line starts with <c>[!type]</c>, e.g.
/// <c>&gt; [!warning]- Title</c>. In the document a callout is a quote <see cref="Section"/> whose first paragraph
/// (the title) begins with the marker text, so the marker survives undo, copy/paste and editing; the section is
/// styled (tinted, coloured edge, coloured title) to match the type. Nesting is a callout inside a callout's section.
/// </summary>
public static class NoteCallouts
{
    private static readonly Regex MarkerPattern = new(@"^\[!(?<type>[A-Za-z][\w-]*)\](?<fold>[+-]?)(?=\s|$)", RegexOptions.Compiled);

    /// <summary>The types offered in menus (Obsidian's, in its order). Other names still work and look like notes.</summary>
    public static readonly IReadOnlyList<string> Types = new[]
    {
        "note", "abstract", "info", "todo", "tip", "success", "question", "warning", "failure", "danger", "bug", "example", "quote",
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["summary"] = "abstract", ["tldr"] = "abstract",
        ["hint"] = "tip", ["important"] = "tip",
        ["check"] = "success", ["done"] = "success",
        ["help"] = "question", ["faq"] = "question",
        ["caution"] = "warning", ["attention"] = "warning",
        ["fail"] = "failure", ["missing"] = "failure",
        ["error"] = "danger",
        ["cite"] = "quote",
    };

    private static readonly Dictionary<string, string> Colors = new()
    {
        ["note"] = "#086DDD", ["abstract"] = "#00A6A3", ["info"] = "#086DDD", ["todo"] = "#086DDD",
        ["tip"] = "#00A6A3", ["success"] = "#08A045", ["question"] = "#D97000", ["warning"] = "#D97000",
        ["failure"] = "#E93147", ["danger"] = "#E93147", ["bug"] = "#E93147", ["example"] = "#7852EE", ["quote"] = "#8A8A8A",
    };

    private static readonly Dictionary<string, string> Glyphs = new()
    {
        ["note"] = "", ["abstract"] = "", ["info"] = "", ["todo"] = "", ["tip"] = "",
        ["success"] = "", ["question"] = "", ["warning"] = "", ["failure"] = "",
        ["danger"] = "", ["bug"] = "", ["example"] = "", ["quote"] = "",
    };

    private static readonly Dictionary<string, (Brush Edge, Brush Tint)> Brushes = new();

    /// <summary>The canonical type a name maps to ("hint" → "tip"); unknown names map to "note" for their look.</summary>
    public static string Canonical(string type)
    {
        var t = type.ToLowerInvariant();
        if (Aliases.TryGetValue(t, out var a)) return a;
        return Colors.ContainsKey(t) ? t : "note";
    }

    public static string GlyphOf(string type) => Glyphs[Canonical(type)];

    public static Brush EdgeBrush(string type) => BrushesOf(type).Edge;

    private static (Brush Edge, Brush Tint) BrushesOf(string type)
    {
        var key = Canonical(type);
        if (Brushes.TryGetValue(key, out var b)) return b;
        var c = (Color)ColorConverter.ConvertFromString(Colors[key]);
        var edge = new SolidColorBrush(c);
        var tint = new SolidColorBrush(Color.FromArgb(0x1A, c.R, c.G, c.B));
        edge.Freeze();
        tint.Freeze();
        return Brushes[key] = (edge, tint);
    }

    /// <summary>Parses a callout marker at the start of a title line.</summary>
    public static CalloutInfo? Parse(string titleLine)
    {
        var m = MarkerPattern.Match(titleLine);
        if (!m.Success) return null;
        var fold = m.Groups["fold"].Value;
        return new CalloutInfo(m.Groups["type"].Value, fold.Length == 1 ? fold[0] : null, m.Length);
    }

    /// <summary>True for a quote section (callout or not).</summary>
    public static bool IsQuote(Block b) => b is Section && NoteDocument.InfoOf(b).Kind == BlockKind.Quote;

    /// <summary>The callout a section is, or null if it's a plain quote (or not a quote).</summary>
    public static CalloutInfo? InfoOf(Section s) =>
        IsQuote(s) && s.Blocks.FirstBlock is Paragraph title && NoteDocument.InfoOf(title).Kind == BlockKind.Paragraph
            ? Parse(FirstLine(title))
            : null;

    /// <summary>The innermost callout containing a position, if any.</summary>
    public static Section? CalloutAt(TextPointer? pos)
    {
        for (DependencyObject? o = pos?.Paragraph; o != null; o = (o as FrameworkContentElement)?.Parent)
            if (o is Section s && InfoOf(s) != null) return s;
        return null;
    }

    /// <summary>The paragraph's text up to its first line break (links included, embedded controls skipped).</summary>
    public static string FirstLine(Paragraph p)
    {
        var sb = new StringBuilder();
        bool Walk(InlineCollection inlines)
        {
            foreach (var i in inlines)
            {
                switch (i)
                {
                    case LineBreak: return false;
                    case Run r: sb.Append(r.Text); break;
                    case Span s when !Walk(s.Inlines): return false;
                }
            }
            return true;
        }
        Walk(p.Inlines);
        var text = sb.ToString();
        int nl = text.IndexOfAny(new[] { '\r', '\n' });
        return nl < 0 ? text : text[..nl];
    }

    /// <summary>
    /// Styles a quote section as the callout its title names, or as a plain quote. Only properties that differ are
    /// set (each property change is an undo step and a text change in the editor). Returns true if anything changed.
    /// </summary>
    public static bool ApplyStyle(Section s)
    {
        if (!IsQuote(s)) return false;
        var info = InfoOf(s);
        bool changed = false;
        void Set(DependencyObject o, DependencyProperty dp, object? value)
        {
            if (Equals(o.ReadLocalValue(dp), value)) return;
            if (value == null && o.ReadLocalValue(dp) == DependencyProperty.UnsetValue) return;
            if (value == null) o.ClearValue(dp);
            else o.SetValue(dp, value);
            changed = true;
        }

        if (info != null)
        {
            var (edge, tint) = BrushesOf(info.Type);
            Set(s, Block.BorderBrushProperty, edge);
            Set(s, Block.BorderThicknessProperty, new Thickness(3, 0, 0, 0));
            Set(s, Block.BackgroundProperty, tint);
            Set(s, Block.PaddingProperty, new Thickness(12, 6, 12, 2));
            Set(s, TextElement.ForegroundProperty, NoteDocument.Foreground);
        }
        else
        {
            var model = NoteDocument.NewQuote();
            Set(s, Block.BorderBrushProperty, model.BorderBrush);
            Set(s, Block.BorderThicknessProperty, model.BorderThickness);
            Set(s, Block.BackgroundProperty, null);
            Set(s, Block.PaddingProperty, model.Padding);
            Set(s, TextElement.ForegroundProperty, model.Foreground);
        }

        bool first = true;
        foreach (var p in s.Blocks.OfType<Paragraph>())
        {
            bool isTitle = first && info != null;
            first = false;
            if (NoteDocument.InfoOf(p).Kind != BlockKind.Paragraph) continue;
            if (isTitle)
            {
                Set(p, TextElement.ForegroundProperty, BrushesOf(info!.Type).Edge);
                Set(p, TextElement.FontWeightProperty, FontWeights.SemiBold);
                Set(p, Block.MarginProperty, new Thickness(0, 0, 0, 6));
            }
            else if (p.ReadLocalValue(TextElement.ForegroundProperty) is SolidColorBrush b && IsCalloutBrush(b))
            {
                // A former title (or a paragraph split off one): back to body text.
                Set(p, TextElement.ForegroundProperty, null);
                Set(p, TextElement.FontWeightProperty, null);
                Set(p, Block.MarginProperty, new Thickness(0, 0, 0, 10));
            }
        }
        return changed;
    }

    private static bool IsCalloutBrush(SolidColorBrush b) => Brushes.Values.Any(v => ReferenceEquals(v.Edge, b) || v.Edge.Equals(b) ||
        (v.Edge is SolidColorBrush e && e.Color == b.Color));

    /// <summary>
    /// After loading: a title paragraph that also holds body lines ("&gt; [!note] Title\n&gt; body" is one Markdown
    /// paragraph) is split at its first line break, so the body can be folded separately from the title.
    /// </summary>
    public static void SplitTitle(Section s)
    {
        if (InfoOf(s) == null || s.Blocks.FirstBlock is not Paragraph title) return;
        MergeLeadingRuns(title);
        var br = title.Inlines.OfType<LineBreak>().FirstOrDefault();
        if (br == null) return;
        var rest = new List<Inline>();
        for (var i = br.NextInline; i != null; i = i.NextInline) rest.Add(i);
        title.Inlines.Remove(br);
        foreach (var i in rest) title.Inlines.Remove(i);
        if (rest.Count == 0) return;
        var body = new Paragraph();
        NoteDocument.Apply(body, new BlockInfo(BlockKind.Paragraph));
        foreach (var i in rest) body.Inlines.Add(i);
        NoteDocument.SetSourceLine(body, NoteDocument.GetSourceLine(title) < 0 ? -1 : NoteDocument.GetSourceLine(title) + 1);
        s.Blocks.InsertAfter(title, body);
    }

    /// <summary>
    /// Joins the unformatted runs at the start of a paragraph. The Markdown parser splits "[!note] Title" into
    /// "[", "!note", "]", " Title"; one run keeps the marker editable as a unit.
    /// </summary>
    private static void MergeLeadingRuns(Paragraph p)
    {
        if (p.Inlines.FirstInline is not Run first || !IsPlain(first)) return;
        while (first.NextInline is Run next && IsPlain(next))
        {
            first.Text += next.Text;
            p.Inlines.Remove(next);
        }
    }

    private static bool IsPlain(Run r) =>
        r.Tag == null &&
        new[] { TextElement.FontWeightProperty, TextElement.FontStyleProperty, Inline.TextDecorationsProperty,
                TextElement.FontFamilyProperty, TextElement.BackgroundProperty, TextElement.ForegroundProperty }
            .All(dp => r.ReadLocalValue(dp) == DependencyProperty.UnsetValue);

    private static readonly Regex EscapedMarker = new(@"^\\\[!([A-Za-z][\w-]*)\\\]", RegexOptions.Compiled);

    /// <summary>The serializer escapes "[" and "]" in text; a callout's marker must stay literal.</summary>
    internal static string UnescapeMarker(string markdown) => EscapedMarker.Replace(markdown, "[!$1]", 1);

    /// <summary>A new callout section: title paragraph "[!type] " (plus <paramref name="title"/>) and an empty body paragraph.</summary>
    public static Section Create(string type, string title = "")
    {
        var s = NoteDocument.NewQuote();
        var t = new Paragraph(new Run($"[!{type}] {title}"));
        NoteDocument.Apply(t, new BlockInfo(BlockKind.Paragraph));
        var body = new Paragraph();
        NoteDocument.Apply(body, new BlockInfo(BlockKind.Paragraph));
        s.Blocks.Add(t);
        s.Blocks.Add(body);
        ApplyStyle(s);
        return s;
    }

    /// <summary>
    /// Rewrites the marker at the start of a callout's title (type and/or fold default). Returns false when the
    /// marker isn't contained in the title's first run (e.g. it was partly formatted), in which case nothing changes.
    /// </summary>
    public static bool SetMarker(Section s, string type, char? fold)
    {
        if (InfoOf(s) is not { } info || s.Blocks.FirstBlock is not Paragraph title) return false;
        MergeLeadingRuns(title);
        if (title.Inlines.FirstInline is not Run run || run.Text.Length < info.MarkerLength) return false;
        run.Text = $"[!{type}]{fold}" + run.Text[info.MarkerLength..];
        ApplyStyle(s);
        return true;
    }
}
