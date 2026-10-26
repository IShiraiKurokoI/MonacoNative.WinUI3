using MonacoNative.Language;

namespace MonacoNative.Language.Css;

/// <summary>Native declaration-context completion over the ported scanner and a bounded structural tree.</summary>
public sealed class CssSyntaxCompletionProvider : ICompletionItemProvider, IHoverProvider, IDefinitionProvider, IDocumentDiagnosticsProvider
{
    private readonly CssCustomData _data;
    public CssSyntaxCompletionProvider(CssCustomData? data = null) => _data = data ?? new();
    public IReadOnlyList<string> TriggerCharacters { get; } = new[] { ":", "-", "(" };
    public ValueTask<CompletionList?> ProvideCompletionItemsAsync(ILanguageTextModel model, LanguagePosition position, CompletionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model); cancellationToken.ThrowIfCancellationRequested();
        var snapshot = new LanguageTextSnapshot(model.GetValue()); var offset = snapshot.OffsetAt(position);
        var document = CssDocument.Parse(snapshot.Text, cancellationToken, model.LanguageId); var block = document.BlockAt(offset);
        var output = new List<CompletionItem>();
        var token = document.Tokens.LastOrDefault(t => t.Offset < offset && offset <= t.Offset + t.Length);
        if (token is not null && (token.Type is CssTokenType.String or CssTokenType.BadString or CssTokenType.EscapedJavaScript or CssTokenType.BadEscapedJavaScript || token.Type == CssTokenType.Comment &&
            (offset < token.Offset + token.Length || token.Length < 4 || !token.Text.EndsWith("*/", StringComparison.Ordinal))))
            return ValueTask.FromResult<CompletionList?>(new(output));
        if (!block.AcceptsDeclarations) return ValueTask.FromResult<CompletionList?>(new(output));
        var declaration = block.Declarations.LastOrDefault(d => d.Start <= offset && offset <= d.End);
        var valueContext = declaration?.Colon is int colon && offset > colon;
        var start = token?.Type == CssTokenType.Ident ? token.Offset : offset;
        var end = token?.Type == CssTokenType.Ident ? token.Offset + token.Length : offset;
        var range = snapshot.Range(start, end);
        if (valueContext)
        {
            foreach (var value in (_data.Property(declaration!.Name)?.Values ?? Array.Empty<string>()).Concat(new[] { "inherit", "initial", "unset", "revert", "revert-layer" }).Distinct())
                output.Add(new(value, CompletionItemKind.Value, InsertText: value, Range: range));
            var before = snapshot.Text[..start].TrimEnd();
            var insideVariable = before.EndsWith("var(", StringComparison.Ordinal);
            foreach (var variable in document.AllDeclarations().Where(d => d.Name.StartsWith("--", StringComparison.Ordinal)).GroupBy(d => d.Name, StringComparer.Ordinal).Select(g => g.First()))
                output.Add(new(variable.Name, CompletionItemKind.Variable, InsertText: insideVariable ? variable.Name : "var(" + variable.Name + ")", FilterText: variable.Name, Range: range));
        }
        else
        {
            var hasColon = declaration?.Colon is not null;
            foreach (var property in _data.Properties)
                output.Add(new(property.Name, CompletionItemKind.Property, Documentation: property.Description,
                    InsertText: hasColon ? property.Name : property.Name + ": ", Range: range,
                    CommandId: hasColon ? null : "editor.action.triggerSuggest"));
        }
        return ValueTask.FromResult<CompletionList?>(new(output));
    }
    public ValueTask<Hover?> ProvideHoverAsync(ILanguageTextModel model, LanguagePosition position, CancellationToken cancellationToken)
    {
        var snapshot = new LanguageTextSnapshot(model.GetValue()); var offset = snapshot.OffsetAt(position);
        var document = CssDocument.Parse(snapshot.Text, cancellationToken, model.LanguageId);
        var property = document.AllDeclarations().FirstOrDefault(d => d.Start <= offset && offset <= d.NameEnd);
        var description = property is null ? null : _data.Property(property.Name)?.Description;
        return ValueTask.FromResult<Hover?>(description is null ? null : new(new[] { new MarkdownString(description) }, snapshot.Range(property!.Start, property.NameEnd)));
    }
    public ValueTask<IReadOnlyList<LanguageLocation>?> ProvideDefinitionAsync(ILanguageTextModel model, LanguagePosition position, CancellationToken cancellationToken)
    {
        var snapshot = new LanguageTextSnapshot(model.GetValue()); var offset = snapshot.OffsetAt(position);
        var document = CssDocument.Parse(snapshot.Text, cancellationToken, model.LanguageId);
        var token = document.Tokens.FirstOrDefault(t => t.Type == CssTokenType.Ident && t.Offset <= offset && offset <= t.Offset + t.Length);
        var locations = token?.Text.StartsWith("--", StringComparison.Ordinal) == true
            ? document.AllDeclarations().Where(d => d.Name == token.Text).Select(d => new LanguageLocation(model.Uri, snapshot.Range(d.Start, d.NameEnd))).ToArray()
            : Array.Empty<LanguageLocation>();
        return ValueTask.FromResult<IReadOnlyList<LanguageLocation>?>(locations);
    }
    public ValueTask<IReadOnlyList<LanguageDiagnostic>?> ProvideDiagnosticsAsync(ILanguageTextModel model, CancellationToken cancellationToken)
    {
        // The structural checker covers CSS only. Do not misdiagnose SCSS/LESS extensions.
        if (model.LanguageId != "css") return ValueTask.FromResult<IReadOnlyList<LanguageDiagnostic>?>(Array.Empty<LanguageDiagnostic>());
        var snapshot = new LanguageTextSnapshot(model.GetValue()); var document = CssDocument.Parse(snapshot.Text, cancellationToken, model.LanguageId);
        return ValueTask.FromResult<IReadOnlyList<LanguageDiagnostic>?>(document.Issues.Select(e => new LanguageDiagnostic(snapshot.Range(e.Offset, e.Offset + e.Length), e.Message, Source: "css-structure")).ToArray());
    }
}
