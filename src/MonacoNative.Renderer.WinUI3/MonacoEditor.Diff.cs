using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using MonacoNative.Core;
using Windows.Foundation;
using Windows.UI;

namespace MonacoNative.Renderer.WinUI3;

public enum MonacoDiffDecorationKind
{
    Inserted,
    Deleted,
    Modified,
    Moved,
}

public readonly record struct MonacoDiffLineDecoration(
    int StartLineNumber,
    int EndLineNumberExclusive,
    MonacoDiffDecorationKind Kind);

public readonly record struct MonacoDiffTextDecoration(
    MonacoRange Range,
    MonacoDiffDecorationKind Kind);

public sealed partial class MonacoEditor
{
    private IReadOnlyList<MonacoDiffLineDecoration> _diffLineDecorations = Array.Empty<MonacoDiffLineDecoration>();
    private IReadOnlyList<MonacoDiffTextDecoration> _diffTextDecorations = Array.Empty<MonacoDiffTextDecoration>();
    private LineRangeIndex<MonacoDiffLineDecoration> _diffLineDecorationIndex = LineRangeIndex<MonacoDiffLineDecoration>.Empty;
    private LineRangeIndex<MonacoDiffTextDecoration> _diffTextDecorationIndex = LineRangeIndex<MonacoDiffTextDecoration>.Empty;

    private static readonly Color DiffInsertedLineColor = Color.FromArgb(42, 46, 160, 67);
    private static readonly Color DiffDeletedLineColor = Color.FromArgb(46, 248, 81, 73);
    private static readonly Color DiffModifiedLineColor = Color.FromArgb(34, 0, 122, 204);
    private static readonly Color DiffMovedLineColor = Color.FromArgb(42, 138, 100, 220);
    private static readonly Color DiffInsertedTextColor = Color.FromArgb(94, 46, 160, 67);
    private static readonly Color DiffDeletedTextColor = Color.FromArgb(104, 248, 81, 73);
    private static readonly Color DiffModifiedTextColor = Color.FromArgb(82, 0, 122, 204);
    private static readonly Color DiffMovedTextColor = Color.FromArgb(88, 138, 100, 220);

    public void SetDiffDecorations(
        IReadOnlyList<MonacoDiffLineDecoration>? lineDecorations,
        IReadOnlyList<MonacoDiffTextDecoration>? textDecorations = null)
    {
        _diffLineDecorations = lineDecorations?.ToArray() ?? Array.Empty<MonacoDiffLineDecoration>();
        _diffTextDecorations = textDecorations?.ToArray() ?? Array.Empty<MonacoDiffTextDecoration>();
        _diffLineDecorationIndex = new LineRangeIndex<MonacoDiffLineDecoration>(
            _diffLineDecorations.Where(static decoration => decoration.EndLineNumberExclusive > decoration.StartLineNumber),
            static decoration => (decoration.StartLineNumber, decoration.EndLineNumberExclusive - 1));
        _diffTextDecorationIndex = new LineRangeIndex<MonacoDiffTextDecoration>(
            _diffTextDecorations,
            static decoration => (decoration.Range.StartLineNumber, decoration.Range.EndLineNumber));
        Render(RenderDirty.Decorations);
    }

    public void ClearDiffDecorations()
    {
        _diffLineDecorations = Array.Empty<MonacoDiffLineDecoration>();
        _diffTextDecorations = Array.Empty<MonacoDiffTextDecoration>();
        _diffLineDecorationIndex = LineRangeIndex<MonacoDiffLineDecoration>.Empty;
        _diffTextDecorationIndex = LineRangeIndex<MonacoDiffTextDecoration>.Empty;
        Render(RenderDirty.Decorations);
    }

    private void DrawDiffDecorations(
        CanvasDrawingSession ds,
        int firstViewLine,
        int lastViewLine,
        double scrollTop,
        double scrollLeft)
    {
        if ((_diffLineDecorationIndex.Count == 0 && _diffTextDecorationIndex.Count == 0) || _layoutCache is null)
            return;

        var visibleModelLines = Enumerable.Range(firstViewLine, lastViewLine - firstViewLine + 1)
            .Select(viewLineNumber => _view.GetViewLine(viewLineNumber).ModelLineNumber)
            .Distinct()
            .ToArray();

        var lineDecorationsByModelLine = new Dictionary<int, IReadOnlyList<MonacoDiffLineDecoration>>();
        var textDecorationsByModelLine = new Dictionary<int, IReadOnlyList<MonacoDiffTextDecoration>>();
        foreach (var modelLine in visibleModelLines)
        {
            var lineDecorations = _diffLineDecorationIndex.Query(modelLine, modelLine);
            if (lineDecorations.Count > 0)
                lineDecorationsByModelLine[modelLine] = lineDecorations;

            var textDecorations = _diffTextDecorationIndex.Query(modelLine, modelLine)
                .Where(decoration => decoration.Range.StartLineNumber == modelLine && decoration.Range.EndLineNumber == modelLine)
                .ToArray();
            if (textDecorations.Length > 0)
                textDecorationsByModelLine[modelLine] = textDecorations;
        }

        for (var viewLineNumber = firstViewLine; viewLineNumber <= lastViewLine; viewLineNumber++)
        {
            var line = _view.GetViewLine(viewLineNumber);
            var y = VerticalPadding + line.VerticalOffset - scrollTop;
            if (y + _lineHeight < 0 || y > ActualHeight)
                continue;

            if (lineDecorationsByModelLine.TryGetValue(line.ModelLineNumber, out var lineDecorations))
            {
                foreach (var decoration in lineDecorations)
                {
                    ds.FillRectangle(
                        new Rect(GutterWidth, y, Math.Max(0, TextViewportRight - GutterWidth), _lineHeight),
                        LineDecorationColor(decoration.Kind));
                }
            }

            if (!textDecorationsByModelLine.TryGetValue(line.ModelLineNumber, out var textDecorations))
                continue;

            var layout = _layoutCache.GetOrCreate(viewLineNumber, line.Text, (float)_lineHeight, l =>
            {
                ApplyLanguageTokenColors(l, line);
                ApplyBracketPairColors(l, line);
            });

            foreach (var decoration in textDecorations)
            {
                var range = decoration.Range;
                MonacoViewPosition start;
                MonacoViewPosition end;
                try
                {
                    start = _view.ModelToView(new MonacoPosition(range.StartLineNumber, range.StartColumn));
                    end = _view.ModelToView(new MonacoPosition(range.EndLineNumber, range.EndColumn));
                }
                catch (ArgumentOutOfRangeException)
                {
                    continue;
                }
                if (start.LineNumber != viewLineNumber || end.LineNumber != viewLineNumber)
                    continue;

                var startIndex = Math.Clamp(start.Column - 1, 0, line.Text.Length);
                var endIndex = Math.Clamp(end.Column - 1, startIndex, line.Text.Length);
                var regions = TextRangeRegions(layout, line.Text, startIndex, endIndex);
                if (regions.Length == 0)
                {
                    var caretX = GutterWidth + LeftPadding + CaretX(layout, line.Text, startIndex) - scrollLeft;
                    if (caretX >= GutterWidth && caretX <= TextViewportRight)
                        ds.FillRectangle(new Rect(caretX, y, 2, _lineHeight), TextDecorationColor(decoration.Kind));
                    continue;
                }
                foreach (var region in regions)
                {
                    var bounds = region.LayoutBounds;
                    var x1 = GutterWidth + LeftPadding + bounds.X - scrollLeft;
                    var x2 = x1 + bounds.Width;
                    if (x2 < GutterWidth || x1 > TextViewportRight)
                        continue;
                    x1 = Math.Max(GutterWidth, x1);
                    x2 = Math.Min(TextViewportRight, x2);
                    ds.FillRectangle(
                        new Rect(x1, y + bounds.Y, Math.Max(1, x2 - x1), Math.Max(1, bounds.Height)),
                        TextDecorationColor(decoration.Kind));
                }
            }
        }
    }

    private static Color LineDecorationColor(MonacoDiffDecorationKind kind) => kind switch
    {
        MonacoDiffDecorationKind.Inserted => DiffInsertedLineColor,
        MonacoDiffDecorationKind.Deleted => DiffDeletedLineColor,
        MonacoDiffDecorationKind.Moved => DiffMovedLineColor,
        _ => DiffModifiedLineColor,
    };

    private static Color TextDecorationColor(MonacoDiffDecorationKind kind) => kind switch
    {
        MonacoDiffDecorationKind.Inserted => DiffInsertedTextColor,
        MonacoDiffDecorationKind.Deleted => DiffDeletedTextColor,
        MonacoDiffDecorationKind.Moved => DiffMovedTextColor,
        _ => DiffModifiedTextColor,
    };
}
