using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using WpfBlock = System.Windows.Documents.Block;
using WpfList = System.Windows.Documents.List;
using WpfTable = System.Windows.Documents.Table;

namespace Spark.Notes.Rich;

/// <summary>Serialises an editor <see cref="FlowDocument"/> back to note Markdown.</summary>
public static class FlowToMarkdown
{
    public static string Convert(FlowDocument doc, string? frontMatter = null)
    {
        var body = Blocks(doc.Blocks);
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(frontMatter))
        {
            sb.Append(frontMatter.TrimEnd('\n')).Append('\n');
            if (body.Length > 0) sb.Append('\n');
        }
        sb.Append(body);
        if (sb.Length > 0) sb.Append('\n');
        return sb.ToString();
    }

    private static string Blocks(IEnumerable<WpfBlock> blocks)
    {
        var parts = new List<string>();
        foreach (var b in blocks)
        {
            var s = Block(b);
            if (!string.IsNullOrWhiteSpace(s)) parts.Add(s);
        }
        return string.Join("\n\n", parts);
    }

    private static string Block(WpfBlock b)
    {
        var info = NoteDocument.InfoOf(b);
        switch (b)
        {
            case Paragraph p:
                return info.Kind switch
                {
                    BlockKind.Heading => new string('#', Math.Clamp(info.Level, 1, 6)) + " " + Inlines(p.Inlines, heading: true).Replace("\n", " "),
                    BlockKind.CodeBlock => CodeBlock(PlainText(p), info.Language),
                    BlockKind.Raw => PlainText(p),
                    _ => Inlines(p.Inlines),
                };
            case Section s when info.Kind == BlockKind.Quote:
            {
                var inner = Blocks(s.Blocks);
                if (inner.Length == 0) return "";
                return string.Join("\n", inner.Split('\n').Select(l => l.Length == 0 ? ">" : "> " + l));
            }
            case Section s:
                return Blocks(s.Blocks);
            case BlockUIContainer when info.Kind == BlockKind.Rule:
                return "---";
            case BlockUIContainer:
                return "";
            case WpfList list:
                return List(list);
            case WpfTable t:
                return Table(t);
            default:
                return "";
        }
    }

    private static string CodeBlock(string code, string? lang)
    {
        int longest = 0, run = 0;
        foreach (var c in code)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        var fence = new string('`', Math.Max(3, longest + 1));
        return $"{fence}{lang}\n{code}\n{fence}";
    }

    private static string List(WpfList list)
    {
        bool ordered = list.MarkerStyle is TextMarkerStyle.Decimal or TextMarkerStyle.LowerLatin or TextMarkerStyle.UpperLatin
            or TextMarkerStyle.LowerRoman or TextMarkerStyle.UpperRoman;
        int n = list.StartIndex;
        var items = new List<string>();
        foreach (var li in list.ListItems)
        {
            var marker = ordered ? $"{n++}. " : "- ";
            var indent = new string(' ', marker.Length);
            var sb = new StringBuilder();
            bool first = true;
            WpfBlock? prev = null;
            foreach (var child in li.Blocks)
            {
                string text;
                if (first && child is Paragraph fp && NoteDocument.TaskState(fp) is bool done)
                    text = (done ? "[x] " : "[ ] ") + Inlines(fp.Inlines);
                else
                    text = Block(child);
                if (string.IsNullOrWhiteSpace(text) && !first) continue;

                if (!first) sb.Append(child is WpfList || prev is WpfList ? "\n" : "\n\n");
                sb.Append(Indent(text, first ? "" : indent, indent));
                first = false;
                prev = child;
            }
            items.Add(marker + sb.ToString().TrimEnd());
        }
        return string.Join("\n", items);
    }

    /// <summary>Indents the first line with <paramref name="firstIndent"/> and the rest with <paramref name="restIndent"/>.</summary>
    private static string Indent(string text, string firstIndent, string restIndent)
    {
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            lines[i] = (i == 0 ? firstIndent : lines[i].Length == 0 ? "" : restIndent) + lines[i];
        return string.Join("\n", lines);
    }

    private static string Table(WpfTable t)
    {
        var rows = t.RowGroups.SelectMany(g => g.Rows).ToList();
        if (rows.Count == 0) return "";
        int cols = Math.Max(t.Columns.Count, rows.Max(r => r.Cells.Count));
        string Cell(TableCell c) => string.Join(" ", c.Blocks.OfType<Paragraph>().Select(p => Inlines(p.Inlines)))
            .Replace("\n", " ").Replace("|", "\\|").Trim();
        string Row(TableRow r) => "| " + string.Join(" | ",
            Enumerable.Range(0, cols).Select(i => i < r.Cells.Count ? Cell(r.Cells[i]) : "")) + " |";

        var sb = new StringBuilder();
        sb.Append(Row(rows[0])).Append('\n');
        sb.Append('|');
        for (int i = 0; i < cols; i++)
        {
            var align = rows[0].Cells.Count > i ? rows[0].Cells[i].Blocks.FirstBlock?.TextAlignment : TextAlignment.Left;
            sb.Append(align switch
            {
                TextAlignment.Center => " :---: |",
                TextAlignment.Right => " ---: |",
                _ => " --- |",
            });
        }
        foreach (var r in rows.Skip(1)) sb.Append('\n').Append(Row(r));
        return sb.ToString();
    }

    private static string PlainText(Paragraph p)
    {
        var sb = new StringBuilder();
        void Walk(InlineCollection inlines)
        {
            foreach (var i in inlines)
            {
                switch (i)
                {
                    case Run r: sb.Append(r.Text); break;
                    case LineBreak: sb.Append('\n'); break;
                    case Span s: Walk(s.Inlines); break;
                }
            }
        }
        Walk(p.Inlines);
        return sb.ToString().Replace("\r\n", "\n").Replace('\r', '\n');
    }

    // ---------- Inlines ----------

    [Flags]
    private enum F { None = 0, Bold = 1, Italic = 2, Strike = 4 }

    private enum SegKind { Text, Code, Break, Markup }

    private sealed record Seg(SegKind Kind, string Text, F Flags);

    private static string Inlines(InlineCollection inlines, bool heading = false)
    {
        var segs = new List<Seg>();
        Collect(inlines, segs);
        return Emit(Merge(segs));
    }

    private static void Collect(InlineCollection inlines, List<Seg> segs)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case InlineUIContainer { Tag: TaskMarker }:
                    break; // emitted by the list serializer
                case InlineUIContainer:
                    break;
                case LineBreak:
                    segs.Add(new Seg(SegKind.Break, "\n", F.None));
                    break;
                case Hyperlink h when NoteDocument.WikiTagOf(h) is { } wl:
                {
                    var display = NoteDocument.LinkText(h);
                    var link = new WikiLink(wl.Target, wl.Heading, null, wl.IsEmbed);
                    if (display.Length > 0 && display != link.DefaultDisplay && display != wl.Target)
                        link = link with { Alias = display.Replace("]]", "] ]").Replace("|", "/") };
                    segs.Add(new Seg(SegKind.Markup, link.ToString(), FlagsOf(h)));
                    break;
                }
                case Hyperlink h:
                {
                    var text = NoteDocument.LinkText(h);
                    var url = NoteDocument.UrlOf(h) ?? "";
                    var md = text == url && Regex.IsMatch(url, @"^[a-zA-Z][a-zA-Z0-9+.-]*:\S+$")
                        ? $"<{url}>"
                        : $"[{EscapeText(text)}]({EscapeUrl(url)})";
                    segs.Add(new Seg(SegKind.Markup, md, FlagsOf(h)));
                    break;
                }
                case Run { Tag: "raw" } raw:
                    segs.Add(new Seg(SegKind.Markup, raw.Text, F.None));
                    break;
                case Run r:
                {
                    var text = r.Text.Replace("\r\n", "\n").Replace('\r', '\n');
                    var kind = NoteDocument.IsCodeFont(r.FontFamily) ? SegKind.Code : SegKind.Text;
                    var lines = text.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (i > 0) segs.Add(new Seg(SegKind.Break, "\n", F.None));
                        if (lines[i].Length > 0) segs.Add(new Seg(kind, lines[i], FlagsOf(r)));
                    }
                    break;
                }
                case Span s:
                    Collect(s.Inlines, segs);
                    break;
            }
        }
    }

    private static F FlagsOf(Inline i)
    {
        var f = F.None;
        if (i.FontWeight.ToOpenTypeWeight() >= 700) f |= F.Bold;
        if (i.FontStyle == FontStyles.Italic || i.FontStyle == FontStyles.Oblique) f |= F.Italic;
        if (i.TextDecorations != null && i.TextDecorations.Any(d => d.Location == TextDecorationLocation.Strikethrough)) f |= F.Strike;
        return f;
    }

    private static List<Seg> Merge(List<Seg> segs)
    {
        var result = new List<Seg>();
        foreach (var s in segs)
        {
            if (result.Count > 0)
            {
                var last = result[^1];
                if (last.Kind == s.Kind && last.Flags == s.Flags && s.Kind is SegKind.Text or SegKind.Code)
                {
                    result[^1] = last with { Text = last.Text + s.Text };
                    continue;
                }
            }
            result.Add(s);
        }
        return result;
    }

    private static readonly F[] Order = { F.Bold, F.Italic, F.Strike };

    private static string Emit(List<Seg> segs)
    {
        var sb = new StringBuilder();
        var open = new List<(F Flag, string Marker)>();
        string pendingWs = "";

        void CloseTo(int depth)
        {
            for (int i = open.Count - 1; i >= depth; i--) sb.Append(Reverse(open[i].Marker));
            open.RemoveRange(depth, open.Count - depth);
        }

        for (int si = 0; si < segs.Count; si++)
        {
            var seg = segs[si];
            if (seg.Kind == SegKind.Break)
            {
                CloseTo(0);
                pendingWs = "";
                sb.Append('\n');
                continue;
            }

            string core = seg.Text, lead = "", trail = "";
            if (seg.Kind == SegKind.Text)
            {
                int a = 0, z = core.Length;
                while (a < z && char.IsWhiteSpace(core[a])) a++;
                while (z > a && char.IsWhiteSpace(core[z - 1])) z--;
                lead = core[..a];
                trail = core[z..];
                core = core[a..z];
                if (core.Length == 0)
                {
                    pendingWs += seg.Text;
                    continue;
                }
                core = EscapeText(core);
            }
            else if (seg.Kind == SegKind.Code)
            {
                core = CodeSpan(core);
            }

            // Keep the longest prefix of already-open markers that the new segment still wants.
            int keep = 0;
            while (keep < open.Count && seg.Flags.HasFlag(open[keep].Flag)) keep++;
            CloseTo(keep);

            var ws = pendingWs + lead;
            pendingWs = "";
            sb.Append(ws);

            foreach (var flag in Order)
            {
                if (!seg.Flags.HasFlag(flag) || open.Any(o => o.Flag == flag)) continue;
                var marker = flag switch
                {
                    F.Bold => "**",
                    F.Strike => "~~",
                    _ => ItalicMarker(sb, core),
                };
                sb.Append(marker);
                open.Add((flag, marker));
            }
            sb.Append(core);
            pendingWs = trail;
        }
        CloseTo(0);
        sb.Append(pendingWs.TrimEnd());

        // Escape characters that would otherwise start a block construct at the beginning of a line.
        var lines = sb.ToString().Split('\n').Select(l => EscapeLineStart(l.TrimStart(' ', '\t').TrimEnd(' ', '\t')));
        return string.Join("\n", lines);
    }

    private static string Reverse(string marker) => new(marker.Reverse().ToArray());

    /// <summary>Use * for italics, except directly after another * marker where "***" would be ambiguous.</summary>
    private static string ItalicMarker(StringBuilder sb, string core) =>
        sb.Length > 0 && sb[^1] == '*' ? "_" : "*";

    private static string CodeSpan(string code)
    {
        int longest = 0, run = 0;
        foreach (var c in code)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        var ticks = new string('`', longest + 1);
        bool pad = code.StartsWith('`') || code.EndsWith('`') || (code.StartsWith(' ') && code.EndsWith(' ') && code.Trim().Length > 0);
        return pad ? $"{ticks} {code} {ticks}" : $"{ticks}{code}{ticks}";
    }

    private static string EscapeText(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '\\':
                case '`':
                case '*':
                case '[':
                case ']':
                case '<':
                case '~':
                    sb.Append('\\').Append(c);
                    break;
                case '_':
                    bool intraword = i > 0 && i < text.Length - 1 && char.IsLetterOrDigit(text[i - 1]) && char.IsLetterOrDigit(text[i + 1]);
                    if (!intraword) sb.Append('\\');
                    sb.Append(c);
                    break;
                case '&':
                    if (Regex.IsMatch(text.AsSpan(i).ToString(), @"^&(#\d+|#[xX][0-9a-fA-F]+|[a-zA-Z][a-zA-Z0-9]*);")) sb.Append('\\');
                    sb.Append(c);
                    break;
                case '!':
                    sb.Append(c);
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private static readonly Regex LineStart = new(@"^(#{1,6}(\s|$)|>|[-+](\s|$)|\d{1,9}[.)](\s|$)|={2,}\s*$|-{2,}\s*$|\|)", RegexOptions.Compiled);

    private static string EscapeLineStart(string line)
    {
        var m = LineStart.Match(line);
        if (!m.Success) return line;
        // Escape the punctuation that triggers the construct (for "1." escape the dot).
        int idx = char.IsDigit(line[0]) ? line.IndexOfAny(new[] { '.', ')' }) : 0;
        return line[..idx] + "\\" + line[idx..];
    }

    private static string EscapeUrl(string url) =>
        url.Contains(' ') || url.Contains(')') || url.Contains('(') ? $"<{url.Replace("<", "%3C").Replace(">", "%3E")}>" : url;
}
