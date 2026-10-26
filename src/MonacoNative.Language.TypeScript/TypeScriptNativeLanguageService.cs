using MonacoNative.Language;
using System.Text.Json;

namespace MonacoNative.Language.TypeScript;

/// <summary>Host-supplied workspace snapshots. No filesystem crawl or external server.</summary>
public sealed class TypeScriptLanguageOptions
{
    private IReadOnlyDictionary<Uri, string> _documents =
        new System.Collections.ObjectModel.ReadOnlyDictionary<Uri, string>(new Dictionary<Uri, string>());

    /// <summary>Copied snapshots. Relative imports resolve only within these files.</summary>
    public IReadOnlyDictionary<Uri, string> Documents
    {
        get => Volatile.Read(ref _documents);
        init => SetDocuments(value);
    }

    /// <summary>Atomically replaces the workspace snapshot without reinstalling the language package.</summary>
    public void SetDocuments(IEnumerable<KeyValuePair<Uri, string>> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var copy = new Dictionary<Uri, string>();
        foreach (var pair in documents)
        {
            ArgumentNullException.ThrowIfNull(pair.Key);
            ArgumentNullException.ThrowIfNull(pair.Value);
            copy.Add(pair.Key, pair.Value);
        }
        Volatile.Write(ref _documents,
            new System.Collections.ObjectModel.ReadOnlyDictionary<Uri, string>(copy));
    }

    /// <summary>If supplied, overrides Documents. Return a stable snapshot of other open files.</summary>
    public Func<IReadOnlyList<NativeLanguageDocument>>? DocumentsProvider { get; init; }
}

/// <summary>
/// C++ port of the TypeScript 5.8.3 scanner plus a bounded declaration/snapshot-module service.
/// This is not the upstream binder/checker, tsserver, or a Node/Go wrapper.
/// Dependency preprocessing also accepts JSX/TSX. Their semantic service remains
/// at the existing baseline; scanner/preprocessor support is not a full parser.
/// </summary>
public sealed class TypeScriptNativeLanguageService : NativeCompilerService, ISignatureHelpProvider, IFoldingRangeProvider
{
    private readonly TypeScriptLanguageOptions _options;
    public TypeScriptNativeLanguageService(TypeScriptLanguageOptions? options = null) : base(false) => _options = options ?? new();
    protected override IReadOnlyList<NativeLanguageDocument> GetAdditionalDocuments() =>
        _options.DocumentsProvider?.Invoke().ToArray() ?? _options.Documents.Select(pair => new NativeLanguageDocument(pair.Key, pair.Value)).ToArray();
    public override IReadOnlyList<string> TriggerCharacters { get; } = new[] { ".", "\"", "'", "`", "/" };
    protected override bool SupportsOperation(ILanguageTextModel model, uint operation) =>
        operation is 7 or 8 or 9
            ? model.LanguageId is "typescript" or "javascript" or "typescriptreact" or "javascriptreact"
            : SupportsModel(model);

    /// <summary>
    /// Runs the C++ translation of TypeScript 5.8.3 preProcessFile, without a
    /// Node runtime, disk crawl or external server. These are dependency records,
    /// not a promise that the bounded checker resolves every referenced module.
    /// </summary>
    public async ValueTask<TypeScriptPreProcessedFile?> PreProcessFileAsync(ILanguageTextModel model,
        bool readImportFiles = true, bool? detectJavaScriptImports = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        model = LanguageModelSnapshot.Capture(model);
        var detectJs = detectJavaScriptImports ?? (model.LanguageId is "javascript" or "javascriptreact");
        var operation = !readImportFiles ? 9u : detectJs ? 8u : 7u;
        using var response = await RequestAsync(model, new(1, 1), operation, cancellationToken).ConfigureAwait(false);
        if (response is null) return null;
        var root = response.Document.RootElement;
        static IReadOnlyList<TypeScriptFileReference> References(JsonElement root, string name) =>
            Array.AsReadOnly(root.GetProperty(name).EnumerateArray().Select(value => new TypeScriptFileReference(
                value.GetProperty("fileName").GetString() ?? string.Empty,
                value.GetProperty("pos").GetInt32(), value.GetProperty("end").GetInt32(),
                value.TryGetProperty("resolutionMode", out var mode) ? mode.GetInt32() : null,
                value.TryGetProperty("preserve", out var preserve) && preserve.ValueKind == JsonValueKind.True)).ToArray());
        IReadOnlyList<string>? ambient = root.TryGetProperty("ambientExternalModules", out var names)
            ? Array.AsReadOnly(names.EnumerateArray().Select(name => name.GetString() ?? string.Empty).ToArray()) : null;
        return new(model.Uri, model.VersionId, References(root, "referencedFiles"), References(root, "typeReferenceDirectives"),
            References(root, "libReferenceDirectives"), References(root, "importedFiles"), root.GetProperty("isLibFile").GetBoolean(), ambient);
    }
    public IReadOnlyList<string> SignatureHelpTriggerCharacters { get; } = new[] { "(", "," };
    public IReadOnlyList<string> SignatureHelpRetriggerCharacters { get; } = new[] { ")" };

    public async ValueTask<SignatureHelp?> ProvideSignatureHelpAsync(ILanguageTextModel model, LanguagePosition position,
        SignatureHelpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);
        using var response = await RequestAsync(model, position,
            context.TriggerKind == SignatureHelpTriggerKind.Invoke ? 11u : 6u, cancellationToken).ConfigureAwait(false);
        if (response is null || !response.Document.RootElement.TryGetProperty("signatures", out var values)) return null;
        var signatures = new List<SignatureInformation>();
        foreach (var value in values.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parameters = value.GetProperty("parameters").EnumerateArray()
                .Select(p => new ParameterInformation(p.GetProperty("label").GetString() ?? string.Empty)).ToArray();
            signatures.Add(new SignatureInformation(value.GetProperty("label").GetString() ?? string.Empty,
                Parameters: Array.AsReadOnly(parameters), ActiveParameter: value.GetProperty("activeParameter").GetInt32()));
        }
        if (signatures.Count == 0) return null;
        var root = response.Document.RootElement;
        return new SignatureHelp(signatures.AsReadOnly(), root.GetProperty("activeSignature").GetInt32(),
            root.GetProperty("activeParameter").GetInt32());
    }

    public async ValueTask<IReadOnlyList<LanguageFoldingRange>?> ProvideFoldingRangesAsync(ILanguageTextModel model,
        FoldingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);
        if (context.RangeLimit is < 0) throw new ArgumentOutOfRangeException(nameof(context));
        model = LanguageModelSnapshot.Capture(model);
        using var response = await RequestAsync(model, new(1, 1), 10, cancellationToken).ConfigureAwait(false);
        if (response is null) return null;
        var result = new List<LanguageFoldingRange>();
        var values = response.Document.RootElement.GetProperty("folding");
        var limit = context.RangeLimit ?? int.MaxValue;
        foreach (var value in values.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Count >= limit) break;
            result.Add(new(value.GetProperty("startLine").GetInt32(), value.GetProperty("endLine").GetInt32(), String(value, "kind")));
        }
        if (values.GetArrayLength() > limit) context.OnRangeLimitExceeded?.Invoke(model.Uri);
        return result.AsReadOnly();
    }

    protected override string LanguageSource => "typescript-native-partial";
    protected override string DefaultExtension => ".ts";
    protected override string GetDocumentExtension(ILanguageTextModel model) =>
        model.LanguageId is "javascript" or "javascriptreact" ? ".js" : ".ts";
    protected override bool SupportsModel(ILanguageTextModel model) => model.LanguageId is "typescript" or "javascript";
    protected override string? UnsupportedModelMessage(ILanguageTextModel model) =>
        "The native TypeScript declaration service does not yet parse JSX/TSX. Syntax highlighting and baseline completion remain available; no external server is started.";
}

/// <summary>
/// Upstream-compatible, zero-based UTF-16 offsets. An imported-file Pos points to
/// the opening quote and End uses the decoded name length; this is NOT an edit
/// range. The definition/completion providers use separate raw-literal spans.
/// ResolutionMode is null, 1 (require/CommonJS) or 99 (import/ESNext).
/// </summary>
public sealed record TypeScriptFileReference(string FileName, int Pos, int End, int? ResolutionMode = null, bool Preserve = false);

/// <summary>Immutable dependency information tied to the analyzed model snapshot.</summary>
public sealed record TypeScriptPreProcessedFile(Uri Uri, ulong VersionId,
    IReadOnlyList<TypeScriptFileReference> ReferencedFiles,
    IReadOnlyList<TypeScriptFileReference> TypeReferenceDirectives,
    IReadOnlyList<TypeScriptFileReference> LibReferenceDirectives,
    IReadOnlyList<TypeScriptFileReference> ImportedFiles,
    bool IsLibFile, IReadOnlyList<string>? AmbientExternalModules);
