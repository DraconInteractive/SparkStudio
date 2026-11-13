using Spark.Api;

namespace Spark.Notes;

public sealed record LinkRef(WikiLink Link, string Line);

/// <summary>Remembers the [[links]] each note contains, keyed by content revision, to compute backlinks cheaply.</summary>
public sealed class LinkCache
{
    private readonly Dictionary<string, (string Revision, List<LinkRef> Links)> _byFile = new();
    private readonly object _gate = new();

    public static List<LinkRef> Extract(string text)
    {
        var result = new List<LinkRef>();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            if (line.Contains("[["))
                foreach (var link in WikiLink.FindAll(line))
                    result.Add(new LinkRef(link, line.Trim()));
        return result;
    }

    public void Update(SparkFile f, string text)
    {
        var links = Extract(text);
        lock (_gate)
        {
            _byFile[f.Id] = (f.ContentRevision, links);
            _texts[f.Id] = text;
        }
    }

    private readonly Dictionary<string, string> _texts = new();

    /// <summary>Drops what's known about the matching files (e.g. notes of a project that was just locked).</summary>
    public void Forget(Func<string, bool> fileId)
    {
        lock (_gate)
        {
            foreach (var id in _byFile.Keys.Where(fileId).ToList()) _byFile.Remove(id);
            foreach (var id in _texts.Keys.Where(fileId).ToList()) _texts.Remove(id);
        }
    }

    /// <summary>The latest text seen for a file (kept for search), or null if it hasn't been read yet.</summary>
    public string? TextOf(string fileId)
    {
        lock (_gate) return _texts.TryGetValue(fileId, out var t) ? t : null;
    }

    public IReadOnlyList<LinkRef> LinksOf(SparkFile f)
    {
        lock (_gate) return _byFile.TryGetValue(f.Id, out var e) ? e.Links : Array.Empty<LinkRef>();
    }

    /// <summary>Downloads only the files whose content revision changed since the last scan.</summary>
    public async Task RefreshAsync(SparkClient client, IReadOnlyCollection<SparkFile> files, CancellationToken ct = default)
    {
        var live = files.Select(f => f.Id).ToHashSet();
        List<SparkFile> stale;
        lock (_gate)
        {
            foreach (var gone in _byFile.Keys.Where(k => !live.Contains(k)).ToList())
            {
                _byFile.Remove(gone);
                _texts.Remove(gone);
            }
            stale = files.Where(f => !_byFile.TryGetValue(f.Id, out var e) || e.Revision != f.ContentRevision).ToList();
        }
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(stale.Select(async f =>
        {
            await gate.WaitAsync(ct);
            try { Update(f, (await client.GetTextAsync(f.Id, ct)).Text); }
            catch (Exception) when (!ct.IsCancellationRequested) { /* best effort */ }
            finally { gate.Release(); }
        }));
    }

    /// <summary>Notes containing a link that resolves to <paramref name="target"/>.</summary>
    public IEnumerable<(SparkFile Source, LinkRef Ref)> BacklinksTo(SparkFile target, NoteIndex index)
    {
        foreach (var f in index.Files)
            foreach (var r in LinksOf(f))
                if (index.Resolve(r.Link.Target, f.Directory)?.Id == target.Id)
                    yield return (f, r);
    }
}
