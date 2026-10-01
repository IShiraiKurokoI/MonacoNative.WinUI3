namespace MonacoNative.Language;

public sealed class LanguageRegistry
{
    private sealed record Entry(long Token, long Order, LanguageExtensionPoint Language);
    private sealed record ConfigEntry(long Token, long Order, LanguageConfiguration Configuration);
    private sealed record TokenizerEntry(long Token, long Order, MonarchLanguage Definition, MonarchTokenizer Tokenizer, long Generation);

    private readonly object _gate = new();
    private readonly Dictionary<string, List<Entry>> _languages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ConfigEntry>> _configurations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<TokenizerEntry>> _tokenizers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _encountered = new(StringComparer.OrdinalIgnoreCase);
    private long _nextToken = 1;
    private long _nextOrder = 1;
    private long _tokenizerGeneration = 1;

    public event EventHandler<string>? LanguageRegistered;
    public event EventHandler<string>? LanguageEncountered;
    public event EventHandler<string>? LanguageConfigurationChanged;
    public event EventHandler<string>? TokenizationChanged;

    public IDisposable Register(LanguageExtensionPoint language)
    {
        ArgumentNullException.ThrowIfNull(language);
        if (string.IsNullOrWhiteSpace(language.Id)) throw new ArgumentException("Language id must not be empty.", nameof(language));
        long token;
        lock (_gate)
        {
            token = _nextToken++;
            if (!_languages.TryGetValue(language.Id, out var list)) _languages[language.Id] = list = new();
            list.Add(new Entry(token, _nextOrder++, language));
        }
        LanguageRegistered?.Invoke(this, language.Id);
        return new DisposableAction(() => RemoveLanguage(language.Id, token));
    }

    public IReadOnlyList<LanguageExtensionPoint> GetLanguages()
    {
        lock (_gate)
            return _languages.Values.Where(v => v.Count > 0).Select(v => v.MaxBy(e => e.Order)!.Language).OrderBy(v => v.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public bool IsRegistered(string languageId)
    {
        lock (_gate) return _languages.TryGetValue(languageId, out var list) && list.Count > 0;
    }

    public void Encounter(string languageId)
    {
        if (string.IsNullOrWhiteSpace(languageId)) return;
        var fire = false;
        lock (_gate) fire = _encountered.Add(languageId);
        if (fire) LanguageEncountered?.Invoke(this, languageId);
    }

    public IDisposable SetLanguageConfiguration(string languageId, LanguageConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(languageId)) throw new ArgumentException("Language id must not be empty.", nameof(languageId));
        ArgumentNullException.ThrowIfNull(configuration);
        long token;
        lock (_gate)
        {
            token = _nextToken++;
            if (!_configurations.TryGetValue(languageId, out var list)) _configurations[languageId] = list = new();
            list.Add(new ConfigEntry(token, _nextOrder++, configuration));
        }
        LanguageConfigurationChanged?.Invoke(this, languageId);
        return new DisposableAction(() => RemoveConfiguration(languageId, token));
    }

    public LanguageConfiguration? GetLanguageConfiguration(string languageId)
    {
        lock (_gate)
            return _configurations.TryGetValue(languageId, out var list) && list.Count > 0 ? list.MaxBy(e => e.Order)!.Configuration : null;
    }

    public IDisposable SetMonarchTokensProvider(string languageId, MonarchLanguage language)
    {
        if (string.IsNullOrWhiteSpace(languageId)) throw new ArgumentException("Language id must not be empty.", nameof(languageId));
        ArgumentNullException.ThrowIfNull(language);
        var tokenizer = new MonarchTokenizer(language);
        long token;
        lock (_gate)
        {
            token = _nextToken++;
            if (!_tokenizers.TryGetValue(languageId, out var list)) _tokenizers[languageId] = list = new();
            list.Add(new TokenizerEntry(token, _nextOrder++, language, tokenizer, _tokenizerGeneration++));
        }
        TokenizationChanged?.Invoke(this, languageId);
        return new DisposableAction(() => RemoveTokenizer(languageId, token));
    }

    public bool TryGetMonarchTokenizer(string languageId, out MonarchTokenizer? tokenizer, out long generation)
    {
        lock (_gate)
        {
            if (_tokenizers.TryGetValue(languageId, out var list) && list.Count > 0)
            {
                var entry = list.MaxBy(e => e.Order)!;
                tokenizer = entry.Tokenizer;
                generation = entry.Generation;
                return true;
            }
        }
        tokenizer = null;
        generation = 0;
        return false;
    }

    private void RemoveLanguage(string id, long token)
    {
        lock (_gate) RemoveByToken(_languages, id, token, e => e.Token);
    }

    private void RemoveConfiguration(string id, long token)
    {
        lock (_gate) RemoveByToken(_configurations, id, token, e => e.Token);
        LanguageConfigurationChanged?.Invoke(this, id);
    }

    private void RemoveTokenizer(string id, long token)
    {
        lock (_gate)
        {
            RemoveByToken(_tokenizers, id, token, e => e.Token);
            _tokenizerGeneration++;
        }
        TokenizationChanged?.Invoke(this, id);
    }

    private static void RemoveByToken<T>(Dictionary<string, List<T>> map, string id, long token, Func<T, long> getToken)
    {
        if (!map.TryGetValue(id, out var list)) return;
        list.RemoveAll(e => getToken(e) == token);
        if (list.Count == 0) map.Remove(id);
    }
}
