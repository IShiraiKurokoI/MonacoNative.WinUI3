namespace MonacoNative.Core;

public sealed record MonacoDiffOptions(
    bool IgnoreTrimWhitespace = true,
    bool ComputeMoves = true);

public readonly record struct MonacoDiffInnerChange(
    int OriginalLineNumber,
    int OriginalStartColumn,
    int OriginalEndColumn,
    int ModifiedLineNumber,
    int ModifiedStartColumn,
    int ModifiedEndColumn)
{
    public MonacoRange OriginalRange => new(
        OriginalLineNumber,
        OriginalStartColumn,
        OriginalLineNumber,
        OriginalEndColumn);

    public MonacoRange ModifiedRange => new(
        ModifiedLineNumber,
        ModifiedStartColumn,
        ModifiedLineNumber,
        ModifiedEndColumn);
}

public sealed record MonacoDiffChange(
    int OriginalStartLineNumber,
    int OriginalEndLineNumberExclusive,
    int ModifiedStartLineNumber,
    int ModifiedEndLineNumberExclusive,
    IReadOnlyList<MonacoDiffInnerChange> InnerChanges)
{
    public int OriginalLineCount => Math.Max(0, OriginalEndLineNumberExclusive - OriginalStartLineNumber);
    public int ModifiedLineCount => Math.Max(0, ModifiedEndLineNumberExclusive - ModifiedStartLineNumber);
    public bool IsInsertion => OriginalLineCount == 0 && ModifiedLineCount > 0;
    public bool IsDeletion => ModifiedLineCount == 0 && OriginalLineCount > 0;
    public bool IsModification => OriginalLineCount > 0 && ModifiedLineCount > 0;
}

public readonly record struct MonacoDiffMove(
    int OriginalStartLineNumber,
    int OriginalEndLineNumberExclusive,
    int ModifiedStartLineNumber,
    int ModifiedEndLineNumberExclusive)
{
    public int LineCount => Math.Max(0, OriginalEndLineNumberExclusive - OriginalStartLineNumber);
}

public sealed record MonacoDiffResult(
    IReadOnlyList<MonacoDiffChange> Changes,
    IReadOnlyList<MonacoDiffMove> Moves)
{
    public bool IsIdentical => Changes.Count == 0;
}

/// <summary>
/// Managed projection of the native advanced diff engine. All coordinates are
/// 1-based line/column coordinates and line-end coordinates are exclusive.
/// </summary>
public static class MonacoDiffService
{
    public static MonacoDiffResult Compute(
        string? original,
        string? modified,
        MonacoDiffOptions? options = null)
    {
        options ??= new MonacoDiffOptions();
        var handle = NativeMethods.mn_diff_compute(
            original ?? string.Empty,
            modified ?? string.Empty,
            options.IgnoreTrimWhitespace ? 1 : 0,
            options.ComputeMoves ? 1 : 0);
        if (handle == 0)
            throw new InvalidOperationException("Native diff computation failed.");

        try
        {
            var changeCount = NativeMethods.mn_diff_change_count(handle);
            var changes = new MonacoDiffChange[checked((int)changeCount)];
            for (nuint i = 0; i < changeCount; i++)
            {
                if (NativeMethods.mn_diff_get_change(handle, i, out var native) == 0)
                    throw new InvalidOperationException("Native diff change retrieval failed.");

                var inner = new MonacoDiffInnerChange[checked((int)native.InnerChangeCount)];
                for (nuint j = 0; j < native.InnerChangeCount; j++)
                {
                    if (NativeMethods.mn_diff_get_inner_change(handle, i, j, out var item) == 0)
                        throw new InvalidOperationException("Native inner diff retrieval failed.");
                    inner[checked((int)j)] = new MonacoDiffInnerChange(
                        item.OriginalLine,
                        item.OriginalStartColumn,
                        item.OriginalEndColumn,
                        item.ModifiedLine,
                        item.ModifiedStartColumn,
                        item.ModifiedEndColumn);
                }

                changes[checked((int)i)] = new MonacoDiffChange(
                    native.OriginalStartLine,
                    native.OriginalEndLineExclusive,
                    native.ModifiedStartLine,
                    native.ModifiedEndLineExclusive,
                    inner);
            }

            var moveCount = NativeMethods.mn_diff_move_count(handle);
            var moves = new MonacoDiffMove[checked((int)moveCount)];
            for (nuint i = 0; i < moveCount; i++)
            {
                if (NativeMethods.mn_diff_get_move(handle, i, out var native) == 0)
                    throw new InvalidOperationException("Native moved-block retrieval failed.");
                moves[checked((int)i)] = new MonacoDiffMove(
                    native.OriginalStartLine,
                    native.OriginalEndLineExclusive,
                    native.ModifiedStartLine,
                    native.ModifiedEndLineExclusive);
            }

            return new MonacoDiffResult(changes, moves);
        }
        finally
        {
            NativeMethods.mn_diff_destroy(handle);
        }
    }
}
