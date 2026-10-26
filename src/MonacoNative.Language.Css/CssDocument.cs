namespace MonacoNative.Language.Css;

public sealed record CssParseIssue(int Offset, int Length, string Message);
public sealed record CssDeclaration(string Name, int Start, int NameEnd, int? Colon, int End);
public sealed class CssBlock
{
    public int Start { get; internal set; }
    public int ContentStart { get; internal set; }
    public int End { get; internal set; }
    public string Prelude { get; internal set; } = string.Empty;
    public bool AcceptsDeclarations { get; internal set; }
    public bool Closed { get; internal set; }
    public List<CssBlock> Children { get; } = new();
    public List<CssDeclaration> Declarations { get; } = new();
}

/// <summary>
/// Native structural/declaration tree over the ported CSS scanner. This is intentionally
/// not presented as the complete upstream selector/expression/SCSS/LESS parser.
/// </summary>
public sealed class CssDocument
{
    public CssBlock Root { get; }
    public IReadOnlyList<CssToken> Tokens { get; }
    public IReadOnlyList<CssParseIssue> Issues { get; }
    private CssDocument(CssBlock root, IReadOnlyList<CssToken> tokens, IReadOnlyList<CssParseIssue> issues)
    { Root = root; Tokens = tokens; Issues = issues; }
    public CssBlock BlockAt(int offset)
    {
        var block = Root;
        while (true)
        {
            var child = block.Children.LastOrDefault(b => b.ContentStart <= offset && offset <= b.End);
            if (child is null) return block;
            block = child;
        }
    }
    public IEnumerable<CssDeclaration> AllDeclarations()
    {
        var stack = new Stack<CssBlock>(); stack.Push(Root);
        while (stack.Count > 0)
        {
            var block = stack.Pop(); foreach (var declaration in block.Declarations) yield return declaration;
            foreach (var child in block.Children.AsEnumerable().Reverse()) stack.Push(child);
        }
    }
    public static CssDocument Parse(string text, CancellationToken cancellationToken = default, string languageId = "css")
    {
        var scanner = CssDialectScanner.Create(languageId, text, cancellationToken);
        scanner.IgnoreComment = false;
        var all = new List<CssToken>(); var errors = new List<CssParseIssue>();
        while (true)
        {
            var token = scanner.Scan(); if (token.Type == CssTokenType.EOF) break;
            all.Add(token);
            if (token.Type is CssTokenType.BadString or CssTokenType.BadEscapedJavaScript) errors.Add(new(token.Offset, token.Length, "Unterminated CSS string."));
            if (token.Type == CssTokenType.Comment && token.Text.StartsWith("/*", StringComparison.Ordinal) && (token.Length < 4 || !token.Text.EndsWith("*/", StringComparison.Ordinal)))
                errors.Add(new(token.Offset, token.Length, "Unterminated CSS comment."));
        }
        var tokens = all.Where(t => t.Type != CssTokenType.Comment && t.Type != CssTokenType.Whitespace).ToArray();
        var root = new CssBlock { Start = 0, ContentStart = 0, End = text.Length, Closed = true };
        var current = root; var stack = new Stack<CssBlock>();
        var first = 0; var parentheses = 0; var brackets = 0;
        for (var i = 0; i < tokens.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested(); var token = tokens[i];
            if (token.Type == CssTokenType.ParenthesisL) parentheses++;
            else if (token.Type == CssTokenType.ParenthesisR) parentheses = Math.Max(0, parentheses - 1);
            else if (token.Type == CssTokenType.BracketL) brackets++;
            else if (token.Type == CssTokenType.BracketR) brackets = Math.Max(0, brackets - 1);
            if (parentheses > 0 || brackets > 0) continue;
            if (token.Type == CssTokenType.CurlyL)
            {
                var prelude = first < i ? text[tokens[first].Offset..token.Offset].Trim() : string.Empty;
                var head = first < i ? tokens[first].Text.ToLowerInvariant() : string.Empty;
                var group = head is "@media" or "@supports" or "@container" or "@layer" or "@scope" or "@keyframes" or "@-webkit-keyframes" or "@document";
                var child = new CssBlock { Start = first < i ? tokens[first].Offset : token.Offset, ContentStart = token.Offset + token.Length,
                    End = text.Length, Prelude = prelude, AcceptsDeclarations = !group };
                current.Children.Add(child); stack.Push(current); current = child; first = i + 1;
            }
            else if (token.Type == CssTokenType.SemiColon || token.Type == CssTokenType.CurlyR)
            {
                AddDeclaration(first, i, token.Offset);
                first = i + 1;
                if (token.Type != CssTokenType.CurlyR) continue;
                if (stack.Count == 0) errors.Add(new(token.Offset, token.Length, "Unexpected closing brace."));
                else { current.End = token.Offset; current.Closed = true; current = stack.Pop(); }
            }
        }
        AddDeclaration(first, tokens.Length, text.Length);
        while (stack.Count > 0)
        { errors.Add(new(current.ContentStart - 1, 1, "Closing brace expected.")); current = stack.Pop(); }
        return new(root, all, errors);
        void AddDeclaration(int begin, int end, int boundary)
        {
            if (!current.AcceptsDeclarations || begin >= end || tokens[begin].Type != CssTokenType.Ident) return;
            int? colon = begin + 1 < end && tokens[begin + 1].Type == CssTokenType.Colon ? tokens[begin + 1].Offset : null;
            var name = tokens[begin]; current.Declarations.Add(new(name.Text, name.Offset, name.Offset + name.Length, colon, boundary));
        }
    }
}
