using Microsoft.UI.Xaml;
using MonacoNative.Core;

namespace MonacoNative.Renderer.WinUI3;

public sealed partial class MonacoEditor
{
    public event EventHandler? FoldingChanged;
    private bool _showFolding = true;
    private readonly DispatcherTimer _foldRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(280) };
    private SortedDictionary<int, int> _foldRanges = new();
    private readonly HashSet<int> _collapsedFolds = new();

    /// <summary>Show clickable fold chevrons in the gutter; existing folds stay active when hidden.</summary>
    public bool ShowFolding
    {
        get => _showFolding;
        set
        {
            if (_showFolding == value) return;
            _showFolding = value;
            Render(RenderDirty.Text);
        }
    }

    private void OnFoldingModelChanged(MonacoModelChangedEventArgs change)
    {
        if (_collapsedFolds.Count == 0) return;
        if (change.IsFullInvalidation)
        {
            _collapsedFolds.Clear();
            return;
        }
        var delta = change.NewEndLineNumber - change.OldEndLineNumber;
        if (delta == 0) return;
        var shifted = new HashSet<int>();
        foreach (var line in _collapsedFolds)
        {
            if (line < change.StartLineNumber) shifted.Add(line);
            else if (line > change.OldEndLineNumber) shifted.Add(line + delta);
            else if (line == change.StartLineNumber) shifted.Add(line);
        }
        _collapsedFolds.Clear();
        foreach (var line in shifted) _collapsedFolds.Add(line);
    }

    private void ScheduleFoldingRefresh()
    {
        if (_disposed || !_loaded) return;
        _foldRefreshTimer.Stop();
        _foldRefreshTimer.Start();
    }

    private void FoldRefreshTimer_Tick(object? sender, object e)
    {
        _foldRefreshTimer.Stop();
        RebuildFoldingRanges();
    }

    private void RebuildFoldingRanges()
    {
        if (_disposed) return;
        var count = checked((int)_model.LineCount);
        var lines = new string[count];
        for (var i = 0; i < count; ++i) lines[i] = _model.GetLineContent(i + 1);
        _foldRanges = EditorFoldingRanges.Compute(lines);
        if (_collapsedFolds.RemoveWhere(start => !_foldRanges.ContainsKey(start)) > 0 || _collapsedFolds.Count > 0)
            ApplyFoldingProjection();
        else Render(RenderDirty.Text);
        FoldingChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Collapse/expand the code block beginning at the given 1-based model line.</summary>
    public bool ToggleFold(int lineNumber)
    {
        if (!_foldRanges.ContainsKey(lineNumber)) return false;
        if (!_collapsedFolds.Add(lineNumber)) _collapsedFolds.Remove(lineNumber);
        ApplyFoldingProjection();
        return true;
    }

    public int CollapsedFoldCount => _collapsedFolds.Count;
    public int AvailableFoldCount => _foldRanges.Count;

    public void ExpandAllFolds()
    {
        if (_collapsedFolds.Count == 0) return;
        _collapsedFolds.Clear();
        ApplyFoldingProjection();
    }

    public void CollapseAllFolds()
    {
        foreach (var line in _foldRanges.Keys) _collapsedFolds.Add(line);
        ApplyFoldingProjection();
    }

    private void ApplyFoldingProjection()
    {
        if (_disposed) return;
        var areas = new List<MonacoHiddenArea>();
        foreach (var start in _collapsedFolds.OrderBy(line => line))
        {
            if (_foldRanges.TryGetValue(start, out var end))
                areas.Add(new MonacoHiddenArea(start + 1, end));
        }
        // Never leave the primary caret inside a hidden line.
        foreach (var area in areas)
        {
            if (_active.LineNumber >= area.StartLineNumber && _active.LineNumber <= area.EndLineNumber)
                _anchor = _active = new MonacoPosition(area.StartLineNumber - 1, 1);
        }
        _view.SetHiddenAreas(areas);
        _layoutCache?.Clear();
        _lastKnownViewLineCount = _view.ViewLineCount;
        _maxObservedLineWidth = 0;
        UpdateExtent();
        RevealCaret();
        _textInput?.NotifyLayoutChanged();
        Render(RenderDirty.All);
        FoldingChanged?.Invoke(this, EventArgs.Empty);
    }
}
