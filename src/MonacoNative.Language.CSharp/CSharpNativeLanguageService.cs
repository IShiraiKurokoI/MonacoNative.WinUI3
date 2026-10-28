using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Text;
using MonacoNative.Language;
using CompletionItem = MonacoNative.Language.CompletionItem;
using CompletionList = MonacoNative.Language.CompletionList;
using CompletionContext = MonacoNative.Language.CompletionContext;
using FormattingOptions = MonacoNative.Language.FormattingOptions;
using DiagnosticSeverity = MonacoNative.Language.DiagnosticSeverity;

namespace MonacoNative.Language.CSharp;

/// <summary>Explicit workspace inputs. No MSBuild evaluation, file crawling, or child process.</summary>
public sealed class CSharpLanguageOptions
{
    public IReadOnlyList<string> MetadataReferencePaths { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<Uri, string> Documents { get; init; } = new Dictionary<Uri, string>();
    /// <summary>
    /// Optional immutable snapshot of open C# documents. When supplied, overrides Documents.
    /// Called on the requesting thread before compiler work is dispatched. Do not read live
    /// editor models from this callback; publish copied URI/text snapshots instead.
    /// </summary>
    public Func<IReadOnlyList<NativeLanguageDocument>>? DocumentsProvider { get; init; }
    public IReadOnlyList<string> PreprocessorSymbols { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> GlobalUsings { get; init; } = new[]
    { "System", "System.Collections.Generic", "System.Linq", "System.Threading", "System.Threading.Tasks" };
    public bool IncludeRuntimeReferences { get; init; } = true;
    public LanguageVersion LanguageVersion { get; init; } = LanguageVersion.CSharp12;
}

/// <summary>
/// Direct integration of Roslyn's existing C# implementation, not a server wrapper or a keyword
/// emulator. Upstream libraries are restored as pinned NuGet dependencies, not vendored sources.
/// Immutable request snapshots keep completion edits tied to the document that produced them.
/// </summary>
public sealed partial class CSharpNativeLanguageService : ICompletionItemProvider, IHoverProvider,
    IDefinitionProvider, IReferenceProvider, ISignatureHelpProvider, IDocumentFormattingEditProvider,
    IDocumentRangeFormattingEditProvider, IDocumentDiagnosticsProvider, IDisposable
{
    private static readonly Lazy<MefHostServices> Host = new(() => MefHostServices.Create(
        MefHostServices.DefaultAssemblies.Concat(new[]
        {
            Assembly.Load("Microsoft.CodeAnalysis.CSharp.Workspaces"),
            Assembly.Load("Microsoft.CodeAnalysis.Features"),
            Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features")
        }).Distinct()));
    private readonly object _gate = new();
    private readonly CSharpLanguageOptions _options;
    private AdhocWorkspace? _workspace;
    private readonly ProjectId _projectId = ProjectId.CreateNewId();
    private readonly Dictionary<Uri, DocumentId> _documents = new();
    private readonly Dictionary<Uri, long> _revisions = new();
    private readonly HashSet<Uri> _providedDocumentUris = new();
    private long _providedDocumentsRevision;
    private long _nextRequest;
    private bool _disposed;
    public CSharpNativeLanguageService(CSharpLanguageOptions? options = null) => _options = options ?? new();
    public IReadOnlyList<string> TriggerCharacters { get; } = new[] { ".", ":", "#", " ", "<", "\"" };
    public IReadOnlyList<string> SignatureHelpTriggerCharacters { get; } = new[] { "(", ",", "<" };
    public IReadOnlyList<string> SignatureHelpRetriggerCharacters { get; } = new[] { ")", "," };

    private void EnsureWorkspace()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_workspace is not null) return;
        var workspace = new AdhocWorkspace(Host.Value);
        try
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_options.IncludeRuntimeReferences)
                foreach (var path in ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string)?.Split(Path.PathSeparator)
                    ?? new[] { typeof(object).Assembly.Location, typeof(Console).Assembly.Location, typeof(Enumerable).Assembly.Location }))
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    if (name is "netstandard" or "mscorlib" or "Microsoft.CSharp" or "System" || name.StartsWith("System.", StringComparison.Ordinal)) paths.Add(path);
                }
            foreach (var path in _options.MetadataReferencePaths)
            {
                var fullPath = Path.GetFullPath(path);
                if (!File.Exists(fullPath)) throw new FileNotFoundException("C# metadata reference does not exist.", fullPath);
                paths.Add(fullPath);
            }
            workspace.AddProject(ProjectInfo.Create(_projectId, VersionStamp.Create(), "MonacoNative.CSharp", "MonacoNative.CSharp",
                LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable),
                parseOptions: new CSharpParseOptions(_options.LanguageVersion, preprocessorSymbols: _options.PreprocessorSymbols),
                metadataReferences: paths.Where(File.Exists).Select(path => MetadataReference.CreateFromFile(path))));
            if (_options.GlobalUsings.Count > 0)
                workspace.AddDocument(_projectId, "__MonacoGlobalUsings.g.cs", SourceText.From(string.Join("\n", _options.GlobalUsings.Select(ns => "global using " + ns + ";"))));
            _workspace = workspace;
            if (_options.DocumentsProvider is null)
                foreach (var pair in _options.Documents) UpdateDocument(pair.Key, pair.Value);
        }
        catch { workspace.Dispose(); _workspace = null; _documents.Clear(); _revisions.Clear(); throw; }
    }
    public void SetDocument(Uri uri, string text)
    {
        ArgumentNullException.ThrowIfNull(uri); ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            EnsureWorkspace();
            UpdateDocument(uri, text);
            _revisions[uri] = Interlocked.Increment(ref _nextRequest);
        }
    }
    public void RemoveDocument(Uri uri)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_workspace is not null && _documents.Remove(uri, out var id)) Apply(_workspace.CurrentSolution.RemoveDocument(id));
            _revisions.Remove(uri);
        }
    }
    private Document UpdateDocument(Uri uri, string text)
    {
        var solution = _workspace!.CurrentSolution;
        if (!_documents.TryGetValue(uri, out var id))
        {
            id = DocumentId.CreateNewId(_projectId); _documents.Add(uri, id);
            var name = Path.GetFileName(uri.IsFile ? uri.LocalPath : uri.AbsolutePath);
            solution = solution.AddDocument(id, string.IsNullOrEmpty(name) ? "untitled.cs" : name, SourceText.From(text), filePath: uri.IsFile ? uri.LocalPath : null);
        }
        else solution = solution.WithDocumentText(id, SourceText.From(text), PreservationMode.PreserveIdentity);
        Apply(solution);
        return _workspace.CurrentSolution.GetDocument(id)!;
    }
    private Task<Document> SnapshotAsync(ILanguageTextModel model, CancellationToken ct)
    {
        // Capture on the caller's thread before moving expensive compiler work off the UI thread.
        var uri = model.Uri; var text = model.GetValue();
        var documents = _options.DocumentsProvider?.Invoke().ToArray();
        var order = Interlocked.Increment(ref _nextRequest);
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                EnsureWorkspace();
                ct.ThrowIfCancellationRequested();
                if (documents is not null)
                    SynchronizeProvidedDocuments(documents, uri, order);
                // A delayed older worker may use its own immutable document, but must not
                // roll the shared project back after a newer request has updated it.
                if (_revisions.TryGetValue(uri, out var latest) && order < latest)
                {
                    if (_documents.TryGetValue(uri, out var existing))
                        return _workspace!.CurrentSolution.WithDocumentText(existing, SourceText.From(text),
                            PreservationMode.PreserveIdentity).GetDocument(existing)!;
                    // A newer workspace snapshot may already have closed this document.
                    // Analyze the old request without adding it back to the live workspace.
                    var transient = DocumentId.CreateNewId(_projectId);
                    var name = Path.GetFileName(uri.IsFile ? uri.LocalPath : uri.AbsolutePath);
                    return _workspace!.CurrentSolution.AddDocument(transient,
                        string.IsNullOrEmpty(name) ? "untitled.cs" : name, SourceText.From(text),
                        filePath: uri.IsFile ? uri.LocalPath : null).GetDocument(transient)!;
                }
                var document = UpdateDocument(uri, text);
                _revisions[uri] = order;
                return document;
            }
        }, ct);
    }

    private void SynchronizeProvidedDocuments(IReadOnlyList<NativeLanguageDocument> documents,
        Uri requestUri, long revision)
    {
        if (revision < _providedDocumentsRevision) return;
        var snapshot = new Dictionary<Uri, string>();
        foreach (var document in documents)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentNullException.ThrowIfNull(document.Uri);
            ArgumentNullException.ThrowIfNull(document.Text);
            snapshot[document.Uri] = document.Text;
        }
        foreach (var uri in _providedDocumentUris.Where(uri => uri != requestUri && !snapshot.ContainsKey(uri)).ToArray())
        {
            if (_revisions.TryGetValue(uri, out var latest) && latest > revision) continue;
            if (_documents.Remove(uri, out var id))
                Apply(_workspace!.CurrentSolution.RemoveDocument(id));
            // Keep a revision tombstone so an older request cannot reopen this file.
            _revisions[uri] = revision;
        }
        foreach (var pair in snapshot)
        {
            if (pair.Key == requestUri || _revisions.TryGetValue(pair.Key, out var latest) && latest > revision) continue;
            UpdateDocument(pair.Key, pair.Value);
            _revisions[pair.Key] = revision;
        }
        _providedDocumentUris.Clear();
        _providedDocumentUris.UnionWith(snapshot.Keys);
        _providedDocumentsRevision = revision;
    }
    private void Apply(Solution solution)
    { if (!_workspace!.TryApplyChanges(solution)) throw new InvalidOperationException("The C# workspace rejected an update."); }
    private static LanguageRange Range(SourceText text, TextSpan span)
    {
        var a = text.Lines.GetLinePosition(Math.Clamp(span.Start, 0, text.Length));
        var b = text.Lines.GetLinePosition(Math.Clamp(span.End, 0, text.Length));
        return new(a.Line + 1, a.Character + 1, b.Line + 1, b.Character + 1);
    }
    private static int Offset(SourceText text, LanguagePosition position)
    {
        var line = text.Lines[Math.Clamp(position.LineNumber - 1, 0, text.Lines.Count - 1)];
        return line.Start + Math.Clamp(position.Column - 1, 0, line.Span.Length);
    }
    public async ValueTask<CompletionList?> ProvideCompletionItemsAsync(ILanguageTextModel model,
        LanguagePosition position, CompletionContext context, CancellationToken ct)
    {
        var document = await SnapshotAsync(model, ct).ConfigureAwait(false);
        var text = await document.GetTextAsync(ct).ConfigureAwait(false);
        var service = CompletionService.GetService(document) ?? throw new InvalidOperationException("Roslyn C# completion exports were not composed. Check the Features dependencies.");
        var trigger = context.TriggerKind == MonacoNative.Language.CompletionTriggerKind.TriggerCharacter && context.TriggerCharacter is { Length: 1 } character
            ? CompletionTrigger.CreateInsertionTrigger(character[0]) : CompletionTrigger.Invoke;
        var list = await service.GetCompletionsAsync(document, Offset(text, position), trigger, cancellationToken: ct).ConfigureAwait(false);
        if (list is null) return new CompletionList(Array.Empty<CompletionItem>());
        var suggestions = new List<CompletionItem>();
        foreach (var item in list.ItemsList)
        {
            ct.ThrowIfCancellationRequested();
            var original = item;
            var mapped = new CompletionItem(item.DisplayText, Kind(item.Tags), InsertText: item.DisplayText,
                FilterText: item.FilterText, SortText: item.SortText, Range: Range(text, item.Span),
                Preselect: item.Rules.MatchPriority > 0, Deprecated: item.Tags.Contains("Deprecated"), RequireResolvedEdit: true);
            suggestions.Add(mapped with
            {
                Resolve = async token =>
                {
                    var change = await service.GetChangeAsync(document, original, cancellationToken: token).ConfigureAwait(false);
                    var description = await service.GetDescriptionAsync(document, original, token).ConfigureAwait(false);
                    // Roslyn's merged edit is authoritative, including any required import edits.
                    var edit = change.TextChange;
                    return mapped with { Range = Range(text, edit.Span), InsertText = edit.NewText ?? string.Empty,
                        Documentation = description?.Text, RequireResolvedEdit = false, CaretOffset = change.NewPosition is int caret
                            ? Math.Clamp(caret - edit.Span.Start, 0, (edit.NewText ?? string.Empty).Length) : null };
                }
            });
        }
        return new CompletionList(suggestions);
    }
    private static CompletionItemKind Kind(ImmutableArray<string> tags)
    {
        foreach (var tag in tags)
        {
            var kind = tag switch
            {
                "Method" or "ExtensionMethod" => CompletionItemKind.Method, "Property" => CompletionItemKind.Property,
                "Field" => CompletionItemKind.Field, "Event" => CompletionItemKind.Event, "Class" => CompletionItemKind.Class,
                "Structure" => CompletionItemKind.Struct, "Interface" => CompletionItemKind.Interface, "Enum" => CompletionItemKind.Enum,
                "EnumMember" => CompletionItemKind.EnumMember, "Constant" => CompletionItemKind.Constant, "Namespace" => CompletionItemKind.Module,
                "TypeParameter" => CompletionItemKind.TypeParameter, "Keyword" => CompletionItemKind.Keyword,
                "Local" or "Parameter" or "RangeVariable" => CompletionItemKind.Variable,
                "Delegate" => CompletionItemKind.Function, "Snippet" => CompletionItemKind.Snippet, _ => CompletionItemKind.Text
            };
            if (kind != CompletionItemKind.Text) return kind;
        }
        return CompletionItemKind.Text;
    }
    public async ValueTask<Hover?> ProvideHoverAsync(ILanguageTextModel model, LanguagePosition position, CancellationToken ct)
    {
        var document = await SnapshotAsync(model, ct).ConfigureAwait(false);
        var text = await document.GetTextAsync(ct).ConfigureAwait(false);
        var service = QuickInfoService.GetService(document);
        if (service is null) return null;
        var info = await service.GetQuickInfoAsync(document, Offset(text, position), ct).ConfigureAwait(false);
        return info is null ? null : new Hover(info.Sections.Select(s => new MarkdownString(s.Text)).ToArray(), Range(text, info.Span));
    }
    private Uri? GetUri(Document document)
    {
        lock (_gate) foreach (var pair in _documents) if (pair.Value == document.Id) return pair.Key;
        return document.FilePath is { } path ? new Uri(Path.GetFullPath(path)) : null;
    }
    public async ValueTask<IReadOnlyList<LanguageLocation>?> ProvideDefinitionAsync(ILanguageTextModel model, LanguagePosition position, CancellationToken ct)
    {
        var document = await SnapshotAsync(model, ct).ConfigureAwait(false);
        var text = await document.GetTextAsync(ct).ConfigureAwait(false);
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, Offset(text, position), ct).ConfigureAwait(false);
        if (symbol is IAliasSymbol alias) symbol = alias.Target;
        var output = new List<LanguageLocation>();
        if (symbol is null) return output;
        foreach (var location in symbol.Locations.Where(l => l.IsInSource))
        {
            var target = document.Project.Solution.GetDocument(location.SourceTree!);
            if (target is null || GetUri(target) is not { } uri) continue;
            output.Add(new(uri, Range(await target.GetTextAsync(ct).ConfigureAwait(false), location.SourceSpan)));
        }
        return output;
    }
    public async ValueTask<IReadOnlyList<LanguageLocation>?> ProvideReferencesAsync(ILanguageTextModel model, LanguagePosition position, ReferenceContext context, CancellationToken ct)
    {
        var document = await SnapshotAsync(model, ct).ConfigureAwait(false);
        var text = await document.GetTextAsync(ct).ConfigureAwait(false);
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, Offset(text, position), ct).ConfigureAwait(false);
        if (symbol is null) return Array.Empty<LanguageLocation>();
        var references = await SymbolFinder.FindReferencesAsync(symbol, document.Project.Solution, ct).ConfigureAwait(false);
        var output = new List<LanguageLocation>();
        foreach (var referenced in references)
        {
            if (context.IncludeDeclaration)
                foreach (var location in referenced.Definition.Locations.Where(l => l.IsInSource))
                {
                    var target = document.Project.Solution.GetDocument(location.SourceTree!);
                    if (target is null || GetUri(target) is not { } uri) continue;
                    output.Add(new(uri, Range(await target.GetTextAsync(ct).ConfigureAwait(false), location.SourceSpan)));
                }
            foreach (var reference in referenced.Locations)
            {
                if (GetUri(reference.Document) is not { } uri) continue;
                output.Add(new(uri, Range(await reference.Document.GetTextAsync(ct).ConfigureAwait(false), reference.Location.SourceSpan)));
            }
        }
        return output.Distinct().ToArray();
    }
    public async ValueTask<SignatureHelp?> ProvideSignatureHelpAsync(ILanguageTextModel model, LanguagePosition position, SignatureHelpContext context, CancellationToken ct)
    {
        var document = await SnapshotAsync(model, ct).ConfigureAwait(false);
        var text = await document.GetTextAsync(ct).ConfigureAwait(false);
        var offset = Offset(text, position);
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var semantic = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (root is null || semantic is null || text.Length == 0) return null;
        var node = root.FindToken(Math.Clamp(offset - 1, 0, text.Length - 1)).Parent;
        var arguments = node?.AncestorsAndSelf().OfType<ArgumentListSyntax>().FirstOrDefault(a => a.OpenParenToken.SpanStart < offset && (a.CloseParenToken.IsMissing || offset <= a.CloseParenToken.SpanStart));
        if (arguments is null) return null;
        IEnumerable<IMethodSymbol> methods = Array.Empty<IMethodSymbol>();
        if (arguments.Parent is InvocationExpressionSyntax invocation) methods = semantic.GetMemberGroup(invocation.Expression, ct).OfType<IMethodSymbol>();
        else if (arguments.Parent is ObjectCreationExpressionSyntax creation && semantic.GetTypeInfo(creation, ct).Type is INamedTypeSymbol type)
            methods = type.InstanceConstructors.Where(m => semantic.IsAccessible(offset, m));
        var active = arguments.Arguments.GetSeparators().Count(separator => separator.SpanStart < offset);
        var candidates = methods.ToArray();
        var signatures = candidates.Select(method => new SignatureInformation(method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            Parameters: method.Parameters.Select(p => new ParameterInformation(p.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))).ToArray(),
            ActiveParameter: Math.Min(active, Math.Max(0, method.Parameters.Length - 1)))).ToArray();
        if (signatures.Length == 0) return null;
        // Unlike a purely syntactic picker, the Roslyn provider can inspect the types of local
        // variables and expressions. Keep the result as an ActiveSignature preference for the UI.
        var argumentTypes = arguments.Arguments.Select(a => semantic.GetTypeInfo(a.Expression, ct).Type).ToArray();
        var required = active + 1;
        var selected = 0;
        var bestScore = int.MinValue;
        for (var index = 0; index < candidates.Length; index++)
        {
            var candidate = candidates[index];
            var parameters = candidate.Parameters;
            var variadic = parameters.Length > 0 && parameters[^1].IsParams;
            var minimum = parameters.Count(p => !p.IsOptional && !p.IsParams);
            var maximum = variadic ? int.MaxValue : parameters.Length;
            var score = required <= maximum ? 100 : -1000;
            if (required >= minimum && required <= maximum) score += 10;
            if (parameters.Length == required) score += 5;
            for (var arg = 0; arg < argumentTypes.Length; arg++)
            {
                if (parameters.Length == 0 || (arg >= parameters.Length && !variadic)) break;
                var actual = argumentTypes[arg];
                if (actual is null) continue;
                var parameter = parameters[Math.Min(arg, parameters.Length - 1)].Type;
                if (SymbolEqualityComparer.Default.Equals(actual, parameter)) score += 40;
                else
                {
                    var conversion = semantic.Compilation.ClassifyCommonConversion(actual, parameter);
                    score += conversion.Exists && conversion.IsImplicit ? 8 : -30;
                }
            }
            if (score <= bestScore) continue;
            bestScore = score;
            selected = index;
        }
        return new SignatureHelp(signatures, ActiveSignature: selected, ActiveParameter: active);
    }
    public ValueTask<IReadOnlyList<LanguageTextEdit>?> ProvideDocumentFormattingEditsAsync(ILanguageTextModel model, FormattingOptions options, CancellationToken ct) => FormatAsync(model, null, options, ct);
    public ValueTask<IReadOnlyList<LanguageTextEdit>?> ProvideDocumentRangeFormattingEditsAsync(ILanguageTextModel model, LanguageRange range, FormattingOptions options, CancellationToken ct) => FormatAsync(model, range, options, ct);
    private async ValueTask<IReadOnlyList<LanguageTextEdit>?> FormatAsync(ILanguageTextModel model, LanguageRange? range, FormattingOptions options, CancellationToken ct)
    {
        var document = await SnapshotAsync(model, ct).ConfigureAwait(false);
        var text = await document.GetTextAsync(ct).ConfigureAwait(false);
        var settings = document.Project.Solution.Options
            .WithChangedOption(Microsoft.CodeAnalysis.Formatting.FormattingOptions.UseTabs, LanguageNames.CSharp, !options.InsertSpaces)
            .WithChangedOption(Microsoft.CodeAnalysis.Formatting.FormattingOptions.TabSize, LanguageNames.CSharp, Math.Max(1, options.TabSize))
            .WithChangedOption(Microsoft.CodeAnalysis.Formatting.FormattingOptions.IndentationSize, LanguageNames.CSharp, Math.Max(1, options.TabSize))
            .WithChangedOption(Microsoft.CodeAnalysis.Formatting.FormattingOptions.NewLine, LanguageNames.CSharp, text.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n");
        Document formatted;
        if (range is { } r)
        {
            r = r.Normalize();
            var span = TextSpan.FromBounds(Offset(text, new(r.StartLineNumber, r.StartColumn)), Offset(text, new(r.EndLineNumber, r.EndColumn)));
            formatted = await Formatter.FormatAsync(document, span, settings, ct).ConfigureAwait(false);
        }
        else formatted = await Formatter.FormatAsync(document, settings, ct).ConfigureAwait(false);
        return (await formatted.GetTextChangesAsync(document, ct).ConfigureAwait(false)).Select(edit => new LanguageTextEdit(Range(text, edit.Span), edit.NewText ?? string.Empty)).ToArray();
    }
    public async ValueTask<IReadOnlyList<LanguageDiagnostic>?> ProvideDiagnosticsAsync(ILanguageTextModel model, CancellationToken ct)
    {
        var document = await SnapshotAsync(model, ct).ConfigureAwait(false);
        var text = await document.GetTextAsync(ct).ConfigureAwait(false);
        var semantic = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (semantic is null) return Array.Empty<LanguageDiagnostic>();
        return semantic.GetDiagnostics(cancellationToken: ct).Where(d => d.Location.IsInSource && d.Severity != Microsoft.CodeAnalysis.DiagnosticSeverity.Hidden)
            .Select(d => new LanguageDiagnostic(Range(text, d.Location.SourceSpan), d.GetMessage(),
                d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error ? DiagnosticSeverity.Error :
                d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Warning ? DiagnosticSeverity.Warning : DiagnosticSeverity.Information,
                d.Id, "roslyn-inprocess")).ToArray();
    }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _documents.Clear(); _revisions.Clear(); _workspace?.Dispose(); _workspace = null; }
    }
}
