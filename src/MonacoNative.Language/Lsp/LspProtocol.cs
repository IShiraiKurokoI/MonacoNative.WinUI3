using System.Text.Json;
using MonacoNative.Language;

namespace MonacoNative.Lsp;

public enum LspTextDocumentSyncKind { None = 0, Full = 1, Incremental = 2 }
public enum LspClientState { Created, Starting, Initialized, ShuttingDown, Stopped, Faulted, Disposed }

public sealed record LspWorkspaceFolder(Uri Uri, string Name);

public sealed class LspInitializeOptions
{
    public Uri? RootUri { get; init; }
    public IReadOnlyList<LspWorkspaceFolder> WorkspaceFolders { get; init; } = Array.Empty<LspWorkspaceFolder>();
    public string? Locale { get; init; }
    public object? InitializationOptions { get; init; }
    public string ClientName { get; init; } = "MonacoNative";
    public string ClientVersion { get; init; } = "0.9.0";
    public int? ProcessId { get; init; } = Environment.ProcessId;
}

public sealed class LspServerCapabilities
{
    private static readonly string[] DefaultSemanticTokenTypes =
    {
        "namespace", "type", "class", "enum", "interface", "struct", "typeParameter", "parameter",
        "variable", "property", "enumMember", "event", "function", "method", "macro", "label",
        "comment", "string", "keyword", "number", "regexp", "operator", "decorator"
    };

    public LspTextDocumentSyncKind TextDocumentSync { get; internal set; }
    public bool TextDocumentOpenClose { get; internal set; }
    public bool Completion { get; internal set; }
    public bool CompletionResolve { get; internal set; }
    public bool Hover { get; internal set; }
    public bool SignatureHelp { get; internal set; }
    public bool Definition { get; internal set; }
    public bool References { get; internal set; }
    public bool Rename { get; internal set; }
    public bool PrepareRename { get; internal set; }
    public bool Formatting { get; internal set; }
    public bool RangeFormatting { get; internal set; }
    public bool SemanticTokens { get; internal set; }
    public bool InlayHints { get; internal set; }
    public bool CodeActions { get; internal set; }
    public IReadOnlyList<string> CompletionTriggerCharacters { get; internal set; } = Array.Empty<string>();
    public IReadOnlyList<string> SignatureTriggerCharacters { get; internal set; } = Array.Empty<string>();
    public IReadOnlyList<string> SignatureRetriggerCharacters { get; internal set; } = Array.Empty<string>();
    public SemanticTokensLegend SemanticTokensLegend { get; internal set; } = new(DefaultSemanticTokenTypes, Array.Empty<string>());
    public string? ServerName { get; internal set; }
    public string? ServerVersion { get; internal set; }
    public JsonElement Raw { get; internal set; }

    internal static LspServerCapabilities Parse(JsonElement initializeResult)
    {
        var capabilities = initializeResult.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Object
            ? caps : default;
        var result = new LspServerCapabilities
        {
            Raw = capabilities.ValueKind == JsonValueKind.Undefined ? default : capabilities.Clone(),
            TextDocumentSync = ParseTextSync(capabilities),
            TextDocumentOpenClose = ParseOpenClose(capabilities),
            Completion = Enabled(capabilities, "completionProvider"),
            Hover = Enabled(capabilities, "hoverProvider"),
            SignatureHelp = Enabled(capabilities, "signatureHelpProvider"),
            Definition = Enabled(capabilities, "definitionProvider"),
            References = Enabled(capabilities, "referencesProvider"),
            Rename = Enabled(capabilities, "renameProvider"),
            Formatting = Enabled(capabilities, "documentFormattingProvider"),
            RangeFormatting = Enabled(capabilities, "documentRangeFormattingProvider"),
            SemanticTokens = SupportsSemanticTokensFull(capabilities),
            InlayHints = Enabled(capabilities, "inlayHintProvider"),
            CodeActions = Enabled(capabilities, "codeActionProvider"),
        };
        if (capabilities.ValueKind == JsonValueKind.Object)
        {
            if (capabilities.TryGetProperty("completionProvider", out var completion) && completion.ValueKind == JsonValueKind.Object)
                {
                result.CompletionTriggerCharacters = ReadStringArray(completion, "triggerCharacters");
                result.CompletionResolve = completion.TryGetProperty("resolveProvider", out var resolve) && resolve.ValueKind == JsonValueKind.True;
            }
            if (capabilities.TryGetProperty("signatureHelpProvider", out var signature) && signature.ValueKind == JsonValueKind.Object)
            {
                result.SignatureTriggerCharacters = ReadStringArray(signature, "triggerCharacters");
                result.SignatureRetriggerCharacters = ReadStringArray(signature, "retriggerCharacters");
            }
            if (capabilities.TryGetProperty("renameProvider", out var rename) && rename.ValueKind == JsonValueKind.Object &&
                rename.TryGetProperty("prepareProvider", out var prepare) && prepare.ValueKind == JsonValueKind.True)
                result.PrepareRename = true;
            if (capabilities.TryGetProperty("semanticTokensProvider", out var semantic) && semantic.ValueKind == JsonValueKind.Object &&
                semantic.TryGetProperty("legend", out var legend) && legend.ValueKind == JsonValueKind.Object)
            {
                result.SemanticTokensLegend = new SemanticTokensLegend(
                    ReadStringArray(legend, "tokenTypes"),
                    ReadStringArray(legend, "tokenModifiers"));
            }
        }
        if (initializeResult.TryGetProperty("serverInfo", out var info) && info.ValueKind == JsonValueKind.Object)
        {
            result.ServerName = info.TryGetProperty("name", out var n) ? n.GetString() : null;
            result.ServerVersion = info.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        return result;
    }

    private static LspTextDocumentSyncKind ParseTextSync(JsonElement caps)
    {
        if (caps.ValueKind != JsonValueKind.Object || !caps.TryGetProperty("textDocumentSync", out var sync))
            return LspTextDocumentSyncKind.None;
        if (sync.ValueKind == JsonValueKind.Number && sync.TryGetInt32(out var number) && number is >= 0 and <= 2)
            return (LspTextDocumentSyncKind)number;
        if (sync.ValueKind == JsonValueKind.Object && sync.TryGetProperty("change", out var change) && change.TryGetInt32(out number) && number is >= 0 and <= 2)
            return (LspTextDocumentSyncKind)number;
        return LspTextDocumentSyncKind.None;
    }

    private static bool ParseOpenClose(JsonElement caps)
    {
        if (caps.ValueKind != JsonValueKind.Object || !caps.TryGetProperty("textDocumentSync", out var sync)) return false;
        if (sync.ValueKind == JsonValueKind.Number)
            return sync.TryGetInt32(out var kind) && kind != (int)LspTextDocumentSyncKind.None;
        return sync.ValueKind == JsonValueKind.Object && sync.TryGetProperty("openClose", out var openClose) && openClose.ValueKind == JsonValueKind.True;
    }

    private static bool SupportsSemanticTokensFull(JsonElement caps)
    {
        if (caps.ValueKind != JsonValueKind.Object || !caps.TryGetProperty("semanticTokensProvider", out var semantic) || semantic.ValueKind != JsonValueKind.Object)
            return false;
        if (!semantic.TryGetProperty("full", out var full)) return false;
        return full.ValueKind is JsonValueKind.True or JsonValueKind.Object;
    }

    private static bool Enabled(JsonElement caps, string property)
    {
        if (caps.ValueKind != JsonValueKind.Object || !caps.TryGetProperty(property, out var value)) return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Object => true,
            _ => false,
        };
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray();
    }
}

public sealed class LspDiagnosticsEventArgs : EventArgs
{
    public LspDiagnosticsEventArgs(Uri uri, IReadOnlyList<LanguageDiagnostic> diagnostics, int? version)
    { Uri = uri; Diagnostics = diagnostics; Version = version; }
    public Uri Uri { get; }
    public IReadOnlyList<LanguageDiagnostic> Diagnostics { get; }
    public int? Version { get; }
}

public sealed class LspLogMessageEventArgs : EventArgs
{
    public LspLogMessageEventArgs(int type, string message) { Type = type; Message = message; }
    public int Type { get; }
    public string Message { get; }
}

public sealed class LspNotificationEventArgs : EventArgs
{
    public LspNotificationEventArgs(string method, JsonElement? parameters) { Method = method; Parameters = parameters; }
    public string Method { get; }
    public JsonElement? Parameters { get; }
}

internal static class LspConversions
{
    public static object Position(LanguagePosition position) => new { line = Math.Max(0, position.LineNumber - 1), character = Math.Max(0, position.Column - 1) };
    public static object Range(LanguageRange range)
    {
        var n = range.Normalize();
        return new { start = Position(new LanguagePosition(n.StartLineNumber, n.StartColumn)), end = Position(new LanguagePosition(n.EndLineNumber, n.EndColumn)) };
    }
    public static object TextDocument(Uri uri) => new { uri = uri.AbsoluteUri };
    public static object TextDocumentPosition(ILanguageTextModel model, LanguagePosition position) => new { textDocument = TextDocument(model.Uri), position = Position(position) };

    public static LanguagePosition ParsePosition(JsonElement element) => new(
        element.TryGetProperty("line", out var line) && line.TryGetInt32(out var l) ? l + 1 : 1,
        element.TryGetProperty("character", out var character) && character.TryGetInt32(out var c) ? c + 1 : 1);

    public static LanguageRange ParseRange(JsonElement element)
    {
        var start = element.TryGetProperty("start", out var s) ? ParsePosition(s) : new LanguagePosition(1, 1);
        var end = element.TryGetProperty("end", out var e) ? ParsePosition(e) : start;
        return new LanguageRange(start.LineNumber, start.Column, end.LineNumber, end.Column);
    }

    public static IReadOnlyList<LanguageLocation> ParseLocations(JsonElement? value)
    {
        if (value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return Array.Empty<LanguageLocation>();
        var element = value.Value;
        if (element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray().Select(ParseLocationLike).Where(x => x is not null).Cast<LanguageLocation>().ToArray();
        var single = ParseLocationLike(element);
        return single is null ? Array.Empty<LanguageLocation>() : new[] { single! };
    }

    private static LanguageLocation? ParseLocationLike(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        string? uri = null;
        JsonElement range = default;
        if (element.TryGetProperty("uri", out var uriElement))
        {
            uri = uriElement.GetString();
            if (element.TryGetProperty("range", out var r)) range = r;
        }
        else if (element.TryGetProperty("targetUri", out var targetUri))
        {
            uri = targetUri.GetString();
            if (element.TryGetProperty("targetSelectionRange", out var selectionRange)) range = selectionRange;
            else if (element.TryGetProperty("targetRange", out var targetRange)) range = targetRange;
        }
        return Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && range.ValueKind == JsonValueKind.Object
            ? new LanguageLocation(parsed, ParseRange(range)) : null;
    }

    public static IReadOnlyList<LanguageTextEdit> ParseTextEdits(JsonElement? value)
    {
        if (value is null || value.Value.ValueKind != JsonValueKind.Array) return Array.Empty<LanguageTextEdit>();
        var edits = new List<LanguageTextEdit>();
        foreach (var item in value.Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("range", out var range)) continue;
            var text = item.TryGetProperty("newText", out var newText) ? newText.GetString() ?? string.Empty : string.Empty;
            edits.Add(new LanguageTextEdit(ParseRange(range), text));
        }
        return edits;
    }

    public static WorkspaceEdit? ParseWorkspaceEdit(JsonElement? value)
    {
        if (value is null || value.Value.ValueKind != JsonValueKind.Object) return null;
        var root = value.Value;
        var edits = new List<WorkspaceTextEdit>();
        if (root.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in changes.EnumerateObject())
            {
                if (!Uri.TryCreate(property.Name, UriKind.Absolute, out var uri) || property.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var edit in ParseTextEdits(property.Value)) edits.Add(new WorkspaceTextEdit(uri, edit.Range, edit.Text));
            }
        }
        if (root.TryGetProperty("documentChanges", out var documentChanges) && documentChanges.ValueKind == JsonValueKind.Array)
        {
            foreach (var change in documentChanges.EnumerateArray())
            {
                if (change.ValueKind != JsonValueKind.Object || !change.TryGetProperty("textDocument", out var textDocument) ||
                    !textDocument.TryGetProperty("uri", out var uriElement) || !Uri.TryCreate(uriElement.GetString(), UriKind.Absolute, out var uri) ||
                    !change.TryGetProperty("edits", out var textEdits) || textEdits.ValueKind != JsonValueKind.Array) continue;
                foreach (var edit in ParseTextEdits(textEdits)) edits.Add(new WorkspaceTextEdit(uri, edit.Range, edit.Text));
            }
        }
        return new WorkspaceEdit(edits);
    }

    public static IReadOnlyList<LanguageDiagnostic> ParseDiagnostics(JsonElement? value)
    {
        if (value is null || value.Value.ValueKind != JsonValueKind.Array) return Array.Empty<LanguageDiagnostic>();
        var output = new List<LanguageDiagnostic>();
        foreach (var item in value.Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("range", out var range)) continue;
            var message = item.TryGetProperty("message", out var m) ? m.GetString() ?? string.Empty : string.Empty;
            var severity = item.TryGetProperty("severity", out var severityElement) && severityElement.TryGetInt32(out var sv) && sv is >= 1 and <= 4
                ? (DiagnosticSeverity)sv : DiagnosticSeverity.Error;
            string? code = null;
            if (item.TryGetProperty("code", out var codeElement))
                code = codeElement.ValueKind == JsonValueKind.String ? codeElement.GetString() : codeElement.GetRawText();
            var source = item.TryGetProperty("source", out var sourceElement) ? sourceElement.GetString() : null;
            var tags = item.TryGetProperty("tags", out var tagElement) && tagElement.ValueKind == JsonValueKind.Array
                ? tagElement.EnumerateArray().Select(x => x.GetRawText()).ToArray() : Array.Empty<string>();
            var related = new List<LanguageDiagnosticRelatedInformation>();
            if (item.TryGetProperty("relatedInformation", out var relatedElement) && relatedElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var info in relatedElement.EnumerateArray())
                {
                    if (!info.TryGetProperty("location", out var locationElement) || !info.TryGetProperty("message", out var relatedMessage)) continue;
                    var location = ParseLocationLike(locationElement);
                    if (location is not null) related.Add(new LanguageDiagnosticRelatedInformation(location, relatedMessage.GetString() ?? string.Empty));
                }
            }
            object? data = item.TryGetProperty("data", out var dataElement) ? dataElement.Clone() : null;
            output.Add(new LanguageDiagnostic(ParseRange(range), message, severity, code, source, related, tags, data));
        }
        return output;
    }

    public static object Diagnostic(LanguageDiagnostic diagnostic)
    {
        var value = new Dictionary<string, object?>
        {
            ["range"] = Range(diagnostic.Range),
            ["severity"] = (int)diagnostic.Severity,
            ["message"] = diagnostic.Message,
        };
        if (!string.IsNullOrWhiteSpace(diagnostic.Code)) value["code"] = diagnostic.Code;
        if (!string.IsNullOrWhiteSpace(diagnostic.Source)) value["source"] = diagnostic.Source;
        if (diagnostic.Tags is { Count: > 0 })
            value["tags"] = diagnostic.Tags.Select(x => int.TryParse(x, out var tag) ? tag : 0).Where(x => x > 0).ToArray();
        if (diagnostic.RelatedInformation is { Count: > 0 })
            value["relatedInformation"] = diagnostic.RelatedInformation.Select(x => new
            {
                location = new { uri = x.Location.Uri.AbsoluteUri, range = Range(x.Location.Range) },
                message = x.Message,
            }).ToArray();
        if (diagnostic.Data is not null) value["data"] = diagnostic.Data;
        return value;
    }

    public static string MarkdownText(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("language", out var language) && language.ValueKind == JsonValueKind.String &&
                value.TryGetProperty("value", out var codeValue) && codeValue.ValueKind == JsonValueKind.String)
                return $"```{language.GetString()}\n{codeValue.GetString()}\n```";
            if (value.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String) return v.GetString() ?? string.Empty;
        }
        return value.GetRawText();
    }

    public static object Formatting(FormattingOptions options)
    {
        var values = new Dictionary<string, object?>
        {
            ["tabSize"] = options.TabSize,
            ["insertSpaces"] = options.InsertSpaces,
            ["trimTrailingWhitespace"] = options.TrimTrailingWhitespace,
            ["insertFinalNewline"] = options.InsertFinalNewline,
            ["trimFinalNewlines"] = options.TrimFinalNewlines,
        };
        if (options.Extra is not null)
            foreach (var pair in options.Extra) values[pair.Key] = pair.Value;
        return values;
    }
}
