using System.Text;
using System.Text.RegularExpressions;

namespace Spark.Notes;

/// <summary>
/// An Obsidian-style cross reference: [[Target]], [[Target#Heading]], [[Target|Alias]] or [[folder/Target]].
/// </summary>
public sealed record WikiLink(string Target, string? Heading = null, string? Alias = null, bool IsEmbed = false)
{
    /// <summary>Matches a complete link. Groups: embed, target, heading, alias.</summary>
    public static readonly Regex Pattern = new(
        @"(?<embed>!)?\[\[(?<target>[^\[\]\|#\r\n]+)(?:#(?<heading>[^\[\]\|\r\n]*))?(?:\|(?<alias>[^\[\]\r\n]*))?\]\]",
        RegexOptions.Compiled);

    /// <summary>Parses the text between the brackets, e.g. "Note#Heading|alias".</summary>
    public static WikiLink? ParseInner(string inner, bool isEmbed = false)
    {
        var m = Pattern.Match("[[" + inner + "]]");
        if (!m.Success || m.Index != 0 || m.Length != inner.Length + 4) return null;
        return FromMatch(m) with { IsEmbed = isEmbed };
    }

    public static WikiLink FromMatch(Match m)
    {
        static string? Opt(Group g) => g.Success ? g.Value.Trim() : null;
        var heading = Opt(m.Groups["heading"]);
        var alias = Opt(m.Groups["alias"]);
        return new WikiLink(m.Groups["target"].Value.Trim(),
            string.IsNullOrEmpty(heading) ? null : heading,
            string.IsNullOrEmpty(alias) ? null : alias,
            m.Groups["embed"].Success);
    }

    public static IEnumerable<WikiLink> FindAll(string markdown)
    {
        foreach (var (start, length) in OutsideCode(markdown))
            foreach (Match m in Pattern.Matches(markdown.Substring(start, length)))
                yield return FromMatch(m);
    }

    /// <summary>The text shown for the link when it has no alias.</summary>
    public string DefaultDisplay => Heading == null ? Target : $"{Target} > {Heading}";

    public string Display => Alias ?? DefaultDisplay;

    public override string ToString()
    {
        var sb = new StringBuilder();
        if (IsEmbed) sb.Append('!');
        sb.Append("[[").Append(Target);
        if (Heading != null) sb.Append('#').Append(Heading);
        if (Alias != null) sb.Append('|').Append(Alias);
        return sb.Append("]]").ToString();
    }

    /// <summary>
    /// Rewrites every link whose target satisfies <paramref name="matches"/> to point at <paramref name="newTarget"/>,
    /// keeping headings and aliases. Fenced code blocks and inline code are left untouched.
    /// </summary>
    public static string RewriteTargets(string markdown, Func<string, bool> matches, string newTarget, out int count)
    {
        var sb = new StringBuilder(markdown.Length);
        int pos = 0, n = 0;
        foreach (var (start, length) in OutsideCode(markdown))
        {
            sb.Append(markdown, pos, start - pos);
            var chunk = markdown.Substring(start, length);
            sb.Append(Pattern.Replace(chunk, m =>
            {
                var link = FromMatch(m);
                if (!matches(link.Target)) return m.Value;
                n++;
                return (link with { Target = newTarget }).ToString();
            }));
            pos = start + length;
        }
        sb.Append(markdown, pos, markdown.Length - pos);
        count = n;
        return sb.ToString();
    }

    /// <summary>Yields (start, length) ranges of the text that lie outside fenced code blocks and inline code spans.</summary>
    internal static IEnumerable<(int Start, int Length)> OutsideCode(string text)
    {
        int i = 0, segStart = 0;
        bool lineStart = true;
        while (i < text.Length)
        {
            if (lineStart)
            {
                int j = i;
                while (j < text.Length && j - i < 4 && text[j] == ' ') j++;
                if (j + 2 < text.Length && (text[j] == '`' || text[j] == '~') && text[j + 1] == text[j] && text[j + 2] == text[j])
                {
                    char fence = text[j];
                    int fenceLen = 0;
                    while (j + fenceLen < text.Length && text[j + fenceLen] == fence) fenceLen++;
                    if (i > segStart) yield return (segStart, i - segStart);
                    // Skip to the closing fence (or end of document).
                    int k = text.IndexOf('\n', j);
                    while (k >= 0)
                    {
                        int ls = k + 1, p = ls;
                        while (p < text.Length && p - ls < 4 && text[p] == ' ') p++;
                        int run = 0;
                        while (p + run < text.Length && text[p + run] == fence) run++;
                        k = text.IndexOf('\n', ls);
                        if (run >= fenceLen) break;
                    }
                    i = k < 0 ? text.Length : k + 1;
                    segStart = i;
                    lineStart = true;
                    continue;
                }
            }
            char c = text[i];
            if (c == '`')
            {
                int run = 0;
                while (i + run < text.Length && text[i + run] == '`') run++;
                int close = FindBacktickRun(text, i + run, run);
                if (close >= 0)
                {
                    if (i > segStart) yield return (segStart, i - segStart);
                    i = close + run;
                    segStart = i;
                    lineStart = false;
                    continue;
                }
                i += run;
                lineStart = false;
                continue;
            }
            lineStart = c == '\n';
            i++;
        }
        if (text.Length > segStart) yield return (segStart, text.Length - segStart);
    }

    private static int FindBacktickRun(string text, int from, int run)
    {
        int i = from;
        while (i < text.Length)
        {
            if (text[i] == '\n' && i + 1 < text.Length && text[i + 1] == '\n') return -1; // spans don't cross paragraphs
            if (text[i] == '`')
            {
                int r = 0;
                while (i + r < text.Length && text[i + r] == '`') r++;
                if (r == run) return i;
                i += r;
                continue;
            }
            i++;
        }
        return -1;
    }
}
