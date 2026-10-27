namespace MonacoNative.Language;

/// <summary>The arguments written before the caret in the innermost unfinished call.</summary>
public sealed record SignatureCallContext(int ActiveArgument, IReadOnlyList<string> Arguments)
{
    public int SuppliedArguments => Arguments.Count(a => !string.IsNullOrWhiteSpace(a));
}

/// <summary>
/// Locates the active argument list without depending on a particular language server.
/// This is a fallback for incomplete code; the provider remains responsible for semantic resolution.
/// </summary>
public static class SignatureCallScanner
{
    private sealed class Frame(char opening, int start)
    {
        public char Opening { get; } = opening;
        public int Start { get; } = start;
        public List<int> Commas { get; } = new();
    }

    public static SignatureCallContext? Scan(string textBeforeCaret)
    {
        ArgumentNullException.ThrowIfNull(textBeforeCaret);
        var stack = new Stack<Frame>();
        var quoted = '\0';
        var escaped = false;
        var verbatim = false;
        var lineComment = false;
        var blockComment = false;
        for (var i = 0; i < textBeforeCaret.Length; i++)
        {
            var c = textBeforeCaret[i];
            var next = i + 1 < textBeforeCaret.Length ? textBeforeCaret[i + 1] : '\0';
            if (lineComment)
            {
                if (c == '\n') lineComment = false;
                continue;
            }
            if (blockComment)
            {
                if (c == '*' && next == '/') { blockComment = false; i++; }
                continue;
            }
            if (quoted != '\0')
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == quoted)
                {
                    // C# verbatim strings escape a quote by doubling it.
                    if (verbatim && c == '"' && next == '"')
                    { i++; continue; }
                    quoted = '\0';
                    verbatim = false;
                }
                continue;
            }
            if (c == '/' && next == '/') { lineComment = true; i++; continue; }
            if (c == '/' && next == '*') { blockComment = true; i++; continue; }
            if (c is '\'' or '"' or '`') { quoted = c; verbatim = c == '"' && i > 0 && textBeforeCaret[i - 1] == '@'; continue; }
            if (c is '(' or '[' or '{') { stack.Push(new Frame(c, i + 1)); continue; }
            // Do not count commas inside closed generic type argument lists as call arguments.
            // The suffix check avoids interpreting ordinary comparisons such as a < b > c as generics.
            if (c == '<' && IsGenericOpening(textBeforeCaret, i))
            { stack.Push(new Frame('<', i + 1)); continue; }
            if (c == '>' && stack.Count != 0 && stack.Peek().Opening == '<')
            { stack.Pop(); continue; }
            if (c is ')' or ']' or '}')
            {
                if (stack.Count == 0) continue;
                var expected = c == ')' ? '(' : c == ']' ? '[' : '{';
                if (stack.Peek().Opening == expected) stack.Pop();
                else stack.Clear();
                continue;
            }
            if (c == ',' && stack.Count != 0 && stack.Peek().Opening == '(')
                stack.Peek().Commas.Add(i);
        }
        // A comma in an array, object literal or nested call must not change the outer argument.
        if (stack.Count == 0 || stack.Peek().Opening != '(') return null;
        var call = stack.Peek();
        var arguments = new List<string>(call.Commas.Count + 1);
        var start = call.Start;
        foreach (var comma in call.Commas)
        {
            arguments.Add(textBeforeCaret[start..comma].Trim());
            start = comma + 1;
        }
        arguments.Add(textBeforeCaret[start..].Trim());
        return new SignatureCallContext(call.Commas.Count, arguments);
    }

    private static bool IsGenericOpening(string text, int opening)
    {
        var preceding = opening - 1;
        while (preceding >= 0 && char.IsWhiteSpace(text[preceding])) preceding--;
        if (preceding < 0 || !(char.IsLetterOrDigit(text[preceding]) ||
            text[preceding] is '_' or '>' or ']' or '?')) return false;
        var depth = 1;
        for (var i = opening + 1; i < text.Length; i++)
        {
            if (text[i] is '\'' or '"' or '`') return false;
            if (text[i] == ';' || text[i] == '\n') return false;
            if (text[i] == '<') depth++;
            if (text[i] != '>' || --depth != 0) continue;
            var next = i + 1;
            while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
            return next == text.Length || text[next] is '(' or ')' or '[' or ']' or '{' or '}' or '.' or ',' or '?' or ':' or ';';
        }
        return false;
    }
}

/// <summary>
/// Chooses a signature using the provider's semantic preference, the argument position,
/// and obvious literal types. Explicit user navigation is retained until the call changes.
/// </summary>
public sealed class SignatureHelpSelection
{
    private SignatureHelp? _help;
    private bool _manuallySelected;
    private int _index;

    public SignatureHelp? Help => _help;
    public int SelectedIndex => _index;
    public SignatureCallContext? CallContext { get; private set; }

    public void SetHelp(SignatureHelp? help, SignatureCallContext? call = null)
    {
        var sameArgument = call?.ActiveArgument == CallContext?.ActiveArgument;
        var previousLabel = _manuallySelected && sameArgument && _help is { } previous &&
            _index >= 0 && _index < previous.Signatures.Count
                ? previous.Signatures[_index].Label : null;
        _help = help;
        CallContext = call;
        if (help is null || help.Signatures.Count == 0)
        {
            Reset();
            return;
        }
        if (previousLabel is not null)
        {
            for (var i = 0; i < help.Signatures.Count; i++)
            {
                if (!string.Equals(help.Signatures[i].Label, previousLabel, StringComparison.Ordinal)) continue;
                _index = i;
                return;
            }
        }
        _index = ChooseAutomatically(help, call);
        _manuallySelected = false;
    }

    private static int ChooseAutomatically(SignatureHelp help, SignatureCallContext? call)
    {
        var preferred = Math.Clamp(help.ActiveSignature, 0, help.Signatures.Count - 1);
        if (call is null || call.SuppliedArguments == 0 || help.Signatures.Count == 1) return preferred;
        var best = preferred;
        var bestScore = int.MinValue;
        for (var i = 0; i < help.Signatures.Count; i++)
        {
            var signature = help.Signatures[i];
            if (signature.Parameters is null) continue; // Unknown arity: trust the provider.
            var parameters = signature.Parameters;
            var variadic = parameters.Count > 0 && IsVariadic(parameters[^1].Label);
            var min = parameters.Count(p => !IsOptional(p.Label) && !IsVariadic(p.Label));
            var max = variadic ? int.MaxValue : parameters.Count;
            var needed = Math.Max(call.ActiveArgument + 1, call.SuppliedArguments);
            var score = needed > max ? -1000 - (needed - max) * 10 : 100;
            if (needed >= min && needed <= max) score += 14;
            if (needed == parameters.Count) score += 4;
            if (i == preferred) score += preferred == 0 ? 2 : 25;
            for (var arg = 0; arg < call.Arguments.Count; arg++)
            {
                if (arg >= parameters.Count && !variadic) break;
                var expected = ParameterKind(parameters[Math.Min(arg, parameters.Count - 1)].Label);
                var actual = LiteralKind(call.Arguments[arg]);
                if (actual == 0 || expected == 0) continue;
                score += expected == actual ? 20 :
                    ((expected is 1 or 5) && (actual is 1 or 5)) ? 7 : -20;
            }
            if (score <= bestScore) continue;
            bestScore = score;
            best = i;
        }
        return best;
    }

    private static bool IsVariadic(string label) =>
        label.Contains("params ", StringComparison.OrdinalIgnoreCase) || label.Contains("...", StringComparison.Ordinal);
    private static bool IsOptional(string label) => label.Contains('=') || label.Contains("?:", StringComparison.Ordinal) ||
        label.TrimEnd().EndsWith("?", StringComparison.Ordinal);

    // Literal inference is deliberately conservative. Symbols/expressions belong to the provider.
    private static int LiteralKind(string expression)
    {
        var text = expression.Trim();
        if (text.Length == 0) return 0;
        if (text is "true" or "false") return 3;
        if (text.StartsWith('"') || text.StartsWith("@\"", StringComparison.Ordinal) ||
            text.StartsWith("$\"", StringComparison.Ordinal) || text.StartsWith('`')) return 2;
        if (text.StartsWith('\'')) return text.Length <= 3 ? 4 : 2;
        if (char.IsDigit(text[0]) || (text[0] is '-' or '+' && text.Length > 1 && char.IsDigit(text[1])))
        {
            var number = text.Replace("_", "", StringComparison.Ordinal);
            if (number.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                number.StartsWith("0b", StringComparison.OrdinalIgnoreCase)) return 1;
            if (number.Any(c => c is '.' or 'e' or 'E') ||
                number.EndsWith("f", StringComparison.OrdinalIgnoreCase) ||
                number.EndsWith("m", StringComparison.OrdinalIgnoreCase) ||
                number.EndsWith("d", StringComparison.OrdinalIgnoreCase)) return 5;
            var digits = number.TrimStart('-', '+').TrimEnd('l', 'L', 'u', 'U');
            if (digits.Length > 0 && digits.All(char.IsDigit)) return 1;
        }
        return 0;
    }
    private static int ParameterKind(string label)
    {
        var words = label.ToLowerInvariant().Replace("::", " ").Split(
            new[] { ' ', ':', '<', '>', '[', ']', '?', '*', '&', ',', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => w is "string" or "stringview" or "wstring")) return 2;
        if (words.Any(w => w is "bool" or "boolean")) return 3;
        if (words.Any(w => w is "char" or "wchar_t")) return 4;
        if (words.Any(w => w is "float" or "double" or "decimal" or "number")) return 5;
        if (words.Any(w => w is "int" or "long" or "short" or "byte" or "uint" or "ulong" or "size_t")) return 1;
        return 0;
    }

    public bool Move(int direction)
    {
        if (_help is null || _help.Signatures.Count < 2 || direction == 0) return false;
        var count = _help.Signatures.Count;
        _index = (_index + (direction > 0 ? 1 : -1) + count) % count;
        _manuallySelected = true;
        return true;
    }

    public void Reset()
    {
        _help = null;
        _index = 0;
        _manuallySelected = false;
        CallContext = null;
    }
}
