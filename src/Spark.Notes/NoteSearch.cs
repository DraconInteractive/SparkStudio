using Spark.Api;

namespace Spark.Notes;

public sealed record SearchOptions(bool MatchCase = false, bool WholeWord = false);

/// <summary>
/// One line of a note that matches a search. <see cref="Line"/> is 1-based (as shown to the user);
/// <see cref="Column"/> is the 0-based position of the first match on that line.
/// </summary>
public sealed record SearchHit(SparkFile File, int Line, int Column, int Length, string LineText, int MatchesOnLine);

/// <summary>Plain-text term search shared by the per-note find bar and the global search.</summary>
public static class NoteSearch
{
    /// <summary>Start indexes of non-overlapping matches of <paramref name="query"/> in <paramref name="text"/>.</summary>
    public static IEnumerable<int> FindAll(string text, string query, SearchOptions options)
    {
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(text)) yield break;
        var comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int i = 0;
        while (i <= text.Length - query.Length)
        {
            int at = text.IndexOf(query, i, comparison);
            if (at < 0) yield break;
            if (!options.WholeWord || IsWholeWord(text, at, query.Length))
            {
                yield return at;
                i = at + query.Length;
            }
            else i = at + 1;
        }
    }

    public static int Count(string text, string query, SearchOptions options) => FindAll(text, query, options).Count();

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool IsWholeWord(string text, int at, int length) =>
        (at == 0 || !IsWordChar(text[at - 1])) && (at + length >= text.Length || !IsWordChar(text[at + length]));

    public static string[] Lines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    /// <summary>Every line of <paramref name="text"/> containing the query, in order.</summary>
    public static List<SearchHit> Search(SparkFile file, string text, string query, SearchOptions options)
    {
        var hits = new List<SearchHit>();
        if (string.IsNullOrWhiteSpace(query)) return hits;
        var lines = Lines(text);
        for (int n = 0; n < lines.Length; n++)
        {
            var matches = FindAll(lines[n], query, options).ToList();
            if (matches.Count > 0)
                hits.Add(new SearchHit(file, n + 1, matches[0], query.Length, lines[n], matches.Count));
        }
        return hits;
    }

    /// <summary>
    /// How many matches come before the match at (<paramref name="line0"/>, <paramref name="column"/>), counting
    /// from the start of line <paramref name="fromLine0"/>. Used to find the same match in the rendered note.
    /// </summary>
    public static int OccurrenceIndex(string source, int fromLine0, int line0, int column, string query, SearchOptions options)
    {
        var lines = Lines(source);
        int count = 0;
        for (int n = Math.Max(0, fromLine0); n < Math.Min(line0, lines.Length); n++) count += Count(lines[n], query, options);
        if (line0 < lines.Length) count += FindAll(lines[line0], query, options).Count(at => at < column);
        return count;
    }
}
