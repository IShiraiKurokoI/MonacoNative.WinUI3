// Copyright (c) Microsoft Corporation. All rights reserved. MIT; see THIRD_PARTY_NOTICES.md.
// Transcribed assertions (inputs and expected 0-based ranges unchanged):
// vscode-json-languageservice v5.4.1/src/test/folding.test.ts (all assertions)
// vscode-html-languageservice v5.5.0/src/test/folding.test.ts (all assertions)
// vscode-css-languageservice v6.3.1/src/test/css/folding.test.ts (all active assertions)
// Separate HostFoldingContracts cases are MonacoNative tests, NOT upstream tests.
using MonacoNative.Language;
using MonacoNative.Language.Json;
using MonacoNative.Language.Html;
using MonacoNative.Language.Css;

static partial class UpstreamPortTests
{
    private readonly record struct Fold(int Start, int End, string? Kind = null);
    private static Fold F(int start, int end, string? kind = null) => new(start, end, kind);
    private static void Folding()
    {
        void Check(string language, string name, string[] lines, Fold[] expected, int? limit = null) => Case(language + "/folding " + name, () =>
        {
            var text = string.Join('\n', lines);
            var actual = language switch
            {
                "json" => JsonFolding.GetRanges(text, limit),
                "html" => HtmlFolding.GetRanges(text, limit),
                _ => CssFolding.GetRanges(text, language, limit)
            };
            var sorted = actual.OrderBy(r => r.StartLineNumber).Select(r => new Fold(r.StartLineNumber - 1, r.EndLineNumber - 1, r.Kind)).ToArray();
            AssertEx.Equal(expected.Length, sorted.Length, "range count");
            for (var i = 0; i < sorted.Length; i++) AssertEx.Equal(expected[i], sorted[i], "range " + i);
        });
        Check("json", "one level", new[] { "{", "\"foo\":\"bar\"", "}" }, new[] { F(0, 1, "object") });
        Check("json", "two levels", new[] { "[", "{", "\"foo\":\"bar\"", "}", "]" }, new[] { F(0, 3, "array"), F(1, 2, "object") });
        Check("json", "arrays", new[] { "[", "[", "],[", "1", "]", "]" }, new[] { F(0, 4, "array"), F(2, 3, "array") });
        Check("json", "same line", new[] { "[[", "[", "],[", "1", "]", "]]" }, new[] { F(0, 4, "array"), F(2, 3, "array") });
        Check("json", "comment", new[] { "/*", " multi line", "*/" }, new[] { F(0, 2, "comment") });
        Check("json", "incomplete comment", new[] { "/*", "{", "\"foo\":\"bar\"", "}" }, new[] { F(1, 2, "object") });
        Check("json", "regions", new[] { "// #region", "{", "}", "// #endregion" }, new[] { F(0, 3, "region") });
        var jsonLimits = new[] { "[", " [", "  [", "  ", "  ],", "  [", "   [", "  ", "   ],", "   [", "  ", "   ],", "  ],", "  [", "  ", "  ],", "  [", "  ", "  ]", " ]", "]" };
        var limitExpected = new[] {
            new[] { F(0,19),F(1,18),F(2,3),F(5,11),F(6,7),F(9,10),F(13,14),F(16,17) },
            new[] { F(0,19),F(1,18),F(2,3),F(5,11),F(6,7),F(13,14),F(16,17) },
            new[] { F(0,19),F(1,18),F(2,3),F(5,11),F(13,14),F(16,17) },
            new[] { F(0,19),F(1,18),F(2,3),F(5,11),F(13,14) },
            new[] { F(0,19),F(1,18),F(2,3),F(5,11) },
            new[] { F(0,19),F(1,18),F(2,3) }, new[] { F(0,19),F(1,18) }, new[] { F(0,19) }
        };
        Check("json", "unlimited", jsonLimits, limitExpected[0].Select(f => f with { Kind = "array" }).ToArray());
        for (var n = 8; n >= 1; n--) Check("json", "limit " + n, jsonLimits, limitExpected[8 - n].Select(f => f with { Kind = "array" }).ToArray(), n);

        Check("html", "one level", new[] { "<html>", "Hello", "</html>" }, new[] { F(0,1) });
        Check("html", "two levels", new[] { "<html>", "<head>", "Hello", "</head>", "</html>" }, new[] { F(0,3),F(1,2) });
        Check("html", "siblings", new[] { "<html>", "<head>", "Head", "</head>", "<body class=\"f\">", "Body", "</body>", "</html>" }, new[] { F(0,6),F(1,2),F(4,5) });
        Check("html", "self closing", new[] { "<div>", "<a href=\"top\"/>", "<img src=\"s\">", "<br/>", "<br>", "<img class=\"c\"", "     src=\"top\"", ">", "</div>" }, new[] { F(0,7),F(5,6) });
        Check("html", "comments", new[] { "<!--", " multi line", "-->", "<!-- some stuff", " some more stuff -->" }, new[] { F(0,2,"comment"),F(3,4,"comment") });
        Check("html", "regions", new[] { "<!-- #region -->", "<!-- #region -->", "<!-- #endregion -->", "<!-- #endregion -->" }, new[] { F(0,3,"region"),F(1,2,"region") });
        Check("html", "incomplete", new[] { "<body>", "<div></div>", "Hello", "</div>", "</body>" }, new[] { F(0,3) });
        Check("html", "incomplete 2", new[] { "<be><div>", "<!-- #endregion -->", "</div>" }, new[] { F(0,1) });
        Check("html", "intersecting", new[] { "<body>", "<!-- #region -->", "Hello", "<div></div>", "</body>", "<!-- #endregion -->" }, new[] { F(0,3) });
        Check("html", "intersecting 2", new[] { "<!-- #region -->", "<body>", "Hello", "<!-- #endregion -->", "<div></div>", "</body>" }, new[] { F(0,3,"region") });
        var htmlLimits = new[] { "<div>", " <span>", "  <b>", "  ", "  </b>,", "  <b>", "   <pre>", "  ", "   </pre>,", "   <pre>", "  ", "   </pre>,", "  </b>,", "  <b>", "  ", "  </b>,", "  <b>", "  ", "  </b>", " </span>", "</div>" };
        Check("html", "unlimited", htmlLimits, limitExpected[0]);
        for (var n = 8; n >= 1; n--) Check("html", "limit " + n, htmlLimits, limitExpected[8 - n], n);

        Check("css", "single rule", new[] { ".foo {", "  color: red;", "}" }, new[] { F(0,1) });
        Check("css", "single line", new[] { ".foo { color: red; }" }, Array.Empty<Fold>());
        Check("css", "multiple rules", new[] { ".foo {", "  color: red;", "  opacity: 1;", "}" }, new[] { F(0,2) });
        Check("css", "no indentation", new[] { ".foo{", "color: red;", "}" }, new[] { F(0,1) });
        Check("css", "opening newline", new[] { ".foo", "{", "color: red;", "}" }, new[] { F(1,2) });
        Check("css", "closing same line", new[] { ".foo", "{", "color: red; }" }, new[] { F(1,2) });
        Check("css", "missing close", new[] { ".foo {", "color: red;" }, Array.Empty<Fold>());
        Check("css", "partial nested", new[] { ".foo {", "color: red;", ".bar {", "color: blue;", "}" }, new[] { F(2,3) });
        Check("css", "partial nested 2", new[] { ".foo {", "  .bar {", "  .baz {", "    color: blue;", "  }", "}" }, new[] { F(1,4),F(2,3) });
        Check("css", "missing open", new[] { ".foo", "  color: blue;", "}}" }, Array.Empty<Fold>());
        Check("css", "comment", new[] { "/*", ".foo {", "  color: red;", "}", "*/" }, new[] { F(0,4,"comment") });
        Check("css", "double-star comment", new[] { "/**", ".foo {", "  color: red;", "}", "*/" }, new[] { F(0,4,"comment") });
        Check("css", "padded comment", new[] { "/**", ".foo{", "color: red;", "} */" }, new[] { F(0,3,"comment") });
        Check("css", "single line comment", new[] { "./* .foo { color: red; } */" }, Array.Empty<Fold>());
        Check("css", "postcss nested", new[] { ".foo {", "& .bar {", "  color: red;", "}", "}" }, new[] { F(0,3),F(1,2) });
        Check("css", "media", new[] { "@media screen {", ".foo {", "color: red;", "}", "}" }, new[] { F(0,3),F(1,2) });
        foreach (var markers in new[] { ("/* #region */","/* #endregion */"), ("/*  #region   */","/*   #endregion   */"), ("/*#region*/","/*#endregion*/") })
            Check("css", "region " + markers.Item1, new[] { markers.Item1, "& .bar {", "  color: red;", "}", markers.Item2 }, new[] { F(0,4,"region"),F(1,2) });
        var description = new[] { "/* #region Header page */", ".bar {", "  color: red;", "}", "/* #endregion */" };
        Check("css", "description", description, new[] { F(0,4,"region"),F(1,2) });
        Check("css", "max range", description, new[] { F(0,4,"region") }, 1);
        Check("css", "region intersection", new[] { "/* #region */", ".bar {", "  color: red;", "/* #endregion */", "  display: block;", "}" }, new[] { F(0,3,"region") });
        Check("css", "declaration intersection", new[] { ".bar {", "/* #region */", "  color: red;", "}", "/* #endregion */" }, new[] { F(0,2) });
        Check("css", "missing region start", new[] { "/* #endregion */" }, Array.Empty<Fold>());
        Check("scss", "mixin", new[] { "@mixin clearfix($width) {", "  @if !$width {", "    // if width is not passed, or empty do this", "  } @else {", "    display: inline-block;", "    width: $width;", "  }", "}" }, new[] { F(0,6),F(1,2),F(3,5) });
        Check("scss", "interpolation", new[] { ".orbit-#{$d}-prev {", "  foo-#{$d}-bar: 1;", "  #{$d}-bar-#{$d}: 2;", "}" }, new[] { F(0,2) });
        Check("scss", "while", new[] { "@while $i > 0 {", "  .item-#{$i} { width: 2em * $i; }", "  $i: $i - 2;", "}" }, new[] { F(0,2) });
        Check("scss", "nested media", new[] { "@mixin desktop {", "  $desktop-width: 1024px;", "  @media(min-width: #{$desktop-width}) {", "    width: 500px;", "  }", "}" }, new[] { F(0,4),F(2,3) });
        foreach (var language in new[] { "scss", "less" })
        {
            foreach (var markers in new[] { ("/* #region */","/* #endregion */"), ("/*  #region  */","/*   #endregion   */"), ("// #region","// #endregion"), ("//   #region  ","//   #endregion") })
                Check(language, "region " + markers.Item1, new[] { markers.Item1,"& .bar {","  color: red;","}",markers.Item2 }, new[] { F(0,4,"region"),F(1,2) });
            Check(language, "mixed comments", new[] { "// #region","/*","comments","*/","& .bar {","  color: red;","}","// #endregion" }, new[] { F(0,7,"region"),F(1,3,"comment"),F(4,5) });
        }
        HostFoldingContracts();
    }

    private sealed class EmptyFoldProvider : IFoldingRangeProvider
    {
        public ValueTask<IReadOnlyList<LanguageFoldingRange>?> ProvideFoldingRangesAsync(ILanguageTextModel model, FoldingContext context, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<LanguageFoldingRange>?>(Array.Empty<LanguageFoldingRange>());
    }
    private static void HostFoldingContracts()
    {
        Case("host/folding registration and disposal", () =>
        {
            var service = new MonacoLanguageService(false);
            using var json = new JsonLanguagePackage().Register(service);
            var model = new LanguageModelSnapshot(new Uri("file:///fold.json"), "json", 1, "{\r\n \"x\": 1\r\n}");
            AssertEx.True(service.HasFoldingRangeProvider(model), "provider registered");
            var ranges = service.ProvideFoldingRangesAsync(model).GetAwaiter().GetResult();
            AssertEx.Equal(new LanguageFoldingRange(1,2,"object"), ranges![0], "1-based CRLF range");
            using (service.RegisterFoldingRangeProvider(LanguageSelector.ForLanguage("json"), new EmptyFoldProvider()))
                AssertEx.Equal(0, service.ProvideFoldingRangesAsync(model).GetAwaiter().GetResult()!.Count, "empty result is authoritative");
            json.Dispose();
            AssertEx.True(service.ProvideFoldingRangesAsync(model).GetAwaiter().GetResult() is null, "disposed provider removed");
        });
        Case("host/folding zero-limit semantics", () =>
        {
            var notified = false;
            AssertEx.Equal(0, JsonFolding.GetRanges("{\n1\n}", 0, onRangeLimitExceeded: () => notified = true).Count, "json zero limit");
            AssertEx.True(notified, "json limit callback");
            AssertEx.Equal(1, HtmlFolding.GetRanges("<p>\na\n</p>", 0).Count, "HTML zero is unspecified upstream");
            AssertEx.Equal(1, CssFolding.GetRanges("p {\na:b;\n}", rangeLimit: 0).Count, "CSS zero is unspecified upstream");
        });
        Case("host/folding cancellation", () =>
        {
            using var cts = new CancellationTokenSource(); cts.Cancel();
            foreach (var run in new Action[] { () => JsonFolding.GetRanges("{}", cancellationToken: cts.Token),
                () => HtmlFolding.GetRanges("<a/>", cancellationToken: cts.Token), () => CssFolding.GetRanges("a{}", cancellationToken: cts.Token) })
            {
                var canceled = false;
                try { run(); } catch (OperationCanceledException) { canceled = true; }
                AssertEx.True(canceled, "cancellation observed");
            }
        });
        Case("host/folding void-element host configuration", () =>
        {
            var set = new HashSet<string>(HtmlParser.VoidElements, StringComparer.OrdinalIgnoreCase) { "custom-void" };
            const string text = "<custom-void\n name=\"x\"\n>\nbody\n</custom-void>";
            var custom = HtmlFolding.GetRanges(text, voidElements: set);
            AssertEx.Equal(1, custom.Count, "one multiline void start tag");
            AssertEx.Equal(new LanguageFoldingRange(1,2), custom[0], "void closes at start-tag end, not explicit end tag");
        });
        Case("host/folding no delimiters inside strings", () =>
        {
            AssertEx.Equal(0, JsonFolding.GetRanges("\"{\\nnot code\\n}\"").Count, "JSON string");
            AssertEx.Equal(0, CssFolding.GetRanges("a { content: '{ }'; }").Count, "CSS string");
            AssertEx.Equal(0, HtmlFolding.GetRanges("<script>const s = '<div>\\ntext\\n</div>';</script>").Count, "HTML embedded script");
        });
    }
}
