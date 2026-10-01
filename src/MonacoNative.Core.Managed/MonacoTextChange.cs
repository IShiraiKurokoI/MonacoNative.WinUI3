namespace MonacoNative.Core;

public readonly record struct MonacoTextChange(nuint Start, nuint End, string Text)
{
    public bool IsNoOp => Start == End && Text.Length == 0;
}

public static class MonacoTextChangeCalculator
{
    /// <summary>
    /// Computes one minimal contiguous replacement by removing the longest common
    /// prefix and suffix. This is the edit shape produced by the temporary WinUI
    /// presentation adapter for a single externally-produced text change.
    /// </summary>
    public static MonacoTextChange Compute(string? previous, string? updated)
    {
        previous ??= string.Empty;
        updated ??= string.Empty;

        var prefix = 0;
        var commonLimit = Math.Min(previous.Length, updated.Length);
        while (prefix < commonLimit && previous[prefix] == updated[prefix])
            prefix++;

        var suffix = 0;
        while (suffix < previous.Length - prefix &&
               suffix < updated.Length - prefix &&
               previous[previous.Length - suffix - 1] == updated[updated.Length - suffix - 1])
        {
            suffix++;
        }

        var oldEnd = previous.Length - suffix;
        var insertedLength = updated.Length - prefix - suffix;
        var inserted = insertedLength == 0 ? string.Empty : updated.Substring(prefix, insertedLength);
        return new MonacoTextChange((nuint)prefix, (nuint)oldEnd, inserted);
    }
}
