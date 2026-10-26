using System.Text.Json;
using System.Text.Json.Nodes;
using MonacoNative.Language;

namespace MonacoNative.Lsp;

/// <summary>
/// Projects one initialized LSP client into MonacoNative's provider registry. The existing native
/// contribution UI then consumes the server through the same provider contracts as local services.
/// </summary>
public sealed class LspLanguageAdapter :
    ICompletionItemProvider, IHoverProvider, ISignatureHelpProvider, IDefinitionProvider,
    IReferenceProvider, IRenameProvider, IDocumentFormattingEditProvider, IDocumentRangeFormattingEditProvider,
    IDocumentSemanticTokensProvider, IInlayHintsProvider, ICodeActionProvider, IDisposable
{
    private readonly LspClient _client;
    private readonly Func<CancellationToken, ValueTask>? _synchronize;
    private readonly MonacoLanguageService _languages;
    private readonly List<IDisposable> _registrations = new();
    private int _disposed;

    public LspLanguageAdapter(LspClient client, MonacoLanguageService languages, LanguageSelector selector,
        Func<CancellationToken, ValueTask>? synchronize = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _synchronize = synchronize;
        _languages = languages ?? throw new ArgumentNullException(nameof(languages));
        ArgumentNullException.ThrowIfNull(selector);
        if (client.State != LspClientState.Initialized)
            throw new InvalidOperationException("LSP client must be initialized before creating a language adapter.");

        var capabilities = client.ServerCapabilities;
        if (capabilities.Completion) _registrations.Add(languages.RegisterCompletionItemProvider(selector, this));
        if (capabilities.Hover) _registrations.Add(languages.RegisterHoverProvider(selector, this));
        if (capabilities.SignatureHelp) _registrations.Add(languages.RegisterSignatureHelpProvider(selector, this));
        if (capabilities.Definition) _registrations.Add(languages.RegisterDefinitionProvider(selector, this));
        if (capabilities.References) _registrations.Add(languages.RegisterReferenceProvider(selector, this));
        if (capabilities.Rename) _registrations.Add(languages.RegisterRenameProvider(selector, this));
        if (capabilities.Formatting) _registrations.Add(languages.RegisterDocumentFormattingEditProvider(selector, this));
        if (capabilities.RangeFormatting) _registrations.Add(languages.RegisterDocumentRangeFormattingEditProvider(selector, this));
        if (capabilities.SemanticTokens) _registrations.Add(languages.RegisterDocumentSemanticTokensProvider(selector, this));
        if (capabilities.InlayHints) _registrations.Add(languages.RegisterInlayHintsProvider(selector, this));
        if (capabilities.CodeActions) _registrations.Add(languages.RegisterCodeActionProvider(selector, this));
        _client.DiagnosticsPublished += Client_DiagnosticsPublished;
        foreach (var snapshot in _client.DiagnosticsSnapshot.Values)
            _languages.SetDiagnostics(snapshot.Uri, snapshot.Diagnostics, snapshot.Version);
    }

    public IReadOnlyList<string> TriggerCharacters => _client.ServerCapabilities.CompletionTriggerCharacters;
    public IReadOnlyList<string> SignatureHelpTriggerCharacters => _client.ServerCapabilities.SignatureTriggerCharacters;
    public IReadOnlyList<string> SignatureHelpRetriggerCharacters => _client.ServerCapabilities.SignatureRetriggerCharacters;
    public SemanticTokensLegend Legend => _client.ServerCapabilities.SemanticTokensLegend;

    private async ValueTask<JsonElement?> RequestSyncedAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        if (_synchronize is not null) await _synchronize(cancellationToken).ConfigureAwait(false);
        return await _client.RequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CompletionList?> ProvideCompletionItemsAsync(ILanguageTextModel model, LanguagePosition position, CompletionContext context, CancellationToken cancellationToken)
    {
        var result = await RequestSyncedAsync("textDocument/completion", new
        {
            textDocument = LspConversions.TextDocument(model.Uri),
            position = LspConversions.Position(position),
            context = new
            {
                triggerKind = context.TriggerKind switch
                {
                    CompletionTriggerKind.TriggerCharacter => 2,
                    CompletionTriggerKind.TriggerForIncompleteCompletions => 3,
                    _ => 1,
                },
                triggerCharacter = context.TriggerCharacter,
            },
        }, cancellationToken).ConfigureAwait(false);
        if (result is null || result.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        var root = result.Value;
        var isIncomplete = false;
        JsonElement items;
        if (root.ValueKind == JsonValueKind.Array) items = root;
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out items))
        {
            isIncomplete = root.TryGetProperty("isIncomplete", out var incomplete) && incomplete.ValueKind == JsonValueKind.True;
        }
        else return null;

        var defaults = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("itemDefaults", out var d) ? d : default;
        var output = new List<CompletionItem>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("label", out _)) continue;
            output.Add(ParseCompletionItem(NormalizeCompletion(item, defaults), allowResolve: true));
        }
        return new CompletionList(output, isIncomplete);
    }

    private static JsonElement NormalizeCompletion(JsonElement item, JsonElement defaults)
    {
        var normalized = JsonNode.Parse(item.GetRawText())!.AsObject();
        if (defaults.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "commitCharacters", "insertTextFormat", "insertTextMode", "data" })
                if (!normalized.ContainsKey(key) && defaults.TryGetProperty(key, out var value))
                    normalized[key] = JsonNode.Parse(value.GetRawText());
            if (!normalized.ContainsKey("textEdit") && defaults.TryGetProperty("editRange", out var range))
            {
                var text = normalized["textEditText"]?.GetValue<string>() ?? normalized["insertText"]?.GetValue<string>() ?? normalized["label"]!.GetValue<string>();
                var edit = new JsonObject { ["newText"] = text };
                if (range.TryGetProperty("start", out _)) edit["range"] = JsonNode.Parse(range.GetRawText());
                else
                {
                    if (range.TryGetProperty("insert", out var insert)) edit["insert"] = JsonNode.Parse(insert.GetRawText());
                    if (range.TryGetProperty("replace", out var replace)) edit["replace"] = JsonNode.Parse(replace.GetRawText());
                }
                normalized["textEdit"] = edit;
            }
        }
        return JsonSerializer.SerializeToElement(normalized);
    }

    private CompletionItem ParseCompletionItem(JsonElement item, bool allowResolve)
    {
        static string? String(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static bool True(JsonElement value, string key) => value.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
        var label = String(item, "label") ?? "";
        var kind = item.TryGetProperty("kind", out var k) && k.TryGetInt32(out var n) ? MapCompletionKind(n) : CompletionItemKind.Text;
        var text = String(item, "insertText") ?? label;
        LanguageRange? insertRange = null; LanguageRange? replaceRange = null;
        if (item.TryGetProperty("textEdit", out var edit) && edit.ValueKind == JsonValueKind.Object)
        {
            text = String(edit, "newText") ?? text;
            if (edit.TryGetProperty("range", out var range)) insertRange = replaceRange = LspConversions.ParseRange(range);
            else
            {
                if (edit.TryGetProperty("insert", out var insert)) insertRange = LspConversions.ParseRange(insert);
                if (edit.TryGetProperty("replace", out var replace)) replaceRange = LspConversions.ParseRange(replace);
            }
        }
        var snippet = item.TryGetProperty("insertTextFormat", out var format) && format.TryGetInt32(out var f) && f == 2 ? text : null;
        var expanded = snippet is null ? new LspSnippetText.Expanded(text, null) : LspSnippetText.ExpandWithCursor(snippet);
        var additional = item.TryGetProperty("additionalTextEdits", out var edits) ? LspConversions.ParseTextEdits(edits) : Array.Empty<LanguageTextEdit>();
        Func<CancellationToken, ValueTask>? command = null;
        string? commandId = null;
        if (item.TryGetProperty("command", out var cmd) && TryParseCommand(cmd, out var id, out var arguments))
        {
            commandId = id;
            command = ct => _client.ExecuteCommandAsync(id, arguments, ct);
        }
        var labels = item.TryGetProperty("labelDetails", out var details) ? details : default;
        var deprecated = True(item, "deprecated") || item.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array && tags.EnumerateArray().Any(t => t.TryGetInt32(out var tag) && tag == 1);
        var commit = item.TryGetProperty("commitCharacters", out var chars) && chars.ValueKind == JsonValueKind.Array
            ? chars.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : Array.Empty<string>();
        Func<CancellationToken, ValueTask<CompletionItem>>? resolver = null;
        if (allowResolve && _client.ServerCapabilities.CompletionResolve)
        {
            var original = item.Clone();
            resolver = async ct =>
            {
                var response = await _client.RequestAsync("completionItem/resolve", original, ct).ConfigureAwait(false);
                if (response is null || response.Value.ValueKind != JsonValueKind.Object) return ParseCompletionItem(original, false);
                var merged = JsonNode.Parse(original.GetRawText())!.AsObject();
                foreach (var property in response.Value.EnumerateObject()) merged[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                return ParseCompletionItem(JsonSerializer.SerializeToElement(merged), false);
            };
        }
        return new CompletionItem(label, kind, String(item, "detail"),
            item.TryGetProperty("documentation", out var doc) ? LspConversions.MarkdownText(doc) : null,
            expanded.Text, String(item, "sortText"), String(item, "filterText"), insertRange ?? replaceRange,
            additional, command, expanded.CaretOffset, snippet, insertRange, replaceRange,
            True(item, "preselect"), deprecated, commit, String(labels, "detail"), String(labels, "description"),
            item.TryGetProperty("insertTextMode", out var mode) && mode.TryGetInt32(out var m) ? m : 1, resolver, commandId);
    }

    public ValueTask<CompletionItem> ResolveCompletionItemAsync(CompletionItem item, CancellationToken cancellationToken) =>
        item.Resolve is null ? ValueTask.FromResult(item) : item.Resolve(cancellationToken);

    public async ValueTask<Hover?> ProvideHoverAsync(ILanguageTextModel model, LanguagePosition position, CancellationToken cancellationToken)
    {
        var result = await RequestSyncedAsync("textDocument/hover", LspConversions.TextDocumentPosition(model, position), cancellationToken).ConfigureAwait(false);
        if (result is null || result.Value.ValueKind != JsonValueKind.Object) return null;
        var root = result.Value;
        var contents = new List<MarkdownString>();
        if (root.TryGetProperty("contents", out var value))
        {
            if (value.ValueKind == JsonValueKind.Array)
                contents.AddRange(value.EnumerateArray().Select(x => new MarkdownString(LspConversions.MarkdownText(x))));
            else
                contents.Add(new MarkdownString(LspConversions.MarkdownText(value)));
        }
        var range = root.TryGetProperty("range", out var r) ? LspConversions.ParseRange(r) : (LanguageRange?)null;
        return contents.Count == 0 ? null : new Hover(contents, range);
    }

    public async ValueTask<SignatureHelp?> ProvideSignatureHelpAsync(ILanguageTextModel model, LanguagePosition position, SignatureHelpContext context, CancellationToken cancellationToken)
    {
        var result = await RequestSyncedAsync("textDocument/signatureHelp", new
        {
            textDocument = LspConversions.TextDocument(model.Uri),
            position = LspConversions.Position(position),
            context = new
            {
                triggerKind = context.TriggerKind switch { SignatureHelpTriggerKind.TriggerCharacter => 2, SignatureHelpTriggerKind.ContentChange => 3, _ => 1 },
                triggerCharacter = context.TriggerCharacter,
                isRetrigger = context.IsRetrigger,
            },
        }, cancellationToken).ConfigureAwait(false);
        if (result is null || result.Value.ValueKind != JsonValueKind.Object) return null;
        var root = result.Value;
        if (!root.TryGetProperty("signatures", out var signaturesElement) || signaturesElement.ValueKind != JsonValueKind.Array) return null;
        var signatures = new List<SignatureInformation>();
        foreach (var signature in signaturesElement.EnumerateArray())
        {
            if (!signature.TryGetProperty("label", out var label)) continue;
            var parameters = new List<ParameterInformation>();
            if (signature.TryGetProperty("parameters", out var parametersElement) && parametersElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var parameter in parametersElement.EnumerateArray())
                {
                    if (!parameter.TryGetProperty("label", out var parameterLabel)) continue;
                    var signatureLabel = label.GetString() ?? string.Empty;
                    var text = parameterLabel.ValueKind == JsonValueKind.String
                        ? parameterLabel.GetString() ?? string.Empty
                        : ParameterLabelFromOffsets(signatureLabel, parameterLabel);
                    var parameterDocumentation = parameter.TryGetProperty("documentation", out var pd) ? LspConversions.MarkdownText(pd) : null;
                    parameters.Add(new ParameterInformation(text, parameterDocumentation));
                }
            }
            signatures.Add(new SignatureInformation(
                label.GetString() ?? string.Empty,
                signature.TryGetProperty("documentation", out var signatureDocumentation) ? LspConversions.MarkdownText(signatureDocumentation) : null,
                parameters,
                signature.TryGetProperty("activeParameter", out var activeParameter) && activeParameter.TryGetInt32(out var ap) ? ap : null));
        }
        return signatures.Count == 0 ? null : new SignatureHelp(
            signatures,
            root.TryGetProperty("activeSignature", out var activeSignature) && activeSignature.TryGetInt32(out var active) ? active : 0,
            root.TryGetProperty("activeParameter", out var activeParameterRoot) && activeParameterRoot.TryGetInt32(out var parameterIndex) ? parameterIndex : 0);
    }

    public async ValueTask<IReadOnlyList<LanguageLocation>?> ProvideDefinitionAsync(ILanguageTextModel model, LanguagePosition position, CancellationToken cancellationToken) =>
        LspConversions.ParseLocations(await RequestSyncedAsync("textDocument/definition", LspConversions.TextDocumentPosition(model, position), cancellationToken).ConfigureAwait(false));

    public async ValueTask<IReadOnlyList<LanguageLocation>?> ProvideReferencesAsync(ILanguageTextModel model, LanguagePosition position, ReferenceContext context, CancellationToken cancellationToken) =>
        LspConversions.ParseLocations(await RequestSyncedAsync("textDocument/references", new
        {
            textDocument = LspConversions.TextDocument(model.Uri),
            position = LspConversions.Position(position),
            context = new { includeDeclaration = context.IncludeDeclaration },
        }, cancellationToken).ConfigureAwait(false));

    public async ValueTask<RenameLocation?> ResolveRenameLocationAsync(ILanguageTextModel model, LanguagePosition position, CancellationToken cancellationToken)
    {
        if (!_client.ServerCapabilities.PrepareRename) return null;
        var result = await RequestSyncedAsync("textDocument/prepareRename", LspConversions.TextDocumentPosition(model, position), cancellationToken).ConfigureAwait(false);
        if (result is null || result.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return new RenameLocation(new LanguageRange(position.LineNumber, position.Column, position.LineNumber, position.Column), string.Empty, "Rename is not valid at this position.");
        var root = result.Value;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("range", out var range))
            return new RenameLocation(LspConversions.ParseRange(range), root.TryGetProperty("placeholder", out var placeholder) ? placeholder.GetString() ?? string.Empty : string.Empty);
        if (root.TryGetProperty("start", out _) && root.TryGetProperty("end", out _))
            return new RenameLocation(LspConversions.ParseRange(root), string.Empty);
        return null;
    }

    public async ValueTask<WorkspaceEdit?> ProvideRenameEditsAsync(ILanguageTextModel model, LanguagePosition position, string newName, CancellationToken cancellationToken)
    {
        var result = await RequestSyncedAsync("textDocument/rename", new
        {
            textDocument = LspConversions.TextDocument(model.Uri),
            position = LspConversions.Position(position),
            newName,
        }, cancellationToken).ConfigureAwait(false);
        return LspConversions.ParseWorkspaceEdit(result);
    }

    public async ValueTask<IReadOnlyList<LanguageTextEdit>?> ProvideDocumentFormattingEditsAsync(ILanguageTextModel model, FormattingOptions options, CancellationToken cancellationToken)
    {
        var result = await RequestSyncedAsync("textDocument/formatting", new { textDocument = LspConversions.TextDocument(model.Uri), options = LspConversions.Formatting(options) }, cancellationToken).ConfigureAwait(false);
        return LspConversions.ParseTextEdits(result);
    }

    public async ValueTask<IReadOnlyList<LanguageTextEdit>?> ProvideDocumentRangeFormattingEditsAsync(ILanguageTextModel model, LanguageRange range, FormattingOptions options, CancellationToken cancellationToken)
    {
        var result = await RequestSyncedAsync("textDocument/rangeFormatting", new
        {
            textDocument = LspConversions.TextDocument(model.Uri),
            range = LspConversions.Range(range),
            options = LspConversions.Formatting(options),
        }, cancellationToken).ConfigureAwait(false);
        return LspConversions.ParseTextEdits(result);
    }

    public async ValueTask<SemanticTokens?> ProvideDocumentSemanticTokensAsync(ILanguageTextModel model, CancellationToken cancellationToken)
    {
        var result = await RequestSyncedAsync("textDocument/semanticTokens/full", new { textDocument = LspConversions.TextDocument(model.Uri) }, cancellationToken).ConfigureAwait(false);
        if (result is null || result.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        var root = result.Value;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
        var values = data.EnumerateArray().Select(x => x.GetInt32()).ToArray();
        var legend = Legend;
        var tokens = new List<SemanticToken>(values.Length / 5);
        var line = 0;
        var character = 0;
        for (var i = 0; i + 4 < values.Length; i += 5)
        {
            var deltaLine = values[i];
            var deltaStart = values[i + 1];
            line += deltaLine;
            character = deltaLine == 0 ? character + deltaStart : deltaStart;
            var length = values[i + 2];
            var typeIndex = values[i + 3];
            var modifierBits = values[i + 4];
            var tokenType = typeIndex >= 0 && typeIndex < legend.TokenTypes.Count ? legend.TokenTypes[typeIndex] : "variable";
            var modifiers = new List<string>();
            for (var bit = 0; bit < legend.TokenModifiers.Count && bit < 31; bit++)
                if ((modifierBits & (1 << bit)) != 0) modifiers.Add(legend.TokenModifiers[bit]);
            tokens.Add(new SemanticToken(line + 1, character + 1, Math.Max(0, length), tokenType, modifiers));
        }
        return new SemanticTokens(tokens, root.TryGetProperty("resultId", out var resultId) ? resultId.GetString() : null);
    }

    public async ValueTask<IReadOnlyList<InlayHint>?> ProvideInlayHintsAsync(ILanguageTextModel model, LanguageRange range, CancellationToken cancellationToken)
    {
        var result = await RequestSyncedAsync("textDocument/inlayHint", new { textDocument = LspConversions.TextDocument(model.Uri), range = LspConversions.Range(range) }, cancellationToken).ConfigureAwait(false);
        if (result is null || result.Value.ValueKind != JsonValueKind.Array) return Array.Empty<InlayHint>();
        var output = new List<InlayHint>();
        foreach (var item in result.Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("position", out var p) || !item.TryGetProperty("label", out var labelElement)) continue;
            string label;
            if (labelElement.ValueKind == JsonValueKind.String) label = labelElement.GetString() ?? string.Empty;
            else if (labelElement.ValueKind == JsonValueKind.Array)
                label = string.Concat(labelElement.EnumerateArray().Select(x => x.TryGetProperty("value", out var v) ? v.GetString() : string.Empty));
            else label = labelElement.GetRawText();
            var kind = item.TryGetProperty("kind", out var kindElement) && kindElement.TryGetInt32(out var k) && k is 1 or 2 ? (InlayHintKind?)k : null;
            var tooltip = item.TryGetProperty("tooltip", out var tooltipElement) ? LspConversions.MarkdownText(tooltipElement) : null;
            output.Add(new InlayHint(LspConversions.ParsePosition(p), label, kind, tooltip,
                item.TryGetProperty("paddingLeft", out var left) && left.ValueKind == JsonValueKind.True,
                item.TryGetProperty("paddingRight", out var right) && right.ValueKind == JsonValueKind.True));
        }
        return output;
    }

    public async ValueTask<IReadOnlyList<CodeAction>?> ProvideCodeActionsAsync(ILanguageTextModel model, LanguageRange range, CodeActionContext context, CancellationToken cancellationToken)
    {
        var only = string.IsNullOrWhiteSpace(context.Only) ? null : new[] { context.Only };
        var diagnostics = _client.DiagnosticsSnapshot.TryGetValue(model.Uri, out var snapshot)
            ? snapshot.Diagnostics.Where(diagnostic => RangesIntersect(range, diagnostic.Range)).Select(LspConversions.Diagnostic).ToArray()
            : Array.Empty<object>();
        var result = await RequestSyncedAsync("textDocument/codeAction", new
        {
            textDocument = LspConversions.TextDocument(model.Uri),
            range = LspConversions.Range(range),
            context = new { diagnostics, only, triggerKind = context.Trigger },
        }, cancellationToken).ConfigureAwait(false);
        if (result is null || result.Value.ValueKind != JsonValueKind.Array) return Array.Empty<CodeAction>();
        var output = new List<CodeAction>();
        foreach (var item in result.Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("title", out var titleElement)) continue;
            var title = titleElement.GetString() ?? string.Empty;
            var kind = item.TryGetProperty("kind", out var kindElement) ? kindElement.GetString() : null;
            var preferred = item.TryGetProperty("isPreferred", out var preferredElement) && preferredElement.ValueKind == JsonValueKind.True;
            var disabledReason = item.TryGetProperty("disabled", out var disabled) && disabled.ValueKind == JsonValueKind.Object && disabled.TryGetProperty("reason", out var reason)
                ? reason.GetString() : null;
            var workspaceEdit = item.TryGetProperty("edit", out var editElement) ? LspConversions.ParseWorkspaceEdit(editElement) : null;
            Func<CancellationToken, ValueTask>? command = null;
            if (item.TryGetProperty("command", out var commandElement) && TryParseCommand(commandElement, out var commandId, out var commandArguments))
                command = ct => _client.ExecuteCommandAsync(commandId, commandArguments, ct);
            else if (item.TryGetProperty("command", out var bareCommand) && bareCommand.ValueKind == JsonValueKind.String)
            {
                var commandId2 = bareCommand.GetString()!;
                var arguments2 = item.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Array ? args.EnumerateArray().Select(x => x.Clone()).ToArray() : Array.Empty<JsonElement>();
                command = ct => _client.ExecuteCommandAsync(commandId2, arguments2, ct);
            }
            if (item.TryGetProperty("command", out _) || item.TryGetProperty("edit", out _) || item.TryGetProperty("kind", out _))
                output.Add(new CodeAction(title, kind, null, preferred, disabledReason, command, workspaceEdit));
            else if (TryParseCommand(item, out var commandOnlyId, out var commandOnlyArguments))
                output.Add(new CodeAction(title, Command: ct => _client.ExecuteCommandAsync(commandOnlyId, commandOnlyArguments, ct)));
        }
        return output;
    }

    public ValueTask<CodeAction> ResolveCodeActionAsync(CodeAction action, CancellationToken cancellationToken) => ValueTask.FromResult(action);

    private void Client_DiagnosticsPublished(object? sender, LspDiagnosticsEventArgs e) => _languages.SetDiagnostics(e.Uri, e.Diagnostics, e.Version);

    private static bool TryParseCommand(JsonElement element, out string commandId, out IReadOnlyList<JsonElement> arguments)
    {
        commandId = string.Empty;
        arguments = Array.Empty<JsonElement>();
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.String) return false;
        commandId = command.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(commandId)) return false;
        if (element.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Array)
            arguments = args.EnumerateArray().Select(x => x.Clone()).ToArray();
        return true;
    }

    private static bool RangesIntersect(LanguageRange left, LanguageRange right)
    {
        var a = left.Normalize();
        var b = right.Normalize();
        static int Compare(int lineA, int columnA, int lineB, int columnB) =>
            lineA != lineB ? lineA.CompareTo(lineB) : columnA.CompareTo(columnB);
        var aEmpty = Compare(a.StartLineNumber, a.StartColumn, a.EndLineNumber, a.EndColumn) == 0;
        if (aEmpty)
            return Compare(b.StartLineNumber, b.StartColumn, a.StartLineNumber, a.StartColumn) <= 0 &&
                   Compare(a.StartLineNumber, a.StartColumn, b.EndLineNumber, b.EndColumn) <= 0;
        return Compare(a.StartLineNumber, a.StartColumn, b.EndLineNumber, b.EndColumn) < 0 &&
               Compare(b.StartLineNumber, b.StartColumn, a.EndLineNumber, a.EndColumn) < 0;
    }

    private static string ParameterLabelFromOffsets(string signatureLabel, JsonElement label)
    {
        if (label.ValueKind != JsonValueKind.Array) return label.GetRawText();
        var offsets = label.EnumerateArray().ToArray();
        if (offsets.Length != 2 || !offsets[0].TryGetInt32(out var start) || !offsets[1].TryGetInt32(out var end))
            return label.GetRawText();
        start = Math.Clamp(start, 0, signatureLabel.Length);
        end = Math.Clamp(end, start, signatureLabel.Length);
        return signatureLabel.Substring(start, end - start);
    }

    private static CompletionItemKind MapCompletionKind(int kind) => kind switch
    {
        1 => CompletionItemKind.Text,
        2 => CompletionItemKind.Method,
        3 => CompletionItemKind.Function,
        4 => CompletionItemKind.Constructor,
        5 => CompletionItemKind.Field,
        6 => CompletionItemKind.Variable,
        7 => CompletionItemKind.Class,
        8 => CompletionItemKind.Interface,
        9 => CompletionItemKind.Module,
        10 => CompletionItemKind.Property,
        11 => CompletionItemKind.Unit,
        12 => CompletionItemKind.Value,
        13 => CompletionItemKind.Enum,
        14 => CompletionItemKind.Keyword,
        15 => CompletionItemKind.Snippet,
        16 => CompletionItemKind.Color,
        17 => CompletionItemKind.File,
        18 => CompletionItemKind.Reference,
        19 => CompletionItemKind.Folder,
        20 => CompletionItemKind.EnumMember,
        21 => CompletionItemKind.Constant,
        22 => CompletionItemKind.Struct,
        23 => CompletionItemKind.Event,
        24 => CompletionItemKind.Operator,
        25 => CompletionItemKind.TypeParameter,
        _ => CompletionItemKind.Text,
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _client.DiagnosticsPublished -= Client_DiagnosticsPublished;
        foreach (var registration in _registrations) registration.Dispose();
        _registrations.Clear();
    }
}
