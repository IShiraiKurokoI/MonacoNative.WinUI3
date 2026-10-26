namespace MonacoNative.Language;

public sealed class MonacoLanguageService
{
    private sealed record ProviderEntry<T>(long Token, long Order, LanguageSelector Selector, T Provider) where T : class;
    private sealed class ProviderRegistry<T> where T : class
    {
        private readonly object _gate = new();
        private readonly List<ProviderEntry<T>> _entries = new();
        private long _nextToken = 1;
        private long _nextOrder = 1;

        public IDisposable Register(LanguageSelector selector, T provider)
        {
            ArgumentNullException.ThrowIfNull(selector);
            ArgumentNullException.ThrowIfNull(provider);
            long token;
            lock (_gate)
            {
                token = _nextToken++;
                _entries.Add(new ProviderEntry<T>(token, _nextOrder++, selector, provider));
            }
            return new DisposableAction(() => { lock (_gate) _entries.RemoveAll(e => e.Token == token); });
        }

        public IReadOnlyList<T> Matching(ILanguageTextModel model)
        {
            lock (_gate)
            {
                return _entries
                    .Select(e => (Entry: e, Score: LanguageSelectorScore.Score(e.Selector, model.Uri, model.LanguageId)))
                    .Where(x => x.Score > 0)
                    .OrderByDescending(x => x.Score)
                    .ThenByDescending(x => x.Entry.Order)
                    .Select(x => x.Entry.Provider)
                    .ToArray();
            }
        }
    }

    private readonly ProviderRegistry<ICompletionItemProvider> _completion = new();
    private readonly ProviderRegistry<IHoverProvider> _hover = new();
    private readonly ProviderRegistry<ISignatureHelpProvider> _signature = new();
    private readonly ProviderRegistry<IInlayHintsProvider> _inlay = new();
    private readonly ProviderRegistry<ICodeActionProvider> _codeAction = new();
    private readonly ProviderRegistry<IReferenceProvider> _reference = new();
    private readonly ProviderRegistry<IRenameProvider> _rename = new();
    private readonly ProviderRegistry<IColorProvider> _color = new();
    private readonly ProviderRegistry<IInlineCompletionsProvider> _inlineCompletion = new();
    private readonly ProviderRegistry<ICodeLensProvider> _codeLens = new();
    private readonly ProviderRegistry<IDefinitionProvider> _definition = new();
    private readonly ProviderRegistry<IDocumentFormattingEditProvider> _formatting = new();
    private readonly ProviderRegistry<IDocumentRangeFormattingEditProvider> _rangeFormatting = new();
    private readonly ProviderRegistry<IDocumentSemanticTokensProvider> _semanticTokens = new();
    private readonly ProviderRegistry<IDocumentDiagnosticsProvider> _documentDiagnostics = new();
    private readonly ProviderRegistry<IFoldingRangeProvider> _folding = new();
    private readonly object _diagnosticsGate = new();
    private readonly Dictionary<Uri, IReadOnlyList<LanguageDiagnostic>> _diagnostics = new();

    public MonacoLanguageService(bool registerBuiltins = true)
    {
        Languages = new LanguageRegistry();
        Packages = new LanguagePackageCollection(this);
        if (registerBuiltins) Packages.Install(new PlainTextLanguagePackage());
    }

    public LanguageRegistry Languages { get; }
    public LanguagePackageCollection Packages { get; }
    public event EventHandler<LanguageDiagnosticsChangedEventArgs>? DiagnosticsChanged;

    public IDisposable Register(LanguageExtensionPoint language) => Languages.Register(language);
    public IReadOnlyList<LanguageExtensionPoint> GetLanguages() => Languages.GetLanguages();
    public IDisposable SetLanguageConfiguration(string languageId, LanguageConfiguration configuration) => Languages.SetLanguageConfiguration(languageId, configuration);
    public IDisposable SetMonarchTokensProvider(string languageId, MonarchLanguage definition) => Languages.SetMonarchTokensProvider(languageId, definition);
    public int Score(LanguageSelector selector, Uri uri, string languageId) => LanguageSelectorScore.Score(selector, uri, languageId);

    public IDisposable RegisterFoldingRangeProvider(LanguageSelector selector, IFoldingRangeProvider provider) => _folding.Register(selector, provider);
    public bool HasFoldingRangeProvider(ILanguageTextModel model) => _folding.Matching(model).Count > 0;

    public async ValueTask<IReadOnlyList<LanguageFoldingRange>?> ProvideFoldingRangesAsync(
        ILanguageTextModel model, FoldingContext? context = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        cancellationToken.ThrowIfCancellationRequested();
        model = LanguageModelSnapshot.Capture(model);
        var options = context ?? new FoldingContext();
        if (options.RangeLimit is < 0) throw new ArgumentOutOfRangeException(nameof(context), "RangeLimit cannot be negative.");
        foreach (var provider in _folding.Matching(model))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await provider.ProvideFoldingRangesAsync(model, options, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result is not null) return result;
        }
        return null;
    }

    public IDisposable RegisterCompletionItemProvider(LanguageSelector selector, ICompletionItemProvider provider) => _completion.Register(selector, provider);

    /// <summary>Use registered native-provider trigger characters rather than a hard-coded punctuation list.</summary>
    public bool IsCompletionTriggerCharacter(ILanguageTextModel model, string character) =>
        !string.IsNullOrEmpty(character) && _completion.Matching(model).Any(provider =>
            provider.TriggerCharacters.Contains(character, StringComparer.Ordinal));

    public bool IsSignatureHelpTriggerCharacter(ILanguageTextModel model, string character) =>
        !string.IsNullOrEmpty(character) && _signature.Matching(model).Any(provider =>
            provider.SignatureHelpTriggerCharacters.Contains(character, StringComparer.Ordinal));
    public bool IsSignatureHelpRetriggerCharacter(ILanguageTextModel model, string character) =>
        !string.IsNullOrEmpty(character) && _signature.Matching(model).Any(provider =>
            provider.SignatureHelpRetriggerCharacters.Contains(character, StringComparer.Ordinal));
    public IDisposable RegisterHoverProvider(LanguageSelector selector, IHoverProvider provider) => _hover.Register(selector, provider);
    public IDisposable RegisterSignatureHelpProvider(LanguageSelector selector, ISignatureHelpProvider provider) => _signature.Register(selector, provider);
    public IDisposable RegisterInlayHintsProvider(LanguageSelector selector, IInlayHintsProvider provider) => _inlay.Register(selector, provider);
    public IDisposable RegisterCodeActionProvider(LanguageSelector selector, ICodeActionProvider provider) => _codeAction.Register(selector, provider);
    public IDisposable RegisterReferenceProvider(LanguageSelector selector, IReferenceProvider provider) => _reference.Register(selector, provider);
    public IDisposable RegisterRenameProvider(LanguageSelector selector, IRenameProvider provider) => _rename.Register(selector, provider);
    public IDisposable RegisterColorProvider(LanguageSelector selector, IColorProvider provider) => _color.Register(selector, provider);
    public IDisposable RegisterInlineCompletionsProvider(LanguageSelector selector, IInlineCompletionsProvider provider) => _inlineCompletion.Register(selector, provider);
    public IDisposable RegisterCodeLensProvider(LanguageSelector selector, ICodeLensProvider provider) => _codeLens.Register(selector, provider);
    public IDisposable RegisterDefinitionProvider(LanguageSelector selector, IDefinitionProvider provider) => _definition.Register(selector, provider);
    public IDisposable RegisterDocumentFormattingEditProvider(LanguageSelector selector, IDocumentFormattingEditProvider provider) => _formatting.Register(selector, provider);
    public IDisposable RegisterDocumentRangeFormattingEditProvider(LanguageSelector selector, IDocumentRangeFormattingEditProvider provider) => _rangeFormatting.Register(selector, provider);
    public IDisposable RegisterDocumentSemanticTokensProvider(LanguageSelector selector, IDocumentSemanticTokensProvider provider) => _semanticTokens.Register(selector, provider);

    public IDisposable RegisterDocumentDiagnosticsProvider(LanguageSelector selector, IDocumentDiagnosticsProvider provider) => _documentDiagnostics.Register(selector, provider);
    public bool HasDiagnosticsProvider(ILanguageTextModel model) => _documentDiagnostics.Matching(model).Count > 0;
    public async ValueTask<IReadOnlyList<LanguageDiagnostic>> ProvideDiagnosticsAsync(ILanguageTextModel model, CancellationToken cancellationToken = default)
    {
        model = LanguageModelSnapshot.Capture(model);
        var output = new List<LanguageDiagnostic>();
        foreach (var provider in _documentDiagnostics.Matching(model))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var diagnostics = await provider.ProvideDiagnosticsAsync(model, cancellationToken).ConfigureAwait(false);
            if (diagnostics is not null) output.AddRange(diagnostics);
        }
        return output;
    }

    public void SetDiagnostics(Uri uri, IReadOnlyList<LanguageDiagnostic>? diagnostics, int? version = null)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var snapshot = diagnostics?.ToArray() ?? Array.Empty<LanguageDiagnostic>();
        lock (_diagnosticsGate)
        {
            if (snapshot.Length == 0) _diagnostics.Remove(uri);
            else _diagnostics[uri] = snapshot;
        }
        DiagnosticsChanged?.Invoke(this, new LanguageDiagnosticsChangedEventArgs(uri, snapshot, version));
    }

    public IReadOnlyList<LanguageDiagnostic> GetDiagnostics(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        lock (_diagnosticsGate)
            return _diagnostics.TryGetValue(uri, out var value) ? value : Array.Empty<LanguageDiagnostic>();
    }

    public void ClearDiagnostics(Uri? uri = null)
    {
        if (uri is not null)
        {
            SetDiagnostics(uri, Array.Empty<LanguageDiagnostic>());
            return;
        }
        Uri[] keys;
        lock (_diagnosticsGate)
        {
            keys = _diagnostics.Keys.ToArray();
            _diagnostics.Clear();
        }
        foreach (var key in keys)
            DiagnosticsChanged?.Invoke(this, new LanguageDiagnosticsChangedEventArgs(key, Array.Empty<LanguageDiagnostic>()));
    }

    public async ValueTask<CompletionList> ProvideCompletionItemsAsync(ILanguageTextModel model, LanguagePosition position, CompletionContext context, CancellationToken cancellationToken = default)
    {
        model = LanguageModelSnapshot.Capture(model);
        Languages.Encounter(model.LanguageId);
        var suggestions = new List<CompletionItem>();
        var incomplete = false;
        // Native semantic providers and explicit host providers are authoritative. A syntax-only
        // fallback must not pollute their results or mask a deliberately empty response.
        var providers = _completion.Matching(model);
        var answered = false;
        foreach (var provider in providers.Where(p => p is not IFallbackCompletionProvider))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await provider.ProvideCompletionItemsAsync(model, position, context, cancellationToken).ConfigureAwait(false);
            if (result is null) continue;
            answered = true;
            suggestions.AddRange(result.Suggestions.Select(item => item.Resolve is not null ? item :
                item with { Resolve = ct => provider.ResolveCompletionItemAsync(item, ct) }));
            incomplete |= result.IsIncomplete;
        }
        if (!answered)
            foreach (var provider in providers.Where(p => p is IFallbackCompletionProvider))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await provider.ProvideCompletionItemsAsync(model, position, context, cancellationToken).ConfigureAwait(false);
                if (result is null) continue;
                suggestions.AddRange(result.Suggestions.Select(item => item.Resolve is not null ? item :
                item with { Resolve = ct => provider.ResolveCompletionItemAsync(item, ct) }));
                incomplete |= result.IsIncomplete;
            }
        return new CompletionList(suggestions, incomplete);
    }

    public async ValueTask<Hover?> ProvideHoverAsync(ILanguageTextModel model, LanguagePosition position, CancellationToken cancellationToken = default)
    {
        model = LanguageModelSnapshot.Capture(model);
        Languages.Encounter(model.LanguageId);
        var contents = new List<MarkdownString>();
        LanguageRange? range = null;
        foreach (var provider in _hover.Matching(model))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = await provider.ProvideHoverAsync(model, position, cancellationToken).ConfigureAwait(false);
            if (value is null) continue;
            contents.AddRange(value.Contents);
            range ??= value.Range;
        }
        return contents.Count == 0 ? null : new Hover(contents, range);
    }

    public async ValueTask<SignatureHelp?> ProvideSignatureHelpAsync(ILanguageTextModel model, LanguagePosition position, SignatureHelpContext context, CancellationToken cancellationToken = default)
    {
        model = LanguageModelSnapshot.Capture(model);
        Languages.Encounter(model.LanguageId);
        foreach (var provider in _signature.Matching(model))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = await provider.ProvideSignatureHelpAsync(model, position, context, cancellationToken).ConfigureAwait(false);
            if (value is not null) return value;
        }
        return null;
    }

    public async ValueTask<IReadOnlyList<InlayHint>> ProvideInlayHintsAsync(ILanguageTextModel model, LanguageRange range, CancellationToken cancellationToken = default)
    {
        model = LanguageModelSnapshot.Capture(model);
        Languages.Encounter(model.LanguageId);
        var output = new List<InlayHint>();
        foreach (var provider in _inlay.Matching(model))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = await provider.ProvideInlayHintsAsync(model, range, cancellationToken).ConfigureAwait(false);
            if (values is not null) output.AddRange(values);
        }
        return output;
    }

    public async ValueTask<IReadOnlyList<CodeAction>> ProvideCodeActionsAsync(ILanguageTextModel model, LanguageRange range, CodeActionContext context, CancellationToken cancellationToken = default)
    {
        model = LanguageModelSnapshot.Capture(model);
        Languages.Encounter(model.LanguageId);
        var output = new List<CodeAction>();
        foreach (var provider in _codeAction.Matching(model))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = await provider.ProvideCodeActionsAsync(model, range, context, cancellationToken).ConfigureAwait(false);
            if (values is not null) output.AddRange(values);
        }
        return output;
    }

    public async ValueTask<IReadOnlyList<LanguageLocation>> ProvideReferencesAsync(ILanguageTextModel model, LanguagePosition position, ReferenceContext context, CancellationToken cancellationToken = default)
    {
        model = LanguageModelSnapshot.Capture(model);
        Languages.Encounter(model.LanguageId);
        var output = new List<LanguageLocation>();
        foreach (var provider in _reference.Matching(model))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = await provider.ProvideReferencesAsync(model, position, context, cancellationToken).ConfigureAwait(false);
            if (values is not null) output.AddRange(values);
        }
        return output;
    }

    public async ValueTask<RenameLocation?> ResolveRenameLocationAsync(ILanguageTextModel model, LanguagePosition position, CancellationToken cancellationToken = default)
    {
        model = LanguageModelSnapshot.Capture(model);
        Languages.Encounter(model.LanguageId);
        foreach (var provider in _rename.Matching(model))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = await provider.ResolveRenameLocationAsync(model, position, cancellationToken).ConfigureAwait(false);
            if (value is not null) return value;
        }
        return null;
    }

    public async ValueTask<WorkspaceEdit?> ProvideRenameEditsAsync(ILanguageTextModel model, LanguagePosition position, string newName, CancellationToken cancellationToken = default)
    {
        model = LanguageModelSnapshot.Capture(model);
        Languages.Encounter(model.LanguageId);
        foreach (var provider in _rename.Matching(model))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = await provider.ProvideRenameEditsAsync(model, position, newName, cancellationToken).ConfigureAwait(false);
            if (value is not null) return value;
        }
        return null;
    }

    public async ValueTask<IReadOnlyList<ColorInformation>> ProvideDocumentColorsAsync(ILanguageTextModel model, CancellationToken cancellationToken = default)
    {
        model = LanguageModelSnapshot.Capture(model);
        Languages.Encounter(model.LanguageId);
        var output = new List<ColorInformation>();
        foreach (var provider in _color.Matching(model))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = await provider.ProvideDocumentColorsAsync(model, cancellationToken).ConfigureAwait(false);
            if (values is not null) output.AddRange(values);
        }
        return output;
    }

    public async ValueTask<IReadOnlyList<ColorPresentation>> ProvideColorPresentationsAsync(ILanguageTextModel model, ColorInformation colorInfo, CancellationToken cancellationToken = default)
    {
        model = LanguageModelSnapshot.Capture(model);
        Languages.Encounter(model.LanguageId);
        var output = new List<ColorPresentation>();
        foreach (var provider in _color.Matching(model))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = await provider.ProvideColorPresentationsAsync(model, colorInfo, cancellationToken).ConfigureAwait(false);
            if (values is not null) output.AddRange(values);
        }
        return output;
    }

    public async ValueTask<InlineCompletions> ProvideInlineCompletionsAsync(ILanguageTextModel model, LanguagePosition position, InlineCompletionContext context, CancellationToken cancellationToken = default)
    {
        model = LanguageModelSnapshot.Capture(model);
        Languages.Encounter(model.LanguageId);
        var output = new List<InlineCompletion>();
        foreach (var provider in _inlineCompletion.Matching(model))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = await provider.ProvideInlineCompletionsAsync(model, position, context, cancellationToken).ConfigureAwait(false);
            if (values is not null) output.AddRange(values.Items);
        }
        return new InlineCompletions(output);
    }

    public async ValueTask<IReadOnlyList<CodeLens>> ProvideCodeLensesAsync(ILanguageTextModel model, CancellationToken cancellationToken = default)
    {
        model = LanguageModelSnapshot.Capture(model);
        Languages.Encounter(model.LanguageId);
        var output = new List<CodeLens>();
        foreach (var provider in _codeLens.Matching(model))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = await provider.ProvideCodeLensesAsync(model, cancellationToken).ConfigureAwait(false);
            if (values is null) continue;
            foreach (var lens in values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Add(lens.Command is null ? await provider.ResolveCodeLensAsync(lens, cancellationToken).ConfigureAwait(false) : lens);
            }
        }
        return output;
    }

    public async ValueTask<IReadOnlyList<LanguageLocation>> ProvideDefinitionAsync(ILanguageTextModel model, LanguagePosition position, CancellationToken cancellationToken = default)
    {
        model = LanguageModelSnapshot.Capture(model);
        Languages.Encounter(model.LanguageId);
        var output = new List<LanguageLocation>();
        foreach (var provider in _definition.Matching(model))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = await provider.ProvideDefinitionAsync(model, position, cancellationToken).ConfigureAwait(false);
            if (values is not null) output.AddRange(values);
        }
        return output;
    }

    public async ValueTask<IReadOnlyList<LanguageTextEdit>> ProvideDocumentFormattingEditsAsync(ILanguageTextModel model, FormattingOptions options, CancellationToken cancellationToken = default)
    {
        model = LanguageModelSnapshot.Capture(model);
        Languages.Encounter(model.LanguageId);
        foreach (var provider in _formatting.Matching(model))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = await provider.ProvideDocumentFormattingEditsAsync(model, options, cancellationToken).ConfigureAwait(false);
            if (values is not null) return values;
        }
        return Array.Empty<LanguageTextEdit>();
    }

    public async ValueTask<IReadOnlyList<LanguageTextEdit>> ProvideDocumentRangeFormattingEditsAsync(ILanguageTextModel model, LanguageRange range, FormattingOptions options, CancellationToken cancellationToken = default)
    {
        model = LanguageModelSnapshot.Capture(model);
        Languages.Encounter(model.LanguageId);
        foreach (var provider in _rangeFormatting.Matching(model))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = await provider.ProvideDocumentRangeFormattingEditsAsync(model, range, options, cancellationToken).ConfigureAwait(false);
            if (values is not null) return values;
        }
        return Array.Empty<LanguageTextEdit>();
    }

    public async ValueTask<SemanticTokens?> ProvideDocumentSemanticTokensAsync(ILanguageTextModel model, CancellationToken cancellationToken = default)
    {
        model = LanguageModelSnapshot.Capture(model);
        Languages.Encounter(model.LanguageId);
        foreach (var provider in _semanticTokens.Matching(model))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = await provider.ProvideDocumentSemanticTokensAsync(model, cancellationToken).ConfigureAwait(false);
            if (value is not null) return value;
        }
        return null;
    }

}
