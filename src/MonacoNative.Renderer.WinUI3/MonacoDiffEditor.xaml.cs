using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MonacoNative.Core;
using MonacoNative.I18n;
using Windows.UI;

namespace MonacoNative.Renderer.WinUI3;

/// <summary>
/// Native DiffEditor. The original/modified source of truth remains native
/// MonacoTextModel instances; no browser DOM, WebView, or JavaScript diff runtime is used.
/// </summary>
public sealed partial class MonacoDiffEditor : UserControl, IDisposable
{
    private readonly MonacoLocalizationService _localization = new();
    private MonacoDiffResult _diff = new(Array.Empty<MonacoDiffChange>(), Array.Empty<MonacoDiffMove>());
    private int _diffGeneration;
    private int _currentChangeIndex = -1;
    private bool _renderSideBySide = true;
    private bool _synchronizedScrolling = true;
    private bool _hideUnchangedRegions;
    private bool _ignoreTrimWhitespace = true;
    private bool _showMoves = true;
    private bool _syncingScroll;
    private bool _applyingDiff;
    private bool _disposed;
    private string _languageId = "plaintext";
    private string _originalTitle = string.Empty;
    private string _modifiedTitle = string.Empty;
    private readonly List<InlineDeletedBlock> _inlineDeletedBlocks = new();

    private sealed record InlineDeletedBlock(MonacoDiffChange Change, ulong ZoneId, IReadOnlyList<string> Lines);

    public MonacoDiffEditor()
    {
        InitializeComponent();

        // A diff owns its three source buffers. Workspace lifecycle shortcuts must
        // not replace one of those buffers with a standalone untitled document.
        foreach (var editor in new[] { OriginalEditor, ModifiedEditor, InlineEditor })
        foreach (var command in new[]
        {
            "workbench.action.files.newUntitledFile", "workbench.action.files.save",
            "workbench.action.closeActiveEditor", "workbench.action.nextEditor", "workbench.action.previousEditor",
            "workbench.action.reopenClosedEditor", "workbench.action.files.saveAs", "workbench.action.closeEditorsInGroup",
            "workbench.action.closeUnmodifiedEditors", "workbench.action.keepEditor", "workbench.action.splitEditor",
        })
            editor.ContextMenuItemOptions.Add(new EditorContextMenuItemOptions
            { CommandId = command, Mode = EditorCommandAvailability.Disabled });

        OriginalEditor.IsReadOnly = true;
        OriginalEditor.WordWrap = false;
        ModifiedEditor.WordWrap = false;
        InlineEditor.WordWrap = false;
        InlineEditor.IsReadOnly = true;

        OriginalEditor.ShowMinimap = false;
        ModifiedEditor.ShowMinimap = false;
        InlineEditor.ShowMinimap = false;
        LanguageId = _languageId;

        OriginalEditor.ModelChanged += SourceEditor_ModelChanged;
        ModifiedEditor.ModelChanged += SourceEditor_ModelChanged;
        OriginalEditor.ViewportChanged += OriginalEditor_ViewportChanged;
        ModifiedEditor.ViewportChanged += ModifiedEditor_ViewportChanged;
        InlineEditor.ViewportChanged += InlineEditor_ViewportChanged;
        _localization.CultureChanged += Localization_CultureChanged;

        ApplyLocalization();
        ApplyViewMode();
    }

    internal void ConfigureRefactorPreview()
    {
        DiffRoot.BorderThickness = new Thickness(0);
        DiffRoot.CornerRadius = new CornerRadius(0);
        DiffToolbar.Visibility = Visibility.Collapsed;
        SideBySideRoot.ColumnDefinitions[1].Width = new GridLength(1);
        RevertGutter.Visibility = Visibility.Collapsed;
        InlineActionOverlay.Visibility = Visibility.Collapsed;
        foreach (var editor in new[] { OriginalEditor, ModifiedEditor, InlineEditor })
        {
            editor.IsReadOnly = true;
            editor.ShowOverviewRuler = false;
            editor.SetEmbeddedPreviewChrome();
        }
        IgnoreTrimWhitespace = false;
    }

    public event EventHandler? DiffUpdated;
    public event EventHandler? CurrentChangeChanged;
    public event EventHandler? ModifiedTextChanged;

    public MonacoEditor OriginalEditorControl => OriginalEditor;
    public MonacoEditor ModifiedEditorControl => ModifiedEditor;
    public MonacoDiffResult DiffResult => _diff;

    public string OriginalText
    {
        get => OriginalEditor.Text;
        set => OriginalEditor.Text = value ?? string.Empty;
    }

    public string ModifiedText
    {
        get => ModifiedEditor.Text;
        set => ModifiedEditor.Text = value ?? string.Empty;
    }

    public string LanguageId
    {
        get => _languageId;
        set
        {
            _languageId = string.IsNullOrWhiteSpace(value) ? "plaintext" : value;
            OriginalEditor.LanguageId = _languageId;
            ModifiedEditor.LanguageId = _languageId;
            InlineEditor.LanguageId = _languageId;
        }
    }

    public string UiLanguage
    {
        get => _localization.CultureName;
        set => _localization.SetCulture(value);
    }

    public IReadOnlyList<string> SupportedUiLanguages => MonacoLocalizationService.SupportedCultures;

    public string OriginalTitle
    {
        get => string.IsNullOrWhiteSpace(_originalTitle) ? _localization.GetString("monacoNative.diff.original") : _originalTitle;
        set { _originalTitle = value ?? string.Empty; ApplyLocalization(); }
    }

    public string ModifiedTitle
    {
        get => string.IsNullOrWhiteSpace(_modifiedTitle) ? _localization.GetString("monacoNative.diff.modified") : _modifiedTitle;
        set { _modifiedTitle = value ?? string.Empty; ApplyLocalization(); }
    }

    public bool OriginalEditable
    {
        get => !OriginalEditor.IsReadOnly;
        set => OriginalEditor.IsReadOnly = !value;
    }

    public bool RenderSideBySide
    {
        get => _renderSideBySide;
        set
        {
            if (_renderSideBySide == value) return;
            _renderSideBySide = value;
            ApplyViewMode();
            ApplyDiffVisuals();
        }
    }

    public bool SynchronizedScrolling
    {
        get => _synchronizedScrolling;
        set => _synchronizedScrolling = value;
    }

    public bool HideUnchangedRegions
    {
        get => _hideUnchangedRegions;
        set
        {
            if (_hideUnchangedRegions == value) return;
            _hideUnchangedRegions = value;
            HideUnchangedCheckBox.IsChecked = value;
            ApplyDiffVisuals();
        }
    }

    public int HideUnchangedContextLineCount { get; set; } = 3;
    public int HideUnchangedMinimumLineCount { get; set; } = 8;

    public bool IgnoreTrimWhitespace
    {
        get => _ignoreTrimWhitespace;
        set { if (_ignoreTrimWhitespace == value) return; _ignoreTrimWhitespace = value; QueueDiffRefresh(); }
    }

    public bool ShowMoves
    {
        get => _showMoves;
        set { if (_showMoves == value) return; _showMoves = value; QueueDiffRefresh(); }
    }

    public int CurrentChangeIndex => _currentChangeIndex;

    public void RefreshDiff() => QueueDiffRefresh();

    public void NextChange() => NavigateChange(+1);
    public void PreviousChange() => NavigateChange(-1);

    public bool RevertCurrentChange()
    {
        if (_currentChangeIndex < 0 || _currentChangeIndex >= _diff.Changes.Count)
            return false;
        return RevertChange(_currentChangeIndex);
    }

    public bool RevertChange(int changeIndex)
    {
        if (changeIndex < 0 || changeIndex >= _diff.Changes.Count || ModifiedEditor.IsReadOnly)
            return false;

        var change = _diff.Changes[changeIndex];
        var originalLines = SplitLogicalLines(OriginalEditor.Text);
        var modifiedLines = SplitLogicalLines(ModifiedEditor.Text);
        var originalStart = Math.Clamp(change.OriginalStartLineNumber - 1, 0, originalLines.Count);
        var originalCount = Math.Clamp(change.OriginalLineCount, 0, originalLines.Count - originalStart);
        var modifiedStart = Math.Clamp(change.ModifiedStartLineNumber - 1, 0, modifiedLines.Count);
        var modifiedCount = Math.Clamp(change.ModifiedLineCount, 0, modifiedLines.Count - modifiedStart);

        var replacementLines = originalLines.GetRange(originalStart, originalCount);
        modifiedLines.RemoveRange(modifiedStart, modifiedCount);
        modifiedLines.InsertRange(modifiedStart, replacementLines);
        var eol = ModifiedEditor.EndOfLine == MonacoEndOfLine.CrLf ? "\r\n" : "\n";
        var target = string.Join(eol, modifiedLines);
        ApplyMinimalReplacement(ModifiedEditor, target);
        return true;
    }

    private void DiffEditor_Loaded(object sender, RoutedEventArgs e)
    {
        QueueDiffRefresh();
    }

    private void DiffEditor_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateRevertControls();
        UpdateInlineDeletedOverlay();
    }

    private void SourceEditor_ModelChanged(object? sender, EventArgs e)
    {
        if (_applyingDiff || _disposed)
            return;
        if (ReferenceEquals(sender, ModifiedEditor))
            ModifiedTextChanged?.Invoke(this, EventArgs.Empty);
        QueueDiffRefresh();
    }

    private void QueueDiffRefresh()
    {
        if (_disposed)
            return;
        var original = OriginalEditor.Text;
        var modified = ModifiedEditor.Text;
        var options = new MonacoDiffOptions(_ignoreTrimWhitespace, _showMoves);
        var generation = Interlocked.Increment(ref _diffGeneration);

        _ = Task.Run(() => MonacoDiffService.Compute(original, modified, options)).ContinueWith(task =>
        {
            if (task.IsCanceled || task.IsFaulted || _disposed)
                return;
            _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
            {
                if (_disposed || generation != _diffGeneration)
                    return;
                _diff = task.Result;
                if (_currentChangeIndex >= _diff.Changes.Count)
                    _currentChangeIndex = _diff.Changes.Count - 1;
                ApplyDiffVisuals();
                DiffUpdated?.Invoke(this, EventArgs.Empty);
            });
        }, TaskScheduler.Default);
    }

    private void ApplyDiffVisuals()
    {
        if (_disposed)
            return;

        _applyingDiff = true;
        try
        {
            InlineEditor.Text = ModifiedEditor.Text;
            InlineEditor.LanguageId = ModifiedEditor.LanguageId;

            OriginalEditor.ViewModel.ClearViewZones();
            ModifiedEditor.ViewModel.ClearViewZones();
            InlineEditor.ViewModel.ClearViewZones();
            _inlineDeletedBlocks.Clear();

            ApplyHiddenAreas();
            ApplyDecorations();
            AddSideBySideAlignmentZones();
            AddInlineDeletedZones();

            OriginalEditor.RefreshProjection();
            ModifiedEditor.RefreshProjection();
            InlineEditor.RefreshProjection();
            UpdateSummary();
            UpdateRevertControls();
            UpdateInlineDeletedOverlay();
            UpdateAccessibilityAnnotations();
        }
        finally
        {
            _applyingDiff = false;
        }
    }

    private void ApplyDecorations()
    {
        var originalLines = new List<MonacoDiffLineDecoration>();
        var modifiedLines = new List<MonacoDiffLineDecoration>();
        var originalText = new List<MonacoDiffTextDecoration>();
        var modifiedText = new List<MonacoDiffTextDecoration>();

        foreach (var change in _diff.Changes)
        {
            if (change.OriginalLineCount > 0)
                originalLines.Add(new(change.OriginalStartLineNumber, change.OriginalEndLineNumberExclusive, MonacoDiffDecorationKind.Deleted));
            if (change.ModifiedLineCount > 0)
                modifiedLines.Add(new(change.ModifiedStartLineNumber, change.ModifiedEndLineNumberExclusive, MonacoDiffDecorationKind.Inserted));

            foreach (var inner in change.InnerChanges)
            {
                if (inner.OriginalEndColumn >= inner.OriginalStartColumn)
                    originalText.Add(new(inner.OriginalRange, MonacoDiffDecorationKind.Deleted));
                if (inner.ModifiedEndColumn >= inner.ModifiedStartColumn)
                    modifiedText.Add(new(inner.ModifiedRange, MonacoDiffDecorationKind.Inserted));
            }
        }

        if (_showMoves)
        {
            foreach (var move in _diff.Moves)
            {
                originalLines.Add(new(move.OriginalStartLineNumber, move.OriginalEndLineNumberExclusive, MonacoDiffDecorationKind.Moved));
                modifiedLines.Add(new(move.ModifiedStartLineNumber, move.ModifiedEndLineNumberExclusive, MonacoDiffDecorationKind.Moved));
            }
        }

        OriginalEditor.SetDiffDecorations(originalLines, originalText);
        ModifiedEditor.SetDiffDecorations(modifiedLines, modifiedText);
        InlineEditor.SetDiffDecorations(modifiedLines, modifiedText);
    }

    private void ApplyHiddenAreas()
    {
        if (!_hideUnchangedRegions)
        {
            OriginalEditor.ViewModel.SetHiddenAreas(null);
            ModifiedEditor.ViewModel.SetHiddenAreas(null);
            InlineEditor.ViewModel.SetHiddenAreas(null);
            return;
        }

        var originalAreas = BuildHiddenAreas(
            checked((int)OriginalEditor.LineCount),
            _diff.Changes.Select(c => (c.OriginalStartLineNumber, c.OriginalEndLineNumberExclusive)));
        var modifiedAreas = BuildHiddenAreas(
            checked((int)ModifiedEditor.LineCount),
            _diff.Changes.Select(c => (c.ModifiedStartLineNumber, c.ModifiedEndLineNumberExclusive)));
        OriginalEditor.ViewModel.SetHiddenAreas(originalAreas);
        ModifiedEditor.ViewModel.SetHiddenAreas(modifiedAreas);
        InlineEditor.ViewModel.SetHiddenAreas(modifiedAreas);
    }

    private IReadOnlyList<MonacoHiddenArea> BuildHiddenAreas(
        int lineCount,
        IEnumerable<(int Start, int EndExclusive)> changedRanges)
    {
        if (lineCount <= 0)
            return Array.Empty<MonacoHiddenArea>();

        var context = Math.Max(0, HideUnchangedContextLineCount);
        var minimum = Math.Max((context * 2) + 1, HideUnchangedMinimumLineCount);
        var points = changedRanges
            .Select(r => (Start: Math.Clamp(r.Start, 1, lineCount + 1), End: Math.Clamp(r.EndExclusive, 1, lineCount + 1)))
            .OrderBy(r => r.Start)
            .ThenBy(r => r.End)
            .ToArray();

        var hidden = new List<MonacoHiddenArea>();
        var cursor = 1;
        foreach (var range in points)
        {
            var gapEnd = Math.Min(lineCount, range.Start - 1);
            AddHiddenGap(hidden, cursor, gapEnd, context, minimum);
            cursor = Math.Max(cursor, Math.Max(range.Start, range.End));
        }
        AddHiddenGap(hidden, cursor, lineCount, context, minimum);
        return hidden;
    }

    private static void AddHiddenGap(
        List<MonacoHiddenArea> hidden,
        int gapStart,
        int gapEnd,
        int context,
        int minimum)
    {
        if (gapEnd < gapStart || (gapEnd - gapStart + 1) < minimum)
            return;
        var start = gapStart + context;
        var end = gapEnd - context;
        if (start <= end)
            hidden.Add(new MonacoHiddenArea(start, end));
    }

    private void AddSideBySideAlignmentZones()
    {
        foreach (var change in _diff.Changes)
        {
            var originalCount = change.OriginalLineCount;
            var modifiedCount = change.ModifiedLineCount;
            if (originalCount == modifiedCount)
                continue;
            if (originalCount < modifiedCount)
            {
                var afterLine = Math.Clamp(change.OriginalEndLineNumberExclusive - 1, 0, checked((int)OriginalEditor.LineCount));
                OriginalEditor.ViewModel.AddViewZone(afterLine, (modifiedCount - originalCount) * OriginalEditor.LineHeight);
            }
            else
            {
                var afterLine = Math.Clamp(change.ModifiedEndLineNumberExclusive - 1, 0, checked((int)ModifiedEditor.LineCount));
                ModifiedEditor.ViewModel.AddViewZone(afterLine, (originalCount - modifiedCount) * ModifiedEditor.LineHeight);
            }
        }
    }

    private void AddInlineDeletedZones()
    {
        var originalLines = SplitLogicalLines(OriginalEditor.Text);
        foreach (var change in _diff.Changes)
        {
            if (change.OriginalLineCount <= 0)
                continue;
            var start = Math.Clamp(change.OriginalStartLineNumber - 1, 0, originalLines.Count);
            var count = Math.Clamp(change.OriginalLineCount, 0, originalLines.Count - start);
            var lines = originalLines.GetRange(start, count);
            var afterLine = Math.Clamp(change.ModifiedStartLineNumber - 1, 0, checked((int)InlineEditor.LineCount));
            var zone = InlineEditor.ViewModel.AddViewZone(afterLine, Math.Max(1, count) * InlineEditor.LineHeight);
            _inlineDeletedBlocks.Add(new InlineDeletedBlock(change, zone, lines));
        }
    }

    private void OriginalEditor_ViewportChanged(object? sender, EventArgs e)
    {
        if (_synchronizedScrolling && !_syncingScroll && _renderSideBySide)
            SynchronizeViewport(OriginalEditor, ModifiedEditor, originalToModified: true);
        UpdateRevertControls();
    }

    private void ModifiedEditor_ViewportChanged(object? sender, EventArgs e)
    {
        if (_synchronizedScrolling && !_syncingScroll && _renderSideBySide)
            SynchronizeViewport(ModifiedEditor, OriginalEditor, originalToModified: false);
        UpdateRevertControls();
    }

    private void InlineEditor_ViewportChanged(object? sender, EventArgs e)
    {
        UpdateInlineDeletedOverlay();
        UpdateInlineActionControls();
    }

    private void SynchronizeViewport(MonacoEditor source, MonacoEditor target, bool originalToModified)
    {
        if (source.ViewModel.ViewLineCount <= 0 || target.ViewModel.ViewLineCount <= 0)
            return;
        try
        {
            _syncingScroll = true;
            var sourceViewLine = source.ViewModel.GetViewLineAtVerticalOffset(Math.Max(0, source.VerticalScrollOffset));
            var sourceLine = source.ViewModel.GetViewLine(sourceViewLine).ModelLineNumber;
            var targetLine = MapLine(sourceLine, originalToModified);
            targetLine = Math.Clamp(targetLine, 1, checked((int)target.LineCount));
            var targetView = target.ViewModel.ModelToView(new MonacoPosition(targetLine, 1));
            var targetOffset = target.ViewModel.GetViewLine(targetView.LineNumber).VerticalOffset;
            target.SetScrollPosition(source.HorizontalScrollOffset, targetOffset, true);
        }
        finally
        {
            _syncingScroll = false;
        }
    }

    private int MapLine(int line, bool originalToModified)
    {
        var sourceCursor = 1;
        var targetCursor = 1;
        foreach (var change in _diff.Changes)
        {
            var sourceStart = originalToModified ? change.OriginalStartLineNumber : change.ModifiedStartLineNumber;
            var sourceEnd = originalToModified ? change.OriginalEndLineNumberExclusive : change.ModifiedEndLineNumberExclusive;
            var targetStart = originalToModified ? change.ModifiedStartLineNumber : change.OriginalStartLineNumber;
            var targetEnd = originalToModified ? change.ModifiedEndLineNumberExclusive : change.OriginalEndLineNumberExclusive;

            if (line < sourceStart)
                return targetCursor + (line - sourceCursor);
            if (line < sourceEnd || (sourceStart == sourceEnd && line == sourceStart))
            {
                var sourceLength = Math.Max(1, sourceEnd - sourceStart);
                var targetLength = Math.Max(1, targetEnd - targetStart);
                var relative = Math.Clamp(line - sourceStart, 0, sourceLength - 1);
                return targetStart + Math.Min(targetLength - 1, (relative * targetLength) / sourceLength);
            }
            sourceCursor = sourceEnd;
            targetCursor = targetEnd;
        }
        return targetCursor + (line - sourceCursor);
    }

    private void NavigateChange(int delta)
    {
        if (_diff.Changes.Count == 0)
            return;
        if (_currentChangeIndex < 0)
            _currentChangeIndex = delta >= 0 ? 0 : _diff.Changes.Count - 1;
        else
            _currentChangeIndex = (_currentChangeIndex + delta + _diff.Changes.Count) % _diff.Changes.Count;

        var change = _diff.Changes[_currentChangeIndex];
        if (_renderSideBySide)
        {
            RevealChange(OriginalEditor, change.OriginalStartLineNumber, change.OriginalEndLineNumberExclusive);
            RevealChange(ModifiedEditor, change.ModifiedStartLineNumber, change.ModifiedEndLineNumberExclusive);
        }
        else
        {
            RevealChange(InlineEditor, change.ModifiedStartLineNumber, change.ModifiedEndLineNumberExclusive);
        }
        UpdateSummary();
        UpdateAccessibilityAnnotations();
        CurrentChangeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void RevealChange(MonacoEditor editor, int startLine, int endLineExclusive)
    {
        var lineCount = checked((int)editor.LineCount);
        var line = Math.Clamp(startLine, 1, Math.Max(1, lineCount));
        var endLine = Math.Clamp(Math.Max(line, endLineExclusive - 1), 1, Math.Max(1, lineCount));
        editor.RevealRange(new MonacoRange(line, 1, endLine, editor.GetLineMaxColumn(endLine)), MonacoRevealStrategy.Center, editor.LineHeight);
    }

    private void UpdateRevertControls()
    {
        RevertGutter.Children.Clear();
        if (!_renderSideBySide || _diff.Changes.Count == 0 || ModifiedEditor.IsReadOnly)
            return;

        for (var i = 0; i < _diff.Changes.Count; i++)
        {
            var change = _diff.Changes[i];
            var y = GetChangeScreenY(ModifiedEditor, change.ModifiedStartLineNumber, change.ModifiedEndLineNumberExclusive);
            if (y < -30 || y > ModifiedEditor.ActualHeight + 30)
                continue;
            var index = i;
            var button = new Button
            {
                Content = "↶",
                Width = 28,
                Height = 24,
                Padding = new Thickness(0),
                Tag = index,
            };
            AutomationProperties.SetName(button, _localization.GetString("monacoNative.diff.revertChange"));
            button.Click += (_, _) => RevertChange(index);
            Canvas.SetLeft(button, 4);
            Canvas.SetTop(button, Math.Max(0, y));
            RevertGutter.Children.Add(button);
        }
    }

    private double GetChangeScreenY(MonacoEditor editor, int startLine, int endLineExclusive)
    {
        var lineCount = checked((int)editor.LineCount);
        if (lineCount <= 0)
            return 0;
        var line = startLine > lineCount ? lineCount : Math.Clamp(startLine, 1, lineCount);
        var view = editor.ViewModel.ModelToView(new MonacoPosition(line, 1));
        var offset = editor.ViewModel.GetViewLine(view.LineNumber).VerticalOffset;
        if (startLine > lineCount)
            offset += editor.LineHeight;
        return editor.ContentVerticalPadding + offset - editor.VerticalScrollOffset;
    }

    private void UpdateInlineDeletedOverlay()
    {
        InlineDeletedOverlay.Children.Clear();
        if (_renderSideBySide || _inlineDeletedBlocks.Count == 0)
            return;

        foreach (var block in _inlineDeletedBlocks)
        {
            var zoneHeight = Math.Max(1, block.Lines.Count) * InlineEditor.LineHeight;
            var zoneTop = GetInlineZoneTop(block.Change.ModifiedStartLineNumber, zoneHeight);
            var screenTop = InlineEditor.ContentVerticalPadding + zoneTop - InlineEditor.VerticalScrollOffset;
            for (var i = 0; i < block.Lines.Count; i++)
            {
                var y = screenTop + (i * InlineEditor.LineHeight);
                if (y + InlineEditor.LineHeight < 0 || y > InlineEditor.ActualHeight)
                    continue;
                var lineNumber = block.Change.OriginalStartLineNumber + i;
                var text = new TextBlock
                {
                    Text = $"{lineNumber,5}  {block.Lines[i]}",
                    FontFamily = new FontFamily("Cascadia Mono"),
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 212, 212, 212)),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                var row = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(66, 248, 81, 73)),
                    Height = InlineEditor.LineHeight,
                    Width = Math.Max(1, InlineEditor.ActualWidth),
                    Padding = new Thickness(6, 0, 4, 0),
                    Child = text,
                };
                AutomationProperties.SetName(row, string.Format(
                    _localization.GetString("monacoNative.diff.deletedLine"), lineNumber, block.Lines[i]));
                Canvas.SetLeft(row, 0);
                Canvas.SetTop(row, y);
                InlineDeletedOverlay.Children.Add(row);
            }
        }
    }

    private void UpdateInlineActionControls()
    {
        InlineActionOverlay.Children.Clear();
        if (_renderSideBySide || ModifiedEditor.IsReadOnly)
            return;
        for (var i = 0; i < _diff.Changes.Count; i++)
        {
            var change = _diff.Changes[i];
            var y = GetChangeScreenY(InlineEditor, change.ModifiedStartLineNumber, change.ModifiedEndLineNumberExclusive);
            if (y < -30 || y > InlineEditor.ActualHeight + 30)
                continue;
            var index = i;
            var button = new Button { Content = "↶", Width = 28, Height = 24, Padding = new Thickness(0) };
            AutomationProperties.SetName(button, _localization.GetString("monacoNative.diff.revertChange"));
            button.Click += (_, _) => RevertChange(index);
            Canvas.SetLeft(button, Math.Max(0, InlineEditor.ActualWidth - 42));
            Canvas.SetTop(button, Math.Max(0, y));
            InlineActionOverlay.Children.Add(button);
        }
    }

    private double GetInlineZoneTop(int modifiedStartLine, double zoneHeight)
    {
        var lineCount = checked((int)InlineEditor.LineCount);
        if (modifiedStartLine <= lineCount)
        {
            var view = InlineEditor.ViewModel.ModelToView(new MonacoPosition(Math.Max(1, modifiedStartLine), 1));
            return Math.Max(0, InlineEditor.ViewModel.GetViewLine(view.LineNumber).VerticalOffset - zoneHeight);
        }
        return Math.Max(0, InlineEditor.ViewModel.TotalHeight - zoneHeight);
    }

    private void UpdateSummary()
    {
        var changeCount = _diff.Changes.Count;
        var moveCount = _diff.Moves.Count;
        var summary = changeCount == 0
            ? _localization.GetString("monacoNative.diff.identical")
            : string.Format(_localization.GetString("monacoNative.diff.summary"), changeCount, moveCount);
        if (_currentChangeIndex >= 0 && _currentChangeIndex < changeCount)
            summary += " · " + string.Format(_localization.GetString("monacoNative.diff.currentChange"), _currentChangeIndex + 1, changeCount);
        SummaryText.Text = summary;
        RevertCurrentButton.IsEnabled = _currentChangeIndex >= 0 && _currentChangeIndex < changeCount && !ModifiedEditor.IsReadOnly;
        PreviousChangeButton.IsEnabled = changeCount > 0;
        NextChangeButton.IsEnabled = changeCount > 0;
    }

    private void UpdateAccessibilityAnnotations()
    {
        var summary = SummaryText.Text;
        AutomationProperties.SetName(DiffRoot, _localization.GetString("monacoNative.diff.accessibility.name"));
        AutomationProperties.SetHelpText(DiffRoot, summary);
        AutomationProperties.SetName(OriginalEditor, _localization.GetString("monacoNative.diff.accessibility.original"));
        AutomationProperties.SetName(ModifiedEditor, _localization.GetString("monacoNative.diff.accessibility.modified"));
        AutomationProperties.SetName(InlineEditor, _localization.GetString("monacoNative.diff.accessibility.inline"));
        AutomationProperties.SetHelpText(OriginalEditor, summary);
        AutomationProperties.SetHelpText(ModifiedEditor, summary);
        AutomationProperties.SetHelpText(InlineEditor, summary);
    }

    private void ApplyViewMode()
    {
        SideBySideRoot.Visibility = _renderSideBySide ? Visibility.Visible : Visibility.Collapsed;
        InlineRoot.Visibility = _renderSideBySide ? Visibility.Collapsed : Visibility.Visible;
        LayoutToggle.IsChecked = !_renderSideBySide;
        LayoutToggle.Content = _localization.GetString(_renderSideBySide
            ? "monacoNative.diff.inlineView"
            : "monacoNative.diff.sideBySideView");
        UpdateRevertControls();
        UpdateInlineDeletedOverlay();
        UpdateInlineActionControls();
    }

    private void ApplyLocalization()
    {
        OriginalEditor.UiLanguage = _localization.CultureName;
        ModifiedEditor.UiLanguage = _localization.CultureName;
        InlineEditor.UiLanguage = _localization.CultureName;
        OriginalHeader.Text = OriginalTitle;
        ModifiedHeader.Text = ModifiedTitle;
        PreviousChangeButton.Content = "↑";
        NextChangeButton.Content = "↓";
        AutomationProperties.SetName(PreviousChangeButton, _localization.GetString("monacoNative.diff.previousChange"));
        AutomationProperties.SetName(NextChangeButton, _localization.GetString("monacoNative.diff.nextChange"));
        AutomationProperties.SetName(RevertCurrentButton, _localization.GetString("monacoNative.diff.revertChange"));
        HideUnchangedCheckBox.Content = _localization.GetString("monacoNative.diff.hideUnchanged");
        AutomationProperties.SetName(LayoutToggle, _localization.GetString("monacoNative.diff.toggleView"));
        ApplyViewMode();
        UpdateSummary();
        UpdateAccessibilityAnnotations();
    }

    private void Localization_CultureChanged(object? sender, EventArgs e) => ApplyLocalization();

    private void PreviousChange_Click(object sender, RoutedEventArgs e) => PreviousChange();
    private void NextChange_Click(object sender, RoutedEventArgs e) => NextChange();
    private void RevertCurrent_Click(object sender, RoutedEventArgs e) => RevertCurrentChange();
    private void LayoutToggle_Click(object sender, RoutedEventArgs e) => RenderSideBySide = LayoutToggle.IsChecked != true;

    private void HideUnchanged_Changed(object sender, RoutedEventArgs e)
    {
        var value = HideUnchangedCheckBox.IsChecked == true;
        if (_hideUnchangedRegions == value)
            return;
        _hideUnchangedRegions = value;
        ApplyDiffVisuals();
    }

    private static List<string> SplitLogicalLines(string text)
    {
        var normalized = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return normalized.Split('\n').ToList();
    }

    private static void ApplyMinimalReplacement(MonacoEditor editor, string target)
    {
        var current = editor.Text;
        if (string.Equals(current, target, StringComparison.Ordinal))
            return;

        var prefix = 0;
        var prefixLimit = Math.Min(current.Length, target.Length);
        while (prefix < prefixLimit && current[prefix] == target[prefix])
            prefix++;

        var suffix = 0;
        while (suffix < current.Length - prefix && suffix < target.Length - prefix &&
               current[current.Length - 1 - suffix] == target[target.Length - 1 - suffix])
            suffix++;

        var replacement = target.Substring(prefix, target.Length - prefix - suffix);
        editor.Model.Replace(
            checked((nuint)prefix),
            checked((nuint)(current.Length - suffix)),
            replacement);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Interlocked.Increment(ref _diffGeneration);
        OriginalEditor.ModelChanged -= SourceEditor_ModelChanged;
        ModifiedEditor.ModelChanged -= SourceEditor_ModelChanged;
        OriginalEditor.ViewportChanged -= OriginalEditor_ViewportChanged;
        ModifiedEditor.ViewportChanged -= ModifiedEditor_ViewportChanged;
        InlineEditor.ViewportChanged -= InlineEditor_ViewportChanged;
        _localization.CultureChanged -= Localization_CultureChanged;
        OriginalEditor.Dispose();
        ModifiedEditor.Dispose();
        InlineEditor.Dispose();
        GC.SuppressFinalize(this);
    }
}
