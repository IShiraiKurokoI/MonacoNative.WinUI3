using System.Text.RegularExpressions;

namespace MonacoNative.Language;

/// <summary>A low-priority provider used only when no authoritative completion provider responds.</summary>
public interface IFallbackCompletionProvider : ICompletionItemProvider { }

/// <summary>
/// Local keyword and current-document word suggestions. This is NOT a semantic language server:
/// contextual type/member/symbol completion requires a native semantic implementation.
/// </summary>
public sealed class WordCompletionProvider : IFallbackCompletionProvider
{
    private static readonly Regex Words = new(@"[A-Za-z_$][A-Za-z0-9_$]*", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly IReadOnlyCollection<string> _keywords;
    private readonly IReadOnlyCollection<string> _builtins;

    public WordCompletionProvider(IEnumerable<string> keywords, IEnumerable<string>? builtins = null)
    {
        _keywords = (keywords ?? throw new ArgumentNullException(nameof(keywords))).Distinct(StringComparer.Ordinal).ToArray();
        _builtins = (builtins ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
    }

    public ValueTask<CompletionList?> ProvideCompletionItemsAsync(ILanguageTextModel model, LanguagePosition position,
        CompletionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        cancellationToken.ThrowIfCancellationRequested();
        var lineNumber = Math.Clamp(position.LineNumber, 1, model.LineCount);
        var line = model.GetLineContent(lineNumber);
        var offset = Math.Clamp(position.Column - 1, 0, line.Length);
        var start = offset;
        while (start > 0 && (char.IsLetterOrDigit(line[start - 1]) || line[start - 1] is '_' or '$')) start--;
        var prefix = line[start..offset];
        if (offset > 0 && (line[offset - 1] is '.' or '>' || (offset >= 2 && line[(offset - 2)..offset] == "::")))
            return ValueTask.FromResult<CompletionList?>(new CompletionList(Array.Empty<CompletionItem>()));
        // Only token-independent keywords and words are available offline. Semantic context,
        // including members, imports, and symbol kinds, requires the native language engine.
        // JSON, HTML and CSS use dedicated syntax-specific fallback providers.
        var range = new LanguageRange(lineNumber, start + 1, lineNumber, offset + 1);
        var result = new List<CompletionItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string word, CompletionItemKind kind)
        {
            if (seen.Add(word) && (prefix.Length == 0 || word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) &&
                !string.Equals(word, prefix, StringComparison.Ordinal))
                result.Add(new CompletionItem(word, kind, InsertText: word, Range: range));
        }
        foreach (var keyword in _keywords) Add(keyword, CompletionItemKind.Keyword);
        foreach (var builtin in _builtins) Add(builtin, CompletionItemKind.Variable);
        // Local identifiers are not symbol resolution; they are a lightweight fallback.
        var value = model.GetValue();
        if (value.Length < 1_000_000)
        {
            foreach (Match match in Words.Matches(value))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (result.Count >= 200) break;
                Add(match.Value, CompletionItemKind.Text);
            }
        }
        return ValueTask.FromResult<CompletionList?>(new CompletionList(result));
    }
}
