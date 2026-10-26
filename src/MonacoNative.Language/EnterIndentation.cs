using System.Text.RegularExpressions;

namespace MonacoNative.Language;

/// <summary>
/// Language-configuration-driven Enter editing. In VS Code this is an editor operation,
/// not an LSP completion request: the language supplies bracket / onEnter / indentation rules.
/// The result is an atomic insertion with an explicit caret location.
/// </summary>
public static class EnterIndentation
{
    public readonly record struct Edit(string Text, int CaretOffset);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(80);

    public static Edit Create(string line, int column, LanguageConfiguration? configuration,
        string endOfLine = "\n", int tabSize = 4, string? previousLine = null)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(endOfLine);
        tabSize = Math.Clamp(tabSize, 1, 16);
        var cut = Math.Clamp(column - 1, 0, line.Length);
        var before = line[..cut];
        var after = line[cut..];
        var indent = line[..(line.Length - line.TrimStart(' ', '\t').Length)];
        var unit = indent.Contains('\t') ? "\t" : new string(' ', tabSize);
        var actual = before.TrimEnd();
        var suffix = after.TrimStart();
        var action = EnterIndentAction.None;
        string? append = null;

        if (configuration is not null)
        {
            foreach (var rule in configuration.OnEnterRules)
            {
                if (!Matches(rule.BeforeText, before) ||
                    (rule.AfterText is not null && !Matches(rule.AfterText, after)) ||
                    (rule.PreviousLineText is not null && !Matches(rule.PreviousLineText, previousLine ?? string.Empty)))
                    continue;
                action = rule.Action;
                append = rule.AppendText;
                break;
            }
        }
        if (action == EnterIndentAction.None && append is null)
        {
            // The first case corresponds to Monaco's IndentOutdent when Enter is pressed
            // between paired delimiters. Both lines are inserted in one undo step.
            var paired = HasOpeningPair(actual, suffix, configuration);
            if (paired)
                action = EnterIndentAction.IndentOutdent;
            else if (Matches(configuration?.IncreaseIndentPattern, actual) ||
                     Matches(configuration?.IndentNextLinePattern, actual) ||
                     EndsWithUnclosedBracket(actual, configuration))
                action = EnterIndentAction.Indent;
        }

        // VS Code's indentNextLine rule only applies to one line. The line after a
        // completed single-statement body returns to the parent's indentation.
        if (action == EnterIndentAction.None && previousLine is not null && configuration?.IndentNextLinePattern is not null &&
            Matches(configuration.IndentNextLinePattern, previousLine) &&
            indent.Length > LeadingWhitespace(previousLine).Length &&
            (actual.EndsWith(';') || actual.EndsWith('}') || actual.EndsWith(')')))
            indent = LeadingWhitespace(previousLine);

        if (Matches(configuration?.UnIndentedLinePattern, actual))
            action = EnterIndentAction.None;

        if (action == EnterIndentAction.Outdent)
            indent = Unindent(indent, tabSize);

        var caretIndent = action is EnterIndentAction.Indent or EnterIndentAction.IndentOutdent
            ? indent + unit : indent;
        var first = endOfLine + caretIndent + (append ?? string.Empty);
        if (action != EnterIndentAction.IndentOutdent)
            return new Edit(first, first.Length);
        return new Edit(first + endOfLine + indent, first.Length);
    }

    public static string LeadingWhitespace(string text) => text[..(text.Length - text.TrimStart(' ', '\t').Length)];

    public static string Unindent(string indent, int tabSize)
    {
        if (indent.EndsWith('\t')) return indent[..^1];
        var count = 0;
        for (var i = indent.Length - 1; i >= 0 && indent[i] == ' ' && count < tabSize; i--) count++;
        return indent[..(indent.Length - count)];
    }

    private static bool Matches(string? pattern, string value)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        try { return Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant, RegexTimeout); }
        catch (ArgumentException) { return false; }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static bool HasOpeningPair(string before, string after, LanguageConfiguration? config)
    {
        var pairs = config?.Brackets;
        if (pairs is null || pairs.Count == 0) return false;
        return pairs.Any(pair => before.EndsWith(pair.Open, StringComparison.Ordinal) &&
                                 after.StartsWith(pair.Close, StringComparison.Ordinal));
    }

    private static bool EndsWithUnclosedBracket(string before, LanguageConfiguration? config)
    {
        if (config is null) return false;
        // Only delimiter pairs are structural; avoid indentation for an operator such
        // as C# '=>' or literal string content containing a brace.
        var sanitized = StripStringsAndComments(before);
        return config.Brackets.Any(pair => pair.Open.Length == 1 && pair.Close.Length == 1 &&
            sanitized.EndsWith(pair.Open, StringComparison.Ordinal) &&
            (pair.Open[0] is '{' or '[' or '('));
    }

    private static string StripStringsAndComments(string line)
    {
        var chars = line.ToCharArray();
        var quote = '\0';
        var escaped = false;
        for (var i = 0; i < chars.Length; i++)
        {
            var ch = chars[i];
            if (quote != '\0')
            {
                chars[i] = ' ';
                if (ch == quote && !escaped) quote = '\0';
                if (ch == '\\' && !escaped) escaped = true;
                else escaped = false;
                continue;
            }
            if (ch is '\'' or '"' or '`') { quote = ch; chars[i] = ' '; continue; }
            if (ch == '/' && i + 1 < chars.Length && chars[i + 1] == '/')
            { Array.Fill(chars, ' ', i, chars.Length - i); break; }
        }
        return new string(chars).TrimEnd();
    }
}
