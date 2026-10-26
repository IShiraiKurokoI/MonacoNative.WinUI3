// Scoring and tie-breaking ported from microsoft/vscode (MIT).
// See THIRD_PARTY_NOTICES.md: filters.ts, completionModel.ts, suggest.ts, suggestMemory.ts.
namespace MonacoNative.Language;

public enum SnippetSortOrder { Inline, Top, Bottom, Hidden }
public enum SuggestSelectionMode { First, RecentlyUsed, RecentlyUsedByPrefix }
public sealed record CompletionMatch(CompletionItem Item, int Score, IReadOnlyList<int> Matches, int SourceIndex, int Distance = 0);
public sealed class CompletionRankingOptions
{
    public SnippetSortOrder Snippets { get; set; } = SnippetSortOrder.Inline;
    public SuggestSelectionMode Selection { get; set; } = SuggestSelectionMode.RecentlyUsed;
    public bool GracefulFiltering { get; set; } = true;
    public bool MatchOnWordStartOnly { get; set; } = true;
    // The host can supply semantic word/scope distance. A plain text distance is not a scope model.
    public Func<CompletionItem, int>? WordDistance { get; set; }
}

/// <summary>VS Code's dynamic-programming fuzzy scorer, with per-call buffers (thread safe).</summary>
public static class CompletionFuzzyScore
{
    private const int Limit = 128;
    private const int Impossible = -1_000_000;
    public sealed record Result(int Score, IReadOnlyList<int> Matches);

    public static Result? Match(string pattern, string word, bool firstMatchCanBeWeak = false,
        bool graceful = false, int patternStart = 0, int wordStart = 0)
    {
        var best = Score(pattern, word, firstMatchCanBeWeak, patternStart, wordStart);
        if (!graceful || pattern.Length < 3) return best;
        // fuzzyScoreGracefulAggressive: allow one adjacent transposition, never at the first letter.
        for (var pos = patternStart + 1; pos < Math.Min(7, pattern.Length - 1); pos++)
        {
            if (pattern[pos] == pattern[pos + 1]) continue;
            var chars = pattern.ToCharArray();
            (chars[pos], chars[pos + 1]) = (chars[pos + 1], chars[pos]);
            var candidate = Score(new string(chars), word, firstMatchCanBeWeak, patternStart, wordStart);
            if (candidate is not null && (best is null || candidate.Score - 3 > best.Score))
                best = candidate with { Score = candidate.Score - 3 };
        }
        return best;
    }

    private static Result? Score(string pattern, string word, bool weak, int ps, int ws)
    {
        var pl = Math.Min(pattern.Length, Limit); var wl = Math.Min(word.Length, Limit);
        if (ps >= pl) return new(-100, Array.Empty<int>());
        if (ws >= wl || pl - ps > wl - ws) return null;
        var p = pattern.ToLowerInvariant(); var w = word.ToLowerInvariant();
        var min = new int[Limit]; var max = new int[Limit];
        var at = ps;
        for (var j = ws; j < wl && at < pl; j++) if (p[at] == w[j]) min[at++] = j;
        if (at != pl) return null;
        at = pl - 1;
        for (var j = wl - 1; j >= ws && at >= ps; j--) if (p[at] == w[j]) max[at--] = j;
        var table = new int[pl - ps + 1, wl - ws + 1];
        var diagonal = new int[pl - ps + 1, wl - ws + 1];
        var arrows = new byte[pl - ps + 1, wl - ws + 1];
        var strong = false;
        for (var pi = ps; pi < pl; pi++)
        {
            var row = pi - ps + 1;
            var nextMax = pi + 1 < pl ? max[pi + 1] : wl;
            for (var wi = min[pi]; wi < nextMax; wi++)
            {
                var col = wi - ws + 1;
                var val = wi <= max[pi] ? CharacterScore(pattern, p, pi, ps, word, w, wi, ws, wl,
                    diagonal[row - 1, col - 1] == 0, ref strong) : Impossible;
                var diagScore = val == Impossible ? Impossible : val + table[row - 1, col - 1];
                var left = wi > min[pi] ? table[row, col - 1] + (diagonal[row, col - 1] > 0 ? -5 : 0) : Impossible;
                var leftLeft = wi > min[pi] + 1 && diagonal[row, col - 1] > 0
                    ? table[row, col - 2] + (diagonal[row, col - 2] > 0 ? -5 : 0) : Impossible;
                if (leftLeft != Impossible && leftLeft >= left && leftLeft >= diagScore)
                { table[row, col] = leftLeft; arrows[row, col] = 3; }
                else if (left != Impossible && left >= diagScore)
                { table[row, col] = left; arrows[row, col] = 2; }
                else if (diagScore != Impossible)
                { table[row, col] = diagScore; arrows[row, col] = 1; diagonal[row, col] = diagonal[row - 1, col - 1] + 1; }
                else return null;
            }
        }
        if (!strong && !weak) return null;
        var r = pl - ps; var c = wl - ws; var total = table[r, c];
        var backwards = 0; var maxMatchColumn = 0; var matches = new List<int>();
        while (r >= 1)
        {
            var dc = c;
            while (dc > 0 && arrows[r, dc] != 1) dc -= arrows[r, dc] == 3 ? 2 : 1;
            if (dc < 1) return null;
            if (backwards > 1 && p[ps + r - 1] == w[ws + c - 1] && !Upper(word, w, dc + ws - 1)
                && backwards + 1 > diagonal[r, dc]) dc = c;
            backwards = dc == c ? backwards + 1 : 1;
            if (maxMatchColumn == 0) maxMatchColumn = dc;
            r--; c = dc - 1; matches.Add(ws + c);
        }
        if (wl - ws == pl) total += 2;
        total -= maxMatchColumn - pl;
        matches.Reverse();
        return new(total, matches);
    }
    private static bool Upper(string original, string lower, int i) => i >= 0 && original[i] != lower[i];
    private static bool Separator(string value, int i) => i >= 0 && i < value.Length &&
        ("_- ./'\"\\:$<>()[]{}".Contains(value[i]) || char.IsSurrogate(value[i]));
    private static bool White(string value, int i) => i >= 0 && i < value.Length && value[i] is ' ' or '\t';
    private static int CharacterScore(string pattern, string p, int pi, int ps, string word, string w,
        int wi, int ws, int wl, bool newMatch, ref bool strong)
    {
        if (p[pi] != w[wi]) return Impossible;
        var score = 1; var gap = false;
        if (wi == pi - ps) score = pattern[pi] == word[wi] ? 7 : 5;
        else if (Upper(word, w, wi) && (wi == 0 || !Upper(word, w, wi - 1)))
        { score = pattern[pi] == word[wi] ? 7 : 5; gap = true; }
        else if (Separator(word, wi) && (wi == 0 || !Separator(word, wi - 1))) score = 5;
        else if (Separator(word, wi - 1) || White(word, wi - 1)) { score = 5; gap = true; }
        if (score > 1 && pi == ps) strong = true;
        gap |= Upper(word, w, wi) || Separator(word, wi - 1) || White(word, wi - 1);
        if (pi == ps && wi > ws) score -= gap ? 3 : 5;
        else if (pi != ps) score += newMatch ? (gap ? 2 : 0) : (gap ? 0 : 1);
        if (wi + 1 == wl) score -= gap ? 3 : 5;
        return score;
    }
}

/// <summary>Filtering/ranking is separate from preselection/MRU, as in Monaco.</summary>
public sealed class CompletionRanking
{
    private readonly Dictionary<string, (long Stamp, CompletionItemKind Kind, string Text)> _memory = new();
    private readonly Dictionary<string, (long Stamp, CompletionItemKind Kind, string Text)> _prefixMemory = new();
    private long _clock;
    public CompletionRankingOptions Options { get; } = new();

    public IReadOnlyList<CompletionMatch> Rank(IReadOnlyList<CompletionItem> items, string lineBeforeCaret, int lineNumber)
    {
        var initial = items.Select((item, index) => (item, index)).ToArray();
        Array.Sort(initial, (a, b) =>
        {
            var sort = !string.IsNullOrEmpty(a.item.SortText) && !string.IsNullOrEmpty(b.item.SortText)
                ? string.Compare(a.item.SortText.ToLowerInvariant(), b.item.SortText.ToLowerInvariant(), StringComparison.Ordinal) : 0;
            if (sort == 0) sort = string.Compare(a.item.Label.ToLowerInvariant(), b.item.Label.ToLowerInvariant(), StringComparison.Ordinal);
            if (sort == 0) sort = a.item.Kind.CompareTo(b.item.Kind);
            return sort != 0 ? sort : a.index.CompareTo(b.index);
        });
        var output = new List<CompletionMatch>(items.Count);
        for (var i = 0; i < initial.Length; i++)
        {
            var item = initial[i].item;
            if (Options.Snippets == SnippetSortOrder.Hidden && item.Kind == CompletionItemKind.Snippet) continue;
            var range = item.InsertRange ?? item.Range;
            var start = range is { } r && r.StartLineNumber == lineNumber
                ? Math.Clamp(r.StartColumn - 1, 0, lineBeforeCaret.Length) : WordStart(lineBeforeCaret);
            var pattern = lineBeforeCaret[start..];
            var ps = 0;
            while (ps < pattern.Length && pattern[ps] is ' ' or '\t') ps++;
            var match = CompletionFuzzyScore.Match(pattern, item.FilterText ?? item.Label,
                !Options.MatchOnWordStartOnly, Options.GracefulFiltering && items.Count <= 2000, ps);
            if (match is null) continue;
            var highlight = match.Matches;
            if (item.FilterText is not null && item.FilterText != item.Label)
            {
                highlight = Array.Empty<int>();
                for (var suffix = ps; suffix < Math.Min(pattern.Length, ps + 13); suffix++)
                {
                    var labelMatch = CompletionFuzzyScore.Match(pattern, item.Label, true, patternStart: suffix);
                    if (labelMatch is not null) { highlight = labelMatch.Matches; break; }
                }
            }
            output.Add(new(item, match.Score, highlight, i, Options.WordDistance?.Invoke(item) ?? 0));
        }
        output.Sort((a, b) =>
        {
            if (Options.Snippets is SnippetSortOrder.Top or SnippetSortOrder.Bottom)
            {
                var d = (a.Item.Kind == CompletionItemKind.Snippet ? 1 : 0) - (b.Item.Kind == CompletionItemKind.Snippet ? 1 : 0);
                if (d != 0) return Options.Snippets == SnippetSortOrder.Top ? -d : d;
            }
            var score = b.Score.CompareTo(a.Score);
            if (score == 0) score = a.Distance.CompareTo(b.Distance);
            return score != 0 ? score : a.SourceIndex.CompareTo(b.SourceIndex);
        });
        return output;
    }

    public int Select(IReadOnlyList<CompletionMatch> matches, string languageId, string prefix)
    {
        if (matches.Count == 0) return -1;
        if (Options.Selection == SuggestSelectionMode.RecentlyUsedByPrefix && prefix.Length > 0)
        {
            var languagePrefix = languageId + "\0";
            var key = languagePrefix + prefix;
            var remembered = _prefixMemory.Where(p => p.Key.StartsWith(languagePrefix, StringComparison.Ordinal) && key.StartsWith(p.Key, StringComparison.Ordinal))
                .OrderByDescending(p => p.Key.Length).Select(p => ((CompletionItemKind Kind, string Text)?)(p.Value.Kind, p.Value.Text)).FirstOrDefault();
            if (remembered is { } saved)
                for (var i = 0; i < matches.Count; i++)
                    if (matches[i].Item.Kind == saved.Kind && (matches[i].Item.SnippetText ?? matches[i].Item.InsertText ?? matches[i].Item.Label) == saved.Text) return i;
        }
        var selected = 0; long newest = -1;
        for (var i = 0; i < matches.Count && matches[i].Score == matches[0].Score; i++)
        {
            var item = matches[i].Item;
            if (item.Preselect) return i;
            if (Options.Selection != SuggestSelectionMode.RecentlyUsed || prefix.Length > 0 && char.IsWhiteSpace(prefix[^1])) continue;
            if (_memory.TryGetValue(Key(languageId, prefix, item.Label), out var entry) && entry.Kind == item.Kind &&
                entry.Text == (item.SnippetText ?? item.InsertText ?? item.Label) && entry.Stamp > newest)
            { selected = i; newest = entry.Stamp; }
        }
        return selected;
    }
    public void Remember(string languageId, string prefix, CompletionItem item)
    {
        var value = (++_clock, item.Kind, item.SnippetText ?? item.InsertText ?? item.Label);
        if (Options.Selection == SuggestSelectionMode.RecentlyUsedByPrefix)
        {
            _prefixMemory[languageId + "\0" + prefix] = value;
            if (_prefixMemory.Count > 300)
                foreach (var key in _prefixMemory.OrderBy(p => p.Value.Stamp).Take(100).Select(p => p.Key).ToArray()) _prefixMemory.Remove(key);
        }
        else if (Options.Selection == SuggestSelectionMode.RecentlyUsed)
        {
            _memory[Key(languageId, prefix, item.Label)] = value;
            if (_memory.Count > 300)
                foreach (var key in _memory.OrderBy(p => p.Value.Stamp).Take(100).Select(p => p.Key).ToArray()) _memory.Remove(key);
        }
    }
    private static string Key(string language, string prefix, string label) => language + "\0" + label;
    private static int WordStart(string text)
    { var i = text.Length; while (i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] is '_' or '$')) i--; return i; }
}
