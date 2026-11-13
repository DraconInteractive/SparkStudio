using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Spark.Notes.Rich;

namespace SparkStudio.Views;

/// <summary>Something that can be folded: a heading (its section) or a callout (its body).</summary>
public sealed record FoldTarget(TextElement Element, bool IsCallout, bool IsFolded, Brush? Color);

/// <summary>
/// Draws fold chevrons over the note: in the margin left of each foldable heading, and after each callout's title.
/// Only the chevrons are hit-testable, so clicks elsewhere reach the text as usual.
/// </summary>
internal sealed class FoldAdorner : Adorner
{
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private const string ChevronDown = "", ChevronRight = "";
    private const double HitSize = 18;

    private readonly RichTextBox _box;
    private readonly Func<IEnumerable<FoldTarget>> _targets;
    private readonly List<(Rect Hit, FoldTarget Target)> _hits = new();
    private TextElement? _hover;

    public event Action<FoldTarget>? Toggle;

    public FoldAdorner(RichTextBox box, Func<IEnumerable<FoldTarget>> targets) : base(box)
    {
        _box = box;
        _targets = targets;
        Cursor = Cursors.Hand;
    }

    private bool _refreshQueued;

    /// <summary>
    /// Redraws after the editor's next layout pass. Character positions aren't available while its layout is
    /// out of date (e.g. straight after a text change), so redrawing immediately would find nothing to draw.
    /// </summary>
    public void Refresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            _refreshQueued = false;
            InvalidateVisual();
        });
    }

    private int _retries;

    protected override void OnRender(DrawingContext dc)
    {
        _hits.Clear();
        int missing = 0;
        var bounds = new Rect(_box.RenderSize);
        dc.PushClip(new RectangleGeometry(bounds));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var muted = (Brush)Application.Current.FindResource("MutedBrush");
        var accent = (Brush)Application.Current.FindResource("AccentBrush");

        foreach (var t in _targets())
        {
            if (Anchor(t) is not { } center)
            {
                missing++;
                continue;
            }
            if (center.Y < -HitSize || center.Y > bounds.Height + HitSize) continue;
            bool hover = ReferenceEquals(_hover, t.Element);
            var brush = t.IsCallout ? t.Color ?? accent : (t.IsFolded || hover ? accent : muted);
            var text = new FormattedText(t.IsFolded ? ChevronRight : ChevronDown, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface(IconFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                10, brush, dpi);
            var hit = new Rect(center.X - HitSize / 2, center.Y - HitSize / 2, HitSize, HitSize);
            if (hover) dc.DrawRoundedRectangle(HoverBrush, null, hit, 3, 3);
            dc.PushOpacity(t.IsCallout || t.IsFolded || hover ? 1 : 0.55);
            dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
            dc.Pop();
            _hits.Add((hit, t));
        }
        dc.Pop();

        // Layout wasn't ready for some targets: try again shortly (a few times at most).
        if (missing > 0 && _retries < 5)
        {
            _retries++;
            Refresh();
        }
        else if (missing == 0) _retries = 0;
    }

    private static readonly Brush HoverBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0x80, 0x80, 0x80)));

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }

    /// <summary>Where the chevron goes: left of a heading's first line, or just after a callout title's first line.</summary>
    private static Point? Anchor(FoldTarget t)
    {
        if (t.Element is Paragraph heading)
        {
            var r = heading.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            if (r.IsEmpty) return null;
            return new Point(r.Left - 16, r.Top + r.Height / 2);
        }
        if (t.Element is Section { Blocks.FirstBlock: Paragraph title })
        {
            var end = FirstLineEnd(title);
            var r = end.GetCharacterRect(LogicalDirection.Backward);
            if (r.IsEmpty) return null;
            return new Point(r.Right + 14, r.Top + r.Height / 2);
        }
        return null;
    }

    private static TextPointer FirstLineEnd(Paragraph p)
    {
        foreach (var i in p.Inlines)
            if (i is LineBreak lb) return lb.ElementStart;
        return p.ContentEnd;
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters p) =>
        _hits.Any(h => h.Hit.Contains(p.HitPoint)) ? new PointHitTestResult(this, p.HitPoint) : null;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var pt = e.GetPosition(this);
        var hit = _hits.FirstOrDefault(h => h.Hit.Contains(pt));
        if (hit.Target == null) return;
        e.Handled = true;
        Toggle?.Invoke(hit.Target);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var pt = e.GetPosition(this);
        var over = _hits.FirstOrDefault(h => h.Hit.Contains(pt)).Target?.Element;
        if (ReferenceEquals(over, _hover)) return;
        _hover = over;
        ToolTip = over == null ? null : _hits.First(h => ReferenceEquals(h.Target.Element, over)).Target.IsFolded ? "Unfold" : "Fold";
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hover == null) return;
        _hover = null;
        InvalidateVisual();
    }
}
