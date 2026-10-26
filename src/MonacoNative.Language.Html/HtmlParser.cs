// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License; see THIRD_PARTY_NOTICES.md.
// C# port of vscode-html-languageservice/src/parser/htmlParser.ts.
namespace MonacoNative.Language.Html;

public sealed class HtmlNode
{
    public string? Tag { get; internal set; }
    public bool Closed { get; internal set; }
    public int Start { get; }
    public int End { get; internal set; }
    public int? StartTagEnd { get; internal set; }
    public int? EndTagStart { get; internal set; }
    public HtmlNode? Parent { get; }
    public List<HtmlNode> Children { get; } = new();
    public Dictionary<string, string?> Attributes { get; } = new(StringComparer.Ordinal);
    internal HtmlNode(int start, int end, HtmlNode? parent = null) { Start = start; End = end; Parent = parent; }
    public bool IsSameTag(string? lowerCaseTag) => Tag is null ? lowerCaseTag is null : lowerCaseTag is not null && string.Equals(Tag, lowerCaseTag, StringComparison.OrdinalIgnoreCase);
    private int ChildBefore(int offset)
    {
        var low = 0; var high = Children.Count;
        while (low < high) { var mid = low + (high - low) / 2; if (offset <= Children[mid].Start) high = mid; else low = mid + 1; }
        return low - 1;
    }
    public HtmlNode FindNodeBefore(int offset)
    {
        var current = this;
        // Iterative equivalent avoids recursion on deeply nested, unfinished HTML.
        while (true)
        {
            var index = current.ChildBefore(offset); if (index < 0) return current;
            var child = current.Children[index];
            if (offset <= child.Start) return current;
            if (offset < child.End || (child.Children.Count > 0 && child.Children[^1].End == child.End)) { current = child; continue; }
            return child;
        }
    }
    public HtmlNode FindNodeAt(int offset)
    {
        var current = this;
        while (true)
        {
            var index = current.ChildBefore(offset); if (index < 0) return current;
            var child = current.Children[index];
            if (offset > child.Start && offset <= child.End) current = child; else return current;
        }
    }
}
public sealed class HtmlDocument
{
    internal HtmlDocument(HtmlNode root) => Root = root;
    internal HtmlNode Root { get; }
    public IReadOnlyList<HtmlNode> Roots => Root.Children;
    public HtmlNode FindNodeBefore(int offset) => Root.FindNodeBefore(offset);
    public HtmlNode FindNodeAt(int offset) => Root.FindNodeAt(offset);
}
public static class HtmlParser
{
    public static IReadOnlySet<string> VoidElements { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr" };
    public static HtmlDocument Parse(string text, bool createNodesForOrphanEndTags = false,
        IReadOnlySet<string>? voidElements = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text); voidElements ??= VoidElements;
        var scanner = new HtmlScanner(text, emitPseudoCloseTags: true, cancellationToken: cancellationToken);
        var root = new HtmlNode(0, text.Length); var current = root;
        var endTagStart = -1; string? endTagName = null; string? pendingAttribute = null;
        while (scanner.Scan() != HtmlTokenType.EOS)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (scanner.TokenType)
            {
                case HtmlTokenType.StartTagOpen:
                    var child = new HtmlNode(scanner.TokenOffset, text.Length, current); current.Children.Add(child); current = child; break;
                case HtmlTokenType.StartTag: current.Tag = scanner.TokenText; break;
                case HtmlTokenType.StartTagClose when current.Parent is not null:
                    current.End = scanner.TokenEnd;
                    if (scanner.TokenLength != 0)
                    {
                        current.StartTagEnd = scanner.TokenEnd;
                        if (current.Tag is not null && voidElements.Contains(current.Tag)) { current.Closed = true; current = current.Parent; }
                    }
                    else current = current.Parent;
                    break;
                case HtmlTokenType.StartTagSelfClose when current.Parent is not null:
                    current.Closed = true; current.End = scanner.TokenEnd; current.StartTagEnd = scanner.TokenEnd; current = current.Parent; break;
                case HtmlTokenType.EndTagOpen: endTagStart = scanner.TokenOffset; endTagName = null; break;
                case HtmlTokenType.EndTag: endTagName = scanner.TokenText.ToLowerInvariant(); break;
                case HtmlTokenType.EndTagClose:
                    var node = current;
                    while (!node.IsSameTag(endTagName) && node.Parent is not null) node = node.Parent;
                    if (node.Parent is not null)
                    {
                        while (current != node) { current.End = endTagStart; current.Closed = false; current = current.Parent!; }
                        current.Closed = true; current.EndTagStart = endTagStart; current.End = scanner.TokenEnd; current = current.Parent!;
                    }
                    else if (createNodesForOrphanEndTags)
                        current.Children.Add(new(endTagStart, scanner.TokenEnd, current) { Tag = endTagName, Closed = true, EndTagStart = endTagStart });
                    break;
                case HtmlTokenType.AttributeName: pendingAttribute = scanner.TokenText; current.Attributes[pendingAttribute] = null; break;
                case HtmlTokenType.AttributeValue:
                    if (pendingAttribute is not null) { current.Attributes[pendingAttribute] = scanner.TokenText; pendingAttribute = null; }
                    break;
            }
        }
        while (current.Parent is not null) { current.End = text.Length; current.Closed = false; current = current.Parent; }
        return new(root);
    }
}
