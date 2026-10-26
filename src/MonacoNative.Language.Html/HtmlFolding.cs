// Copyright (c) Microsoft Corporation. All rights reserved.
// MIT; see THIRD_PARTY_NOTICES.md.
// Source port: vscode-html-languageservice v5.5.0/src/services/htmlFolding.ts.
using System.Text.RegularExpressions;

namespace MonacoNative.Language.Html;

public sealed class HtmlFolding : IFoldingRangeProvider
{
    private static readonly Regex Region = new(@"^[\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]*#(region(?![A-Za-z0-9_]))|(endregion(?![A-Za-z0-9_]))", RegexOptions.ECMAScript | RegexOptions.CultureInvariant);
    private readonly IReadOnlySet<string> _voidElements;
    // Equivalent host input to upstream HTMLDataManager.getVoidElements; snapshot it for worker safety.
    public HtmlFolding(IReadOnlySet<string>? voidElements = null) =>
        _voidElements = new HashSet<string>(voidElements ?? HtmlParser.VoidElements, StringComparer.OrdinalIgnoreCase);
    public static IReadOnlyList<LanguageFoldingRange> GetRanges(string text, int? rangeLimit = null,
        CancellationToken cancellationToken = default, IReadOnlySet<string>? voidElements = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (rangeLimit is < 0) throw new ArgumentOutOfRangeException(nameof(rangeLimit));
        cancellationToken.ThrowIfCancellationRequested();
        var activeVoidElements = new HashSet<string>(voidElements ?? HtmlParser.VoidElements, StringComparer.OrdinalIgnoreCase);
        var document = new LanguageTextSnapshot(text);
        var scanner = new HtmlScanner(text, cancellationToken: cancellationToken);
        var ranges = new List<LanguageFoldingRange>();
        var stack = new List<(int Line, string Tag)>();
        string? lastTag = null; var prevStart = -1;
        void Add(LanguageFoldingRange range) { ranges.Add(range); prevStart = range.StartLineNumber; }
        for (var token = scanner.Scan(); token != HtmlTokenType.EOS; token = scanner.Scan())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = document.PositionAt(scanner.TokenOffset).LineNumber;
            switch (token)
            {
                case HtmlTokenType.StartTag:
                    lastTag = scanner.TokenText; stack.Add((line, lastTag)); break;
                case HtmlTokenType.EndTag:
                    lastTag = scanner.TokenText; break;
                case HtmlTokenType.StartTagClose:
                    if (lastTag is null || !activeVoidElements.Contains(lastTag)) break;
                    goto case HtmlTokenType.EndTagClose;
                case HtmlTokenType.EndTagClose:
                case HtmlTokenType.StartTagSelfClose:
                    var i = stack.FindLastIndex(entry => entry.Tag == lastTag);
                    if (i < 0) break;
                    var start = stack[i].Line; stack.RemoveRange(i, stack.Count - i);
                    if (line - 1 > start && prevStart != start) Add(new(start, line - 1));
                    break;
                case HtmlTokenType.Comment:
                    var match = Region.Match(scanner.TokenText);
                    if (match.Success)
                    {
                        if (match.Groups[1].Success) stack.Add((line, ""));
                        else
                        {
                            var region = stack.FindLastIndex(entry => entry.Tag.Length == 0);
                            if (region < 0) break;
                            var regionStart = stack[region].Line; stack.RemoveRange(region, stack.Count - region);
                            if (line > regionStart && prevStart != regionStart) Add(new(regionStart, line, "region"));
                        }
                    }
                    else
                    {
                        var end = document.PositionAt(scanner.TokenEnd).LineNumber;
                        if (line < end) Add(new(line, end, "comment"));
                    }
                    break;
            }
        }
        // Upstream treats 0 as unspecified for HTML (unlike JSON).
        return rangeLimit is > 0 && ranges.Count > rangeLimit.Value
            ? Limit(ranges, rangeLimit.Value, cancellationToken) : ranges;
    }
    private static IReadOnlyList<LanguageFoldingRange> Limit(List<LanguageFoldingRange> ranges, int limit, CancellationToken ct)
    {
        // LINQ ordering is stable, as is modern JS Array.sort.
        ranges = ranges.OrderBy(r => r.StartLineNumber).ThenBy(r => r.EndLineNumber).ToList();
        var levels = new int?[ranges.Count]; var counts = new int[30];
        var previous = new List<LanguageFoldingRange>(); LanguageFoldingRange? top = null;
        void Level(int i, int level) { levels[i] = level; if (level < counts.Length) counts[level]++; }
        for (var i = 0; i < ranges.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = ranges[i];
            if (top is null) { top = entry; Level(i, 0); }
            else if (entry.StartLineNumber > top.StartLineNumber)
            {
                if (entry.EndLineNumber <= top.EndLineNumber)
                { previous.Add(top); top = entry; Level(i, previous.Count); }
                else if (entry.StartLineNumber > top.EndLineNumber)
                {
                    do
                    {
                        top = previous.Count == 0 ? null : previous[^1];
                        if (previous.Count > 0) previous.RemoveAt(previous.Count - 1);
                    } while (top is not null && entry.StartLineNumber > top.EndLineNumber);
                    if (top is not null) previous.Add(top);
                    top = entry; Level(i, previous.Count);
                }
            }
        }
        var entries = 0; var maxLevel = 0;
        for (var i = 0; i < counts.Length; i++)
        {
            if (counts[i] == 0) continue;
            if (counts[i] + entries > limit) { maxLevel = i; break; }
            entries += counts[i];
        }
        var result = new List<LanguageFoldingRange>();
        for (var i = 0; i < ranges.Count; i++)
            if (levels[i] is int level && (level < maxLevel || (level == maxLevel && entries++ < limit))) result.Add(ranges[i]);
        return result;
    }
    public ValueTask<IReadOnlyList<LanguageFoldingRange>?> ProvideFoldingRangesAsync(
        ILanguageTextModel model, FoldingContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<LanguageFoldingRange>?>(GetRanges(model.GetValue(), context.RangeLimit, cancellationToken, _voidElements));
}
