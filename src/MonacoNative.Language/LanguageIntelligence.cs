namespace MonacoNative.Language;

public enum DiagnosticSeverity
{
    Error = 1,
    Warning = 2,
    Information = 3,
    Hint = 4,
}

public sealed record LanguageDiagnosticRelatedInformation(LanguageLocation Location, string Message);

public sealed record LanguageDiagnostic(
    LanguageRange Range,
    string Message,
    DiagnosticSeverity Severity = DiagnosticSeverity.Error,
    string? Code = null,
    string? Source = null,
    IReadOnlyList<LanguageDiagnosticRelatedInformation>? RelatedInformation = null,
    IReadOnlyList<string>? Tags = null,
    object? Data = null);

public sealed class LanguageDiagnosticsChangedEventArgs : EventArgs
{
    public LanguageDiagnosticsChangedEventArgs(Uri uri, IReadOnlyList<LanguageDiagnostic> diagnostics, int? version = null)
    {
        Uri = uri ?? throw new ArgumentNullException(nameof(uri));
        Diagnostics = diagnostics ?? Array.Empty<LanguageDiagnostic>();
        Version = version;
    }

    public Uri Uri { get; }
    public IReadOnlyList<LanguageDiagnostic> Diagnostics { get; }
    public int? Version { get; }
}

public interface IDefinitionProvider
{
    ValueTask<IReadOnlyList<LanguageLocation>?> ProvideDefinitionAsync(
        ILanguageTextModel model,
        LanguagePosition position,
        CancellationToken cancellationToken);
}

public sealed record FormattingOptions(
    int TabSize = 4,
    bool InsertSpaces = true,
    bool? TrimTrailingWhitespace = null,
    bool? InsertFinalNewline = null,
    bool? TrimFinalNewlines = null,
    IReadOnlyDictionary<string, object?>? Extra = null);

public interface IDocumentFormattingEditProvider
{
    ValueTask<IReadOnlyList<LanguageTextEdit>?> ProvideDocumentFormattingEditsAsync(
        ILanguageTextModel model,
        FormattingOptions options,
        CancellationToken cancellationToken);
}

public interface IDocumentRangeFormattingEditProvider
{
    ValueTask<IReadOnlyList<LanguageTextEdit>?> ProvideDocumentRangeFormattingEditsAsync(
        ILanguageTextModel model,
        LanguageRange range,
        FormattingOptions options,
        CancellationToken cancellationToken);
}

public sealed record SemanticTokensLegend(
    IReadOnlyList<string> TokenTypes,
    IReadOnlyList<string> TokenModifiers);

public sealed record SemanticToken(
    int LineNumber,
    int StartColumn,
    int Length,
    string TokenType,
    IReadOnlyList<string>? TokenModifiers = null);

public sealed record SemanticTokens(
    IReadOnlyList<SemanticToken> Tokens,
    string? ResultId = null);

public interface IDocumentSemanticTokensProvider
{
    SemanticTokensLegend Legend { get; }

    ValueTask<SemanticTokens?> ProvideDocumentSemanticTokensAsync(
        ILanguageTextModel model,
        CancellationToken cancellationToken);
}

/// <summary>Pull diagnostics for an immutable document snapshot.</summary>
public interface IDocumentDiagnosticsProvider
{
    ValueTask<IReadOnlyList<LanguageDiagnostic>?> ProvideDiagnosticsAsync(ILanguageTextModel model, CancellationToken cancellationToken);
}

/// <summary>Inclusive, 1-based model line numbers. The header line remains visible.</summary>
public sealed record LanguageFoldingRange(int StartLineNumber, int EndLineNumber, string? Kind = null);
public sealed record FoldingContext(int? RangeLimit = null, Action<Uri>? OnRangeLimitExceeded = null);
public interface IFoldingRangeProvider
{
    // null means not applicable; an empty result is authoritative and must NOT enable heuristic fallback.
    ValueTask<IReadOnlyList<LanguageFoldingRange>?> ProvideFoldingRangesAsync(
        ILanguageTextModel model, FoldingContext context, CancellationToken cancellationToken);
}
