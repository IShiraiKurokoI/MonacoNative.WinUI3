using MonacoNative.Language;

namespace MonacoNative.SampleApp;

/// <summary>
/// Conservative built-in formatting provider for the self-contained samples.
/// This is intentionally not represented as an LSP formatter or a full parser:
/// strings and comments are protected, and general source code is never reformatted
/// by this class outside the SampleApp language provider registration.
/// </summary>
internal sealed class DemoSampleFormattingProvider : IDocumentFormattingEditProvider
{
    public ValueTask<IReadOnlyList<LanguageTextEdit>?> ProvideDocumentFormattingEditsAsync(
        ILanguageTextModel model, FormattingOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var before = model.GetValue();
        var formatted = Format(before, model.LanguageId, Math.Clamp(options.TabSize, 1, 8));
        if (string.Equals(before, formatted, StringComparison.Ordinal))
            return ValueTask.FromResult<IReadOnlyList<LanguageTextEdit>?>(Array.Empty<LanguageTextEdit>());
        var last = model.LineCount;
        var edit = new LanguageTextEdit(new LanguageRange(1, 1, last, model.GetLineMaxColumn(last)), formatted);
        return ValueTask.FromResult<IReadOnlyList<LanguageTextEdit>?>(new[] { edit });
    }

    internal static string Format(string source, string languageId, int tabSize = 4)
    {
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        if (languageId == "html") return FormatHtml(lines, tabSize);
        var depth = 0;
        var inBlockComment = false;
        var inTemplate = false;
        var result = new List<string>(lines.Length);
        // Other languages: only normalize existing indentation/trailing spaces.
        if (languageId is not ("cpp" or "csharp" or "javascript"))
            return string.Join("\n", lines.Select(x => x.TrimEnd().Replace("\t", new string(' ', tabSize), StringComparison.Ordinal)).SkipLast(lines[^1].Length == 0 ? 1 : 0)) + "\n";
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) { result.Add(string.Empty); continue; }
            var leadingClosers = line.StartsWith('}') ? 1 : 0;
            var label = (line is "private:" or "protected:" or "public:") ? 1 : 0;
            var lineDepth = Math.Max(0, depth - Math.Max(leadingClosers, label));
            if (line.StartsWith('#')) lineDepth = 0; // Preprocessor directives remain column zero.
            result.Add(new string(' ', lineDepth * tabSize) + line);
            var inString = '\0';
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
                if (inString != '\0')
                {
                    if (c == inString && !escaped) inString = '\0';
                    escaped = c == '\\' && !escaped;
                    continue;
                }
                if (c == '/' && next == '/') break;
                if (c == '/' && next == '*') { inBlockComment = true; i++; continue; }
                if (c is '\'' or '"') { inString = c; escaped = false; continue; }
                if (c == '`') { inTemplate = true; escaped = false; continue; }
                if (c == '{') depth++;
                if (c == '}') depth = Math.Max(0, depth - 1);
            }
        }
        while (result.Count > 0 && result[^1].Length == 0) result.RemoveAt(result.Count - 1);
        return string.Join("\n", result) + "\n";
    }

    private static string FormatHtml(string[] lines, int tabSize)
    {
        var result = new List<string>(lines.Length);
        var depth = 0;
        var scriptOrStyle = false;
        var innerDepth = 0;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) { result.Add(string.Empty); continue; }
            var closingTag = line.StartsWith("</", StringComparison.Ordinal);
            if (closingTag) depth = Math.Max(0, depth - 1);
            if (!closingTag && scriptOrStyle && !line.StartsWith('<'))
            {
                if (line.StartsWith('}')) innerDepth = Math.Max(0, innerDepth - 1);
                result.Add(new string(' ', (depth + innerDepth) * tabSize) + line);
                if (line.EndsWith('{')) innerDepth++;
                continue;
            }
            result.Add(new string(' ', depth * tabSize) + line);
            if (closingTag && (line.StartsWith("</script", StringComparison.OrdinalIgnoreCase) ||
                               line.StartsWith("</style", StringComparison.OrdinalIgnoreCase)))
            { scriptOrStyle = false; innerDepth = 0; }
            if (!closingTag && line.StartsWith('<') && !line.StartsWith("<!", StringComparison.Ordinal) &&
                !line.StartsWith("<?", StringComparison.Ordinal) && !line.EndsWith("/>", StringComparison.Ordinal))
            {
                var endOfTag = line.IndexOf('>');
                if (endOfTag >= 0 && !line[(endOfTag + 1)..].Contains("</", StringComparison.Ordinal))
                {
                    var elementNameEnd = line.IndexOfAny(new[] { ' ', '\t', '>' }, 1);
                    var name = elementNameEnd > 1 ? line.Substring(1, elementNameEnd - 1) : string.Empty;
                    if (name is not ("meta" or "link" or "input" or "br" or "hr" or "img")) depth++;
                    if (name is "script" or "style") scriptOrStyle = true;
                }
            }
        }
        while (result.Count > 0 && result[^1].Length == 0) result.RemoveAt(result.Count - 1);
        return string.Join("\n", result) + "\n";
    }

}
