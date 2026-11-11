using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using SparkStudio.Infrastructure;
using SparkStudio.ViewModels;

namespace SparkStudio.Views;

public partial class NotesView : UserControl
{
    private NotesViewModel? _vm;
    private bool _loadingPlain;
    private readonly Debouncer _linksRefresh = new(TimeSpan.FromMilliseconds(900));

    /// <summary>Unsaved text of view models this view was detached from, restored on return.</summary>
    private readonly Dictionary<NotesViewModel, string> _detachedText = new();

    public NotesView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as NotesViewModel);
        Editor.Changed += OnEditorChanged;
        Editor.WikiLinkActivated += async (target, heading) =>
        {
            if (_vm != null) await Ui.Try("Open link", () => _vm.OpenLinkAsync(target, heading));
        };
        Editor.ExternalLinkActivated += url =>
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        };
    }

    private void Attach(NotesViewModel? vm)
    {
        if (_vm != null)
        {
            // Freeze the old view model's editor text so its pending autosave can't read another project's note.
            var old = _vm;
            old.DocumentLoaded -= OnDocumentLoaded;
            old.IndexChanged -= OnIndexChanged;
            old.PropertyChanged -= OnVmPropertyChanged;
            if (old.Current != null)
            {
                var text = ReadCurrent(old.Current);
                old.ReadEditor = () => text;
                if (old.Current.IsDirty)
                {
                    _detachedText[old] = text;
                    _ = old.SaveAsync().ContinueWith(t =>
                    {
                        if (t.Result) Ui.Post(() => _detachedText.Remove(old));
                    });
                }
            }
        }

        _vm = vm;
        if (vm == null) return;
        vm.DocumentLoaded += OnDocumentLoaded;
        vm.IndexChanged += OnIndexChanged;
        vm.PropertyChanged += OnVmPropertyChanged;
        vm.ReadEditor = () => vm.Current is { } doc ? ReadCurrent(doc) : "";
        ApplyReadOnly();
        if (vm.Current is { } current)
        {
            var text = _detachedText.Remove(vm, out var pending) ? pending : current.SavedText;
            OnDocumentLoaded(current, text, null);
        }
        else ShowDocument(null);
    }

    private string ReadCurrent(OpenDocument doc) => doc.IsMarkdown ? Editor.GetMarkdown() : PlainBox.Text;

    private void ApplyReadOnly()
    {
        var locked = _vm?.IsOffline == true;
        Editor.IsReadOnly = locked;
        PlainBox.IsReadOnly = locked;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NotesViewModel.IsOffline)) ApplyReadOnly();
        if (e.PropertyName == nameof(NotesViewModel.Current) && _vm?.Current == null) ShowDocument(null);
    }

    private void ShowDocument(OpenDocument? doc)
    {
        EmptyState.Visibility = doc == null ? Visibility.Visible : Visibility.Collapsed;
        DocumentPanel.Visibility = doc == null ? Visibility.Collapsed : Visibility.Visible;
        Editor.Visibility = doc?.IsMarkdown == true ? Visibility.Visible : Visibility.Collapsed;
        PlainBox.Visibility = doc is { IsMarkdown: false } ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnDocumentLoaded(OpenDocument doc, string text, string? heading)
    {
        if (_vm == null) return;
        ShowDocument(doc);
        if (doc.IsMarkdown)
        {
            Editor.Index = _vm.Index;
            Editor.CurrentDirectory = doc.File.Directory;
            var current = _vm.ReadEditor?.Invoke();
            if (heading == null || current != text) Editor.Load(text, _vm.IsResolved);
            if (heading != null)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => Editor.ScrollToHeading(heading));
            else
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, Editor.FocusEditor);
        }
        else
        {
            _loadingPlain = true;
            PlainBox.Text = text;
            _loadingPlain = false;
        }
    }

    private void OnIndexChanged()
    {
        if (_vm == null) return;
        Editor.Index = _vm.Index;
        if (_vm.Current?.IsMarkdown == true) Editor.RefreshLinks(_vm.IsResolved);
    }

    private void OnEditorChanged()
    {
        _vm?.EditorChanged();
        _linksRefresh.Run(() =>
        {
            _vm?.RefreshOutgoingLinks();
            return Task.CompletedTask;
        });
    }

    private void PlainBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadingPlain) _vm?.EditorChanged();
    }

    private async void FileTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_vm != null && e.NewValue is FileNode node && _vm.Current?.File.Id != node.File.Id)
            await _vm.OpenNodeAsync(node);
    }

    private FileTreeNode? NodeOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as FileTreeNode
        ?? ((sender as MenuItem)?.Parent as ContextMenu)?.PlacementTarget switch
        {
            TreeViewItem tvi => tvi.DataContext as FileTreeNode,
            _ => FileTree.SelectedItem as FileTreeNode,
        };

    private void NewNote_Click(object sender, RoutedEventArgs e) => _vm?.NewNoteCommand.Execute(NodeOf(sender) ?? FileTree.SelectedItem as FileTreeNode);
    private void NewFolder_Click(object sender, RoutedEventArgs e) => _vm?.NewFolderCommand.Execute(NodeOf(sender) ?? FileTree.SelectedItem as FileTreeNode);
    private void Rename_Click(object sender, RoutedEventArgs e) => _vm?.RenameNodeCommand.Execute(NodeOf(sender));
    private void Delete_Click(object sender, RoutedEventArgs e) => _vm?.DeleteNodeCommand.Execute(NodeOf(sender));
    private void Save_Click(object sender, RoutedEventArgs e) => _vm?.SaveNowCommand.Execute(null);
    private void Reload_Click(object sender, RoutedEventArgs e) => _vm?.ReloadFromServerCommand.Execute(null);
    private void KeepMine_Click(object sender, RoutedEventArgs e) => _vm?.KeepMineCommand.Execute(null);
    private void Compare_Click(object sender, RoutedEventArgs e) => _vm?.CompareWithServerCommand.Execute(null);
    private void ConvertToNote_Click(object sender, RoutedEventArgs e) => _vm?.ConvertToNoteCommand.Execute(null);
}
