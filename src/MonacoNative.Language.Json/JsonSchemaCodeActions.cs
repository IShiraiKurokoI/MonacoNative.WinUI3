using System.Text.Json;
using MonacoNative.Language;

namespace MonacoNative.Language.Json;

public sealed partial class JsonNativeLanguageService
{
    public IReadOnlyList<string> ProvidedCodeActionKinds { get; } = new[] { "quickfix" };

    public ValueTask<IReadOnlyList<CodeAction>?> ProvideCodeActionsAsync(ILanguageTextModel model,
        LanguageRange range, CodeActionContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (context.Only is not null && !"quickfix".StartsWith(context.Only, StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult<IReadOnlyList<CodeAction>?>(Array.Empty<CodeAction>());
        var snapshot = new LanguageTextSnapshot(model.GetValue());
        var parsed = Parse(model, snapshot.Text, ct);
        if (parsed.Root is null) return ValueTask.FromResult<IReadOnlyList<CodeAction>?>(Array.Empty<CodeAction>());
        var offset = snapshot.OffsetAt(new(range.StartLineNumber, range.StartColumn));
        var selected = JsoncParser.FindNodeAtOffset(parsed.Root, offset, true) ?? parsed.Root;
        var roots = _schemas.ForDocument(model.Uri, parsed.Root);
        var actions = new List<CodeAction>();
        for (var current = selected; current is not null; current = current.Parent)
        {
            ct.ThrowIfCancellationRequested();
            if (current.Type != JsoncNodeType.Object) continue;
            var path = JsoncParser.GetNodePath(current).ToArray();
            var available = current.Children.Where(p => p.Children.Count > 0)
                .Select(p => p.Children[0].Value as string).ToHashSet(StringComparer.Ordinal);
            foreach (var source in Applicable(roots, path, parsed.Root, snapshot, ct))
            {
                var schema = source.Schema;
                if (schema.ValueKind != JsonValueKind.Object) continue;
                var missing = new HashSet<string>(StringComparer.Ordinal);
                if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
                    foreach (var nameValue in required.EnumerateArray())
                        if (nameValue.ValueKind == JsonValueKind.String && nameValue.GetString() is { } name && !available.Contains(name))
                            missing.Add(name);
                if (schema.TryGetProperty("dependentRequired", out var dependencies) && dependencies.ValueKind == JsonValueKind.Object)
                    foreach (var property in dependencies.EnumerateObject())
                        if (available.Contains(property.Name) && property.Value.ValueKind == JsonValueKind.Array)
                            foreach (var nameValue in property.Value.EnumerateArray())
                                if (nameValue.ValueKind == JsonValueKind.String && nameValue.GetString() is { } name && !available.Contains(name))
                                    missing.Add(name);
                foreach (var name in missing)
                {
                    var value = MissingValue(schema, name);
                    var edit = JsoncEdit.Modify(snapshot.Text, path.Concat(new object[] { name }).ToArray(), value,
                        new JsoncModificationOptions(new JsoncFormattingOptions(TabSize: 2)), ct);
                    actions.Add(new CodeAction("Add required property '" + name + "'", "quickfix",
                        edit.Select(e => new LanguageTextEdit(snapshot.Range(e.Offset, e.Offset + e.Length), e.Content)).ToArray(),
                        IsPreferred: true));
                }
            }
        }
        // Enum and const values may be replaced without affecting surrounding JSONC comments.
        if (selected.Type is not (JsoncNodeType.Object or JsoncNodeType.Array or JsoncNodeType.Property))
        {
            var path = JsoncParser.GetNodePath(selected).ToArray();
            foreach (var contextSchema in Applicable(roots, path, parsed.Root, snapshot, ct))
            {
                ct.ThrowIfCancellationRequested();
                var schema = contextSchema.Schema;
                if (schema.ValueKind != JsonValueKind.Object) continue;
                var choices = new List<JsonElement>();
                if (schema.TryGetProperty("const", out var constant)) choices.Add(constant);
                if (schema.TryGetProperty("enum", out var allowed) && allowed.ValueKind == JsonValueKind.Array)
                    choices.AddRange(allowed.EnumerateArray());
                foreach (var choice in choices.Take(20))
                {
                    if (ValueEquals(selected, choice)) continue;
                    actions.Add(new CodeAction("Replace with " + choice.GetRawText(), "quickfix",
                        new[] { new LanguageTextEdit(snapshot.Range(selected.Offset, selected.End), choice.GetRawText()) }));
                }
            }
        }
        return ValueTask.FromResult<IReadOnlyList<CodeAction>?>(actions
            .GroupBy(a => a.Title, StringComparer.Ordinal).Select(g => g.First()).Take(50).ToArray());
    }

    private static object? MissingValue(JsonElement schema, string name)
    {
        if (!schema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object || !properties.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in new[] { "default", "const" })
            if (property.TryGetProperty(key, out var value)) return value.Clone();
        if (property.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array && values.GetArrayLength() > 0)
            return values[0].Clone();
        return JsonSchemaRegistry.String(property, "type") switch
        {
            "string" => "", "integer" or "number" => 0, "boolean" => false,
            "object" => new Dictionary<string, object?>(), "array" => Array.Empty<object>(), _ => null
        };
    }
}
