using System.Text.RegularExpressions;

namespace MonacoNative.Renderer.WinUI3;

/// <summary>
/// Regex branch of editor Find. Plain literal searches stay on the native model's fast path.
/// A finite timeout and a match cap protect the UI against pathological patterns.
/// Offsets use UTF-16 code units, consistent with MonacoTextModel's position mapping.
/// </summary>
internal static class EditorFindEngine
{
    internal readonly record struct Hit(int Start, int Length, Match? RegexMatch = null);

    internal static (IReadOnlyList<Hit> Hits, string? Error) SearchRegex(
        string text, string pattern, bool matchCase, bool wholeWord,
        int start = 0, int end = int.MaxValue, int limit = 10_000)
    {
        if (string.IsNullOrEmpty(pattern)) return (Array.Empty<Hit>(), null);
        try
        {
            if (wholeWord) pattern = @"(?<![\p{L}\p{N}_])(?:" + pattern + @")(?![\p{L}\p{N}_])";
            var options = RegexOptions.Multiline | RegexOptions.CultureInvariant;
            if (!matchCase) options |= RegexOptions.IgnoreCase;
            var regex = new Regex(pattern, options, TimeSpan.FromMilliseconds(150));
            var hits = new List<Hit>();
            start = Math.Clamp(start, 0, text.Length);
            end = Math.Clamp(end, start, text.Length);
            foreach (Match match in regex.Matches(text))
            {
                if (!match.Success || match.Length == 0) continue;
                if (match.Index < start) continue;
                if (match.Index + match.Length > end) continue;
                hits.Add(new Hit(match.Index, match.Length, match));
                if (hits.Count >= limit) break;
            }
            return (hits, null);
        }
        catch (ArgumentException ex) { return (Array.Empty<Hit>(), ex.Message); }
        catch (RegexMatchTimeoutException) { return (Array.Empty<Hit>(), "Regular expression timed out (150 ms). Simplify your expression."); }
    }

    internal static string ExpandReplacement(string original, string replacement, Match? match, bool preserveCase)
    {
        var expanded = match is null ? replacement : match.Result(replacement);
        if (!preserveCase || string.IsNullOrEmpty(original) || string.IsNullOrEmpty(expanded)) return expanded;
        if (original.All(c => !char.IsLetter(c) || char.IsUpper(c)))
            return expanded.ToUpperInvariant();
        if (original.All(c => !char.IsLetter(c) || char.IsLower(c)))
            return expanded.ToLowerInvariant();
        if (char.IsUpper(original[0]) && original.Skip(1).All(c => !char.IsLetter(c) || char.IsLower(c)))
            return char.ToUpperInvariant(expanded[0]) + expanded[1..].ToLowerInvariant();
        return expanded;
    }
}
