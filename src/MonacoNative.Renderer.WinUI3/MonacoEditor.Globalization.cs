using Microsoft.Graphics.Canvas.Text;
using MonacoNative.Core;
using MonacoNative.Input;

namespace MonacoNative.Renderer.WinUI3;

public enum MonacoDocumentTextDirection
{
    Auto = 0,
    LeftToRight = 1,
    RightToLeft = 2,
}

public sealed partial class MonacoEditor
{
    private readonly List<(MonacoPosition Anchor, MonacoPosition Active)> _secondarySelections = new();
    private MonacoDocumentTextDirection _documentTextDirection = MonacoDocumentTextDirection.Auto;

    /// <summary>
    /// Controls the base paragraph direction used by DirectWrite. This is deliberately
    /// independent from XAML FlowDirection so editor chrome, gutter and popups do not flip
    /// merely because the document contains RTL text.
    /// </summary>
    public MonacoDocumentTextDirection DocumentTextDirection
    {
        get => _documentTextDirection;
        set
        {
            if (_documentTextDirection == value) return;
            _documentTextDirection = value;
            _layoutCache?.Clear();
            _measurer?.Clear();
            Render();
        }
    }

    /// <summary>Primary selection followed by secondary selections/carets.</summary>
    public IReadOnlyList<MonacoRange> Selections
    {
        get
        {
            var result = new List<MonacoRange>(_secondarySelections.Count + 1)
            {
                new(_anchor.LineNumber, _anchor.Column, _active.LineNumber, _active.Column)
            };
            foreach (var selection in _secondarySelections)
                result.Add(new MonacoRange(selection.Anchor.LineNumber, selection.Anchor.Column, selection.Active.LineNumber, selection.Active.Column));
            return result;
        }
    }

    public void SetSelections(IEnumerable<MonacoRange> selections)
    {
        ArgumentNullException.ThrowIfNull(selections);
        var normalized = selections.Select(range => (
            Anchor: Normalize(range.StartLineNumber, range.StartColumn),
            Active: Normalize(range.EndLineNumber, range.EndColumn))).ToList();
        if (normalized.Count == 0)
            normalized.Add((new MonacoPosition(1, 1), new MonacoPosition(1, 1)));
        _anchor = normalized[0].Anchor;
        _active = normalized[0].Active;
        _secondarySelections.Clear();
        _secondarySelections.AddRange(normalized.Skip(1));
        _preferredCaretX = null;
        OnSelectionChanged();
    }

    private void ClearSecondarySelections() => _secondarySelections.Clear();

    private void TransformSelections(Func<MonacoPosition, MonacoPosition> transform, bool extend)
    {
        var states = EnumerateSelectionStates().ToArray();
        if (states.Length == 0)
            return;

        var transformed = new List<(MonacoPosition Anchor, MonacoPosition Active)>(states.Length);
        foreach (var state in states)
        {
            var transformedActive = transform(state.Active);
            var active = Normalize(transformedActive.LineNumber, transformedActive.Column);
            transformed.Add(extend ? (state.Anchor, active) : (active, active));
        }

        _anchor = transformed[0].Anchor;
        _active = transformed[0].Active;
        _secondarySelections.Clear();
        _secondarySelections.AddRange(transformed.Skip(1));
        _preferredCaretX = null;
        OnSelectionChanged();
    }

    private void AddSecondaryCaret(MonacoPosition position)
    {
        position = Normalize(position.LineNumber, position.Column);
        var offset = _model.GetOffsetAt(position);
        if (_model.GetOffsetAt(_active) == offset || _secondarySelections.Any(item => _model.GetOffsetAt(item.Active) == offset && item.Anchor == item.Active))
            return;
        _secondarySelections.Add((position, position));
        OnSelectionChanged();
    }

    private IEnumerable<(MonacoPosition Anchor, MonacoPosition Active)> EnumerateSelectionStates()
    {
        yield return (_anchor, _active);
        foreach (var selection in _secondarySelections)
            yield return selection;
    }

    internal IReadOnlyList<(int Start, int End)> AccessibilitySelectionOffsetRanges =>
        EnumerateSelectionStates().Select(selection =>
        {
            var a = _model.GetOffsetAt(selection.Anchor);
            var b = _model.GetOffsetAt(selection.Active);
            if (b < a) (a, b) = (b, a);
            var safeA = a > (nuint)int.MaxValue ? int.MaxValue : checked((int)a);
            var safeB = b > (nuint)int.MaxValue ? int.MaxValue : checked((int)b);
            return (safeA, safeB);
        }).ToArray();

    private CanvasTextDirection ResolveTextDirection(string text)
    {
        if (_documentTextDirection == MonacoDocumentTextDirection.LeftToRight)
            return CanvasTextDirection.LeftToRightThenTopToBottom;
        if (_documentTextDirection == MonacoDocumentTextDirection.RightToLeft)
            return CanvasTextDirection.RightToLeftThenTopToBottom;

        return BidiTextDirectionResolver.Resolve(text) == BidiBaseDirection.RightToLeft
            ? CanvasTextDirection.RightToLeftThenTopToBottom
            : CanvasTextDirection.LeftToRightThenTopToBottom;
    }

    private void ReplaceMultipleSelections(string replacement, bool deleteBackward = false, bool deleteForward = false, bool byWord = false)
    {
        var text = _model.Text;
        var ranges = new List<(nuint Start, nuint End)>();
        foreach (var selection in EnumerateSelectionStates())
        {
            var a = _model.GetOffsetAt(selection.Anchor);
            var b = _model.GetOffsetAt(selection.Active);
            var start = a <= b ? a : b;
            var end = a <= b ? b : a;
            if (start == end && deleteBackward && start > 0)
                start = (nuint)(byWord
                    ? MonacoUnicode.PreviousWordBoundary(text, checked((int)start))
                    : PreviousTextElementOffset(text, checked((int)start)));
            else if (start == end && deleteForward && end < _model.Length)
                end = (nuint)(byWord
                    ? MonacoUnicode.NextWordBoundary(text, checked((int)end))
                    : NextTextElementOffset(text, checked((int)end)));
            ranges.Add((start, end));
        }

        ranges.Sort((left, right) => left.Start != right.Start ? left.Start.CompareTo(right.Start) : left.End.CompareTo(right.End));
        var merged = new List<(nuint Start, nuint End)>();
        foreach (var range in ranges)
        {
            if (merged.Count == 0 || range.Start > merged[^1].End)
            {
                merged.Add(range);
                continue;
            }
            merged[^1] = (merged[^1].Start, merged[^1].End >= range.End ? merged[^1].End : range.End);
        }
        if (merged.Count == 0)
            return;
        if (replacement.Length == 0 && merged.All(range => range.Start == range.End))
            return;

        var finalOffsets = new List<nuint>(merged.Count);
        long delta = 0;
        foreach (var range in merged)
        {
            var postStart = checked((long)range.Start + delta);
            finalOffsets.Add(checked((nuint)(postStart + replacement.Length)));
            delta += replacement.Length - checked((long)(range.End - range.Start));
        }

        using (var transaction = _model.BeginUndoTransaction())
        {
            for (var index = merged.Count - 1; index >= 0; --index)
                _model.Replace(merged[index].Start, merged[index].End, replacement);
            transaction.Complete();
        }

        _secondarySelections.Clear();
        _anchor = _active = _model.GetPositionAt(finalOffsets[0]);
        for (var index = 1; index < finalOffsets.Count; ++index)
        {
            var caret = _model.GetPositionAt(finalOffsets[index]);
            _secondarySelections.Add((caret, caret));
        }
        AfterProjectionChanged(true);
        OnSelectionChanged();
    }

    private string GetMultiSelectionClipboardText()
    {
        var pieces = new List<string>();
        foreach (var selection in EnumerateSelectionStates())
        {
            var a = _model.GetOffsetAt(selection.Anchor);
            var b = _model.GetOffsetAt(selection.Active);
            var start = a <= b ? a : b;
            var end = a <= b ? b : a;
            if (start != end)
                pieces.Add(_model.GetTextRange(start, end - start));
        }
        return string.Join(_model.EndOfLine == MonacoEndOfLine.CrLf ? "\r\n" : "\n", pieces);
    }
}
