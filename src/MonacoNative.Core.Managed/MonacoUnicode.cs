namespace MonacoNative.Core;

/// <summary>Shared Unicode segmentation used by the model, view and renderer.</summary>
public static class MonacoUnicode
{
    public static unsafe int PreviousGraphemeBoundary(string text, int offset) => Boundary(text, offset, NativeMethods.mn_unicode_previous_grapheme_boundary);
    public static unsafe int NextGraphemeBoundary(string text, int offset) => Boundary(text, offset, NativeMethods.mn_unicode_next_grapheme_boundary);
    public static unsafe int PreviousWordBoundary(string text, int offset) => Boundary(text, offset, NativeMethods.mn_unicode_previous_word_boundary);
    public static unsafe int NextWordBoundary(string text, int offset) => Boundary(text, offset, NativeMethods.mn_unicode_next_word_boundary);

    private unsafe delegate nuint NativeBoundary(char* text, nuint length, nuint offset);

    private static unsafe int Boundary(string? text, int offset, NativeBoundary boundary)
    {
        text ??= string.Empty;
        offset = Math.Clamp(offset, 0, text.Length);
        fixed (char* ptr = text)
            return checked((int)boundary(ptr, (nuint)text.Length, (nuint)offset));
    }
}
