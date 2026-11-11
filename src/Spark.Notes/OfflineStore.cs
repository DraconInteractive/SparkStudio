using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Spark.Api;

namespace Spark.Notes;

/// <summary>A cached copy of a note's text, with the server revision it came from.</summary>
public sealed record CachedContent(SparkFile File, string Text, string ETag, DateTimeOffset SavedAt);

/// <summary>A cached directory listing of a root project (directories and file metadata).</summary>
public sealed record CachedTree(List<string> Directories, List<SparkFile> Files, DateTimeOffset SavedAt);

/// <summary>
/// On-disk copies of one SPARK server's projects, file listings and note text, used when the server can't be
/// reached. Layout under the server's folder:
/// <code>
///   projects.json            project list
///   pins.json                project ID -> directory kept offline
///   trees/{hash}.json        listing of one root project's directory
///   content/{fileId}.json    file metadata + ETag
///   content/{fileId}.txt     note text (UTF-8)
/// </code>
/// Every write goes to a temporary file first and is then moved into place, so a crash never leaves a
/// half-written copy behind.
/// </summary>
public sealed class OfflineStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    private readonly object _gate = new();

    public OfflineStore(string folder)
    {
        Folder = folder;
        Directory.CreateDirectory(Path.Combine(folder, "content"));
        Directory.CreateDirectory(Path.Combine(folder, "trees"));
    }

    public string Folder { get; }

    /// <summary>The store for one server, in its own subfolder of <paramref name="baseFolder"/>.</summary>
    public static OfflineStore ForServer(string baseFolder, string normalisedServerUrl)
    {
        var host = new Uri(normalisedServerUrl.TrimEnd('/') + "/").Host;
        var safeHost = new string(host.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_').ToArray());
        return new OfflineStore(Path.Combine(baseFolder, $"{safeHost}-{Hash(normalisedServerUrl)}"));
    }

    // ---------- Projects ----------

    private sealed record ProjectsFile(List<Project> Projects, DateTimeOffset SavedAt);

    public void SaveProjects(IEnumerable<Project> projects) =>
        Write("projects.json", new ProjectsFile(projects.ToList(), DateTimeOffset.Now));

    public (List<Project> Projects, DateTimeOffset SavedAt)? LoadProjects() =>
        Read<ProjectsFile>("projects.json") is { } f ? (f.Projects, f.SavedAt) : null;

    // ---------- Pins ("keep this project available offline") ----------

    private Dictionary<string, string> LoadPins() => Read<Dictionary<string, string>>("pins.json") ?? new();

    public bool IsPinned(string projectId) => LoadPins().ContainsKey(projectId);

    /// <summary>Directories whose text files are kept offline (each pinned project's directory).</summary>
    public IReadOnlyCollection<string> PinnedDirectories => LoadPins().Values.ToList();

    public void SetPinned(Project project, bool pinned)
    {
        lock (_gate)
        {
            var pins = LoadPins();
            if (pinned) pins[project.Id] = project.Directory;
            else pins.Remove(project.Id);
            Write("pins.json", pins);
        }
    }

    /// <summary>True when <paramref name="file"/> lies inside a pinned project's directory.</summary>
    public bool IsWanted(SparkFile file) => PinnedDirectories.Any(d => IsUnder(file.Directory, d));

    public static bool IsUnder(string directory, string root) =>
        directory == root || directory.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal);

    // ---------- Directory listings ----------

    public void SaveTree(string rootDirectory, List<string> directories, List<SparkFile> files)
    {
        Write(TreePath(rootDirectory), new CachedTree(directories, files, DateTimeOffset.Now));
        // Drop copies of files that no longer exist under this root.
        var live = files.Select(f => f.Id).ToHashSet();
        foreach (var cached in CachedFiles())
            if (IsUnder(cached.Directory, rootDirectory) && !live.Contains(cached.Id)) RemoveContent(cached.Id);
    }

    public CachedTree? LoadTree(string rootDirectory) => Read<CachedTree>(TreePath(rootDirectory));

    private static string TreePath(string rootDirectory) => Path.Combine("trees", Hash(rootDirectory) + ".json");

    // ---------- Note text ----------

    private sealed record ContentMeta(SparkFile File, string ETag, DateTimeOffset SavedAt);

    public void SaveContent(SparkFile file, string text, string etag)
    {
        lock (_gate)
        {
            WriteText(Path.Combine("content", file.Id + ".txt"), text);
            Write(Path.Combine("content", file.Id + ".json"), new ContentMeta(file, etag, DateTimeOffset.Now));
        }
    }

    public CachedContent? LoadContent(string fileId)
    {
        lock (_gate)
        {
            if (Read<ContentMeta>(Path.Combine("content", fileId + ".json")) is not { } meta) return null;
            var textPath = Path.Combine(Folder, "content", fileId + ".txt");
            if (!File.Exists(textPath)) return null;
            return new CachedContent(meta.File, File.ReadAllText(textPath, Encoding.UTF8), meta.ETag, meta.SavedAt);
        }
    }

    /// <summary>The ETag of the cached copy, or null if the file isn't cached.</summary>
    public string? CachedETag(string fileId) => Read<ContentMeta>(Path.Combine("content", fileId + ".json"))?.ETag;

    public bool HasContent(string fileId) => File.Exists(Path.Combine(Folder, "content", fileId + ".json"));

    public void RemoveContent(string fileId)
    {
        lock (_gate)
        {
            TryDelete(Path.Combine(Folder, "content", fileId + ".json"));
            TryDelete(Path.Combine(Folder, "content", fileId + ".txt"));
        }
    }

    /// <summary>Metadata of every cached file.</summary>
    public List<SparkFile> CachedFiles()
    {
        var list = new List<SparkFile>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(Folder, "content"), "*.json"))
            if (Read<ContentMeta>(Path.GetRelativePath(Folder, path)) is { } meta) list.Add(meta.File);
        return list;
    }

    /// <summary>Removes cached text under <paramref name="directory"/> that no pin covers (after unpinning).</summary>
    public int PruneUnpinned(string directory, IEnumerable<string> keepFileIds)
    {
        var keep = keepFileIds.ToHashSet();
        int removed = 0;
        foreach (var f in CachedFiles())
            if (IsUnder(f.Directory, directory) && !IsWanted(f) && !keep.Contains(f.Id))
            {
                RemoveContent(f.Id);
                removed++;
            }
        return removed;
    }

    // ---------- Plumbing ----------

    private T? Read<T>(string relative) where T : class
    {
        var path = Path.Combine(Folder, relative);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), Json) : null;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return null; // a damaged cache entry is treated as missing
        }
    }

    private void Write<T>(string relative, T value) => WriteText(relative, JsonSerializer.Serialize(value, Json));

    private void WriteText(string relative, string text)
    {
        var path = Path.Combine(Folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }

    private static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();
}
