using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Spark.Api;
using Spark.Notes;
using Spark.Notes.Rich;

namespace SparkStudio.Views;

public sealed record Suggestion(string Title, string Detail, SparkFile? File, string Target);

/// <summary>
/// WYSIWYG Markdown note editor: a RichTextBox whose FlowDocument follows the conventions in
/// <see cref="NoteDocument"/>, with [[wikilink]] autocomplete and Markdown-style typing shortcuts.
/// </summary>
public partial class NoteEditor : UserControl
{
    private static readonly Regex TypedLink = new(@"(!?)\[\[([^\[\]\r\n]+)\]\]$", RegexOptions.Compiled);
    private sealed record InlineShortcut(Regex Pattern, params (DependencyProperty Prop, object On, object? Off)[] Props);

    private static readonly InlineShortcut[] InlineShortcuts =
    {
        new(new Regex(@"\*\*([^*\s](?:[^*]*[^*\s])?)\*\*$"), (TextElement.FontWeightProperty, FontWeights.Bold, FontWeights.Normal)),
        new(new Regex(@"__([^_\s](?:[^_]*[^_\s])?)__$"), (TextElement.FontWeightProperty, FontWeights.Bold, FontWeights.Normal)),
        new(new Regex(@"~~([^~\s](?:[^~]*[^~\s])?)~~$"), (Inline.TextDecorationsProperty, TextDecorations.Strikethrough, new TextDecorationCollection())),
        new(new Regex(@"(?<![*\w])\*([^*\s](?:[^*]*[^*\s])?)\*$"), (TextElement.FontStyleProperty, FontStyles.Italic, FontStyles.Normal)),
        new(new Regex(@"(?<![_\w])_([^_\s](?:[^_]*[^_\s])?)_$"), (TextElement.FontStyleProperty, FontStyles.Italic, FontStyles.Normal)),
        new(new Regex(@"`([^`]+)`$"),
            (TextElement.FontFamilyProperty, NoteDocument.CodeFont, NoteDocument.BodyFont),
            (TextElement.BackgroundProperty, NoteDocument.CodeBackground, null)),
    };

    private static readonly Regex MarkdownHint = new(
        @"(^|\n)\s{0,3}(#{1,6}\s|[-*+]\s|\d+\.\s|>\s|```|- \[[ xX]\])|\*\*|\[\[|\]\(|~~", RegexOptions.Compiled);

    private bool _loading, _editing, _syncingStyle;
    private string? _frontMatter;
    private Func<WikiLink, bool> _resolver = _ => true;
    private TextPointer? _queryStart;
    private string? _queryHeading;
    private bool _queryEmbed;

    public NoteEditor()
    {
        InitializeComponent();
        Box.Document = NoteDocument.NewDocument();
        Box.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(TaskToggled));
        Box.AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(TaskToggled));
        DataObject.AddPastingHandler(Box, OnPaste);
    }

    public NoteIndex? Index { get; set; }
    public string? CurrentDirectory { get; set; }

    public event Action? Changed;
    public event Action<string, string?>? WikiLinkActivated;
    public event Action<string>? ExternalLinkActivated;

    // ---------- Load / save ----------

    public void Load(string markdown, Func<WikiLink, bool> resolver)
    {
        _loading = true;
        try
        {
            CloseSuggestions();
            _resolver = resolver;
            var conv = new MarkdownToFlow(resolver);
            var doc = conv.Convert(markdown);
            _frontMatter = conv.FrontMatter;
            if (doc.Blocks.Count == 0) doc.Blocks.Add(NewParagraph());
            Box.IsUndoEnabled = false;
            Box.Document = doc;
            Box.IsUndoEnabled = true;
            Box.CaretPosition = doc.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
            Box.ScrollToHome();
        }
        finally { _loading = false; }
        SyncBlockStyle();
    }

    public string GetMarkdown() => FlowToMarkdown.Convert(Box.Document, _frontMatter);

    public void FocusEditor() => Box.Focus();

    /// <summary>Re-colours wikilinks after notes were created or deleted.</summary>
    public void RefreshLinks(Func<WikiLink, bool> resolver)
    {
        _resolver = resolver;
        var was = _loading;
        _loading = true;
        try
        {
            foreach (var h in Descendants(Box.Document).OfType<Hyperlink>())
                if (NoteDocument.WikiTagOf(h) is { } t)
                    NoteDocument.StyleWikiLink(h, resolver(new WikiLink(t.Target, t.Heading)));
        }
        finally { _loading = was; }
    }

    public void ScrollToHeading(string heading)
    {
        var want = heading.Trim();
        foreach (var p in Descendants(Box.Document).OfType<Paragraph>())
        {
            if (NoteDocument.InfoOf(p).Kind != BlockKind.Heading) continue;
            var text = new TextRange(p.ContentStart, p.ContentEnd).Text.Trim();
            if (!text.Equals(want, StringComparison.OrdinalIgnoreCase)) continue;
            Box.CaretPosition = p.ContentStart;
            p.BringIntoView();
            Box.Focus();
            return;
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    private static Paragraph NewParagraph()
    {
        var p = new Paragraph();
        NoteDocument.Apply(p, new BlockInfo(BlockKind.Paragraph));
        return p;
    }

    // ---------- Change tracking ----------

    private void Box_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        Changed?.Invoke();
        if (_editing) return;
        // Auto-conversions run synchronously, after the typed text is really in the document
        // (the RichTextBox batches fast typing), and each becomes its own undo step.
        var last = Box.CaretPosition.GetTextInRun(LogicalDirection.Backward);
        if (last.Length > 0 && last[^1] is ']' or '*' or '_' or '~' or '`')
        {
            if (TryConvertTypedLink("") || TryConvertTypedFormatting("")) return;
        }
        else if (last.Length > 0 && last[^1] == ' ' && BlockShortcutAt(Box.CaretPosition) is { } shortcut)
        {
            // List/quote commands can't run inside TextChanged (they flush pending typing into an open
            // undo unit), so apply after this event; the marker is matched at the paragraph start.
            var para = Box.CaretPosition.Paragraph!;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ApplyBlockShortcut(para, shortcut));
            return;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Input, UpdateSuggestions);
    }

    private void TaskToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading) Changed?.Invoke();
    }

    private void Box_SelectionChanged(object sender, RoutedEventArgs e)
    {
        SyncBlockStyle();
        if (Suggest.IsOpen && !_editing) UpdateSuggestions();
    }

    private static readonly System.Reflection.PropertyInfo? EditorProperty =
        typeof(System.Windows.Controls.Primitives.TextBoxBase).GetProperty("TextEditor",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    private static readonly System.Reflection.MethodInfo? FlushInputMethod =
        typeof(FrameworkElement).Assembly.GetType("System.Windows.Documents.TextEditorTyping")?
            .GetMethod("_FlushPendingInputItems", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

    /// <summary>
    /// The RichTextBox queues fast typing and inserts it later. Its own key handlers flush that queue first;
    /// ours must too, or characters typed just before Enter land after our change. (Internal API; best effort.)
    /// </summary>
    private void FlushPendingInput()
    {
        try
        {
            if (EditorProperty?.GetValue(Box) is { } editor) FlushInputMethod?.Invoke(null, new[] { editor });
        }
        catch (Exception) { }
    }

    /// <summary>Runs a built-in editing command without our TextChanged handling and outside any change block.</summary>
    private void Quiet(Action command)
    {
        _editing = true;
        try { command(); }
        finally { _editing = false; }
    }

    /// <summary>Gives lists created by WPF commands the same look as lists loaded from Markdown.</summary>
    private void StyleListAt(TextPointer pos)
    {
        if (pos.Paragraph?.Parent is not ListItem { Parent: List list }) return;
        var model = NoteDocument.NewList(list.MarkerStyle is TextMarkerStyle.Decimal);
        list.Margin = model.Margin;
        list.Padding = model.Padding;
        foreach (var para in list.ListItems.SelectMany(li => li.Blocks.OfType<Paragraph>()))
            para.Margin = new Thickness(0, 0, 0, 2);
    }

    private void ToggleList(bool numbered)
    {
        Quiet(() => (numbered ? EditingCommands.ToggleNumbering : EditingCommands.ToggleBullets).Execute(null, Box));
        StyleListAt(Box.CaretPosition);
    }

    private void Edit(Action change)
    {
        _editing = true;
        Box.BeginChange();
        try { change(); }
        finally
        {
            Box.EndChange();
            _editing = false;
        }
    }

    // ---------- Wikilinks ----------

    private static Hyperlink? HyperlinkAt(TextPointer? p)
    {
        for (DependencyObject? o = p?.Parent; o != null; o = (o as TextElement)?.Parent)
            if (o is Hyperlink h) return h;
        return null;
    }

    private bool InPlainTextBlock(TextPointer p) =>
        p.Paragraph is { } para && NoteDocument.InfoOf(para).Kind is BlockKind.CodeBlock or BlockKind.Raw;

    /// <summary>Text of the current run before the caret, plus the character about to be typed.</summary>
    private string? TextBeforeWith(string typed)
    {
        var caret = Box.CaretPosition;
        if (!Box.Selection.IsEmpty || HyperlinkAt(caret) != null || InPlainTextBlock(caret)) return null;
        return caret.GetTextInRun(LogicalDirection.Backward) + typed;
    }

    /// <summary>Turns "[[Title]]" into a link as the closing bracket is typed.</summary>
    private bool TryConvertTypedLink(string typed)
    {
        if (TextBeforeWith(typed) is not { } before) return false;
        var m = TypedLink.Match(before);
        if (!m.Success) return false;
        var link = WikiLink.ParseInner(m.Groups[2].Value, m.Groups[1].Length > 0);
        var start = Box.CaretPosition.GetPositionAtOffset(-(m.Length - typed.Length));
        if (link == null || start == null) return false;
        CloseSuggestions();
        InsertWikiLink(start, Box.CaretPosition, link);
        return true;
    }

    /// <summary>Applies Markdown-style inline formatting as the closing delimiter is typed (**bold**, *italic*, `code`, ~~strike~~).</summary>
    private bool TryConvertTypedFormatting(string typed)
    {
        if (TextBeforeWith(typed) is not { Length: >= 3 } before) return false;
        foreach (var shortcut in InlineShortcuts)
        {
            var m = shortcut.Pattern.Match(before);
            if (!m.Success) continue;
            var caret = Box.CaretPosition;
            var start = caret.GetPositionAtOffset(-(m.Length - typed.Length));
            if (start == null) return false;
            var inner = m.Groups[1].Value;
            Edit(() =>
            {
                var range = new TextRange(start, caret) { Text = inner };
                foreach (var (prop, on, _) in shortcut.Props) range.ApplyPropertyValue(prop, on);
                // Keep typing unformatted: collapse after the text and "spring-load" the plain formatting.
                Box.CaretPosition = range.End;
                foreach (var (prop, _, off) in shortcut.Props) Box.Selection.ApplyPropertyValue(prop, off);
            });
            return true;
        }
        return false;
    }

    private void InsertWikiLink(TextPointer start, TextPointer end, WikiLink link)
    {
        Edit(() =>
        {
            var range = new TextRange(start, end);
            range.Text = "";
            var h = new Hyperlink(new Run(link.Display), range.Start);
            NoteDocument.ConfigureWikiLink(h, link, _resolver(link));
            PlaceCaretAfter(h);
        });
    }

    /// <summary>Puts the caret just outside a link so that further typing isn't part of it.</summary>
    private void PlaceCaretAfter(Inline inline)
    {
        if (inline.NextInline is Run { Text.Length: > 0 } next)
        {
            Box.CaretPosition = next.ContentStart;
            return;
        }
        var tail = new Run("");
        inline.SiblingInlines.InsertAfter(inline, tail);
        Box.CaretPosition = tail.ContentStart;
    }

    private void UpdateSuggestions()
    {
        var caret = Box.CaretPosition;
        if (Index == null || !Box.Selection.IsEmpty || HyperlinkAt(caret) != null || InPlainTextBlock(caret))
        {
            CloseSuggestions();
            return;
        }
        var before = caret.GetTextInRun(LogicalDirection.Backward);
        var idx = before.LastIndexOf("[[", StringComparison.Ordinal);
        if (idx < 0)
        {
            CloseSuggestions();
            return;
        }
        var query = before[(idx + 2)..];
        if (query.Contains("]]") || query.Contains('|') || query.Length > 120)
        {
            CloseSuggestions();
            return;
        }
        _queryEmbed = idx > 0 && before[idx - 1] == '!';
        _queryStart = caret.GetPositionAtOffset(-(query.Length + 2 + (_queryEmbed ? 1 : 0)));
        var hash = query.IndexOf('#');
        _queryHeading = hash >= 0 ? query[(hash + 1)..].Trim() : null;
        var search = hash >= 0 ? query[..hash] : query;

        var items = Index.Search(search, 12)
            .Select(f => new Suggestion(NoteIndex.TitleOf(f), Index.RelativePath(f), f, Index.LinkTextFor(f, CurrentDirectory)))
            .ToList();
        var trimmed = search.Trim();
        if (trimmed.Length > 0 && !items.Any(i => i.Title.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
            items.Add(new Suggestion($"Create \"{trimmed}\"", "New note (created when you Ctrl+Click the link)", null, trimmed));
        if (items.Count == 0)
        {
            CloseSuggestions();
            return;
        }

        SuggestList.ItemsSource = items;
        SuggestList.SelectedIndex = 0;
        var rect = caret.GetCharacterRect(LogicalDirection.Backward);
        Suggest.Placement = PlacementMode.Custom;
        Suggest.CustomPopupPlacementCallback = (popupSize, targetSize, _) => new[]
        {
            new CustomPopupPlacement(new Point(rect.Left, rect.Bottom + 4), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(rect.Left, rect.Top - popupSize.Height - 4), PopupPrimaryAxis.Horizontal),
        };
        Suggest.IsOpen = false;
        Suggest.IsOpen = true;
    }

    private void CloseSuggestions()
    {
        Suggest.IsOpen = false;
        _queryStart = null;
    }

    private void AcceptSuggestion(Suggestion s)
    {
        if (_queryStart == null) return;
        var link = new WikiLink(s.Target, string.IsNullOrEmpty(_queryHeading) ? null : _queryHeading, null, _queryEmbed);
        var start = _queryStart;
        CloseSuggestions();
        InsertWikiLink(start, Box.CaretPosition, link);
        Box.Focus();
    }

    private void SuggestList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (SuggestList.SelectedItem is Suggestion s) AcceptSuggestion(s);
    }

    private void Box_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        var pos = Box.GetPositionFromPoint(e.GetPosition(Box), false);
        var h = HyperlinkAt(pos);
        if (h == null) return;
        e.Handled = true;
        if (NoteDocument.WikiTagOf(h) is { } wiki) WikiLinkActivated?.Invoke(wiki.Target, wiki.Heading);
        else if (NoteDocument.UrlOf(h) is { Length: > 0 } url) ExternalLinkActivated?.Invoke(url);
    }

    private void Box_MouseMove(object sender, MouseEventArgs e)
    {
        var overLink = (Keyboard.Modifiers & ModifierKeys.Control) != 0 &&
                       HyperlinkAt(Box.GetPositionFromPoint(e.GetPosition(Box), false)) != null;
        Box.Cursor = overLink ? Cursors.Hand : null;
    }

    // ---------- Keyboard ----------

    /// <summary>Locks the note (offline): the text can be read, selected and copied, and links followed.</summary>
    public bool IsReadOnly
    {
        get => Box.IsReadOnly;
        set
        {
            Box.IsReadOnly = value;
            Box.IsReadOnlyCaretVisible = value;
            Toolbar.IsEnabled = !value;
            Toolbar.Opacity = value ? 0.5 : 1;
            if (value) Suggest.IsOpen = false;
        }
    }

    private void Box_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Box.IsReadOnly) return; // our shortcuts edit the document directly; leave navigation/copy to WPF
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        if (Suggest.IsOpen && SuggestList.Items.Count > 0)
        {
            switch (key)
            {
                case Key.Down:
                    SuggestList.SelectedIndex = (SuggestList.SelectedIndex + 1) % SuggestList.Items.Count;
                    SuggestList.ScrollIntoView(SuggestList.SelectedItem);
                    e.Handled = true;
                    return;
                case Key.Up:
                    SuggestList.SelectedIndex = (SuggestList.SelectedIndex - 1 + SuggestList.Items.Count) % SuggestList.Items.Count;
                    SuggestList.ScrollIntoView(SuggestList.SelectedItem);
                    e.Handled = true;
                    return;
                case Key.Enter:
                case Key.Tab:
                    if (SuggestList.SelectedItem is Suggestion s) AcceptSuggestion(s);
                    e.Handled = true;
                    return;
                case Key.Escape:
                    CloseSuggestions();
                    e.Handled = true;
                    return;
            }
        }

        if (mods.HasFlag(ModifierKeys.Control))
        {
            bool shift = mods.HasFlag(ModifierKeys.Shift);
            switch (key)
            {
                case Key.E: ToggleInlineCode(); e.Handled = true; return;
                case Key.K when shift: InsertExternalLink(); e.Handled = true; return;
                case Key.K: StartWikiLink(); e.Handled = true; return;
                case Key.X when shift: ToggleStrike(); e.Handled = true; return;
                case Key.D0: SetBlockKind(new BlockInfo(BlockKind.Paragraph)); e.Handled = true; return;
                case Key.D1 or Key.D2 or Key.D3 or Key.D4:
                    SetBlockKind(new BlockInfo(BlockKind.Heading, key - Key.D0));
                    e.Handled = true;
                    return;
                // Formatting Markdown can't store: swallow so the editor stays faithful to the file.
                case Key.U or Key.L or Key.R or Key.J or Key.OemOpenBrackets or Key.OemCloseBrackets:
                case Key.D5:
                    e.Handled = true;
                    return;
            }
        }

        if (key == Key.Enter && !mods.HasFlag(ModifierKeys.Shift))
        {
            FlushPendingInput();
            HandleEnter(e);
        }
        else if (key == Key.Back && mods == ModifierKeys.None)
        {
            FlushPendingInput();
            HandleBackspace(e);
        }
    }

    private void Box_PreviewTextInput(object sender, TextCompositionEventArgs e) { }

    private static string TextBefore(Paragraph p, TextPointer caret) => new TextRange(p.ContentStart, caret).Text;

    private static bool IsEmpty(Paragraph p) => new TextRange(p.ContentStart, p.ContentEnd).Text.Trim().Length == 0;

    private bool CaretAtEnd(Paragraph p) =>
        new TextRange(Box.CaretPosition, p.ContentEnd).Text.Trim('\r', '\n').Length == 0;

    /// <summary>Markdown typing shortcuts at the start of a paragraph: "# ", "- ", "1. ", "[] ", "> ".</summary>
    /// <summary>The block shortcut marker (including its trailing space) just typed at the start of the caret's paragraph.</summary>
    private string? BlockShortcutAt(TextPointer caret)
    {
        if (caret.Paragraph is not { } p || !Box.Selection.IsEmpty) return null;
        if (NoteDocument.InfoOf(p).Kind is BlockKind.CodeBlock or BlockKind.Raw || p.Parent is TableCell) return null;
        var typed = TextBefore(p, caret);
        if (typed.Length < 2 || typed.Length > 8 || typed[^1] != ' ' || typed[^2] == ' ') return null;
        return ShortcutAction(p, typed[..^1]) != null ? typed : null;
    }

    private Action<Paragraph>? ShortcutAction(Paragraph p, string marker)
    {
        bool inList = p.Parent is ListItem;
        if (Regex.IsMatch(marker, "^#{1,6}$") && !inList)
            return para => NoteDocument.Apply(para, new BlockInfo(BlockKind.Heading, marker.Length));
        if (marker is "-" or "*" or "+" && !inList)
            return _ => ToggleList(false);
        if (Regex.IsMatch(marker, @"^1[.)]$") && !inList)
            return _ => ToggleList(true);
        if (Regex.IsMatch(marker, @"^(-\s?)?\[\s?\]$") && NoteDocument.TaskState(p) == null)
            return _ => MakeTask(Box.CaretPosition.Paragraph!, false);
        if (Regex.IsMatch(marker, @"^(-\s?)?\[[xX]\]$") && NoteDocument.TaskState(p) == null)
            return _ => MakeTask(Box.CaretPosition.Paragraph!, true);
        if (marker == ">" && !inList)
            return _ => ToggleQuote();
        return null;
    }

    private void ApplyBlockShortcut(Paragraph p, string typed)
    {
        // The paragraph may have gained more text since the marker was typed; it must still start with it.
        if (p.Parent == null) return;
        var full = new TextRange(p.ContentStart, p.ContentEnd).Text;
        if (!full.StartsWith(typed, StringComparison.Ordinal)) return;
        var action = ShortcutAction(p, typed[..^1]);
        if (action == null) return;
        if (p.Inlines.FirstInline is not Run first || !first.Text.StartsWith(typed, StringComparison.Ordinal)) return;
        var end = first.ContentStart.GetPositionAtOffset(typed.Length);
        if (end == null) return;

        // TextPointers follow edits, so the caret keeps its logical place when the marker is removed.
        var caret = Box.CaretPosition;
        var caretInPara = caret.Paragraph == p;
        Edit(() => new TextRange(first.ContentStart, end).Text = "");
        // Commands act on the caret's paragraph, so make sure it's in p, then restore it.
        if (!caretInPara) Box.CaretPosition = p.ContentStart;
        Quiet(() => action(p));
        if (!caretInPara) Box.CaretPosition = caret;
        SyncBlockStyle();
    }

    private void HandleEnter(KeyEventArgs e)
    {
        var caret = Box.CaretPosition;
        if (caret.Paragraph is not { } p || !Box.Selection.IsEmpty) return;
        var info = NoteDocument.InfoOf(p);

        if (info.Kind == BlockKind.CodeBlock)
        {
            e.Handled = true;
            // Enter on an empty last line leaves the code block.
            if (CaretAtEnd(p) && p.Inlines.LastInline is LineBreak lb)
            {
                Edit(() =>
                {
                    p.Inlines.Remove(lb);
                    var np = NewParagraph();
                    p.SiblingBlocks.InsertAfter(p, np);
                    Box.CaretPosition = np.ContentStart;
                });
            }
            else Edit(() => EditingCommands.EnterLineBreak.Execute(null, Box));
            return;
        }

        var text = TextBefore(p, caret);
        if (info.Kind == BlockKind.Paragraph && p.Parent is not ListItem && CaretAtEnd(p))
        {
            if (Regex.IsMatch(text.Trim(), @"^(-{3,}|\*{3,}|_{3,})$"))
            {
                e.Handled = true;
                Edit(() =>
                {
                    new TextRange(p.ContentStart, p.ContentEnd).Text = "";
                    p.SiblingBlocks.InsertBefore(p, NoteDocument.NewRule());
                    Box.CaretPosition = p.ContentStart;
                });
                return;
            }
            var fence = Regex.Match(text.Trim(), @"^```\s*([\w+#.-]*)$");
            if (fence.Success)
            {
                e.Handled = true;
                Edit(() =>
                {
                    new TextRange(p.ContentStart, p.ContentEnd).Text = "";
                    NoteDocument.Apply(p, new BlockInfo(BlockKind.CodeBlock, Language: fence.Groups[1].Value.Length > 0 ? fence.Groups[1].Value : null));
                });
                SyncBlockStyle();
                return;
            }
        }

        bool? task = NoteDocument.TaskState(p);
        if (task != null && IsEmpty(p))
        {
            // Enter on an empty checklist item ends the list.
            e.Handled = true;
            if (p.Parent is ListItem emptyItem) Edit(() => ExitList(emptyItem));
            return;
        }

        if (CaretAtEnd(p) && info.Kind == BlockKind.Heading)
        {
            // Enter at the end of a heading continues with normal text.
            e.Handled = true;
            Edit(() =>
            {
                var np = NewParagraph();
                p.SiblingBlocks.InsertAfter(p, np);
                Box.CaretPosition = np.ContentStart;
            });
            SyncBlockStyle();
            return;
        }

        if (CaretAtEnd(p) && task != null && p.Parent is ListItem currentItem)
        {
            // Enter at the end of a checklist item starts a new unchecked item.
            e.Handled = true;
            Edit(() =>
            {
                var np = NewParagraph();
                np.Margin = new Thickness(0, 0, 0, 2);
                var item = new ListItem(np);
                currentItem.SiblingListItems.InsertAfter(currentItem, item);
                MakeTask(np, false);
            });
            return;
        }

        if (p.Parent is Section quote && NoteDocument.InfoOf(quote).Kind == BlockKind.Quote && IsEmpty(p) && quote.Blocks.LastBlock == p)
        {
            // Enter on an empty last line of a quote leaves the quote.
            e.Handled = true;
            Edit(() =>
            {
                quote.Blocks.Remove(p);
                var np = NewParagraph();
                quote.SiblingBlocks.InsertAfter(quote, np);
                if (quote.Blocks.Count == 0) quote.SiblingBlocks.Remove(quote);
                Box.CaretPosition = np.ContentStart;
            });
            return;
        }

        // Let the RichTextBox split the paragraph, then fix up the new one.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (Box.CaretPosition.Paragraph is not { } np || np == p) return;
            Edit(() =>
            {
                if (info.Kind == BlockKind.Heading)
                {
                    if (IsEmpty(np)) NoteDocument.Apply(np, new BlockInfo(BlockKind.Paragraph));
                    else NoteDocument.Apply(np, info);
                }
                else if (task != null && np.Parent is ListItem && NoteDocument.TaskState(np) == null)
                {
                    MakeTask(np, false);
                }
                else if (np.Tag == null)
                {
                    NoteDocument.Apply(np, NoteDocument.InfoOf(np));
                    if (np.Parent is ListItem) np.Margin = new Thickness(0, 0, 0, 2);
                }
            });
            SyncBlockStyle();
        });
    }

    private void HandleBackspace(KeyEventArgs e)
    {
        var caret = Box.CaretPosition;
        if (caret.Paragraph is not { } p || !Box.Selection.IsEmpty) return;
        if (TextBefore(p, caret).Length > 0) return;

        if (NoteDocument.TaskState(p) != null && caret.CompareTo(p.Inlines.FirstInline.ElementEnd) >= 0)
        {
            e.Handled = true;
            Edit(() => p.Inlines.Remove(p.Inlines.FirstInline));
            return;
        }
        var kind = NoteDocument.InfoOf(p).Kind;
        if (kind is BlockKind.Heading or BlockKind.CodeBlock)
        {
            e.Handled = true;
            Edit(() => NoteDocument.Apply(p, new BlockInfo(BlockKind.Paragraph)));
            SyncBlockStyle();
            return;
        }
        if (p.Parent is Section q && NoteDocument.InfoOf(q).Kind == BlockKind.Quote && q.Blocks.FirstBlock == p)
        {
            e.Handled = true;
            Edit(ToggleQuote);
        }
    }

    // ---------- Formatting commands ----------

    private void SyncBlockStyle()
    {
        if (Box.CaretPosition.Paragraph is not { } p) return;
        var info = NoteDocument.InfoOf(p);
        var tag = info.Kind switch
        {
            BlockKind.Heading => Math.Clamp(info.Level, 1, 4).ToString(),
            BlockKind.CodeBlock => "code",
            _ => "0",
        };
        _syncingStyle = true;
        BlockStyle.SelectedItem = BlockStyle.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag);
        _syncingStyle = false;
    }

    private void BlockStyle_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingStyle || _loading || BlockStyle.SelectedItem is not ComboBoxItem item) return;
        var tag = (string)item.Tag;
        SetBlockKind(tag switch
        {
            "code" => new BlockInfo(BlockKind.CodeBlock),
            "0" => new BlockInfo(BlockKind.Paragraph),
            _ => new BlockInfo(BlockKind.Heading, int.Parse(tag)),
        });
        Box.Focus();
    }

    private IEnumerable<Paragraph> SelectedParagraphs()
    {
        var start = Box.Selection.Start.Paragraph;
        var end = Box.Selection.End.Paragraph;
        if (start == null) yield break;
        bool inside = false;
        foreach (var p in Descendants(Box.Document).OfType<Paragraph>().ToList())
        {
            if (p == start) inside = true;
            if (inside) yield return p;
            if (p == end || end == null) yield break;
        }
    }

    private void SetBlockKind(BlockInfo info)
    {
        Edit(() =>
        {
            foreach (var p in SelectedParagraphs().ToList())
            {
                if (p.Parent is TableCell) continue;
                if (info.Kind == BlockKind.Heading && p.Parent is ListItem) continue;
                NoteDocument.Apply(p, info);
                if (p.Parent is ListItem) p.Margin = new Thickness(0, 0, 0, 2);
            }
        });
        SyncBlockStyle();
        Changed?.Invoke();
    }

    private void ToggleStrike()
    {
        var sel = Box.Selection;
        bool has = sel.GetPropertyValue(Inline.TextDecorationsProperty) is TextDecorationCollection c &&
                   c.Any(d => d.Location == TextDecorationLocation.Strikethrough);
        Edit(() => sel.ApplyPropertyValue(Inline.TextDecorationsProperty, has ? new TextDecorationCollection() : TextDecorations.Strikethrough));
    }

    private void ToggleInlineCode()
    {
        var sel = Box.Selection;
        if (sel.Start.Paragraph is { } p && NoteDocument.InfoOf(p).Kind is BlockKind.CodeBlock or BlockKind.Raw) return;
        bool isCode = sel.GetPropertyValue(TextElement.FontFamilyProperty) is FontFamily f && NoteDocument.IsCodeFont(f);
        Edit(() =>
        {
            sel.ApplyPropertyValue(TextElement.FontFamilyProperty, isCode ? NoteDocument.BodyFont : NoteDocument.CodeFont);
            sel.ApplyPropertyValue(TextElement.BackgroundProperty, isCode ? null : NoteDocument.CodeBackground);
        });
    }

    private void StartWikiLink()
    {
        var sel = Box.Selection;
        if (!sel.IsEmpty && sel.Start.Paragraph == sel.End.Paragraph)
        {
            var text = sel.Text.Trim();
            var link = WikiLink.ParseInner(text);
            if (link != null)
            {
                InsertWikiLink(sel.Start, sel.End, link);
                return;
            }
        }
        Box.Selection.Text = "[[";
        Box.CaretPosition = Box.Selection.End;
        Box.Focus();
    }

    private void InsertExternalLink()
    {
        var sel = Box.Selection;
        var url = InputDialog.Show(Window.GetWindow(this), "Web link", "URL", "https://");
        if (string.IsNullOrWhiteSpace(url)) return;
        Edit(() =>
        {
            if (!sel.IsEmpty && sel.Start.Paragraph == sel.End.Paragraph)
            {
                var text = sel.Text;
                sel.Text = "";
                var h = NoteDocument.NewExternalLink(text, url.Trim());
                InsertInline(h, sel.Start);
            }
            else
            {
                var h = NoteDocument.NewExternalLink(url.Trim(), url.Trim());
                InsertInline(h, Box.CaretPosition);
            }
        });
    }

    private void InsertInline(Hyperlink h, TextPointer at)
    {
        var shell = new Hyperlink(new Run(new TextRange(h.ContentStart, h.ContentEnd).Text), at)
        {
            NavigateUri = h.NavigateUri,
            Tag = h.Tag,
            Foreground = h.Foreground,
            ToolTip = h.ToolTip,
        };
        PlaceCaretAfter(shell);
    }

    private void MakeTask(Paragraph p, bool done)
    {
        if (p.Parent is not ListItem)
        {
            Box.CaretPosition = p.ContentStart;
            ToggleList(false);
            p = Box.CaretPosition.Paragraph ?? p;
        }
        var box = NoteDocument.NewTaskBox(done);
        if (p.Inlines.FirstInline == null) p.Inlines.Add(box);
        else p.Inlines.InsertBefore(p.Inlines.FirstInline, box);
        p.Margin = new Thickness(0, 0, 0, 2);
        if (p.Parent is ListItem { Parent: List list } &&
            list.ListItems.All(li => li.Blocks.FirstBlock is Paragraph fp && NoteDocument.TaskState(fp) != null))
            list.MarkerStyle = TextMarkerStyle.None;
        Box.CaretPosition = box.ElementEnd;
    }

    /// <summary>Replaces an (empty) list item with a normal paragraph after the list, splitting the list if needed.</summary>
    private void ExitList(ListItem item)
    {
        if (item.Parent is not List list) return;
        var following = new List<ListItem>();
        for (var next = item.NextListItem; next != null; next = next.NextListItem) following.Add(next);
        list.ListItems.Remove(item);
        foreach (var f in following) list.ListItems.Remove(f);

        var para = NewParagraph();
        list.SiblingBlocks.InsertAfter(list, para);
        if (following.Count > 0)
        {
            var rest = NoteDocument.NewList(list.MarkerStyle == TextMarkerStyle.Decimal);
            rest.MarkerStyle = list.MarkerStyle;
            foreach (var f in following) rest.ListItems.Add(f);
            para.SiblingBlocks.InsertAfter(para, rest);
        }
        if (list.ListItems.Count == 0) list.SiblingBlocks.Remove(list);
        Box.CaretPosition = para.ContentStart;
    }

    private void ToggleTask()
    {
        if (Box.CaretPosition.Paragraph is not { } p) return;
        if (NoteDocument.TaskState(p) != null)
        {
            Edit(() =>
            {
                p.Inlines.Remove(p.Inlines.FirstInline);
                if (p.Parent is ListItem { Parent: List list }) list.MarkerStyle = TextMarkerStyle.Disc;
            });
        }
        else Quiet(() => MakeTask(p, false));
    }

    /// <summary>The outermost block containing the caret (below the document or a quote).</summary>
    private Block? TopBlock(out Section? enclosingQuote)
    {
        enclosingQuote = null;
        if (Box.CaretPosition.Paragraph is not { } p) return null;
        Block top = p;
        DependencyObject? o = p;
        while (o is FrameworkContentElement fce)
        {
            var parent = fce.Parent;
            if (parent is FlowDocument) break;
            if (parent is Section s && NoteDocument.InfoOf(s).Kind == BlockKind.Quote)
            {
                enclosingQuote = s;
                break;
            }
            if (parent is Block b) top = b;
            o = parent;
        }
        return top;
    }

    private void ToggleQuote()
    {
        var top = TopBlock(out var quote);
        var caretPara = Box.CaretPosition.Paragraph;
        if (top == null) return;
        if (quote != null)
        {
            var siblings = quote.SiblingBlocks!;
            foreach (var b in quote.Blocks.ToList())
            {
                quote.Blocks.Remove(b);
                siblings.InsertBefore(quote, b);
            }
            siblings.Remove(quote);
        }
        else
        {
            var siblings = top.SiblingBlocks!;
            var section = NoteDocument.NewQuote();
            siblings.InsertBefore(top, section);
            siblings.Remove(top);
            section.Blocks.Add(top);
        }
        if (caretPara != null) Box.CaretPosition = caretPara.ContentEnd;
        Changed?.Invoke();
    }

    private void InsertRule()
    {
        var top = TopBlock(out _);
        if (top == null) return;
        Edit(() =>
        {
            var rule = NoteDocument.NewRule();
            top.SiblingBlocks.InsertAfter(top, rule);
            var np = NewParagraph();
            top.SiblingBlocks.InsertAfter(rule, np);
            Box.CaretPosition = np.ContentStart;
        });
    }

    // ---------- Paste ----------

    private void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (Box.IsReadOnly)
        {
            e.CancelCommand();
            return;
        }
        var data = e.SourceDataObject;
        if (!data.GetDataPresent(DataFormats.UnicodeText)) return;
        var text = data.GetData(DataFormats.UnicodeText) as string ?? "";
        var fromWpf = data.GetDataPresent(DataFormats.Xaml);
        if (fromWpf)
        {
            // Copied from a note: keep the formatting, then restore link metadata.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => RefreshLinks(_resolver));
            return;
        }

        e.CancelCommand();
        var caret = Box.CaretPosition;
        if (InPlainTextBlock(caret) || !MarkdownHint.IsMatch(text))
        {
            Edit(() =>
            {
                Box.Selection.Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
                Box.CaretPosition = Box.Selection.End;
            });
            return;
        }

        // Markdown text (e.g. from another note or Obsidian): paste it formatted.
        var doc = new MarkdownToFlow(_resolver).Convert(text);
        using var ms = new MemoryStream();
        new TextRange(doc.ContentStart, doc.ContentEnd).Save(ms, DataFormats.Xaml);
        ms.Position = 0;
        Edit(() =>
        {
            Box.Selection.Load(ms, DataFormats.Xaml);
            Box.CaretPosition = Box.Selection.End;
        });
        RefreshLinks(_resolver);
    }

    // ---------- Toolbar ----------

    private void Bold_Click(object sender, RoutedEventArgs e) { EditingCommands.ToggleBold.Execute(null, Box); Box.Focus(); }
    private void Italic_Click(object sender, RoutedEventArgs e) { EditingCommands.ToggleItalic.Execute(null, Box); Box.Focus(); }
    private void Strike_Click(object sender, RoutedEventArgs e) { ToggleStrike(); Box.Focus(); }
    private void Code_Click(object sender, RoutedEventArgs e) { ToggleInlineCode(); Box.Focus(); }
    private void WikiLink_Click(object sender, RoutedEventArgs e) => StartWikiLink();
    private void ExternalLink_Click(object sender, RoutedEventArgs e) { InsertExternalLink(); Box.Focus(); }
    private void Bullets_Click(object sender, RoutedEventArgs e) { ToggleList(false); Box.Focus(); }
    private void Numbers_Click(object sender, RoutedEventArgs e) { ToggleList(true); Box.Focus(); }
    private void Task_Click(object sender, RoutedEventArgs e) { ToggleTask(); Box.Focus(); }
    private void Quote_Click(object sender, RoutedEventArgs e) { Edit(ToggleQuote); Box.Focus(); }
    private void Rule_Click(object sender, RoutedEventArgs e) { InsertRule(); Box.Focus(); }
}
