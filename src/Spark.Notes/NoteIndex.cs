using System.IO;
using Spark.Api;

namespace Spark.Notes;

/// <summary>
/// The set of files in a project tree, with Obsidian-style link resolution:
/// [[Title]] matches Title.md (or a file literally named Title), case-insensitively;
/// [[folder/Title]] matches by path suffix. Ties prefer the linking note's folder, then the shortest path.
/// </summary>
public sealed class NoteIndex
{
    public const string NoteExtension = ".md";
    /// <summary>Suffix of files encrypted on the client ("Plan.md.enc"); titles, types and links ignore it.</summary>
    public const string EncryptedSuffix = ".enc";

    private readonly List<SparkFile> _files;

    public string RootDirectory { get; }
    public IReadOnlyList<SparkFile> Files => _files;

    public NoteIndex(string rootDirectory, IEnumerable<SparkFile> files)
    {
        RootDirectory = rootDirectory;
        _files = files.ToList();
    }

    /// <summary>True when the name marks the file as encrypted on the client.</summary>
    public static bool HasEncryptedName(SparkFile f) => HasEncryptedName(f.Name);

    public static bool HasEncryptedName(string name) =>
        name.Length > EncryptedSuffix.Length && name.EndsWith(EncryptedSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The name without the encrypted suffix: "Plan.md.enc" -> "Plan.md".</summary>
    public static string LogicalName(SparkFile f) => LogicalName(f.Name);

    public static string LogicalName(string name) => HasEncryptedName(name) ? name[..^EncryptedSuffix.Length] : name;

    public static bool IsNote(SparkFile f) => LogicalName(f).EndsWith(NoteExtension, StringComparison.OrdinalIgnoreCase);

    public static bool IsText(SparkFile f) =>
        IsNote(f) || TextExtensions.Contains(Path.GetExtension(LogicalName(f)).ToLowerInvariant());

    private static readonly HashSet<string> TextExtensions = new()
    {
        ".txt", ".log", ".json", ".csv", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".conf", ".toml",
        ".cs", ".py", ".js", ".ts", ".kt", ".java", ".c", ".h", ".cpp", ".sh", ".ps1", ".sql", ".html", ".css",
    };

    /// <summary>The link title of a file: notes drop .md, other files keep their extension (both drop .enc).</summary>
    public static string TitleOf(SparkFile f) =>
        IsNote(f) ? LogicalName(f)[..^NoteExtension.Length] : LogicalName(f);

    public IEnumerable<SparkFile> Notes => _files.Where(IsNote);

    public SparkFile? Resolve(string target, string? fromDirectory = null)
    {
        var candidates = Candidates(target).ToList();
        if (candidates.Count <= 1) return candidates.FirstOrDefault();
        return candidates
            .OrderByDescending(f => fromDirectory != null && f.Directory == fromDirectory)
            .ThenBy(f => f.FullPath.Length)
            .ThenBy(f => f.CreatedAt)
            .First();
    }

    private IEnumerable<SparkFile> Candidates(string target)
    {
        target = target.Trim().Replace('\\', '/').TrimStart('/');
        if (target.Length == 0) yield break;
        var cmp = StringComparison.OrdinalIgnoreCase;

        if (target.Contains('/'))
        {
            var withExt = target.EndsWith(NoteExtension, cmp) ? target : target + NoteExtension;
            foreach (var f in _files)
            {
                var rel = LogicalName(RelativePath(f));
                if (EndsWithPath(rel, target, cmp) || (IsNote(f) && EndsWithPath(rel, withExt, cmp)))
                    yield return f;
            }
            yield break;
        }

        foreach (var f in _files)
        {
            if (string.Equals(LogicalName(f), target, cmp) ||
                (IsNote(f) && string.Equals(TitleOf(f), target, cmp)))
                yield return f;
        }
    }

    private static bool EndsWithPath(string path, string suffix, StringComparison cmp) =>
        path.Equals(suffix, cmp) || path.EndsWith("/" + suffix, cmp);

    /// <summary>Path of the file relative to the project root, e.g. "Design/Timer.md".</summary>
    public string RelativePath(SparkFile f)
    {
        var full = f.FullPath;
        if (RootDirectory != "/" && full.StartsWith(RootDirectory + "/", StringComparison.Ordinal))
            return full[(RootDirectory.Length + 1)..];
        return full.TrimStart('/');
    }

    /// <summary>
    /// The shortest link text that resolves unambiguously to <paramref name="f"/> from <paramref name="fromDirectory"/>.
    /// </summary>
    public string LinkTextFor(SparkFile f, string? fromDirectory = null)
    {
        var title = TitleOf(f);
        if (Resolve(title, fromDirectory)?.Id == f.Id) return title;
        var rel = LogicalName(RelativePath(f));
        if (IsNote(f)) rel = rel[..^NoteExtension.Length];
        var parts = rel.Split('/');
        for (int i = parts.Length - 2; i >= 0; i--)
        {
            var candidate = string.Join('/', parts[i..]);
            if (Resolve(candidate, fromDirectory)?.Id == f.Id) return candidate;
        }
        return rel;
    }

    /// <summary>Titles for autocomplete, best matches first.</summary>
    public IEnumerable<SparkFile> Search(string query, int max = 20)
    {
        query = query.Trim();
        return _files
            .Select(f => (File: f, Score: Score(TitleOf(f), LogicalName(RelativePath(f)), query)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => IsNote(x.File))
            .ThenBy(x => TitleOf(x.File), StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(x => x.File);
    }

    private static int Score(string title, string path, string q)
    {
        if (q.Length == 0) return 1;
        var cmp = StringComparison.OrdinalIgnoreCase;
        if (title.Equals(q, cmp)) return 100;
        if (title.StartsWith(q, cmp)) return 80;
        if (title.Contains(q, cmp)) return 60;
        if (path.Contains(q, cmp)) return 40;
        // Subsequence match ("tmv" -> "Timer view").
        int qi = 0;
        foreach (var c in title)
            if (qi < q.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(q[qi])) qi++;
        return qi == q.Length ? 20 : 0;
    }
}
