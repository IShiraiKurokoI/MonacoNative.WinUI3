using System.Text;
using System.Text.RegularExpressions;

namespace MonacoNative.Language;

public sealed record SnippetTransform(string Pattern, string Format, string Options)
{
    public string Apply(string value)
    {
        try
        {
            var options = RegexOptions.CultureInvariant;
            if (Options.Contains('i')) options |= RegexOptions.IgnoreCase;
            if (Options.Contains('m')) options |= RegexOptions.Multiline;
            if (Options.Contains('s')) options |= RegexOptions.Singleline;
            var regex = new Regex(Pattern, options, TimeSpan.FromMilliseconds(100));
            return regex.Replace(value, m => FormatMatch(m), Options.Contains('g') ? int.MaxValue : 1);
        }
        catch (ArgumentException) { return value; }
        catch (RegexMatchTimeoutException) { return value; }
    }
    private string FormatMatch(Match match)
    {
        string Group(int n) => n >= 0 && n < match.Groups.Count ? match.Groups[n].Value : "";
        return Regex.Replace(Format, @"\$\{(\d+)(?::([^}]*))?\}|\$(\d+)", token =>
        {
            if (token.Groups[3].Success) return Group(int.Parse(token.Groups[3].Value));
            var value = Group(int.Parse(token.Groups[1].Value)); var operation = token.Groups[2].Value;
            if (!token.Groups[2].Success) return value;
            if (operation.StartsWith("/", StringComparison.Ordinal))
            {
                var words = Regex.Split(value, @"[^\p{L}\p{Nd}]+").Where(x => x.Length > 0).ToArray();
                return operation switch
                {
                    "/upcase" => value.ToUpperInvariant(), "/downcase" => value.ToLowerInvariant(),
                    "/capitalize" => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..],
                    "/pascalcase" => string.Concat(words.Select(x => char.ToUpperInvariant(x[0]) + x[1..].ToLowerInvariant())),
                    "/camelcase" => words.Length == 0 ? "" : words[0].ToLowerInvariant() + string.Concat(words.Skip(1).Select(x => char.ToUpperInvariant(x[0]) + x[1..].ToLowerInvariant())),
                    _ => value,
                };
            }
            if (operation.StartsWith('+')) return value.Length > 0 ? operation[1..] : "";
            if (operation.StartsWith('?'))
            { var parts = operation[1..].Split(':', 2); return value.Length > 0 ? parts[0] : parts.ElementAtOrDefault(1) ?? ""; }
            if (operation.StartsWith('-')) return value.Length == 0 ? operation[1..] : value;
            return value.Length == 0 ? operation : value;
        }, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }
}

public sealed class SnippetPlaceholder
{
    public int Index { get; }
    public int Start { get; internal set; }
    public int Length { get; internal set; }
    public int End => Start + Length;
    public IReadOnlyList<string> Choices { get; }
    public SnippetTransform? Transform { get; }
    internal int[] Parents { get; }
    internal bool Removed { get; set; }
    internal SnippetPlaceholder(int index, int start, int length, IReadOnlyList<string>? choices = null,
        SnippetTransform? transform = null, int[]? parents = null)
    { Index = index; Start = start; Length = length; Choices = choices ?? Array.Empty<string>(); Transform = transform; Parents = parents ?? Array.Empty<int>(); }
    internal SnippetPlaceholder Copy(int offset) => new(Index, Start + offset, Length, Choices, Transform, Parents);
}
public sealed record SnippetExpansion(string Text, IReadOnlyList<SnippetPlaceholder> Placeholders)
{
    public int? InitialOffset => Placeholders.Where(p => p.Transform is null).OrderBy(p => p.Index == 0 ? int.MaxValue : p.Index)
        .Select(p => (int?)p.Start).FirstOrDefault();
}

/// <summary>TextMate/LSP snippet grammar: tabstops, nesting, choices, variables and transforms.</summary>
public static class SnippetParser
{
    private sealed record Node(string Text = "", int? Index = null, string? Variable = null,
        IReadOnlyList<Node>? Children = null, IReadOnlyList<string>? Choices = null, SnippetTransform? Transform = null);
    private sealed class Reader
    {
        private readonly string _text; private int _at; private int _depth;
        internal Reader(string text) => _text = text;
        internal List<Node> Read(bool nested = false)
        {
            var output = new List<Node>(); var text = new StringBuilder();
            void Flush() { if (text.Length > 0) { output.Add(new Node(text.ToString())); text.Clear(); } }
            while (_at < _text.Length)
            {
                var c = _text[_at++];
                if (nested && c == '}') break;
                if (c == '\\' && _at < _text.Length && _text[_at] is '$' or '}' or '\\') { text.Append(_text[_at++]); continue; }
                if (c != '$') { text.Append(c); continue; }
                var saved = _at;
                var node = ReadDollar();
                if (node is null) { _at = saved; text.Append('$'); }
                else { Flush(); output.Add(node); }
            }
            Flush(); return output;
        }
        private Node? ReadDollar()
        {
            if (_at >= _text.Length) return null;
            var braced = _text[_at] == '{'; if (braced) _at++;
            var start = _at;
            if (_at < _text.Length && char.IsAsciiDigit(_text[_at]))
                while (_at < _text.Length && char.IsAsciiDigit(_text[_at])) _at++;
            else if (_at < _text.Length && (char.IsAsciiLetter(_text[_at]) || _text[_at] == '_'))
                while (_at < _text.Length && (char.IsAsciiLetterOrDigit(_text[_at]) || _text[_at] == '_')) _at++;
            if (_at == start) return null;
            var name = _text[start.._at];
            var node = int.TryParse(name, out var index) ? new Node(Index: index) : new Node(Variable: name);
            if (!braced) return node;
            if (_at >= _text.Length) return null;
            var separator = _text[_at++];
            if (separator == '}') return node;
            if (separator == ':' && _depth < 64)
            { _depth++; var children = Read(true); _depth--; return node with { Children = children }; }
            if (separator == '|' && node.Index is not null)
            {
                var choices = new List<string>(); var choice = new StringBuilder();
                while (_at < _text.Length)
                {
                    var c = _text[_at++];
                    if (c == '\\' && _at < _text.Length && _text[_at] is '\\' or ',' or '|') { choice.Append(_text[_at++]); continue; }
                    if (c == ',') { choices.Add(choice.ToString()); choice.Clear(); continue; }
                    if (c == '|' && _at < _text.Length && _text[_at] == '}')
                    { _at++; choices.Add(choice.ToString()); return node with { Choices = choices }; }
                    choice.Append(c);
                }
                return null;
            }
            if (separator == '/')
            {
                var pattern = ReadTransformPart(false); var format = ReadTransformPart(true);
                if (pattern is null || format is null) return null;
                var end = _text.IndexOf('}', _at); if (end < 0) return null;
                var options = _text[_at..end]; _at = end + 1;
                return node with { Transform = new(pattern, format, options) };
            }
            return null;
        }
        private string? ReadTransformPart(bool format)
        {
            var output = new StringBuilder(); var braces = 0;
            while (_at < _text.Length)
            {
                var c = _text[_at++];
                if (c == '\\' && _at < _text.Length)
                { var n = _text[_at++]; if (n != '/') output.Append('\\'); output.Append(n); continue; }
                if (format && c == '{') braces++;
                if (format && c == '}') braces--;
                if (c == '/' && braces == 0) return output.ToString();
                output.Append(c);
            }
            return null;
        }
    }

    public static SnippetExpansion Parse(string snippet, IReadOnlyDictionary<string, string>? variables = null)
    {
        ArgumentNullException.ThrowIfNull(snippet);
        var nodes = new Reader(snippet).Read(); var definitions = new Dictionary<int, Node>();
        var unknown = new Dictionary<string, int>(); var values = new Dictionary<int, string>();
        var visiting = new HashSet<int>(); var placeholders = new List<SnippetPlaceholder>();
        var output = new StringBuilder(); var next = 1;
        void Collect(IEnumerable<Node> list)
        {
            foreach (var n in list)
            {
                if (n.Index is { } id)
                {
                    next = Math.Max(next, id + 1);
                    if (n.Transform is null && (!definitions.TryGetValue(id, out var previous) || previous.Children is null && previous.Choices is null)) definitions[id] = n;
                }
                if (n.Children is not null) Collect(n.Children);
            }
        }
        Collect(nodes);
        string Variable(Node n)
        {
            if (variables is not null && variables.TryGetValue(n.Variable!, out var known)) return known;
            if (n.Children is not null) return Plain(n.Children);
            return n.Variable!;
        }
        string Value(int id)
        {
            if (values.TryGetValue(id, out var cached)) return cached;
            if (!visiting.Add(id)) return "";
            var result = definitions.TryGetValue(id, out var n) ? n.Choices?.FirstOrDefault() ?? (n.Children is null ? "" : Plain(n.Children)) : "";
            visiting.Remove(id); values[id] = result; return result;
        }
        string Plain(IEnumerable<Node> list) => string.Concat(list.Select(n => n.Index is { } id
            ? n.Transform?.Apply(Value(id)) ?? Value(id) : n.Variable is not null
            ? n.Transform?.Apply(Variable(n)) ?? Variable(n) : n.Text));
        var rendering = new HashSet<int>();
        void Render(IEnumerable<Node> list, int[] parents)
        {
            foreach (var n in list)
            {
                if (n.Index is { } id)
                {
                    var start = output.Length;
                    if (n.Transform is not null) output.Append(n.Transform.Apply(Value(id)));
                    else if (n.Children is not null && rendering.Add(id))
                    { Render(n.Children, parents.Append(id).ToArray()); rendering.Remove(id); }
                    else output.Append(Value(id));
                    placeholders.Add(new(id, start, output.Length - start, n.Choices, n.Transform, parents));
                }
                else if (n.Variable is not null)
                {
                    var known = variables is not null && variables.ContainsKey(n.Variable);
                    if (known || n.Transform is not null) output.Append(n.Transform?.Apply(Variable(n)) ?? Variable(n));
                    else if (n.Children is not null) Render(n.Children, parents);
                    else
                    {
                        if (!unknown.TryGetValue(n.Variable, out var variableIndex)) unknown[n.Variable] = variableIndex = next++;
                        var start = output.Length; output.Append(n.Variable);
                        placeholders.Add(new(variableIndex, start, output.Length - start, parents: parents));
                    }
                }
                else output.Append(n.Text);
            }
        }
        Render(nodes, Array.Empty<int>());
        if (!placeholders.Any(x => x.Index == 0 && x.Transform is null)) placeholders.Add(new(0, output.Length, 0));
        return new(output.ToString(), placeholders.OrderBy(x => x.Start).ToArray());
    }
}

public sealed record SnippetEdit(int Start, int Length, string Text)
{
    internal SnippetPlaceholder? Target { get; init; }
}

/// <summary>Offset-based session, independent of WinUI. All offsets are UTF-16, like LSP.</summary>
public sealed class SnippetSession
{
    private readonly List<SnippetPlaceholder> _stops;
    private List<int> _order;
    private int _ordinal;
    public bool IsActive { get; private set; } = true;
    public SnippetPlaceholder? Current => !IsActive ? null : _stops.FirstOrDefault(p => !p.Removed && p.Transform is null && p.Index == _order[_ordinal]);
    public IReadOnlyList<SnippetPlaceholder> Placeholders => _stops.Where(p => !p.Removed).ToArray();
    public SnippetSession(SnippetExpansion expansion, int documentOffset)
    {
        _stops = expansion.Placeholders.Select(p => p.Copy(documentOffset)).ToList();
        _order = Order();
        if (_order.Count == 0) IsActive = false;
    }
    private List<int> Order() => _stops.Where(p => !p.Removed && p.Transform is null).Select(p => p.Index).Distinct()
        .OrderBy(i => i == 0 ? int.MaxValue : i).ToList();
    public void Cancel() => IsActive = false;
    public SnippetPlaceholder? Move(bool backwards)
    {
        if (!IsActive) return null;
        if (backwards) _ordinal = Math.Max(0, _ordinal - 1);
        else if (_ordinal + 1 < _order.Count) _ordinal++;
        else { Cancel(); return null; }
        return Current;
    }
    public bool ContainsSelection(int start, int end) => Current is { } p && start >= p.Start && end <= p.End;
    public bool ObserveEdit(int start, int oldLength, string text)
    {
        var active = Current;
        if (active is null || start < active.Start || start + oldLength > active.End) { Cancel(); return false; }
        Rebase(start, oldLength, text.Length, active);
        return true;
    }
    public IReadOnlyList<int> LinkedGroups => Current is { } p ? new[] { p.Index }.Concat(p.Parents.Reverse()).Distinct().ToArray() : Array.Empty<int>();
    public IReadOnlyList<SnippetEdit> GetLinkedEdits(int group, Func<int, int, string> readText)
    {
        var current = Current;
        var source = group == current?.Index ? current : _stops.FirstOrDefault(p => !p.Removed && p.Transform is null && p.Index == group && current is not null && p.Start <= current.Start && p.End >= current.End);
        if (source is null) return Array.Empty<SnippetEdit>();
        var value = readText(source.Start, source.Length);
        return _stops.Where(p => !p.Removed && p.Index == group && !ReferenceEquals(p, source))
            .Select(p => new SnippetEdit(p.Start, p.Length, p.Transform?.Apply(value) ?? value) { Target = p })
            .Where(e => readText(e.Start, e.Length) != e.Text).OrderByDescending(e => e.Start).ToArray();
    }
    public void ApplyLinkedEdit(SnippetEdit edit)
    {
        var target = edit.Target ?? _stops.FirstOrDefault(p => !p.Removed && p.Start == edit.Start && p.Length == edit.Length);
        Rebase(edit.Start, edit.Length, edit.Text.Length, target);
    }
    private void Rebase(int start, int oldLength, int length, SnippetPlaceholder? target)
    {
        var end = start + oldLength; var delta = length - oldLength; var currentId = Current?.Index;
        foreach (var p in _stops.Where(p => !p.Removed))
        {
            if (ReferenceEquals(p, target)) { p.Length += delta; continue; }
            if (target is not null && p.Parents.Contains(target.Index) && p.Start >= start && p.End <= end)
            { p.Removed = true; continue; }
            if (target is not null && target.Parents.Contains(p.Index) && p.Start <= start && p.End >= end)
            { p.Length += delta; continue; }
            if (p.Start >= end) p.Start += delta;
            else if (p.End <= start) { }
            else if (p.Start <= start && p.End >= end) p.Length += delta;
            else p.Removed = true;
        }
        _order = Order();
        _ordinal = currentId is { } id ? Math.Max(0, _order.IndexOf(id)) : 0;
        if (_order.Count == 0) Cancel();
    }
}
