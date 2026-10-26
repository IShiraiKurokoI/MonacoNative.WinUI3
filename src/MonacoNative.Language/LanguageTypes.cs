using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace MonacoNative.Language;

public readonly record struct LanguagePosition(int LineNumber, int Column)
{
    public LanguagePosition ClampToPositive() => new(Math.Max(1, LineNumber), Math.Max(1, Column));
}

public readonly record struct LanguageRange(int StartLineNumber, int StartColumn, int EndLineNumber, int EndColumn)
{
    public LanguageRange Normalize()
    {
        if (StartLineNumber < EndLineNumber || (StartLineNumber == EndLineNumber && StartColumn <= EndColumn))
            return this;
        return new LanguageRange(EndLineNumber, EndColumn, StartLineNumber, StartColumn);
    }
}

public interface ILanguageTextModel
{
    Uri Uri { get; }
    string LanguageId { get; }
    ulong VersionId { get; }
    int LineCount { get; }
    string GetValue();
    string GetLineContent(int lineNumber);
    int GetLineMaxColumn(int lineNumber);
}

public sealed record LanguageExtensionPoint(
    string Id,
    IReadOnlyList<string>? Extensions = null,
    IReadOnlyList<string>? Filenames = null,
    IReadOnlyList<string>? FilenamePatterns = null,
    string? FirstLine = null,
    IReadOnlyList<string>? Aliases = null,
    IReadOnlyList<string>? Mimetypes = null);

public sealed record CharacterPair(string Open, string Close);

public sealed record CommentRule(string? LineComment = null, CharacterPair? BlockComment = null);

public sealed record AutoClosingPair(string Open, string Close, string? NotIn = null);

public enum EnterIndentAction { None, Indent, IndentOutdent, Outdent }

public sealed record OnEnterRule(string BeforeText, EnterIndentAction Action,
    string? AfterText = null, string? PreviousLineText = null, string? AppendText = null);

public sealed class LanguageConfiguration
{
    public CommentRule? Comments { get; init; }
    public IReadOnlyList<CharacterPair> Brackets { get; init; } = Array.Empty<CharacterPair>();
    public IReadOnlyList<CharacterPair>? ColorizedBracketPairs { get; init; }
    public IReadOnlyList<AutoClosingPair> AutoClosingPairs { get; init; } = Array.Empty<AutoClosingPair>();
    public IReadOnlyList<CharacterPair> SurroundingPairs { get; init; } = Array.Empty<CharacterPair>();
    public string? WordPattern { get; init; }
    public string? IncreaseIndentPattern { get; init; }
    public string? DecreaseIndentPattern { get; init; }
    public string? IndentNextLinePattern { get; init; }
    public string? UnIndentedLinePattern { get; init; }
    public IReadOnlyList<OnEnterRule> OnEnterRules { get; init; } = Array.Empty<OnEnterRule>();
}

public sealed record LanguageSelector(
    string? Language = null,
    string? Scheme = null,
    string? Pattern = null,
    bool Exclusive = false)
{
    public static LanguageSelector ForLanguage(string languageId) => new(languageId);
}

public static class LanguageSelectorScore
{
    public static int Score(LanguageSelector selector, Uri uri, string languageId)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(uri);
        languageId ??= string.Empty;

        var score = 0;
        if (!Match(selector.Language, languageId, out var languageScore)) return 0;
        score += languageScore;
        if (!Match(selector.Scheme, uri.Scheme, out var schemeScore)) return 0;
        score += schemeScore;
        if (!MatchPattern(selector.Pattern, uri, out var patternScore)) return 0;
        score += patternScore;
        if (score == 0) score = 5;
        return selector.Exclusive ? 1000 + score : score;
    }

    private static bool Match(string? selector, string value, out int score)
    {
        if (string.IsNullOrWhiteSpace(selector)) { score = 0; return true; }
        if (selector == "*") { score = 5; return true; }
        if (string.Equals(selector, value, StringComparison.OrdinalIgnoreCase)) { score = 10; return true; }
        score = 0;
        return false;
    }

    private static bool MatchPattern(string? pattern, Uri uri, out int score)
    {
        if (string.IsNullOrWhiteSpace(pattern)) { score = 0; return true; }
        var path = uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString;
        var regex = "^" + Regex.Escape(pattern)
            .Replace(@"\*\*", ".*", StringComparison.Ordinal)
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
            .Replace(@"\?", ".", StringComparison.Ordinal) + "$";
        if (Regex.IsMatch(path, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            score = pattern.Contains('*') || pattern.Contains('?') ? 5 : 10;
            return true;
        }
        score = 0;
        return false;
    }
}

internal sealed class DisposableAction : IDisposable
{
    private Action? _action;
    public DisposableAction(Action action) => _action = action;
    public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
}

internal static class ReadOnlyHelpers
{
    public static IReadOnlyList<T> Frozen<T>(IEnumerable<T>? values) =>
        new ReadOnlyCollection<T>((values ?? Array.Empty<T>()).ToArray());
}
