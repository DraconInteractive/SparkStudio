using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using Spark.Notes;
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
        PreviewKeyDown += OnPreviewKeyDown;
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
            old.RevealRequested -= OnRevealRequested;
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
        vm.RevealRequested += OnRevealRequested;
        UpdateSidebar();
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
        if (e.PropertyName == nameof(NotesViewModel.SidebarTab)) UpdateSidebar();
        if (e.PropertyName == nameof(NotesViewModel.Current) && _vm?.Current == null) ShowDocument(null);
    }

    private void ShowDocument(OpenDocument? doc)
    {
        if (doc == null)
        {
            Find.Close();
            Find.Attach(null);
        }
        else
        {
            Find.Attach(doc.IsMarkdown ? new RichFindTarget(Editor) : new PlainFindTarget(PlainBox));
            Find.Margin = new Thickness(0, doc.IsMarkdown ? 44 : 6, 22, 0); // below the formatting toolbar
        }
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

    // ---------- Search ----------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (ctrl && e.Key == Key.F)
        {
            e.Handled = true;
            if (shift) OpenGlobalSearch();
            else if (_vm?.Current != null) Find.Open();
        }
        else if (e.Key == Key.F3 && Find.IsOpen)
        {
            e.Handled = true;
            Find.Step(shift ? -1 : +1);
        }
        else if (e.Key == Key.Escape && Find.IsOpen && !Find.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            Find.Close();
        }
    }

    private void OpenGlobalSearch()
    {
        if (_vm == null) return;
        var selected = _vm.Current?.IsMarkdown == true ? Editor.RichBox.Selection.Text : PlainBox.SelectedText;
        if (selected.Length is > 0 and < 200 && !selected.Contains('\n')) _vm.SearchQuery = selected.Trim();
        _vm.SidebarTab = 1;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            GlobalSearchBox.Focus();
            GlobalSearchBox.SelectAll();
        });
    }

    private void FilesTab_Click(object sender, RoutedEventArgs e)
    {
        if (_vm != null) _vm.SidebarTab = 0;
    }

    private void SearchTab_Click(object sender, RoutedEventArgs e) => OpenGlobalSearch();

    private void UpdateSidebar()
    {
        var search = _vm?.SidebarTab == 1;
        FilesPane.Visibility = search ? Visibility.Collapsed : Visibility.Visible;
        SearchPane.Visibility = search ? Visibility.Visible : Visibility.Collapsed;
        StyleTab(FilesTab, !search);
        StyleTab(SearchTab, search);
    }

    private static void StyleTab(Button tab, bool active)
    {
        tab.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        tab.SetResourceReference(ForegroundProperty, active ? "TextBrush" : "MutedBrush");
        tab.BorderThickness = new Thickness(0, 0, 0, active ? 2 : 0);
        tab.SetResourceReference(BorderBrushProperty, "AccentBrush");
    }

    private async void GlobalSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await _vm.RunSearchAsync();
        }
        else if (e.Key == Key.Down && SearchList.Items.Count > 0)
        {
            e.Handled = true;
            SearchList.SelectedIndex = Math.Max(0, SearchList.SelectedIndex);
            (SearchList.ItemContainerGenerator.ContainerFromIndex(SearchList.SelectedIndex) as ListBoxItem)?.Focus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _vm.SearchQuery = "";
        }
    }

    private async void SearchList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null || (e.OriginalSource as DependencyObject) is not { } src) return;
        if (ItemsControl.ContainerFromElement(SearchList, src) is not ListBoxItem { DataContext: { } item }) return;
        e.Handled = true;
        await _vm.OpenSearchResultAsync(item);
    }

    private async void SearchList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        if (e.Key == Key.Enter && SearchList.SelectedItem is { } item)
        {
            e.Handled = true;
            await _vm.OpenSearchResultAsync(item);
        }
        else if (e.Key == Key.Up && SearchList.SelectedIndex == 0)
        {
            e.Handled = true;
            GlobalSearchBox.Focus();
        }
    }

    /// <summary>Shows a global search hit in the open note: selects that match and opens the find bar on it.</summary>
    private void OnRevealRequested(OpenDocument doc, string source, SearchHit hit, string query, SearchOptions options)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_vm?.Current != doc) return;
            if (doc.IsMarkdown)
            {
                var target = Editor.LocateSourceHit(source, hit.Line - 1, hit.Column, query, options);
                Find.OpenAt(query, options, matches =>
                {
                    if (target == null) return 0;
                    for (int i = 0; i < matches.Count; i++)
                        if (((TextRange)matches[i]).Start.CompareTo(target.Start) >= 0) return i;
                    return matches.Count - 1;
                });
            }
            else
            {
                var lineStart = PlainBox.GetCharacterIndexFromLineIndex(Math.Min(hit.Line - 1, PlainBox.LineCount - 1));
                var offset = Math.Max(0, lineStart) + hit.Column;
                Find.OpenAt(query, options, matches =>
                {
                    for (int i = 0; i < matches.Count; i++)
                        if (((PlainFindTarget.PlainMatch)matches[i]).Start >= offset) return i;
                    return matches.Count - 1;
                });
            }
        });
    }
}
