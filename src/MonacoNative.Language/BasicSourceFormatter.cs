namespace MonacoNative.Language;

public enum BasicSourceFormattingMode
{
    BraceLanguage,
    Html,
}

public sealed class BasicSourceFormattingProvider : IDocumentFormattingEditProvider, IDocumentRangeFormattingEditProvider
{
    private readonly BasicSourceFormattingMode _mode;

    public BasicSourceFormattingProvider(BasicSourceFormattingMode mode) => _mode = mode;

    public ValueTask<IReadOnlyList<LanguageTextEdit>?> ProvideDocumentFormattingEditsAsync(
        ILanguageTextModel model, FormattingOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var original = model.GetValue();
        var formatted = Format(original, options, _mode);
        if (string.Equals(original, formatted, StringComparison.Ordinal))
            return ValueTask.FromResult<IReadOnlyList<LanguageTextEdit>?>(Array.Empty<LanguageTextEdit>());

        var lastLine = model.LineCount;
        var edit = new LanguageTextEdit(
            new LanguageRange(1, 1, lastLine, model.GetLineMaxColumn(lastLine)), formatted);
        return ValueTask.FromResult<IReadOnlyList<LanguageTextEdit>?>(new[] { edit });
    }

    public ValueTask<IReadOnlyList<LanguageTextEdit>?> ProvideDocumentRangeFormattingEditsAsync(
        ILanguageTextModel model, LanguageRange range, FormattingOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = Math.Clamp(range.StartLineNumber, 1, model.LineCount);
        var end = Math.Clamp(range.EndLineNumber, start, model.LineCount);
        var source = string.Join("\n", Enumerable.Range(start, end - start + 1).Select(model.GetLineContent));
        var formatted = Format(source, options with { InsertFinalNewline = false }, _mode);
        var edit = new LanguageTextEdit(
            new LanguageRange(start, 1, end, model.GetLineMaxColumn(end)), formatted);
        return ValueTask.FromResult<IReadOnlyList<LanguageTextEdit>?>(new[] { edit });
    }

    internal static string Format(string source, FormattingOptions options, BasicSourceFormattingMode mode)
    {
        var tabSize = Math.Clamp(options.TabSize, 1, 16);
        var indentUnit = options.InsertSpaces ? new string(' ', tabSize) : "\t";
        var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var result = mode == BasicSourceFormattingMode.Html
            ? FormatHtml(lines, indentUnit)
            : FormatBraceLanguage(lines, indentUnit);

        var text = string.Join("\n", result);
        if (options.TrimTrailingWhitespace ?? true)
            text = string.Join("\n", text.Split('\n').Select(static line => line.TrimEnd()));
        if (options.TrimFinalNewlines == true)
            text = text.TrimEnd('\n');
        if (options.InsertFinalNewline ?? source.EndsWith('\n'))
            text = text.TrimEnd('\n') + "\n";
        return newline == "\n" ? text : text.Replace("\n", newline, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> FormatBraceLanguage(string[] lines, string indentUnit)
    {
        var result = new List<string>(lines.Length);
        var depth = 0;
        var inBlockComment = false;
        var inTemplate = false;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) { result.Add(string.Empty); continue; }

            var leadingCloser = line[0] is '}' or ']' or ')' ? 1 : 0;
            var accessLabel = line is "public:" or "protected:" or "private:" ? 1 : 0;
            var lineDepth = Math.Max(0, depth - Math.Max(leadingCloser, accessLabel));
            if (line.StartsWith('#')) lineDepth = 0;
            result.Add(string.Concat(Enumerable.Repeat(indentUnit, lineDepth)) + line);

            char quote = '\0';
            var escaped = false;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                var next = i + 1 < line.Length ? line[i + 1] : '\0';
                if (inBlockComment)
                {
                    if (c == '*' && next == '/') { inBlockComment = false; i++; }
                    continue;
                }
                if (inTemplate)
                {
                    if (c == '`' && !escaped) inTemplate = false;
                    escaped = c == '\\' && !escaped;
                    continue;
                }
                if (quote != '\0')
                {
                    if (c == quote && !escaped) quote = '\0';
                    escaped = c == '\\' && !escaped;
                    continue;
                }
                if (c == '/' && next == '/') break;
                if (c == '/' && next == '*') { inBlockComment = true; i++; continue; }
                if (c is '\'' or '"') { quote = c; escaped = false; continue; }
                if (c == '`') { inTemplate = true; escaped = false; continue; }
                if (c is '{' or '[') depth++;
                else if (c is '}' or ']') depth = Math.Max(0, depth - 1);
            }
        }
        return result;
    }

    private static IReadOnlyList<string> FormatHtml(string[] lines, string indentUnit)
    {
        var result = new List<string>(lines.Length);
        var depth = 0;
        var rawTextTag = string.Empty;
        var rawTextDepth = 0;
        var voidTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr" };

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) { result.Add(string.Empty); continue; }
            var closing = line.StartsWith("</", StringComparison.Ordinal);
            if (closing) depth = Math.Max(0, depth - 1);

            if (rawTextTag.Length > 0 && !line.StartsWith("</" + rawTextTag, StringComparison.OrdinalIgnoreCase))
            {
                if (line.StartsWith('}')) rawTextDepth = Math.Max(0, rawTextDepth - 1);
                result.Add(string.Concat(Enumerable.Repeat(indentUnit, depth + rawTextDepth)) + line);
                if (line.EndsWith('{')) rawTextDepth++;
                continue;
            }

            result.Add(string.Concat(Enumerable.Repeat(indentUnit, depth)) + line);
            if (closing)
            {
                if (line.StartsWith("</script", StringComparison.OrdinalIgnoreCase) || line.StartsWith("</style", StringComparison.OrdinalIgnoreCase))
                { rawTextTag = string.Empty; rawTextDepth = 0; }
                continue;
            }
            if (!line.StartsWith('<') || line.StartsWith("<!", StringComparison.Ordinal) || line.StartsWith("<?", StringComparison.Ordinal) || line.EndsWith("/>", StringComparison.Ordinal))
                continue;

            var end = line.IndexOfAny(new[] { ' ', '\t', '>', '/' }, 1);
            if (end <= 1) continue;
            var name = line[1..end];
            if (voidTags.Contains(name)) continue;
            var closeOnSameLine = line.Contains("</" + name, StringComparison.OrdinalIgnoreCase);
            if (!closeOnSameLine) depth++;
            if (name.Equals("script", StringComparison.OrdinalIgnoreCase) || name.Equals("style", StringComparison.OrdinalIgnoreCase))
                rawTextTag = name;
        }
        return result;
    }
}
