using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Spark.Notes.Rich;

public enum BlockKind { Paragraph, Heading, CodeBlock, Quote, Rule, Table, Raw }

/// <summary>Stored in <see cref="FrameworkContentElement.Tag"/> of blocks to record their Markdown meaning.</summary>
public sealed record BlockInfo(BlockKind Kind, int Level = 0, string? Language = null);

/// <summary>Stored in a <see cref="Hyperlink"/>'s Tag to mark it as a wikilink.</summary>
public sealed record WikiLinkTag(string Target, string? Heading, bool IsEmbed);

/// <summary>Stored in a task-list <see cref="CheckBox"/>'s container Tag.</summary>
public sealed record TaskMarker;

/// <summary>Visual conventions shared by the converters and the editor.</summary>
public static class NoteDocument
{
    /// <summary>
    /// The 0-based Markdown line a block was converted from (-1 for blocks typed since loading). Lets search
    /// results jump from a line number in the file to the right place in the editor.
    /// </summary>
    public static readonly DependencyProperty SourceLineProperty = DependencyProperty.RegisterAttached(
        "SourceLine", typeof(int), typeof(NoteDocument), new PropertyMetadata(-1));

    public static int GetSourceLine(DependencyObject o) => (int)o.GetValue(SourceLineProperty);
    public static void SetSourceLine(DependencyObject o, int line) => o.SetValue(SourceLineProperty, line);

    public static FontFamily BodyFont { get; set; } = new("Segoe UI");
    public static FontFamily CodeFont { get; set; } = new("Cascadia Mono, Consolas, Courier New");
    public static double BodySize { get; set; } = 14.5;

    public static Brush Foreground { get; set; } = Brush("#1F2328");
    public static Brush Muted { get; set; } = Brush("#656D76");
    public static Brush Accent { get; set; } = Brush("#6E56CF");
    public static Brush Unresolved { get; set; } = Brush("#A79BD9");
    public static Brush CodeBackground { get; set; } = Brush("#F2F1F4");
    public static Brush QuoteBorder { get; set; } = Brush("#D0C9EE");
    public static Brush RuleBrush { get; set; } = Brush("#D8DEE4");

    private static readonly double[] HeadingSizes = { 0, 27, 22, 18.5, 16, 15, 14.5 };

    public static Brush Brush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    /// <summary>
    /// The Markdown meaning of a block. Uses the Tag when present; otherwise infers it from the visuals, because
    /// WPF copy/paste and undo recreate elements without their Tag.
    /// </summary>
    public static BlockInfo InfoOf(Block b)
    {
        if (b.Tag is BlockInfo info) return info;
        switch (b)
        {
            case Paragraph p when IsCodeFont(p.FontFamily) && p.Background != null:
                return new BlockInfo(BlockKind.CodeBlock);
            case Paragraph p when IsCodeFont(p.FontFamily):
                return new BlockInfo(BlockKind.Raw);
            case Paragraph p when p.FontWeight.ToOpenTypeWeight() >= 600 && p.FontSize >= BodySize + 1 && p.Parent is not TableCell:
                for (int lvl = 1; lvl <= 6; lvl++)
                    if (p.FontSize >= HeadingSizes[lvl] - 0.75) return new BlockInfo(BlockKind.Heading, lvl);
                return new BlockInfo(BlockKind.Heading, 4);
            case Section s when s.BorderThickness.Left >= 2:
                return new BlockInfo(BlockKind.Quote);
            case BlockUIContainer { Child: Separator }:
                return new BlockInfo(BlockKind.Rule);
            default:
                return new BlockInfo(BlockKind.Paragraph);
        }
    }

    public const string WikiScheme = "spark-wiki";

    public static Uri WikiUri(WikiLink link) =>
        new($"{WikiScheme}:{Uri.EscapeDataString(link.Target)}" + (link.Heading != null ? "#" + Uri.EscapeDataString(link.Heading) : ""));

    /// <summary>The wikilink a hyperlink represents (from its Tag, or its spark-wiki: URI after copy/paste).</summary>
    public static WikiLinkTag? WikiTagOf(Hyperlink h)
    {
        if (h.Tag is WikiLinkTag t) return t;
        var uri = h.NavigateUri?.OriginalString;
        if (uri == null || !uri.StartsWith(WikiScheme + ":", StringComparison.Ordinal)) return null;
        var rest = uri[(WikiScheme.Length + 1)..];
        var hash = rest.IndexOf('#');
        var target = Uri.UnescapeDataString(hash < 0 ? rest : rest[..hash]);
        var heading = hash < 0 ? null : Uri.UnescapeDataString(rest[(hash + 1)..]);
        return new WikiLinkTag(target, heading, false);
    }

    public static bool IsCodeFont(FontFamily f) =>
        f.Source.Contains("Consolas", StringComparison.OrdinalIgnoreCase) ||
        f.Source.Contains("Cascadia", StringComparison.OrdinalIgnoreCase) ||
        f.Source.Contains("Courier", StringComparison.OrdinalIgnoreCase);

    public static FlowDocument NewDocument() => new()
    {
        FontFamily = BodyFont,
        FontSize = BodySize,
        Foreground = Foreground,
        PagePadding = new Thickness(36, 24, 36, 48),
        LineHeight = double.NaN,
        TextAlignment = TextAlignment.Left,
    };

    /// <summary>Applies the look for a paragraph kind and records it in the Tag.</summary>
    public static void Apply(Paragraph p, BlockInfo info)
    {
        p.Tag = info;
        p.ClearValue(TextElement.FontSizeProperty);
        p.ClearValue(TextElement.FontWeightProperty);
        p.ClearValue(TextElement.FontFamilyProperty);
        p.ClearValue(TextElement.ForegroundProperty);
        p.ClearValue(Block.BackgroundProperty);
        p.ClearValue(Block.PaddingProperty);
        p.Margin = new Thickness(0, 0, 0, 10);

        switch (info.Kind)
        {
            case BlockKind.Heading:
                int lvl = Math.Clamp(info.Level, 1, 6);
                p.FontSize = HeadingSizes[lvl];
                p.FontWeight = FontWeights.SemiBold;
                p.Margin = new Thickness(0, lvl <= 2 ? 18 : 12, 0, 8);
                break;
            case BlockKind.CodeBlock:
                p.FontFamily = CodeFont;
                p.FontSize = BodySize - 1;
                p.Background = CodeBackground;
                p.Padding = new Thickness(12, 8, 12, 8);
                break;
            case BlockKind.Raw:
                p.FontFamily = CodeFont;
                p.FontSize = BodySize - 1.5;
                p.Foreground = Muted;
                break;
        }
    }

    public static Section NewQuote() => new()
    {
        Tag = new BlockInfo(BlockKind.Quote),
        BorderBrush = QuoteBorder,
        BorderThickness = new Thickness(3, 0, 0, 0),
        Padding = new Thickness(12, 0, 0, 0),
        Margin = new Thickness(0, 0, 0, 10),
        Foreground = Muted,
    };

    public static BlockUIContainer NewRule() => new(new Separator { Margin = new Thickness(0, 6, 0, 6), Background = RuleBrush })
    {
        Tag = new BlockInfo(BlockKind.Rule),
        Margin = new Thickness(0, 4, 0, 10),
    };

    public static List NewList(bool ordered, int start = 1) => new()
    {
        MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
        StartIndex = Math.Max(1, start),
        Margin = new Thickness(0, 0, 0, 10),
        Padding = new Thickness(24, 0, 0, 0),
    };

    public static InlineUIContainer NewTaskBox(bool isChecked)
    {
        var box = new CheckBox
        {
            IsChecked = isChecked,
            Margin = new Thickness(0, 0, 6, -3),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Arrow,
            Focusable = false,
        };
        return new InlineUIContainer(box) { Tag = new TaskMarker(), BaselineAlignment = BaselineAlignment.Center };
    }

    public static bool? TaskState(Paragraph p) =>
        p.Inlines.FirstInline is InlineUIContainer { Child: CheckBox cb } ? cb.IsChecked == true : null;

    public static Hyperlink NewWikiLink(WikiLink link, bool resolved)
    {
        var h = new Hyperlink(new Run(link.Display));
        ConfigureWikiLink(h, link, resolved);
        return h;
    }

    public static void ConfigureWikiLink(Hyperlink h, WikiLink link, bool resolved)
    {
        h.Tag = new WikiLinkTag(link.Target, link.Heading, link.IsEmbed);
        h.NavigateUri = WikiUri(link);
        StyleWikiLink(h, resolved);
    }

    public static void StyleWikiLink(Hyperlink h, bool resolved)
    {
        h.Foreground = resolved ? Accent : Unresolved;
        h.TextDecorations = resolved ? null : TextDecorations.Underline;
        h.Cursor = System.Windows.Input.Cursors.Hand;
        h.ToolTip = resolved ? "Ctrl+Click to open" : "Note does not exist yet. Ctrl+Click to create it";
    }

    public static Hyperlink NewExternalLink(string text, string url)
    {
        var h = new Hyperlink(new Run(text)) { Foreground = Accent, ToolTip = url + "  (Ctrl+Click to open)" };
        if (Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out var uri)) h.NavigateUri = uri;
        else h.Tag = url;
        return h;
    }

    /// <summary>
    /// The text a link displays. Built from its own runs rather than a <see cref="TextRange"/>: a TextRange that starts
    /// at the beginning of a list item also includes the list marker ("•\t"), which would leak into the saved alias.
    /// </summary>
    public static string LinkText(Span link)
    {
        var sb = new System.Text.StringBuilder();
        void Walk(InlineCollection inlines)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case Run r: sb.Append(r.Text); break;
                    case LineBreak: sb.Append(' '); break;
                    case Span s: Walk(s.Inlines); break;
                }
            }
        }
        Walk(link.Inlines);
        return WikiLink.StripListMarkers(sb.ToString());
    }

    /// <summary>The URL of an external link (NavigateUri or a raw string when it wasn't a valid URI).</summary>
    public static string? UrlOf(Hyperlink h) =>
        WikiTagOf(h) != null ? null : h.Tag is string s ? s : h.NavigateUri?.OriginalString;

    public static Run CodeRun(string text) => new(text) { FontFamily = CodeFont, Background = CodeBackground, FontSize = BodySize - 1 };
}
