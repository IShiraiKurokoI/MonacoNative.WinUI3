namespace MonacoNative.Renderer.WinUI3;

internal readonly record struct LineRangeIndexStatistics(
    long QueryCount,
    long NodesVisited,
    long MatchesReturned,
    long LastNodesVisited);

/// <summary>
/// Immutable augmented interval index for line-oriented renderer data.
/// Snapshots such as diagnostics/diff decorations are replaced atomically, so a compact
/// balanced tree gives Monaco-style O(log n + k) visible-range queries without carrying
/// mutable editor-model decoration state into the renderer.
/// </summary>
internal sealed class LineRangeIndex<T>
{
    private readonly Entry[] _entries;
    private readonly int[] _subtreeMaxEnd;
    private readonly int _root;
    private long _queryCount;
    private long _nodesVisited;
    private long _matchesReturned;
    private long _lastNodesVisited;

    private readonly record struct Entry(T Value, int StartLine, int EndLine, int Order);

    public static LineRangeIndex<T> Empty { get; } = new(Array.Empty<T>(), static _ => (1, 0));

    public LineRangeIndex(IEnumerable<T> values, Func<T, (int StartLine, int EndLine)> rangeSelector)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(rangeSelector);

        _entries = values
            .Select((value, order) =>
            {
                var (start, end) = rangeSelector(value);
                if (end < start) (start, end) = (end, start);
                return new Entry(value, Math.Max(1, start), Math.Max(1, end), order);
            })
            .OrderBy(entry => entry.StartLine)
            .ThenBy(entry => entry.EndLine)
            .ThenBy(entry => entry.Order)
            .ToArray();

        if (_entries.Length == 0)
        {
            _subtreeMaxEnd = Array.Empty<int>();
            _root = -1;
            return;
        }

        _subtreeMaxEnd = new int[_entries.Length];
        _root = BuildMetadata(0, _entries.Length - 1);
    }

    public int Count => _entries.Length;

    public LineRangeIndexStatistics Statistics => new(
        _queryCount, _nodesVisited, _matchesReturned, _lastNodesVisited);

    public void ResetStatistics()
    {
        _queryCount = 0;
        _nodesVisited = 0;
        _matchesReturned = 0;
        _lastNodesVisited = 0;
    }

    public IReadOnlyList<T> Query(int startLine, int endLine)
    {
        _queryCount++;
        var beforeVisited = _nodesVisited;
        if (_root < 0)
        {
            _lastNodesVisited = 0;
            return Array.Empty<T>();
        }
        if (endLine < startLine)
            (startLine, endLine) = (endLine, startLine);
        startLine = Math.Max(1, startLine);
        endLine = Math.Max(startLine, endLine);

        var matches = new List<Entry>();
        QueryNode(0, _entries.Length - 1, startLine, endLine, matches);
        _lastNodesVisited = _nodesVisited - beforeVisited;
        _matchesReturned += matches.Count;
        if (matches.Count == 0)
            return Array.Empty<T>();

        // Rendering order is observable for overlapping translucent decorations. The tree is
        // start-sorted for searching, so restore the caller's snapshot order before drawing.
        matches.Sort(static (left, right) => left.Order.CompareTo(right.Order));
        return matches.Select(static entry => entry.Value).ToArray();
    }

    private int BuildMetadata(int low, int high)
    {
        if (low > high)
            return -1;
        var mid = low + ((high - low) / 2);
        var left = BuildMetadata(low, mid - 1);
        var right = BuildMetadata(mid + 1, high);
        var maxEnd = _entries[mid].EndLine;
        if (left >= 0) maxEnd = Math.Max(maxEnd, _subtreeMaxEnd[left]);
        if (right >= 0) maxEnd = Math.Max(maxEnd, _subtreeMaxEnd[right]);
        _subtreeMaxEnd[mid] = maxEnd;
        return mid;
    }

    private void QueryNode(int low, int high, int queryStart, int queryEnd, List<Entry> matches)
    {
        if (low > high)
            return;
        _nodesVisited++;
        var mid = low + ((high - low) / 2);
        if (_subtreeMaxEnd[mid] < queryStart)
            return;

        if (low <= mid - 1)
        {
            var leftRoot = low + (((mid - 1) - low) / 2);
            if (_subtreeMaxEnd[leftRoot] >= queryStart)
                QueryNode(low, mid - 1, queryStart, queryEnd, matches);
        }

        var entry = _entries[mid];
        if (entry.StartLine <= queryEnd && entry.EndLine >= queryStart)
            matches.Add(entry);

        // Every entry in the right subtree starts at or after this entry's start. Once the
        // current start is beyond queryEnd, the whole right subtree can be pruned.
        if (entry.StartLine <= queryEnd && mid + 1 <= high)
            QueryNode(mid + 1, high, queryStart, queryEnd, matches);
    }
}
