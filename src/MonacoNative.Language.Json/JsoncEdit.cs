// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License; see THIRD_PARTY_NOTICES.md.
// Source port: node-jsonc-parser v3.3.1, src/impl/edit.ts.
// Host adaptation: Remove is explicit (C# null denotes JSON null, not JS undefined).
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MonacoNative.Language.Json;

public sealed record JsoncModificationOptions(JsoncFormattingOptions? FormattingOptions = null,
    bool IsArrayInsertion = false, Func<IReadOnlyList<string>, int>? GetInsertionIndex = null);

/// <summary>Produces localized JSON/JSONC edits without serializing the entire document.</summary>
public static class JsoncEdit
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static IReadOnlyList<JsoncFormatEdit> Modify(string text, IReadOnlyList<object> path, object? value,
        JsoncModificationOptions? options = null, CancellationToken cancellationToken = default) =>
        SetProperty(text, path, value, false, options ?? new(), cancellationToken);

    public static IReadOnlyList<JsoncFormatEdit> Remove(string text, IReadOnlyList<object> path,
        JsoncModificationOptions? options = null, CancellationToken cancellationToken = default) =>
        SetProperty(text, path, null, true, options ?? new(), cancellationToken);

    public static string ApplyEdits(string text, IReadOnlyList<JsoncFormatEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(edits);
        var lastOffset = text.Length;
        for (var i = edits.Count - 1; i >= 0; i--)
        {
            var edit = edits[i];
            if (edit.Offset < 0 || edit.Length < 0 || edit.Offset > lastOffset - edit.Length)
                throw new ArgumentException("Edits must be ordered, non-overlapping and within the original text.", nameof(edits));
            lastOffset = edit.Offset;
            text = ApplyEdit(text, edit);
        }
        return text;
    }

    private static IReadOnlyList<JsoncFormatEdit> SetProperty(string text, IReadOnlyList<object> originalPath,
        object? value, bool remove, JsoncModificationOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(originalPath);
        ct.ThrowIfCancellationRequested();
        foreach (var segment in originalPath)
            if (segment is not string && segment is not int)
                throw new ArgumentException("A JSON path contains only string properties and integer indices.", nameof(originalPath));
        var path = originalPath.ToList();
        var root = JsoncParser.ParseTree(text, cancellationToken: ct).Root;
        JsoncNode? parent = null;
        object? lastSegment = null;
        while (path.Count > 0)
        {
            lastSegment = path[^1];
            path.RemoveAt(path.Count - 1);
            parent = JsoncParser.FindNodeAtLocation(root, path);
            if (parent is null && !remove)
                value = lastSegment is string key
                    ? new Dictionary<string, object?> { [key] = value } : new object?[] { value };
            else break;
        }
        if (parent is null)
        {
            if (remove) throw new InvalidOperationException("Can not delete in empty document.");
            return WithFormatting(text, new(root?.Offset ?? 0, root?.Length ?? 0, Serialize(value)), options, ct);
        }
        if (parent.Type == JsoncNodeType.Object && lastSegment is string name)
        {
            var existing = JsoncParser.FindNodeAtLocation(parent, new object[] { name });
            if (existing is not null)
            {
                if (!remove) return WithFormatting(text, new(existing.Offset, existing.Length, Serialize(value)), options, ct);
                var property = existing.Parent ?? throw new InvalidOperationException("Malformed AST.");
                var index = parent.Children.IndexOf(property);
                var begin = index > 0 ? parent.Children[index - 1].End : parent.Offset + 1;
                var end = index == 0 && parent.Children.Count > 1 ? parent.Children[1].Offset : property.End;
                return WithFormatting(text, new(begin, end - begin, ""), options, ct);
            }
            if (remove) return Array.Empty<JsoncFormatEdit>();
            var content = Serialize(name) + ": " + Serialize(value);
            var insertion = options.GetInsertionIndex?.Invoke(parent.Children.Select(p => (string)p.Children[0].Value!).ToArray())
                ?? parent.Children.Count;
            if (insertion < 0 || insertion > parent.Children.Count)
                throw new ArgumentOutOfRangeException(nameof(options), "Insertion index is outside the object's properties.");
            var edit = insertion > 0 ? new JsoncFormatEdit(parent.Children[insertion - 1].End, 0, "," + content)
                : new JsoncFormatEdit(parent.Offset + 1, 0, content + (parent.Children.Count > 0 ? "," : ""));
            return WithFormatting(text, edit, options, ct);
        }
        if (parent.Type == JsoncNodeType.Array && lastSegment is int arrayIndex)
        {
            var children = parent.Children;
            JsoncFormatEdit edit;
            if (arrayIndex == -1 && !remove)
            {
                edit = children.Count == 0 ? new(parent.Offset + 1, 0, Serialize(value))
                    : new(children[^1].End, 0, "," + Serialize(value));
            }
            else if (remove)
            {
                // Upstream dereferences invalid indices. Use an explicit managed exception instead.
                if (arrayIndex < 0 || arrayIndex >= children.Count)
                    throw new ArgumentOutOfRangeException(nameof(originalPath), "Removal index is outside the array.");
                if (children.Count == 1) edit = new(parent.Offset + 1, parent.Length - 2, "");
                else if (arrayIndex == children.Count - 1)
                {
                    var offset = children[arrayIndex - 1].End;
                    edit = new(offset, parent.End - 2 - offset, "");
                }
                else edit = new(children[arrayIndex].Offset, children[arrayIndex + 1].Offset - children[arrayIndex].Offset, "");
            }
            else
            {
                if (arrayIndex < 0) throw new ArgumentOutOfRangeException(nameof(originalPath));
                var content = Serialize(value);
                if (!options.IsArrayInsertion && children.Count > arrayIndex)
                    edit = new(children[arrayIndex].Offset, children[arrayIndex].Length, content);
                else if (children.Count == 0 || arrayIndex == 0)
                    edit = new(parent.Offset + 1, 0, content + (children.Count > 0 ? "," : ""));
                else edit = new(children[Math.Min(arrayIndex, children.Count) - 1].End, 0, "," + content);
            }
            return WithFormatting(text, edit, options, ct);
        }
        throw new InvalidOperationException($"Can not modify path segment '{lastSegment}' on {parent.Type}.");
    }

    private static IReadOnlyList<JsoncFormatEdit> WithFormatting(string text, JsoncFormatEdit edit,
        JsoncModificationOptions options, CancellationToken ct)
    {
        if (options.FormattingOptions is null) return new[] { edit };
        var newText = ApplyEdit(text, edit);
        var begin = edit.Offset;
        var end = edit.Offset + edit.Content.Length;
        if (edit.Length == 0 || edit.Content.Length == 0)
        {
            while (begin > 0 && !IsEol(newText[begin - 1])) begin--;
            while (end < newText.Length && !IsEol(newText[end])) end++;
        }
        var formatting = JsoncFormatter.Format(newText, new(begin, end - begin),
            options.FormattingOptions with { KeepLines = false }, ct);
        for (var i = formatting.Count - 1; i >= 0; i--)
        {
            ct.ThrowIfCancellationRequested();
            var change = formatting[i];
            newText = ApplyEdit(newText, change);
            begin = Math.Min(begin, change.Offset);
            end = Math.Max(end, change.Offset + change.Length) + change.Content.Length - change.Length;
        }
        return new[] { new JsoncFormatEdit(begin, text.Length - (newText.Length - end) - begin, newText[begin..end]) };
    }
    private static string Serialize(object? value) => JsonSerializer.Serialize(value, SerializerOptions);
    private static bool IsEol(char c) => c is '\r' or '\n';
    private static string ApplyEdit(string text, JsoncFormatEdit edit)
    {
        if (edit.Offset < 0 || edit.Length < 0 || edit.Offset > text.Length - edit.Length)
            throw new ArgumentOutOfRangeException(nameof(edit));
        return text[..edit.Offset] + edit.Content + text[(edit.Offset + edit.Length)..];
    }
}
