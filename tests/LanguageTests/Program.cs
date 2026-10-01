using MonacoNative.Language;
using MonacoNative.Language.BracketPairs;

static class AssertEx
{
    public static void True(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{message}: expected {expected}, got {actual}");
    }
}

sealed class FakeModel : ILanguageTextModel
{
    private readonly List<string> _lines;
    public FakeModel(string languageId, params string[] lines)
    {
        LanguageId = languageId;
        _lines = lines.Length == 0 ? new List<string> { string.Empty } : new List<string>(lines);
    }
    public Uri Uri { get; set; } = new("file:///workspace/test.cpp");
    public string LanguageId { get; set; }
    public ulong VersionId { get; private set; } = 1;
    public int LineCount => _lines.Count;
    public string GetValue() => string.Join("\n", _lines);
    public string GetLineContent(int lineNumber) => _lines[lineNumber - 1];
    public int GetLineMaxColumn(int lineNumber) => _lines[lineNumber - 1].Length + 1;
    public void SetLine(int lineNumber, string value) { _lines[lineNumber - 1] = value; VersionId++; }
}

sealed class CompletionProvider(string label) : ICompletionItemProvider
{
    public IReadOnlyList<string> TriggerCharacters => new[] { "." };
    public ValueTask<CompletionList?> ProvideCompletionItemsAsync(ILanguageTextModel model, LanguagePosition position, CompletionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<CompletionList?>(new CompletionList(new[] { new CompletionItem(label, CompletionItemKind.Keyword) }));
    }
}

sealed class HoverProvider(string text) : IHoverProvider
{
    public ValueTask<Hover?> ProvideHoverAsync(ILanguageTextModel model, LanguagePosition position, CancellationToken cancellationToken) =>
        ValueTask.FromResult<Hover?>(new Hover(new[] { new MarkdownString(text) }));
}

sealed class SignatureProvider : ISignatureHelpProvider
{
    public IReadOnlyList<string> SignatureHelpTriggerCharacters => new[] { "(" };
    public ValueTask<SignatureHelp?> ProvideSignatureHelpAsync(ILanguageTextModel model, LanguagePosition position, SignatureHelpContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult<SignatureHelp?>(new SignatureHelp(new[] { new SignatureInformation("f(x)", Parameters: new[] { new ParameterInformation("x") }) }));
}

sealed class InlayProvider : IInlayHintsProvider
{
    public ValueTask<IReadOnlyList<InlayHint>?> ProvideInlayHintsAsync(ILanguageTextModel model, LanguageRange range, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<InlayHint>?>(new[] { new InlayHint(new LanguagePosition(1, 2), ": int", InlayHintKind.Type, PaddingLeft: true) });
}

sealed class ActionProvider : ICodeActionProvider
{
    public ValueTask<IReadOnlyList<CodeAction>?> ProvideCodeActionsAsync(ILanguageTextModel model, LanguageRange range, CodeActionContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<CodeAction>?>(new[] { new CodeAction("Fix", "quickfix", new[] { new LanguageTextEdit(range, "fixed") }, true) });
}

sealed class ReferenceProvider : IReferenceProvider
{
    public ValueTask<IReadOnlyList<LanguageLocation>?> ProvideReferencesAsync(ILanguageTextModel model, LanguagePosition position, ReferenceContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<LanguageLocation>?>(new[] { new LanguageLocation(model.Uri, new LanguageRange(1, 1, 1, 2)) });
}

sealed class RenameProvider : IRenameProvider
{
    public ValueTask<RenameLocation?> ResolveRenameLocationAsync(ILanguageTextModel model, LanguagePosition position, CancellationToken cancellationToken) =>
        ValueTask.FromResult<RenameLocation?>(new RenameLocation(new LanguageRange(1, 1, 1, 2), "x"));

    public ValueTask<WorkspaceEdit?> ProvideRenameEditsAsync(ILanguageTextModel model, LanguagePosition position, string newName, CancellationToken cancellationToken) =>
        ValueTask.FromResult<WorkspaceEdit?>(new WorkspaceEdit(new[] { new WorkspaceTextEdit(model.Uri, new LanguageRange(1, 1, 1, 2), newName) }));
}

sealed class ColorProvider : IColorProvider
{
    public ValueTask<IReadOnlyList<ColorInformation>?> ProvideDocumentColorsAsync(ILanguageTextModel model, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<ColorInformation>?>(new[] { new ColorInformation(new LanguageRange(1, 1, 1, 2), new LanguageColor(1, 0, 0, 1)) });

    public ValueTask<IReadOnlyList<ColorPresentation>?> ProvideColorPresentationsAsync(ILanguageTextModel model, ColorInformation colorInfo, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<ColorPresentation>?>(new[] { new ColorPresentation("#ff0000", new LanguageTextEdit(colorInfo.Range, "#ff0000")) });
}

sealed class InlineProvider : IInlineCompletionsProvider
{
    public ValueTask<InlineCompletions?> ProvideInlineCompletionsAsync(ILanguageTextModel model, LanguagePosition position, InlineCompletionContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult<InlineCompletions?>(new InlineCompletions(new[] { new InlineCompletion("inline") }));
}

sealed class LensProvider : ICodeLensProvider
{
    public ValueTask<IReadOnlyList<CodeLens>?> ProvideCodeLensesAsync(ILanguageTextModel model, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<CodeLens>?>(new[] { new CodeLens(new LanguageRange(1, 1, 1, 2), new LanguageCommand("test.lens", "Run"), "lens-1") });
}

sealed class DefinitionProvider : IDefinitionProvider
{
    public ValueTask<IReadOnlyList<LanguageLocation>?> ProvideDefinitionAsync(ILanguageTextModel model, LanguagePosition position, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<LanguageLocation>?>(new[] { new LanguageLocation(model.Uri, new LanguageRange(1, 1, 1, 2)) });
}

sealed class FormattingProvider : IDocumentFormattingEditProvider, IDocumentRangeFormattingEditProvider
{
    public ValueTask<IReadOnlyList<LanguageTextEdit>?> ProvideDocumentFormattingEditsAsync(ILanguageTextModel model, FormattingOptions options, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<LanguageTextEdit>?>(new[] { new LanguageTextEdit(new LanguageRange(1, 1, 1, 2), "formatted") });
    public ValueTask<IReadOnlyList<LanguageTextEdit>?> ProvideDocumentRangeFormattingEditsAsync(ILanguageTextModel model, LanguageRange range, FormattingOptions options, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<LanguageTextEdit>?>(new[] { new LanguageTextEdit(range, "range-formatted") });
}

sealed class SemanticProvider : IDocumentSemanticTokensProvider
{
    public SemanticTokensLegend Legend { get; } = new(new[] { "variable" }, new[] { "readonly" });
    public ValueTask<SemanticTokens?> ProvideDocumentSemanticTokensAsync(ILanguageTextModel model, CancellationToken cancellationToken) =>
        ValueTask.FromResult<SemanticTokens?>(new SemanticTokens(new[] { new SemanticToken(1, 1, 1, "variable", new[] { "readonly" }) }, "1"));
}

static class Tests
{
    public static async Task RunAsync()
    {
        LanguageRegistryStacking();
        LanguageConfigurationStacking();
        LanguageSelectorScoring();
        MonarchCasesAndState();
        SampleLanguagesTokenizerSmoke();
        SampleFormattingSmoke();
        IncrementalInvalidation();
        BracketLengthParity();
        SmallImmutableSetParity();
        BracketTokenParity();
        BracketAstAnd23TreeParity();
        BeforeEditPositionMapperParity();
        IncrementalBracketParserParity();
        IncrementalBracketParserLocality();
        BracketPairsTreeTokenAwareParity();
        await ProvidersAsync();
        await ContributionProvidersAsync();
        await IntelligenceProvidersAsync();
        await CancellationAsync();
        Console.WriteLine("LanguageTests: PASS");
    }

    private static void SampleFormattingSmoke()
    {
        var code = "const value = \"{escaped}\";\nfunction demo() {\nconsole.log(value);\n}\n";
        var formatted = MonacoNative.SampleApp.DemoSampleFormattingProvider.Format(code, "javascript");
        AssertEx.True(formatted.Contains("    console.log(value);", StringComparison.Ordinal), "JavaScript indentation");
        AssertEx.True(formatted.Contains("\"{escaped}\"", StringComparison.Ordinal), "Quoted braces preserved");
        AssertEx.Equal(formatted, MonacoNative.SampleApp.DemoSampleFormattingProvider.Format(formatted, "javascript"), "JS formatter idempotent");
        var html = "<!DOCTYPE html>\n<html>\n<body>\n<p>Hi</p>\n</body>\n</html>\n";
        var formattedHtml = MonacoNative.SampleApp.DemoSampleFormattingProvider.Format(html, "html");
        AssertEx.True(formattedHtml.Contains("        <p>Hi</p>", StringComparison.Ordinal), "HTML tag nesting");
        AssertEx.Equal(formattedHtml, MonacoNative.SampleApp.DemoSampleFormattingProvider.Format(formattedHtml, "html"), "HTML formatter idempotent");
        var sharp = MonacoNative.SampleApp.DemoSampleFormattingProvider.Format("class C {\npublic:\nvoid M() {\nvar text = \"}\";\n}\n}\n", "csharp");
        AssertEx.True(sharp.Contains("        var text =", StringComparison.Ordinal), "C# indentation with string braces");
        var cpp = MonacoNative.SampleApp.DemoSampleFormattingProvider.Format("#include <vector>\nint main() {\nreturn 0;\n}", "cpp");
        AssertEx.True(cpp.Contains("    return 0;", StringComparison.Ordinal), "C++ formatting");
    }

    private static void LanguageRegistryStacking()
    {
        var service = new MonacoLanguageService(registerBuiltins: false);
        var encountered = 0;
        service.Languages.LanguageEncountered += (_, id) => { if (id == "x") encountered++; };
        using var a = service.Register(new LanguageExtensionPoint("x", Aliases: new[] { "X" }));
        using (var b = service.Register(new LanguageExtensionPoint("x", Aliases: new[] { "X2" })))
            AssertEx.Equal("X2", service.GetLanguages().Single().Aliases!.Single(), "latest language registration should win");
        AssertEx.Equal("X", service.GetLanguages().Single().Aliases!.Single(), "disposing override should restore previous language");
        service.Languages.Encounter("x");
        service.Languages.Encounter("x");
        AssertEx.Equal(1, encountered, "onLanguageEncountered should fire once per language");
    }

    private static void LanguageConfigurationStacking()
    {
        var service = new MonacoLanguageService(registerBuiltins: false);
        using var baseConfig = service.SetLanguageConfiguration("x", new LanguageConfiguration { Comments = new CommentRule("//") });
        using (var top = service.SetLanguageConfiguration("x", new LanguageConfiguration { Comments = new CommentRule("#") }))
            AssertEx.Equal("#", service.Languages.GetLanguageConfiguration("x")!.Comments!.LineComment, "latest language configuration should win");
        AssertEx.Equal("//", service.Languages.GetLanguageConfiguration("x")!.Comments!.LineComment, "configuration disposal should restore prior value");
    }

    private static void LanguageSelectorScoring()
    {
        var uri = new Uri("file:///workspace/src/test.cpp");
        var exact = LanguageSelectorScore.Score(new LanguageSelector("cpp", "file", "**/*.cpp"), uri, "cpp");
        var wildcard = LanguageSelectorScore.Score(new LanguageSelector("*", "*", "**/*"), uri, "cpp");
        var exclusive = LanguageSelectorScore.Score(new LanguageSelector("cpp", Exclusive: true), uri, "cpp");
        AssertEx.True(exact > wildcard, "exact selector should outrank wildcard selector");
        AssertEx.True(exclusive > exact, "exclusive selector should outrank non-exclusive selector");
        AssertEx.Equal(0, LanguageSelectorScore.Score(new LanguageSelector("json"), uri, "cpp"), "language mismatch should score zero");
    }

    private static void MonarchCasesAndState()
    {
        var tokenizer = new MonarchTokenizer(BuiltinLanguages.CreateCppMonarch());
        var first = tokenizer.TokenizeLine("int value = 1; /* comment");
        AssertEx.True(first.Tokens.Any(t => t.Type == "keyword"), "keyword cases should resolve through @keywords");
        AssertEx.Equal("comment", first.EndState.Current, "multiline comment should preserve end state");
        var second = tokenizer.TokenizeLine("continued */ return value;", first.EndState);
        AssertEx.Equal("root", second.EndState.Current, "@pop should restore previous tokenizer state");
        AssertEx.True(second.Tokens.Any(t => t.Type == "keyword"), "tokenization should resume after multiline comment");

        var json = new MonarchTokenizer(BuiltinLanguages.CreateJsonMonarch(false));
        var jsonLine = json.TokenizeLine("{\"ok\":true,\"n\":12}");
        AssertEx.True(jsonLine.Tokens.Any(t => t.Type == "string"), "JSON string content should tokenize");
        AssertEx.True(jsonLine.Tokens.Any(t => t.Type == "keyword"), "JSON keywords should tokenize");
        AssertEx.True(jsonLine.Tokens.Any(t => t.Type == "number"), "JSON numbers should tokenize");
    }

    private static void SampleLanguagesTokenizerSmoke()
    {
        var service = new MonacoLanguageService();
        var cases = new[]
        {
            ("html", "<main class=\"card\">Hello</main>", "keyword"),
            ("javascript", "const number = 42; // note", "keyword"),
            ("csharp", "public static void Main() { }", "keyword"),
            ("cpp", "int main() { return 0; }", "keyword"),
        };
        foreach (var (language, line, expectedToken) in cases)
        {
            AssertEx.True(service.Languages.GetLanguageConfiguration(language) is not null,
                $"{language}: language configuration not registered");
            AssertEx.True(service.Languages.TryGetMonarchTokenizer(language, out var tokenizer, out _),
                $"{language}: lexical tokenizer not registered");
            var parsed = tokenizer!.TokenizeLine(line);
            AssertEx.True(parsed.Tokens.Any(t => t.Type == expectedToken),
                $"{language}: expected {expectedToken} token");
        }
        var js = service.Languages.TryGetMonarchTokenizer("javascript", out var jsLexer, out _);
        AssertEx.True(js && jsLexer!.TokenizeLine("// comment").Tokens.Any(t => t.Type == "comment"),
            "JavaScript: comments should be highlighted");
    }

    private static void IncrementalInvalidation()
    {
        var service = new MonacoLanguageService();
        var model = new FakeModel("cpp", "/* start", "middle", "*/ int x;");
        var cache = new IncrementalTokenizationCache(model, service);
        AssertEx.Equal("root", cache.GetLineTokens(3).EndState.Current, "initial state should close multiline comment");
        model.SetLine(1, "int first;");
        cache.InvalidateFrom(1);
        var line2 = cache.GetLineTokens(2);
        AssertEx.True(line2.Tokens.All(t => t.Type != "comment"), "invalidating from edit line should recompute downstream state");
    }


    private static void BracketLengthParity()
    {
        var a = BracketLength.From(10, 5);
        var zeroLine = BracketLength.From(0, 3);
        var multiLine = BracketLength.From(20, 3);
        var sameLineSum = a + zeroLine;
        AssertEx.Equal(10, sameLineSum.LineCount, "zero-line length addition should preserve line count");
        AssertEx.Equal(8, sameLineSum.ColumnCount, "zero-line length addition should add columns");
        var multiLineSum = a + multiLine;
        AssertEx.Equal(30, multiLineSum.LineCount, "multi-line length addition should add line counts");
        AssertEx.Equal(3, multiLineSum.ColumnCount, "multi-line length addition should take the right-hand final column");
        AssertEx.Equal(BracketLength.From(2, 3), BracketLength.OfString("a\nbb\nccc"), "lengthOfString should mirror Monaco packed length semantics");
        AssertEx.True(BracketLength.From(1, 0) > BracketLength.From(0, 1000), "packed ordering must compare line count before column count");
    }

    private static void SmallImmutableSetParity()
    {
        var provider = new DenseKeyProvider<string>(StringComparer.Ordinal);
        var a = SmallImmutableSet<string>.GetEmpty().Add("a", provider).Add("b", provider);
        var b = SmallImmutableSet<string>.GetEmpty().Add("b", provider).Add("c", provider);
        AssertEx.True(a.Intersects(b), "sets sharing a dense key must intersect");
        var merged = a.Merge(b);
        AssertEx.True(merged.Has("a", provider) && merged.Has("b", provider) && merged.Has("c", provider), "merge should preserve all dense keys");

        // Exercise the additional 32-bit words used by Monaco when more than 32 bracket ids exist.
        var many = SmallImmutableSet<int>.GetEmpty();
        for (var i = 0; i < 70; i++)
            many = many.Add(i, IdentityDenseKeyProvider.Instance);
        AssertEx.True(many.Has(0, IdentityDenseKeyProvider.Instance) && many.Has(69, IdentityDenseKeyProvider.Instance), "dense set must support ids beyond the inline word");
    }

    private static void BracketTokenParity()
    {
        var provider = new DenseKeyProvider<string>(StringComparer.Ordinal);
        var config = new LanguageConfiguration
        {
            Brackets = new[]
            {
                new CharacterPair("{", "}"),
                new CharacterPair("begin", "end"),
            }
        };
        var tokens = BracketTokens.CreateFromLanguage("x", config, provider);
        var open = tokens.GetToken("{")!;
        var close = tokens.GetToken("}")!;
        AssertEx.Equal(BracketTreeTokenKind.OpeningBracket, open.Kind, "opening token kind");
        AssertEx.Equal(BracketTreeTokenKind.ClosingBracket, close.Kind, "closing token kind");
        AssertEx.True(close.BracketIds.Has(open.BracketId, IdentityDenseKeyProvider.Instance), "closing token should contain the opening bracket id it closes");
        var pattern = tokens.GetRegexPattern()!;
        AssertEx.True(pattern.Contains("\\bbegin", StringComparison.Ordinal) && pattern.Contains("end\\b", StringComparison.Ordinal), "word-like brackets need Monaco-style regex boundaries");
        AssertEx.True(tokens.GlobalRegex!.IsMatch("begin value end"), "compiled bracket regex should match configured word brackets");

        var agnostic = new LanguageAgnosticBracketTokens(id => id == "x" ? config : null);
        AssertEx.True(!agnostic.DidLanguageChange("x"), "language should be lazy until first bracket request");
        _ = agnostic.GetSingleLanguageBracketTokens("x");
        AssertEx.True(agnostic.DidLanguageChange("x"), "requested language should participate in configuration invalidation");
    }

    private static void BracketAstAnd23TreeParity()
    {
        static TextAstNode Text(string value) => new(BracketLength.OfString(value));

        var a = Text("a");
        var b = Text("b");
        var c = Text("c");
        var d = Text("d");
        var e = Text("e");
        var sameHeight = Concat23Trees.ConcatSameHeight(new AstNode[] { a, b, c, d, e }, createImmutableLists: true)!;
        AssertEx.Equal(2, sameHeight.ListHeight, "five leaves should build a height-2 Monaco (2,3) tree");
        AssertEx.Equal(5, ((ListAstNode)sameHeight.FlattenLists()).ChildrenLength, "flattening a (2,3) tree should preserve leaf count");
        AssertEx.Equal(BracketLength.From(0, 5), sameHeight.Length, "(2,3) tree length should equal concatenated child length");

        var leftTall = ListAstNode.Create23(Text("x"), Text("y"), null, immutable: true);
        var mixed = Concat23Trees.Concat(new AstNode[] { Text("a"), leftTall, Text("b") })!;
        AssertEx.Equal(BracketLength.From(0, 4), mixed.Length, "mixed-height concat should preserve total length");
        AssertEx.Equal(4, ((ListAstNode)mixed.FlattenLists()).ChildrenLength, "mixed-height concat should preserve document order and all leaves");
    }

    private static void BeforeEditPositionMapperParity()
    {
        var mapper = new BeforeEditPositionMapper(new[]
        {
            new TextEditInfo(BracketLength.From(1, 3), BracketLength.From(1, 5), BracketLength.From(0, 4))
        });

        AssertEx.Equal(BracketLength.From(0, 3), mapper.GetDistanceToNextChange(BracketLength.From(1, 0))!.Value,
            "distance to next edit must be expressed in current-document coordinates");
        AssertEx.Equal(BracketLength.From(1, 5), mapper.GetOffsetBeforeChange(BracketLength.From(1, 7)),
            "after an edit, current offsets must map back to the old AST coordinate space");
    }

    private static void IncrementalBracketParserParity()
    {
        static SmallImmutableSet<int> IdSet(int id) => SmallImmutableSet<int>.GetEmpty().Add(id, IdentityDenseKeyProvider.Instance);
        static BracketParserToken Text(string text)
        {
            var length = BracketLength.OfString(text);
            return new BracketParserToken(length, BracketTreeTokenKind.Text, -1, SmallImmutableSet<int>.GetEmpty(), new TextAstNode(length));
        }
        static BracketParserToken Open(string text, int id)
        {
            var length = BracketLength.OfString(text);
            var ids = IdSet(id);
            return new BracketParserToken(length, BracketTreeTokenKind.OpeningBracket, id, ids, BracketAstNode.Create(length, text, "x", ids));
        }
        static BracketParserToken Close(string text, int id)
        {
            var length = BracketLength.OfString(text);
            var ids = IdSet(id);
            return new BracketParserToken(length, BracketTreeTokenKind.ClosingBracket, id, ids, BracketAstNode.Create(length, text, "x", ids));
        }

        var oldTokens = new[] { Text("a"), Open("{", 0), Text("x"), Close("}", 0), Text("b") };
        var initial = BracketParser.ParseDocument(new ArrayBracketTreeTokenizer(oldTokens), Array.Empty<TextEditInfo>(), oldNode: null, createImmutableLists: true);
        AssertEx.Equal(0, initial.NodesReused, "initial immutable parse must not report reused nodes");
        AssertEx.True(initial.NodesConstructed > 0, "initial parse must construct AST nodes");

        var oldFlat = (ListAstNode)initial.Root.FlattenLists();
        var oldFirst = oldFlat.GetChild(0)!;
        var oldLast = oldFlat.GetChild(oldFlat.ChildrenLength - 1)!;

        // Replace the one-code-unit text inside the pair with two code units. Prefix/suffix are untouched.
        var newTokens = new[] { Text("a"), Open("{", 0), Text("yz"), Close("}", 0), Text("b") };
        var edits = new[]
        {
            new TextEditInfo(BracketLength.From(0, 2), BracketLength.From(0, 3), BracketLength.From(0, 2))
        };
        var incremental = BracketParser.ParseDocument(new ArrayBracketTreeTokenizer(newTokens), edits, initial.Root, createImmutableLists: false);
        AssertEx.True(incremental.NodesReused >= 2, "incremental parse should reuse untouched prefix/suffix nodes from the old AST");
        AssertEx.True(incremental.NodesConstructed < initial.NodesConstructed, "localized edit should construct fewer nodes than a full parse");
        AssertEx.Equal(BracketLength.From(0, 6), incremental.Root.Length, "incremental AST must have the new document length");

        var newFlat = (ListAstNode)incremental.Root.FlattenLists();
        AssertEx.True(ReferenceEquals(oldFirst, newFlat.GetChild(0)), "untouched prefix AST node should be reused by identity");
        AssertEx.True(ReferenceEquals(oldLast, newFlat.GetChild(newFlat.ChildrenLength - 1)), "untouched suffix AST node should be reused by identity");
        var pair = newFlat.Children.OfType<PairAstNode>().Single();
        AssertEx.Equal(BracketLength.From(0, 2), pair.Child!.Length, "edited pair child must reflect the replacement length");
        AssertEx.True(!ReferenceEquals(((ListAstNode)initial.Root.FlattenLists()).Children.OfType<PairAstNode>().Single(), pair),
            "node touching the edit must not be reused");
    }

    private static void IncrementalBracketParserLocality()
    {
        static BracketParserToken Text(string text)
        {
            var length = BracketLength.OfString(text);
            return new BracketParserToken(length, BracketTreeTokenKind.Text, -1, SmallImmutableSet<int>.GetEmpty(), new TextAstNode(length));
        }

        const int count = 512;
        var oldTokens = Enumerable.Range(0, count).Select(_ => Text("x")).ToArray();
        var initial = BracketParser.ParseDocument(new ArrayBracketTreeTokenizer(oldTokens), Array.Empty<TextEditInfo>(), null, createImmutableLists: true);
        AssertEx.True(initial.NodesConstructed >= count, "full initial parse should construct the token AST leaves");

        var newTokens = Enumerable.Range(0, count).Select(i => Text(i == count / 2 ? "y" : "x")).ToArray();
        var edit = new TextEditInfo(
            BracketLength.From(0, count / 2),
            BracketLength.From(0, count / 2 + 1),
            BracketLength.From(0, 1));
        var incremental = BracketParser.ParseDocument(new ArrayBracketTreeTokenizer(newTokens), new[] { edit }, initial.Root, createImmutableLists: false);

        AssertEx.True(incremental.NodesReused > 0, "large local edit must reuse old balanced-tree subtrees");
        AssertEx.True(incremental.NodesConstructed < 32,
            "Monaco-style old-AST reuse should keep constructed parser nodes bounded around a one-token edit, not proportional to document size");
        AssertEx.Equal(initial.Root.Length, incremental.Root.Length, "same-length local edit must preserve total document length");
    }

    private static void BracketPairsTreeTokenAwareParity()
    {
        var service = new MonacoLanguageService(registerBuiltins: true);
        var line = "auto s = \"{ ignored }\"; if (x) { return a[0]; }";
        var model = new FakeModel("cpp", line);
        var cache = new IncrementalTokenizationCache(model, service);
        var tree = new BracketPairsTree(model, cache, service.Languages);

        var fullRange = new LanguageRange(1, 1, 1, line.Length + 1);
        var fast = tree.GetBracketsInRange(fullRange, onlyColorizedBrackets: true);
        tree.CompleteTokenization();
        var semantic = tree.GetBracketsInRange(fullRange, onlyColorizedBrackets: true);
        AssertEx.True(semantic.Count < fast.Count, "token-aware tree must exclude bracket text inside strings/comments");

        var stringOpen = line.IndexOf("{ ignored", StringComparison.Ordinal) + 1;
        AssertEx.True(!semantic.Any(b => b.Range.StartColumn == stringOpen), "string bracket must not survive semantic bracket tokenization");

        var codeOpen = line.IndexOf("{ return", StringComparison.Ordinal) + 1;
        var match = tree.FindMatchingBracket(new LanguagePosition(1, codeOpen));
        AssertEx.True(match is not null, "token-aware pair tree should match the code block bracket");
        AssertEx.Equal(codeOpen, match!.Bracket.StartColumn, "match should report the bracket under the cursor");
        AssertEx.True(match.Match.StartColumn > codeOpen, "matching close bracket should be after the opening bracket");

        // Verify Monaco-style token-change propagation: changing // to /* changes token metadata
        // on later lines even though their text did not change. The tree must not reuse those
        // stale bracket nodes and should stop invalidating once Monarch end-state stabilizes.
        var propagationModel = new FakeModel("cpp",
            "int x = 0;",
            "//",
            "{ active_before_edit }",
            "*/",
            "if (x) { y(); }");
        var propagationCache = new IncrementalTokenizationCache(propagationModel, service);
        var propagationTree = new BracketPairsTree(propagationModel, propagationCache, service.Languages);
        propagationTree.CompleteTokenization();
        AssertEx.True(propagationTree.GetBracketsInRange(new LanguageRange(3, 1, 3, propagationModel.GetLineMaxColumn(3))).Count > 0,
            "line 3 should initially contain active brackets");

        propagationModel.SetLine(2, "/*");
        propagationCache.ApplyModelChange(2, 2, 2);
        propagationTree.HandleContentChanged(
            new TextEditInfo(BracketLength.From(1, 0), BracketLength.From(1, 2), BracketLength.From(0, 2)),
            fullInvalidation: false);
        AssertEx.Equal(0,
            propagationTree.GetBracketsInRange(new LanguageRange(3, 1, 3, propagationModel.GetLineMaxColumn(3))).Count,
            "brackets on a line newly covered by a block comment must be removed by token-change invalidation");
    }

    private static async Task ProvidersAsync()
    {
        var service = new MonacoLanguageService(registerBuiltins: false);
        var model = new FakeModel("cpp", "x");
        using var low = service.RegisterCompletionItemProvider(new LanguageSelector("*"), new CompletionProvider("wildcard"));
        using var exact = service.RegisterCompletionItemProvider(new LanguageSelector("cpp"), new CompletionProvider("exact"));
        using var hoverA = service.RegisterHoverProvider(new LanguageSelector("cpp"), new HoverProvider("A"));
        using var hoverB = service.RegisterHoverProvider(new LanguageSelector("cpp"), new HoverProvider("B"));
        using var signature = service.RegisterSignatureHelpProvider(new LanguageSelector("cpp"), new SignatureProvider());
        using var inlay = service.RegisterInlayHintsProvider(new LanguageSelector("cpp"), new InlayProvider());
        using var actions = service.RegisterCodeActionProvider(new LanguageSelector("cpp"), new ActionProvider());

        var completions = await service.ProvideCompletionItemsAsync(model, new LanguagePosition(1, 1), new CompletionContext());
        AssertEx.Equal("exact", completions.Suggestions[0].Label, "higher selector score should be queried first");
        AssertEx.Equal(2, completions.Suggestions.Count, "completion lists should aggregate providers");
        var hover = await service.ProvideHoverAsync(model, new LanguagePosition(1, 1));
        AssertEx.Equal(2, hover!.Contents.Count, "hover contents should aggregate providers");
        AssertEx.True(await service.ProvideSignatureHelpAsync(model, new LanguagePosition(1, 1), new SignatureHelpContext()) is not null, "signature provider should resolve");
        AssertEx.Equal(1, (await service.ProvideInlayHintsAsync(model, new LanguageRange(1, 1, 1, 2))).Count, "inlay provider should resolve");
        AssertEx.Equal(1, (await service.ProvideCodeActionsAsync(model, new LanguageRange(1, 1, 1, 2), new CodeActionContext())).Count, "code action provider should resolve");
    }

    private static async Task ContributionProvidersAsync()
    {
        var service = new MonacoLanguageService(registerBuiltins: false);
        var model = new FakeModel("cpp", "x");
        using var references = service.RegisterReferenceProvider(new LanguageSelector("cpp"), new ReferenceProvider());
        using var rename = service.RegisterRenameProvider(new LanguageSelector("cpp"), new RenameProvider());
        using var color = service.RegisterColorProvider(new LanguageSelector("cpp"), new ColorProvider());
        using var inline = service.RegisterInlineCompletionsProvider(new LanguageSelector("cpp"), new InlineProvider());
        using var lens = service.RegisterCodeLensProvider(new LanguageSelector("cpp"), new LensProvider());

        AssertEx.Equal(1, (await service.ProvideReferencesAsync(model, new LanguagePosition(1, 1), new ReferenceContext())).Count, "reference provider should resolve");
        AssertEx.Equal("x", (await service.ResolveRenameLocationAsync(model, new LanguagePosition(1, 1)))!.Text, "rename location should resolve");
        AssertEx.Equal("renamed", (await service.ProvideRenameEditsAsync(model, new LanguagePosition(1, 1), "renamed"))!.Edits.Single().Text, "rename edits should resolve");
        var colors = await service.ProvideDocumentColorsAsync(model);
        AssertEx.Equal(1, colors.Count, "document colors should aggregate");
        AssertEx.Equal("#ff0000", (await service.ProvideColorPresentationsAsync(model, colors[0])).Single().Label, "color presentation should resolve");
        AssertEx.Equal("inline", (await service.ProvideInlineCompletionsAsync(model, new LanguagePosition(1, 1), new InlineCompletionContext(InlineCompletionTriggerKind.Explicit))).Items.Single().InsertText, "inline completion should resolve");
        AssertEx.Equal("Run", (await service.ProvideCodeLensesAsync(model)).Single().Command!.Title, "code lens should resolve");
    }

    private static async Task IntelligenceProvidersAsync()
    {
        var service = new MonacoLanguageService(registerBuiltins: false);
        var model = new FakeModel("cpp", "x");
        using var definition = service.RegisterDefinitionProvider(new LanguageSelector("cpp"), new DefinitionProvider());
        var formattingProvider = new FormattingProvider();
        using var formatting = service.RegisterDocumentFormattingEditProvider(new LanguageSelector("cpp"), formattingProvider);
        using var rangeFormatting = service.RegisterDocumentRangeFormattingEditProvider(new LanguageSelector("cpp"), formattingProvider);
        using var semantic = service.RegisterDocumentSemanticTokensProvider(new LanguageSelector("cpp"), new SemanticProvider());

        AssertEx.Equal(1, (await service.ProvideDefinitionAsync(model, new LanguagePosition(1, 1))).Count, "definition provider should resolve");
        AssertEx.Equal("formatted", (await service.ProvideDocumentFormattingEditsAsync(model, new FormattingOptions())).Single().Text, "document formatting should resolve");
        AssertEx.Equal("range-formatted", (await service.ProvideDocumentRangeFormattingEditsAsync(model, new LanguageRange(1, 1, 1, 2), new FormattingOptions())).Single().Text, "range formatting should resolve");
        AssertEx.Equal("variable", (await service.ProvideDocumentSemanticTokensAsync(model))!.Tokens.Single().TokenType, "semantic token provider should resolve");

        var changed = 0;
        service.DiagnosticsChanged += (_, e) => { if (Equals(e.Uri, model.Uri)) changed++; };
        service.SetDiagnostics(model.Uri, new[] { new LanguageDiagnostic(new LanguageRange(1, 1, 1, 2), "warning", DiagnosticSeverity.Warning) }, 1);
        AssertEx.Equal(1, service.GetDiagnostics(model.Uri).Count, "diagnostics should be stored per URI");
        service.ClearDiagnostics(model.Uri);
        AssertEx.Equal(0, service.GetDiagnostics(model.Uri).Count, "diagnostics should clear per URI");
        AssertEx.Equal(2, changed, "diagnostics change event should fire for set and clear");
    }

    private static async Task CancellationAsync()
    {
        var service = new MonacoLanguageService(registerBuiltins: false);
        var model = new FakeModel("cpp", "x");
        using var registration = service.RegisterCompletionItemProvider(new LanguageSelector("cpp"), new CompletionProvider("x"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try
        {
            _ = await service.ProvideCompletionItemsAsync(model, new LanguagePosition(1, 1), new CompletionContext(), cts.Token);
            throw new Exception("cancelled provider request should throw");
        }
        catch (OperationCanceledException) { }
    }
}

internal static class Program
{
    public static async Task<int> Main()
    {
        await Tests.RunAsync();
        return 0;
    }
}
