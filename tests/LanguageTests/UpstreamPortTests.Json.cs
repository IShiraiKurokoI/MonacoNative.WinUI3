// Copyright (c) Microsoft Corporation. All rights reserved. MIT.
// node-jsonc-parser v3.3.1 src/test/json.test.ts: scanner tests (not parser/visitor suites).
// node-jsonc-parser v3.3.1 src/test/edit.test.ts: all edit assertions.
// JS undefined in modify maps to the explicit JsoncEdit.Remove API, not C# null.
using MonacoNative.Language.Json;
using K = MonacoNative.Language.Json.JsoncSyntaxKind;
using E = MonacoNative.Language.Json.JsoncScanError;

static partial class UpstreamPortTests
{
    private static void JsonScanner()
    {
        void Scan(string source, E error, params K[] kinds) => Case("json/scanner " + source, () =>
        {
            var scanner = new JsoncScanner(source);
            for (var i = 0; i < kinds.Length; i++)
            {
                AssertEx.Equal(kinds[i], scanner.Scan(), "token");
                if (error == E.None || i == 0) AssertEx.Equal(i == 0 ? error : E.None, scanner.TokenError, "scan error");
            }
            AssertEx.Equal(K.EOF, scanner.Scan(), "EOF");
        });
        void Kinds(string source, params K[] kinds) => Scan(source, E.None, kinds);
        foreach (var (text, kind) in new[] { ("{", K.OpenBraceToken), ("}", K.CloseBraceToken),
            ("[", K.OpenBracketToken), ("]", K.CloseBracketToken), (":", K.ColonToken), (",", K.CommaToken) }) Kinds(text, kind);
        Kinds("// this is a comment", K.LineCommentTrivia);
        Kinds("// this is a comment\n", K.LineCommentTrivia, K.LineBreakTrivia);
        Kinds("/* this is a comment*/", K.BlockCommentTrivia);
        Kinds("/* this is a \r\ncomment*/", K.BlockCommentTrivia);
        Kinds("/* this is a \ncomment*/", K.BlockCommentTrivia);
        Scan("/* this is a", E.UnexpectedEndOfComment, K.BlockCommentTrivia);
        Scan("/* this is a \ncomment", E.UnexpectedEndOfComment, K.BlockCommentTrivia);
        Kinds("/ ttt", K.Unknown, K.Trivia, K.Unknown);
        foreach (var text in new[] { "\"test\"", "\"\\\"\"", "\"\\/\"", "\"\\b\"", "\"\\f\"",
            "\"\\n\"", "\"\\r\"", "\"\\t\"", "\"\u88ff\"", "\"\u200b\u2028\"" }) Kinds(text, K.StringLiteral);
        Scan("\"\\v\"", E.InvalidEscapeCharacter, K.StringLiteral);
        Scan("\"test", E.UnexpectedEndOfString, K.StringLiteral);
        Scan("\"test\n\"", E.UnexpectedEndOfString, K.StringLiteral, K.LineBreakTrivia, K.StringLiteral);
        foreach (var text in new[] { "\"\t\"", "\"\t \"", "\"\0 \"" }) Scan(text, E.InvalidCharacter, K.StringLiteral);
        foreach (var text in new[] { "0", "0.1", "-0.1", "-1", "1", "123456789", "10", "90",
            "90E+123", "90e+123", "90e-123", "90E-123", "90E123", "90e123" }) Kinds(text, K.NumericLiteral);
        Kinds("01", K.NumericLiteral, K.NumericLiteral); Kinds("-01", K.NumericLiteral, K.NumericLiteral);
        Kinds("-", K.Unknown); Kinds(".0", K.Unknown);
        Kinds("true", K.TrueKeyword); Kinds("false", K.FalseKeyword); Kinds("null", K.NullKeyword);
        Kinds("true false null", K.TrueKeyword, K.Trivia, K.FalseKeyword, K.Trivia, K.NullKeyword);
        foreach (var text in new[] { "nulllll", "True", "foo-bar" }) Kinds(text, K.Unknown);
        Kinds("foo bar", K.Unknown, K.Trivia, K.Unknown);
        Kinds("false//hello", K.FalseKeyword, K.LineCommentTrivia);
        Kinds(" ", K.Trivia); Kinds("  \t  ", K.Trivia);
        Kinds("  \t  \n  \t  ", K.Trivia, K.LineBreakTrivia, K.Trivia);
        Kinds("\r\n", K.LineBreakTrivia); Kinds("\r", K.LineBreakTrivia); Kinds("\n", K.LineBreakTrivia);
        Kinds("\n\r", K.LineBreakTrivia, K.LineBreakTrivia);
        Kinds("\n   \n", K.LineBreakTrivia, K.Trivia, K.LineBreakTrivia);
    }

    private static void JsonEdits()
    {
        var options = new JsoncModificationOptions(new(TabSize: 2));
        void Edit(string content, object[] path, object? value, string expected, JsoncModificationOptions? custom = null, bool remove = false) =>
            Case("json/edit " + content + " / " + string.Join("/", path), () =>
            {
                var edits = remove ? JsoncEdit.Remove(content, path, custom ?? options)
                    : JsoncEdit.Modify(content, path, value, custom ?? options);
                AssertEx.Equal(expected, JsoncEdit.ApplyEdits(content, edits), "edited text and edit ordering/bounds");
            });
        Edit("{\n  \"x\": \"y\"\n}", new object[] { "x" }, "bar", "{\n  \"x\": \"bar\"\n}");
        Edit("true", Array.Empty<object>(), "bar", "\"bar\"");
        Edit("{\n  \"x\": \"y\"\n}", new object[] { "x" }, new { key = true }, "{\n  \"x\": {\n    \"key\": true\n  }\n}");
        Edit("{\n  \"a\": \"b\",  \"x\": \"y\"\n}", new object[] { "a" }, null, "{\n  \"a\": null,  \"x\": \"y\"\n}");
        Edit("{}", new object[] { "foo" }, "bar", "{\n  \"foo\": \"bar\"\n}");
        Edit("{}", new object[] { "foo", "foo2" }, "bar", "{\n  \"foo\": {\n    \"foo2\": \"bar\"\n  }\n}");
        Edit("{\n}", new object[] { "foo" }, "bar", "{\n  \"foo\": \"bar\"\n}");
        Edit("  {\n  }", new object[] { "foo" }, "bar", "  {\n    \"foo\": \"bar\"\n  }");
        var content = "{\n  \"x\": \"y\"\n}";
        Edit(content, new object[] { "foo" }, "bar", "{\n  \"x\": \"y\",\n  \"foo\": \"bar\"\n}");
        Edit(content, new object[] { "e" }, "null", "{\n  \"x\": \"y\",\n  \"e\": \"null\"\n}");
        Edit(content, new object[] { "x" }, "bar", "{\n  \"x\": \"bar\"\n}");
        content = "{\n  \"x\": {\n    \"a\": 1,\n    \"b\": true\n  }\n}\n";
        Edit(content, new object[] { "x" }, "bar", "{\n  \"x\": \"bar\"\n}\n");
        Edit(content, new object[] { "x", "b" }, "bar", "{\n  \"x\": {\n    \"a\": 1,\n    \"b\": \"bar\"\n  }\n}\n");
        Edit(content, new object[] { "x", "c" }, "bar", "{\n  \"x\": {\n    \"c\": \"bar\",\n    \"a\": 1,\n    \"b\": true\n  }\n}\n", options with { GetInsertionIndex = _ => 0 });
        Edit(content, new object[] { "x", "c" }, "bar", "{\n  \"x\": {\n    \"a\": 1,\n    \"c\": \"bar\",\n    \"b\": true\n  }\n}\n", options with { GetInsertionIndex = _ => 1 });
        Edit(content, new object[] { "x", "c" }, "bar", "{\n  \"x\": {\n    \"a\": 1,\n    \"b\": true,\n    \"c\": \"bar\"\n  }\n}\n", options with { GetInsertionIndex = _ => 2 });
        Edit(content, new object[] { "c" }, "bar", "{\n  \"x\": {\n    \"a\": 1,\n    \"b\": true\n  },\n  \"c\": \"bar\"\n}\n");
        Edit("{\n  \"a\": [\n    {\n    } \n  ]  \n}", new object[] { "foo" }, "bar", "{\n  \"a\": [\n    {\n    } \n  ],\n  \"foo\": \"bar\"\n}");
        Edit("", new object[] { "foo", 0 }, "bar", "{\n  \"foo\": [\n    \"bar\"\n  ]\n}");
        Edit("//comment", new object[] { "foo", 0 }, "bar", "{\n  \"foo\": [\n    \"bar\"\n  ]\n} //comment");
        Edit("{\n  \"x\": \"y\"\n}", new object[] { "x" }, null, "{\n}", remove: true);
        Edit("{\n  \"x\": \"y\", \"a\": []\n}", new object[] { "x" }, null, "{\n  \"a\": []\n}", remove: true);
        Edit("{\n  \"x\": \"y\", \"a\": []\n}", new object[] { "a" }, null, "{\n  \"x\": \"y\"\n}", remove: true);
        content = "{\n  \"x\": [1, 2, 3],\n  \"y\": 0\n}";
        Edit(content, new object[] { "x", 0 }, 6, "{\n  \"x\": [6, 2, 3],\n  \"y\": 0\n}");
        Edit(content, new object[] { "x", 1 }, 5, "{\n  \"x\": [1, 5, 3],\n  \"y\": 0\n}");
        Edit(content, new object[] { "x", 2 }, 4, "{\n  \"x\": [1, 2, 4],\n  \"y\": 0\n}");
        Edit(content, new object[] { "x", 3 }, 3, "{\n  \"x\": [\n    1,\n    2,\n    3,\n    3\n  ],\n  \"y\": 0\n}");
        Edit("[\n  2,\n  3\n]", new object[] { 0 }, 1, "[\n  1,\n  2,\n  3\n]", options with { IsArrayInsertion = true });
        Edit("[\n]", new object[] { 0 }, 1, "[\n  1\n]");
        Edit("[\n  1,\n  3\n]", new object[] { 1 }, 2, "[\n  1,\n  2,\n  3\n]", options with { IsArrayInsertion = true });
        Edit("[\n]", new object[] { 1 }, 1, "[\n  1\n]");
        Edit("[\n  1,\n  2\n]", new object[] { 2 }, 3, "[\n  1,\n  2,\n  3\n]");
        Edit("[\n]", new object[] { -1 }, "bar", "[\n  \"bar\"\n]");
        Edit("[\n  1,\n  2\n]", new object[] { -1 }, "bar", "[\n  1,\n  2,\n  \"bar\"\n]");
        Edit("[\n  1\n]", new object[] { 0 }, null, "[]", remove: true);
        Edit("[\n  1,\n  2,\n  3\n]", new object[] { 1 }, null, "[\n  1,\n  3\n]", remove: true);
        Edit("[\n  1,\n  2,\n  \"bar\"\n]", new object[] { 2 }, null, "[\n  1,\n  2\n]", remove: true);
        Edit("[\n  1,\n  \"foo\",\n  \"bar\",\n]", new object[] { 2 }, null, "[\n  1,\n  \"foo\"\n]", remove: true);
        Edit("// This is a comment\n[\n  1,\n  \"foo\",\n  \"bar\"\n]", new object[] { 2 }, null, "// This is a comment\n[\n  1,\n  \"foo\"\n]", remove: true);
        Edit(content, new object[] { "x", 0 }, new { a = 1, b = 2 }, "{\n  \"x\": [{\n      \"a\": 1,\n      \"b\": 2\n    }, 2, 3],\n  \"y\": 0\n}");
        Edit(content, new object[] { "x", 0 }, new { a = 1, b = 2 }, "{\n  \"x\": [{\"a\":1,\"b\":2}, 2, 3],\n  \"y\": 0\n}", new());
        // Upstream's final keepLines-named case actually passes ordinary options; retain that assertion.
        Edit("{}", new object[] { "foo", "foo2" }, "bar", "{\n  \"foo\": {\n    \"foo2\": \"bar\"\n  }\n}");
        // Additional host regression, explicitly not an upstream test.
        Edit("{}", new object[] { "foo", "foo2" }, "bar", "{\n  \"foo\": {\n    \"foo2\": \"bar\"\n  }\n}", new(new(TabSize: 2, KeepLines: true)));
    }
}
