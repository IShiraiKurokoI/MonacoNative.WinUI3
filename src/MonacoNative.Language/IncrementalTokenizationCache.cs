namespace MonacoNative.Language;

/// <summary>
/// Incremental Monarch line-token cache. Besides Monaco-style line state propagation, this keeps
/// the previous tokenized suffix across an edit so consumers (notably BracketPairsTree) can find
/// the exact line where tokenization becomes identical again instead of invalidating to EOF.
/// </summary>
public sealed class IncrementalTokenizationCache
{
    private readonly ILanguageTextModel _model;
    private readonly MonacoLanguageService _service;
    private readonly Dictionary<int, MonarchLineTokens> _lines = new();
    private readonly Dictionary<int, MonarchLineTokens> _staleLines = new();
    private int _validThrough;
    private int _pendingInvalidFrom = int.MaxValue;
    private string _languageId = string.Empty;
    private long _generation = -1;

    public IncrementalTokenizationCache(ILanguageTextModel model, MonacoLanguageService service)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    public MonarchLineTokens GetLineTokens(int lineNumber)
    {
        if (lineNumber < 1 || lineNumber > _model.LineCount) throw new ArgumentOutOfRangeException(nameof(lineNumber));
        if (!_service.Languages.TryGetMonarchTokenizer(_model.LanguageId, out var tokenizer, out var generation) || tokenizer is null)
            return new MonarchLineTokens(Array.Empty<MonarchToken>(), new MonarchState());

        if (!string.Equals(_languageId, _model.LanguageId, StringComparison.OrdinalIgnoreCase) || _generation != generation)
        {
            Clear();
            _languageId = _model.LanguageId;
            _generation = generation;
        }

        if (_lines.TryGetValue(lineNumber, out var cached)) return cached;
        var state = _validThrough > 0 && _lines.TryGetValue(_validThrough, out var previous) ? previous.EndState : new MonarchState();
        var start = _validThrough + 1;
        if (start < 1) start = 1;
        for (var line = start; line <= lineNumber; line++)
        {
            var value = tokenizer.TokenizeLine(_model.GetLineContent(line), state);
            _lines[line] = value;
            state = value.EndState;
            _validThrough = line;
        }
        return _lines[lineNumber];
    }

    /// <summary>
    /// Applies the line mapping of a text edit while retaining the old tokenized suffix. This is
    /// the native-port equivalent of Monaco receiving later IModelTokensChangedEvent ranges.
    /// </summary>
    public void ApplyModelChange(int startLineNumber, int oldEndLineNumber, int newEndLineNumber)
    {
        startLineNumber = Math.Max(1, startLineNumber);
        oldEndLineNumber = Math.Max(startLineNumber, oldEndLineNumber);
        newEndLineNumber = Math.Max(startLineNumber, newEndLineNumber);
        var delta = newEndLineNumber - oldEndLineNumber;

        // Existing stale entries are already expressed in the current-before-this-edit line space.
        // Remap them through this edit so they continue to identify the original tokenized suffix.
        if (_staleLines.Count > 0)
        {
            var remapped = new Dictionary<int, MonarchLineTokens>();
            foreach (var pair in _staleLines)
            {
                if (pair.Key < startLineNumber)
                    remapped[pair.Key] = pair.Value;
                else if (pair.Key > oldEndLineNumber)
                    remapped[pair.Key + delta] = pair.Value;
            }
            _staleLines.Clear();
            foreach (var pair in remapped) _staleLines[pair.Key] = pair.Value;
        }

        // Preserve the currently tokenized unchanged suffix. Lines inside the replaced span do
        // not have a stable one-to-one mapping; lines after it shift by the line-count delta.
        foreach (var pair in _lines.Where(p => p.Key >= startLineNumber).ToArray())
        {
            if (pair.Key > oldEndLineNumber)
                _staleLines[pair.Key + delta] = pair.Value;
            _lines.Remove(pair.Key);
        }

        _validThrough = Math.Min(_validThrough, startLineNumber - 1);
        _pendingInvalidFrom = Math.Min(_pendingInvalidFrom, startLineNumber);
    }

    /// <summary>
    /// Retokenizes from the first invalid line until the first line whose tokens and end state are
    /// identical to the pre-edit suffix. Once that fixed point is reached, all following unchanged
    /// lines have the same tokenizer input state and therefore do not need token-change invalidation.
    /// </summary>
    public (int FromLine, int ToLine)? ConsumePendingChangedRange()
    {
        if (_pendingInvalidFrom == int.MaxValue)
            return null;
        var from = Math.Min(_pendingInvalidFrom, Math.Max(1, _model.LineCount));
        var changedThrough = from - 1;
        for (var line = from; line <= _model.LineCount; line++)
        {
            var current = GetLineTokens(line);
            if (_staleLines.TryGetValue(line, out var stale) && TokensEqual(current, stale))
                break;
            changedThrough = line;
        }

        _pendingInvalidFrom = int.MaxValue;
        _staleLines.Clear();
        return changedThrough >= from ? (from, changedThrough) : null;
    }

    public void InvalidateFrom(int lineNumber)
    {
        lineNumber = Math.Max(1, lineNumber);
        foreach (var key in _lines.Keys.Where(k => k >= lineNumber).ToArray()) _lines.Remove(key);
        _validThrough = Math.Min(_validThrough, lineNumber - 1);
        _pendingInvalidFrom = Math.Min(_pendingInvalidFrom, lineNumber);
        _staleLines.Clear();
    }

    public void Clear()
    {
        _lines.Clear();
        _staleLines.Clear();
        _validThrough = 0;
        _pendingInvalidFrom = int.MaxValue;
    }

    private static bool TokensEqual(MonarchLineTokens left, MonarchLineTokens right)
    {
        if (!left.EndState.Equals(right.EndState) || left.Tokens.Count != right.Tokens.Count)
            return false;
        for (var i = 0; i < left.Tokens.Count; i++)
        {
            if (!left.Tokens[i].Equals(right.Tokens[i]))
                return false;
        }
        return true;
    }
}
