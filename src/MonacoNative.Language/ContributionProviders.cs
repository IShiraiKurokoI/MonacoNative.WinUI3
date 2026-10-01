namespace MonacoNative.Language;

/// <summary>A location in a language document.</summary>
public sealed record LanguageLocation(Uri Uri, LanguageRange Range);

public sealed record ReferenceContext(bool IncludeDeclaration = true);

public interface IReferenceProvider
{
    ValueTask<IReadOnlyList<LanguageLocation>?> ProvideReferencesAsync(
        ILanguageTextModel model,
        LanguagePosition position,
        ReferenceContext context,
        CancellationToken cancellationToken);
}

public sealed record RenameLocation(LanguageRange Range, string Text, string? RejectReason = null);
public sealed record WorkspaceTextEdit(Uri Uri, LanguageRange Range, string Text);
public sealed record WorkspaceEdit(IReadOnlyList<WorkspaceTextEdit> Edits);

public interface IRenameProvider
{
    ValueTask<RenameLocation?> ResolveRenameLocationAsync(
        ILanguageTextModel model,
        LanguagePosition position,
        CancellationToken cancellationToken) => ValueTask.FromResult<RenameLocation?>(null);

    ValueTask<WorkspaceEdit?> ProvideRenameEditsAsync(
        ILanguageTextModel model,
        LanguagePosition position,
        string newName,
        CancellationToken cancellationToken);
}

/// <summary>Monaco-style RGBA color. Components are normalized to the 0..1 range.</summary>
public readonly record struct LanguageColor(double Red, double Green, double Blue, double Alpha = 1.0)
{
    public LanguageColor Clamp() => new(
        Math.Clamp(Red, 0.0, 1.0),
        Math.Clamp(Green, 0.0, 1.0),
        Math.Clamp(Blue, 0.0, 1.0),
        Math.Clamp(Alpha, 0.0, 1.0));
}

public sealed record ColorInformation(LanguageRange Range, LanguageColor Color);
public sealed record ColorPresentation(
    string Label,
    LanguageTextEdit? TextEdit = null,
    IReadOnlyList<LanguageTextEdit>? AdditionalTextEdits = null);

public interface IColorProvider
{
    ValueTask<IReadOnlyList<ColorInformation>?> ProvideDocumentColorsAsync(
        ILanguageTextModel model,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ColorPresentation>?> ProvideColorPresentationsAsync(
        ILanguageTextModel model,
        ColorInformation colorInfo,
        CancellationToken cancellationToken);
}

public enum InlineCompletionTriggerKind { Automatic = 0, Explicit = 1 }
public sealed record InlineCompletionContext(InlineCompletionTriggerKind TriggerKind = InlineCompletionTriggerKind.Automatic);
public sealed record InlineCompletion(
    string InsertText,
    LanguageRange? Range = null,
    string? FilterText = null,
    Func<CancellationToken, ValueTask>? Command = null);
public sealed record InlineCompletions(IReadOnlyList<InlineCompletion> Items);

public interface IInlineCompletionsProvider
{
    ValueTask<InlineCompletions?> ProvideInlineCompletionsAsync(
        ILanguageTextModel model,
        LanguagePosition position,
        InlineCompletionContext context,
        CancellationToken cancellationToken);

    ValueTask FreeInlineCompletionsAsync(InlineCompletions completions) => ValueTask.CompletedTask;
}

public sealed record LanguageCommand(
    string Id,
    string Title,
    Func<CancellationToken, ValueTask>? Execute = null);

public sealed record CodeLens(LanguageRange Range, LanguageCommand? Command = null, string? Id = null);

public interface ICodeLensProvider
{
    ValueTask<IReadOnlyList<CodeLens>?> ProvideCodeLensesAsync(
        ILanguageTextModel model,
        CancellationToken cancellationToken);

    ValueTask<CodeLens> ResolveCodeLensAsync(CodeLens codeLens, CancellationToken cancellationToken) =>
        ValueTask.FromResult(codeLens);
}
