using Spark.Api;

namespace Spark.Notes;

public sealed record RenameResult(SparkFile File, int LinksUpdated, int NotesFailed);

public static class NoteOperations
{
    /// <summary>Text files small enough to scan for [[links]].</summary>
    public static bool IsLinkScannable(SparkFile f) =>
        f.SizeBytes <= 1024 * 1024 &&
        (NoteIndex.IsNote(f) || f.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));

    /// <summary>Lists every file under <paramref name="rootDirectory"/> on <paramref name="drive"/>, including subfolders.</summary>
    public static async Task<(List<string> Directories, List<SparkFile> Files)> ListTreeAsync(
        SparkClient client, string drive, string rootDirectory, CancellationToken ct = default)
    {
        var dirs = await client.ListDirectoriesAsync(drive, ct);
        var inRoot = dirs.Where(d => d == rootDirectory || d.StartsWith(rootDirectory + "/", StringComparison.Ordinal)).ToList();
        if (!inRoot.Contains(rootDirectory)) inRoot.Insert(0, rootDirectory);
        using var gate = new SemaphoreSlim(6);
        var lists = await Task.WhenAll(inRoot.Select(async d =>
        {
            await gate.WaitAsync(ct);
            try { return await client.ListFilesAsync(drive, d, ct); }
            finally { gate.Release(); }
        }));
        return (inRoot, lists.SelectMany(l => l).ToList());
    }

    /// <summary>
    /// Renames a file and rewrites [[links]] in the notes that pointed at it (each save guarded by its ETag).
    /// Uses the server's in-place rename when available (API 1.45+, the file keeps its ID); older servers
    /// get a copy under the new name followed by deletion of the original.
    /// </summary>
    public static async Task<RenameResult> RenameAsync(SparkClient client, string drive, string rootDirectory,
        SparkFile file, string newName, LinkCache links, CancellationToken ct = default)
    {
        var (_, before) = await ListTreeAsync(client, drive, rootDirectory, ct);
        if (before.Any(f => f.Directory == file.Directory && f.Id != file.Id && f.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"\"{newName}\" already exists in that folder.");

        var oldIndex = new NoteIndex(rootDirectory, before);
        await links.RefreshAsync(client, before.Where(IsLinkScannable).ToList(), ct);
        var referrers = links.BacklinksTo(file, oldIndex).Select(b => b.Source).DistinctBy(f => f.Id).ToList();

        SparkFile created;
        if (await client.SupportsFileRenameAsync(ct))
        {
            created = await client.RenameFileAsync(file.Id, newName, ct);
        }
        else
        {
            var bytes = await client.GetBytesAsync(file.Id, ct);
            created = await client.UploadAsync(file.DriveLetter, file.Directory, newName, bytes, ct);
            await client.DeleteFileAsync(file.Id, ct);
        }

        var after = before.Where(f => f.Id != file.Id).Append(created).ToList();
        var newIndex = new NoteIndex(rootDirectory, after);

        int updated = 0, failed = 0;
        foreach (var src in referrers)
        {
            var id = src.Id == file.Id ? created.Id : src.Id;
            try
            {
                var content = await client.GetTextAsync(id, ct);
                var dir = src.Directory;
                var newTarget = newIndex.LinkTextFor(created, dir);
                var text = WikiLink.RewriteTargets(content.Text, t => oldIndex.Resolve(t, dir)?.Id == file.Id, newTarget, out var n);
                if (n == 0) continue;
                var (saved, _) = await client.PutTextAsync(id, text, content.ETag, ct);
                links.Update(saved, text);
                if (saved.Id == created.Id) created = saved;
                updated += n;
            }
            catch (Exception) when (!ct.IsCancellationRequested) { failed++; }
        }
        return new RenameResult(created, updated, failed);
    }
}
