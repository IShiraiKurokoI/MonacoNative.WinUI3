namespace MonacoNative.Language.BracketPairs;

internal sealed record TextEditInfo(BracketLength StartOffset, BracketLength EndOffset, BracketLength NewLength);

internal sealed class BeforeEditPositionMapper
{
    private int _nextEditIndex;
    private int _deltaOldToNewLineCount;
    private int _deltaOldToNewColumnCount;
    private int _deltaLineIndexInOld = -1;
    private readonly TextEditInfoCache[] _edits;

    public BeforeEditPositionMapper(IReadOnlyList<TextEditInfo> edits) =>
        _edits = edits.Select(TextEditInfoCache.From).ToArray();

    public BracketLength GetOffsetBeforeChange(BracketLength offset)
    {
        AdjustNextEdit(offset);
        return TranslateCurrentToOld(offset);
    }

    public BracketLength? GetDistanceToNextChange(BracketLength offset)
    {
        AdjustNextEdit(offset);
        var next = _nextEditIndex < _edits.Length ? _edits[_nextEditIndex] : null;
        if (next is null) return null;
        return BracketLength.DiffNonNegative(offset, TranslateOldToCurrent(next.StartOffset));
    }

    private BracketLength TranslateOldToCurrent(BracketLength oldOffset)
    {
        if (oldOffset.LineCount == _deltaLineIndexInOld)
            return BracketLength.From(oldOffset.LineCount + _deltaOldToNewLineCount, oldOffset.ColumnCount + _deltaOldToNewColumnCount);
        return BracketLength.From(oldOffset.LineCount + _deltaOldToNewLineCount, oldOffset.ColumnCount);
    }

    private BracketLength TranslateCurrentToOld(BracketLength currentOffset)
    {
        var oldLine = currentOffset.LineCount - _deltaOldToNewLineCount;
        if (oldLine == _deltaLineIndexInOld)
            return BracketLength.From(oldLine, currentOffset.ColumnCount - _deltaOldToNewColumnCount);
        return BracketLength.From(oldLine, currentOffset.ColumnCount);
    }

    private void AdjustNextEdit(BracketLength offset)
    {
        while (_nextEditIndex < _edits.Length)
        {
            var next = _edits[_nextEditIndex];
            var endAfterInCurrent = TranslateOldToCurrent(next.EndOffsetAfter);
            if (endAfterInCurrent <= offset)
            {
                _nextEditIndex++;
                var endAfter = endAfterInCurrent;
                var endBefore = TranslateOldToCurrent(next.EndOffsetBefore);
                var lineDelta = endAfter.LineCount - endBefore.LineCount;
                _deltaOldToNewLineCount += lineDelta;
                var previousColumnDelta = _deltaLineIndexInOld == next.EndOffsetBefore.LineCount ? _deltaOldToNewColumnCount : 0;
                var columnDelta = endAfter.ColumnCount - endBefore.ColumnCount;
                _deltaOldToNewColumnCount = previousColumnDelta + columnDelta;
                _deltaLineIndexInOld = next.EndOffsetBefore.LineCount;
            }
            else
            {
                break;
            }
        }
    }

    private sealed class TextEditInfoCache
    {
        private TextEditInfoCache(BracketLength startOffset, BracketLength endOffset, BracketLength newLength)
        {
            StartOffset = startOffset;
            EndOffsetBefore = endOffset;
            EndOffsetAfter = startOffset + newLength;
        }

        public BracketLength StartOffset { get; }
        public BracketLength EndOffsetBefore { get; }
        public BracketLength EndOffsetAfter { get; }
        public static TextEditInfoCache From(TextEditInfo edit) => new(edit.StartOffset, edit.EndOffset, edit.NewLength);
    }
}
