namespace MonacoNative.Lsp;

public readonly record struct LspTextPosition(int Line, int Character);
public readonly record struct LspIncrementalTextChange(LspTextPosition Start, LspTextPosition End, string Text);

public static class LspTextSynchronization
{
    /// <summary>
    /// Computes one minimal contiguous UTF-16 edit that transforms <paramref name="oldText"/>
    /// into <paramref name="newText"/>. LSP's default UTF-16 position encoding therefore maps
    /// directly to .NET string code-unit offsets.
    /// </summary>
    public static LspIncrementalTextChange? ComputeMinimalChange(string oldText, string newText)
    {
        oldText ??= string.Empty;
        newText ??= string.Empty;
        if (string.Equals(oldText, newText, StringComparison.Ordinal)) return null;

        var maxPrefix = Math.Min(oldText.Length, newText.Length);
        var prefix = 0;
        while (prefix < maxPrefix && oldText[prefix] == newText[prefix]) prefix++;
        while (prefix > 0 && (SplitsUnsafeUtf16Boundary(oldText, prefix) || SplitsUnsafeUtf16Boundary(newText, prefix)))
            prefix--;

        var oldRemaining = oldText.Length - prefix;
        var newRemaining = newText.Length - prefix;
        var suffix = 0;
        while (suffix < oldRemaining && suffix < newRemaining &&
               oldText[oldText.Length - 1 - suffix] == newText[newText.Length - 1 - suffix]) suffix++;

        var oldEndOffset = oldText.Length - suffix;
        var newEndOffset = newText.Length - suffix;
        while (suffix > 0 && (SplitsUnsafeUtf16Boundary(oldText, oldEndOffset) || SplitsUnsafeUtf16Boundary(newText, newEndOffset)))
        {
            suffix--;
            oldEndOffset = oldText.Length - suffix;
            newEndOffset = newText.Length - suffix;
        }

        return new LspIncrementalTextChange(
            OffsetToPosition(oldText, prefix),
            OffsetToPosition(oldText, oldEndOffset),
            newText.Substring(prefix, Math.Max(0, newEndOffset - prefix)));
    }

    public static LspTextPosition OffsetToPosition(string text, int offset)
    {
        text ??= string.Empty;
        offset = Math.Clamp(offset, 0, text.Length);
        var line = 0;
        var lineStart = 0;
        for (var i = 0; i < offset; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < offset && text[i + 1] == '\n') i++;
                line++;
                lineStart = i + 1;
            }
            else if (text[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }
        return new LspTextPosition(line, offset - lineStart);
    }

    private static bool SplitsUnsafeUtf16Boundary(string text, int offset) =>
        offset > 0 && offset < text.Length &&
        ((char.IsHighSurrogate(text[offset - 1]) && char.IsLowSurrogate(text[offset])) ||
         (text[offset - 1] == '\r' && text[offset] == '\n'));
}
