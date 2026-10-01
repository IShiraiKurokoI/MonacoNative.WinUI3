namespace MonacoNative.Renderer.WinUI3;

internal readonly record struct MinimapRowSummary(int MaxLength, int MinIndent);

internal readonly record struct MinimapRowCacheStatistics(
    long EnsureCalls,
    long CacheHits,
    long FullRebuilds,
    long IncrementalRebuilds,
    long RowsRebuilt,
    long SourceLinesScanned);

/// <summary>
/// Pixel-row aggregation cache for the density minimap. The line-summary cache remains the
/// authoritative document-sized state; this cache projects those summaries into at most one
/// entry per device-independent minimap row. Steady-state scrolling therefore touches only
/// cached rows plus the viewport indicator and never re-scans every document line.
/// </summary>
internal sealed class MinimapRowCache
{
    private readonly List<MinimapRowSummary> _rows = new();
    private bool _needsFullRebuild = true;
    private int _lineCount;
    private int _rowCount;
    private int _dirtyLineStart = int.MaxValue;
    private int _dirtyLineEnd;

    private long _ensureCalls;
    private long _cacheHits;
    private long _fullRebuilds;
    private long _incrementalRebuilds;
    private long _rowsRebuilt;
    private long _sourceLinesScanned;

    public IReadOnlyList<MinimapRowSummary> Rows => _rows;

    public MinimapRowCacheStatistics Statistics => new(
        _ensureCalls,
        _cacheHits,
        _fullRebuilds,
        _incrementalRebuilds,
        _rowsRebuilt,
        _sourceLinesScanned);

    public void ResetStatistics()
    {
        _ensureCalls = 0;
        _cacheHits = 0;
        _fullRebuilds = 0;
        _incrementalRebuilds = 0;
        _rowsRebuilt = 0;
        _sourceLinesScanned = 0;
    }

    public void Invalidate()
    {
        _needsFullRebuild = true;
        _lineCount = 0;
        _rowCount = 0;
        _dirtyLineStart = int.MaxValue;
        _dirtyLineEnd = 0;
    }

    public void NotifyModelChanged(
        int startLine,
        int oldEndLine,
        int newEndLine,
        int resultingLineCount,
        bool fullInvalidation)
    {
        if (fullInvalidation || startLine < 1 || oldEndLine < startLine || newEndLine < startLine)
        {
            Invalidate();
            return;
        }

        var oldCount = oldEndLine - startLine + 1;
        var newCount = newEndLine - startLine + 1;
        if (_needsFullRebuild || oldCount != newCount || (_lineCount != 0 && resultingLineCount != _lineCount))
        {
            // A structural splice changes the source-line ranges represented by downstream
            // minimap rows. Rebuild the bounded row projection on the next draw; the
            // line-summary cache itself remains incrementally spliced by MinimapLineCache.
            _needsFullRebuild = true;
            _lineCount = resultingLineCount;
            _dirtyLineStart = int.MaxValue;
            _dirtyLineEnd = 0;
            return;
        }

        _lineCount = resultingLineCount;
        _dirtyLineStart = Math.Min(_dirtyLineStart, startLine);
        _dirtyLineEnd = Math.Max(_dirtyLineEnd, newEndLine);
    }

    public void Ensure(IReadOnlyList<MinimapLineSummary> lines, double height)
    {
        ArgumentNullException.ThrowIfNull(lines);
        _ensureCalls++;

        if (lines.Count == 0 || height <= 0)
        {
            if (_rows.Count != 0)
                _rows.Clear();
            _lineCount = lines.Count;
            _rowCount = 0;
            _needsFullRebuild = false;
            _dirtyLineStart = int.MaxValue;
            _dirtyLineEnd = 0;
            return;
        }

        var rows = Math.Max(1, Math.Min(lines.Count, (int)Math.Ceiling(height)));
        if (_needsFullRebuild || _lineCount != lines.Count || _rowCount != rows || _rows.Count != rows)
        {
            _rows.Clear();
            for (var row = 0; row < rows; ++row)
                _rows.Add(SummarizeRow(lines, row, rows));
            _lineCount = lines.Count;
            _rowCount = rows;
            _needsFullRebuild = false;
            _dirtyLineStart = int.MaxValue;
            _dirtyLineEnd = 0;
            _fullRebuilds++;
            _rowsRebuilt += rows;
            return;
        }

        if (_dirtyLineStart > _dirtyLineEnd)
        {
            _cacheHits++;
            return;
        }

        var firstRow = RowForLine(_dirtyLineStart, lines.Count, rows);
        var lastRow = RowForLine(_dirtyLineEnd, lines.Count, rows);
        for (var row = firstRow; row <= lastRow; ++row)
            _rows[row] = SummarizeRow(lines, row, rows);

        _incrementalRebuilds++;
        _rowsRebuilt += lastRow - firstRow + 1;
        _dirtyLineStart = int.MaxValue;
        _dirtyLineEnd = 0;
    }

    private MinimapRowSummary SummarizeRow(IReadOnlyList<MinimapLineSummary> lines, int row, int rowCount)
    {
        var start = row * lines.Count / rowCount;
        var end = Math.Max(start + 1, (row + 1) * lines.Count / rowCount);
        var maxLength = 0;
        var minIndent = int.MaxValue;
        for (var i = start; i < end && i < lines.Count; ++i)
        {
            _sourceLinesScanned++;
            maxLength = Math.Max(maxLength, lines[i].Length);
            minIndent = Math.Min(minIndent, lines[i].Indent);
        }
        return new MinimapRowSummary(maxLength, minIndent == int.MaxValue ? 0 : minIndent);
    }

    private static int RowForLine(int oneBasedLine, int lineCount, int rowCount)
    {
        var zeroBasedLine = Math.Clamp(oneBasedLine - 1, 0, Math.Max(0, lineCount - 1));
        return Math.Clamp((int)((long)zeroBasedLine * rowCount / Math.Max(1, lineCount)), 0, rowCount - 1);
    }
}
