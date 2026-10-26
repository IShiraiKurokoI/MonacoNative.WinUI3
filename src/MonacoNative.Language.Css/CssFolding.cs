// Copyright (c) Microsoft Corporation. All rights reserved.
// MIT; see THIRD_PARTY_NOTICES.md.
// Source port: vscode-css-languageservice v6.3.1/src/services/cssFolding.ts.
using System.Text.RegularExpressions;

namespace MonacoNative.Language.Css;

public sealed class CssFolding : IFoldingRangeProvider
{
    private static readonly Regex BlockRegion = new(@"^[\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]*/\*[\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]*(#region|#endregion)(?![A-Za-z0-9_])[\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]*([^\r\n\u2028\u2029]*?)[\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]*\*/", RegexOptions.ECMAScript | RegexOptions.CultureInvariant);
    private static readonly Regex LineRegion = new(@"^[\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]*//[\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]*(#region|#endregion)(?![A-Za-z0-9_])[\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]*([^\r\n\u2028\u2029]*?)[\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]*", RegexOptions.ECMAScript | RegexOptions.CultureInvariant);
    public static IReadOnlyList<LanguageFoldingRange> GetRanges(string text, string languageId = "css", int? rangeLimit = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (rangeLimit is < 0) throw new ArgumentOutOfRangeException(nameof(rangeLimit));
        cancellationToken.ThrowIfCancellationRequested();
        var document = new LanguageTextSnapshot(text);
        var scanner = CssDialectScanner.Create(languageId, text, cancellationToken);
        scanner.IgnoreComment = false;
        var ranges = new List<LanguageFoldingRange>();
        var stack = new List<(int Line, bool Comment)>();
        int? Pop(bool comment)
        {
            var index = stack.FindLastIndex(entry => entry.Comment == comment);
            if (index < 0) return null;
            var line = stack[index].Line; stack.RemoveAt(index); return line;
        }
        int Start(CssToken t) => document.PositionAt(t.Offset).LineNumber;
        int End(CssToken t) => document.PositionAt(t.Offset + t.Length).LineNumber;
        CssToken? previous = null;
        for (var token = scanner.Scan(); token.Type != CssTokenType.EOF; token = scanner.Scan())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (token.Type == CssTokenType.CurlyL || (languageId == "scss" && token.Type == ScssScanner.InterpolationFunction))
                stack.Add((Start(token), false));
            else if (token.Type == CssTokenType.CurlyR)
            {
                if (Pop(false) is int start)
                {
                    var end = End(token);
                    if (previous is not null && End(previous) != end) end--;
                    if (start != end) ranges.Add(new(start, end));
                }
            }
            else if (token.Type == CssTokenType.Comment)
            {
                var match = BlockRegion.Match(token.Text);
                if (!match.Success && languageId is "scss" or "less") match = LineRegion.Match(token.Text);
                if (match.Success)
                {
                    if (match.Groups[1].Value == "#region") stack.Add((Start(token), true));
                    else if (Pop(true) is int start && start != End(token)) ranges.Add(new(start, End(token), "region"));
                }
                else if (Start(token) != End(token)) ranges.Add(new(Start(token), End(token), "comment"));
            }
            previous = token;
        }
        var valid = new List<LanguageFoldingRange>(); var prevEnd = -1;
        foreach (var range in ranges.OrderBy(r => r.StartLineNumber).ThenBy(r => r.EndLineNumber))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!(range.StartLineNumber < prevEnd && prevEnd < range.EndLineNumber))
            { valid.Add(range); prevEnd = range.EndLineNumber; }
        }
        // CSS treats a zero limit as unspecified, matching upstream's || expression.
        return rangeLimit is > 0 ? valid.Take(rangeLimit.Value).ToArray() : valid;
    }
    public ValueTask<IReadOnlyList<LanguageFoldingRange>?> ProvideFoldingRangesAsync(
        ILanguageTextModel model, FoldingContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<LanguageFoldingRange>?>(GetRanges(model.GetValue(), model.LanguageId, context.RangeLimit, cancellationToken));
}
