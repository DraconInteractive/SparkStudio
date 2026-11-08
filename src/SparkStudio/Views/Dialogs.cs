using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Spark.Api;

namespace SparkStudio.Views;

/// <summary>Small code-only dialogs, styled to match the app.</summary>
internal static class DialogKit
{
    public static Window NewWindow(Window? owner, string title, double width)
    {
        var w = new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = owner,
            ShowInTaskbar = false,
            FontFamily = new FontFamily("Segoe UI"),
            Background = (Brush)Application.Current.FindResource("EditorBrush"),
        };
        return w;
    }

    public static StackPanel Buttons(Window w, out Button ok, string okText = "OK")
    {
        ok = new Button { Content = okText, IsDefault = true, Margin = new Thickness(8, 0, 0, 0), Style = (Style)Application.Current.FindResource("PrimaryButton") };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(8, 0, 0, 0), Style = (Style)Application.Current.FindResource("FlatButton") };
        ok.Click += (_, _) => w.DialogResult = true;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(cancel);
        panel.Children.Add(ok);
        return panel;
    }

    public static TextBlock Label(string text) => new()
    {
        Text = text,
        Margin = new Thickness(0, 0, 0, 4),
        Foreground = (Brush)Application.Current.FindResource("MutedBrush"),
    };
}

public static class InputDialog
{
    public static string? Show(Window? owner, string title, string label, string initial = "")
    {
        var w = DialogKit.NewWindow(owner, title, 380);
        var box = new TextBox { Text = initial };
        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(DialogKit.Label(label));
        root.Children.Add(box);
        root.Children.Add(DialogKit.Buttons(w, out _));
        w.Content = root;
        w.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        return w.ShowDialog() == true ? box.Text : null;
    }
}

public static class TextDialog
{
    public static string? Show(Window? owner, string title, string initial)
    {
        var w = DialogKit.NewWindow(owner, title, 520);
        var box = new TextBox
        {
            Text = initial, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 180,
            VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(box);
        root.Children.Add(DialogKit.Buttons(w, out _, "Save"));
        w.Content = root;
        w.Loaded += (_, _) => box.Focus();
        return w.ShowDialog() == true ? box.Text : null;
    }
}

public sealed record TaskDialogResult(string Name, string Description, string ColumnId, bool Delete);

public static class TaskDialog
{
    public static TaskDialogResult? Show(Window? owner, BoardTask task, IEnumerable<BoardColumn> columns)
    {
        var w = DialogKit.NewWindow(owner, "Edit task", 520);
        var name = new TextBox { Text = task.Name, FontSize = 14, FontWeight = FontWeights.SemiBold };
        var desc = new TextBox
        {
            Text = task.Description, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, Height = 200,
            VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var cols = columns.ToList();
        var column = new ComboBox { ItemsSource = cols, DisplayMemberPath = "Name", SelectedItem = cols.FirstOrDefault(c => c.Id == task.ColumnId) };
        bool delete = false;
        var del = new Button
        {
            Content = "Delete task", Style = (Style)Application.Current.FindResource("FlatButton"),
            Foreground = (Brush)Application.Current.FindResource("DangerBrush"), HorizontalAlignment = HorizontalAlignment.Left,
        };
        del.Click += (_, _) =>
        {
            if (MessageBox.Show(w, $"Delete \"{task.Name}\"?", "Delete task", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            delete = true;
            w.DialogResult = true;
        };

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(DialogKit.Label("Name"));
        root.Children.Add(name);
        root.Children.Add(new Border { Height = 10 });
        root.Children.Add(DialogKit.Label("Description"));
        root.Children.Add(desc);
        root.Children.Add(new Border { Height = 10 });
        root.Children.Add(DialogKit.Label("Column"));
        root.Children.Add(column);
        var footer = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var buttons = DialogKit.Buttons(w, out var ok, "Save");
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        del.VerticalAlignment = VerticalAlignment.Bottom;
        footer.Children.Add(del);
        root.Children.Add(footer);
        w.Content = root;
        w.Loaded += (_, _) => name.Focus();
        w.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) w.DialogResult = true;
        };

        if (w.ShowDialog() != true) return null;
        if (!delete && string.IsNullOrWhiteSpace(name.Text)) return null;
        return new TaskDialogResult(name.Text.Trim(), desc.Text, (column.SelectedItem as BoardColumn)?.Id ?? task.ColumnId, delete);
    }
}
