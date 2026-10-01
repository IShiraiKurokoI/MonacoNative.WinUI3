using System.Text.RegularExpressions;

namespace MonacoNative.Language;

public sealed record MonarchAction(
    string Token,
    string? Next = null,
    IReadOnlyDictionary<string, string>? Cases = null);

public sealed class MonarchRule
{
    private MonarchRule(string? pattern, MonarchAction? action, string? include)
    {
        Pattern = pattern;
        Action = action;
        Include = include;
    }

    public string? Pattern { get; }
    public MonarchAction? Action { get; }
    public string? Include { get; }

    public static MonarchRule Match(string pattern, string token, string? next = null) =>
        new(pattern, new MonarchAction(token, next), null);

    public static MonarchRule Match(string pattern, MonarchAction action) =>
        new(pattern, action, null);

    public static MonarchRule IncludeState(string state) => new(null, null, state);
}

public sealed class MonarchLanguage
{
    public string DefaultToken { get; init; } = string.Empty;
    public bool IgnoreCase { get; init; }
    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> WordLists { get; init; } =
        new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, IReadOnlyList<MonarchRule>> Tokenizer { get; init; } =
        new Dictionary<string, IReadOnlyList<MonarchRule>>(StringComparer.Ordinal);
}

public readonly record struct MonarchToken(int StartIndex, int EndIndex, string Type)
{
    public int Length => Math.Max(0, EndIndex - StartIndex);
}

public sealed class MonarchState : IEquatable<MonarchState>
{
    private readonly string[] _stack;

    public MonarchState() : this(new[] { "root" }) { }
    private MonarchState(string[] stack) => _stack = stack.Length == 0 ? new[] { "root" } : stack;

    public string Current => _stack[^1];
    public IReadOnlyList<string> Stack => _stack;
    public MonarchState Clone() => new((string[])_stack.Clone());

    internal List<string> ToMutable() => new(_stack);
    internal static MonarchState FromMutable(List<string> stack) => new(stack.ToArray());

    public bool Equals(MonarchState? other) => other is not null && _stack.SequenceEqual(other._stack, StringComparer.Ordinal);
    public override bool Equals(object? obj) => obj is MonarchState other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in _stack) hash.Add(item, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

public sealed record MonarchLineTokens(IReadOnlyList<MonarchToken> Tokens, MonarchState EndState);

public sealed class MonarchTokenizer
{
    private sealed record CompiledRule(Regex Regex, MonarchAction Action);

    private readonly MonarchLanguage _language;
    private readonly Dictionary<string, IReadOnlyList<CompiledRule>> _compiled = new(StringComparer.Ordinal);

    public MonarchTokenizer(MonarchLanguage language)
    {
        _language = language ?? throw new ArgumentNullException(nameof(language));
        if (!_language.Tokenizer.ContainsKey("root"))
            throw new ArgumentException("A Monarch language must define tokenizer state 'root'.", nameof(language));
        foreach (var state in _language.Tokenizer.Keys)
            _compiled[state] = CompileState(state, new HashSet<string>(StringComparer.Ordinal));
    }

    public MonarchLineTokens TokenizeLine(string line, MonarchState? startState = null)
    {
        line ??= string.Empty;
        var stack = (startState ?? new MonarchState()).ToMutable();
        if (stack.Count == 0) stack.Add("root");
        var output = new List<MonarchToken>();
        var index = 0;

        while (index < line.Length)
        {
            var state = ResolveState(stack[^1]);
            var rules = _compiled[state];
            var matched = false;
            foreach (var rule in rules)
            {
                var match = rule.Regex.Match(line, index);
                if (!match.Success || match.Index != index || match.Length == 0)
                    continue;

                var token = ResolveToken(rule.Action, match.Value);
                if (!string.IsNullOrEmpty(token) && token != "@rematch")
                    output.Add(new MonarchToken(index, index + match.Length, token));
                ApplyNext(stack, rule.Action.Next);
                index += match.Length;
                matched = true;
                break;
            }

            if (matched)
                continue;

            if (!string.IsNullOrEmpty(_language.DefaultToken))
                output.Add(new MonarchToken(index, index + 1, _language.DefaultToken));
            index++;
        }

        return new MonarchLineTokens(output, MonarchState.FromMutable(stack));
    }

    private IReadOnlyList<CompiledRule> CompileState(string state, HashSet<string> path)
    {
        if (!path.Add(state))
            throw new ArgumentException($"Recursive Monarch include detected at state '{state}'.");
        try
        {
            if (!_language.Tokenizer.TryGetValue(state, out var rules))
                throw new ArgumentException($"Unknown Monarch state '{state}'.");
            var result = new List<CompiledRule>();
            foreach (var rule in rules)
            {
                if (!string.IsNullOrWhiteSpace(rule.Include))
                {
                    var include = NormalizeState(rule.Include!);
                    result.AddRange(CompileState(include, path));
                    continue;
                }
                if (rule.Pattern is null || rule.Action is null)
                    throw new ArgumentException($"Monarch state '{state}' contains an incomplete rule.");
                var options = RegexOptions.CultureInvariant;
                if (_language.IgnoreCase) options |= RegexOptions.IgnoreCase;
                result.Add(new CompiledRule(new Regex(@"\G(?:" + rule.Pattern + ")", options), rule.Action));
            }
            return result;
        }
        finally
        {
            path.Remove(state);
        }
    }

    private string ResolveState(string state)
    {
        state = NormalizeState(state);
        var candidate = state;
        while (true)
        {
            if (_compiled.ContainsKey(candidate)) return candidate;
            var dot = candidate.LastIndexOf('.');
            if (dot < 0) break;
            candidate = candidate[..dot];
        }
        return "root";
    }

    private string ResolveToken(MonarchAction action, string text)
    {
        if (action.Cases is null || action.Cases.Count == 0)
            return action.Token;

        foreach (var pair in action.Cases)
        {
            if (pair.Key == "@default") continue;
            if (pair.Key.StartsWith('@'))
            {
                var listName = pair.Key[1..];
                if (_language.WordLists.TryGetValue(listName, out var values) && values.Contains(text, StringComparerForLanguage()))
                    return pair.Value;
                continue;
            }
            if (string.Equals(pair.Key, text, _language.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return pair.Value;
        }
        return action.Cases.TryGetValue("@default", out var fallback) ? fallback : action.Token;
    }

    private StringComparer StringComparerForLanguage() => _language.IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string NormalizeState(string state) => state.StartsWith('@') ? state[1..] : state;

    private static void ApplyNext(List<string> stack, string? next)
    {
        if (string.IsNullOrWhiteSpace(next)) return;
        switch (next)
        {
            case "@push":
                stack.Add(stack[^1]);
                return;
            case "@pop":
                if (stack.Count > 1) stack.RemoveAt(stack.Count - 1);
                return;
            case "@popall":
                stack.Clear();
                stack.Add("root");
                return;
            default:
                var state = NormalizeState(next);
                if (string.IsNullOrWhiteSpace(state) || state == "root")
                {
                    stack.Clear();
                    stack.Add("root");
                }
                else if (!string.Equals(stack[^1], state, StringComparison.Ordinal))
                {
                    stack.Add(state);
                }
                return;
        }
    }
}
