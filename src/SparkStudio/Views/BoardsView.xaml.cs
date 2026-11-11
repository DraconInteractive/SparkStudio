using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SparkStudio.ViewModels;

namespace SparkStudio.Views;

public partial class BoardsView : UserControl
{
    private Point _dragStart;
    private TaskCard? _dragCard;

    /// <summary>Opens the sort menu under the button (left click, like a dropdown).</summary>
    private void SortButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.DataContext = button.DataContext;
        menu.IsOpen = true;
    }

    public BoardsView() => InitializeComponent();

    private BoardsViewModel? Vm => DataContext as BoardsViewModel;

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var card = (sender as FrameworkElement)?.DataContext as TaskCard;
        if (e.ClickCount == 2 && card != null)
        {
            Vm?.EditTaskCommand.Execute(card);
            e.Handled = true;
            return;
        }
        _dragStart = e.GetPosition(this);
        _dragCard = card;
    }

    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCard == null || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var card = _dragCard;
        _dragCard = null;
        var element = (FrameworkElement)sender;
        element.Opacity = 0.5;
        try { DragDrop.DoDragDrop(element, new DataObject(typeof(TaskCard), card), DragDropEffects.Move); }
        finally { element.Opacity = 1; }
    }

    private void Column_DragOver(object sender, DragEventArgs e)
    {
        var ok = e.Data.GetDataPresent(typeof(TaskCard));
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        if (ok && sender is Border b) b.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
        if (sender is Border bb) bb.BorderThickness = new Thickness(ok ? 2 : 0);
        e.Handled = true;
    }

    private void Column_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border b) b.BorderThickness = new Thickness(0);
    }

    private async void Column_Drop(object sender, DragEventArgs e)
    {
        if (sender is Border b) b.BorderThickness = new Thickness(0);
        if (Vm == null || e.Data.GetData(typeof(TaskCard)) is not TaskCard card) return;
        if ((sender as FrameworkElement)?.DataContext is ColumnVm column) await Vm.MoveTaskAsync(card, column);
    }

    private void ColumnMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } btn)
        {
            menu.PlacementTarget = btn;
            menu.IsOpen = true;
        }
    }

    private void AddBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is TextBox box)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => box.Focus());
    }

    private void AddBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: ColumnVm col } && string.IsNullOrWhiteSpace(col.NewTaskName))
            col.IsAdding = false;
    }
}
