// Copyright (c) Microsoft Corporation. All rights reserved. MIT.
// Source tests: vscode-css-languageservice v6.3.1 src/test/css/scanner.test.ts
// and src/test/less/scanner.test.ts. SCSS extension cases below are host regressions.
using MonacoNative.Language.Css;
using T = MonacoNative.Language.Css.CssTokenType;

static partial class UpstreamPortTests
{
    private static void CssScanners()
    {
        void Single(CssScanner scanner, string source, int length, int offset, string text, params T[] kinds) =>
            Case("css/scanner " + source, () =>
            {
                scanner.SetSource(source);
                var token = scanner.Scan();
                AssertEx.Equal(length, token.Length, "length"); AssertEx.Equal(offset, token.Offset, "offset");
                AssertEx.Equal(text, token.Text, "text"); AssertEx.Equal(kinds[0], token.Type, "type");
                for (var i = 1; i < kinds.Length; i++) AssertEx.Equal(kinds[i], scanner.Scan().Type, "next type");
                AssertEx.Equal(T.EOF, scanner.Scan().Type, "EOF");
            });
        var s = new CssScanner();
        Single(s, " @", 1, 1, "@", T.Delim);
        Single(s, " /* comment*/ \n/*comment*/@", 1, 26, "@", T.Delim);
        var whitespace = new CssScanner { IgnoreWhitespace = false };
        Single(whitespace, " @", 1, 0, " ", T.Whitespace, T.Delim);
        Single(whitespace, "/*comment*/ @", 1, 11, " ", T.Whitespace, T.Delim);
        var comments = new CssScanner { IgnoreComment = false };
        Single(comments, " /*comment*/@", 11, 1, "/*comment*/", T.Comment, T.Delim);
        Single(comments, "/*comment*/ @", 11, 0, "/*comment*/", T.Comment, T.Delim);
        foreach (var text in new[] { "\u060frf", "über", "-bo", "_bo", "boo", "Boo", "red--", "--red", "--100", "---red", "---" })
            Single(s, text, text.Length, 0, text, T.Ident);
        Single(s, "red-->", 5, 0, "red--", T.Ident, T.Delim);
        Single(s, "a\\.b", 4, 0, "a.b", T.Ident);
        Single(s, "\\E9motion", 9, 0, "émotion", T.Ident);
        Single(s, "\\E9 dition", 10, 0, "édition", T.Ident);
        Single(s, "\\0000E9dition", 13, 0, "édition", T.Ident);
        Single(s, "S\\0000e9f", 9, 0, "Séf", T.Ident);
        foreach (var source in new[] { "http://msft.com", "http://msft.com'" }) Case("css/url " + source, () =>
        {
            s.SetSource(source); var t = s.ScanUnquotedString();
            AssertEx.True(t is not null, "URL token");
            AssertEx.Equal(15, t!.Length, "URL length"); AssertEx.Equal(0, t.Offset, "URL offset");
            AssertEx.Equal("http://msft.com", t.Text, "URL text"); AssertEx.Equal(T.UnquotedString, t.Type, "URL kind");
        });
        foreach (var text in new[] { "@import", "@importttt", "@imp", "@5", "@media", "@page", "@-mport", "@ðmport", "@apply" })
            Single(s, text, text.Length, 0, text, T.AtKeyword);
        Single(s, "@charset", 8, 0, "@charset", T.Charset); Single(s, "@", 1, 0, "@", T.Delim);
        foreach (var text in new[] { "1234", "1.34", ".234" }) Single(s, text, 4, 0, text, T.Num);
        Single(s, ".234.", 4, 0, ".234", T.Num, T.Delim); Single(s, "..234", 1, 0, ".", T.Delim, T.Num);
        foreach (var text in new[] { "@", "+", ">", "#" }) Single(s, text, 1, 0, text, T.Delim);
        Single(s, "'", 1, 0, "'", T.BadString); Single(s, "\"", 1, 0, "\"", T.BadString);
        foreach (var text in new[] { "#import", "#-mport", "#123" }) Single(s, text, text.Length, 0, text, T.Hash);
        foreach (var (text, kind) in new[] {
            ("3em", T.EMS), ("4.423ex", T.EXS), ("3423px", T.Length), ("4.423cm", T.Length),
            ("4.423mm", T.Length), ("4.423in", T.Length), ("4.423pt", T.Length), ("4.423pc", T.Length),
            ("4.423deg", T.Angle), ("4.423rad", T.Angle), ("4.423grad", T.Angle), ("4.423ms", T.Time),
            ("4.423s", T.Time), ("4.423hz", T.Freq), (".423khz", T.Freq), ("3.423%", T.Percentage),
            (".423%", T.Percentage), (".423ft", T.Dimension), ("200dpi", T.Resolution), ("123dpcm", T.Resolution) })
            Single(s, text, text.Length, 0, text, kind);
        Single(s, "'farboo'", 8, 0, "'farboo'", T.String);
        Single(s, "\"farboo\"", 8, 0, "\"farboo\"", T.String);
        Single(s, "\"farboð\"", 8, 0, "\"farboð\"", T.String);
        Single(s, "\"far\\\"oo\"", 9, 0, "\"far\"oo\"", T.String);
        Single(s, "\"fa\\\noo\"", 8, 0, "\"fa\noo\"", T.String);
        Single(s, "\"fa\\\roo\"", 8, 0, "\"fa\roo\"", T.String);
        Single(s, "\"fa\\\foo\"", 8, 0, "\"fa\foo\"", T.String);
        foreach (var text in new[] { "'farboo\"", "'farboo", "'", "\"" }) Single(s, text, text.Length, 0, text, T.BadString);
        Single(s, "<!--", 4, 0, "<!--", T.CDO);
        Single(s, "<!-\n-", 1, 0, "<", T.Delim, T.Exclamation, T.Delim, T.Delim);
        Single(s, "-->", 3, 0, "-->", T.CDC);
        Single(s, "--y>", 3, 0, "--y", T.Ident, T.Delim); Single(s, "--<", 2, 0, "--", T.Ident, T.Delim);
        foreach (var (text, kind) in new[] { (":", T.Colon), (";", T.SemiColon), ("{", T.CurlyL), ("}", T.CurlyR),
            ("[", T.BracketL), ("]", T.BracketR), ("(", T.ParenthesisL), (")", T.ParenthesisR) })
            Single(s, text + "  ", 1, 0, text, kind);
        foreach (var (text, kind) in new[] { ("~=", T.Includes), ("~", T.Delim), ("|=", T.Dashmatch),
            ("|", T.Delim), ("^=", T.PrefixOperator), ("$=", T.SuffixOperator), ("*=", T.SubstringOperator) })
            Single(s, text, text.Length, 0, text, kind);
        foreach (var text in new[] { "/*      */", "/*      abcd*/", "/*abcd  */", "/* ab- .-cd  */", " ", "      " })
            Single(s, text, 0, text.Length, "", T.EOF);
        void Sequence(string text, params T[] kinds) => Case("css/sequence " + text, () =>
        {
            s.SetSource(text);
            foreach (var kind in kinds) AssertEx.Equal(kind, s.Scan().Type, "sequence kind");
        });
        Sequence("5 5 5 5", T.Num, T.Num, T.Num, T.Num);
        foreach (var text in new[] { "/* 5 4 */-->", "/* 5 4 */ -->", "/* \"adaasd\" */ -->", "/* <!-- */ -->" }) Sequence(text, T.CDC);
        Sequence("red-->", T.Ident, T.Delim);
        Sequence("@ import", T.Delim, T.Ident);

        var less = new LessScanner();
        Single(less, "`", 1, 0, "`", T.BadEscapedJavaScript);
        Single(less, "`a", 2, 0, "`a", T.BadEscapedJavaScript);
        Single(less, "`let a = \"ssss\"`", 16, 0, "`let a = \"ssss\"`", T.EscapedJavaScript);
        Single(less, "`let a = \"ss\ns\"`", 16, 0, "`let a = \"ss\ns\"`", T.EscapedJavaScript);
        Single(less, "//", 0, 2, "", T.EOF);
        Single(less, "//this is a comment test", 0, 24, "", T.EOF);
        Single(less, "// this is a comment test", 0, 25, "", T.EOF);
        Single(less, "// this is a\na", 1, 13, "a", T.Ident);
        Single(less, "// this is a\n// more\n   \n/* comment */a", 1, 38, "a", T.Ident);

        // Host-written SCSS regressions, not represented as upstream test ports.
        var scss = new ScssScanner();
        Single(scss, "$name", 5, 0, "$name", ScssScanner.VariableName);
        Single(scss, "#{", 2, 0, "#{", ScssScanner.InterpolationFunction);
        foreach (var (text, kind) in new[] { ("==", ScssScanner.EqualsOperator), ("!=", ScssScanner.NotEqualsOperator),
            (">=", ScssScanner.GreaterEqualsOperator), ("<=", ScssScanner.SmallerEqualsOperator), ("...", ScssScanner.Ellipsis) })
            Single(scss, text, text.Length, 0, text, kind);
        Single(scss, "// comment\n$x", 2, 11, "$x", ScssScanner.VariableName);
        Single(new LessScanner { InUrl = true }, "//host", 1, 0, "/", T.Delim, T.Delim, T.Ident);
        Single(new ScssScanner { InUrl = true }, "//host", 1, 0, "/", T.Delim, T.Delim, T.Ident);
    }
}
