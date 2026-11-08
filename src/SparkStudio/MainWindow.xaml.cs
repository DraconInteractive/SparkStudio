using System.ComponentModel;
using System.Windows;
using SparkStudio.ViewModels;

namespace SparkStudio;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private bool _closingSaved;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Loaded += async (_, _) => await _vm.ConnectAsync();
    }

    private void ProjectTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is ProjectNode node) _vm.SelectedNode = node;
    }

    private void NewSubproject_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ProjectNode node) _vm.NewProjectCommand.Execute(node);
    }

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
