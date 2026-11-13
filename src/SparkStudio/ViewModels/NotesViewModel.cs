using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Spark.Api;
using Spark.Notes;
using Spark.Notes.Crypto;
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
        Name = NoteIndex.IsNote(file) ? NoteIndex.TitleOf(file) : NoteIndex.LogicalName(file);
    }

    public SparkFile File { get; }
    /// <summary>Stored encrypted on the server (or will be, at its next save).</summary>
    public bool IsEncrypted { get; init; }
    /// <summary>False when offline and this file has no saved copy (shown dimmed, can't be opened).</summary>
    public bool IsAvailable { get; init; } = true;
    public bool IsNote => NoteIndex.IsNote(File);
    public bool IsText => NoteIndex.IsText(File);
    public string Glyph => IsNote ? "" : IsText ? "" : "";
}

/// <summary>A note heading a group of search results.</summary>
public sealed record SearchGroupItem(SparkFile File, string Title, string Folder, int LineCount, SearchHit FirstHit)
{
    public string CountText => LineCount == 1 ? "1 line" : $"{LineCount} lines";
}

/// <summary>One matching line, split around the (first) match so the view can highlight it.</summary>
public sealed record SearchHitItem(SearchHit Hit, string Before, string Match, string After)
{
    public int LineNumber => Hit.Line;
    public string ToolTip => $"Line {Hit.Line}: {Hit.LineText.Trim()}" + (Hit.MatchesOnLine > 1 ? $"  ({Hit.MatchesOnLine} matches)" : "");

    public static SearchHitItem From(SearchHit hit)
    {
        var line = hit.LineText;
        int start = Math.Max(0, hit.Column - 40);
        var before = (start > 0 ? "…" : "") + line[start..hit.Column];
        if (start == 0) before = before.TrimStart();
        var match = line.Substring(hit.Column, Math.Min(hit.Length, line.Length - hit.Column));
        var after = line[(hit.Column + match.Length)..];
        if (after.Length > 160) after = after[..160] + "…";
        return new SearchHitItem(hit, before, match, after);
    }
}

public sealed record LinkItem(string Title, string Detail, SparkFile? File, WikiLink? Link, bool Resolved);

public partial class OpenDocument : ObservableObject
{
    public OpenDocument(SparkFile file, NoteSession session)
    {
        _file = file;
        Session = session;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(IsMarkdown))]
    private SparkFile _file;

    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string _saveState = "";
    [ObservableProperty] private bool _remoteChanged;
    [ObservableProperty] private bool _remoteDeleted;

    /// <summary>Saves, ETag tracking and conflict detection for this note.</summary>
    public NoteSession Session { get; }
    public string ETag => Session.ETag;
    public string SavedText => Session.SavedText;
    /// <summary>Incremented on every edit; tells whether the text changed while a save was running.</summary>
    public int Version { get; set; }
    public string Title => NoteIndex.IsNote(File) ? NoteIndex.TitleOf(File) : NoteIndex.LogicalName(File);
    public bool IsMarkdown => NoteIndex.IsNote(File);

    // The session saves to this file; its folder decides whether saves are encrypted.
    partial void OnFileChanged(SparkFile value) => Session.File = value;
}

public partial class NotesViewModel : ObservableObject
{
    private readonly SparkClient _client;
    private readonly Settings _settings;

    /// <summary>App settings (shared with the view for layout preferences such as the links panel).</summary>
    public Settings Settings => _settings;
    private readonly LinkCache _links = new();
    private readonly Debouncer _autosave = new(TimeSpan.FromMilliseconds(1200));
    private readonly Debouncer _remoteRefresh = new(TimeSpan.FromMilliseconds(600));
    private bool _remoteCheckPending, _remoteCheckForeign;
    private readonly OfflineStore? _store;
    private CancellationTokenSource? _syncCts;

    public NotesViewModel(SparkClient client, Project project, Project root, Settings settings, OfflineStore? store = null)
    {
        _client = client;
        _settings = settings;
        _store = store;
        _isPinned = store?.IsPinned(project.Id) == true;
        Project = project;
        Root = root;
        _index = new NoteIndex(root.Directory, Array.Empty<SparkFile>());
        if (Keys != null) Keys.Changed += () => Ui.Post(OnKeysChanged);
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

    /// <summary>True while the server can't be reached: files come from saved copies and are read-only.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isOffline;

    public bool CanEdit => !IsOffline;

    /// <summary>True when this project's text files are kept on this PC for offline use.</summary>
    [ObservableProperty] private bool _isPinned;

    /// <summary>Short description of the offline copy, e.g. "Saved copy from 11/11 14:02".</summary>
    [ObservableProperty] private string? _offlineInfo;

    // ---------- Global search (sidebar "Search" tab) ----------

    /// <summary>0 = file tree, 1 = search results.</summary>
    [ObservableProperty] private int _sidebarTab;
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private bool _searchMatchCase;
    [ObservableProperty] private bool _searchWholeWord;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string? _searchSummary;

    /// <summary>Flat list for keyboard navigation: a <see cref="SearchGroupItem"/> per note, then its lines.</summary>
    public ObservableCollection<object> SearchItems { get; } = new();

    /// <summary>Asks the view to show a hit: (document, text that was searched, hit, query, options).</summary>
    public event Action<OpenDocument, string, SearchHit, string, SearchOptions>? RevealRequested;

    private readonly Debouncer _searchDebounce = new(TimeSpan.FromMilliseconds(300));
    private CancellationTokenSource? _searchCts;
    private Dictionary<string, string> _searchedText = new();
    private (string Query, SearchOptions Options) _lastSearch = ("", new SearchOptions());

    partial void OnSearchQueryChanged(string value) => _searchDebounce.Run(RunSearchAsync);
    partial void OnSearchMatchCaseChanged(bool value) => _searchDebounce.Run(RunSearchAsync);
    partial void OnSearchWholeWordChanged(bool value) => _searchDebounce.Run(RunSearchAsync);

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
            if (IsOffline)
            {
                LoadFromCache();
                return;
            }
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
        finally
        {
            IsLoading = false;
        }
        // An encrypted project asks for its passphrase once when it's first shown (it can be unlocked later too).
        if (IsLocked && !_unlockAsked)
        {
            _unlockAsked = true;
            Unlock();
        }
    }

    private async Task RefreshFilesAsync()
    {
        if (IsOffline) return;
        var (dirs, all) = await NoteOperations.ListTreeAsync(_client, Drive, Root.Directory);
        await LoadVaultAsync(all);
        var files = all.Where(f => !ProjectVault.IsVaultFile(f)).ToList();
        Index = new NoteIndex(Root.Directory, files);
        BuildTree(dirs, files);
        IndexChanged?.Invoke();
        UpdateLinkPanels();
        _ = ScanLinksAsync(files.Where(NoteOperations.IsLinkScannable).ToList());
        if (_store != null)
        {
            try { _store.SaveTree(Root.Directory, dirs, files); } catch (IOException) { }
            _ = SyncOfflineCopiesAsync(files);
        }
    }

    /// <summary>Shows the saved listing and text of this project (offline mode).</summary>
    private void LoadFromCache()
    {
        var tree = _store?.LoadTree(Root.Directory);
        if (tree == null)
        {
            Index = new NoteIndex(Root.Directory, Array.Empty<SparkFile>());
            Tree.Clear();
            LoadError = "You're offline and this project hasn't been saved on this PC yet. " +
                        "Next time you're connected, turn on \"Keep offline\" (cloud button above).";
            OfflineInfo = null;
            return;
        }
        if (_store!.LoadVault(Root.Directory) is { } json && ProjectVault.Parse(json) is { } vault) SetVault(vault);
        var treeFiles = tree.Files.Where(f => !ProjectVault.IsVaultFile(f)).ToList();
        Index = new NoteIndex(Root.Directory, treeFiles);
        BuildTree(tree.Directories, treeFiles);
        int available = 0, locked = 0;
        foreach (var f in treeFiles.Where(NoteIndex.IsText))
        {
            CachedContent? c;
            try { c = _store.LoadContent(f.Id); }
            catch (Exception e) when (e is VaultLockedException or System.Security.Cryptography.CryptographicException)
            {
                locked++;
                continue;
            }
            if (c == null) continue;
            available++;
            if (NoteOperations.IsLinkScannable(f)) _links.Update(f, c.Text);
        }
        OfflineInfo = $"Saved copy from {tree.SavedAt:g} · {available} of {treeFiles.Count(NoteIndex.IsText)} text files available" +
                      (locked > 0 ? $" ({locked} more once unlocked)" : "");
        IndexChanged?.Invoke();
        UpdateLinkPanels();
    }

    /// <summary>
    /// Downloads text files of pinned projects whose saved copy is missing or out of date. Runs in the
    /// background after each listing; a newer run cancels an older one.
    /// </summary>
    private async Task SyncOfflineCopiesAsync(IReadOnlyCollection<SparkFile> files)
    {
        if (_store == null) return;
        _syncCts?.Cancel();
        var cts = _syncCts = new CancellationTokenSource();
        var wanted = files.Where(f => NoteIndex.IsText(f) && f.SizeBytes <= MaxOfflineBytes && _store.IsWanted(f)).ToList();
        try
        {
            foreach (var f in wanted)
            {
                if (cts.IsCancellationRequested || IsOffline) return;
                if (_store.CachedETag(f.Id) == SparkClient.ETagFor(f)) continue;
                try
                {
                    var content = await _client.GetTextAsync(f.Id, cts.Token);
                    _store.SaveContent(f, content.Text, content.ETag);
                }
                catch (VaultLockedException) { /* copied once the project is unlocked */ }
            }
            if (IsPinned) OfflineInfo = $"Kept offline · {wanted.Count} text file{(wanted.Count == 1 ? "" : "s")} · synced {DateTime.Now:t}";
        }
        catch (Exception) { /* best effort: the next listing tries again */ }
    }

    /// <summary>Text files larger than this aren't kept offline.</summary>
    private const long MaxOfflineBytes = 8 * 1024 * 1024;

    private void SaveOfflineCopy(SparkFile file, string text, string etag)
    {
        try { _store?.SaveContent(file, text, etag); }
        catch (Exception e) when (e is IOException or VaultLockedException) { }
    }

    /// <summary>Turns "keep this project available offline" on or off.</summary>
    [RelayCommand]
    private async Task TogglePinAsync()
    {
        if (_store == null || Ui.BlockedOffline("Keep offline")) return;
        var pin = !IsPinned;
        _store.SetPinned(Project, pin);
        IsPinned = pin;
        if (pin)
        {
            OfflineInfo = "Saving for offline use…";
            await Ui.Try("Keep offline", async () =>
            {
                var (_, files) = await NoteOperations.ListTreeAsync(_client, Drive, Root.Directory);
                await SyncOfflineCopiesAsync(files);
            });
        }
        else
        {
            var keep = Current != null ? new[] { Current.File.Id } : Array.Empty<string>();
            _store.PruneUnpinned(Project.Directory, keep);
            OfflineInfo = null;
        }
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
        bool Available(SparkFile f) => !IsOffline || (NoteIndex.IsText(f) && _store?.HasContent(f.Id) == true);
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
            folder.Children.Add(new FileNode(f)
            {
                Parent = folder, IsSelected = f.Id == selectedId, IsAvailable = Available(f),
                IsEncrypted = NoteIndex.IsText(f) && Keys?.IsEncrypted(f) == true,
            });
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

    /// <summary>
    /// Searches every note and text file in this project (the open note's unsaved text included). Online, notes
    /// changed since the last scan are downloaded first; offline, the saved copies are searched.
    /// </summary>
    [RelayCommand]
    public async Task RunSearchAsync()
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var query = SearchQuery.Trim();
        var options = new SearchOptions(SearchMatchCase, SearchWholeWord);
        if (query.Length == 0)
        {
            SearchItems.Clear();
            SearchSummary = null;
            IsSearching = false;
            return;
        }

        IsSearching = true;
        SearchSummary = "Searching…";
        try
        {
            var files = Index.Files
                .Where(f => NoteOperations.IsLinkScannable(f) && OfflineStore.IsUnder(f.Directory, Project.Directory))
                .ToList();
            if (!IsOffline)
            {
                try { await _links.RefreshAsync(_client, files, cts.Token); }
                catch (Exception e) when (e is not OperationCanceledException) { /* search what we have */ }
            }
            if (cts.IsCancellationRequested) return;

            var openId = Current?.File.Id;
            var openText = openId != null && ReadEditor != null ? ReadEditor() : null;
            var texts = new Dictionary<string, string>();
            int unavailable = 0;
            foreach (var f in files)
            {
                var text = f.Id == openId ? openText : _links.TextOf(f.Id);
                if (text != null) texts[f.Id] = text;
                else unavailable++;
            }

            var hitsByFile = await Task.Run(() => files
                .Where(f => texts.ContainsKey(f.Id))
                .Select(f => (File: f, Hits: NoteSearch.Search(f, texts[f.Id], query, options)))
                .Where(x => x.Hits.Count > 0)
                .OrderBy(x => Index.RelativePath(x.File), StringComparer.OrdinalIgnoreCase)
                .ToList(), cts.Token);
            if (cts.IsCancellationRequested) return;

            _searchedText = texts;
            _lastSearch = (query, options);
            SearchItems.Clear();
            int lines = 0, matches = 0;
            foreach (var (file, hits) in hitsByFile)
            {
                var title = NoteIndex.IsNote(file) ? NoteIndex.TitleOf(file) : NoteIndex.LogicalName(file);
                var folder = Index.RelativePath(file);
                folder = folder.Contains('/') ? folder[..folder.LastIndexOf('/')] : "";
                SearchItems.Add(new SearchGroupItem(file, title, folder, hits.Count, hits[0]));
                foreach (var h in hits) SearchItems.Add(SearchHitItem.From(h));
                lines += hits.Count;
                matches += hits.Sum(h => h.MatchesOnLine);
            }
            SearchSummary = hitsByFile.Count == 0
                ? "No matches"
                : $"{matches} match{(matches == 1 ? "" : "es")} on {lines} line{(lines == 1 ? "" : "s")} in {hitsByFile.Count} note{(hitsByFile.Count == 1 ? "" : "s")}";
            if (IsOffline) SearchSummary += $" · saved copies{(unavailable > 0 ? $" ({unavailable} not on this PC)" : "")}";
            else if (unavailable > 0) SearchSummary += IsLocked ? $" · {unavailable} not searched (project locked)" : $" · {unavailable} couldn't be read";
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_searchCts == cts) IsSearching = false;
        }
    }

    /// <summary>Opens the note of a search result (a line, or a note header) and shows the match.</summary>
    public async Task OpenSearchResultAsync(object item)
    {
        var hit = item switch
        {
            SearchHitItem h => h.Hit,
            SearchGroupItem g => g.FirstHit,
            _ => null,
        };
        if (hit == null) return;
        var file = Index.Files.FirstOrDefault(f => f.Id == hit.File.Id) ?? hit.File;
        await OpenAsync(file, null);
        if (Current is not { } doc || doc.File.Id != hit.File.Id) return;
        if (!_searchedText.TryGetValue(hit.File.Id, out var source)) return;
        RevealRequested?.Invoke(doc, source, hit, _lastSearch.Query, _lastSearch.Options);
    }

    // ---------- Link resolution ----------

    public bool IsResolved(WikiLink link) => Index.Resolve(link.Target, Current?.File.Directory) != null;

    public SparkFile? Resolve(string target) => Index.Resolve(target, Current?.File.Directory);

    /// <summary>Opens the note a link points to, creating it first if it doesn't exist (like Obsidian).</summary>
    public async Task OpenLinkAsync(string target, string? heading)
    {
        var file = Resolve(target);
        if (file == null)
        {
            if (Ui.BlockedOffline("Create note")) return;
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
            OutgoingLinks.Add(new LinkItem(link.Target, file == null ? "Not created yet" : NoteIndex.LogicalName(Index.RelativePath(file)), file, link, file != null));
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
        if (!EnsureUnlocked(file)) return;
        if (IsOffline)
        {
            OpenFromCache(file, heading);
            return;
        }
        await Ui.Try("Open file", async () =>
        {
            var content = await _client.GetTextAsync(file.Id);
            var latest = Index.Files.FirstOrDefault(f => f.Id == file.Id) ?? file;
            SaveOfflineCopy(latest, content.Text, content.ETag);
            var session = new NoteSession(_client, latest, content.Text, content.ETag);
            session.Log += SyncLog.Write;
            session.SaveFinished += OnSaveFinished;
            Current = new OpenDocument(latest, session);
            foreach (var n in AllNodes().OfType<FileNode>()) n.IsSelected = n.File.Id == file.Id;
            DocumentLoaded?.Invoke(Current, content.Text, heading);
            UpdateLinkPanels();
        });
    }

    private void OpenFromCache(SparkFile file, string? heading)
    {
        CachedContent? cached;
        try { cached = _store?.LoadContent(file.Id); }
        catch (Exception e) when (e is VaultLockedException or System.Security.Cryptography.CryptographicException)
        {
            Ui.Error("Open file", e);
            return;
        }
        if (cached == null)
        {
            Ui.Error("Open file", new InvalidOperationException(
                $"\"{NoteIndex.LogicalName(file)}\" isn't saved on this PC, so it can't be opened while offline. " +
                "Turn on \"Keep offline\" for this project next time you're connected."));
            return;
        }
        var session = new NoteSession(_client, cached.File, cached.Text, cached.ETag);
        session.Log += SyncLog.Write;
        session.SaveFinished += OnSaveFinished;
        Current = new OpenDocument(cached.File, session) { SaveState = $"Offline · saved copy from {cached.SavedAt:g}" };
        foreach (var n in AllNodes().OfType<FileNode>()) n.IsSelected = n.File.Id == file.Id;
        DocumentLoaded?.Invoke(Current, cached.Text, heading);
        UpdateLinkPanels();
    }

    private async Task OpenExternallyAsync(SparkFile file)
    {
        if (Ui.BlockedOffline("Open file")) return;
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
        if (Current is not { } doc || IsOffline) return;
        doc.Version++;
        doc.IsDirty = true;
        doc.SaveState = "Edited";
        if (_settings.AutoSave && !doc.RemoteChanged) _autosave.Run(AutoSaveAsync);
    }

    private Task AutoSaveAsync() => IsOffline ? Task.CompletedTask : SaveAsync();

    /// <summary>Locks (offline) or unlocks editing. Unsaved text in the open note is kept either way.</summary>
    public void SetOffline(bool offline)
    {
        if (IsOffline == offline) return;
        IsOffline = offline;
        if (offline)
        {
            _autosave.Cancel();
            _syncCts?.Cancel();
            if (Current is { } doc)
                doc.SaveState = doc.IsDirty ? "Offline · unsaved changes kept until you reconnect" : "Offline · read-only";
        }
    }

    /// <summary>Back online after a reload: save text kept while offline, and pick up remote changes.</summary>
    public async Task ResumeOnlineAsync()
    {
        if (Current is { } doc && !doc.RemoteChanged)
        {
            doc.SaveState = doc.IsDirty ? "Edited" : "Saved";
            // Edits kept while offline: save now. If-Match makes the server the judge, so a change made
            // elsewhere in the meantime still shows up as a conflict instead of being overwritten.
            if (doc.IsDirty) await SaveAsync();
        }
        NotifyFilesChanged("resync", null, false);
    }

    public void RefreshOutgoingLinks() => UpdateLinkPanels();

    [RelayCommand]
    private Task SaveNowAsync() => Ui.BlockedOffline("Save") ? Task.CompletedTask : SaveAsync();

    /// <summary>
    /// Saves the open document if it has changes, waiting for any save already running. Returns false if the
    /// user's work is not safely saved. The text is read now, so switching notes afterwards can't lose it.
    /// </summary>
    public async Task<bool> SaveAsync()
    {
        _autosave.Cancel();
        if (Current is not { } doc) return true;
        if (!doc.IsDirty)
        {
            // Nothing new to send, but a save may still be running: wait so callers know the text is stored.
            await doc.Session.WaitForSavesAsync();
            if (!doc.IsDirty) return true;
            return Current == doc && await SaveAsync();
        }
        if (doc.RemoteChanged) return Ui.Confirm("Unsaved changes",
            "This note has a save conflict and your changes are not saved. Discard them?");
        if (IsOffline) return Ui.Confirm("Unsaved changes",
            "You're offline, so the changes to this note can't be saved yet. Stay on it and they'll be saved " +
            "when the connection returns.\n\nDiscard them instead?");
        if (ReadEditor == null) return true;

        var version = doc.Version;
        var text = ReadEditor();
        doc.SaveState = "Saving…";
        var result = await doc.Session.SaveAsync(text);
        return ApplySaveResult(doc, result, text, version);
    }

    private bool ApplySaveResult(OpenDocument doc, SaveResult result, string text, int version)
    {
        switch (result.Status)
        {
            case SaveStatus.Saved:
                if (result.File != null)
                {
                    doc.File = result.File;
                    ReplaceInIndex(result.File);
                }
                // A later queued save may have sent newer text; only that caller can mark the note clean.
                if (doc.Session.SavedText == text && doc.Version == version) doc.IsDirty = false;
                if (!doc.Session.IsSaving) doc.SaveState = doc.IsDirty ? "Edited" : "Saved";
                if (doc.Session.SavedText == text)
                {
                    _links.Update(doc.File, text);
                    SaveOfflineCopy(doc.File, text, doc.ETag);
                    if (doc == Current) UpdateLinkPanels();
                }
                return true;
            case SaveStatus.Conflict:
                _autosave.Cancel();
                doc.RemoteChanged = true;
                doc.SaveState = "Conflict: changed on server";
                return false;
            default:
                if (result.Error != null && Connectivity.IsNetworkError(result.Error))
                {
                    // Probably offline: keep the text, and say so rather than showing a socket error.
                    doc.SaveState = "Not saved yet · can't reach the server";
                    _ = Connectivity.ReportAsync().ContinueWith(t =>
                    {
                        if (IsOffline && doc.IsDirty) doc.SaveState = "Offline · unsaved changes kept until you reconnect";
                    }, TaskScheduler.FromCurrentSynchronizationContext());
                    return false;
                }
                doc.SaveState = "Save failed: " + result.Error?.Message;
                return false;
        }
    }

    private void ReplaceInIndex(SparkFile file)
    {
        Index = new NoteIndex(Index.RootDirectory, Index.Files.Select(f => f.Id == file.Id ? file : f));
    }

    /// <summary>
    /// Opens the side-by-side comparison of the editor and the server's latest version, and applies the choice.
    /// </summary>
    [RelayCommand]
    private async Task CompareWithServerAsync()
    {
        if (Ui.BlockedOffline("Compare with server")) return;
        if (Current is not { } doc || ReadEditor == null) return;
        await Ui.Try("Compare with server", async () =>
        {
            _autosave.Cancel();
            await doc.Session.WaitForSavesAsync(); // compare against a settled server revision
            while (true)
            {
                var server = await _client.GetTextAsync(doc.File.Id); // always show what the server has now
                var mine = ReadEditor();
                var choice = Views.ConflictDialog.Show(Ui.Owner, doc.Title, mine, server.Text);
                if (Current != doc) return;
                if (choice == Views.ConflictChoice.Cancel)
                {
                    if (doc.IsDirty && !doc.RemoteChanged && _settings.AutoSave) _autosave.Run(() => SaveAsync());
                    return;
                }
                if (choice == Views.ConflictChoice.UseServer)
                {
                    UseServerVersion(doc, server.Text, server.ETag);
                    return;
                }
                var version = doc.Version;
                var text = ReadEditor();
                doc.SaveState = "Saving…";
                var result = await doc.Session.OverwriteAsync(text, server.ETag);
                if (result.Status == SaveStatus.Conflict)
                {
                    Ui.Error("Keep mine", new InvalidOperationException(
                        "The server version changed again while you were comparing. Review the new version."));
                    continue;
                }
                if (ApplySaveResult(doc, result, text, version)) doc.RemoteChanged = false;
                return;
            }
        });
    }

    private void UseServerVersion(OpenDocument doc, string text, string etag)
    {
        SaveOfflineCopy(doc.File, text, etag);
        doc.Session.Adopt(text, etag);
        doc.IsDirty = false;
        doc.RemoteChanged = false;
        doc.SaveState = "Loaded server version";
        DocumentLoaded?.Invoke(doc, text, null);
        _links.Update(doc.File, text);
        UpdateLinkPanels();
    }

    [RelayCommand]
    private async Task ReloadFromServerAsync()
    {
        if (Ui.BlockedOffline("Use server version")) return;
        if (Current is not { } doc) return;
        if (doc.IsDirty && !Ui.Confirm("Use server version", "Discard your changes and load the server's version of this note?\n\n" +
                                                             "Use Compare to see the differences first.")) return;
        await Ui.Try("Reload", async () =>
        {
            await doc.Session.WaitForSavesAsync();
            var content = await _client.GetTextAsync(doc.File.Id);
            UseServerVersion(doc, content.Text, content.ETag);
        });
    }

    [RelayCommand]
    private async Task KeepMineAsync()
    {
        if (Ui.BlockedOffline("Keep mine")) return;
        if (Current is not { } doc || ReadEditor == null) return;
        if (!Ui.Confirm("Keep mine", "Replace the server's version of this note with yours?\n\n" +
                                     "Use Compare to see the differences first.")) return;
        await Ui.Try("Keep mine", async () =>
        {
            var latest = await _client.GetTextAsync(doc.File.Id);
            var version = doc.Version;
            var text = ReadEditor();
            var result = await doc.Session.OverwriteAsync(text, latest.ETag);
            if (ApplySaveResult(doc, result, text, version)) doc.RemoteChanged = false;
        });
    }

    /// <summary>File notifications from the server (including other devices' edits and echoes of our own saves).</summary>
    public void NotifyFilesChanged(string type, string? resource, bool fromSelf)
    {
        // Our own saves come back as notifications; they only need the tree refreshed. Anything else (or a
        // resync) also checks the open note. A burst of events is debounced into one check.
        if (!fromSelf) _remoteCheckForeign = true;
        _remoteRefresh.Run(CheckRemoteAsync);
    }

    private void OnSaveFinished()
    {
        // A listing taken while a save was in flight couldn't be judged; look again now it has landed.
        if (_remoteCheckPending && Current?.Session.IsSaving == false)
        {
            _remoteCheckPending = false;
            _remoteRefresh.Run(CheckRemoteAsync);
        }
    }

    private async Task CheckRemoteAsync()
    {
        if (IsOffline) return;
        try { await RefreshFilesAsync(); }
        catch (Exception) { return; }
        var foreign = _remoteCheckForeign;
        _remoteCheckForeign = false;
        if (Current is not { } doc) return;

        var latest = Index.Files.FirstOrDefault(f => f.Id == doc.File.Id);
        if (latest == null)
        {
            doc.RemoteDeleted = true;
            doc.SaveState = "Deleted on server";
            return;
        }
        if (!foreign && !_remoteCheckPending) return; // only our own echoes this round

        var listed = SparkClient.ETagFor(latest);
        switch (doc.Session.Classify(listed))
        {
            case RemoteState.InSync:
            case RemoteState.OwnEcho:
                return;
            case RemoteState.Deferred:
                _remoteCheckPending = true;
                _remoteCheckForeign = true;
                return;
        }

        SyncLog.Write($"foreign revision of {doc.File.Id}: listed {listed}, local {doc.ETag}, dirty={doc.IsDirty}");
        if (doc.RemoteChanged) return;
        if (doc.IsDirty)
        {
            // Don't guess: try to save. If-Match makes the server the judge (412 = genuine conflict).
            if (!await SaveAsync() || Current != doc) return;
            // Our text matched the last save, so nothing was sent; the foreign revision still needs loading.
            if (doc.IsDirty || doc.Session.Classify(listed) != RemoteState.Foreign) return;
        }

        var versionBefore = doc.Version;
        var etagBefore = doc.ETag;
        var content = await _client.GetTextAsync(doc.File.Id);
        // The user may have typed (or a save may have started) while that download was in flight.
        if (Current != doc || doc.Version != versionBefore || doc.ETag != etagBefore || doc.Session.IsSaving)
        {
            if (Current == doc && doc.IsDirty && !doc.RemoteChanged) await SaveAsync();
            return;
        }
        doc.File = latest;
        if (content.Text == doc.SavedText)
        {
            doc.Session.Adopt(content.Text, content.ETag); // same text, new revision: nothing to reload
            return;
        }
        doc.Session.Adopt(content.Text, content.ETag);
        SaveOfflineCopy(doc.File, content.Text, content.ETag);
        doc.SaveState = "Updated from server";
        DocumentLoaded?.Invoke(doc, content.Text, null);
        _links.Update(doc.File, content.Text);
        UpdateLinkPanels();
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
        if (Ui.BlockedOffline("New note")) return;
        var name = Ui.Prompt("New note", "Note title", "Untitled");
        if (string.IsNullOrWhiteSpace(name)) return;
        var file = await CreateNoteAsync(FolderOf(node), name.Trim());
        if (file != null) await OpenAsync(file, null);
    }

    private async Task<SparkFile?> CreateNoteAsync(string directory, string title)
    {
        var name = Path.HasExtension(title) && NoteIndex.IsText(new SparkFile { Name = title }) ? title : title + NoteIndex.NoteExtension;
        if (!ValidName(name)) return null;
        if (Index.Files.Any(f => f.Directory == directory && NoteIndex.LogicalName(f).Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            Ui.Error("New note", new InvalidOperationException($"A file named \"{name}\" already exists in that folder."));
            return null;
        }
        // Notes in an encrypted project are created encrypted (even when empty), and named to say so.
        if (Keys?.VaultFor(directory) is { } vault)
        {
            if (!Keys.IsUnlocked(vault.KeyId) && !Unlock()) return null;
            name += NoteIndex.EncryptedSuffix;
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
        if (Ui.BlockedOffline("New folder")) return;
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
        if (Ui.BlockedOffline("Delete")) return;
        switch (node)
        {
            case FileNode fn:
            {
                var backlinks = _links.BacklinksTo(fn.File, Index).Select(b => b.Source.Id).Distinct().Count();
                var warn = backlinks > 0 ? $"\n\n{backlinks} other note(s) link to it; those links will become unresolved." : "";
                if (!Ui.Confirm("Delete", $"Permanently delete \"{NoteIndex.LogicalName(fn.File)}\" from the server?{warn}")) return;
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
        if (Ui.BlockedOffline("Rename")) return;
        if (node is not FileNode fn) return;
        var file = fn.File;
        var isNote = NoteIndex.IsNote(file);
        var input = Ui.Prompt("Rename", isNote ? "Note title" : "File name", isNote ? NoteIndex.TitleOf(file) : NoteIndex.LogicalName(file));
        if (string.IsNullOrWhiteSpace(input)) return;
        var newName = isNote && !input.EndsWith(NoteIndex.NoteExtension, StringComparison.OrdinalIgnoreCase)
            ? input.Trim() + NoteIndex.NoteExtension : input.Trim();
        if (!ValidName(newName)) return;
        if (NoteIndex.HasEncryptedName(file)) newName += NoteIndex.EncryptedSuffix; // stays marked as encrypted
        if (newName == file.Name) return;
        await RenameAsync(file, newName);
    }

    [RelayCommand]
    private async Task ConvertToNoteAsync()
    {
        if (Ui.BlockedOffline("Convert to note")) return;
        if (Current is not { IsMarkdown: false } doc) return;
        var newName = Path.GetFileNameWithoutExtension(NoteIndex.LogicalName(doc.File)) + NoteIndex.NoteExtension;
        if (!Ui.Confirm("Convert to note", $"Convert \"{NoteIndex.LogicalName(doc.File)}\" into the rich note \"{newName}\"?\n\n" +
                                           "The text is kept as-is and becomes Markdown. Links to the old name are updated.")) return;
        if (!await SaveAsync()) return;
        if (NoteIndex.HasEncryptedName(doc.File)) newName += NoteIndex.EncryptedSuffix;
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

    // ---------- Encryption ----------
    //
    // A project is encrypted when its root folder holds a vault file (.spark-vault.json). The client's codec (the
    // Keyring) then encrypts every text written under that folder and decrypts what's read, so everything above it
    // (editor, links, search, conflicts) works on plain text while the server and the offline copies only hold
    // ciphertext.

    private Keyring? Keys => _client.Codec as Keyring;
    private VaultFile? _vault;
    private SparkFile? _vaultFile;
    private bool _unlockAsked;

    /// <summary>Largest text file encrypted: the armoured form must stay under the server's 2 MiB save limit.</summary>
    public const long MaxEncryptBytes = 1_400_000;

    [ObservableProperty] private bool _isEncrypted;
    [ObservableProperty] private bool _isLocked;
    /// <summary>Status line under the file panel's header (locked, files left to encrypt, progress); null hides it.</summary>
    [ObservableProperty] private string? _encryptionInfo;
    [ObservableProperty] private string _encryptionToolTip = "Encrypt this project's notes";
    [ObservableProperty] private bool _isEncrypting;

    /// <summary>Text files in an encrypted project not yet named/stored as encrypted.</summary>
    public int UnencryptedCount { get; private set; }

    public string ProjectName => Root.Name;

    /// <summary>Reads the root folder's vault file (when it changed) and registers it with the keyring.</summary>
    private async Task LoadVaultAsync(List<SparkFile> all)
    {
        // If the vault file disappears, the registration stays for this session: notes keep being encrypted.
        var vf = all.Where(f => f.Directory == Root.Directory && ProjectVault.IsVaultFile(f)).OrderBy(f => f.CreatedAt).FirstOrDefault();
        if (vf == null)
        {
            UpdateEncryptionState();
            return;
        }
        if (_vault != null && _vaultFile?.Id == vf.Id && _vaultFile.ContentRevision == vf.ContentRevision) return;
        var json = SparkClient.DecodeUtf8(await _client.GetBytesAsync(vf.Id));
        var vault = ProjectVault.Parse(json) ?? throw new InvalidOperationException(
            $"This project's {ProjectVault.FileName} is damaged or from a newer version, so its encrypted notes can't be opened.");
        _vaultFile = vf;
        SetVault(vault);
        try { _store?.SaveVault(Root.Directory, json); } catch (IOException) { }
    }

    partial void OnIndexChanged(NoteIndex value) => UpdateEncryptionState();

    private void SetVault(VaultFile vault)
    {
        _vault = vault;
        Keys?.Register(Root.Directory, vault);
        UpdateEncryptionState();
    }

    private void UpdateEncryptionState()
    {
        var keys = Keys;
        IsEncrypted = _vault != null;
        IsLocked = _vault != null && keys?.IsUnlocked(_vault.KeyId) != true;
        UnencryptedCount = _vault == null ? 0 : Index.Files.Count(f => NoteIndex.IsText(f) && !NoteIndex.HasEncryptedName(f));
        if (IsEncrypting) return;
        EncryptionInfo = _vault == null ? null
            : IsLocked ? "Encrypted and locked. Unlock it (lock button above) to read its notes."
            : UnencryptedCount > 0 ? $"{UnencryptedCount} text file{(UnencryptedCount == 1 ? " isn't" : "s aren't")} encrypted yet. Lock button → Encrypt remaining files."
            : null;
        EncryptionToolTip = _vault == null ? "Encrypt this project's notes"
            : IsLocked ? "Encrypted · locked. Click to unlock"
            : "Encrypted · unlocked" + (keys?.IsRemembered(_vault.KeyId) == true ? " and remembered on this PC" : " for this session");
    }

    private void OnKeysChanged()
    {
        var wasLocked = IsLocked;
        UpdateEncryptionState();
        if (IsLocked && !wasLocked) ForgetDecrypted();
        else if (!IsLocked && wasLocked) _ = LoadAsync(); // now readable: rescan links, sync offline copies
    }

    /// <summary>The project was locked: drop decrypted text kept in memory and close an open encrypted note.</summary>
    private void ForgetDecrypted()
    {
        if (Keys is not { } keys) return;
        var encrypted = Index.Files.Where(keys.IsEncrypted).Select(f => f.Id).ToHashSet();
        _links.Forget(encrypted.Contains);
        _searchedText = new();
        SearchItems.Clear();
        SearchSummary = null;
        if (Current is { } doc && encrypted.Contains(doc.File.Id))
        {
            // The active workspace saves before locking; a note left dirty elsewhere keeps its text until unlocked.
            if (doc.IsDirty) doc.SaveState = "Project locked · unlock it to save your changes";
            else CloseDocument();
        }
        UpdateLinkPanels();
    }

    /// <summary>True when <paramref name="file"/> can be read now, asking for the passphrase if its project is locked.</summary>
    private bool EnsureUnlocked(SparkFile file)
    {
        if (Keys is not { } keys || keys.VaultFor(file.Directory) is not { } vault) return true;
        return keys.IsUnlocked(vault.KeyId) || Unlock();
    }

    /// <summary>Asks for the passphrase (or recovery key). Returns true when the project is unlocked.</summary>
    public bool Unlock()
    {
        if (_vault is not { } vault || Keys is not { } keys) return false;
        if (keys.IsUnlocked(vault.KeyId)) return true;
        var result = Views.UnlockDialog.Show(Ui.Owner, Root.Name, vault);
        if (result == null) return false;
        keys.AddKey(vault.KeyId, result.Key, result.Remember);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(result.Key);
        try { _store?.ProtectUnder(Root.Directory); } catch (Exception e) when (e is IOException or VaultLockedException) { }
        return true;
    }

    /// <summary>Forgets the project key on this PC (and in memory) after saving the open note.</summary>
    public async Task LockAsync()
    {
        if (_vault is not { } vault || Keys is not { } keys) return;
        if (Current is { } doc && keys.IsEncrypted(doc.File))
        {
            if (!await SaveAsync()) return;
            CloseDocument();
        }
        keys.Lock(vault.KeyId);
    }

    /// <summary>
    /// Encrypts this project: creates its vault (asking for a passphrase and showing the recovery key), then
    /// encrypts its text files. On an encrypted project, encrypts the files that aren't yet.
    /// </summary>
    public async Task EncryptProjectAsync()
    {
        if (Keys is not { } keys || Ui.BlockedOffline("Encrypt project")) return;
        if (_vault == null)
        {
            var pass = Views.PassphraseDialog.Show(Ui.Owner, "Encrypt project",
                $"Notes in \"{Root.Name}\" and its subprojects will be encrypted on this PC before they're uploaded, so the server " +
                "only ever stores encrypted text. Every device that opens them needs this passphrase (or the recovery key shown next).\n\n" +
                "Not encrypted: file and folder names, sizes and dates, boards, and files that aren't text (such as images). " +
                "Copies the server made before now (backups, history) stay unencrypted. Other apps, such as the web UI, will see " +
                "encrypted text instead of the notes.", "Encrypt");
            if (pass == null) return;
            var created = await Ui.Try("Encrypt project", async () =>
            {
                var (_, all) = await NoteOperations.ListTreeAsync(_client, Drive, Root.Directory);
                if (all.Any(f => f.Directory == Root.Directory && ProjectVault.IsVaultFile(f)))
                    throw new InvalidOperationException("This project was just encrypted from another device. Refresh, then unlock it with that passphrase.");
                IsEncrypting = true;
                EncryptionInfo = "Creating the project key…";
                var (vault, key, code) = await Task.Run(() => ProjectVault.Create(pass));
                var json = ProjectVault.Serialize(vault);
                // The vault is uploaded as raw bytes: it must never pass through the codec.
                _vaultFile = await _client.UploadAsync(Root.DriveLetter, Root.Directory, ProjectVault.FileName, System.Text.Encoding.UTF8.GetBytes(json));
                try { _store?.SaveVault(Root.Directory, json); } catch (IOException) { }
                keys.AddKey(vault.KeyId, key, remember: true);
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
                SetVault(vault);
                Views.RecoveryKeyDialog.Show(Ui.Owner, Root.Name, code);
            });
            IsEncrypting = false;
            UpdateEncryptionState();
            if (!created) return;
        }
        else if (!Unlock()) return;
        await EncryptFilesAsync();
    }

    /// <summary>Encrypts every text file in the project that isn't yet: content first (ETag-guarded), then the name.</summary>
    private async Task EncryptFilesAsync()
    {
        var reopen = Current?.File;
        if (Current != null)
        {
            if (!await SaveAsync()) return;
            CloseDocument();
        }
        int done = 0, failed = 0, tooLarge = 0, nonText = 0;
        var failures = new List<string>();
        IsEncrypting = true;
        try
        {
            await Ui.Try("Encrypt project", async () =>
            {
                var (_, all) = await NoteOperations.ListTreeAsync(_client, Drive, Root.Directory);
                var files = all.Where(f => !ProjectVault.IsVaultFile(f)).ToList();
                nonText = files.Count(f => !NoteIndex.IsText(f));
                var todo = files.Where(NoteIndex.IsText).ToList();
                for (int i = 0; i < todo.Count; i++)
                {
                    var f = todo[i];
                    EncryptionInfo = $"Encrypting {i + 1} of {todo.Count}…";
                    if (f.SizeBytes > MaxEncryptBytes && !NoteIndex.HasEncryptedName(f))
                    {
                        tooLarge++;
                        failures.Add($"{NoteIndex.LogicalName(Index.RelativePath(f))} (too large)");
                        continue;
                    }
                    try
                    {
                        if (await EncryptFileAsync(f)) done++;
                    }
                    catch (Exception e) when (!Connectivity.IsNetworkError(e))
                    {
                        failed++;
                        failures.Add($"{NoteIndex.LogicalName(Index.RelativePath(f))} ({(e is SparkApiException { Status: HttpStatusCode.PreconditionFailed } ? "changed while encrypting" : e.Message)})");
                        SyncLog.Write($"encrypt {f.Id} failed: {e.Message}");
                    }
                }
                try { _store?.ProtectUnder(Root.Directory); } catch (IOException) { }
            });
        }
        finally
        {
            IsEncrypting = false;
        }
        await Ui.Try("Refresh", RefreshFilesAsync);
        UpdateEncryptionState();

        var lines = new List<string> { done == 0 ? "No files needed encrypting." : $"Encrypted {done} file{(done == 1 ? "" : "s")}." };
        if (failures.Count > 0)
            lines.Add($"\n{failures.Count} couldn't be encrypted (run Encrypt remaining files to retry):\n  " + string.Join("\n  ", failures.Take(12)) +
                      (failures.Count > 12 ? $"\n  … and {failures.Count - 12} more" : ""));
        if (nonText > 0) lines.Add($"\n{nonText} file{(nonText == 1 ? " isn't" : "s aren't")} text (images and other files) and stay unencrypted.");
        System.Windows.MessageBox.Show(Ui.Owner!, string.Join("\n", lines), "Encrypt project",
            System.Windows.MessageBoxButton.OK, failures.Count > 0 ? System.Windows.MessageBoxImage.Warning : System.Windows.MessageBoxImage.Information);

        if (reopen != null && Index.Files.FirstOrDefault(f => f.Id == reopen.Id
                || f.Directory == reopen.Directory && NoteIndex.LogicalName(f) == NoteIndex.LogicalName(reopen)) is { } again)
            await OpenAsync(again, null);
    }

    /// <summary>Encrypts one file's content (if stored as plain text) and marks its name. Returns true if anything changed.</summary>
    private async Task<bool> EncryptFileAsync(SparkFile f)
    {
        var content = await _client.GetTextAsync(f.Id);
        var file = f;
        var etag = content.ETag;
        bool changed = false;
        if (!Keys!.WasReadEncrypted(f.Id))
        {
            // The file is in the vault's folder, so this save is encrypted. If-Match: an edit made meanwhile wins (412).
            (file, etag) = await _client.PutTextAsync(f, content.Text, content.ETag);
            changed = true;
        }
        if (!NoteIndex.HasEncryptedName(file))
        {
            var renamed = await NoteOperations.RenameFileAsync(_client, file, file.Name + NoteIndex.EncryptedSuffix);
            if (renamed.Id != file.Id) _store?.RemoveContent(file.Id);
            file = renamed;
            etag = SparkClient.ETagFor(renamed);
            changed = true;
        }
        if (changed && _store != null && (_store.HasContent(f.Id) || _store.IsWanted(file))) SaveOfflineCopy(file, content.Text, etag);
        return changed;
    }

    /// <summary>Wraps the project key under a new passphrase. Notes aren't re-encrypted; the recovery key is unchanged.</summary>
    public async Task ChangePassphraseAsync()
    {
        if (_vault is not { } vault || _vaultFile is not { } vaultFile || Keys is not { } keys || Ui.BlockedOffline("Change passphrase")) return;
        if (!Unlock()) return;
        var pass = Views.PassphraseDialog.Show(Ui.Owner, "Change passphrase",
            $"Choose a new passphrase for \"{Root.Name}\". The notes don't need re-encrypting and the recovery key stays the same. " +
            "Devices that remember the project stay unlocked; others will need the new passphrase.\n\n" +
            "The project key itself doesn't change, so an old copy of the vault file (e.g. in a server backup) still opens with the old passphrase.",
            "Change");
        if (pass == null) return;
        await Ui.Try("Change passphrase", async () =>
        {
            var updated = await Task.Run(() => keys.ChangePassphrase(vault, pass));
            var current = await _client.GetTextAsync(vaultFile.Id);
            if (ProjectVault.Parse(current.Text)?.KeyId != vault.KeyId)
                throw new InvalidOperationException("The vault file changed on the server. Refresh and try again.");
            var json = ProjectVault.Serialize(updated);
            var (saved, _) = await _client.PutRawTextAsync(vaultFile.Id, json, current.ETag);
            _vaultFile = saved;
            SetVault(updated);
            try { _store?.SaveVault(Root.Directory, json); } catch (IOException) { }
            Ui.Info("Change passphrase", "The passphrase was changed.");
        });
    }
}
