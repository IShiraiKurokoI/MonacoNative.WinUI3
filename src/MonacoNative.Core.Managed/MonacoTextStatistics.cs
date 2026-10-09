using System.Text;

namespace MonacoNative.Core;

/// <summary>Document statistics independent of the WinUI sample/status-bar presentation.</summary>
public static class MonacoTextStatistics
{
    /// <summary>
    /// Counts Unicode scalar values rather than UTF-16 code units. Valid surrogate
    /// pairs count once; an unpaired surrogate counts as one replacement scalar.
    /// CR and LF are separate characters, while CRLF is one line break.
    /// </summary>
    public static long CountCharacters(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        long count = 0;
        foreach (var rune in text.EnumerateRunes()) count++;
        return count;
    }
}
