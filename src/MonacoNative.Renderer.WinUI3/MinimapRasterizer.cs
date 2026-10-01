using Microsoft.Graphics.Canvas;
using MonacoNative.Core;
using Windows.UI;

namespace MonacoNative.Renderer.WinUI3;

/// <summary>
/// Independent minimap rasterizer. The summary cache is line-indexed and incrementally spliced
/// on model edits, so normal typing refreshes only the affected line instead of rescanning the
/// entire document after every version change.
/// </summary>
internal sealed class MinimapRasterizer
{
    private readonly MinimapLineCache _cache = new();
    private readonly MinimapRowCache _rows = new();

    public void NotifyModelChanged(MonacoModelChangedEventArgs change, int resultingLineCount)
    {
        _cache.ApplyChange(
            change.StartLineNumber,
            change.OldEndLineNumber,
            change.NewEndLineNumber,
            resultingLineCount,
            change.VersionId,
            change.IsFullInvalidation);
        _rows.NotifyModelChanged(
            change.StartLineNumber,
            change.OldEndLineNumber,
            change.NewEndLineNumber,
            resultingLineCount,
            change.IsFullInvalidation);
    }

    public void Ensure(MonacoTextModel model)
    {
        _cache.Ensure(checked((int)model.LineCount), model.GetLineContent, model.VersionId);
    }

    public bool IsCurrent(ulong version) => _cache.IsCurrent(version);

    public void Invalidate()
    {
        _cache.Invalidate();
        _rows.Invalidate();
    }

    internal MinimapRowCacheStatistics RowStatistics => _rows.Statistics;

    internal void ResetRowStatistics() => _rows.ResetStatistics();

    public void Draw(
        CanvasDrawingSession ds,
        float x,
        float width,
        float height,
        double scrollTop,
        double totalHeight,
        double viewportHeight,
        Color background,
        Color foreground,
        Color viewport)
    {
        if (width <= 0 || height <= 0)
            return;

        ds.FillRectangle(x, 0, width, height, background);
        var lines = _cache.Lines;
        _rows.Ensure(lines, height);
        var rows = _rows.Rows;
        if (rows.Count > 0)
        {
            // VS Code-inspired miniature token layout (roughly 2.5px/line). The old
            // length-only, full-height bars made a 30-line file look like a histogram.
            const float microLineHeight = 2.5f;
            const float microCharWidth = 0.74f;
            var targetRows = Math.Max(1, Math.Min(lines.Count, (int)Math.Floor(height / microLineHeight)));
            var lineHeight = lines.Count > targetRows ? height / targetRows : microLineHeight;
            var neutral = foreground;
            var quoted = Color.FromArgb(foreground.A, 176, 128, 93);
            var comment = Color.FromArgb(foreground.A, 96, 144, 91);
            var punctuation = Color.FromArgb(foreground.A, 108, 148, 200);
            for (var row = 0; row < targetRows; row++)
            {
                var lineIndex = (int)((long)row * lines.Count / targetRows);
                // Prefer a nonempty representative within an aggregated block.
                var end = Math.Min(lines.Count, (int)((long)(row + 1) * lines.Count / targetRows));
                if (lines[lineIndex].Length == 0 && lineIndex + 1 < end && lines[lineIndex + 1].Length != 0)
                    ++lineIndex;
                var preview = _cache.PreviewAt(lineIndex);
                var y = row * lineHeight;
                foreach (var run in preview)
                {
                    var left = x + 3 + run.Start * microCharWidth;
                    if (left > x + width - 2) break;
                    var runWidth = Math.Min(width - 3 - (left - x), run.Length * microCharWidth);
                    if (runWidth < 0.5f) continue;
                    var tint = run.Kind switch { 1 => quoted, 2 => comment, 3 => punctuation, _ => neutral };
                    ds.FillRectangle(left, y, runWidth, Math.Min(1.7f, lineHeight * 0.72f), tint);
                }
            }
        }

        if (totalHeight > 0)
        {
            var top = (float)Math.Clamp(scrollTop / totalHeight * height, 0, height);
            var visible = (float)Math.Clamp(viewportHeight / totalHeight * height, 3, height);
            if (top + visible > height)
                top = Math.Max(0, height - visible);
            ds.DrawRectangle(x + 0.5f, top + 0.5f, Math.Max(1f, width - 1f), Math.Max(1f, visible - 1f), viewport, 1f);
        }
    }
}
