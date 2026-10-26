// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License; see THIRD_PARTY_NOTICES.md.
// Port/adaptation of the scanner dispatch, close-tag, attribute and attribute-value
// completion paths in vscode-html-languageservice/src/services/htmlCompletion.ts.
// Metadata is deliberately a limited default set plus host custom data; path/entity
// completion and embedded language delegation are not included in this module.
using MonacoNative.Language;

namespace MonacoNative.Language.Html;

public sealed class HtmlSyntaxCompletionProvider : ICompletionItemProvider, IHoverProvider
{
    private readonly HtmlCustomData _data;
    public HtmlSyntaxCompletionProvider(HtmlCustomData? data = null) => _data = data ?? new();
    public IReadOnlyList<string> TriggerCharacters { get; } = new[] { "<", "/", " ", "=", "\"", "'" };
    private sealed record Context(HtmlTokenType Token, HtmlScannerState State, int Start, int End, string Text, string Tag, string Attribute, int CloseStart);
    private static Context Scan(string text, int offset, CancellationToken ct)
    {
        var scanner = new HtmlScanner(text, cancellationToken: ct);
        var tag = string.Empty; var attribute = string.Empty; var closeStart = offset;
        var last = new Context(HtmlTokenType.EOS, HtmlScannerState.WithinContent, offset, offset, "", tag, attribute, closeStart);
        while (scanner.Scan() != HtmlTokenType.EOS)
        {
            if (scanner.TokenType == HtmlTokenType.StartTagOpen) { tag = ""; attribute = ""; }
            if (scanner.TokenType == HtmlTokenType.StartTag) tag = scanner.TokenText;
            if (scanner.TokenType == HtmlTokenType.AttributeName) attribute = scanner.TokenText;
            if (scanner.TokenType == HtmlTokenType.EndTagOpen) closeStart = scanner.TokenOffset;
            last = new(scanner.TokenType, scanner.State, scanner.TokenOffset, scanner.TokenEnd, scanner.TokenText, tag, attribute, closeStart);
            if (scanner.TokenEnd >= offset) break;
        }
        return last;
    }
    public ValueTask<CompletionList?> ProvideCompletionItemsAsync(ILanguageTextModel model, LanguagePosition position, CompletionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model); cancellationToken.ThrowIfCancellationRequested();
        var snapshot = new LanguageTextSnapshot(model.GetValue()); var text = snapshot.Text; var offset = snapshot.OffsetAt(position);
        var document = HtmlParser.Parse(text, cancellationToken: cancellationToken);
        var token = Scan(text, offset, cancellationToken);
        var output = new List<CompletionItem>();
        var node = document.FindNodeBefore(offset);
        if (token.Token is HtmlTokenType.Comment or HtmlTokenType.StartCommentTag or HtmlTokenType.Script or HtmlTokenType.Styles or HtmlTokenType.Doctype)
            return ValueTask.FromResult<CompletionList?>(new(output));
        if (token.Token == HtmlTokenType.StartTag || token.Token == HtmlTokenType.StartTagOpen && offset >= token.End)
        {
            var start = token.Token == HtmlTokenType.StartTag ? token.Start : offset;
            var end = token.Token == HtmlTokenType.StartTag ? token.End : NameEnd(text, start);
            foreach (var tag in _data.Tags) output.Add(new(tag.Name, CompletionItemKind.Property, Documentation: tag.Description, InsertText: tag.Name, Range: snapshot.Range(start, end)));
        }
        else if (token.Token is HtmlTokenType.EndTag or HtmlTokenType.EndTagOpen || token.State is HtmlScannerState.AfterOpeningEndTag or HtmlScannerState.WithinEndTag)
        {
            var start = token.Token == HtmlTokenType.EndTag ? token.Start : offset;
            var end = token.Token == HtmlTokenType.EndTag ? token.End : NameEnd(text, start);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var candidate = node; candidate is not null; candidate = candidate.Parent)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidate.Tag is not { } tag || HtmlParser.VoidElements.Contains(tag) || candidate.Closed && candidate.EndTagStart != token.CloseStart || !seen.Add(tag)) continue;
                var insert = tag + (end < text.Length && text[end] == '>' ? "" : ">");
                var range = snapshot.Range(start, end);
                var closeLineStart = text.LastIndexOf('\n', Math.Max(0, token.CloseStart - 1)) + 1;
                var openLineStart = text.LastIndexOf('\n', Math.Max(0, candidate.Start - 1)) + 1;
                var beforeClosing = text[closeLineStart..token.CloseStart];
                var beforeOpening = text[openLineStart..candidate.Start];
                if (beforeClosing.All(IsIndent) && beforeOpening.All(IsIndent) && beforeClosing != beforeOpening)
                { range = snapshot.Range(closeLineStart, end); insert = beforeOpening + "</" + insert; }
                output.Add(new(tag, CompletionItemKind.Property, InsertText: insert, Range: range, Preselect: output.Count == 0));
            }
        }
        else if (token.Token == HtmlTokenType.AttributeValue || token.State == HtmlScannerState.BeforeAttributeValue)
        {
            var start = token.Token == HtmlTokenType.AttributeValue ? token.Start : offset;
            var end = token.Token == HtmlTokenType.AttributeValue ? token.End : offset;
            var quoted = token.Token == HtmlTokenType.AttributeValue && start < text.Length && (text[start] is '\'' or '"') && offset > start;
            if (quoted)
            {
                var quote = text[start]; var contentStart = start + 1;
                var contentEnd = end > contentStart && text[end - 1] == quote ? end - 1 : end;
                start = Math.Clamp(offset, contentStart, contentEnd);
                end = start;
                while (start > contentStart && !char.IsWhiteSpace(text[start - 1])) start--;
                while (end < contentEnd && !char.IsWhiteSpace(text[end])) end++;
            }
            else if (token.Token != HtmlTokenType.AttributeValue)
            {
                var valueStart = offset;
                while (valueStart < text.Length && char.IsWhiteSpace(text[valueStart])) valueStart++;
                if (valueStart < text.Length && (text[valueStart] is '\'' or '"'))
                {
                    var quote = text[valueStart];
                    var closing = text.IndexOf(quote, valueStart + 1);
                    end = closing >= 0 ? closing + 1 : text.Length;
                }
                else
                {
                    end = valueStart;
                    while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] != '>') end++;
                }
            }
            var attribute = _data.AttributesFor(token.Tag).FirstOrDefault(a => a.Name.Equals(token.Attribute, StringComparison.OrdinalIgnoreCase));
            foreach (var value in attribute?.Values ?? Array.Empty<string>())
                output.Add(new(value, CompletionItemKind.Value, Documentation: attribute?.Description, InsertText: quoted ? value : "\"" + value + "\"", Range: snapshot.Range(start, end)));
        }
        else if (token.Token == HtmlTokenType.AttributeName || token.Token == HtmlTokenType.Whitespace && (token.State is HtmlScannerState.WithinTag or HtmlScannerState.AfterAttributeName))
        {
            var start = token.Token == HtmlTokenType.AttributeName ? token.Start : offset;
            var end = token.Token == HtmlTokenType.AttributeName ? token.End : offset;
            var used = node.Attributes.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (token.Token == HtmlTokenType.AttributeName) used.Remove(token.Text);
            var lookahead = end; while (lookahead < text.Length && char.IsWhiteSpace(text[lookahead])) lookahead++;
            var hasAssignment = lookahead < text.Length && text[lookahead] == '=';
            foreach (var attribute in _data.AttributesFor(token.Tag))
            {
                if (used.Contains(attribute.Name)) continue;
                var insert = attribute.Name; string? snippet = null;
                if (!attribute.Boolean && !hasAssignment) { insert += "=\"\""; snippet = attribute.Name + "=\"${1}\""; }
                output.Add(new(attribute.Name, CompletionItemKind.Value, Documentation: attribute.Description, InsertText: insert, Range: snapshot.Range(start, end), SnippetText: snippet));
            }
        }
        return ValueTask.FromResult<CompletionList?>(new(output));
    }
    public ValueTask<Hover?> ProvideHoverAsync(ILanguageTextModel model, LanguagePosition position, CancellationToken cancellationToken)
    {
        var snapshot = new LanguageTextSnapshot(model.GetValue()); var token = Scan(snapshot.Text, snapshot.OffsetAt(position), cancellationToken);
        var description = token.Token is HtmlTokenType.StartTag or HtmlTokenType.EndTag
            ? _data.Tags.FirstOrDefault(t => t.Name.Equals(token.Text, StringComparison.OrdinalIgnoreCase))?.Description
            : token.Token is HtmlTokenType.AttributeName or HtmlTokenType.AttributeValue
                ? _data.AttributesFor(token.Tag).FirstOrDefault(a => a.Name.Equals(token.Attribute, StringComparison.OrdinalIgnoreCase))?.Description : null;
        return ValueTask.FromResult<Hover?>(description is null ? null : new(new[] { new MarkdownString(description) }, snapshot.Range(token.Start, token.End)));
    }
    private static bool IsIndent(char c) => c is ' ' or '\t' or '\r';
    private static int NameEnd(string text, int start)
    { while (start < text.Length && (char.IsLetterOrDigit(text[start]) || text[start] is '-' or '_' or ':' or '.')) start++; return start; }
}
