using System.Text.Json;
using System.Text.RegularExpressions;

namespace MonacoNative.Language.Json;

/// <summary>Explicit, offline schema inputs. The registry never downloads a URL or opens a file.</summary>
public sealed class JsonSchemaRegistry
{
    internal sealed record Entry(Uri Uri, JsonElement Root, string[] FilePatterns);
    internal readonly record struct Context(JsonElement Schema, JsonElement Root, Uri Uri);
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public void Register(Uri uri, string schemaJson, params string[] filePatterns)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri) throw new ArgumentException("Schema URI must be absolute.", nameof(uri));
        using var parsed = JsonDocument.Parse(schemaJson, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (parsed.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException("A JSON Schema must be an object or a boolean.", nameof(schemaJson));
        var entry = new Entry(uri, parsed.RootElement.Clone(), (filePatterns ?? Array.Empty<string>()).ToArray());
        lock (_gate) _entries[WithoutFragment(uri)] = entry;
    }
    public bool Unregister(Uri uri) { lock (_gate) return _entries.Remove(WithoutFragment(uri)); }

    internal Context[] ForDocument(Uri documentUri, JsoncNode? root)
    {
        lock (_gate)
        {
            var id = JsoncParser.FindNodeAtLocation(root, new object[] { "$schema" })?.Value as string;
            if (id is not null && Uri.TryCreate(documentUri, id, out var requested) && _entries.TryGetValue(WithoutFragment(requested), out var entry))
            {
                var selected = Fragment(new(entry.Root, entry.Root, entry.Uri), requested.Fragment);
                return selected is { } context ? new[] { context } : Array.Empty<Context>();
            }
            var path = Uri.UnescapeDataString(documentUri.AbsolutePath);
            return _entries.Values.Where(e => e.FilePatterns.Any(pattern => Matches(pattern, path)))
                .Select(e => new Context(e.Root, e.Root, e.Uri)).ToArray();
        }
    }
    private static bool Matches(string pattern, string path)
    {
        var normalized = pattern.Replace('\\', '/');
        var regex = "^" + Regex.Escape(normalized).Replace(@"\*\*/", "(?:.*/)?", StringComparison.Ordinal)
            .Replace(@"\*\*", ".*", StringComparison.Ordinal).Replace(@"\*", "[^/]*", StringComparison.Ordinal)
            .Replace(@"\?", "[^/]", StringComparison.Ordinal) + "$";
        bool Match(string value) => Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return Match(path) || Match(path.TrimStart('/')) || (!normalized.Contains('/') && Match(Path.GetFileName(path)));
    }
    private static string WithoutFragment(Uri uri) => uri.GetLeftPart(UriPartial.Path) + uri.Query;
    internal Context? ResolveReference(Context context, string reference)
    {
        var baseUri = context.Uri;
        if (String(context.Schema, "$id") is { } id && Uri.TryCreate(baseUri, id, out var updated)) baseUri = updated;
        if (!Uri.TryCreate(baseUri, reference, out var uri)) return null;
        if (WithoutFragment(uri) == WithoutFragment(context.Uri)) return Fragment(context, uri.Fragment);
        lock (_gate) return _entries.TryGetValue(WithoutFragment(uri), out var entry)
            ? Fragment(new(entry.Root, entry.Root, entry.Uri), uri.Fragment) : null;
    }
    private static Context? Fragment(Context context, string fragment)
    {
        if (fragment.Length == 0 || fragment == "#") return context with { Schema = context.Root };
        var pointer = Uri.UnescapeDataString(fragment.TrimStart('#'));
        if (!pointer.StartsWith('/')) return null; // Anchors/dynamic references are not implemented.
        var current = context.Root;
        foreach (var raw in pointer[1..].Split('/'))
        {
            var segment = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segment, out var property)) current = property;
            else if (current.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var index) && index >= 0 && index < current.GetArrayLength()) current = current[index];
            else return null;
        }
        return context with { Schema = current };
    }
    internal IEnumerable<Context> Expand(Context context, int depth = 0, HashSet<string>? visited = null)
    {
        if (depth > 64) yield break;
        yield return context;
        if (context.Schema.ValueKind != JsonValueKind.Object) yield break;
        if (String(context.Schema, "$ref") is { } reference)
        {
            visited = visited is null ? new(StringComparer.Ordinal) : new(visited, StringComparer.Ordinal);
            if (visited.Add(context.Uri + "|" + reference) && ResolveReference(context, reference) is { } resolved)
                foreach (var next in Expand(resolved, depth + 1, visited)) yield return next;
        }
        foreach (var keyword in new[] { "allOf", "anyOf", "oneOf" })
            if (context.Schema.TryGetProperty(keyword, out var group) && group.ValueKind == JsonValueKind.Array)
                foreach (var child in group.EnumerateArray())
                    foreach (var next in Expand(context with { Schema = child }, depth + 1, visited)) yield return next;
    }
    internal Context[] AtPath(IEnumerable<Context> roots, IReadOnlyList<object> path)
    {
        var contexts = roots.ToArray();
        foreach (var segment in path)
        {
            var next = new List<Context>();
            foreach (var source in contexts)
                foreach (var context in Expand(source))
                {
                    var schema = context.Schema;
                    if (schema.ValueKind != JsonValueKind.Object) continue;
                    if (segment is string name)
                    {
                        var matched = false;
                        if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name, out var property))
                        { next.Add(context with { Schema = property }); matched = true; }
                        if (schema.TryGetProperty("patternProperties", out var patterns) && patterns.ValueKind == JsonValueKind.Object)
                            foreach (var pattern in patterns.EnumerateObject())
                                if (PatternMatches(name, pattern.Name)) { next.Add(context with { Schema = pattern.Value }); matched = true; }
                        if (!matched && schema.TryGetProperty("additionalProperties", out var additional)) next.Add(context with { Schema = additional });
                    }
                    else if (segment is int index)
                    {
                        if (schema.TryGetProperty("prefixItems", out var tuple) && tuple.ValueKind == JsonValueKind.Array && index < tuple.GetArrayLength()) next.Add(context with { Schema = tuple[index] });
                        else if (schema.TryGetProperty("items", out var items))
                        {
                            if (items.ValueKind != JsonValueKind.Array) next.Add(context with { Schema = items });
                            else if (index < items.GetArrayLength()) next.Add(context with { Schema = items[index] });
                            else if (schema.TryGetProperty("additionalItems", out var extra)) next.Add(context with { Schema = extra });
                        }
                    }
                }
            contexts = next.ToArray();
        }
        return contexts.SelectMany(c => Expand(c)).ToArray();
    }
    internal static bool PatternMatches(string value, string pattern)
    {
        try { return Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); }
        catch (ArgumentException) { return false; }
        catch (RegexMatchTimeoutException) { return false; }
    }
    internal static string? String(JsonElement schema, string key) => schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
