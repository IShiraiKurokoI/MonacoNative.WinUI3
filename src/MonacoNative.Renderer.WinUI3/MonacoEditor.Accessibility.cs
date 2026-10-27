using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls.Primitives;
using MonacoNative.Core;
using MonacoNative.Input;
using Windows.Foundation;

namespace MonacoNative.Renderer.WinUI3;

internal readonly record struct AccessibilityOffsetRange(int Start, int End);

public sealed partial class MonacoEditor
{
    protected override AutomationPeer OnCreateAutomationPeer() => new MonacoEditorAutomationPeer(this);

    internal string AccessibilityText => _model.Text;
    internal int AccessibilityLength => checked((int)Math.Min(_model.Length, (nuint)int.MaxValue));
    internal bool AccessibilityHasKeyboardFocus => HasEditorInputFocus;

    internal (int Start, int End) AccessibilitySelectionOffsets
    {
        get
        {
            var (start, end) = OrderedSelectionOffsets();
            return (checked((int)Math.Min(start, (nuint)int.MaxValue)), checked((int)Math.Min(end, (nuint)int.MaxValue)));
        }
    }

    internal ImeTextRange? AccessibilityCompositionRange =>
        _textInput is { IsComposing: true } input ? input.CompositionRange : null;

    internal ImeTextRange? AccessibilityConversionTargetRange => _textInput?.ConversionTargetRange;

    internal IReadOnlyList<AccessibilityOffsetRange> AccessibilityVisibleRanges
    {
        get
        {
            if (_view.ViewLineCount <= 0 || ActualHeight <= 0)
                return Array.Empty<AccessibilityOffsetRange>();
            var top = Math.Max(0, ScrollHost.VerticalOffset - VerticalPadding);
            var bottom = Math.Max(top, ScrollHost.VerticalOffset + ActualHeight - VerticalPadding);
            var first = Math.Clamp(_view.GetViewLineAtVerticalOffset(top), 1, _view.ViewLineCount);
            var last = Math.Clamp(_view.GetViewLineAtVerticalOffset(bottom), first, _view.ViewLineCount);
            var result = new List<AccessibilityOffsetRange>();
            for (var lineNumber = first; lineNumber <= last; ++lineNumber)
            {
                var line = _view.GetViewLine(lineNumber);
                var start = _model.GetOffsetAt(new MonacoPosition(line.ModelLineNumber, line.StartColumn));
                var end = _model.GetOffsetAt(new MonacoPosition(line.ModelLineNumber, line.EndColumn));
                var a = checked((int)Math.Min(start, (nuint)int.MaxValue));
                var b = checked((int)Math.Min(end, (nuint)int.MaxValue));
                if (b < a)
                    (a, b) = (b, a);
                if (b == a)
                    continue; // projection-only injected text has no document range
                if (result.Count > 0 && a <= result[^1].End)
                    result[^1] = new AccessibilityOffsetRange(result[^1].Start, Math.Max(result[^1].End, b));
                else
                    result.Add(new AccessibilityOffsetRange(a, b));
            }
            if (result.Count == 0)
            {
                var caret = AccessibilitySelectionOffsets.End;
                result.Add(new AccessibilityOffsetRange(caret, caret));
            }
            return result;
        }
    }

    internal int AccessibilityOffsetFromScreenPoint(Point screenPoint)
    {
        if (XamlRoot is null || _disposed || _layoutCache is null)
            return 0;

        // UI Automation supplies physical desktop pixels, not XAML DIPs. Use the
        // same screen-to-surface conversion as the native TSF hit-test path:
        // subtracting the control's screen origin and dividing by a cached DPI
        // loses the border/visual transform and is incorrect across monitors.
        var local = XamlRoot.CoordinateConverter.ConvertScreenToLocal(
            new Windows.Graphics.PointInt32(
                checked((int)Math.Round(screenPoint.X)),
                checked((int)Math.Round(screenPoint.Y))));
        var surfacePoint = XamlRoot.Content.TransformToVisual(SurfaceRoot).TransformPoint(local);
        var position = HitTest(
            Math.Clamp(surfacePoint.X, 0, Math.Max(0, SurfaceRoot.ActualWidth)),
            Math.Clamp(surfacePoint.Y, 0, Math.Max(0, SurfaceRoot.ActualHeight)));
        return checked((int)Math.Min(_model.GetOffsetAt(position), (nuint)int.MaxValue));
    }

    private Rect AccessibilitySurfaceRectToScreenPixels(Rect surfaceRect)
    {
        if (XamlRoot is null)
            return new Rect(0, 0, 0, 0);
        // Draw/hit-test positions in this file are SurfaceRoot-local DIPs. The
        // converter applies RasterizationScale and the desktop window offset
        // exactly once, returning the physical coordinates required by UIA.
        var transform = SurfaceRoot.TransformToVisual(XamlRoot.Content);
        var topLeft = transform.TransformPoint(new Point(surfaceRect.Left, surfaceRect.Top));
        var bottomRight = transform.TransformPoint(new Point(surfaceRect.Right, surfaceRect.Bottom));
        var rootRect = new Rect(
            Math.Min(topLeft.X, bottomRight.X), Math.Min(topLeft.Y, bottomRight.Y),
            Math.Abs(bottomRight.X - topLeft.X), Math.Abs(bottomRight.Y - topLeft.Y));
        var screen = XamlRoot.CoordinateConverter.ConvertLocalToScreen(rootRect);
        return new Rect(screen.X, screen.Y, screen.Width, screen.Height);
    }

    internal double[] AccessibilityBoundingRectangles(int start, int end)
    {
        start = Math.Clamp(start, 0, AccessibilityLength);
        end = Math.Clamp(end, start, AccessibilityLength);
        if (start == end || _layoutCache is null || _view.ViewLineCount <= 0 || ActualHeight <= 0)
            return Array.Empty<double>();

        var top = Math.Max(0, ScrollHost.VerticalOffset - VerticalPadding);
        var bottom = Math.Max(top, ScrollHost.VerticalOffset + ActualHeight - VerticalPadding);
        var first = Math.Clamp(_view.GetViewLineAtVerticalOffset(top), 1, _view.ViewLineCount);
        var last = Math.Clamp(_view.GetViewLineAtVerticalOffset(bottom), first, _view.ViewLineCount);
        var values = new List<double>();

        for (var lineNumber = first; lineNumber <= last; ++lineNumber)
        {
            var line = _view.GetViewLine(lineNumber);
            var modelStart = checked((int)Math.Min(
                _model.GetOffsetAt(new MonacoPosition(line.ModelLineNumber, line.StartColumn)), (nuint)int.MaxValue));
            var modelEnd = checked((int)Math.Min(
                _model.GetOffsetAt(new MonacoPosition(line.ModelLineNumber, line.EndColumn)), (nuint)int.MaxValue));
            if (modelEnd < modelStart)
                (modelStart, modelEnd) = (modelEnd, modelStart);
            var a = Math.Max(start, modelStart);
            var b = Math.Min(end, modelEnd);
            if (b <= a)
                continue;

            var startView = _view.ModelToView(_model.GetPositionAt((nuint)a));
            var endView = _view.ModelToView(_model.GetPositionAt((nuint)b));
            var startIndex = startView.LineNumber == lineNumber ? Math.Clamp(startView.Column - 1, 0, line.Text.Length) : 0;
            var endIndex = endView.LineNumber == lineNumber ? Math.Clamp(endView.Column - 1, 0, line.Text.Length) : line.Text.Length;
            if (b >= modelEnd)
                endIndex = line.Text.Length;
            if (a <= modelStart)
                startIndex = 0;
            if (endIndex < startIndex)
                (startIndex, endIndex) = (endIndex, startIndex);

            var layout = _layoutCache.GetOrCreate(lineNumber, line.Text, (float)_lineHeight);
            var y1 = VerticalPadding + line.VerticalOffset - ScrollHost.VerticalOffset;
            var regions = TextRangeRegions(layout, line.Text, startIndex, endIndex);
            if (regions.Length == 0)
            {
                var x1 = GutterWidth + LeftPadding + CaretX(layout, line.Text, startIndex) - ScrollHost.HorizontalOffset;
                var x2 = GutterWidth + LeftPadding + CaretX(layout, line.Text, endIndex) - ScrollHost.HorizontalOffset;
                AddAccessibilityRectangle(new Rect(
                    Math.Min(x1, x2),
                    y1,
                    Math.Max(1, Math.Abs(x2 - x1)),
                    Math.Max(1, _lineHeight)));
                continue;
            }

            foreach (var region in regions)
            {
                var bounds = region.LayoutBounds;
                AddAccessibilityRectangle(new Rect(
                    GutterWidth + LeftPadding + bounds.X - ScrollHost.HorizontalOffset,
                    y1 + bounds.Y,
                    Math.Max(1, bounds.Width),
                    Math.Max(1, bounds.Height)));
            }

            void AddAccessibilityRectangle(Rect rect)
            {
                var local = ClipToViewport(rect);
                if (local.Width <= 0 || local.Height <= 0)
                    return;
                var screen = AccessibilitySurfaceRectToScreenPixels(local);
                values.Add(screen.X);
                values.Add(screen.Y);
                values.Add(screen.Width);
                values.Add(screen.Height);
            }
        }
        return values.ToArray();
    }

    internal void AccessibilitySetSelection(int start, int end)
    {
        start = Math.Clamp(start, 0, AccessibilityLength);
        end = Math.Clamp(end, 0, AccessibilityLength);
        ClearSecondarySelections();
        _anchor = _model.GetPositionAt((nuint)start);
        _active = _model.GetPositionAt((nuint)end);
        _preferredCaretX = null;
        OnSelectionChanged();
    }


    internal void AccessibilityAddSelection(int start, int end)
    {
        start = Math.Clamp(start, 0, AccessibilityLength);
        end = Math.Clamp(end, 0, AccessibilityLength);
        var anchor = _model.GetPositionAt((nuint)start);
        var active = _model.GetPositionAt((nuint)end);
        if (AccessibilitySelectionOffsetRanges.Any(range => range.Start == Math.Min(start, end) && range.End == Math.Max(start, end)))
            return;
        _secondarySelections.Add((anchor, active));
        OnSelectionChanged();
    }

    internal void AccessibilityRemoveSelection(int start, int end)
    {
        start = Math.Clamp(start, 0, AccessibilityLength);
        end = Math.Clamp(end, 0, AccessibilityLength);
        var low = Math.Min(start, end);
        var high = Math.Max(start, end);
        for (var index = 0; index < _secondarySelections.Count; ++index)
        {
            var selection = _secondarySelections[index];
            var a = checked((int)Math.Min(_model.GetOffsetAt(selection.Anchor), (nuint)int.MaxValue));
            var b = checked((int)Math.Min(_model.GetOffsetAt(selection.Active), (nuint)int.MaxValue));
            if (Math.Min(a, b) != low || Math.Max(a, b) != high) continue;
            _secondarySelections.RemoveAt(index);
            OnSelectionChanged();
            return;
        }
        var primary = AccessibilitySelectionOffsets;
        if (primary.Start == low && primary.End == high)
        {
            if (_secondarySelections.Count > 0)
            {
                var promoted = _secondarySelections[0];
                _secondarySelections.RemoveAt(0);
                _anchor = promoted.Anchor;
                _active = promoted.Active;
            }
            else
            {
                _anchor = _active;
            }
            OnSelectionChanged();
        }
    }

    internal void AccessibilityScrollRangeIntoView(int start, int end, bool alignToTop)
    {
        start = Math.Clamp(start, 0, AccessibilityLength);
        end = Math.Clamp(end, start, AccessibilityLength);
        var a = _model.GetPositionAt((nuint)start);
        var b = _model.GetPositionAt((nuint)end);
        var range = new MonacoRange(a.LineNumber, a.Column, b.LineNumber, b.Column);
        var strategy = alignToTop ? MonacoRevealStrategy.Top : MonacoRevealStrategy.Bottom;
        var top = _view.GetScrollTopToReveal(
            range,
            Math.Max(1, ActualHeight - (VerticalPadding * 2)),
            ScrollHost.VerticalOffset,
            strategy,
            _lineHeight);
        ScrollHost.ChangeView(null, top, null, true);
        RaiseAccessibilityLayoutInvalidated();
    }

    internal void AccessibilityShowContextMenu(int offset)
    {
        offset = Math.Clamp(offset, 0, AccessibilityLength);
        var position = _model.GetPositionAt((nuint)offset);
        _anchor = _active = position;
        OnSelectionChanged();
        SurfaceRoot.ContextFlyout?.ShowAt(SurfaceRoot);
    }

    internal void RaiseAccessibilityTextChanged()
    {
        if (FrameworkElementAutomationPeer.FromElement(this) is MonacoEditorAutomationPeer peer)
            peer.RaiseTextChanged();
    }

    internal void RaiseAccessibilitySelectionChanged()
    {
        if (FrameworkElementAutomationPeer.FromElement(this) is MonacoEditorAutomationPeer peer)
            peer.RaiseSelectionChanged();
    }

    internal void RaiseAccessibilityLayoutInvalidated()
    {
        if (FrameworkElementAutomationPeer.FromElement(this) is MonacoEditorAutomationPeer peer)
            peer.RaiseLayoutInvalidated();
    }

    private Rect ClipToViewport(Rect rect)
    {
        var left = Math.Max(0, rect.X);
        var top = Math.Max(0, rect.Y);
        var right = Math.Min(Math.Max(0, ActualWidth), rect.Right);
        var bottom = Math.Min(Math.Max(0, ActualHeight), rect.Bottom);
        return right <= left || bottom <= top ? new Rect(0, 0, 0, 0) : new Rect(left, top, right - left, bottom - top);
    }
}
