using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using MonacoNative.Language;
using CodeAction = MonacoNative.Language.CodeAction;

namespace MonacoNative.Language.CSharp;

/// <summary>Roslyn-backed refactoring, classification, and optional inline editing helpers.</summary>
public sealed partial class CSharpNativeLanguageService : IRenameProvider, ICodeActionProvider,
    IDocumentSemanticTokensProvider, IInlayHintsProvider
{
    public SemanticTokensLegend Legend { get; } = new(
        new[] { "namespace", "type", "class", "enum", "interface", "struct", "typeParameter",
            "parameter", "variable", "property", "enumMember", "event", "function", "method",
            "keyword", "comment", "string", "number", "operator" },
        new[] { "declaration", "static", "readonly", "deprecated", "async" });

    public IReadOnlyList<string> ProvidedCodeActionKinds { get; } = new[] { "quickfix", "source.organizeImports" };

    public async ValueTask<RenameLocation?> ResolveRenameLocationAsync(ILanguageTextModel model,
        LanguagePosition position, CancellationToken cancellationToken)
    {
        var document = await SnapshotAsync(model, cancellationToken).ConfigureAwait(false);
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, Offset(text, position), cancellationToken).ConfigureAwait(false);
        if (symbol is null || symbol.IsImplicitlyDeclared || symbol is INamespaceSymbol { IsGlobalNamespace: true }) return null;
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || text.Length == 0) return null;
        var token = root.FindToken(Math.Clamp(Offset(text, position), 0, text.Length - 1));
        var span = token.Span;
        if (!SyntaxFacts.IsValidIdentifier(token.ValueText) || !string.Equals(token.ValueText, symbol.Name, StringComparison.Ordinal))
            return null;
        return new RenameLocation(Range(text, span), token.Text);
    }

    public async ValueTask<WorkspaceEdit?> ProvideRenameEditsAsync(ILanguageTextModel model,
        LanguagePosition position, string newName, CancellationToken cancellationToken)
    {
        if (!SyntaxFacts.IsValidIdentifier(newName)) return null;
        var document = await SnapshotAsync(model, cancellationToken).ConfigureAwait(false);
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, Offset(text, position), cancellationToken).ConfigureAwait(false);
        if (symbol is null || symbol.IsImplicitlyDeclared) return null;
        if (symbol is IAliasSymbol alias) symbol = alias;
        var original = document.Project.Solution;
        // Roslyn resolves conflicts and updates references in the explicitly supplied workspace.
        var renamed = await Renamer.RenameSymbolAsync(original, symbol, new SymbolRenameOptions(), newName,
            cancellationToken).ConfigureAwait(false);
        return await ToWorkspaceEditsAsync(original, renamed, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<WorkspaceEdit> ToWorkspaceEditsAsync(Solution original, Solution updated, CancellationToken ct)
    {
        var changes = new List<WorkspaceTextEdit>();
        foreach (var documentId in original.ProjectIds.SelectMany(id => original.GetProject(id)!.DocumentIds))
        {
            ct.ThrowIfCancellationRequested();
            var before = original.GetDocument(documentId);
            var after = updated.GetDocument(documentId);
            if (before is null || after is null || GetUri(before) is not { } uri) continue;
            var originalText = await before.GetTextAsync(ct).ConfigureAwait(false);
            foreach (var change in await after.GetTextChangesAsync(before, ct).ConfigureAwait(false))
                changes.Add(new WorkspaceTextEdit(uri, Range(originalText, change.Span), change.NewText ?? string.Empty));
        }
        return new WorkspaceEdit(changes);
    }

    public async ValueTask<IReadOnlyList<CodeAction>?> ProvideCodeActionsAsync(ILanguageTextModel model,
        LanguageRange range, CodeActionContext context, CancellationToken ct)
    {
        var document = await SnapshotAsync(model, ct).ConfigureAwait(false);
        var text = await document.GetTextAsync(ct).ConfigureAwait(false);
        var actions = new List<CodeAction>();
        bool Accept(string kind) => context.Only is null || kind.StartsWith(context.Only, StringComparison.OrdinalIgnoreCase);
        if (Accept("source.organizeImports"))
        {
            var organized = await Formatter.OrganizeImportsAsync(document, ct).ConfigureAwait(false);
            var edits = (await organized.GetTextChangesAsync(document, ct).ConfigureAwait(false))
                .Select(change => new LanguageTextEdit(Range(text, change.Span), change.NewText ?? "")).ToArray();
            if (edits.Length != 0) actions.Add(new CodeAction("Organize usings", "source.organizeImports", edits));
        }
        if (!Accept("quickfix")) return actions;
        var semantic = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        if (semantic is null || root is null) return actions;
        var start = Offset(text, new(range.StartLineNumber, range.StartColumn));
        var end = Offset(text, new(range.EndLineNumber, range.EndColumn));
        var span = TextSpan.FromBounds(Math.Min(start, end), Math.Max(start, end));
        foreach (var diagnostic in semantic.GetDiagnostics(cancellationToken: ct))
        {
            ct.ThrowIfCancellationRequested();
            if (!diagnostic.Location.IsInSource || !diagnostic.Location.SourceSpan.IntersectsWith(span) &&
                !span.Contains(diagnostic.Location.SourceSpan.Start)) continue;
            // These are conservative syntax fixes. No unchecked text rewriting or project evaluation.
            if (diagnostic.Id == "CS0105") // Repeated using directive.
            {
                var usingNode = root.FindToken(Math.Clamp(diagnostic.Location.SourceSpan.Start, 0, Math.Max(0, text.Length - 1)))
                    .Parent?.AncestorsAndSelf().OfType<UsingDirectiveSyntax>().FirstOrDefault();
                if (usingNode is null) continue;
                var directive = usingNode.Span;
                actions.Add(new CodeAction("Remove duplicate using", "quickfix",
                    new[] { new LanguageTextEdit(Range(text, directive), "") }, IsPreferred: true));
            }
        }
        return actions;
    }

    public async ValueTask<SemanticTokens?> ProvideDocumentSemanticTokensAsync(ILanguageTextModel model, CancellationToken ct)
    {
        var document = await SnapshotAsync(model, ct).ConfigureAwait(false);
        var text = await document.GetTextAsync(ct).ConfigureAwait(false);
        var classified = await Classifier.GetClassifiedSpansAsync(document, new TextSpan(0, text.Length), ct).ConfigureAwait(false);
        var tokens = new List<SemanticToken>();
        foreach (var item in classified)
        {
            ct.ThrowIfCancellationRequested();
            var kind = MapClassification(item.ClassificationType);
            if (kind is null || item.TextSpan.IsEmpty) continue;
            var a = text.Lines.GetLinePosition(item.TextSpan.Start);
            var b = text.Lines.GetLinePosition(item.TextSpan.End);
            if (a.Line != b.Line) // Semantic spans cannot cross LSP token lines.
            {
                for (var line = a.Line; line <= b.Line; line++)
                {
                    var current = text.Lines[line];
                    var from = Math.Max(item.TextSpan.Start, current.Start);
                    var to = Math.Min(item.TextSpan.End, current.End);
                    if (to > from) tokens.Add(new(line + 1, from - current.Start + 1, to - from, kind));
                }
            }
            else tokens.Add(new(a.Line + 1, a.Character + 1, item.TextSpan.Length, kind));
        }
        return new SemanticTokens(tokens);
    }

    private static string? MapClassification(string classification)
    {
        if (classification.Contains("static", StringComparison.Ordinal) ||
            classification.Contains("obsolete", StringComparison.Ordinal)) return null; // Modifiers are not separate tokens.
        return classification switch
        {
            "namespace name" => "namespace", "class name" => "class", "record class name" => "class",
            "struct name" or "record struct name" => "struct", "interface name" => "interface",
            "enum name" => "enum", "type parameter name" => "typeParameter",
            "parameter name" => "parameter", "local name" => "variable", "field name" => "property",
            "property name" => "property", "enum member name" => "enumMember", "event name" => "event",
            "method name" or "extension method name" => "method", "keyword" or "control keyword" => "keyword",
            "comment" or "xml doc comment - text" => "comment", "string" or "verbatim string" => "string",
            "numeric literal" => "number", "operator" => "operator", _ => null
        };
    }

    public async ValueTask<IReadOnlyList<InlayHint>?> ProvideInlayHintsAsync(ILanguageTextModel model,
        LanguageRange range, CancellationToken ct)
    {
        var document = await SnapshotAsync(model, ct).ConfigureAwait(false);
        var text = await document.GetTextAsync(ct).ConfigureAwait(false);
        var semantic = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        if (semantic is null || root is null) return Array.Empty<InlayHint>();
        var from = Offset(text, new(range.StartLineNumber, range.StartColumn));
        var to = Offset(text, new(range.EndLineNumber, range.EndColumn));
        var span = TextSpan.FromBounds(Math.Min(from, to), Math.Max(from, to));
        var hints = new List<InlayHint>();
        foreach (var invocation in root.DescendantNodes(span).OfType<InvocationExpressionSyntax>())
        {
            ct.ThrowIfCancellationRequested();
            var method = semantic.GetSymbolInfo(invocation, ct).Symbol as IMethodSymbol;
            if (method is null) continue;
            foreach (var (argument, index) in invocation.ArgumentList.Arguments.Select((arg, i) => (arg, i)))
            {
                if (method.Parameters.Length == 0 || argument.NameColon is not null || index >= method.Parameters.Length && !(method.Parameters.Length > 0 && method.Parameters[^1].IsParams))
                    continue;
                if (!argument.Expression.Span.IntersectsWith(span) || hints.Count >= 200) continue;
                var parameter = method.Parameters[Math.Min(index, method.Parameters.Length - 1)];
                // Named arguments already show their name. Hints only add a visual label.
                hints.Add(new InlayHint(PositionAt(text, argument.Expression.SpanStart), parameter.Name + ":",
                    InlayHintKind.Parameter, PaddingRight: true));
            }
        }
        // Inferred local types can be surfaced without opening a project or executing a generator.
        foreach (var declaration in root.DescendantNodes(span).OfType<VariableDeclarationSyntax>())
        {
            ct.ThrowIfCancellationRequested();
            if (!declaration.Type.IsVar || hints.Count >= 200) continue;
            foreach (var variable in declaration.Variables)
            {
                if (hints.Count >= 200) break;
                if (!variable.Identifier.Span.IntersectsWith(span)) continue;
                if (semantic.GetDeclaredSymbol(variable, ct) is not ILocalSymbol local ||
                    local.Type.TypeKind == TypeKind.Error) continue;
                var label = local.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                if (label.Length is 0 or > 100) continue;
                hints.Add(new InlayHint(PositionAt(text, variable.Identifier.Span.End), ": " + label,
                    InlayHintKind.Type, PaddingLeft: true));
            }
        }
        return hints;
    }

    private static LanguagePosition PositionAt(SourceText text, int offset)
    {
        var location = text.Lines.GetLinePosition(Math.Clamp(offset, 0, text.Length));
        return new LanguagePosition(location.Line + 1, location.Character + 1);
    }
}
