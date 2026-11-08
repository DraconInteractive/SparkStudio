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
        _token = _settings.Token;
    }

    public static string DeviceId { get; } =
        "spark-studio-" + Regex.Replace(Environment.MachineName.ToLowerInvariant(), "[^a-z0-9_-]", "-")[..Math.Min(40, Environment.MachineName.Length)];

    public SparkClient? Client { get; private set; }
    public Settings Settings => _settings;

    [ObservableProperty] private string _serverUrl;
    [ObservableProperty] private string? _token;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isLive;
    [ObservableProperty] private string _status = "Not connected";
    [ObservableProperty] private ProjectNode? _selectedNode;
    [ObservableProperty] private ProjectWorkspace? _workspace;

    public ObservableCollection<ProjectNode> RootProjects { get; } = new();

    [RelayCommand]
    public async Task ConnectAsync()
    {
        if (!Uri.TryCreate(ServerUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri))
        {
            Status = "Invalid server URL";
            return;
        }
        if (_live != null) await _live.DisposeAsync();
        Client?.Dispose();
        _workspaces.Clear();
        Workspace = null;

        Client = new SparkClient(uri, Token);
        Status = "Connecting…";
        try
        {
            var version = await Client.GetApiVersionAsync();
            IsConnected = true;
            Status = $"Connected · API {version}";
            _settings.ServerUrl = ServerUrl.Trim();
            _settings.Token = string.IsNullOrWhiteSpace(Token) ? null : Token;
            _settings.Save();
        }
        catch (Exception ex)
        {
            IsConnected = false;
            Status = "Connection failed: " + ex.Message;
            return;
        }

        await RefreshProjectsAsync();

        int port = _settings.NotificationPort;
        try { port = await Client.GetNotificationPortAsync() ?? port; } catch (Exception) { }
        _live = new NotificationClient(uri.Host, port, DeviceId);
        _live.ConnectionChanged += up => Ui.Post(() => IsLive = up);
        _live.Notified += n => Ui.Post(() => OnNotification(n));
        _live.ResyncRequired += () => Ui.Post(OnResync);
        _live.Start();
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
        Boards = new BoardsViewModel(client, project);
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
