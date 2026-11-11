using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Spark.Api;
using SparkStudio.Infrastructure;
using SparkStudio.Views;

namespace SparkStudio.ViewModels;

public enum TaskSortField { Created, Name, Severity }

/// <summary>One entry in a card's "Severity" context submenu.</summary>
public sealed record SeverityOption(TaskCard Card, string Level, bool IsCurrent);

public partial class TaskCard : ObservableObject
{
    private static long _nextSeq;

    public TaskCard(BoardTask task, ICommand? setSeverity = null)
    {
        _task = task;
        SetSeverityCommand = setSeverity;
    }

    /// <summary>Arrival order (server order, then creation in this session); breaks ties between equal timestamps.</summary>
    public long Seq { get; } = Interlocked.Increment(ref _nextSeq);

    /// <summary>The board's set-severity command, so context-menu items (in their own popup) can reach it.</summary>
    public ICommand? SetSeverityCommand { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview), nameof(HasPreview), nameof(Severity), nameof(ShowSeverity), nameof(SeverityOptions))]
    private BoardTask _task;

    /// <summary>The task's severity, or null when the server doesn't support severities.</summary>
    public string? Severity => Task.Severity;

    /// <summary>Normal is the default, so only other levels get a badge (keeps boards uncluttered).</summary>
    public bool ShowSeverity => Task.Severity != null && Task.Severity != TaskSeverity.Normal;

    public IEnumerable<SeverityOption> SeverityOptions =>
        TaskSeverity.Levels.Reverse().Select(l => new SeverityOption(this, l, l == (Task.Severity ?? TaskSeverity.Normal)));

    public string Preview
    {
        get
        {
            var lines = Task.Description.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var text = string.Join("\n", lines.Take(3));
            return lines.Length > 3 ? text + " …" : text;
        }
    }

    public bool HasPreview => Task.Description.Trim().Length > 0;
}

public partial class ColumnVm : ObservableObject
{
    public ColumnVm(BoardColumn column, ICommand? setSeverity = null)
    {
        _column = column;
        foreach (var t in column.Tasks) Tasks.Add(new TaskCard(t, setSeverity));
    }

    [ObservableProperty] private BoardColumn _column;
    [ObservableProperty] private bool _isAdding;
    [ObservableProperty] private string _newTaskName = "";

    public ObservableCollection<TaskCard> Tasks { get; } = new();

    /// <summary>Reorders the cards in place (Move keeps the ItemsControl's containers and scroll position).</summary>
    public void Sort(IComparer<TaskCard> comparer)
    {
        var sorted = Tasks.OrderBy(t => t, comparer).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            var from = Tasks.IndexOf(sorted[i]);
            if (from != i) Tasks.Move(from, i);
        }
    }
}

/// <summary>Orders cards by the chosen field; ties fall back to oldest-first so the order is stable.</summary>
public sealed class TaskCardComparer(TaskSortField field, bool descending) : IComparer<TaskCard>
{
    public int Compare(TaskCard? a, TaskCard? b)
    {
        if (a == null || b == null) return a == null ? (b == null ? 0 : -1) : 1;
        int byCreated = a.Task.CreatedAt.CompareTo(b.Task.CreatedAt);
        if (byCreated == 0) byCreated = a.Seq.CompareTo(b.Seq);
        int primary = field switch
        {
            TaskSortField.Name => string.Compare(a.Task.Name, b.Task.Name, StringComparison.CurrentCultureIgnoreCase),
            TaskSortField.Severity => TaskSeverity.Rank(a.Severity).CompareTo(TaskSeverity.Rank(b.Severity)),
            _ => byCreated,
        };
        if (descending) primary = -primary;
        return primary != 0 ? primary : byCreated;
    }
}

public partial class BoardsViewModel : ObservableObject
{
    private readonly SparkClient _client;
    private readonly Project _project;
    private readonly Settings? _settings;
    private readonly Debouncer _refresh = new(TimeSpan.FromMilliseconds(300));

    public BoardsViewModel(SparkClient client, Project project, Settings? settings = null)
    {
        _client = client;
        _project = project;
        _settings = settings;
        if (settings != null)
        {
            _sortField = Enum.TryParse<TaskSortField>(settings.BoardSort, out var f) ? f : TaskSortField.Created;
            _sortDescending = settings.BoardSortDescending;
        }
    }

    // ---------- Sorting ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortLabel), nameof(IsSortCreated), nameof(IsSortName), nameof(IsSortSeverity))]
    private TaskSortField _sortField;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortLabel), nameof(IsSortAscending))]
    private bool _sortDescending;

    public bool IsSortAscending => !SortDescending;

    /// <summary>Severity sorting falls back to creation order on servers without severities.</summary>
    private TaskSortField EffectiveSortField =>
        SortField == TaskSortField.Severity && !SupportsSeverity ? TaskSortField.Created : SortField;

    public bool IsSortCreated => EffectiveSortField == TaskSortField.Created;
    public bool IsSortName => EffectiveSortField == TaskSortField.Name;
    public bool IsSortSeverity => EffectiveSortField == TaskSortField.Severity;

    public string SortLabel => (EffectiveSortField switch
    {
        TaskSortField.Name => "Name",
        TaskSortField.Severity => "Severity",
        _ => "Created",
    }) + (SortDescending ? " ↓" : " ↑");

    public IComparer<TaskCard> Comparer => new TaskCardComparer(EffectiveSortField, SortDescending);

    [RelayCommand]
    private void SortBy(string field)
    {
        if (Enum.TryParse<TaskSortField>(field, out var f)) SortField = f;
    }

    [RelayCommand]
    private void SortDirection(string descending) => SortDescending = descending == "True";

    partial void OnSortFieldChanged(TaskSortField value) => SortChanged();
    partial void OnSortDescendingChanged(bool value) => SortChanged();

    private void SortChanged()
    {
        if (_settings != null)
        {
            _settings.BoardSort = SortField.ToString();
            _settings.BoardSortDescending = SortDescending;
            _settings.Save();
        }
        ApplySort();
    }

    private void ApplySort()
    {
        var comparer = Comparer;
        foreach (var c in Columns) c.Sort(comparer);
    }

    private void ResortColumnOf(TaskCard card) =>
        Columns.FirstOrDefault(c => c.Tasks.Contains(card))?.Sort(Comparer);

    public ObservableCollection<BoardSummary> Boards { get; } = new();
    public ObservableCollection<ColumnVm> Columns { get; } = new();

    [ObservableProperty] private BoardSummary? _selectedBoard;
    [ObservableProperty] private Board? _board;
    [ObservableProperty] private bool _isBusy;

    /// <summary>True when the server supports task severity (API 1.48+).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortLabel), nameof(IsSortCreated), nameof(IsSortName), nameof(IsSortSeverity))]
    private bool _supportsSeverity;

    public bool HasBoards => Boards.Count > 0;

    public async Task LoadAsync()
    {
        await Ui.Try("Load boards", async () =>
        {
            var keep = SelectedBoard?.Id;
            var boards = await _client.ListBoardsAsync(_project.Id);
            Boards.Clear();
            foreach (var b in boards) Boards.Add(b);
            OnPropertyChanged(nameof(HasBoards));
            var pick = boards.FirstOrDefault(b => b.Id == keep) ?? boards.FirstOrDefault();
            if (pick?.Id == SelectedBoard?.Id && pick != null) await LoadBoardAsync(pick.Id);
            else SelectedBoard = pick;
            if (pick == null)
            {
                Board = null;
                Columns.Clear();
            }
        });
    }

    partial void OnSelectedBoardChanged(BoardSummary? value)
    {
        if (value != null) _ = Ui.Try("Load board", () => LoadBoardAsync(value.Id));
    }

    private async Task LoadBoardAsync(string boardId)
    {
        IsBusy = true;
        try
        {
            var board = await _client.GetBoardAsync(_project.Id, boardId);
            if (board == null || SelectedBoard?.Id != boardId) return;
            Board = board;
            SupportsSeverity = (await _client.GetContractAsync())?.SupportsTaskSeverity == true
                               || board.Columns.Any(c => c.Tasks.Any(t => t.Severity != null));
            // Rebuild in place so scroll position survives refreshes.
            var byId = Columns.ToDictionary(c => c.Column.Id);
            Columns.Clear();
            foreach (var c in board.Columns)
            {
                var vm = new ColumnVm(c, SetSeverityCommand);
                if (byId.TryGetValue(c.Id, out var old) && old.IsAdding)
                {
                    vm.IsAdding = true;
                    vm.NewTaskName = old.NewTaskName;
                }
                vm.Sort(Comparer);
                Columns.Add(vm);
            }
        }
        finally { IsBusy = false; }
    }

    private Task ReloadBoardAsync() => SelectedBoard == null ? Task.CompletedTask : LoadBoardAsync(SelectedBoard.Id);

    /// <summary>Called for board.* / project.* notifications; refreshes after a short quiet period.</summary>
    public void NotifyChanged(string? resource)
    {
        if (resource != null && !resource.Contains(_project.Id)) return;
        _refresh.Run(async () =>
        {
            if (resource == null || resource.TrimEnd('/').EndsWith("/boards") || !Boards.Any(b => resource.Contains(b.Id)))
                await LoadAsync();
            else
                await Ui.Try("Refresh board", ReloadBoardAsync);
        });
    }

    // ---------- Boards ----------

    [RelayCommand]
    private async Task NewBoardAsync()
    {
        var name = Ui.Prompt("New board", "Board name", Boards.Count == 0 ? "Development" : "");
        if (string.IsNullOrWhiteSpace(name)) return;
        await Ui.Try("Create board", async () =>
        {
            var board = await _client.CreateBoardAsync(_project.Id, name.Trim());
            if (board == null) return;
            // Give new boards a sensible default layout.
            if (Ui.Confirm("New board", "Add the default columns (To do, In progress, Done)?"))
                foreach (var col in new[] { "To do", "In progress", "Done" })
                    await _client.CreateColumnAsync(_project.Id, board.Id, col);
            var summary = new BoardSummary { Id = board.Id, Name = board.Name, ProjectId = _project.Id };
            Boards.Add(summary);
            OnPropertyChanged(nameof(HasBoards));
            SelectedBoard = summary;
        });
    }

    [RelayCommand]
    private async Task RenameBoardAsync()
    {
        if (SelectedBoard is not { } b) return;
        var name = Ui.Prompt("Rename board", "Board name", b.Name);
        if (string.IsNullOrWhiteSpace(name) || name == b.Name) return;
        await Ui.Try("Rename board", async () =>
        {
            await _client.RenameBoardAsync(_project.Id, b.Id, name.Trim());
            await LoadAsync();
        });
    }

    [RelayCommand]
    private async Task DeleteBoardAsync()
    {
        if (SelectedBoard is not { } b) return;
        if (!Ui.Confirm("Delete board", $"Delete the board \"{b.Name}\" and ALL of its columns and tasks?\n\nThis cannot be undone.")) return;
        await Ui.Try("Delete board", async () =>
        {
            await _client.DeleteBoardAsync(_project.Id, b.Id);
            SelectedBoard = null;
            await LoadAsync();
        });
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    // ---------- Columns ----------

    [RelayCommand]
    private async Task AddColumnAsync()
    {
        if (Board == null) return;
        var name = Ui.Prompt("New column", "Column name");
        if (string.IsNullOrWhiteSpace(name)) return;
        await Ui.Try("Add column", async () =>
        {
            await _client.CreateColumnAsync(_project.Id, Board.Id, name.Trim());
            await ReloadBoardAsync();
        });
    }

    [RelayCommand]
    private async Task RenameColumnAsync(ColumnVm col)
    {
        if (Board == null) return;
        var name = Ui.Prompt("Rename column", "Column name", col.Column.Name);
        if (string.IsNullOrWhiteSpace(name) || name == col.Column.Name) return;
        await Ui.Try("Rename column", async () =>
        {
            await _client.RenameColumnAsync(_project.Id, Board.Id, col.Column.Id, name.Trim());
            await ReloadBoardAsync();
        });
    }

    [RelayCommand]
    private async Task DeleteColumnAsync(ColumnVm col)
    {
        if (Board == null) return;
        if (col.Tasks.Count > 0)
        {
            Ui.Error("Delete column", new InvalidOperationException(
                $"\"{col.Column.Name}\" still has {col.Tasks.Count} task(s). Move or delete them first."));
            return;
        }
        if (!Ui.Confirm("Delete column", $"Delete the empty column \"{col.Column.Name}\"?")) return;
        await Ui.Try("Delete column", async () =>
        {
            await _client.DeleteColumnAsync(_project.Id, Board.Id, col.Column.Id);
            await ReloadBoardAsync();
        });
    }

    // ---------- Tasks ----------

    [RelayCommand]
    private void BeginAddTask(ColumnVm col)
    {
        foreach (var c in Columns) c.IsAdding = false;
        col.NewTaskName = "";
        col.IsAdding = true;
    }

    [RelayCommand]
    private void CancelAddTask(ColumnVm col) => col.IsAdding = false;

    [RelayCommand]
    private async Task CommitAddTaskAsync(ColumnVm col)
    {
        if (Board == null) return;
        var name = col.NewTaskName.Trim();
        if (name.Length == 0)
        {
            col.IsAdding = false;
            return;
        }
        await Ui.Try("Add task", async () =>
        {
            var task = await _client.CreateTaskAsync(_project.Id, Board.Id, col.Column.Id, name, "");
            if (task != null)
            {
                col.Tasks.Add(new TaskCard(task, SetSeverityCommand));
                col.Sort(Comparer);
            }
            col.NewTaskName = ""; // stay in add mode for quick entry
        });
    }

    [RelayCommand]
    private async Task EditTaskAsync(TaskCard card)
    {
        if (Board == null) return;
        var result = TaskDialog.Show(Ui.Owner, card.Task, Board.Columns, SupportsSeverity);
        if (result == null) return;
        await Ui.Try("Update task", async () =>
        {
            if (result.Delete)
            {
                await _client.DeleteTaskAsync(_project.Id, Board.Id, card.Task.Id);
            }
            else
            {
                var t = card.Task;
                await _client.UpdateTaskAsync(_project.Id, Board.Id, t.Id,
                    name: result.Name != t.Name ? result.Name : null,
                    description: result.Description != t.Description ? result.Description : null,
                    columnId: result.ColumnId != t.ColumnId ? result.ColumnId : null,
                    severity: result.Severity != null && result.Severity != (t.Severity ?? TaskSeverity.Normal) ? result.Severity : null);
            }
            await ReloadBoardAsync();
        });
    }

    [RelayCommand]
    private async Task DeleteTaskAsync(TaskCard card)
    {
        if (Board == null) return;
        if (!Ui.Confirm("Delete task", $"Delete \"{card.Task.Name}\"?")) return;
        await Ui.Try("Delete task", async () =>
        {
            await _client.DeleteTaskAsync(_project.Id, Board.Id, card.Task.Id);
            foreach (var c in Columns) c.Tasks.Remove(card);
        });
    }

    /// <summary>Sets a card's severity from its context menu. Updates the UI immediately, then the server.</summary>
    [RelayCommand]
    private async Task SetSeverityAsync(SeverityOption option)
    {
        if (Board == null || option.IsCurrent) return;
        var card = option.Card;
        var before = card.Task;
        card.Task = before with { Severity = option.Level };
        ResortColumnOf(card);
        var ok = await Ui.Try("Set severity", async () =>
        {
            var updated = await _client.UpdateTaskAsync(_project.Id, Board.Id, before.Id, severity: option.Level);
            if (updated != null) card.Task = updated;
        });
        if (!ok) card.Task = before;
        ResortColumnOf(card);
    }

    /// <summary>Moves a card to another column (drag and drop). Updates the UI immediately, then the server.</summary>
    public async Task MoveTaskAsync(TaskCard card, ColumnVm target)
    {
        if (Board == null || card.Task.ColumnId == target.Column.Id) return;
        var source = Columns.FirstOrDefault(c => c.Tasks.Contains(card));
        source?.Tasks.Remove(card);
        target.Tasks.Add(card);
        target.Sort(Comparer);
        var ok = await Ui.Try("Move task", async () =>
        {
            var updated = await _client.UpdateTaskAsync(_project.Id, Board.Id, card.Task.Id, columnId: target.Column.Id);
            if (updated != null) card.Task = updated;
        });
        if (!ok) await Ui.Try("Refresh board", ReloadBoardAsync);
    }
}
