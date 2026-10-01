namespace MonacoNative.Language.BracketPairs;

/// <summary>
/// Packed non-negative text length used by Monaco's bracket-pair tree. The upper 26 bits
/// store the line count and the lower 26 bits store the column count. This mirrors Monaco
/// 0.57 / VS Code 6a598d4 bracketPairsTree/length.ts so lengths can be compared as integers
/// and composed without allocating line/column objects.
/// </summary>
internal readonly record struct BracketLength : IComparable<BracketLength>
{
    internal const ulong Factor = 1UL << 26;
    internal const ulong ComponentMask = Factor - 1;

    private BracketLength(ulong value) => Value = value;

    public ulong Value { get; }
    public static BracketLength Zero => new(0);
    public int LineCount => checked((int)(Value / Factor));
    public int ColumnCount => checked((int)(Value % Factor));
    public bool IsZero => Value == 0;

    public static BracketLength From(int lineCount, int columnCount)
    {
        if ((uint)lineCount >= Factor)
            throw new ArgumentOutOfRangeException(nameof(lineCount));
        if ((uint)columnCount >= Factor)
            throw new ArgumentOutOfRangeException(nameof(columnCount));
        return new BracketLength(checked((ulong)lineCount * Factor + (uint)columnCount));
    }

    public static BracketLength Diff(int startLineCount, int startColumnCount, int endLineCount, int endColumnCount) =>
        startLineCount != endLineCount
            ? From(checked(endLineCount - startLineCount), endColumnCount)
            : From(0, checked(endColumnCount - startColumnCount));

    public static BracketLength Add(BracketLength left, BracketLength right)
    {
        var value = checked(left.Value + right.Value);
        if (right.Value >= Factor)
            value -= left.Value % Factor;
        return new BracketLength(value);
    }

    public static BracketLength DiffNonNegative(BracketLength left, BracketLength right)
    {
        if (right.Value <= left.Value)
            return Zero;
        if (left.LineCount == right.LineCount)
            return From(0, right.ColumnCount - left.ColumnCount);
        return From(right.LineCount - left.LineCount, right.ColumnCount);
    }

    public static BracketLength OfString(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lineCount = 0;
        var lastLineStart = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
                continue;
            lineCount++;
            lastLineStart = i + 1;
        }
        return From(lineCount, text.Length - lastLineStart);
    }

    public int CompareTo(BracketLength other) => Value.CompareTo(other.Value);
    public static bool operator <(BracketLength left, BracketLength right) => left.Value < right.Value;
    public static bool operator >(BracketLength left, BracketLength right) => left.Value > right.Value;
    public static bool operator <=(BracketLength left, BracketLength right) => left.Value <= right.Value;
    public static bool operator >=(BracketLength left, BracketLength right) => left.Value >= right.Value;
    public static BracketLength operator +(BracketLength left, BracketLength right) => Add(left, right);
}
