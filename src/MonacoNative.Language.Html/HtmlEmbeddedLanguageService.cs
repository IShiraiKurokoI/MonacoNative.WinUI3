using System.Text.RegularExpressions;
using MonacoNative.Language;

namespace MonacoNative.Language.Html;

/// <summary>Routes inline style/script diagnostics to registered CSS/JavaScript providers.</summary>
public sealed class HtmlEmbeddedLanguageService : IDocumentDiagnosticsProvider, IDocumentSemanticTokensProvider
{
    private readonly MonacoLanguageService _languages;
    public HtmlEmbeddedLanguageService(MonacoLanguageService languages) => _languages = languages;
    public SemanticTokensLegend Legend { get; } = new(
        new[] { "keyword", "comment", "string", "number", "operator", "property", "variable" },
        Array.Empty<string>());

    private sealed record EmbeddedBlock(int Offset, string Content, string Language);
    private static IEnumerable<EmbeddedBlock> Blocks(string content, CancellationToken ct)
    {
        var scanner = new HtmlScanner(content, cancellationToken: ct);
        while (scanner.Scan() != HtmlTokenType.EOS)
        {
            ct.ThrowIfCancellationRequested();
            if (scanner.TokenType is HtmlTokenType.Script or HtmlTokenType.Styles && scanner.TokenLength > 0)
                yield return new EmbeddedBlock(scanner.TokenOffset, scanner.TokenText,
                    scanner.TokenType == HtmlTokenType.Script ? "javascript" : "css");
        }
    }

    private sealed class EmbeddedModel : ILanguageTextModel
    {
        private readonly LanguageTextSnapshot _snapshot;
        public EmbeddedModel(ILanguageTextModel parent, EmbeddedBlock block)
        {
            LanguageId = block.Language;
            VersionId = parent.VersionId;
            _snapshot = new LanguageTextSnapshot(block.Content);
            // A separate URI ensures compiler providers do not parse the host HTML file as JS/CSS.
            Uri = new Uri("monaco-embedded:///" + System.Uri.EscapeDataString(parent.Uri.AbsoluteUri) + "/" + block.Offset +
                (block.Language == "css" ? ".css" : ".js"));
        }
        public Uri Uri { get; }
        public string LanguageId { get; }
        public ulong VersionId { get; }
        public int LineCount => _snapshot.Text.Count(ch => ch == '\n') + 1;
        public string GetValue() => _snapshot.Text;
        public string GetLineContent(int lineNumber)
        {
            var lines = _snapshot.Text.Split('\n');
            return lines[Math.Clamp(lineNumber - 1, 0, lines.Length - 1)].TrimEnd('\r');
        }
        public int GetLineMaxColumn(int lineNumber) => GetLineContent(lineNumber).Length + 1;
    }

    public async ValueTask<IReadOnlyList<LanguageDiagnostic>?> ProvideDiagnosticsAsync(ILanguageTextModel model,
        CancellationToken ct)
    {
        var snapshot = new LanguageTextSnapshot(model.GetValue());
        var output = new List<LanguageDiagnostic>();
        foreach (var block in Blocks(snapshot.Text, ct).Take(128))
        {
            ct.ThrowIfCancellationRequested();
            if (block.Content.Length > 1_000_000) continue;
            var embedded = new EmbeddedModel(model, block);
            var offsetMap = new LanguageTextSnapshot(block.Content);
            var diagnostics = await _languages.ProvideDiagnosticsAsync(embedded, ct).ConfigureAwait(false);
            foreach (var d in diagnostics)
            {
                if (d.Code == "NATIVE_UNAVAILABLE") continue; // Optional native JS diagnostics are not an HTML syntax error.
                var a = block.Offset + offsetMap.OffsetAt(new(d.Range.StartLineNumber, d.Range.StartColumn));
                var b = block.Offset + offsetMap.OffsetAt(new(d.Range.EndLineNumber, d.Range.EndColumn));
                output.Add(d with { Range = snapshot.Range(a, b) });
            }
        }
        return output;
    }

    public async ValueTask<SemanticTokens?> ProvideDocumentSemanticTokensAsync(ILanguageTextModel model,
        CancellationToken ct)
    {
        var snapshot = new LanguageTextSnapshot(model.GetValue());
        var output = new List<SemanticToken>();
        foreach (var block in Blocks(snapshot.Text, ct).Take(128))
        {
            ct.ThrowIfCancellationRequested();
            if (block.Content.Length > 1_000_000) continue;
            var embedded = new EmbeddedModel(model, block);
            var contributed = await _languages.ProvideDocumentSemanticTokensAsync(embedded, ct).ConfigureAwait(false);
            if (contributed is { Tokens.Count: > 0 })
            {
                var fragment = new LanguageTextSnapshot(block.Content);
                foreach (var t in contributed.Tokens)
                {
                    var at = block.Offset + fragment.OffsetAt(new(t.LineNumber, t.StartColumn));
                    Add(snapshot, output, at, t.Length, t.TokenType);
                }
                continue;
            }
            // Standalone lightweight lexical fallback. Not a JS/TS parser or type checker.
            foreach (Match match in LexicalTokens(block.Content, block.Language, ct))
            {
                var type = match.Groups["comment"].Success ? "comment" :
                    match.Groups["string"].Success ? "string" :
                    match.Groups["number"].Success ? "number" :
                    match.Groups["keyword"].Success ? "keyword" :
                    match.Groups["property"].Success ? "property" : "operator";
                Add(snapshot, output, block.Offset + match.Index, match.Length, type);
            }
        }
        return new SemanticTokens(output);
    }

    private static IEnumerable<Match> LexicalTokens(string source, string language, CancellationToken ct)
    {
        // Match comments/quotes before identifiers so their contents never become keywords.
        var pattern = language == "css"
            ? @"(?<comment>/\*[\s\S]*?(?:\*/|$))|(?<string>""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*')|(?<number>\b\d+(?:\.\d+)?\b)|(?<property>[\w-]+(?=\s*:))|(?<keyword>![a-z]+|@[\w-]+)"
            : @"(?<comment>//[^\r\n]*|/\*[\s\S]*?(?:\*/|$))|(?<string>""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|`(?:\\.|[^`\\])*`)|(?<number>\b\d+(?:\.\d+)?\b)|(?<keyword>\b(?:async|await|break|case|catch|class|const|continue|default|delete|do|else|export|extends|false|finally|for|from|function|if|import|in|instanceof|let|new|null|of|return|static|super|switch|this|throw|true|try|typeof|var|void|while|yield)\b)";
        var regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        foreach (Match match in regex.Matches(source))
        { ct.ThrowIfCancellationRequested(); yield return match; }
    }

    private static void Add(LanguageTextSnapshot full, List<SemanticToken> output, int at, int length, string kind)
    {
        var end = Math.Min(full.Text.Length, at + length);
        while (at < end)
        {
            var position = full.PositionAt(at);
            var lineEnd = full.Text.IndexOf('\n', at, end - at);
            var next = lineEnd >= 0 ? lineEnd : end;
            if (next > at) output.Add(new(position.LineNumber, position.Column, next - at, kind));
            at = next + (lineEnd >= 0 ? 1 : 0);
        }
    }
}
