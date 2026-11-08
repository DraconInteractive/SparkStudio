using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Spark.Api;
using SparkStudio.Infrastructure;
using SparkStudio.Views;

namespace SparkStudio.ViewModels;

public partial class TaskCard : ObservableObject
{
    public TaskCard(BoardTask task) => _task = task;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview), nameof(HasPreview))]
    private BoardTask _task;

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
    public ColumnVm(BoardColumn column)
    {
        _column = column;
        foreach (var t in column.Tasks) Tasks.Add(new TaskCard(t));
    }

    [ObservableProperty] private BoardColumn _column;
    [ObservableProperty] private bool _isAdding;
    [ObservableProperty] private string _newTaskName = "";

    public ObservableCollection<TaskCard> Tasks { get; } = new();
}

public partial class BoardsViewModel : ObservableObject
{
    private readonly SparkClient _client;
    private readonly Project _project;
    private readonly Debouncer _refresh = new(TimeSpan.FromMilliseconds(300));

    public BoardsViewModel(SparkClient client, Project project)
    {
        _client = client;
        _project = project;
    }

    public ObservableCollection<BoardSummary> Boards { get; } = new();
    public ObservableCollection<ColumnVm> Columns { get; } = new();

    [ObservableProperty] private BoardSummary? _selectedBoard;
    [ObservableProperty] private Board? _board;
    [ObservableProperty] private bool _isBusy;

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
            // Rebuild in place so scroll position survives refreshes.
            var byId = Columns.ToDictionary(c => c.Column.Id);
            Columns.Clear();
            foreach (var c in board.Columns)
            {
                var vm = new ColumnVm(c);
                if (byId.TryGetValue(c.Id, out var old) && old.IsAdding)
                {
                    vm.IsAdding = true;
                    vm.NewTaskName = old.NewTaskName;
                }
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
            if (task != null) col.Tasks.Add(new TaskCard(task));
            col.NewTaskName = ""; // stay in add mode for quick entry
        });
    }

    [RelayCommand]
    private async Task EditTaskAsync(TaskCard card)
    {
        if (Board == null) return;
        var result = TaskDialog.Show(Ui.Owner, card.Task, Board.Columns);
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
                    columnId: result.ColumnId != t.ColumnId ? result.ColumnId : null);
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

    /// <summary>Moves a card to another column (drag and drop). Updates the UI immediately, then the server.</summary>
    public async Task MoveTaskAsync(TaskCard card, ColumnVm target)
    {
        if (Board == null || card.Task.ColumnId == target.Column.Id) return;
        var source = Columns.FirstOrDefault(c => c.Tasks.Contains(card));
        source?.Tasks.Remove(card);
        target.Tasks.Add(card);
        var ok = await Ui.Try("Move task", async () =>
        {
            var updated = await _client.UpdateTaskAsync(_project.Id, Board.Id, card.Task.Id, columnId: target.Column.Id);
            if (updated != null) card.Task = updated;
        });
        if (!ok) await Ui.Try("Refresh board", ReloadBoardAsync);
    }
}
