using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Spark.Notes;
using Spark.Notes.Rich;

namespace SparkStudio.Views;

/// <summary>Something the find bar can search: the rich note editor or the plain-text box.</summary>
public interface IFindTarget
{
    /// <summary>The control that hosts the text (adorned with match highlights).</summary>
    Control Host { get; }
    IReadOnlyList<object> FindAll(string query, SearchOptions options);
    void Select(object match);
    /// <summary>Index of the first match at or after the caret (wrapping to 0).</summary>
    int IndexNearCaret(IReadOnlyList<object> matches);
    IEnumerable<Rect> RectsOf(object match);
    string SelectedText { get; }
    void FocusText();
    /// <summary>Matches hidden in folded sections (not in <see cref="FindAll"/>).</summary>
    int HiddenCount(string query, SearchOptions options) => 0;
    /// <summary>Unfolds the sections holding hidden matches; true if anything changed.</summary>
    bool RevealHidden(string query, SearchOptions options) => false;
}

public sealed class RichFindTarget(NoteEditor editor) : IFindTarget
{
    public Control Host => editor.RichBox;
    public IReadOnlyList<object> FindAll(string query, SearchOptions options) =>
        FlowSearch.FindAll(editor.RichBox.Document, query, options);
    public void Select(object match) => editor.SelectRange((TextRange)match);
    public int IndexNearCaret(IReadOnlyList<object> matches)
    {
        var caret = editor.RichBox.Selection.Start;
        for (int i = 0; i < matches.Count; i++)
            if (((TextRange)matches[i]).Start.CompareTo(caret) >= 0) return i;
        return 0;
    }
    public IEnumerable<Rect> RectsOf(object match)
    {
        var r = (TextRange)match;
        var a = r.Start.GetCharacterRect(LogicalDirection.Forward);
        var b = r.End.GetCharacterRect(LogicalDirection.Backward);
        if (a.IsEmpty || b.IsEmpty) yield break;
        if (Math.Abs(a.Top - b.Top) < 1) yield return new Rect(a.Left, a.Top, Math.Max(2, b.Left - a.Left), a.Height);
        else yield return new Rect(a.Left, a.Top, Math.Max(2, editor.RichBox.ActualWidth - a.Left), a.Height); // wrapped
    }
    public string SelectedText => editor.RichBox.Selection.Text;
    public void FocusText() => editor.RichBox.Focus();
    public int HiddenCount(string query, SearchOptions options) => editor.CountFolded(query, options);
    public bool RevealHidden(string query, SearchOptions options) => editor.RevealFolded(query, options);
}

public sealed class PlainFindTarget(TextBox box) : IFindTarget
{
    public sealed record PlainMatch(int Start, int Length);

    public Control Host => box;
    public IReadOnlyList<object> FindAll(string query, SearchOptions options) =>
        NoteSearch.FindAll(box.Text, query, options).Select(i => (object)new PlainMatch(i, query.Length)).ToList();
    public void Select(object match)
    {
        var m = (PlainMatch)match;
        box.Select(m.Start, m.Length);
        var line = box.GetLineIndexFromCharacterIndex(m.Start);
        if (line >= 0) box.ScrollToLine(line);
    }
    public int IndexNearCaret(IReadOnlyList<object> matches)
    {
        for (int i = 0; i < matches.Count; i++)
            if (((PlainMatch)matches[i]).Start >= box.SelectionStart) return i;
        return 0;
    }
    public IEnumerable<Rect> RectsOf(object match)
    {
        var m = (PlainMatch)match;
        var a = box.GetRectFromCharacterIndex(m.Start);
        var b = box.GetRectFromCharacterIndex(m.Start + m.Length - 1, trailingEdge: true);
        if (a.IsEmpty || b.IsEmpty) yield break;
        yield return new Rect(a.Left, a.Top, Math.Max(2, b.Right - a.Left), a.Height);
    }
    public string SelectedText => box.SelectedText;
    public void FocusText() => box.Focus();

}

/// <summary>Draws a translucent box over every visible match, and a stronger one over the current match.</summary>
internal sealed class MatchAdorner : Adorner
{
    private static readonly Brush All = Frozen(Color.FromArgb(70, 255, 196, 0));
    private static readonly Brush Current = Frozen(Color.FromArgb(150, 255, 150, 0));
    private readonly Func<IEnumerable<(Rect Rect, bool IsCurrent)>> _rects;

    public MatchAdorner(UIElement adorned, Func<IEnumerable<(Rect, bool)>> rects) : base(adorned)
    {
        _rects = rects;
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(AdornedElement.RenderSize);
        dc.PushClip(new RectangleGeometry(bounds));
        foreach (var (rect, current) in _rects())
            if (rect.IntersectsWith(bounds)) dc.DrawRectangle(current ? Current : All, null, rect);
        dc.Pop();
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>
/// The per-note find bar (Ctrl+F): type to search, Enter / Down for the next match, Shift+Enter / Up for the
/// previous one, Esc to close. All matches are highlighted; the current one is selected in the note.
/// </summary>
public sealed class FindBar : Border
{
    private const int MaxHighlights = 2000;

    private readonly TextBox _box = new() { Width = 220, BorderThickness = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 3, 4, 3) };
    private readonly ToggleButton _case = Toggle("Aa", "Match case (Alt+C)");
    private readonly ToggleButton _word = Toggle("ab|", "Whole word (Alt+W)");
    private readonly TextBlock _count = new() { MinWidth = 70, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0), FontSize = 12 };
    private readonly System.Windows.Threading.DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(150) };

    private IFindTarget? _target;
    private IReadOnlyList<object> _matches = Array.Empty<object>();
    private int _index = -1;
    private int _hidden;
    private MatchAdorner? _adorner;

    public FindBar()
    {
        Visibility = Visibility.Collapsed;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(0, 6, 22, 0);
        Padding = new Thickness(6, 4, 4, 4);
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(1);
        SetResourceReference(BackgroundProperty, "EditorBrush");
        SetResourceReference(BorderBrushProperty, "LineBrush");
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.18 };
        _count.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");

        var prev = IconButton("", "Previous match (Shift+Enter or Up)");
        var next = IconButton("", "Next match (Enter or Down)");
        var close = IconButton("", "Close (Esc)");
        prev.Click += (_, _) => Step(-1);
        next.Click += (_, _) => Step(+1);
        close.Click += (_, _) => Close();
        _case.Click += (_, _) => Research(keepPosition: true);
        _word.Click += (_, _) => Research(keepPosition: true);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0), Opacity = 0.6 });
        row.Children.Add(_box);
        row.Children.Add(_case);
        row.Children.Add(_word);
        row.Children.Add(_count);
        row.Children.Add(prev);
        row.Children.Add(next);
        row.Children.Add(close);
        Child = row;

        _box.TextChanged += (_, _) => Research(keepPosition: false);
        _box.PreviewKeyDown += OnKey;
        _refresh.Tick += (_, _) =>
        {
            _refresh.Stop();
            Research(keepPosition: true, select: false);
        };
    }

    public bool IsOpen => Visibility == Visibility.Visible;
    public string Query => _box.Text;
    public SearchOptions Options => new(_case.IsChecked == true, _word.IsChecked == true);

    /// <summary>Switches the note being searched (e.g. another note was opened).</summary>
    public void Attach(IFindTarget? target)
    {
        if (ReferenceEquals(target?.Host, _target?.Host)) { _target = target; return; }
        Detach();
        _target = target;
        if (target == null) return;
        target.Host.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScroll));
        target.Host.SizeChanged += OnHostSizeChanged;
        if (target.Host is TextBoxBase tb) tb.TextChanged += OnHostTextChanged;
        if (IsOpen) Research(keepPosition: true, select: false);
    }

    private void Detach()
    {
        RemoveAdorner();
        if (_target == null) return;
        _target.Host.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScroll));
        _target.Host.SizeChanged -= OnHostSizeChanged;
        if (_target.Host is TextBoxBase tb) tb.TextChanged -= OnHostTextChanged;
        _matches = Array.Empty<object>();
        _index = -1;
    }

    /// <summary>Opens (or focuses) the bar, seeded with the note's selected text when it's a single line.</summary>
    public void Open()
    {
        var selected = _target?.SelectedText ?? "";
        Visibility = Visibility.Visible;
        if (selected.Length is > 0 and < 200 && !selected.Contains('\n') && !selected.Contains('\r')) _box.Text = selected;
        else Research(keepPosition: false);
        _box.Focus();
        _box.SelectAll();
    }

    /// <summary>Opens the bar with <paramref name="query"/> and makes the match chosen by <paramref name="pick"/> current.</summary>
    public void OpenAt(string query, SearchOptions options, Func<IReadOnlyList<object>, int> pick)
    {
        _case.IsChecked = options.MatchCase;
        _word.IsChecked = options.WholeWord;
        Visibility = Visibility.Visible;
        _suppress = true;
        _box.Text = query;
        _suppress = false;
        if (_target == null) return;
        _matches = _target.FindAll(query, Options);
        _hidden = _target.HiddenCount(query, Options);
        _index = _matches.Count == 0 ? -1 : Math.Clamp(pick(_matches), 0, _matches.Count - 1);
        if (_index >= 0) _target.Select(_matches[_index]);
        UpdateCount();
        EnsureAdorner();
        _box.Focus(); // Up/Down (or Enter) move between matches; Esc returns to the note at this match
        _box.SelectAll();
    }

    private bool _suppress;

    public void Close()
    {
        if (!IsOpen) return;
        Visibility = Visibility.Collapsed;
        RemoveAdorner();
        _target?.FocusText();
    }

    /// <summary>Next (+1) or previous (-1) match, wrapping around.</summary>
    public void Step(int direction)
    {
        if (_target == null) return;
        if (_hidden > 0 && _target.RevealHidden(_box.Text, Options))
        {
            // Navigating to matches: unfold the sections that hide some, then carry on from the current match.
            object? current = _index >= 0 && _index < _matches.Count ? _matches[_index] : null;
            _matches = _target.FindAll(_box.Text, Options);
            _hidden = _target.HiddenCount(_box.Text, Options);
            _index = current != null && _matches.Count > 0 ? IndexOfSameOrNext(current) : -1;
            EnsureAdorner();
        }
        if (_matches.Count == 0)
        {
            UpdateCount();
            return;
        }
        _index = _index < 0 ? (direction > 0 ? 0 : _matches.Count - 1) : (_index + direction + _matches.Count) % _matches.Count;
        _target.Select(_matches[_index]);
        UpdateCount();
        _adorner?.InvalidateVisual();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (e.Key)
        {
            case Key.Enter: Step(shift ? -1 : +1); e.Handled = true; break;
            case Key.F3: Step(shift ? -1 : +1); e.Handled = true; break;
            case Key.Down: Step(+1); e.Handled = true; break;
            case Key.Up: Step(-1); e.Handled = true; break;
            case Key.Escape: Close(); e.Handled = true; break;
            case Key.System when e.SystemKey == Key.C: _case.IsChecked = _case.IsChecked != true; Research(true); e.Handled = true; break;
            case Key.System when e.SystemKey == Key.W: _word.IsChecked = _word.IsChecked != true; Research(true); e.Handled = true; break;
        }
    }

    private void Research(bool keepPosition, bool select = true)
    {
        if (_suppress || _target == null || !IsOpen) return;
        object? current = _index >= 0 && _index < _matches.Count ? _matches[_index] : null;
        _matches = _box.Text.Length == 0 ? Array.Empty<object>() : _target.FindAll(_box.Text, Options);
        _hidden = _box.Text.Length == 0 ? 0 : _target.HiddenCount(_box.Text, Options);
        if (_matches.Count == 0) _index = -1;
        else if (keepPosition && current != null) _index = Math.Min(IndexOfSameOrNext(current), _matches.Count - 1);
        else _index = _target.IndexNearCaret(_matches);
        if (select && _index >= 0) _target.Select(_matches[_index]);
        UpdateCount();
        EnsureAdorner();
        _adorner?.InvalidateVisual();
    }

    private int IndexOfSameOrNext(object previous)
    {
        if (previous is TextRange pr)
        {
            for (int i = 0; i < _matches.Count; i++)
                if (((TextRange)_matches[i]).Start.CompareTo(pr.Start) >= 0) return i;
            return 0;
        }
        for (int i = 0; i < _matches.Count; i++)
            if (_matches[i].Equals(previous)) return i;
        return Math.Max(0, _index);
    }

    private void UpdateCount()
    {
        _count.Text = _box.Text.Length == 0 ? ""
            : _matches.Count == 0 && _hidden > 0 ? $"{_hidden} folded"
            : _matches.Count == 0 ? "No results"
            : $"{_index + 1} of {_matches.Count}" + (_hidden > 0 ? $" +{_hidden} folded" : "");
        _count.ToolTip = _hidden > 0 ? "Some matches are in folded sections. Press Enter to unfold them and go to the next match." : null;
        _box.Foreground = _box.Text.Length > 0 && _matches.Count == 0 && _hidden == 0
            ? (Brush)Application.Current.FindResource("DangerBrush")
            : (Brush)Application.Current.FindResource("TextBrush");
    }

    private void EnsureAdorner()
    {
        if (_adorner != null || _target == null) return;
        var layer = AdornerLayer.GetAdornerLayer(_target.Host);
        if (layer == null) return;
        _adorner = new MatchAdorner(_target.Host, VisibleRects);
        layer.Add(_adorner);
    }

    private void RemoveAdorner()
    {
        if (_adorner == null) return;
        AdornerLayer.GetAdornerLayer(_adorner.AdornedElement)?.Remove(_adorner);
        _adorner = null;
    }

    private IEnumerable<(Rect, bool)> VisibleRects()
    {
        if (_target == null || !IsOpen) yield break;
        var height = _target.Host.ActualHeight;
        int drawn = 0;
        for (int i = 0; i < _matches.Count && drawn < MaxHighlights; i++)
        {
            foreach (var r in _target.RectsOf(_matches[i]))
            {
                if (r.Bottom < 0 || r.Top > height) continue;
                drawn++;
                yield return (r, i == _index);
            }
        }
    }

    private void OnScroll(object sender, ScrollChangedEventArgs e) => _adorner?.InvalidateVisual();
    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e) => _adorner?.InvalidateVisual();
    private void OnHostTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsOpen) return;
        _refresh.Stop();
        _refresh.Start(); // re-search shortly after edits, keeping the current match
    }

    private static Button IconButton(string glyph, string tip)
    {
        var b = new Button { Content = glyph, ToolTip = tip, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, Padding = new Thickness(6, 4, 6, 4) };
        b.SetResourceReference(StyleProperty, "FlatButton");
        return b;
    }

    private static ToggleButton Toggle(string text, string tip)
    {
        var t = new ToggleButton
        {
            Content = text, ToolTip = tip, FontSize = 11, FontWeight = FontWeights.SemiBold, Padding = new Thickness(5, 2, 5, 2),
            Margin = new Thickness(2, 0, 0, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(1), Focusable = false,
        };
        t.SetResourceReference(BorderBrushProperty, "EditorBrush");
        t.Checked += (_, _) => t.SetResourceReference(BorderBrushProperty, "AccentBrush");
        t.Unchecked += (_, _) => t.SetResourceReference(BorderBrushProperty, "EditorBrush");
        return t;
    }
}
