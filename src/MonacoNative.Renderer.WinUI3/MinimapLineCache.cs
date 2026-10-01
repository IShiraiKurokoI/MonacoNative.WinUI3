namespace MonacoNative.Renderer.WinUI3;

internal readonly record struct MinimapLineSummary(int Length, int Indent);

// Compact precomputed visual runs; never shape full-size text while painting/scrolling.
internal readonly record struct MinimapPreviewSpan(int Start, int Length, byte Kind);

internal readonly record struct MinimapLineCacheStatistics(
    long EnsureCalls,
    long FullRebuilds,
    long IncrementalRefreshes,
    long LinesSummarized,
    long StructuralSplices);

/// <summary>
/// Line-indexed minimap summary cache. Structural edits splice the cache immediately while
/// textual summaries are refreshed lazily for only the affected model-line interval.
/// </summary>
internal sealed class MinimapLineCache
{
    private readonly List<MinimapLineSummary> _lines = new();
    // Bounded, lazy previews: never build a glyph map for every line of a million-line file.
    private readonly Dictionary<int, MinimapPreviewSpan[]> _previews = new();
    private Func<int, string>? _getLineContent;
    private int _dirtyStart = int.MaxValue;
    private int _dirtyEnd;
    private bool _needsFullRebuild = true;
    private ulong _version;
    private long _ensureCalls;
    private long _fullRebuilds;
    private long _incrementalRefreshes;
    private long _linesSummarized;
    private long _structuralSplices;

    public IReadOnlyList<MinimapLineSummary> Lines => _lines;
    public IReadOnlyList<MinimapPreviewSpan> PreviewAt(int zeroBasedLine)
    {
        if (!_previews.TryGetValue(zeroBasedLine, out var preview))
        {
            if (_previews.Count >= 700) _previews.Clear();
            preview = BuildPreview(_getLineContent?.Invoke(zeroBasedLine + 1) ?? string.Empty);
            _previews[zeroBasedLine] = preview;
        }
        return preview;
    }
    public MinimapLineCacheStatistics Statistics => new(
        _ensureCalls, _fullRebuilds, _incrementalRefreshes, _linesSummarized, _structuralSplices);

    public void ResetStatistics()
    {
        _ensureCalls = 0;
        _fullRebuilds = 0;
        _incrementalRefreshes = 0;
        _linesSummarized = 0;
        _structuralSplices = 0;
    }
    public bool IsCurrent(ulong version) => !_needsFullRebuild && _version == version;
    public bool HasPendingWork => _needsFullRebuild || _dirtyStart <= _dirtyEnd;

    public void Invalidate()
    {
        _needsFullRebuild = true;
        _previews.Clear();
        _version = 0;
        _dirtyStart = int.MaxValue;
        _dirtyEnd = 0;
    }

    public void ApplyChange(
        int startLine,
        int oldEndLine,
        int newEndLine,
        int resultingLineCount,
        ulong version,
        bool fullInvalidation)
    {
        if (fullInvalidation || _needsFullRebuild || startLine < 1 || oldEndLine < startLine || newEndLine < startLine)
        {
            Invalidate();
            return;
        }

        var oldCount = checked(oldEndLine - startLine + 1);
        var newCount = checked(newEndLine - startLine + 1);
        if (oldCount != newCount)
            _structuralSplices++;
        var delta = checked(newCount - oldCount);
        var expectedOldLineCount = checked(resultingLineCount - delta);
        if (expectedOldLineCount < 1 || _lines.Count != expectedOldLineCount || startLine - 1 + oldCount > _lines.Count)
        {
            Invalidate();
            return;
        }

        TransformPendingDirtyRange(startLine, oldEndLine, newEndLine, delta);

        var index = startLine - 1;
        _lines.RemoveRange(index, oldCount);
        _previews.Clear();
        for (var i = 0; i < newCount; ++i)
        {
            _lines.Insert(index + i, default);

        }

        if (_lines.Count != resultingLineCount)
        {
            Invalidate();
            return;
        }

        MarkDirty(startLine, newEndLine);
        _version = version;
    }

    public void Ensure(int lineCount, Func<int, string> getLineContent, ulong version)
    {
        ArgumentNullException.ThrowIfNull(getLineContent);
        _getLineContent = getLineContent;
        _ensureCalls++;
        if (lineCount < 1)
            throw new ArgumentOutOfRangeException(nameof(lineCount));

        if (_needsFullRebuild || _version != version || _lines.Count != lineCount)
        {
            _lines.Clear();
            _previews.Clear();
            for (var line = 1; line <= lineCount; ++line)
            {
                var content = getLineContent(line);
                _lines.Add(Summarize(content));

                _linesSummarized++;
            }
            _fullRebuilds++;
            _needsFullRebuild = false;
            _version = version;
            _dirtyStart = int.MaxValue;
            _dirtyEnd = 0;
            return;
        }

        if (_dirtyStart > _dirtyEnd)
            return;

        var start = Math.Clamp(_dirtyStart, 1, lineCount);
        var end = Math.Clamp(_dirtyEnd, start, lineCount);
        for (var line = start; line <= end; ++line)
        {
            var content = getLineContent(line);
            _lines[line - 1] = Summarize(content);
            _previews.Remove(line - 1);
            _linesSummarized++;
        }
        _incrementalRefreshes++;

        _dirtyStart = int.MaxValue;
        _dirtyEnd = 0;
    }

    private void TransformPendingDirtyRange(int startLine, int oldEndLine, int newEndLine, int delta)
    {
        if (_dirtyStart > _dirtyEnd)
            return;

        if (_dirtyEnd < startLine)
            return;

        if (_dirtyStart > oldEndLine)
        {
            _dirtyStart = checked(_dirtyStart + delta);
            _dirtyEnd = checked(_dirtyEnd + delta);
            return;
        }

        var oldDirtyEnd = _dirtyEnd;
        _dirtyStart = Math.Min(_dirtyStart, startLine);
        var mappedOldEnd = oldDirtyEnd > oldEndLine ? checked(oldDirtyEnd + delta) : newEndLine;
        _dirtyEnd = Math.Max(newEndLine, mappedOldEnd);
    }

    private void MarkDirty(int startLine, int endLine)
    {
        _dirtyStart = Math.Min(_dirtyStart, startLine);
        _dirtyEnd = Math.Max(_dirtyEnd, endLine);
    }

    private static MinimapPreviewSpan[] BuildPreview(string text)
    {
        // Keep indentation and spacing within each line; unlike a density bar, these
        // spans retain the visual structure of tokens, operators and comments.
        const int maxColumns = 110;
        var count = Math.Min(text.Length, maxColumns);
        var result = new List<MinimapPreviewSpan>(Math.Min(12, count));
        for (var i = 0; i < count;)
        {
            var character = text[i];
            if (char.IsWhiteSpace(character)) { ++i; continue; }
            var start = i;
            byte kind = (character == '"' || character == (char)39) ? (byte)1 :
                (character == '/' && i + 1 < count && text[i + 1] == '/') ||
                (character == '<' && i + 3 < count && text.AsSpan(i).StartsWith("<!--")) ? (byte)2 :
                char.IsLetterOrDigit(character) || character == '_' ? (byte)0 : (byte)3;
            if (kind == 2)
                i = count;
            else if (kind == 1)
            {
                ++i;
                while (i < count)
                {
                    if (text[i++] == character && (i < 2 || text[i - 2] != '\\')) break;
                }
            }
            else if (kind == 0)
            {
                while (i < count && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) ++i;
            }
            else
            {
                while (i < count && !char.IsWhiteSpace(text[i]) &&
                       !char.IsLetterOrDigit(text[i]) && text[i] != '"' && text[i] != (char)39) ++i;
            }
            result.Add(new MinimapPreviewSpan(start, Math.Max(1, i - start), kind));
        }
        return result.ToArray();
    }

    private static MinimapLineSummary Summarize(string text)
    {
        var indent = 0;
        while (indent < text.Length && (text[indent] == ' ' || text[indent] == '\t'))
            ++indent;
        return new MinimapLineSummary(text.Length, indent);
    }
}
