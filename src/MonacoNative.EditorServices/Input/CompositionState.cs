namespace MonacoNative.Input;

public enum ImeCompositionSegmentKind
{
    None = 0,
    Unconverted = 1,
    Converted = 2,
    TargetUnconverted = 3,
    TargetConverted = 4,
}

public enum ImeUnderlineStyle
{
    Undefined = 0,
    None = 1,
    Single = 2,
    Words = 3,
    Double = 4,
    Dotted = 5,
    Dash = 6,
    DashDot = 7,
    DashDotDot = 8,
    Wave = 9,
    Thick = 10,
    Thin = 11,
    DoubleWave = 12,
    HeavyWave = 13,
    LongDash = 14,
    ThickDash = 15,
    ThickDashDot = 16,
    ThickDashDotDot = 17,
    ThickDotted = 18,
    ThickLongDash = 19,
}

public readonly record struct ImeTextRange(int Start, int End)
{
    public int Length => Math.Max(0, End - Start);
    public bool IsEmpty => End <= Start;

    public ImeTextRange Normalize(int textLength)
    {
        textLength = Math.Max(0, textLength);
        var start = Math.Clamp(Math.Min(Start, End), 0, textLength);
        var end = Math.Clamp(Math.Max(Start, End), start, textLength);
        return new ImeTextRange(start, end);
    }

    public ImeTextRange Intersect(ImeTextRange other)
    {
        var start = Math.Max(Start, other.Start);
        var end = Math.Min(End, other.End);
        return new ImeTextRange(start, Math.Max(start, end));
    }
}

public readonly record struct ImeCompositionSegment(
    ImeTextRange Range,
    ImeCompositionSegmentKind Kind,
    ImeUnderlineStyle UnderlineStyle,
    int? TextColorRole = null,
    int? BackgroundColorRole = null,
    int? UnderlineColorRole = null)
{
    public bool IsTarget => Kind is ImeCompositionSegmentKind.TargetUnconverted or ImeCompositionSegmentKind.TargetConverted;
}

/// <summary>
/// Portable composition decoration model. Windows TSF feeds it UTF-16 ACP ranges; the
/// renderer consumes a normalized, gap-free segment list. Gaps deliberately receive
/// a Monaco-style default single underline so transient IME format timing never makes
/// active composition visually disappear.
/// </summary>
public sealed class ImeCompositionState
{
    private readonly List<ImeCompositionSegment> _segments = new();

    public bool IsComposing { get; private set; }
    public ImeTextRange CompositionRange { get; private set; }
    public IReadOnlyList<ImeCompositionSegment> ExplicitSegments => _segments;

    public void Begin(ImeTextRange range, int textLength)
    {
        IsComposing = true;
        CompositionRange = range.Normalize(textLength);
        _segments.Clear();
    }

    public void SetCompositionRange(ImeTextRange range, int textLength)
    {
        CompositionRange = range.Normalize(textLength);
        TrimToComposition();
    }

    public void ClearFormatting() => _segments.Clear();

    public void ApplyTextUpdate(ImeTextRange replacedRange, int insertedLength, int resultingTextLength)
    {
        if (!IsComposing)
            return;
        insertedLength = Math.Max(0, insertedLength);
        var oldLength = Math.Max(0, resultingTextLength - insertedLength + replacedRange.Length);
        var edit = replacedRange.Normalize(oldLength);
        var start = TransformOffset(CompositionRange.Start, edit, insertedLength, isEnd: false);
        var end = TransformOffset(CompositionRange.End, edit, insertedLength, isEnd: true);
        CompositionRange = new ImeTextRange(start, Math.Max(start, end)).Normalize(resultingTextLength);
        _segments.Clear();
    }

    public void ApplyFormat(ImeCompositionSegment segment, int textLength)
    {
        if (!IsComposing)
            return;

        var range = segment.Range.Normalize(textLength).Intersect(CompositionRange);
        if (range.IsEmpty)
            return;

        for (var index = _segments.Count - 1; index >= 0; --index)
        {
            var existing = _segments[index];
            if (existing.Range.End <= range.Start || existing.Range.Start >= range.End)
                continue;

            _segments.RemoveAt(index);
            if (existing.Range.Start < range.Start)
                _segments.Add(existing with { Range = new ImeTextRange(existing.Range.Start, range.Start) });
            if (existing.Range.End > range.End)
                _segments.Add(existing with { Range = new ImeTextRange(range.End, existing.Range.End) });
        }

        _segments.Add(segment with { Range = range });
        NormalizeSegments();
    }

    public IReadOnlyList<ImeCompositionSegment> GetRenderableSegments()
    {
        if (!IsComposing || CompositionRange.IsEmpty)
            return Array.Empty<ImeCompositionSegment>();

        var result = new List<ImeCompositionSegment>();
        var cursor = CompositionRange.Start;
        foreach (var segment in _segments.OrderBy(s => s.Range.Start).ThenBy(s => s.Range.End))
        {
            var clipped = segment.Range.Intersect(CompositionRange);
            if (clipped.IsEmpty)
                continue;
            if (cursor < clipped.Start)
                result.Add(DefaultSegment(new ImeTextRange(cursor, clipped.Start)));
            result.Add(segment with { Range = clipped });
            cursor = Math.Max(cursor, clipped.End);
        }
        if (cursor < CompositionRange.End)
            result.Add(DefaultSegment(new ImeTextRange(cursor, CompositionRange.End)));
        return result;
    }

    public void Complete()
    {
        IsComposing = false;
        CompositionRange = default;
        _segments.Clear();
    }

    public static ImeCompositionSegment DefaultSegment(ImeTextRange range) =>
        new(range, ImeCompositionSegmentKind.Unconverted, ImeUnderlineStyle.Single);

    private static int TransformOffset(int offset, ImeTextRange edit, int insertedLength, bool isEnd)
    {
        var delta = insertedLength - edit.Length;
        if (offset < edit.Start)
            return offset;
        if (offset > edit.End)
            return offset + delta;
        if (edit.IsEmpty && offset == edit.Start)
            return isEnd ? offset + insertedLength : offset;
        if (offset == edit.End)
            return offset + delta;
        return isEnd ? edit.Start + insertedLength : edit.Start;
    }

    private void TrimToComposition()
    {
        for (var i = _segments.Count - 1; i >= 0; --i)
        {
            var clipped = _segments[i].Range.Intersect(CompositionRange);
            if (clipped.IsEmpty)
                _segments.RemoveAt(i);
            else
                _segments[i] = _segments[i] with { Range = clipped };
        }
        NormalizeSegments();
    }

    private void NormalizeSegments()
    {
        _segments.Sort(static (a, b) => a.Range.Start != b.Range.Start
            ? a.Range.Start.CompareTo(b.Range.Start)
            : a.Range.End.CompareTo(b.Range.End));

        for (var i = _segments.Count - 1; i > 0; --i)
        {
            var left = _segments[i - 1];
            var right = _segments[i];
            if (left.Range.End != right.Range.Start || !EquivalentStyle(left, right))
                continue;
            _segments[i - 1] = left with { Range = new ImeTextRange(left.Range.Start, right.Range.End) };
            _segments.RemoveAt(i);
        }
    }

    private static bool EquivalentStyle(ImeCompositionSegment a, ImeCompositionSegment b) =>
        a.Kind == b.Kind &&
        a.UnderlineStyle == b.UnderlineStyle &&
        a.TextColorRole == b.TextColorRole &&
        a.BackgroundColorRole == b.BackgroundColorRole &&
        a.UnderlineColorRole == b.UnderlineColorRole;
}

public enum ImeInputCapability
{
    BasicCharacterFallback = 0,
    NativeTsf = 1,
}
