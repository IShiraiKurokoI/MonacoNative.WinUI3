using Microsoft.UI.Xaml;
using MonacoNative.Core;
using MonacoNative.Language;

namespace MonacoNative.Renderer.WinUI3;

public sealed partial class MonacoEditor
{
    public event EventHandler? FoldingChanged;
    private bool _showFolding = true;
    private readonly DispatcherTimer _foldRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(280) };
    private SortedDictionary<int, int> _foldRanges = new();
    private readonly HashSet<int> _collapsedFolds = new();
    private CancellationTokenSource? _foldingCts;
    private Task? _foldingRefreshTask;

    private void CancelFoldingRefresh()
    {
        var source = _foldingCts;
        _foldingCts = null;
        source?.Cancel();
        source?.Dispose();
    }

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
        CancelFoldingRefresh();
        if (_disposed || !_loaded) return;
        _foldRefreshTimer.Stop();
        _foldRefreshTimer.Start();
    }

    private async void FoldRefreshTimer_Tick(object? sender, object e)
    {
        _foldRefreshTimer.Stop();
        _foldingRefreshTask = _lifetime.Track(RebuildFoldingRangesAsync());
        await _foldingRefreshTask;
    }

    private async Task RebuildFoldingRangesAsync()
    {
        CancelFoldingRefresh();
        if (_disposed || !_loaded) return;
        var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ShutdownToken);
        _foldingCts = source;
        var token = source.Token;
        try
        {
            // Capture on the UI thread: providers must never read the live native model on a worker.
            var snapshot = LanguageModelSnapshot.Capture(_languageDocument);
            var ranges = await Task.Run(async () =>
            {
                var provided = await _languages.ProvideFoldingRangesAsync(snapshot, new FoldingContext(5000), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (provided is null)
                {
                    // No applicable provider: retain the existing fallback for other languages.
                    var lines = Enumerable.Range(1, snapshot.LineCount).Select(snapshot.GetLineContent).ToArray();
                    return EditorFoldingRanges.Compute(lines);
                }
                var result = new SortedDictionary<int, int>();
                foreach (var range in provided.OrderBy(r => r.StartLineNumber).ThenBy(r => r.EndLineNumber))
                {
                    token.ThrowIfCancellationRequested();
                    if (range.StartLineNumber < 1 || range.EndLineNumber > snapshot.LineCount || range.EndLineNumber <= range.StartLineNumber) continue;
                    // Only one chevron can be shown per model line; retain the first (innermost) range.
                    result.TryAdd(range.StartLineNumber, range.EndLineNumber);
                }
                return result;
            }, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (_disposed || !_loaded || !ReferenceEquals(_foldingCts, source) || !snapshot.Matches(_languageDocument)) return;
            _foldRanges = ranges;
            if (_collapsedFolds.RemoveWhere(start => !_foldRanges.ContainsKey(start)) > 0 || _collapsedFolds.Count > 0)
                ApplyFoldingProjection();
            else Render(RenderDirty.Text);
            FoldingChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (_disposed || token.IsCancellationRequested) return;
            LastNativeLanguageServiceError = error;
            NativeLanguageServiceFailed?.Invoke(this, EventArgs.Empty);
            System.Diagnostics.Debug.WriteLine($"Native language folding failed: {error}");
        }
        finally
        {
            if (ReferenceEquals(_foldingCts, source)) { _foldingCts = null; source.Dispose(); }
        }
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
