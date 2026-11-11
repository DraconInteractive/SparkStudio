using System.ComponentModel;
using System.Windows;
using SparkStudio.Infrastructure;
using SparkStudio.ViewModels;

namespace SparkStudio;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private bool _closingSaved;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = AppVersion.Display;
        Title = $"SPARK Studio {AppVersion.Display}";
        DataContext = _vm;
        Loaded += async (_, _) => await _vm.ConnectAsync();
    }

    // Projects switch on click or Enter only, so browsing the tree with the arrow keys doesn't load each one.
    private void ProjectItem_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ProjectNode node) _vm.SelectProject(node);
    }

    private void ProjectPopup_Opened(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            ProjectFilterBox.Focus();
            System.Windows.Input.Keyboard.Focus(ProjectFilterBox);
        });
    }

    private void ProjectPopup_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case System.Windows.Input.Key.Escape:
                _vm.IsProjectPickerOpen = false;
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Down when ProjectFilterBox.IsKeyboardFocused:
                // Move from the search box into the results.
                if (_vm.IsFilteringProjects && FilteredList.Items.Count > 0)
                {
                    FilteredList.SelectedIndex = 0;
                    (FilteredList.ItemContainerGenerator.ContainerFromIndex(0) as UIElement)?.Focus();
                }
                else if (ProjectTree.ItemContainerGenerator.ContainerFromIndex(0) is System.Windows.Controls.TreeViewItem first)
                {
                    first.Focus();
                }
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Enter:
                ProjectNode? pick = ProjectFilterBox.IsKeyboardFocused
                    ? _vm.FilteredProjects.FirstOrDefault()
                    : _vm.IsFilteringProjects ? FilteredList.SelectedItem as ProjectNode : ProjectTree.SelectedItem as ProjectNode;
                if (pick != null) _vm.SelectProject(pick);
                e.Handled = true;
                break;
        }
    }

    private void NewSubproject_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ProjectNode node) _vm.NewProjectCommand.Execute(node);
    }

    private void Status_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        _vm.EditConnectionCommand.Execute(null);

    private void Description_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        _vm.EditProjectDescriptionCommand.Execute(null);

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closingSaved) return;
        var notes = _vm.Workspace?.Notes;
        if (notes?.Current is not { IsDirty: true }) return;
        e.Cancel = true;
        var saved = await notes.SaveAsync();
        if (!saved && MessageBox.Show(this, "Your latest changes could not be saved. Close anyway?", "SPARK Studio",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _closingSaved = true;
        await _vm.DisposeAsync();
        Close();
    }
}
