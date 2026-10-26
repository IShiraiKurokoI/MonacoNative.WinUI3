using System.Text.Json;
using MonacoNative.Language;

namespace MonacoNative.Language.Json;

/// <summary>
/// Local structural suggestions using the ported JSONC scanner. This small provider
/// is not a port of vscode-json-languageservice's schema completion engine.
/// </summary>
public sealed class JsonSyntaxCompletionProvider : IFallbackCompletionProvider
{
    private enum Expect { Key, Colon, Value, Separator }
    private sealed class Frame(bool isObject)
    {
        public bool IsObject { get; } = isObject;
        public Expect Next { get; set; } = isObject ? Expect.Key : Expect.Value;
    }
    private static readonly string[] Literals = { "true", "false", "null" };
    public IReadOnlyList<string> TriggerCharacters => new[] { "\"", ":", " " };

    public ValueTask<CompletionList?> ProvideCompletionItemsAsync(ILanguageTextModel model, LanguagePosition position,
        CompletionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = new LanguageTextSnapshot(model.GetValue());
        var offset = snapshot.OffsetAt(position);
        var prefixText = snapshot.Text[..offset];
        var scanner = new JsoncScanner(prefixText, cancellationToken: cancellationToken);
        var stack = new Stack<Frame>();
        var rootNext = Expect.Value;
        var next = Expect.Value;
        var previous = JsoncSyntaxKind.EOF;
        var lastOffset = offset; var lastEnd = offset;
        var lastError = JsoncScanError.None;
        var lastValue = string.Empty;
        void ValueComplete()
        {
            if (stack.TryPeek(out var parent)) parent.Next = Expect.Separator;
            else rootNext = Expect.Separator;
        }
        for (var token = scanner.Scan(); token != JsoncSyntaxKind.EOF; token = scanner.Scan())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (token is JsoncSyntaxKind.Trivia or JsoncSyntaxKind.LineBreakTrivia) continue;
            next = stack.TryPeek(out var current) ? current.Next : rootNext;
            previous = token; lastOffset = scanner.TokenOffset;
            lastEnd = scanner.TokenOffset + scanner.TokenLength;
            lastError = scanner.TokenError; lastValue = scanner.TokenValue;
            switch (token)
            {
                case JsoncSyntaxKind.OpenBraceToken: stack.Push(new(true)); break;
                case JsoncSyntaxKind.OpenBracketToken: stack.Push(new(false)); break;
                case JsoncSyntaxKind.CloseBraceToken:
                case JsoncSyntaxKind.CloseBracketToken:
                    if (stack.Count > 0) stack.Pop();
                    ValueComplete();
                    break;
                case JsoncSyntaxKind.ColonToken:
                    if (stack.TryPeek(out current) && current.IsObject) current.Next = Expect.Value;
                    break;
                case JsoncSyntaxKind.CommaToken:
                    if (stack.TryPeek(out current)) current.Next = current.IsObject ? Expect.Key : Expect.Value;
                    break;
                case JsoncSyntaxKind.StringLiteral:
                    if (lastError == JsoncScanError.UnexpectedEndOfString) break;
                    if (next == Expect.Key && stack.TryPeek(out current)) current.Next = Expect.Colon;
                    else ValueComplete();
                    break;
                case JsoncSyntaxKind.NullKeyword:
                case JsoncSyntaxKind.TrueKeyword:
                case JsoncSyntaxKind.FalseKeyword:
                case JsoncSyntaxKind.NumericLiteral: ValueComplete(); break;
            }
        }
        var items = new List<CompletionItem>();
        ValueTask<CompletionList?> Result() => ValueTask.FromResult<CompletionList?>(new CompletionList(items));
        if (previous == JsoncSyntaxKind.LineCommentTrivia && lastEnd >= offset ||
            previous == JsoncSyntaxKind.BlockCommentTrivia && lastError == JsoncScanError.UnexpectedEndOfComment)
            return Result();
        var inString = previous == JsoncSyntaxKind.StringLiteral && lastError == JsoncScanError.UnexpectedEndOfString;
        if (inString && next != Expect.Key) return Result();
        var partialLiteral = lastEnd == offset && (previous is JsoncSyntaxKind.Unknown or
            JsoncSyntaxKind.TrueKeyword or JsoncSyntaxKind.FalseKeyword or JsoncSyntaxKind.NullKeyword);
        var expect = inString || partialLiteral ? next : stack.TryPeek(out var frame) ? frame.Next : rootNext;
        var prefix = inString ? lastValue : partialLiteral ? prefixText[lastOffset..] : string.Empty;
        var replaceStart = inString ? lastOffset + 1 : partialLiteral ? lastOffset : offset;
        var range = snapshot.Range(replaceStart, offset);
        if (expect == Expect.Key)
        {
            // Use actual string/colon tokens, so comments and escaped quotes cannot
            // masquerade as property names. The list remains a local heuristic.
            var names = new HashSet<string>(StringComparer.Ordinal) { "$schema" };
            var all = new JsoncScanner(snapshot.Text, ignoreTrivia: true, cancellationToken: cancellationToken);
            string? name = null;
            while (all.Scan() != JsoncSyntaxKind.EOF)
            {
                if (all.Token == JsoncSyntaxKind.ColonToken && name is not null) names.Add(name);
                name = all.Token == JsoncSyntaxKind.StringLiteral && all.TokenError == JsoncScanError.None
                    ? all.TokenValue : null;
            }
            foreach (var candidate in names.OrderBy(value => value, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || candidate == prefix) continue;
                var quoted = JsonSerializer.Serialize(candidate);
                items.Add(new CompletionItem(candidate, CompletionItemKind.Property,
                    InsertText: inString ? quoted[1..^1] : quoted + ": ", Range: range));
                if (items.Count >= 100) break;
            }
        }
        else if (expect == Expect.Value && !inString)
            foreach (var literal in Literals)
                if (literal.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && literal != prefix)
                    items.Add(new CompletionItem(literal, CompletionItemKind.Keyword, InsertText: literal, Range: range));
        return Result();
    }
}
