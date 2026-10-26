// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License; see THIRD_PARTY_NOTICES.md.
// Source port of vscode-html-languageservice v5.5.0:
// services/htmlMatchingTagPosition.ts, htmlLinkedEditing.ts, htmlRename.ts.
using MonacoNative.Language;

namespace MonacoNative.Language.Html;

public static class HtmlTagEditing
{
    public static LanguagePosition? FindMatchingTagPosition(LanguageTextSnapshot document,
        LanguagePosition position, HtmlDocument htmlDocument)
    {
        var offset = document.OffsetAt(position);
        var node = htmlDocument.FindNodeAt(offset);
        if (string.IsNullOrEmpty(node.Tag) || node.EndTagStart is not int endTag || endTag == 0) return null;
        if (node.Start + 1 <= offset && offset <= node.Start + 1 + node.Tag.Length)
            return document.PositionAt(offset - 1 - node.Start + endTag + 2);
        if (endTag + 2 <= offset && offset <= endTag + 2 + node.Tag.Length)
            return document.PositionAt(offset - 2 - endTag + node.Start + 1);
        return null;
    }

    public static IReadOnlyList<LanguageRange>? FindLinkedEditingRanges(LanguageTextSnapshot document,
        LanguagePosition position, HtmlDocument htmlDocument)
    {
        var offset = document.OffsetAt(position);
        var node = htmlDocument.FindNodeAt(offset);
        var length = node.Tag?.Length ?? 0;
        if (node.EndTagStart is not int endTag || endTag == 0) return null;
        // Upstream permits empty names here (e.g. <|></>), unlike rename.
        if ((node.Start + 1 <= offset && offset <= node.Start + 1 + length) ||
            (endTag + 2 <= offset && offset <= endTag + 2 + length))
            return new[] { document.Range(node.Start + 1, node.Start + 1 + length),
                document.Range(endTag + 2, endTag + 2 + length) };
        return null;
    }

    public static WorkspaceEdit? Rename(Uri uri, LanguageTextSnapshot document, LanguagePosition position,
        string newName, HtmlDocument htmlDocument)
    {
        ArgumentNullException.ThrowIfNull(newName);
        var offset = document.OffsetAt(position);
        var node = htmlDocument.FindNodeAt(offset);
        if (string.IsNullOrEmpty(node.Tag) || !IsWithinTagRange(node, offset)) return null;
        var edits = new List<WorkspaceTextEdit>
        { new(uri, document.Range(node.Start + 1, node.Start + 1 + node.Tag.Length), newName) };
        if (node.EndTagStart is int endTag && endTag != 0)
            edits.Add(new(uri, document.Range(endTag + 2, endTag + 2 + node.Tag.Length), newName));
        return new(edits);
    }

    internal static bool IsWithinTagRange(HtmlNode node, int offset)
    {
        var length = node.Tag?.Length ?? 0;
        if (node.EndTagStart is int endTag && endTag != 0 && endTag + 2 <= offset && offset <= endTag + 2 + length) return true;
        return node.Start + 1 <= offset && offset <= node.Start + 1 + length;
    }
}

/// <summary>Adapts the source-ported rename algorithm to the existing in-process provider interface.</summary>
public sealed class HtmlTagRenameProvider : IRenameProvider
{

    public ValueTask<RenameLocation?> ResolveRenameLocationAsync(ILanguageTextModel model,
        LanguagePosition position, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = new LanguageTextSnapshot(model.GetValue());
        var offset = snapshot.OffsetAt(position);
        var html = HtmlParser.Parse(snapshot.Text, cancellationToken: cancellationToken);
        var node = html.FindNodeAt(offset);
        if (string.IsNullOrEmpty(node.Tag) || !HtmlTagEditing.IsWithinTagRange(node, offset))
            return ValueTask.FromResult<RenameLocation?>(null);
        var start = node.EndTagStart is int endTag && offset >= endTag + 2 ? endTag + 2 : node.Start + 1;
        return ValueTask.FromResult<RenameLocation?>(new(snapshot.Range(start, start + node.Tag.Length), node.Tag));
    }

    public ValueTask<WorkspaceEdit?> ProvideRenameEditsAsync(ILanguageTextModel model, LanguagePosition position,
        string newName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = new LanguageTextSnapshot(model.GetValue());
        var html = HtmlParser.Parse(snapshot.Text, cancellationToken: cancellationToken);
        return ValueTask.FromResult(HtmlTagEditing.Rename(model.Uri, snapshot, position, newName, html));
    }
}
