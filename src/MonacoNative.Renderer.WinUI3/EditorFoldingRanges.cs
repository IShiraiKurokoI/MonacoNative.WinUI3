namespace MonacoNative.Renderer.WinUI3;

/// <summary>
/// Lightweight fallback folding provider for native editor projection. Braces and #region
/// boundaries take precedence over indentation. The O(document-size) scan runs after
/// document changes are debounced, never in pointer hit testing or the draw loop.
/// </summary>
internal static class EditorFoldingRanges
{
    internal static SortedDictionary<int, int> Compute(IReadOnlyList<string> lines)
    {
        var result = new SortedDictionary<int, int>();
        var braceStack = new Stack<(char Open, int Line)>();
        var regions = new Stack<int>();
        var indentation = new List<(int Line, int Spaces)>();
        var previousText = string.Empty;
        var previousLine = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var lineNo = i + 1;
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            var trimmed = line.Trim();
            var level = 0;
            foreach (var c in line)
            {
                if (c == ' ') level++;
                else if (c == '\t') level += 4 - level % 4;
                else break;
            }
            indentation.Add((lineNo, level));
            if (trimmed.StartsWith("#region", StringComparison.OrdinalIgnoreCase))
                regions.Push(lineNo);
            else if (trimmed.StartsWith("#endregion", StringComparison.OrdinalIgnoreCase) && regions.Count > 0)
            {
                var start = regions.Pop();
                if (start < lineNo) result[start] = lineNo;
            }

            var quoted = '\0';
            var escaped = false;
            for (var j = 0; j < line.Length; j++)
            {
                var c = line[j];
                if (quoted != '\0')
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == quoted) quoted = '\0';
                    continue;
                }
                if (c == '/' && j + 1 < line.Length && line[j + 1] == '/') break;
                if (c == '\'' || c == '"' || c == '`') { quoted = c; continue; }
                if (c is '{' or '[')
                {
                    // VS Code/Monaco places the chevron on the declaration line when
                    // a lone opening brace follows the declaration on its next line.
                    var head = c == '{' && trimmed == "{" && previousLine > 0 &&
                        !previousText.EndsWith(';') && !previousText.EndsWith('}')
                        ? previousLine : lineNo;
                    braceStack.Push((c, head));
                }
                else if (c is '}' or ']')
                {
                    var wanted = c == '}' ? '{' : '[';
                    // Do not match an unmatched close against an unrelated opening token.
                    if (braceStack.Count > 0 && braceStack.Peek().Open == wanted)
                    {
                        var start = braceStack.Pop().Line;
                        if (start < lineNo) result[start] = Math.Max(result.GetValueOrDefault(start), lineNo);
                    }
                }
            }
            previousText = trimmed;
            previousLine = lineNo;
        }
        // Single-pass stack-based indentation provider: O(lines), including deeply
        // nested Python/YAML, rather than rescanning the tail for each parent.
        var openIndents = new Stack<(int Line, int Spaces)>();
        for (var i = 1; i < indentation.Count; i++)
        {
            var previous = indentation[i - 1];
            var current = indentation[i];
            while (openIndents.Count > 0 && openIndents.Peek().Spaces >= current.Spaces)
            {
                var head = openIndents.Pop().Line;
                if (!result.ContainsKey(head) && previous.Line > head)
                    result[head] = previous.Line;
            }
            if (current.Spaces > previous.Spaces)
                openIndents.Push(previous);
        }
        if (indentation.Count > 0)
        {
            var lastLine = indentation[^1].Line;
            while (openIndents.Count > 0)
            {
                var head = openIndents.Pop().Line;
                if (!result.ContainsKey(head) && lastLine > head)
                    result[head] = lastLine;
            }
        }
        return result;
    }
}
