using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Spark.Api;
using Spark.Notes;
using SparkStudio.Infrastructure;

namespace SparkStudio.ViewModels;

public abstract partial class FileTreeNode : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;
    public FolderNode? Parent { get; set; }
}

public sealed class FolderNode : FileTreeNode
{
    public FolderNode(string path, string name)
    {
        Path = path;
        Name = name;
    }

    public string Path { get; }
    public ObservableCollection<FileTreeNode> Children { get; } = new();
}

public sealed class FileNode : FileTreeNode
{
    public FileNode(SparkFile file)
    {
        File = file;
        Name = NoteIndex.IsNote(file) ? NoteIndex.TitleOf(file) : file.Name;
    }

    public SparkFile File { get; }
    public bool IsNote => NoteIndex.IsNote(File);
    public bool IsText => NoteIndex.IsText(File);
    public string Glyph => IsNote ? "" : IsText ? "" : "";
}

public sealed record LinkItem(string Title, string Detail, SparkFile? File, WikiLink? Link, bool Resolved);

public partial class OpenDocument : ObservableObject
{
    public OpenDocument(SparkFile file, string text, string etag)
    {
        _file = file;
        SavedText = text;
        ETag = etag;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(IsMarkdown))]
    private SparkFile _file;

    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string _saveState = "";
    [ObservableProperty] private bool _remoteChanged;
    [ObservableProperty] private bool _remoteDeleted;

    public string ETag { get; set; }
    public string SavedText { get; set; }
    public int Version { get; set; }
    public string Title => NoteIndex.IsNote(File) ? NoteIndex.TitleOf(File) : File.Name;
    public bool IsMarkdown => NoteIndex.IsNote(File);
}

public partial class NotesViewModel : ObservableObject
{
    private readonly SparkClient _client;
    private readonly Settings _settings;
    private readonly LinkCache _links = new();
    private readonly Debouncer _autosave = new(TimeSpan.FromMilliseconds(1200));
    private readonly Debouncer _remoteRefresh = new(TimeSpan.FromMilliseconds(600));
    private bool _saving, _saveAgain;

    public NotesViewModel(SparkClient client, Project project, Project root, Settings settings)
    {
        _client = client;
        _settings = settings;
        Project = project;
        Root = root;
        _index = new NoteIndex(root.Directory, Array.Empty<SparkFile>());
    }

    public Project Project { get; }
    public Project Root { get; }
    public string Drive => Project.DriveLetter;

    public ObservableCollection<FileTreeNode> Tree { get; } = new();
    public ObservableCollection<LinkItem> Backlinks { get; } = new();
    public ObservableCollection<LinkItem> OutgoingLinks { get; } = new();

    [ObservableProperty] private NoteIndex _index;
    [ObservableProperty] private OpenDocument? _current;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _loadError;

    /// <summary>Raised when a document's text should be (re)loaded into the editor. Heading is optional scroll target.</summary>
    public event Action<OpenDocument, string, string?>? DocumentLoaded;
    /// <summary>Raised when link resolution may have changed (files added/removed).</summary>
    public event Action? IndexChanged;

    /// <summary>Supplied by the view: returns the editor's current text for the open document.</summary>
    public Func<string>? ReadEditor { get; set; }

    // ---------- Loading ----------

    public async Task LoadAsync()
    {
        IsLoading = true;
        LoadError = null;
        try
        {
            if (!Project.DriveOnline)
            {
                LoadError = $"Drive {Project.DriveLetter} is offline, so this project's files are unavailable.";
                return;
            }
            await RefreshFilesAsync();
        }
        catch (Exception ex)
        {
            LoadError = "Could not load files: " + ex.Message;
        }
        finally { IsLoading = false; }
    }

    private async Task RefreshFilesAsync()
    {
        var (dirs, files) = await NoteOperations.ListTreeAsync(_client, Drive, Root.Directory);
        Index = new NoteIndex(Root.Directory, files);
        BuildTree(dirs, files);
        IndexChanged?.Invoke();
        UpdateLinkPanels();
        _ = ScanLinksAsync(files.Where(NoteOperations.IsLinkScannable).ToList());
    }

    private async Task ScanLinksAsync(IReadOnlyCollection<SparkFile> files)
    {
        try
        {
            await _links.RefreshAsync(_client, files);
            UpdateLinkPanels();
        }
        catch (Exception) { /* backlinks are best effort */ }
    }

    private void BuildTree(List<string> dirs, List<SparkFile> files)
    {
        var expanded = AllNodes().OfType<FolderNode>().Where(f => f.IsExpanded).Select(f => f.Path).ToHashSet();
        bool first = expanded.Count == 0;
        var selectedId = Current?.File.Id;

        var top = Project.Directory;
        var folders = new Dictionary<string, FolderNode>();
        FolderNode GetFolder(string path)
        {
            if (folders.TryGetValue(path, out var f)) return f;
            var name = path == top ? Project.Name : path[(path.LastIndexOf('/') + 1)..];
            f = new FolderNode(path, name) { IsExpanded = first ? path == top : expanded.Contains(path) };
            folders[path] = f;
            if (path != top)
            {
                var parent = GetFolder(path[..path.LastIndexOf('/')]);
                f.Parent = parent;
                parent.Children.Add(f);
            }
            return f;
        }

        var rootFolder = GetFolder(top);
        rootFolder.IsExpanded = true;
        foreach (var d in dirs.Where(d => d.StartsWith(top + "/", StringComparison.Ordinal)).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            GetFolder(d);
        foreach (var f in files.Where(f => f.Directory == top || f.Directory.StartsWith(top + "/", StringComparison.Ordinal))
                     .OrderByDescending(NoteIndex.IsNote).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            var folder = GetFolder(f.Directory);
            folder.Children.Add(new FileNode(f) { Parent = folder, IsSelected = f.Id == selectedId });
        }
        // Folders first, then files.
        foreach (var folder in folders.Values)
        {
            var sorted = folder.Children.OrderBy(c => c is FileNode).ThenBy(c => c is FileNode fn && !fn.IsNote)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
            folder.Children.Clear();
            foreach (var c in sorted) folder.Children.Add(c);
        }

        Tree.Clear();
        foreach (var c in rootFolder.Children) Tree.Add(c);
    }

    private IEnumerable<FileTreeNode> AllNodes()
    {
        var stack = new Stack<FileTreeNode>(Tree);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            if (n is FolderNode f) foreach (var c in f.Children) stack.Push(c);
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    // ---------- Link resolution ----------

    public bool IsResolved(WikiLink link) => Index.Resolve(link.Target, Current?.File.Directory) != null;

    public SparkFile? Resolve(string target) => Index.Resolve(target, Current?.File.Directory);

    /// <summary>Opens the note a link points to, creating it first if it doesn't exist (like Obsidian).</summary>
    public async Task OpenLinkAsync(string target, string? heading)
    {
        var file = Resolve(target);
        if (file == null)
        {
            if (!Ui.Confirm("Create note", $"\"{target}\" doesn't exist yet. Create it?")) return;
            var (dir, name) = SplitLinkTarget(target);
            file = await CreateNoteAsync(dir, name);
            if (file == null) return;
        }
        await OpenAsync(file, heading);
    }

    private (string Directory, string Name) SplitLinkTarget(string target)
    {
        target = target.Replace('\\', '/').Trim('/');
        var slash = target.LastIndexOf('/');
        if (slash < 0) return (Current?.File.Directory ?? Project.Directory, target);
        return (Root.Directory + "/" + target[..slash], target[(slash + 1)..]);
    }

    private void UpdateLinkPanels()
    {
        Backlinks.Clear();
        OutgoingLinks.Clear();
        if (Current is not { } doc) return;

        foreach (var g in _links.BacklinksTo(doc.File, Index).GroupBy(b => b.Source.Id))
        {
            var src = g.First().Source;
            Backlinks.Add(new LinkItem(NoteIndex.TitleOf(src), g.First().Ref.Line, src, null, true));
        }

        var outgoing = doc.IsMarkdown && ReadEditor != null ? LinkCache.Extract(ReadEditor()) : _links.LinksOf(doc.File).ToList();
        foreach (var g in outgoing.GroupBy(r => r.Link.Target, StringComparer.OrdinalIgnoreCase))
        {
            var link = g.First().Link;
            var file = Index.Resolve(link.Target, doc.File.Directory);
            OutgoingLinks.Add(new LinkItem(link.Target, file == null ? "Not created yet" : Index.RelativePath(file), file, link, file != null));
        }
    }

    [RelayCommand]
    private async Task OpenLinkItemAsync(LinkItem item)
    {
        if (item.File != null) await OpenAsync(item.File, item.Link?.Heading);
        else if (item.Link != null) await OpenLinkAsync(item.Link.Target, item.Link.Heading);
    }

    // ---------- Opening & saving ----------

    [RelayCommand]
    public async Task OpenNodeAsync(FileTreeNode? node)
    {
        if (node is FileNode fn) await OpenAsync(fn.File, null);
    }

    public async Task OpenAsync(SparkFile file, string? heading)
    {
        if (Current?.File.Id == file.Id)
        {
            if (heading != null) DocumentLoaded?.Invoke(Current, ReadEditor?.Invoke() ?? Current.SavedText, heading);
            return;
        }
        if (!NoteIndex.IsText(file))
        {
            await OpenExternallyAsync(file);
            return;
        }
        if (!await SaveAsync()) return;
        await Ui.Try("Open file", async () =>
        {
            var content = await _client.GetTextAsync(file.Id);
            var latest = Index.Files.FirstOrDefault(f => f.Id == file.Id) ?? file;
            Current = new OpenDocument(latest, content.Text, content.ETag);
            foreach (var n in AllNodes().OfType<FileNode>()) n.IsSelected = n.File.Id == file.Id;
            DocumentLoaded?.Invoke(Current, content.Text, heading);
            UpdateLinkPanels();
        });
    }

    private async Task OpenExternallyAsync(SparkFile file)
    {
        if (!Ui.Confirm("Open file", $"\"{file.Name}\" isn't a text file. Download it and open it with the default app?")) return;
        await Ui.Try("Open file", async () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "SparkStudio", file.Id);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, file.Name);
            await File.WriteAllBytesAsync(path, await _client.GetBytesAsync(file.Id));
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        });
    }

    /// <summary>Called by the view whenever the editor content changes.</summary>
    public void EditorChanged()
    {
        if (Current is not { } doc) return;
        doc.Version++;
        doc.IsDirty = true;
        doc.SaveState = "Edited";
        if (_settings.AutoSave && !doc.RemoteChanged) _autosave.Run(() => SaveAsync());
    }

    public void RefreshOutgoingLinks() => UpdateLinkPanels();

    [RelayCommand]
    private Task SaveNowAsync() => SaveAsync();

    /// <summary>Saves the open document if it has changes. Returns false if the user's work is not safely saved.</summary>
    public async Task<bool> SaveAsync()
    {
        _autosave.Cancel();
        if (Current is not { IsDirty: true } doc || ReadEditor == null) return true;
        if (doc.RemoteChanged) return Ui.Confirm("Unsaved changes",
            "This note has a save conflict and your changes are not saved. Discard them?");
        if (_saving)
        {
            _saveAgain = true;
            return true;
        }

        _saving = true;
        try
        {
            var version = doc.Version;
            var text = ReadEditor();
            if (text == doc.SavedText)
            {
                doc.IsDirty = false;
                doc.SaveState = "Saved";
                return true;
            }
            doc.SaveState = "Saving…";
            var (file, etag) = await _client.PutTextAsync(doc.File.Id, text, doc.ETag);
            doc.ETag = etag;
            doc.SavedText = text;
            doc.File = file;
            _links.Update(file, text);
            ReplaceInIndex(file);
            if (doc.Version == version) doc.IsDirty = false;
            doc.SaveState = "Saved";
            UpdateLinkPanels();
            return true;
        }
        catch (SparkApiException ex) when (ex.Status == HttpStatusCode.PreconditionFailed)
        {
            doc.RemoteChanged = true;
            doc.SaveState = "Conflict: changed on server";
            return false;
        }
        catch (Exception ex)
        {
            doc.SaveState = "Save failed: " + ex.Message;
            return false;
        }
        finally
        {
            _saving = false;
            if (_saveAgain)
            {
                _saveAgain = false;
                _ = SaveAsync();
            }
        }
    }

    private void ReplaceInIndex(SparkFile file)
    {
        Index = new NoteIndex(Index.RootDirectory, Index.Files.Select(f => f.Id == file.Id ? file : f));
    }

    [RelayCommand]
    private async Task ReloadFromServerAsync()
    {
        if (Current is not { } doc) return;
        if (doc.IsDirty && !Ui.Confirm("Reload", "Discard your unsaved changes and load the server version?")) return;
        await Ui.Try("Reload", async () =>
        {
            var content = await _client.GetTextAsync(doc.File.Id);
            doc.ETag = content.ETag;
            doc.SavedText = content.Text;
            doc.IsDirty = false;
            doc.RemoteChanged = false;
            doc.SaveState = "Reloaded";
            DocumentLoaded?.Invoke(doc, content.Text, null);
            UpdateLinkPanels();
        });
    }

    [RelayCommand]
    private async Task KeepMineAsync()
    {
        if (Current is not { } doc) return;
        if (!Ui.Confirm("Overwrite", "Replace the server version of this note with your version?")) return;
        await Ui.Try("Overwrite", async () =>
        {
            var latest = await _client.GetTextAsync(doc.File.Id);
            doc.ETag = latest.ETag;
            doc.RemoteChanged = false;
            doc.IsDirty = true;
            await SaveAsync();
        });
    }

    /// <summary>File notifications from the server (including other devices' edits).</summary>
    public void NotifyFilesChanged(string type, string? resource, bool fromSelf)
    {
        _remoteRefresh.Run(async () =>
        {
            try { await RefreshFilesAsync(); }
            catch (Exception) { return; }
            if (Current is not { } doc) return;
            var latest = Index.Files.FirstOrDefault(f => f.Id == doc.File.Id);
            if (latest == null)
            {
                doc.RemoteDeleted = true;
                doc.SaveState = "Deleted on server";
                return;
            }
            if (SparkClient.ETagFor(latest) == doc.ETag) return;
            if (!doc.IsDirty)
            {
                var content = await _client.GetTextAsync(doc.File.Id);
                doc.ETag = content.ETag;
                doc.SavedText = content.Text;
                doc.File = latest;
                doc.SaveState = "Updated from server";
                DocumentLoaded?.Invoke(doc, content.Text, null);
                UpdateLinkPanels();
            }
            else doc.RemoteChanged = true;
        });
    }

    // ---------- File operations ----------

    private string FolderOf(FileTreeNode? node) => node switch
    {
        FolderNode f => f.Path,
        FileNode fn => fn.File.Directory,
        _ => Project.Directory,
    };

    [RelayCommand]
    private async Task NewNoteAsync(FileTreeNode? node)
    {
        var name = Ui.Prompt("New note", "Note title", "Untitled");
        if (string.IsNullOrWhiteSpace(name)) return;
        var file = await CreateNoteAsync(FolderOf(node), name.Trim());
        if (file != null) await OpenAsync(file, null);
    }

    private async Task<SparkFile?> CreateNoteAsync(string directory, string title)
    {
        var name = Path.HasExtension(title) && NoteIndex.IsText(new SparkFile { Name = title }) ? title : title + NoteIndex.NoteExtension;
        if (!ValidName(name)) return null;
        if (Index.Files.Any(f => f.Directory == directory && f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            Ui.Error("New note", new InvalidOperationException($"A file named \"{name}\" already exists in that folder."));
            return null;
        }
        SparkFile? created = null;
        await Ui.Try("Create note", async () =>
        {
            created = await _client.UploadTextAsync(Drive, directory, name, "");
            await RefreshFilesAsync();
        });
        return created;
    }

    private static bool ValidName(string name)
    {
        if (name.IndexOfAny(new[] { '/', '\\' }) >= 0 || name.Any(char.IsControl) || name is "." or "..")
        {
            Ui.Error("Invalid name", new InvalidOperationException("Names can't contain / or \\ or control characters."));
            return false;
        }
        if (name.Contains("[[") || name.Contains("]]") || name.Contains('|') || name.Contains('#'))
        {
            Ui.Error("Invalid name", new InvalidOperationException("Note names can't contain [[ ]] | or #, since those are used by links."));
            return false;
        }
        return true;
    }

    [RelayCommand]
    private async Task NewFolderAsync(FileTreeNode? node)
    {
        var name = Ui.Prompt("New folder", "Folder name");
        if (string.IsNullOrWhiteSpace(name) || !ValidName(name.Trim())) return;
        var path = FolderOf(node) + "/" + name.Trim();
        await Ui.Try("Create folder", async () =>
        {
            await _client.CreateDirectoryAsync(Drive, path);
            await RefreshFilesAsync();
            foreach (var f in AllNodes().OfType<FolderNode>()) if (path.StartsWith(f.Path)) f.IsExpanded = true;
        });
    }

    [RelayCommand]
    private async Task DeleteNodeAsync(FileTreeNode? node)
    {
        switch (node)
        {
            case FileNode fn:
            {
                var backlinks = _links.BacklinksTo(fn.File, Index).Select(b => b.Source.Id).Distinct().Count();
                var warn = backlinks > 0 ? $"\n\n{backlinks} other note(s) link to it; those links will become unresolved." : "";
                if (!Ui.Confirm("Delete", $"Permanently delete \"{fn.File.Name}\" from the server?{warn}")) return;
                await Ui.Try("Delete", async () =>
                {
                    await _client.DeleteFileAsync(fn.File.Id);
                    if (Current?.File.Id == fn.File.Id) CloseDocument();
                    await RefreshFilesAsync();
                });
                break;
            }
            case FolderNode folder:
            {
                bool empty = folder.Children.Count == 0;
                if (!Ui.Confirm("Delete folder", empty
                        ? $"Delete the empty folder \"{folder.Name}\"?"
                        : $"Delete \"{folder.Name}\" and EVERYTHING inside it from the server?\n\nThis cannot be undone.")) return;
                await Ui.Try("Delete folder", async () =>
                {
                    await _client.DeleteDirectoryAsync(Drive, folder.Path, recursive: !empty);
                    if (Current != null && Current.File.Directory.StartsWith(folder.Path)) CloseDocument();
                    await RefreshFilesAsync();
                });
                break;
            }
        }
    }

    private void CloseDocument()
    {
        _autosave.Cancel();
        Current = null;
        UpdateLinkPanels();
    }

    [RelayCommand]
    private async Task RenameNodeAsync(FileTreeNode? node)
    {
        if (node is not FileNode fn) return;
        var file = fn.File;
        var isNote = NoteIndex.IsNote(file);
        var input = Ui.Prompt("Rename", isNote ? "Note title" : "File name", isNote ? NoteIndex.TitleOf(file) : file.Name);
        if (string.IsNullOrWhiteSpace(input)) return;
        var newName = isNote && !input.EndsWith(NoteIndex.NoteExtension, StringComparison.OrdinalIgnoreCase)
            ? input.Trim() + NoteIndex.NoteExtension : input.Trim();
        if (newName == file.Name || !ValidName(newName)) return;
        await RenameAsync(file, newName);
    }

    [RelayCommand]
    private async Task ConvertToNoteAsync()
    {
        if (Current is not { IsMarkdown: false } doc) return;
        var newName = Path.GetFileNameWithoutExtension(doc.File.Name) + NoteIndex.NoteExtension;
        if (!Ui.Confirm("Convert to note", $"Convert \"{doc.File.Name}\" into the rich note \"{newName}\"?\n\n" +
                                           "The text is kept as-is and becomes Markdown. Links to the old name are updated.")) return;
        if (!await SaveAsync()) return;
        await RenameAsync(doc.File, newName);
    }

    /// <summary>
    /// Renames a file (native PATCH rename on API 1.45+, otherwise copy + delete) and updates [[links]] that
    /// pointed at it. Also used by "Convert to rich note" (.txt -> .md).
    /// </summary>
    private async Task RenameAsync(SparkFile file, string newName)
    {
        var wasOpen = Current?.File.Id == file.Id;
        if (wasOpen && !await SaveAsync()) return;
        await Ui.Try("Rename", async () =>
        {
            var result = await NoteOperations.RenameAsync(_client, Drive, Root.Directory, file, newName, _links);
            // A native rename keeps the ID and ETag, so the open editor can stay as it is, unless the
            // editor type changes (.txt -> .md) or the note's own text was rewritten (a self-link).
            var keepOpen = wasOpen && Current is { } doc && result.File.Id == doc.File.Id
                           && NoteIndex.IsNote(result.File) == doc.IsMarkdown
                           && result.File.ContentRevision == doc.File.ContentRevision;
            if (keepOpen) Current!.File = result.File;
            else if (wasOpen) CloseDocument();
            await RefreshFilesAsync();
            if (result.NotesFailed > 0)
                Ui.Error("Rename", new InvalidOperationException(
                    $"Renamed, but {result.NotesFailed} note(s) linking to it could not be updated."));
            if (wasOpen && !keepOpen) await OpenAsync(Index.Files.FirstOrDefault(f => f.Id == result.File.Id) ?? result.File, null);
            UpdateLinkPanels();
        });
    }
}
