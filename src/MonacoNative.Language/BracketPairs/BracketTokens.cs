using System.Text.RegularExpressions;

namespace MonacoNative.Language.BracketPairs;

internal enum BracketTreeTokenKind
{
    Text = 0,
    OpeningBracket = 1,
    ClosingBracket = 2,
}

internal sealed record BracketTreeToken(
    BracketLength Length,
    BracketTreeTokenKind Kind,
    int BracketId,
    SmallImmutableSet<int> BracketIds,
    SmallImmutableSet<int> ColorizedBracketIds,
    string Text);

/// <summary>
/// Per-language bracket token table matching Monaco's BracketTokens responsibilities.
/// </summary>
internal sealed class BracketTokens
{
    private readonly Dictionary<string, BracketTreeToken> _map;
    private Regex? _globalRegex;
    private bool _regexInitialized;

    private BracketTokens(Dictionary<string, BracketTreeToken> map) => _map = map;

    public static BracketTokens CreateFromLanguage(
        string languageId,
        LanguageConfiguration configuration,
        DenseKeyProvider<string> denseKeyProvider)
    {
        if (string.IsNullOrWhiteSpace(languageId)) throw new ArgumentException("Language id must not be empty.", nameof(languageId));
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(denseKeyProvider);

        int GetId(string openingText) => denseKeyProvider.GetKey($"{languageId}:::{openingText}");

        var map = new Dictionary<string, BracketTreeToken>(StringComparer.OrdinalIgnoreCase);
        var pairs = configuration.Brackets
            .Where(pair => !string.IsNullOrEmpty(pair.Open) && !string.IsNullOrEmpty(pair.Close) && !string.Equals(pair.Open, pair.Close, StringComparison.Ordinal))
            .ToArray();
        var colorizedPairs = new HashSet<string>(
            (configuration.ColorizedBracketPairs ?? configuration.Brackets)
                .Select(pair => $"{pair.Open}\0{pair.Close}"),
            StringComparer.OrdinalIgnoreCase);

        // Monaco builds all opening tokens first and all closing tokens second. Keeping this
        // order matters when a delimiter text is used in more than one bracket relation.
        foreach (var pair in pairs)
        {
            var openingId = GetId(pair.Open);
            var openingIds = SmallImmutableSet<int>.GetEmpty().Add(openingId, IdentityDenseKeyProvider.Instance);
            map[pair.Open.ToLowerInvariant()] = new BracketTreeToken(
                BracketLength.From(0, pair.Open.Length),
                BracketTreeTokenKind.OpeningBracket,
                openingId,
                openingIds,
                openingIds,
                pair.Open);
        }

        foreach (var closingGroup in pairs.GroupBy(pair => pair.Close, StringComparer.OrdinalIgnoreCase))
        {
            var openings = closingGroup.ToArray();
            var ids = SmallImmutableSet<int>.GetEmpty();
            var colorizedIds = SmallImmutableSet<int>.GetEmpty();
            foreach (var pair in openings)
            {
                var id = GetId(pair.Open);
                ids = ids.Add(id, IdentityDenseKeyProvider.Instance);
                if (colorizedPairs.Contains($"{pair.Open}\0{pair.Close}"))
                    colorizedIds = colorizedIds.Add(id, IdentityDenseKeyProvider.Instance);
            }
            var firstOpeningId = GetId(openings[0].Open);
            var closingText = openings[0].Close;
            map[closingText.ToLowerInvariant()] = new BracketTreeToken(
                BracketLength.From(0, closingText.Length),
                BracketTreeTokenKind.ClosingBracket,
                firstOpeningId,
                ids,
                colorizedIds,
                closingText);
        }

        return new BracketTokens(map);
    }

    public bool IsEmpty => _map.Count == 0;

    public BracketTreeToken? GetToken(string value) =>
        _map.TryGetValue(value.ToLowerInvariant(), out var token) ? token : null;

    public string? FindClosingTokenText(SmallImmutableSet<int> openingBracketIds)
    {
        foreach (var pair in _map)
        {
            if (pair.Value.Kind == BracketTreeTokenKind.ClosingBracket && pair.Value.BracketIds.Intersects(openingBracketIds))
                return pair.Key;
        }
        return null;
    }

    public string? GetRegexPattern()
    {
        if (IsEmpty)
            return null;
        var keys = _map.Keys.OrderBy(key => key, StringComparer.Ordinal).Reverse();
        return string.Join('|', keys.Select(PrepareBracketForRegex));
    }

    public Regex? GlobalRegex
    {
        get
        {
            if (_regexInitialized)
                return _globalRegex;
            var pattern = GetRegexPattern();
            _globalRegex = pattern is null
                ? null
                : new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
            _regexInitialized = true;
            return _globalRegex;
        }
    }

    private static string PrepareBracketForRegex(string value)
    {
        var escaped = Regex.Escape(value);
        if (Regex.IsMatch(value, @"^[\w ]+", RegexOptions.CultureInvariant))
            escaped = @"\b" + escaped;
        if (Regex.IsMatch(value, @"[\w ]+$", RegexOptions.CultureInvariant))
            escaped += @"\b";
        return escaped;
    }
}

internal sealed class LanguageAgnosticBracketTokens
{
    private readonly DenseKeyProvider<string> _denseKeyProvider = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BracketTokens> _tokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, LanguageConfiguration?> _getLanguageConfiguration;

    public LanguageAgnosticBracketTokens(Func<string, LanguageConfiguration?> getLanguageConfiguration) =>
        _getLanguageConfiguration = getLanguageConfiguration ?? throw new ArgumentNullException(nameof(getLanguageConfiguration));

    public bool DidLanguageChange(string languageId) => _tokens.ContainsKey(languageId);

    public BracketTokens GetSingleLanguageBracketTokens(string languageId)
    {
        if (_tokens.TryGetValue(languageId, out var tokens))
            return tokens;
        var configuration = _getLanguageConfiguration(languageId) ?? new LanguageConfiguration();
        tokens = BracketTokens.CreateFromLanguage(languageId, configuration, _denseKeyProvider);
        _tokens.Add(languageId, tokens);
        return tokens;
    }

    public BracketTreeToken? GetToken(string value, string languageId) =>
        GetSingleLanguageBracketTokens(languageId).GetToken(value);
}
