using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Spark.Api;
using SparkStudio.Infrastructure;

namespace SparkStudio.ViewModels;

public partial class ProjectNode : ObservableObject
{
    public ProjectNode(Project project) => Project = project;

    [ObservableProperty] private Project _project;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    public ProjectNode? Parent { get; set; }
    public ObservableCollection<ProjectNode> Children { get; } = new();

    public ProjectNode Root => Parent?.Root ?? this;

    /// <summary>"SPARK / SPARK Watch": the project's position in the tree.</summary>
    public string PathDisplay => Parent == null ? Project.Name : Parent.PathDisplay + " / " + Project.Name;
}

public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Settings _settings = Settings.Load();
    private readonly Dictionary<string, ProjectWorkspace> _workspaces = new();
    private readonly Debouncer _projectsRefresh = new(TimeSpan.FromMilliseconds(400));
    private NotificationClient? _live;

    public MainViewModel()
    {
        _serverUrl = _settings.ServerUrl;
    }

    /// <summary>The device ID this app acts as on the connected server (from its connection profile).</summary>
    public string DeviceId { get; private set; } = Settings.DefaultDeviceId;

    public SparkClient? Client { get; private set; }
    public Settings Settings => _settings;

    [ObservableProperty] private string _serverUrl;
    /// <summary>Details shown as the status tooltip: identity, permissions, warnings.</summary>
    [ObservableProperty] private string _statusDetails = "";
    [ObservableProperty] private bool _hasWarning;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isLive;
    [ObservableProperty] private string _status = "Not connected";
    [ObservableProperty] private ProjectNode? _selectedNode;
    [ObservableProperty] private ProjectWorkspace? _workspace;

    public ObservableCollection<ProjectNode> RootProjects { get; } = new();

    // ---------- Project switcher (top-bar dropdown) ----------

    [ObservableProperty] private bool _isProjectPickerOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilteringProjects))]
    private string _projectFilter = "";

    public bool IsFilteringProjects => !string.IsNullOrWhiteSpace(ProjectFilter);
    public ObservableCollection<ProjectNode> FilteredProjects { get; } = new();

    partial void OnProjectFilterChanged(string value)
    {
        FilteredProjects.Clear();
        var q = value.Trim();
        if (q.Length == 0) return;
        foreach (var n in AllNodes()
                     .Where(n => n.PathDisplay.Contains(q, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(n => n.Project.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                     .ThenBy(n => n.PathDisplay, StringComparer.OrdinalIgnoreCase))
            FilteredProjects.Add(n);
    }

    partial void OnIsProjectPickerOpenChanged(bool value)
    {
        if (!value) ProjectFilter = "";
    }

    [RelayCommand]
    private void ToggleProjectPicker() => IsProjectPickerOpen = !IsProjectPickerOpen;

    /// <summary>Switches to a project chosen in the dropdown and closes it.</summary>
    public void SelectProject(ProjectNode node)
    {
        foreach (var n in AllNodes()) n.IsSelected = n == node;
        for (var p = node.Parent; p != null; p = p.Parent) p.IsExpanded = true;
        SelectedNode = node;
        IsProjectPickerOpen = false;
    }

    [RelayCommand]
    private async Task EditConnectionAsync()
    {
        var result = Views.ConnectionDialog.Show(Ui.Owner, _settings, ServerUrl);
        if (result == null) return;
        _settings.SaveProfile(result.Url, result.Profile);
        ServerUrl = result.Url;
        await ConnectAsync();
    }

    [RelayCommand]
    public async Task ConnectAsync()
    {
        if (!Uri.TryCreate(ServerUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri))
        {
            Status = "Invalid server URL";
            return;
        }
        if (_live != null) await _live.DisposeAsync();
        _live = null;
        IsLive = false;
        Client?.Dispose();
        _workspaces.Clear();
        Workspace = null;
        RootProjects.Clear();
        SelectedNode = null;
        HasWarning = false;
        StatusDetails = "";

        var profile = _settings.ProfileFor(ServerUrl);
        var token = profile.GetToken();
        DeviceId = profile.DeviceId;
        if (profile.HasToken && token == null)
        {
            Fail("Saved token can't be decrypted",
                "The token was saved under a different Windows account. Re-enter it in connection settings (key button).");
            return;
        }

        Client = new SparkClient(uri, token);
        Status = "Connecting…";

        // 1. Who are we? /auth/verify needs no permission, so it works before anything else.
        var check = await ConnectionCheck.RunAsync(Client, DeviceId, token != null);
        if (!check.Ok)
        {
            Fail(check.Headline, check.Details);
            if (check.Headline == "This server requires a device token") _ = EditConnectionAsync();
            return;
        }
        var tokenMode = check.Auth?.Authenticated == true;
        bool Has(string permission) => !tokenMode || check.Auth!.Permissions.Contains(permission);

        // 2. Optional: the API contract (needs discovery.read in token mode).
        var contract = await Client.GetContractAsync();

        IsConnected = true;
        _settings.ServerUrl = ServerUrl.Trim();
        _settings.Save();

        var who = tokenMode ? $"as {DeviceId}" : "(open mode)";
        var version = contract != null ? $" · API {contract.ApiVersion}" : "";
        Status = $"Connected {who}{version}";
        var details = check.Details;
        if (token != null && ConnectionCheck.IsPlaintext(uri))
            details += "\n\nWarning: this connection is plain http, so the token is sent unencrypted.";
        HasWarning = check.MissingPermissions.Count > 0 || (token != null && ConnectionCheck.IsPlaintext(uri));
        if (HasWarning) Status += " · ⚠";
        StatusDetails = details;

        if (Has("projects.read")) await RefreshProjectsAsync();
        if (SelectedNode == null && RootProjects.Count > 0) IsProjectPickerOpen = true;

        // 3. Live updates over TCP (token in the hello; TLS when the contract says tcp+tls).
        if (!Has("notifications.read")) return;
        var options = new NotificationOptions(
            Host: contract?.NotificationHost ?? uri.Host,
            Port: contract?.NotificationPort ?? _settings.NotificationPort,
            DeviceId: DeviceId,
            Token: token,
            UseTls: contract?.NotificationsUseTls ?? false);
        if (token != null && !options.UseTls && uri.Scheme == Uri.UriSchemeHttps && contract == null)
        {
            // Without the contract we can't tell whether TCP is TLS; don't send the token in clear text.
            StatusDetails += "\n\nLive updates are off: grant discovery.read so the app can learn the notification transport.";
            return;
        }
        _live = new NotificationClient(options);
        _live.ConnectionChanged += up => Ui.Post(() => IsLive = up);
        _live.Notified += n => Ui.Post(() => OnNotification(n));
        _live.ResyncRequired += () => Ui.Post(OnResync);
        _live.Rejected += code => Ui.Post(() =>
        {
            HasWarning = true;
            if (!Status.EndsWith("⚠")) Status += " · ⚠";
            StatusDetails += code switch
            {
                "unauthorized" => "\n\nLive updates stopped: the token was rejected or revoked. Update it in connection settings.",
                "forbidden" => "\n\nLive updates stopped: this device lacks notifications.read or the device ID doesn't match the token.",
                _ => $"\n\nLive updates stopped: the server rejected the connection ({code}).",
            };
        });
        _live.Start();
    }

    private void Fail(string headline, string details)
    {
        IsConnected = false;
        HasWarning = true;
        Status = headline;
        StatusDetails = details;
    }

    [RelayCommand]
    public async Task RefreshProjectsAsync()
    {
        if (Client == null) return;
        await Ui.Try("Load projects", async () =>
        {
            var projects = await Client.ListProjectsAsync();
            var selectedId = SelectedNode?.Project.Id ?? _settings.LastProjectId;
            var expanded = AllNodes().Where(n => n.IsExpanded).Select(n => n.Project.Id).ToHashSet();

            var nodes = projects.ToDictionary(p => p.Id, p => new ProjectNode(p));
            RootProjects.Clear();
            foreach (var p in projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                var node = nodes[p.Id];
                node.IsExpanded = expanded.Contains(p.Id);
                if (p.ParentId != null && nodes.TryGetValue(p.ParentId, out var parent))
                {
                    node.Parent = parent;
                    parent.Children.Add(node);
                }
                else RootProjects.Add(node);
            }

            if (selectedId != null && nodes.TryGetValue(selectedId, out var sel))
            {
                for (var n = sel.Parent; n != null; n = n.Parent) n.IsExpanded = true;
                sel.IsSelected = true;
                SelectedNode = sel;
                if (_workspaces.TryGetValue(sel.Project.Id, out var ws)) ws.UpdateProject(sel.Project, sel.Root.Project);
            }
        });
    }

    private IEnumerable<ProjectNode> AllNodes()
    {
        var stack = new Stack<ProjectNode>(RootProjects);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            foreach (var c in n.Children) stack.Push(c);
        }
    }

    partial void OnSelectedNodeChanged(ProjectNode? value)
    {
        if (value == null || Client == null) return;
        if (Workspace?.Project.Id == value.Project.Id) return;
        _settings.LastProjectId = value.Project.Id;
        _settings.Save();
        if (!_workspaces.TryGetValue(value.Project.Id, out var ws))
        {
            ws = new ProjectWorkspace(Client, value.Project, value.Root.Project, _settings);
            _workspaces[value.Project.Id] = ws;
            _ = ws.LoadAsync();
        }
        else if (ws.IsStale) _ = ws.LoadAsync();
        Workspace = ws;
    }

    [RelayCommand]
    private async Task NewProjectAsync(ProjectNode? parent)
    {
        if (Client == null) return;
        IsProjectPickerOpen = false;
        var name = Ui.Prompt(parent == null ? "New project" : $"New subproject of {parent.Project.Name}", "Name");
        if (string.IsNullOrWhiteSpace(name)) return;
        await Ui.Try("Create project", async () =>
        {
            var body = new { name = name.Trim(), description = "", parent_id = parent?.Project.Id };
            var created = await Client.CreateProjectAsync(body);
            _settings.LastProjectId = created.Id;
            SelectedNode = null;
            await RefreshProjectsAsync();
        });
    }

    [RelayCommand]
    private async Task EditProjectDescriptionAsync()
    {
        if (Client == null || Workspace == null) return;
        var p = Workspace.Project;
        var text = Views.TextDialog.Show(Ui.Owner, $"{p.Name}: description", p.Description);
        if (text == null || text == p.Description) return;
        await Ui.Try("Update project", async () =>
        {
            var updated = await Client.UpdateProjectAsync(p.Id, new { description = text });
            Workspace.UpdateProject(updated, Workspace.RootProject.Id == updated.Id ? updated : Workspace.RootProject);
        });
    }

    // ---------- Live updates ----------

    private void OnNotification(SparkNotification n)
    {
        var fromSelf = n.SourceDevice == DeviceId;
        if (n.Type.StartsWith("project."))
        {
            _projectsRefresh.Run(RefreshProjectsAsync);
            foreach (var ws in _workspaces.Values) ws.Boards.NotifyChanged(n.Resource); // board changes update the owning project
        }
        else if (n.Type.StartsWith("board."))
        {
            foreach (var ws in _workspaces.Values)
                if (n.Resource?.Contains(ws.Project.Id) == true) ws.Boards.NotifyChanged(n.Resource);
        }
        else if (n.Type.StartsWith("file") || n.Type == "drives.updated")
        {
            foreach (var ws in _workspaces.Values) ws.Notes.NotifyFilesChanged(n.Type, n.Resource, fromSelf);
        }
    }

    private void OnResync()
    {
        _projectsRefresh.Run(RefreshProjectsAsync);
        foreach (var ws in _workspaces.Values)
        {
            ws.Notes.NotifyFilesChanged("resync", null, false);
            ws.Boards.NotifyChanged(null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_live != null) await _live.DisposeAsync();
        Client?.Dispose();
    }
}

/// <summary>Everything shown for one selected project: its notes and its boards.</summary>
public partial class ProjectWorkspace : ObservableObject
{
    public ProjectWorkspace(SparkClient client, Project project, Project root, Settings settings)
    {
        _project = project;
        _rootProject = root;
        Notes = new NotesViewModel(client, project, root, settings);
        Boards = new BoardsViewModel(client, project, settings);
    }

    [ObservableProperty] private Project _project;
    [ObservableProperty] private Project _rootProject;
    [ObservableProperty] private int _selectedTab;

    public NotesViewModel Notes { get; }
    public BoardsViewModel Boards { get; }
    public bool IsStale { get; set; }

    public void UpdateProject(Project p, Project root)
    {
        Project = p;
        RootProject = root;
    }

    public Task LoadAsync()
    {
        IsStale = false;
        return Task.WhenAll(Notes.LoadAsync(), Boards.LoadAsync());
    }
}
