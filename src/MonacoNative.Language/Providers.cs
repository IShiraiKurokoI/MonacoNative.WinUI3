namespace MonacoNative.Language;

public enum CompletionItemKind
{
    Method, Function, Constructor, Field, Variable, Class, Struct, Interface, Module,
    Property, Event, Operator, Unit, Value, Constant, Enum, EnumMember, Keyword,
    Text, Color, File, Reference, CustomColor, Folder, TypeParameter, User, Issue, Snippet,
}

public enum CompletionTriggerKind { Invoke = 0, TriggerCharacter = 1, TriggerForIncompleteCompletions = 2 }
public sealed record CompletionContext(CompletionTriggerKind TriggerKind = CompletionTriggerKind.Invoke, string? TriggerCharacter = null);
public sealed record CompletionItem(string Label, CompletionItemKind Kind = CompletionItemKind.Text, string? Detail = null,
    string? Documentation = null, string? InsertText = null, string? SortText = null, string? FilterText = null,
    LanguageRange? Range = null, IReadOnlyList<LanguageTextEdit>? AdditionalTextEdits = null,
    Func<CancellationToken, ValueTask>? Command = null, int? CaretOffset = null,
    string? SnippetText = null, LanguageRange? InsertRange = null, LanguageRange? ReplaceRange = null,
    bool Preselect = false, bool Deprecated = false, IReadOnlyList<string>? CommitCharacters = null,
    string? LabelDetail = null, string? LabelDescription = null, int InsertTextMode = 1,
    Func<CancellationToken, ValueTask<CompletionItem>>? Resolve = null, string? CommandId = null, bool RequireResolvedEdit = false);
public sealed record CompletionList(IReadOnlyList<CompletionItem> Suggestions, bool IsIncomplete = false);

public interface ICompletionItemProvider
{
    IReadOnlyList<string> TriggerCharacters => Array.Empty<string>();
    ValueTask<CompletionList?> ProvideCompletionItemsAsync(ILanguageTextModel model, LanguagePosition position, CompletionContext context, CancellationToken cancellationToken);
    ValueTask<CompletionItem> ResolveCompletionItemAsync(CompletionItem item, CancellationToken cancellationToken) => ValueTask.FromResult(item);
}

public sealed record MarkdownString(string Value, bool IsTrusted = false);
public sealed record Hover(IReadOnlyList<MarkdownString> Contents, LanguageRange? Range = null);
public interface IHoverProvider
{
    ValueTask<Hover?> ProvideHoverAsync(ILanguageTextModel model, LanguagePosition position, CancellationToken cancellationToken);
}

public sealed record ParameterInformation(string Label, string? Documentation = null);
public sealed record SignatureInformation(string Label, string? Documentation = null, IReadOnlyList<ParameterInformation>? Parameters = null, int? ActiveParameter = null);
public sealed record SignatureHelp(IReadOnlyList<SignatureInformation> Signatures, int ActiveSignature = 0, int ActiveParameter = 0);
public enum SignatureHelpTriggerKind { Invoke = 1, TriggerCharacter = 2, ContentChange = 3 }
public sealed record SignatureHelpContext(SignatureHelpTriggerKind TriggerKind = SignatureHelpTriggerKind.Invoke, string? TriggerCharacter = null,
    bool IsRetrigger = false, SignatureHelp? ActiveSignatureHelp = null);
public interface ISignatureHelpProvider
{
    IReadOnlyList<string> SignatureHelpTriggerCharacters => Array.Empty<string>();
    IReadOnlyList<string> SignatureHelpRetriggerCharacters => Array.Empty<string>();
    ValueTask<SignatureHelp?> ProvideSignatureHelpAsync(ILanguageTextModel model, LanguagePosition position, SignatureHelpContext context, CancellationToken cancellationToken);
}

public enum InlayHintKind { Type = 1, Parameter = 2 }
public sealed record InlayHint(LanguagePosition Position, string Label, InlayHintKind? Kind = null, string? Tooltip = null,
    bool PaddingLeft = false, bool PaddingRight = false);
public interface IInlayHintsProvider
{
    ValueTask<IReadOnlyList<InlayHint>?> ProvideInlayHintsAsync(ILanguageTextModel model, LanguageRange range, CancellationToken cancellationToken);
}

public sealed record LanguageTextEdit(LanguageRange Range, string Text);
public sealed record CodeActionContext(IReadOnlyList<string>? Diagnostics = null, string? Only = null, int Trigger = 1);
public sealed record CodeAction(string Title, string? Kind = null, IReadOnlyList<LanguageTextEdit>? Edits = null,
    bool IsPreferred = false, string? DisabledReason = null, Func<CancellationToken, ValueTask>? Command = null,
    WorkspaceEdit? WorkspaceEdit = null);
public interface ICodeActionProvider
{
    IReadOnlyList<string> ProvidedCodeActionKinds => Array.Empty<string>();
    ValueTask<IReadOnlyList<CodeAction>?> ProvideCodeActionsAsync(ILanguageTextModel model, LanguageRange range, CodeActionContext context, CancellationToken cancellationToken);
    ValueTask<CodeAction> ResolveCodeActionAsync(CodeAction action, CancellationToken cancellationToken) => ValueTask.FromResult(action);
}
