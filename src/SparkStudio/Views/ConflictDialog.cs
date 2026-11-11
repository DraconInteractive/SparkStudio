using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Spark.Notes;

namespace SparkStudio.Views;

public enum ConflictChoice { Cancel, KeepMine, UseServer }

/// <summary>Side-by-side comparison of the editor's text and the server's, for choosing which version to keep.</summary>
public static class ConflictDialog
{
    private const int Context = 2;

    private static readonly Brush MineTint = Frozen("#E3EDFD");
    private static readonly Brush ServerTint = Frozen("#FFF0D2");
    private static readonly Brush EmptyTint = Frozen("#F3F2F5");
    private static readonly Brush GutterText = Frozen("#9A97A3");
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");

    public static ConflictChoice Show(Window? owner, string title, string mine, string server)
    {
        var rows = LineDiff.Compute(mine, server);
        int changed = rows.Count(r => r.Kind == DiffKind.Changed);
        int onlyMine = rows.Count(r => r.Kind == DiffKind.Removed);
        int onlyServer = rows.Count(r => r.Kind == DiffKind.Added);
        bool identical = changed + onlyMine + onlyServer == 0;

        var w = DialogKit.NewWindow(owner, $"Compare versions: {title}", 1100);
        w.SizeToContent = SizeToContent.Manual;
        w.Height = Math.Min(760, SystemParameters.WorkArea.Height - 80);
        w.ResizeMode = ResizeMode.CanResize;
        w.MinWidth = 640;
        w.MinHeight = 360;

        var root = new DockPanel { Margin = new Thickness(18) };

        // Header + summary
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(new TextBlock
        {
            Text = identical
                ? "Both versions have the same text. Either choice is safe."
                : "Your version and the server's differ. Review the differences, then choose which version to keep.",
            TextWrapping = TextWrapping.Wrap, FontSize = 14,
        });
        if (!identical)
        {
            var parts = new List<string>();
            if (changed > 0) parts.Add($"{changed} line{S(changed)} differ");
            if (onlyMine > 0) parts.Add($"{onlyMine} line{S(onlyMine)} only in yours");
            if (onlyServer > 0) parts.Add($"{onlyServer} line{S(onlyServer)} only on the server");
            header.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", parts), Margin = new Thickness(0, 4, 0, 0),
                Foreground = (Brush)Application.Current.FindResource("MutedBrush"),
            });
        }
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // Footer
        var choice = ConflictChoice.Cancel;
        var hideSame = new CheckBox
        {
            Content = "Only show differences", IsChecked = !identical && rows.Count > 60,
            VerticalAlignment = VerticalAlignment.Center, IsEnabled = !identical,
        };
        var useServer = new Button { Content = "Use server's version", Margin = new Thickness(8, 0, 0, 0), Style = Style("FlatButton") };
        var keepMine = new Button { Content = "Keep my version", Margin = new Thickness(8, 0, 0, 0), Style = Style("PrimaryButton") };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(8, 0, 0, 0), Style = Style("FlatButton") };
        useServer.ToolTip = "Replace the editor's text with the server's version (your changes are discarded)";
        keepMine.ToolTip = "Overwrite the server's version with yours";
        useServer.Click += (_, _) => { choice = ConflictChoice.UseServer; w.DialogResult = true; };
        keepMine.Click += (_, _) => { choice = ConflictChoice.KeepMine; w.DialogResult = true; };
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(cancel);
        buttons.Children.Add(useServer);
        buttons.Children.Add(keepMine);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(hideSame);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        // Column headings
        var headings = RowGrid();
        headings.Children.Add(Heading("Yours (in the editor)", MineTint, 0));
        headings.Children.Add(Heading("Server (latest)", ServerTint, 3));
        DockPanel.SetDock(headings, Dock.Top);
        root.Children.Add(headings);

        // Diff body
        var body = new StackPanel();
        var scroll = new ScrollViewer
        {
            Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var frame = new Border
        {
            Child = scroll, BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.FindResource("LineBrush"),
        };
        root.Children.Add(frame);

        FrameworkElement? firstDifference = null;
        void Fill()
        {
            body.Children.Clear();
            firstDifference = null;
            var show = Visible(rows, hideSame.IsChecked == true);
            for (int i = 0; i < show.Count; i++)
            {
                if (show[i] is not { } r)
                {
                    body.Children.Add(Gap(show, i, rows));
                    continue;
                }
                var row = Row(r);
                if (r.Kind != DiffKind.Same) firstDifference ??= row;
                body.Children.Add(row);
            }
        }
        hideSame.Checked += (_, _) => Fill();
        hideSame.Unchecked += (_, _) => Fill();
        Fill();

        w.Content = root;
        w.Loaded += (_, _) =>
        {
            keepMine.Focus();
            if (firstDifference != null)
                w.Dispatcher.BeginInvoke(() => firstDifference.BringIntoView(new Rect(0, -60, 1, 200)),
                    System.Windows.Threading.DispatcherPriority.Loaded);
        };
        w.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) w.DialogResult = false; };
        return w.ShowDialog() == true ? choice : ConflictChoice.Cancel;
    }

    /// <summary>Rows to display; null entries stand for a run of hidden unchanged lines.</summary>
    private static List<DiffRow?> Visible(IReadOnlyList<DiffRow> rows, bool onlyDifferences)
    {
        if (!onlyDifferences) return rows.Cast<DiffRow?>().ToList();
        var keep = new bool[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Kind == DiffKind.Same) continue;
            for (int k = Math.Max(0, i - Context); k <= Math.Min(rows.Count - 1, i + Context); k++) keep[k] = true;
        }
        var list = new List<DiffRow?>();
        for (int i = 0; i < rows.Count; i++)
        {
            if (keep[i]) list.Add(rows[i]);
            else if (list.Count == 0 || list[^1] != null) list.Add(null);
        }
        return list;
    }

    private static FrameworkElement Gap(List<DiffRow?> show, int index, IReadOnlyList<DiffRow> all)
    {
        // Count the hidden lines between the neighbouring visible rows.
        int before = index > 0 && show[index - 1] is { } p ? IndexOf(all, p) + 1 : 0;
        int after = index + 1 < show.Count && show[index + 1] is { } n ? IndexOf(all, n) : all.Count;
        int hidden = after - before;
        return new TextBlock
        {
            Text = $"⋯  {hidden} unchanged line{S(hidden)}",
            Foreground = GutterText, Background = EmptyTint, Padding = new Thickness(10, 3, 10, 3), FontSize = 11,
        };
    }

    private static int IndexOf(IReadOnlyList<DiffRow> rows, DiffRow row)
    {
        for (int i = 0; i < rows.Count; i++) if (ReferenceEquals(rows[i], row)) return i;
        return -1;
    }

    private static Grid RowGrid()
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return g;
    }

    private static UIElement Heading(string text, Brush tint, int column)
    {
        var b = new Border
        {
            Background = tint, Padding = new Thickness(10, 6, 10, 6),
            Child = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold },
        };
        Grid.SetColumn(b, column);
        Grid.SetColumnSpan(b, 2);
        return b;
    }

    private static FrameworkElement Row(DiffRow r)
    {
        var g = RowGrid();
        bool differs = r.Kind != DiffKind.Same;
        AddSide(g, 0, r.LeftNumber, r.Left, differs ? (r.Left == null ? EmptyTint : MineTint) : null);
        var divider = new Border { Background = (Brush)Application.Current.FindResource("LineBrush") };
        Grid.SetColumn(divider, 2);
        g.Children.Add(divider);
        AddSide(g, 3, r.RightNumber, r.Right, differs ? (r.Right == null ? EmptyTint : ServerTint) : null);
        return g;
    }

    private static void AddSide(Grid g, int column, int? number, string? text, Brush? tint)
    {
        var bg = new Border { Background = tint ?? Brushes.Transparent };
        Grid.SetColumn(bg, column);
        Grid.SetColumnSpan(bg, 2);
        g.Children.Add(bg);
        var num = new TextBlock
        {
            Text = number?.ToString() ?? "", Foreground = GutterText, FontFamily = Mono, FontSize = 12,
            TextAlignment = TextAlignment.Right, Padding = new Thickness(0, 2, 8, 2),
        };
        Grid.SetColumn(num, column);
        g.Children.Add(num);
        var line = new TextBlock
        {
            Text = text ?? "", FontFamily = Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(0, 2, 10, 2),
        };
        Grid.SetColumn(line, column + 1);
        g.Children.Add(line);
    }

    private static string S(int n) => n == 1 ? "" : "s";
    private static Style Style(string key) => (Style)Application.Current.FindResource(key);

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
