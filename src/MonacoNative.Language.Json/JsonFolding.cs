// Copyright (c) Microsoft Corporation. All rights reserved.
// MIT; see THIRD_PARTY_NOTICES.md.
// Source port: vscode-json-languageservice v5.4.1/src/services/jsonFolding.ts.
// Offset/line conversion is adapted to MonacoNative's 1-based model coordinates.
using System.Text.RegularExpressions;

namespace MonacoNative.Language.Json;

public sealed class JsonFolding : IFoldingRangeProvider
{
    // Keep upstream alternatives/anchors and explicitly use JS ASCII word-boundary semantics.
    private static readonly Regex Region = new(@"^//[\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]*#(region(?![A-Za-z0-9_]))|(endregion(?![A-Za-z0-9_]))", RegexOptions.ECMAScript | RegexOptions.CultureInvariant);
    public static IReadOnlyList<LanguageFoldingRange> GetRanges(string text, int? rangeLimit = null,
        CancellationToken cancellationToken = default, Action? onRangeLimitExceeded = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (rangeLimit is < 0) throw new ArgumentOutOfRangeException(nameof(rangeLimit));
        cancellationToken.ThrowIfCancellationRequested();
        var document = new LanguageTextSnapshot(text);
        var lineCount = document.PositionAt(text.Length).LineNumber;
        var ranges = new List<LanguageFoldingRange>();
        var levels = new List<int>();
        var stack = new List<LanguageFoldingRange>();
        var prevStart = -1;
        var scanner = new JsoncScanner(text, cancellationToken: cancellationToken);
        void Add(LanguageFoldingRange range) { ranges.Add(range); levels.Add(stack.Count); }
        for (var token = scanner.Scan(); token != JsoncSyntaxKind.EOF; token = scanner.Scan())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = document.PositionAt(scanner.TokenOffset).LineNumber;
            switch (token)
            {
                case JsoncSyntaxKind.OpenBraceToken:
                case JsoncSyntaxKind.OpenBracketToken:
                    stack.Add(new(line, line, token == JsoncSyntaxKind.OpenBraceToken ? "object" : "array"));
                    break;
                case JsoncSyntaxKind.CloseBraceToken:
                case JsoncSyntaxKind.CloseBracketToken:
                    var kind = token == JsoncSyntaxKind.CloseBraceToken ? "object" : "array";
                    if (stack.Count > 0 && stack[^1].Kind == kind)
                    {
                        var range = stack[^1]; stack.RemoveAt(stack.Count - 1);
                        if (line > range.StartLineNumber + 1 && prevStart != range.StartLineNumber)
                        { Add(range with { EndLineNumber = line - 1 }); prevStart = range.StartLineNumber; }
                    }
                    break;
                case JsoncSyntaxKind.BlockCommentTrivia:
                    var endLine = document.PositionAt(scanner.TokenOffset + scanner.TokenLength).LineNumber;
                    if (scanner.TokenError == JsoncScanError.UnexpectedEndOfComment && line < lineCount)
                        scanner.SetPosition(document.OffsetAt(new(line + 1, 1)));
                    else if (line < endLine)
                    { Add(new(line, endLine, "comment")); prevStart = line; }
                    break;
                case JsoncSyntaxKind.LineCommentTrivia:
                    var match = Region.Match(text.Substring(scanner.TokenOffset, scanner.TokenLength));
                    if (!match.Success) break;
                    if (match.Groups[1].Success) stack.Add(new(line, line, "region"));
                    else
                    {
                        var i = stack.FindLastIndex(range => range.Kind == "region");
                        if (i < 0) break;
                        var range = stack[i]; stack.RemoveRange(i, stack.Count - i);
                        if (line > range.StartLineNumber && prevStart != range.StartLineNumber)
                        { Add(range with { EndLineNumber = line }); prevStart = range.StartLineNumber; }
                    }
                    break;
            }
        }
        if (rangeLimit is not int limit || ranges.Count <= limit) return ranges;
        onRangeLimitExceeded?.Invoke();
        var counts = new int[30];
        foreach (var level in levels) if (level < counts.Length) counts[level]++;
        var entries = 0; var maxLevel = 0;
        for (var i = 0; i < counts.Length; i++)
        {
            if (counts[i] == 0) continue;
            if (counts[i] + entries > limit) { maxLevel = i; break; }
            entries += counts[i];
        }
        var result = new List<LanguageFoldingRange>();
        for (var i = 0; i < ranges.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (levels[i] < maxLevel || (levels[i] == maxLevel && entries++ < limit)) result.Add(ranges[i]);
        }
        return result;
    }
    public ValueTask<IReadOnlyList<LanguageFoldingRange>?> ProvideFoldingRangesAsync(
        ILanguageTextModel model, FoldingContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<LanguageFoldingRange>?>(GetRanges(model.GetValue(), context.RangeLimit,
            cancellationToken, () => context.OnRangeLimitExceeded?.Invoke(model.Uri)));
}
