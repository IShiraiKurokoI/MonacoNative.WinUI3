// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License; see THIRD_PARTY_NOTICES.md.
// C# source port of microsoft/node-jsonc-parser/src/impl/format.ts (main, reviewed 2026-10-23).
// The upstream string-intern optimization is replaced by ordinary .NET strings;
// token decisions and edit boundaries are retained. This is not JSON schema validation.
using MonacoNative.Language;

namespace MonacoNative.Language.Json;

public readonly record struct JsoncOffsetRange(int Offset, int Length);
public sealed record JsoncFormattingOptions(int TabSize = 4, bool InsertSpaces = true,
    bool KeepLines = false, bool InsertFinalNewline = false, string Eol = "\n");
public sealed record JsoncFormatEdit(int Offset, int Length, string Content);

public static class JsoncFormatter
{
    public static IReadOnlyList<JsoncFormatEdit> Format(string documentText, JsoncOffsetRange? range = null,
        JsoncFormattingOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentText);
        options ??= new();
        var tabSize = options.TabSize == 0 ? 4 : options.TabSize;
        if (tabSize < 0 || tabSize > 1024) throw new ArgumentOutOfRangeException(nameof(options), "TabSize must be between 0 and 1024.");
        var rangeStart = 0; var rangeEnd = documentText.Length;
        var formatStart = 0; var initialIndent = 0;
        var formatText = documentText;
        if (range is { } requested)
        {
            if (requested.Offset < 0 || requested.Length < 0 || requested.Offset > documentText.Length - requested.Length)
                throw new ArgumentOutOfRangeException(nameof(range));
            rangeStart = requested.Offset; rangeEnd = rangeStart + requested.Length;
            formatStart = rangeStart;
            while (formatStart > 0 && !IsEol(documentText, formatStart - 1)) formatStart--;
            var end = rangeEnd;
            while (end < documentText.Length && !IsEol(documentText, end)) end++;
            formatText = documentText[formatStart..end];
            var indentChars = 0;
            foreach (var ch in formatText)
            {
                if (ch == ' ') indentChars++;
                else if (ch == '\t') indentChars += tabSize;
                else break;
            }
            initialIndent = indentChars / tabSize;
        }
        var eol = GetEol(documentText, options.Eol);
        var lineBreaks = 0; var indentLevel = 0; var hasError = false;
        var indentValue = options.InsertSpaces ? new string(' ', tabSize) : "\t";
        var scanner = new JsoncScanner(formatText, cancellationToken: cancellationToken);
        var edits = new List<JsoncFormatEdit>();
        string NewLinesAndIndent() => Repeat(eol, Math.Max(1, lineBreaks)) + Repeat(indentValue, initialIndent + indentLevel);
        JsoncSyntaxKind ScanNext()
        {
            var token = scanner.Scan(); lineBreaks = 0;
            while (token is JsoncSyntaxKind.Trivia or JsoncSyntaxKind.LineBreakTrivia)
            {
                if (token == JsoncSyntaxKind.LineBreakTrivia) lineBreaks = options.KeepLines ? lineBreaks + 1 : 1;
                token = scanner.Scan();
            }
            hasError = token == JsoncSyntaxKind.Unknown || scanner.TokenError != JsoncScanError.None;
            return token;
        }
        void AddEdit(string text, int start, int end)
        {
            // A malformed token can overrun EOF in the upstream scanner; such edits are discarded.
            if (start < 0 || end < start || end > documentText.Length) return;
            if (!hasError && (range is null || start < rangeEnd && end > rangeStart) && documentText[start..end] != text)
                edits.Add(new(start, end - start, text));
        }
        var first = ScanNext();
        if (options.KeepLines && lineBreaks > 0) AddEdit(Repeat(eol, lineBreaks), 0, 0);
        if (first != JsoncSyntaxKind.EOF) AddEdit(Repeat(indentValue, initialIndent), formatStart, scanner.TokenOffset + formatStart);
        while (first != JsoncSyntaxKind.EOF)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var firstEnd = scanner.TokenOffset + scanner.TokenLength + formatStart;
            var second = ScanNext(); var replacement = string.Empty; var needsLineBreak = false;
            while (lineBreaks == 0 && (second is JsoncSyntaxKind.LineCommentTrivia or JsoncSyntaxKind.BlockCommentTrivia))
            {
                AddEdit(" ", firstEnd, scanner.TokenOffset + formatStart);
                firstEnd = scanner.TokenOffset + scanner.TokenLength + formatStart;
                needsLineBreak = second == JsoncSyntaxKind.LineCommentTrivia;
                replacement = needsLineBreak ? NewLinesAndIndent() : string.Empty;
                second = ScanNext();
            }
            if (second == JsoncSyntaxKind.CloseBraceToken || second == JsoncSyntaxKind.CloseBracketToken)
            {
                var opening = second == JsoncSyntaxKind.CloseBraceToken ? JsoncSyntaxKind.OpenBraceToken : JsoncSyntaxKind.OpenBracketToken;
                if (first != opening) indentLevel--;
                if (options.KeepLines && lineBreaks > 0 || !options.KeepLines && first != opening) replacement = NewLinesAndIndent();
                else if (options.KeepLines) replacement = " ";
            }
            else
            {
                switch (first)
                {
                    case JsoncSyntaxKind.OpenBracketToken:
                    case JsoncSyntaxKind.OpenBraceToken:
                        indentLevel++;
                        replacement = !options.KeepLines || lineBreaks > 0 ? NewLinesAndIndent() : " ";
                        break;
                    case JsoncSyntaxKind.CommaToken:
                        replacement = !options.KeepLines || lineBreaks > 0 ? NewLinesAndIndent() : " ";
                        break;
                    case JsoncSyntaxKind.LineCommentTrivia: replacement = NewLinesAndIndent(); break;
                    case JsoncSyntaxKind.BlockCommentTrivia:
                        if (lineBreaks > 0) replacement = NewLinesAndIndent();
                        else if (!needsLineBreak) replacement = " ";
                        break;
                    case JsoncSyntaxKind.ColonToken:
                        if (options.KeepLines && lineBreaks > 0) replacement = NewLinesAndIndent();
                        else if (!needsLineBreak) replacement = " ";
                        break;
                    case JsoncSyntaxKind.StringLiteral:
                        if (options.KeepLines && lineBreaks > 0) replacement = NewLinesAndIndent();
                        else if (second == JsoncSyntaxKind.ColonToken && !needsLineBreak) replacement = string.Empty;
                        break;
                    case JsoncSyntaxKind.NullKeyword:
                    case JsoncSyntaxKind.TrueKeyword:
                    case JsoncSyntaxKind.FalseKeyword:
                    case JsoncSyntaxKind.NumericLiteral:
                    case JsoncSyntaxKind.CloseBraceToken:
                    case JsoncSyntaxKind.CloseBracketToken:
                        if (options.KeepLines && lineBreaks > 0) replacement = NewLinesAndIndent();
                        else if ((second is JsoncSyntaxKind.LineCommentTrivia or JsoncSyntaxKind.BlockCommentTrivia) && !needsLineBreak) replacement = " ";
                        else if (second is not (JsoncSyntaxKind.CommaToken or JsoncSyntaxKind.EOF)) hasError = true;
                        break;
                    case JsoncSyntaxKind.Unknown: hasError = true; break;
                }
                if (lineBreaks > 0 && (second is JsoncSyntaxKind.LineCommentTrivia or JsoncSyntaxKind.BlockCommentTrivia))
                    replacement = NewLinesAndIndent();
            }
            if (second == JsoncSyntaxKind.EOF)
                replacement = options.KeepLines && lineBreaks > 0 ? NewLinesAndIndent() : options.InsertFinalNewline ? eol : string.Empty;
            AddEdit(replacement, firstEnd, scanner.TokenOffset + formatStart);
            first = second;
        }
        return edits;
    }

    private static bool IsEol(string text, int offset) => offset >= 0 && offset < text.Length && (text[offset] is '\r' or '\n');
    private static string Repeat(string text, int count) => count <= 0 ? string.Empty : string.Concat(Enumerable.Repeat(text, count));
    private static string GetEol(string text, string fallback)
    {
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\r') return i + 1 < text.Length && text[i + 1] == '\n' ? "\r\n" : "\r";
            else if (text[i] == '\n') return "\n";
        return string.IsNullOrEmpty(fallback) ? "\n" : fallback;
    }
}

/// <summary>Direct provider registration. No LSP process, JSON-RPC or JavaScript runtime.</summary>
public sealed class JsoncFormattingProvider : IDocumentFormattingEditProvider, IDocumentRangeFormattingEditProvider
{
    public ValueTask<IReadOnlyList<LanguageTextEdit>?> ProvideDocumentFormattingEditsAsync(
        ILanguageTextModel model, FormattingOptions options, CancellationToken cancellationToken) =>
        Format(model, null, options, cancellationToken);

    public ValueTask<IReadOnlyList<LanguageTextEdit>?> ProvideDocumentRangeFormattingEditsAsync(
        ILanguageTextModel model, LanguageRange range, FormattingOptions options, CancellationToken cancellationToken) =>
        Format(model, range.Normalize(), options, cancellationToken);

    private static ValueTask<IReadOnlyList<LanguageTextEdit>?> Format(ILanguageTextModel model, LanguageRange? range,
        FormattingOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        var snapshot = new LanguageTextSnapshot(model.GetValue());
        JsoncOffsetRange? offsets = null;
        if (range is { } r)
        {
            var start = snapshot.OffsetAt(new(r.StartLineNumber, r.StartColumn));
            var end = snapshot.OffsetAt(new(r.EndLineNumber, r.EndColumn));
            offsets = new(start, end - start);
        }
        var keepLines = options.Extra is { } extra && extra.TryGetValue("keepLines", out var keep) && keep is true;
        var eol = options.Extra is { } extras && extras.TryGetValue("eol", out var value) ? value as string ?? "\n" : "\n";
        var nativeOptions = new JsoncFormattingOptions(options.TabSize, options.InsertSpaces, keepLines, options.InsertFinalNewline == true, eol);
        var edits = JsoncFormatter.Format(snapshot.Text, offsets, nativeOptions, cancellationToken)
            .Select(edit => new LanguageTextEdit(snapshot.Range(edit.Offset, edit.Offset + edit.Length), edit.Content)).ToArray();
        return ValueTask.FromResult<IReadOnlyList<LanguageTextEdit>?>(edits);
    }
}
