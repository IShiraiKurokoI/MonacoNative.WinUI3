using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;

namespace MonacoNative.Renderer.WinUI3;

internal readonly record struct LineLayoutCacheStatistics(
    long Requests,
    long Hits,
    long Misses,
    long EntriesCreated,
    long EntriesDisposed,
    long Evictions,
    long SpliceInvalidated,
    long SpliceMoved,
    int CurrentEntries);

internal sealed class LineLayoutCache : IDisposable
{
    private sealed class Entry
    {
        public required string Text { get; init; }
        public required CanvasTextLayout Layout { get; init; }
        public required float LineHeight { get; init; }
        public long Stamp { get; set; }
    }

    private readonly Dictionary<int, Entry> _entries = new();
    private readonly CanvasDevice _device;
    private readonly CanvasTextFormat _format;
    private readonly int _capacity;
    private readonly Func<string, CanvasTextDirection>? _directionResolver;
    private long _stamp;
    private long _requests;
    private long _hits;
    private long _misses;
    private long _entriesCreated;
    private long _entriesDisposed;
    private long _evictions;
    private long _spliceInvalidated;
    private long _spliceMoved;

    public LineLayoutCacheStatistics Statistics => new(
        _requests, _hits, _misses, _entriesCreated, _entriesDisposed, _evictions,
        _spliceInvalidated, _spliceMoved, _entries.Count);

    public void ResetStatistics()
    {
        _requests = 0;
        _hits = 0;
        _misses = 0;
        _entriesCreated = 0;
        _entriesDisposed = 0;
        _evictions = 0;
        _spliceInvalidated = 0;
        _spliceMoved = 0;
    }

    public LineLayoutCache(CanvasDevice device, CanvasTextFormat format, int capacity = 512, Func<string, CanvasTextDirection>? directionResolver = null)
    {
        _device = device;
        _format = format;
        _capacity = Math.Max(32, capacity);
        _directionResolver = directionResolver;
    }

    public CanvasTextLayout GetOrCreate(int viewLineNumber, string text, float lineHeight, Action<CanvasTextLayout>? configure = null)
    {
        _requests++;
        if (_entries.TryGetValue(viewLineNumber, out var entry) &&
            string.Equals(entry.Text, text, StringComparison.Ordinal) &&
            Math.Abs(entry.LineHeight - lineHeight) < 0.01f)
        {
            _hits++;
            entry.Stamp = ++_stamp;
            return entry.Layout;
        }

        _misses++;
        if (entry is not null)
        {
            entry.Layout.Dispose();
            _entriesDisposed++;
            _entries.Remove(viewLineNumber);
        }

        var layout = new CanvasTextLayout(_device, text, _format, 1_000_000f, Math.Max(1, lineHeight * 2));
        if (_directionResolver is not null)
            layout.Direction = _directionResolver(text);
        configure?.Invoke(layout);
        var next = new Entry { Text = text, Layout = layout, LineHeight = lineHeight, Stamp = ++_stamp };
        _entriesCreated++;
        _entries[viewLineNumber] = next;
        Trim();
        return layout;
    }

    public void ApplySplice(int firstViewLineNumber, int oldViewLineCount, int newViewLineCount)
    {
        var first = Math.Max(1, firstViewLineNumber);
        var oldCount = Math.Max(0, oldViewLineCount);
        var newCount = Math.Max(0, newViewLineCount);
        var oldEnd = checked(first + oldCount - 1);
        var delta = newCount - oldCount;

        // Layouts intersecting the changed projection are stale. Cached suffix layouts are
        // still valid because their text/format did not change; only their view-line keys
        // moved after the native projection splice.
        var changedKeys = _entries.Keys
            .Where(key => key >= first && key <= oldEnd)
            .ToArray();
        foreach (var key in changedKeys)
        {
            _entries[key].Layout.Dispose();
            _entriesDisposed++;
            _spliceInvalidated++;
            _entries.Remove(key);
        }

        if (delta == 0)
            return;

        var suffix = _entries
            .Where(pair => pair.Key > oldEnd)
            .Select(pair => (pair.Key, pair.Value))
            .ToArray();
        foreach (var (key, _) in suffix)
            _entries.Remove(key);
        foreach (var (key, entry) in suffix)
            _entries[checked(key + delta)] = entry;
        _spliceMoved += suffix.Length;
    }

    public void InvalidateFrom(int firstViewLineNumber)
    {
        var keys = _entries.Keys.Where(key => key >= firstViewLineNumber).ToArray();
        foreach (var key in keys)
        {
            _entries[key].Layout.Dispose();
            _entriesDisposed++;
            _entries.Remove(key);
        }
    }

    public void Clear()
    {
        foreach (var entry in _entries.Values)
        {
            entry.Layout.Dispose();
            _entriesDisposed++;
        }
        _entries.Clear();
    }

    private void Trim()
    {
        while (_entries.Count > _capacity)
        {
            var oldest = _entries.MinBy(pair => pair.Value.Stamp);
            if (oldest.Value is null)
                break;
            oldest.Value.Layout.Dispose();
            _entriesDisposed++;
            _evictions++;
            _entries.Remove(oldest.Key);
        }
    }

    public void Dispose() => Clear();
}
