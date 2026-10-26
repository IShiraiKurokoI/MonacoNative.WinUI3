using System.Globalization;
using System.Text;
using System.Text.Json;
using MonacoNative.Language;

namespace MonacoNative.Language.Json;

/// <summary>AST-driven native providers. JSON Schema coverage is a documented subset, not a full upstream port.</summary>
public sealed class JsonNativeLanguageService : ICompletionItemProvider, IHoverProvider, IDocumentDiagnosticsProvider
{
    private readonly JsonSchemaRegistry _schemas;
    public JsonNativeLanguageService(JsonSchemaRegistry schemas) => _schemas = schemas ?? throw new ArgumentNullException(nameof(schemas));
    public IReadOnlyList<string> TriggerCharacters { get; } = new[] { "\"", ":", " " };
    private static JsoncParseResult Parse(ILanguageTextModel model, string text, CancellationToken ct) => JsoncParser.ParseTree(text,
        new(DisallowComments: model.LanguageId == "json", AllowTrailingComma: model.LanguageId == "jsonc", AllowEmptyContent: true), ct);

    public ValueTask<CompletionList?> ProvideCompletionItemsAsync(ILanguageTextModel model, LanguagePosition position, CompletionContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var snapshot = new LanguageTextSnapshot(model.GetValue()); var offset = snapshot.OffsetAt(position);
        var parsed = Parse(model, snapshot.Text, ct);
        var location = JsoncParser.GetLocation(snapshot.Text, offset, ct);
        var previous = location.PreviousNode;
        var start = previous is not null && offset >= previous.Offset && offset <= previous.End ? previous.Offset : offset;
        var end = start != offset || previous?.Offset == offset ? previous!.End : offset;
        var scanner = new JsoncScanner(snapshot.Text, cancellationToken: ct);
        while (scanner.Scan() != JsoncSyntaxKind.EOF && scanner.TokenOffset < offset)
        {
            if ((scanner.Token is JsoncSyntaxKind.LineCommentTrivia or JsoncSyntaxKind.BlockCommentTrivia) &&
                (offset < scanner.Position || (offset == scanner.Position && (scanner.Token == JsoncSyntaxKind.LineCommentTrivia || scanner.TokenError == JsoncScanError.UnexpectedEndOfComment))))
                return ValueTask.FromResult<CompletionList?>(new(Array.Empty<CompletionItem>()));
            if (offset <= scanner.Position && (scanner.Token is JsoncSyntaxKind.Unknown or JsoncSyntaxKind.StringLiteral or JsoncSyntaxKind.NumericLiteral or JsoncSyntaxKind.TrueKeyword or JsoncSyntaxKind.FalseKeyword or JsoncSyntaxKind.NullKeyword))
            { start = scanner.TokenOffset; end = scanner.Position; break; }
        }
        var range = snapshot.Range(start, end);
        var roots = _schemas.ForDocument(model.Uri, parsed.Root);
        var output = new Dictionary<string, CompletionItem>(StringComparer.Ordinal);
        var path = location.Path.ToArray();
        if (location.IsAtPropertyKey)
        {
            var parentPath = path.Length > 0 ? path[..^1] : path;
            var parent = JsoncParser.FindNodeAtLocation(parsed.Root, parentPath);
            var used = parent?.Type == JsoncNodeType.Object ? parent.Children.Where(p => p.Children.Count > 0).Select(p => p.Children[0].Value as string).ToHashSet(StringComparer.Ordinal) : new HashSet<string?>(StringComparer.Ordinal);
            var currentProperty = previous?.Value as string;
            var alreadyHasValue = parent?.Children.FirstOrDefault(p => p.Offset == start)?.ColonOffset is not null;
            foreach (var schema in _schemas.AtPath(roots, parentPath))
            {
                ct.ThrowIfCancellationRequested();
                if (schema.Schema.ValueKind != JsonValueKind.Object || !schema.Schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object) continue;
                foreach (var property in properties.EnumerateObject())
                {
                    if (used.Contains(property.Name) && property.Name != currentProperty || property.Value.ValueKind == JsonValueKind.False) continue;
                    var resolved = _schemas.Expand(schema with { Schema = property.Value }).ToArray();
                    var description = resolved.Select(c => Description(c.Schema)).FirstOrDefault(d => d is not null);
                    var value = resolved.Select(c => DefaultValue(c.Schema)).FirstOrDefault(v => v is not null) ?? "null";
                    var quoted = JsonSerializer.Serialize(property.Name); var comma = CommaSuffix(snapshot.Text, end);
                    output[property.Name] = new(property.Name, CompletionItemKind.Property, Documentation: description,
                        InsertText: alreadyHasValue ? quoted : quoted + ": " + value + comma, FilterText: quoted, Range: range,
                        SnippetText: alreadyHasValue ? null : Escape(quoted) + ": ${1:" + Escape(value) + "}" + comma,
                        Deprecated: resolved.Any(c => c.Schema.ValueKind == JsonValueKind.Object && c.Schema.TryGetProperty("deprecated", out var d) && d.ValueKind == JsonValueKind.True));
                }
            }
            if (roots.Length == 0 && parent?.Parent is { Type: JsoncNodeType.Array } siblings)
                foreach (var sibling in siblings.Children.Where(n => n.Type == JsoncNodeType.Object))
                    foreach (var property in sibling.Children)
                    {
                        if (property.Children.Count == 0 || property.Children[0].Value is not string name || used.Contains(name)) continue;
                        var quoted = JsonSerializer.Serialize(name);
                        output.TryAdd(name, new(name, CompletionItemKind.Property, InsertText: quoted + ": null", FilterText: quoted, Range: range, SnippetText: Escape(quoted) + ": ${1:null}"));
                    }
        }
        else
        {
            foreach (var contextSchema in _schemas.AtPath(roots, path))
            {
                ct.ThrowIfCancellationRequested(); var schema = contextSchema.Schema;
                if (schema.ValueKind != JsonValueKind.Object) continue;
                foreach (var key in new[] { "enum", "examples" })
                    if (schema.TryGetProperty(key, out var choices) && choices.ValueKind == JsonValueKind.Array)
                        foreach (var choice in choices.EnumerateArray()) AddValue(choice.GetRawText(), Description(schema));
                foreach (var key in new[] { "const", "default" }) if (schema.TryGetProperty(key, out var choice)) AddValue(choice.GetRawText(), Description(schema));
                switch (JsonSchemaRegistry.String(schema, "type"))
                {
                    case "boolean": AddValue("true"); AddValue("false"); break;
                    case "null": AddValue("null"); break;
                    case "object": AddValue("{}"); break;
                    case "array": AddValue("[]"); break;
                }
            }
            if (roots.Length == 0 && previous?.Type != JsoncNodeType.String) { AddValue("true"); AddValue("false"); AddValue("null"); }
            void AddValue(string value, string? description = null) => output.TryAdd(value, new(value, CompletionItemKind.Value, Documentation: description, InsertText: value, Range: range));
        }
        return ValueTask.FromResult<CompletionList?>(new(output.Values.ToArray()));
    }
    private static string? Description(JsonElement schema) => JsonSchemaRegistry.String(schema, "markdownDescription") ?? JsonSchemaRegistry.String(schema, "description");
    private static string CommaSuffix(string text, int offset)
    { var scanner = new JsoncScanner(text, ignoreTrivia: true); scanner.SetPosition(offset); return scanner.Scan() == JsoncSyntaxKind.StringLiteral ? "," : string.Empty; }
    private static string Escape(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("$", "\\$", StringComparison.Ordinal).Replace("}", "\\}", StringComparison.Ordinal);
    private static string? DefaultValue(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in new[] { "default", "const" }) if (schema.TryGetProperty(key, out var value)) return value.GetRawText();
        if (schema.TryGetProperty("enum", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0) return choices[0].GetRawText();
        return JsonSchemaRegistry.String(schema, "type") switch { "string" => "\"\"", "number" or "integer" => "0", "boolean" => "true", "object" => "{}", "array" => "[]", "null" => "null", _ => null };
    }
    public ValueTask<Hover?> ProvideHoverAsync(ILanguageTextModel model, LanguagePosition position, CancellationToken ct)
    {
        var snapshot = new LanguageTextSnapshot(model.GetValue()); var parsed = Parse(model, snapshot.Text, ct);
        var node = JsoncParser.FindNodeAtOffset(parsed.Root, snapshot.OffsetAt(position), true);
        if (node is null) return ValueTask.FromResult<Hover?>(null);
        var contexts = _schemas.AtPath(_schemas.ForDocument(model.Uri, parsed.Root), JsoncParser.GetNodePath(node));
        var descriptions = contexts.Select(c => Description(c.Schema)).Where(s => !string.IsNullOrEmpty(s)).Distinct().Select(s => new MarkdownString(s!)).ToArray();
        return ValueTask.FromResult<Hover?>(descriptions.Length == 0 ? null : new(descriptions, snapshot.Range(node.Offset, node.End)));
    }
    public ValueTask<IReadOnlyList<LanguageDiagnostic>?> ProvideDiagnosticsAsync(ILanguageTextModel model, CancellationToken ct)
    {
        var snapshot = new LanguageTextSnapshot(model.GetValue()); var parsed = Parse(model, snapshot.Text, ct);
        var errors = parsed.Errors.Select(e => new LanguageDiagnostic(snapshot.Range(e.Offset, e.Offset + e.Length), ErrorMessage(e.Code), Code: ((int)e.Code).ToString(CultureInfo.InvariantCulture), Source: "jsonc-parser")).ToList();
        if (parsed.Root is { } root)
        {
            Duplicates(root, snapshot, errors, ct);
            foreach (var schema in _schemas.ForDocument(model.Uri, root)) Validate(root, schema, snapshot, errors, new(StringComparer.Ordinal), 0, ct);
        }
        return ValueTask.FromResult<IReadOnlyList<LanguageDiagnostic>?>(errors);
    }
    private static void Duplicates(JsoncNode node, LanguageTextSnapshot snapshot, List<LanguageDiagnostic> errors, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (node.Type == JsoncNodeType.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.Children)
                if (property.Children.FirstOrDefault() is { Value: string name } key && !seen.Add(name)) errors.Add(new(snapshot.Range(key.Offset, key.End), "Duplicate object key: " + name, DiagnosticSeverity.Warning, "DuplicateKey", "json"));
        }
        foreach (var child in node.Children) Duplicates(child, snapshot, errors, ct);
    }
    private void Validate(JsoncNode node, JsonSchemaRegistry.Context context, LanguageTextSnapshot snapshot, List<LanguageDiagnostic> errors, HashSet<string> visited, int depth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > 96 || errors.Count > 200)
        {
            if (!errors.Any(e => e.Code == "SchemaBudget"))
                errors.Add(new(snapshot.Range(node.Offset, node.End), "Schema validation stopped at its safety limit.", DiagnosticSeverity.Warning, "SchemaBudget", "json-schema"));
            return;
        }
        var schema = context.Schema;
        void Error(string message, JsoncNode? at = null) { var target = at ?? node; errors.Add(new(snapshot.Range(target.Offset, target.End), message, Source: "json-schema")); }
        if (schema.ValueKind == JsonValueKind.False) { Error("Value is not permitted by the schema."); return; }
        if (schema.ValueKind != JsonValueKind.Object) return;
        if (JsonSchemaRegistry.String(schema, "$ref") is { } reference)
        {
            var branch = new HashSet<string>(visited, StringComparer.Ordinal);
            if (branch.Add(context.Uri + "|" + reference + "|" + node.Offset))
            {
                if (_schemas.ResolveReference(context, reference) is { } resolved) Validate(node, resolved, snapshot, errors, branch, depth + 1, ct);
                else errors.Add(new(snapshot.Range(node.Offset, node.End), "Schema reference is not registered: " + reference, DiagnosticSeverity.Warning, "UnresolvedSchema", "json-schema"));
            }
        }
        if (schema.TryGetProperty("allOf", out var all) && all.ValueKind == JsonValueKind.Array)
            foreach (var child in all.EnumerateArray()) Validate(node, context with { Schema = child }, snapshot, errors, visited, depth + 1, ct);
        foreach (var keyword in new[] { "anyOf", "oneOf" })
            if (schema.TryGetProperty(keyword, out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                var matches = 0;
                foreach (var choice in choices.EnumerateArray())
                {
                    var branch = new List<LanguageDiagnostic>(); Validate(node, context with { Schema = choice }, snapshot, branch, visited, depth + 1, ct);
                    if (!branch.Any(d => d.Severity == DiagnosticSeverity.Error)) matches++;
                }
                if (matches == 0 || keyword == "oneOf" && matches != 1) Error("Value does not satisfy " + keyword + ".");
            }
        if (schema.TryGetProperty("not", out var forbidden))
        {
            var branch = new List<LanguageDiagnostic>(); Validate(node, context with { Schema = forbidden }, snapshot, branch, visited, depth + 1, ct);
            if (branch.Count == 0) Error("Value matches a forbidden schema.");
        }
        if (schema.TryGetProperty("type", out var type))
        {
            var types = type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()).ToArray() : new[] { type.ValueKind == JsonValueKind.String ? type.GetString() : null };
            if (types.Any(t => t is not null) && !types.Any(t => TypeMatches(node, t))) Error("Incorrect type. Expected " + string.Join(" or ", types) + ".");
        }
        if (schema.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array && !values.EnumerateArray().Any(v => ValueEquals(node, v))) Error("Value is not one of the allowed enum values.");
        if (schema.TryGetProperty("const", out var constant) && !ValueEquals(node, constant)) Error("Value must equal " + constant.GetRawText() + ".");
        if (node.Type == JsoncNodeType.Object)
        {
            var names = node.Children.Where(p => p.Children.Count > 0).Select(p => p.Children[0].Value as string).ToHashSet(StringComparer.Ordinal);
            if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
                foreach (var property in required.EnumerateArray()) if (property.ValueKind == JsonValueKind.String && !names.Contains(property.GetString())) Error("Missing required property: " + property.GetString());
            CheckCount("minProperties", "maxProperties", names.Count);
            foreach (var property in node.Children.Where(p => p.Children.Count == 2))
            {
                var name = (string)property.Children[0].Value!; var matched = false;
                if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name, out var propertySchema))
                { Validate(property.Children[1], context with { Schema = propertySchema }, snapshot, errors, visited, depth + 1, ct); matched = true; }
                if (schema.TryGetProperty("patternProperties", out var patterns) && patterns.ValueKind == JsonValueKind.Object)
                    foreach (var pattern in patterns.EnumerateObject()) if (JsonSchemaRegistry.PatternMatches(name, pattern.Name))
                        { Validate(property.Children[1], context with { Schema = pattern.Value }, snapshot, errors, visited, depth + 1, ct); matched = true; }
                if (!matched && schema.TryGetProperty("additionalProperties", out var extra))
                { if (extra.ValueKind == JsonValueKind.False) Error("Property is not allowed: " + name, property.Children[0]); else Validate(property.Children[1], context with { Schema = extra }, snapshot, errors, visited, depth + 1, ct); }
            }
        }
        if (node.Type == JsoncNodeType.Array)
        {
            CheckCount("minItems", "maxItems", node.Children.Count);
            for (var i = 0; i < node.Children.Count; i++)
                if (schema.TryGetProperty("prefixItems", out var tuple) && tuple.ValueKind == JsonValueKind.Array && i < tuple.GetArrayLength()) Validate(node.Children[i], context with { Schema = tuple[i] }, snapshot, errors, visited, depth + 1, ct);
                else if (schema.TryGetProperty("items", out var items))
                {
                    if (items.ValueKind != JsonValueKind.Array) Validate(node.Children[i], context with { Schema = items }, snapshot, errors, visited, depth + 1, ct);
                    else if (i < items.GetArrayLength()) Validate(node.Children[i], context with { Schema = items[i] }, snapshot, errors, visited, depth + 1, ct);
                    else if (schema.TryGetProperty("additionalItems", out var extra)) Validate(node.Children[i], context with { Schema = extra }, snapshot, errors, visited, depth + 1, ct);
                }
        }
        if (node.Type == JsoncNodeType.String && node.Value is string text)
        {
            var length = 0; foreach (var rune in text.EnumerateRunes()) length++;
            CheckCount("minLength", "maxLength", length);
            if (JsonSchemaRegistry.String(schema, "pattern") is { } pattern && !JsonSchemaRegistry.PatternMatches(text, pattern)) Error("String does not match the required pattern.");
        }
        if (node.Type == JsoncNodeType.Number && node.Value is double number)
        {
            if (Number("minimum") is double min && number < min) Error("Value is smaller than minimum.");
            if (Number("maximum") is double max && number > max) Error("Value is larger than maximum.");
            if (Number("exclusiveMinimum") is double emin && number <= emin) Error("Value must exceed exclusiveMinimum.");
            if (Number("exclusiveMaximum") is double emax && number >= emax) Error("Value must be below exclusiveMaximum.");
            if (Number("multipleOf") is double multiple && multiple > 0 && Math.Abs(number / multiple - Math.Round(number / multiple)) > 1e-9) Error("Value is not a multiple of the required number.");
        }
        double? Number(string key) => schema.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var result) ? result : null;
        void CheckCount(string minimum, string maximum, int count)
        { if (Number(minimum) is double min && count < min) Error("Value violates " + minimum + "."); if (Number(maximum) is double max && count > max) Error("Value violates " + maximum + "."); }
    }
    private static bool TypeMatches(JsoncNode node, string? type) => type switch
    {
        "object" => node.Type == JsoncNodeType.Object, "array" => node.Type == JsoncNodeType.Array, "string" => node.Type == JsoncNodeType.String,
        "boolean" => node.Type == JsoncNodeType.Boolean, "null" => node.Type == JsoncNodeType.Null, "number" => node.Type == JsoncNodeType.Number,
        "integer" => node.Value is double value && double.IsFinite(value) && value == Math.Truncate(value), _ => true
    };
    private static bool ValueEquals(JsoncNode node, JsonElement value)
    {
        if (node.Type == JsoncNodeType.Number) return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && Equals(node.Value, number);
        if (node.Type == JsoncNodeType.Null) return value.ValueKind == JsonValueKind.Null;
        if (node.Type == JsoncNodeType.String) return value.ValueKind == JsonValueKind.String && Equals(node.Value, value.GetString());
        if (node.Type == JsoncNodeType.Boolean) return (value.ValueKind is JsonValueKind.True or JsonValueKind.False) && Equals(node.Value, value.GetBoolean());
        if (node.Type == JsoncNodeType.Array) return value.ValueKind == JsonValueKind.Array && node.Children.Count == value.GetArrayLength() && node.Children.Select((child, i) => ValueEquals(child, value[i])).All(x => x);
        if (node.Type == JsoncNodeType.Object)
        {
            var properties = node.Children.Where(p => p.Children.Count == 2).ToArray();
            return value.ValueKind == JsonValueKind.Object && properties.Length == value.EnumerateObject().Count() && properties.All(p => value.TryGetProperty((string)p.Children[0].Value!, out var v) && ValueEquals(p.Children[1], v));
        }
        return false;
    }
    private static string ErrorMessage(JsoncParseErrorCode code) => code switch
    {
        JsoncParseErrorCode.PropertyNameExpected => "Property name expected.", JsoncParseErrorCode.ValueExpected => "Value expected.",
        JsoncParseErrorCode.ColonExpected => "Colon expected.", JsoncParseErrorCode.CommaExpected => "Comma expected.",
        JsoncParseErrorCode.CloseBraceExpected => "Closing brace expected.", JsoncParseErrorCode.CloseBracketExpected => "Closing bracket expected.",
        JsoncParseErrorCode.InvalidCommentToken => "Comments are not permitted in JSON.", JsoncParseErrorCode.EndOfFileExpected => "End of file expected.", _ => code.ToString()
    };
}
