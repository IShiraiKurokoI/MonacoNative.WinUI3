// Copyright (c) Microsoft Corporation. All rights reserved. MIT.
// vscode-html-languageservice v5.5.0 src/test/{rename,matchingTagPosition,linkedEditing}.test.ts.
// All assertions in those three files are transcribed; provider registration is a host regression.
using MonacoNative.Language;
using MonacoNative.Language.Html;

static partial class UpstreamPortTests
{
    private static void HtmlTags()
    {
        var uri = new Uri("test://test/test.html");
        void Rename(string marked, string name, string? expected) => Case("html/rename " + marked, () =>
        {
            var (text, offset) = Cursor(marked);
            var snapshot = new LanguageTextSnapshot(text);
            var edits = HtmlTagEditing.Rename(uri, snapshot, snapshot.PositionAt(offset), name, HtmlParser.Parse(text));
            if (expected is null) { AssertEx.True(edits is null, "no rename"); return; }
            AssertEx.True(edits is not null, "rename workspace edits");
            var result = text;
            foreach (var e in edits!.Edits.OrderByDescending(e => Start(snapshot, e.Range)))
            {
                AssertEx.Equal(uri, e.Uri, "edit URI");
                var start = Start(snapshot, e.Range); var end = End(snapshot, e.Range);
                result = result[..start] + e.Text + result[end..];
            }
            AssertEx.Equal(expected, result, "renamed document");
        });
        foreach (var marked in new[] { "<|div></div>", "<d|iv></div>", "<di|v></div>", "<div|></div>", "<|div></div>", "<|div></div>" })
            Rename(marked, "h1", "<h1></h1>");
        foreach (var marked in new[] { "|<div></div>", "<div>|</div>", "<div><|/div>", "<div></div>|",
            "<div |id=\"foo\"></div>", "<div i|d=\"foo\"></div>", "<div id|=\"foo\"></div>", "<div id=|\"foo\"></div>",
            "<div id=\"|foo\"></div>", "<div id=\"f|oo\"></div>", "<div id=\"fo|o\"></div>",
            "<div id=\"foo|\"></div>", "<div id=\"foo\"|></div>" }) Rename(marked, "h1", null);
        Rename("<|br>", "h1", "<h1>"); Rename("<|br/>", "h1", "<h1/>"); Rename("<|br />", "h1", "<h1 />");
        Rename("<div><|h1></h1></div>", "h2", "<div><h2></h2></div>");
        Rename("<div><|h1></div>", "h2", "<div><h2></div>");
        Rename("<|div><h1></h1></div>", "span", "<span><h1></h1></span>");

        foreach (var marked in new[] { "<|div></$div>", "<d|iv></d$iv>", "<di|v></di$v>", "<div|></div$>",
            "<$div></|div>", "<d$iv></d|iv>", "<di$v></di|v>", "<div$></div|>",
            "<div| ></div$>", "<div| id=\"foo\"></div$>", "<div$ ></div|>", "<div$ id=\"foo\"></div|>" })
            Case("html/matching " + marked, () =>
            {
                var (text, offset) = Cursor(marked);
                var mirror = text.IndexOf('$'); text = text.Remove(mirror, 1);
                if (mirror < offset) offset--;
                var snapshot = new LanguageTextSnapshot(text);
                var match = HtmlTagEditing.FindMatchingTagPosition(snapshot, snapshot.PositionAt(offset), HtmlParser.Parse(text));
                AssertEx.True(match.HasValue, "matching position");
                AssertEx.Equal(snapshot.PositionAt(mirror), match!.Value, "matching position value");
            });
        void Linked(string marked, params (int Offset, string Text)[] expected) => Case("html/linked " + marked, () =>
        {
            var (text, offset) = Cursor(marked); var snapshot = new LanguageTextSnapshot(text);
            var ranges = HtmlTagEditing.FindLinkedEditingRanges(snapshot, snapshot.PositionAt(offset), HtmlParser.Parse(text));
            AssertEx.Equal(expected.Length, ranges?.Count ?? 0, "linked count");
            for (var i = 0; i < expected.Length; i++)
            {
                var range = ranges![i]; var start = Start(snapshot, range);
                AssertEx.Equal(expected[i].Offset, start, "linked offset");
                AssertEx.Equal(expected[i].Text, text[start..End(snapshot, range)], "linked text");
            }
        });
        Linked("|<div></div>");
        foreach (var text in new[] { "<|div></div>", "<d|iv></div>", "<di|v></div>", "<div|></div>" }) Linked(text, (1, "div"), (7, "div"));
        Linked("<div>|</div>"); Linked("<div><|/div>");
        foreach (var text in new[] { "<div></|div>", "<div></d|iv>", "<div></di|v>", "<div></div|>" }) Linked(text, (1, "div"), (7, "div"));
        Linked("<div></div>|"); Linked("<div><div|</div>"); Linked("<div><div><div|</div></div>");
        Linked("<div| ></div>", (1, "div"), (8, "div"));
        Linked("<div| id=\"foo\"></div>", (1, "div"), (16, "div"));
        Linked("<|></>", (1, ""), (4, "")); Linked("<><div></div></|>", (1, ""), (15, ""));

        Case("html/provider integration (host)", () =>
        {
            var model = new LanguageModelSnapshot(uri, "html", 1, "<div></div>");
            var provider = new HtmlTagRenameProvider();
            var location = provider.ResolveRenameLocationAsync(model, new(1, 3), CancellationToken.None).GetAwaiter().GetResult();
            AssertEx.Equal("div", location?.Text, "provider rename span");
            var edit = provider.ProvideRenameEditsAsync(model, new(1, 3), "p", CancellationToken.None).GetAwaiter().GetResult();
            AssertEx.Equal(2, edit?.Edits.Count ?? 0, "provider paired rename");
        });
    }
}
