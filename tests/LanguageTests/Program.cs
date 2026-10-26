using MonacoNative.Lsp;
using MonacoNative.Language;
using MonacoNative.Language.BracketPairs;
using MonacoNative.Language.Cpp;
using MonacoNative.Language.CSharp;
using MonacoNative.Language.Html;
using MonacoNative.Language.Json;
using MonacoNative.Language.TypeScript;
using MonacoNative.Language.Css;

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
    private static void IntegrationRequirementsRegression()
    {
        var optional = IntegrationRequirements.Parse(Array.Empty<string>());
        optional.Validate(_ => null, _ => false);
        AssertEx.True(!optional.Native && !optional.LibClang, "unit-only execution may skip optional libraries");
        var native = IntegrationRequirements.Parse(new[] { "--require-native" });
        AssertEx.True(native.Native && !native.LibClang, "native-only acceptance mode");
        var both = IntegrationRequirements.Parse(new[] { "--require-libclang" });
        AssertEx.True(both.Native && both.LibClang, "Clang integration also requires Core.Native");
        var failures = 0;
        try { native.Validate(_ => null, _ => true); } catch (InvalidOperationException) { failures++; }
        try { native.Validate(_ => "   ", _ => true); } catch (InvalidOperationException) { failures++; }
        try { native.Validate(_ => "missing.dll", _ => false); } catch (InvalidOperationException) { failures++; }
        try { both.Validate(key => key == "MONACONATIVE_NATIVE_LIBRARY" ? "core.dll" : null, _ => true); }
        catch (InvalidOperationException) { failures++; }
        AssertEx.Equal(4, failures, "required dependencies cannot silently turn into SKIP");
        both.Validate(key => key == "MONACONATIVE_NATIVE_LIBRARY" ? "core.dll" : "libclang.dll", _ => true);
        var badFlag = false;
        try { _ = IntegrationRequirements.Parse(new[] { "--require-natvie" }); }
        catch (ArgumentException) { badFlag = true; }
        AssertEx.True(badFlag, "a misspelled acceptance flag cannot weaken the run");
    }

    private static void SnippetSessionRegression()
    {
        var expansion = SnippetParser.Parse("for (var ${1:i} = ${2:0}; $1 < ${3:10}; $1++) {$0}");
        AssertEx.Equal("for (var i = 0; i < 10; i++) {}", expansion.Text, "snippet visible expansion");
        var text = "// prefix\n" + expansion.Text;
        var session = new SnippetSession(expansion, "// prefix\n".Length);
        AssertEx.Equal(1, session.Current!.Index, "first numeric tabstop");
        AssertEx.Equal("i", text.Substring(session.Current.Start, session.Current.Length), "placeholder selected as a range");
        void ReplaceActive(string replacement)
        {
            var current = session.Current!;
            var start = current.Start; var length = current.Length;
            AssertEx.True(session.ObserveEdit(start, length, replacement), "active placeholder edit accepted");
            text = text.Remove(start, length).Insert(start, replacement);
            foreach (var group in session.LinkedGroups)
                foreach (var edit in session.GetLinkedEdits(group, (offset, count) => text.Substring(offset, count)))
                {
                    session.ApplyLinkedEdit(edit);
                    text = text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Text);
                }
        }
        ReplaceActive("index");
        AssertEx.True(text.Contains("var index = 0; index < 10; index++", StringComparison.Ordinal), "same-number fields mirror after length change");
        AssertEx.Equal(2, session.Move(false)!.Index, "Tab moves to second field");
        ReplaceActive("100");
        AssertEx.Equal(1, session.Move(true)!.Index, "Shift+Tab moves backwards");
        AssertEx.Equal("index", text.Substring(session.Current!.Start, session.Current.Length), "prior placeholder offsets survived later edit");
        session.Move(false);
        AssertEx.Equal(3, session.Move(false)!.Index, "Tab moves in numeric rather than textual order");
        AssertEx.Equal(0, session.Move(false)!.Index, "final stop is last");
        AssertEx.True(session.Move(false) is null && !session.IsActive, "end of snippet exits");

        var order = new SnippetSession(SnippetParser.Parse("${3:c} ${1:a} ${2:b}$0"), 0);
        AssertEx.Equal(1, order.Current!.Index, "nontextual first field");
        AssertEx.Equal(1, order.Move(true)!.Index, "backwards at first stays on first");
        AssertEx.Equal(2, order.Move(false)!.Index, "numeric next");
        AssertEx.True(!order.ObserveEdit(100, 0, "x") && !order.IsActive, "unrelated edit cancels session safely");

        var nested = new SnippetSession(SnippetParser.Parse("${1:outer ${2:inner}} $1 $0"), 0);
        AssertEx.True(nested.ObserveEdit(nested.Current!.Start, nested.Current.Length, "replacement"), "parent replacement");
        AssertEx.True(nested.Placeholders.All(p => p.Index != 2), "replaced nested fields removed");
        AssertEx.Equal(0, nested.Move(false)!.Index, "removed children not visited");
        var forward = SnippetParser.Parse("$1 ${1:name}");
        AssertEx.Equal("name name", forward.Text, "forward references receive placeholder default");
        var choices = SnippetParser.Parse(@"${1|red\,blue,green\|gold|} \$ ${2:x}$0");
        AssertEx.Equal("red,blue $ x", choices.Text, "choice and dollar escapes");
        AssertEx.Equal("green|gold", choices.Placeholders.First(p => p.Index == 1).Choices[1], "all choices retained");
        var variables = SnippetParser.Parse("${TM_FILENAME} ${UNSET:default} $UNKNOWN", new Dictionary<string, string> { ["TM_FILENAME"] = "demo.cs" });
        AssertEx.Equal("demo.cs default UNKNOWN", variables.Text, "known/default/unknown variables");
        AssertEx.True(variables.Placeholders.Any(p => p.Index > 0 && p.Length == 7), "unknown variable becomes editable");
        var transformed = SnippetParser.Parse("${1:hello} ${1/(.*)/${1:/upcase}/}$0");
        AssertEx.Equal("hello HELLO", transformed.Text, "placeholder transform initial value");
        AssertEx.Equal("WORLD", transformed.Placeholders.First(p => p.Transform is not null).Transform!.Apply("world"), "transform after edit");
        AssertEx.Equal("literal", new SnippetTransform("[", "$1", "").Apply("literal"), "invalid regex degrades safely");
        var utf16 = SnippetParser.Parse("😀${1:x}$0");
        AssertEx.Equal<int?>(2, utf16.InitialOffset, "snippet offsets are UTF-16, not Unicode scalar indices");
        AssertEx.Equal(3, new SnippetSession(SnippetParser.Parse("abc$0"), 0).Current!.Start, "final-only snippet");
    }

    private static void CompletionRankingRegression()
    {
        var exact = CompletionFuzzyScore.Match("WriteLine", "WriteLine");
        var abbreviated = CompletionFuzzyScore.Match("WL", "WriteLine");
        AssertEx.True(exact is not null && abbreviated is not null, "exact and camel case match");
        AssertEx.True(abbreviated!.Matches.SequenceEqual(new[] { 0, 5 }), "camel case highlight positions");
        AssertEx.True(CompletionFuzzyScore.Match("zzz", "WriteLine") is null, "unrelated candidate removed");
        AssertEx.True(CompletionFuzzyScore.Match("ine", "WriteLine") is null, "weak first letter rejected by default");
        AssertEx.True(CompletionFuzzyScore.Match("ine", "WriteLine", firstMatchCanBeWeak: true) is not null, "weak match option");
        AssertEx.True(CompletionFuzzyScore.Match("WriteLnie", "WriteLine", graceful: true) is not null, "adjacent transposition fallback");
        var ranking = new CompletionRanking();
        var items = new[]
        {
            new CompletionItem("Z", SortText: "02", FilterText: "WriteLine"),
            new CompletionItem("A", SortText: "01", FilterText: "WriteLog", Preselect: true),
            new CompletionItem("unrelated", SortText: "00"),
        };
        AssertEx.Equal("unrelated", ranking.Rank(items, "", 1)[0].Item.Label, "empty prefix preserves server sortText order");
        var filtered = ranking.Rank(items, "W", 1);
        AssertEx.Equal(2, filtered.Count, "filterText, not decorated label, determines inclusion");
        AssertEx.Equal("A", filtered[ranking.Select(filtered, "csharp", "W")].Item.Label, "preselect within best-score group");
        var rangeItem = new CompletionItem("WriteLine", CompletionItemKind.Method, InsertRange: new LanguageRange(1, 9, 1, 11));
        AssertEx.Equal(1, ranking.Rank(new[] { rangeItem }, "Console.Wr", 1).Count, "per-item range excludes receiver prefix");
        var duplicates = new[] { new CompletionItem("same", Detail: "first"), new CompletionItem("same", Detail: "second") };
        AssertEx.Equal("first", ranking.Rank(duplicates, "", 1)[0].Item.Detail!, "stable equal-key sorting");
        var normal = new CompletionItem("WriteLine", CompletionItemKind.Method);
        var other = new CompletionItem("WriteLog", CompletionItemKind.Method);
        var snippet = new CompletionItem("while", CompletionItemKind.Snippet, SnippetText: "while (${1:true}) {$0}");
        ranking.Options.Snippets = SnippetSortOrder.Top;
        AssertEx.Equal(CompletionItemKind.Snippet, ranking.Rank(new[] { normal, snippet }, "", 1)[0].Item.Kind, "snippet top policy");
        ranking.Options.Snippets = SnippetSortOrder.Hidden;
        AssertEx.Equal(1, ranking.Rank(new[] { normal, snippet }, "", 1).Count, "hidden snippets excluded");
        ranking.Options.Snippets = SnippetSortOrder.Inline;
        ranking.Options.Selection = SuggestSelectionMode.RecentlyUsed;
        ranking.Remember("csharp", "W", other);
        var candidates = ranking.Rank(new[] { normal, other }, "", 1);
        AssertEx.Equal("WriteLog", candidates[ranking.Select(candidates, "csharp", "")].Item.Label, "MRU preselection without reordering");
        ranking.Options.Selection = SuggestSelectionMode.RecentlyUsedByPrefix;
        ranking.Remember("csharp", "W", other);
        candidates = ranking.Rank(new[] { normal, other }, "Wri", 1);
        AssertEx.Equal("WriteLog", candidates[ranking.Select(candidates, "csharp", "Wri")].Item.Label, "longest stored prefix, not exact key only");
        ranking.Options.Selection = SuggestSelectionMode.First;
        AssertEx.Equal(0, ranking.Select(candidates, "csharp", "Wri"), "first selection ignores remembered choices");
    }

    private static (FakeModel Model, LanguagePosition Position) Marked(string language, string text)
    {
        var caret = text.IndexOf('|');
        AssertEx.True(caret >= 0, "test marker is present");
        var source = text.Remove(caret, 1);
        var model = new FakeModel(language, source.Split('\n')) { Uri = new Uri("file:///workspace/native-test." + (language == "csharp" ? "cs" : language)) };
        return (model, new LanguageTextSnapshot(source).PositionAt(caret));
    }
    private static string ApplyLanguageEdits(string text, IReadOnlyList<LanguageTextEdit> edits)
    {
        var snapshot = new LanguageTextSnapshot(text);
        foreach (var edit in edits.Select(e => (Start: snapshot.OffsetAt(new(e.Range.StartLineNumber, e.Range.StartColumn)), End: snapshot.OffsetAt(new(e.Range.EndLineNumber, e.Range.EndColumn)), e.Text)).OrderByDescending(e => e.Start))
        {
            AssertEx.True(edit.Start <= edit.End && edit.End <= text.Length, "native edit is within source bounds");
            text = text.Remove(edit.Start, edit.End - edit.Start).Insert(edit.Start, edit.Text);
        }
        return text;
    }
    private static void NativeTreeRegression()
    {
        const string json = "{\"a\":[{\"name\":\"😀\"},true,null,-1.5],\"__proto__\":7}";
        var parsed = JsoncParser.ParseTree(json);
        AssertEx.Equal(0, parsed.Errors.Count, "native JSON tree accepts valid source");
        var name = JsoncParser.FindNodeAtLocation(parsed.Root, new object[] { "a", 0, "name" });
        AssertEx.Equal("😀", name!.Value as string, "JSON tree preserves UTF-16 strings");
        AssertEx.True(JsoncParser.GetNodePath(name).SequenceEqual(new object[] { "a", 0, "name" }), "JSON path roundtrip");
        AssertEx.True(ReferenceEquals(name, JsoncParser.FindNodeAtOffset(parsed.Root, name.Offset + 1)), "JSON innermost node lookup");
        var value = (Dictionary<string, object?>)JsoncParser.GetNodeValue(parsed.Root!)!;
        AssertEx.Equal(7.0, (double)value["__proto__"]!, "prototype-like key is an ordinary C# dictionary entry");
        AssertEx.True(JsoncParser.GetLocation("{\"a\":[1, ", 9).Path.SequenceEqual(new object[] { "a", 1 }), "incomplete array location");
        AssertEx.True(JsoncParser.GetLocation("{\"na", 4).IsAtPropertyKey, "incomplete property-key location");
        AssertEx.True(JsoncParser.ParseTree("{\"a\":1,}").Errors.Count > 0, "strict JSON trailing comma rejected");
        AssertEx.Equal(0, JsoncParser.ParseTree("{/*comment*/\"a\":1,}", new(AllowTrailingComma: true)).Errors.Count, "JSONC trailing comma and comment accepted");
        AssertEx.True(JsoncParser.ParseTree("{/*comment*/}", new(DisallowComments: true)).Errors.Any(e => e.Code == JsoncParseErrorCode.InvalidCommentToken), "JSON comment diagnostic");
        AssertEx.True(JsoncParser.ParseTree("{\"a\" 1, \"b\":true}").Errors.Any(e => e.Code == JsoncParseErrorCode.ColonExpected), "JSON missing colon recovery");
        var strip = JsoncParser.StripComments("{\"x\":\"//\",/*x\r\ny*/\"a\":1}", ' ');
        AssertEx.True(strip.Contains("\"//\"", StringComparison.Ordinal) && strip.Contains("\r\n", StringComparison.Ordinal), "comment removal protects strings and line structure");
        AssertEx.True(JsoncParser.ParseTree(new string('[', 80) + "0" + new string(']', 80), new(MaximumDepth: 32)).Errors.Any(e => e.Code == JsoncParseErrorCode.MaximumDepthExceeded), "JSON depth limit recovers without recursive overflow");
        var html = HtmlParser.Parse("<main><section id='x'><b>text</section><img></main>");
        AssertEx.Equal("main", html.Roots.Single().Tag, "HTML root tag");
        AssertEx.True(html.Roots.Single().Closed, "HTML root closure tracked");
        AssertEx.Equal("'x'", html.Roots[0].Children[0].Attributes["id"], "HTML raw attribute quote is preserved");
        AssertEx.True(!html.Roots[0].Children[0].Children[0].Closed, "HTML mismatched nested tag remains unclosed");
        AssertEx.True(html.Roots[0].Children[1].Closed, "HTML void element closes itself");
        AssertEx.Equal(1, HtmlParser.Parse("</orphan>", createNodesForOrphanEndTags: true).Roots.Count, "HTML orphan end-tag recovery option");
        var css = CssDocument.Parse("@media screen { .a:hover { display: flex; --gap: 1rem; } }");
        AssertEx.Equal(0, css.Issues.Count, "CSS structural tree valid nesting");
        AssertEx.Equal(2, css.AllDeclarations().Count(), "CSS declarations distinguish selectors from property colons");
        AssertEx.True(!css.Root.Children[0].AcceptsDeclarations && css.Root.Children[0].Children[0].AcceptsDeclarations, "CSS at-rule and selector blocks differ");
        AssertEx.True(CssDocument.Parse(".a { color: red;").Issues.Any(i => i.Message.Contains("Closing brace", StringComparison.Ordinal)), "CSS missing brace diagnostics");
        var model = new FakeModel("json", "old"); var frozen = LanguageModelSnapshot.Capture(model); model.SetLine(1, "new");
        AssertEx.Equal("old", frozen.GetValue(), "captured model does not follow live edits");
        AssertEx.True(!frozen.Matches(model), "snapshot revision guard rejects edits");
    }
    private static async Task NativeSchemaRegressionAsync()
    {
        var service = new MonacoLanguageService(); var package = new JsonLanguagePackage();
        using var installed = service.Packages.Install(package);
        package.Schemas.Register(new Uri("monaco://schema/test"), """
            {"type":"object","required":["name"],"additionalProperties":false,"properties":{
              "name":{"type":"string","description":"Object name","minLength":2},
              "enabled":{"type":"boolean","default":true},
              "mode":{"enum":["fast","safe"]},
              "profile":{"$ref":"#/$defs/profile"}
            },"$defs":{"profile":{"type":"object","properties":{"age":{"type":"integer","minimum":0},"label":{"type":"string"}}}}}
            """, "**/*.json");
        var (empty, at) = Marked("json", "{\n  |\n}");
        var keys = await service.ProvideCompletionItemsAsync(empty, at, new());
        AssertEx.True(keys.Suggestions.Any(s => s.Label == "name") && keys.Suggestions.Any(s => s.Label == "enabled"), "schema object-property suggestions");
        var enabled = keys.Suggestions.Single(s => s.Label == "enabled");
        AssertEx.True(enabled.SnippetText is not null && enabled.SnippetText.Contains("${1:true}", StringComparison.Ordinal), "schema defaults create real placeholders");
        var (existing, existingAt) = Marked("json", "{\"name\":\"ok\", |}");
        AssertEx.True(!(await service.ProvideCompletionItemsAsync(existing, existingAt, new())).Suggestions.Any(s => s.Label == "name"), "existing schema properties are not duplicated");
        var (nested, nestedAt) = Marked("json", "{\"profile\":{ | }}");
        AssertEx.True((await service.ProvideCompletionItemsAsync(nested, nestedAt, new())).Suggestions.Any(s => s.Label == "age"), "JSON pointer reference drives nested completion");
        var (mode, modeAt) = Marked("json", "{\"mode\":\"f|ast\"}");
        var choices = await service.ProvideCompletionItemsAsync(mode, modeAt, new());
        var safe = choices.Suggestions.Single(s => s.Label == "\"safe\"");
        AssertEx.Equal("{\"mode\":\"safe\"}", ApplyLanguageEdits(mode.GetValue(), new[] { new LanguageTextEdit(safe.Range!.Value, safe.InsertText!) }), "completion replaces the entire value token at mid-word caret");
        var (invalid, _) = Marked("json", "{\"name\":\"😀\",\"extra\":1,\"profile\":{\"age\":-1}}|");
        var diagnostics = await service.ProvideDiagnosticsAsync(invalid);
        AssertEx.True(diagnostics.Count >= 3, "schema type/limits/additional properties produce diagnostics");
        AssertEx.Equal(0, service.GetDiagnostics(invalid.Uri).Count, "pull diagnostics does not publish an unchecked revision");
        var (duplicate, _) = Marked("json", "{\"name\":\"aa\",\"name\":\"bb\"}|");
        AssertEx.True((await service.ProvideDiagnosticsAsync(duplicate)).Any(d => d.Code == "DuplicateKey"), "native duplicate key diagnostics");
        var (brokenRef, _) = Marked("json", "{}|");
        package.Schemas.Register(new Uri("monaco://schema/test"), "{\"$ref\":\"#/missing\"}", "**/*.json");
        AssertEx.True((await service.ProvideDiagnosticsAsync(brokenRef)).Any(d => d.Code == "UnresolvedSchema"), "unregistered references do not pretend to validate");
        package.Schemas.Register(new Uri("monaco://schema/test"), "{\"$ref\":\"#\"}", "**/*.json");
        _ = await service.ProvideDiagnosticsAsync(brokenRef); // Cyclic reference must terminate.
        service.Packages.Dispose();
    }
    private static async Task NativeHtmlCssContextRegressionAsync()
    {
        var data = new HtmlCustomData();
        data.Register("""{"tags":[{"name":"x-card","description":"Custom card","attributes":[{"name":"tone","valueSet":"tones"}]}],"valueSets":[{"name":"tones","values":[{"name":"quiet"},{"name":"loud"}]}]}""");
        var provider = new HtmlSyntaxCompletionProvider(data);
        var (custom, customAt) = Marked("html", "<x-|>");
        AssertEx.True((await provider.ProvideCompletionItemsAsync(custom, customAt, new(), default))!.Suggestions.Any(s => s.Label == "x-card"), "HTML custom tag data participates in completion");
        var (value, valueAt) = Marked("html", "<input type='ra|dio'>");
        var text = (await provider.ProvideCompletionItemsAsync(value, valueAt, new(), default))!.Suggestions.Single(s => s.Label == "text");
        AssertEx.Equal("<input type='text'>", ApplyLanguageEdits(value.GetValue(), new[] { new LanguageTextEdit(text.Range!.Value, text.InsertText!) }), "HTML value completion preserves quote and replaces suffix");
        var (attribute, attributeAt) = Marked("html", "<input class='a' ty|pe='text'>");
        var attributes = (await provider.ProvideCompletionItemsAsync(attribute, attributeAt, new(), default))!.Suggestions;
        AssertEx.True(!attributes.Any(s => s.Label == "class"), "HTML duplicate attribute filtered");
        AssertEx.Equal("type", attributes.Single(s => s.Label == "type").InsertText, "HTML existing assignment is not duplicated");
        var (customValue, customValueAt) = Marked("html", "<x-card tone=\"q|\">");
        AssertEx.True((await provider.ProvideCompletionItemsAsync(customValue, customValueAt, new(), default))!.Suggestions.Any(s => s.Label == "quiet"), "HTML custom value set");
        var (script, scriptAt) = Marked("html", "<script>const tag = '<in|';</script>");
        AssertEx.Equal(0, (await provider.ProvideCompletionItemsAsync(script, scriptAt, new(), default))!.Suggestions.Count, "HTML does not offer tags in JavaScript content");
        var cssData = new CssCustomData(); cssData.Register("""{"properties":[{"name":"-app-mode","description":"Application mode","values":[{"name":"compact"},{"name":"wide"}]}]}""");
        var css = new CssSyntaxCompletionProvider(cssData);
        var (display, displayAt) = Marked("css", ".a:hover { display: fl|; }");
        var displayItems = (await css.ProvideCompletionItemsAsync(display, displayAt, new(), default))!.Suggestions;
        AssertEx.True(displayItems.Any(s => s.Label == "flex") && !displayItems.Any(s => s.Label == "absolute"), "CSS values are property-specific");
        var (variable, variableAt) = Marked("css", ":root { --gap: 1rem; } .card { margin: var(--g|ap); }");
        AssertEx.True((await css.ProvideCompletionItemsAsync(variable, variableAt, new(), default))!.Suggestions.Any(s => s.Label == "--gap" && s.InsertText == "--gap"), "CSS var completion uses document declarations");
        AssertEx.Equal(1, (await css.ProvideDefinitionAsync(variable, variableAt, default))!.Count, "CSS custom-property declaration lookup");
        var (customCss, customCssAt) = Marked("css", ".card { -app-mode: c|; }");
        AssertEx.True((await css.ProvideCompletionItemsAsync(customCss, customCssAt, new(), default))!.Suggestions.Any(s => s.Label == "compact"), "CSS custom property data");
    }
    private static async Task NativeCSharpRegressionAsync()
    {
        var service = new MonacoLanguageService();
        var helpers = new Uri("file:///workspace/Widget.cs");
        var package = new CSharpLanguagePackage { Options = new CSharpLanguageOptions
        {
            Documents = new Dictionary<Uri, string> { [helpers] = "public class Widget { public int Count {get;set;} public string Label; public void Spin() {} private void Secret() {} }" }
        }};
        using var installed = service.Packages.Install(package);
        var (console, at) = Marked("csharp", "class C { void M() { Console.| } }");
        var members = await service.ProvideCompletionItemsAsync(console, at, new(CompletionTriggerKind.TriggerCharacter, "."));
        AssertEx.True(members.Suggestions.Any(s => s.Label == "WriteLine" && s.Kind == CompletionItemKind.Method), "Console methods originate from Roslyn metadata");
        AssertEx.True(members.Suggestions.Any(s => s.Label == "ForegroundColor" && s.Kind == CompletionItemKind.Property), "Console properties carry property icons");
        var method = members.Suggestions.First(s => s.Label == "WriteLine");
        AssertEx.True(method.RequireResolvedEdit && method.Resolve is not null, "Roslyn changes must resolve before commit");
        var resolved = await method.Resolve!(default);
        AssertEx.True(!resolved.RequireResolvedEdit && resolved.InsertText is not null && resolved.Range is not null, "Roslyn supplies authoritative text change");
        var (widget, widgetAt) = Marked("csharp", "class C { void M() { var w = new Widget(); w.| } }");
        var items = (await service.ProvideCompletionItemsAsync(widget, widgetAt, new())).Suggestions;
        AssertEx.True(items.Any(s => s.Label == "Spin" && s.Kind == CompletionItemKind.Method), "user-defined methods are semantic completions");
        AssertEx.True(items.Any(s => s.Label == "Count" && s.Kind == CompletionItemKind.Property), "user-defined properties are typed");
        AssertEx.True(items.Any(s => s.Label == "Label" && s.Kind == CompletionItemKind.Field), "user-defined fields are typed");
        AssertEx.True(!items.Any(s => s.Label == "Secret"), "private members are not offered from an unrelated class");
        var (strings, stringsAt) = Marked("csharp", "class C { void M() { var value = \"\"; value.| } }");
        AssertEx.True((await service.ProvideCompletionItemsAsync(strings, stringsAt, new())).Suggestions.Any(s => s.Label == "Length"), "local var inference drives member completion");
        var (comment, commentAt) = Marked("csharp", "class C { void M() {\n// Console.|\n} }");
        AssertEx.True(!(await service.ProvideCompletionItemsAsync(comment, commentAt, new())).Suggestions.Any(s => s.Label == "WriteLine"), "no fabricated Console completions in comments");
        var (definition, definitionAt) = Marked("csharp", "class C { void M() { new Widget().Sp|in(); } }");
        AssertEx.True((await service.ProvideDefinitionAsync(definition, definitionAt)).Any(l => l.Uri == helpers), "definition follows a symbol to a supplied workspace document");
        var (invalid, _) = Marked("csharp", "class C { void M() { int count = \"wrong\"; } }|");
        AssertEx.True((await service.ProvideDiagnosticsAsync(invalid)).Any(d => d.Code == "CS0029"), "Roslyn semantic diagnostics are available without LSP");
        var (format, _) = Marked("csharp", "class C{void M(){Console.WriteLine(1);}}|");
        var edits = await service.ProvideDocumentFormattingEditsAsync(format, new());
        AssertEx.True(edits.Count > 0 && ApplyLanguageEdits(format.GetValue(), edits).Contains("void M()", StringComparison.Ordinal), "in-process C# formatter returns source edits");
        var cancelled = false;
        try { await service.ProvideCompletionItemsAsync(console, at, new(), new CancellationToken(true)); }
        catch (OperationCanceledException) { cancelled = true; }
        AssertEx.True(cancelled, "native semantic request cancellation");
        service.Packages.Dispose();
    }

    private static async Task NativeCompilerBridgeRegressionAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MONACONATIVE_NATIVE_LIBRARY")))
        {
            Console.WriteLine("SKIP: native compiler bridge integration; set MONACONATIVE_NATIVE_LIBRARY to the built Core.Native library.");
            return;
        }
        using var script = new TypeScriptNativeLanguageService();
        var (model, at) = Marked("typescript", "// Chinese 中文 + emoji 😀\nclass Item { name: string; run(count: number, text: string): void {} } const item = new Item(); item.ru|n;");
        var completion = await script.ProvideCompletionItemsAsync(model, at, new(), CancellationToken.None);
        AssertEx.True(completion is not null, script.LastError ?? "native TypeScript bridge unavailable");
        var run = completion!.Suggestions.First(i => i.Label == "run");
        AssertEx.Equal(CompletionItemKind.Method, run.Kind, "native method kind survives bridge");
        AssertEx.Equal("run(${1:count}, ${2:text})$0", run.SnippetText!, "native multiple placeholders survive bridge");
        AssertEx.Equal(3, run.Range!.Value.EndColumn - run.Range.Value.StartColumn, "whole token replacement after Unicode prefix");
        var hover = await script.ProvideHoverAsync(model, at, CancellationToken.None);
        AssertEx.True(hover is not null && hover.Contents.Any(c => c.Value.Contains("Item.run", StringComparison.Ordinal)), "native hover");
        var definitions = await script.ProvideDefinitionAsync(model, at, CancellationToken.None);
        AssertEx.True(definitions is { Count: 1 } && definitions[0].Uri == model.Uri && definitions[0].Range.StartLineNumber == 2, "UTF-8 definition converted to editor UTF-16 line");
        var (console, consoleAt) = Marked("typescript", "console.|");
        AssertEx.True((await script.ProvideCompletionItemsAsync(console, consoleAt, new(), CancellationToken.None))!.Suggestions.Any(i => i.Label == "log"), "upstream Console declaration data");
        var cancelled = false;
        try { await script.ProvideCompletionItemsAsync(console, consoleAt, new(), new CancellationToken(true)); }
        catch (OperationCanceledException) { cancelled = true; }
        AssertEx.True(cancelled, "native bridge cancellation before request");
        var moduleUri = new Uri("file:///workspace/module.ts");
        var moduleOptions = new TypeScriptLanguageOptions
        {
            Documents = new Dictionary<Uri, string>
            {
                [moduleUri] = "// Unicode 中文😀\nexport class Remote { run(count: number): void {} }"
            }
        };
        using var modules = new TypeScriptNativeLanguageService(moduleOptions);
        var (imported, importedAt) = Marked("typescript", "import { Remote as Local } from './module'; const item = new Local(); item.ru|n;");
        imported.Uri = new Uri("file:///workspace/main.ts");
        var importedItems = await modules.ProvideCompletionItemsAsync(imported, importedAt, new(), CancellationToken.None);
        AssertEx.True(importedItems is not null && importedItems.Suggestions.Any(i => i.Label == "run"), modules.LastError ?? "cross-file TypeScript completion");
        var importedDefinitions = await modules.ProvideDefinitionAsync(imported, importedAt, CancellationToken.None);
        AssertEx.True(importedDefinitions is { Count: 1 } && importedDefinitions[0].Uri == moduleUri && importedDefinitions[0].Range.StartLineNumber == 2,
            "cross-file definition uses source snapshot and Unicode-aware coordinates");
        moduleOptions.SetDocuments(new Dictionary<Uri, string> { [moduleUri] = "export class Remote { replacement: string; }" });
        var (updated, updatedAt) = Marked("typescript", "import { Remote } from './module'; const item = new Remote(); item.|");
        updated.Uri = imported.Uri;
        var updatedItems = await modules.ProvideCompletionItemsAsync(updated, updatedAt, new(), CancellationToken.None);
        AssertEx.True(updatedItems is not null && updatedItems.Suggestions.Any(i => i.Label == "replacement") &&
            !updatedItems.Suggestions.Any(i => i.Label == "run"), "changed module snapshot invalidates old members");
        await TypeScriptPreProcessBridgeRegressionAsync();
        await TypeScriptBasicEditingBridgeRegressionAsync();
        using var missing = new CppNativeLanguageService(new() { LibClangPath = Path.Combine(Path.GetTempPath(), "missing-monaco-libclang.dll") });
        var (cpp, cppAt) = Marked("cpp", "struct A { int field; }; void f() { A a; a.| }");
        AssertEx.True(await missing.ProvideCompletionItemsAsync(cpp, cppAt, new(), CancellationToken.None) is null, "missing engine is not fake semantic success");
        var missingDiagnostics = await missing.ProvideDiagnosticsAsync(cpp, CancellationToken.None);
        AssertEx.True(missingDiagnostics!.Any(d => d.Code == "NATIVE_UNAVAILABLE"), "missing engine has explicit diagnostic");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MONACONATIVE_LIBCLANG")))
        {
            Console.WriteLine("SKIP: managed libclang integration; set MONACONATIVE_LIBCLANG.");
            return;
        }
        using var clang = new CppNativeLanguageService();
        var members = await clang.ProvideCompletionItemsAsync(cpp, cppAt, new(), CancellationToken.None);
        AssertEx.True(members is not null && members.Suggestions.Any(i => i.Label == "field" && i.Kind == CompletionItemKind.Field), clang.LastError ?? "real libclang field completion");
    }

    private static async Task TypeScriptPreProcessBridgeRegressionAsync()
    {
        var dependencyUri = new Uri("file:///workspace/module.ts");
        var options = new TypeScriptLanguageOptions
        {
            Documents = new Dictionary<Uri, string>
            {
                [dependencyUri] = "export const name = 'native';",
                [new Uri("file:///workspace/folder/nested.ts")] = "export const value = 1;"
            }
        };
        using var engine = new TypeScriptNativeLanguageService(options);
        var model = new FakeModel("javascript",
            "/// <reference types=\"node\" resolution-mode=\"import\" preserve=\"true\"/>",
            "// 中文😀", "import './module'; const m = require('./other');")
        { Uri = new("file:///workspace/main.js") };
        var original = model.GetValue();
        var result = await engine.PreProcessFileAsync(model);
        AssertEx.True(result is not null, engine.LastError ?? "native preprocessing bridge unavailable");
        AssertEx.Equal(model.Uri, result!.Uri, "preprocessing is tied to the document URI");
        AssertEx.Equal(model.VersionId, result.VersionId, "preprocessing is tied to the document version");
        AssertEx.Equal(2, result.ImportedFiles.Count, "JavaScript mode recognizes require without a server");
        AssertEx.Equal("./module", result.ImportedFiles[0].FileName, "dependency order survives JSON decoding");
        var start = original.IndexOf("'./module'", StringComparison.Ordinal);
        AssertEx.Equal(start, result.ImportedFiles[0].Pos, "dependency start is opening quote in UTF-16, not UTF-8");
        AssertEx.Equal(start + "./module".Length, result.ImportedFiles[0].End, "upstream cooked-length end is not an edit range");
        AssertEx.Equal<int?>(99, result.TypeReferenceDirectives.Single().ResolutionMode, "import resolution mode");
        AssertEx.True(result.TypeReferenceDirectives.Single().Preserve, "reference preserve flag");
        AssertEx.True(!result.IsLibFile && result.AmbientExternalModules is null, "absent ambient modules remain absent");
        var tsMode = await engine.PreProcessFileAsync(model, detectJavaScriptImports: false);
        AssertEx.Equal(1, tsMode!.ImportedFiles.Count, "explicitly disabling JavaScript detection excludes require");
        var refsOnly = await engine.PreProcessFileAsync(model, readImportFiles: false);
        AssertEx.Equal(0, refsOnly!.ImportedFiles.Count, "directive-only preprocessing skips imports");
        AssertEx.Equal(1, refsOnly.TypeReferenceDirectives.Count, "directive-only preprocessing retains reference pragmas");
        var (tsx, _) = Marked("typescriptreact", "import './module'; const x = <div />;|");
        tsx.Uri = new("file:///workspace/main.tsx");
        AssertEx.Equal("./module", (await engine.PreProcessFileAsync(tsx))!.ImportedFiles.Single().FileName,
            "TSX preprocessing does not imply TSX semantic service");
        var pending = engine.PreProcessFileAsync(model);
        model.SetLine(3, "import './changed';");
        var captured = await pending;
        AssertEx.Equal(1UL, captured!.VersionId, "worker retains the captured model version");
        AssertEx.Equal("./module", captured.ImportedFiles[0].FileName, "worker retains original text after editing");
        AssertEx.Equal("./changed", (await engine.PreProcessFileAsync(model))!.ImportedFiles.Single().FileName,
            "later request observes the new snapshot");
        var cancelled = false;
        try { await engine.PreProcessFileAsync(model, cancellationToken: new CancellationToken(true)); }
        catch (OperationCanceledException) { cancelled = true; }
        AssertEx.True(cancelled, "preprocessing observes cancellation before native work");
        AssertEx.True(engine.TriggerCharacters.Contains("/") && engine.TriggerCharacters.Contains("\""),
            "module-path completion trigger characters are registered");

        var (path, at) = Marked("typescript", "// 😀\nimport './mo|dule';");
        path.Uri = new("file:///workspace/main.ts");
        var response = await engine.ProvideCompletionItemsAsync(path, at, new(), CancellationToken.None);
        var file = response!.Suggestions.Single(i => i.Label == "./module");
        AssertEx.Equal(CompletionItemKind.File, file.Kind, "dependency candidates select the file Codicon");
        AssertEx.True(file.SnippetText is null && file.InsertText == "./module", "plain path text is not converted to a snippet");
        AssertEx.Equal("// 😀\nimport './module';", ApplyLanguageEdits(path.GetValue(), new[] { new LanguageTextEdit(file.Range!.Value, file.InsertText!) }),
            "whole raw literal is replaced without duplicating suffix or quotes");
        var definitions = await engine.ProvideDefinitionAsync(path, at, CancellationToken.None);
        AssertEx.True(definitions is { Count: 1 } && definitions[0].Uri == dependencyUri &&
            definitions[0].Range == new LanguageRange(1, 1, 1, 1), "module path navigation targets the snapshot file start");
        var (folder, folderAt) = Marked("typescript", "import './fo|';");
        folder.Uri = path.Uri;
        AssertEx.True((await engine.ProvideCompletionItemsAsync(folder, folderAt, new(), CancellationToken.None))!.Suggestions
            .Any(i => i.Label == "./folder/" && i.Kind == CompletionItemKind.Folder), "directory candidates select the folder Codicon");
        var languages = new MonacoLanguageService();
        languages.Packages.Install(new TypeScriptLanguagePackage { Options = options });
        try
        {
            var combined = await languages.ProvideCompletionItemsAsync(path, at, new());
            AssertEx.True(combined.Suggestions.Any(i => i.Label == "./module") &&
                combined.Suggestions.All(i => i.Kind is CompletionItemKind.File or CompletionItemKind.Folder),
                "authoritative path results suppress keyword fallback providers");
        }
        finally { languages.Packages.Dispose(); }
        options.SetDocuments(new Dictionary<Uri, string>());
        AssertEx.True((await engine.ProvideCompletionItemsAsync(path, at, new(), CancellationToken.None))!.Suggestions.Count == 0,
            "removed workspace snapshots do not remain in path completion");
        AssertEx.True((await engine.ProvideDefinitionAsync(path, at, CancellationToken.None))!.Count == 0,
            "removed snapshots do not silently resolve from disk");
    }

    private static async Task TypeScriptBasicEditingBridgeRegressionAsync()
    {
        using var engine = new TypeScriptNativeLanguageService();
        var (model, at) = Marked("typescript", "// 中文😀\r\nconst greet = (name:string, count=1):string => name; greet('x', |)");
        var help = await engine.ProvideSignatureHelpAsync(model, at, new(SignatureHelpTriggerKind.TriggerCharacter, ","), CancellationToken.None);
        AssertEx.True(help is not null, engine.LastError ?? "basic script signature service");
        AssertEx.Equal("greet(name: string, count?: number): string", help!.Signatures.Single().Label, "arrow/default signature metadata");
        AssertEx.Equal(1, help.ActiveParameter, "comma advances the active parameter after UTF-16 prefix");
        AssertEx.Equal("count?: number", help.Signatures[0].Parameters![1].Label, "optional modifier is retained");
        var (rest, restAt) = Marked("javascript", "console.log('x', 1, |)");
        rest.Uri = new Uri("untitled:script");
        var restHelp = await engine.ProvideSignatureHelpAsync(rest, restAt, new(), CancellationToken.None);
        AssertEx.Equal(0, restHelp!.ActiveParameter, "rest parameter stays highlighted for later arguments");
        var (completionModel, completionAt) = Marked("javascript", "|");
        completionModel.Uri = new Uri("untitled:javascript-keywords");
        var completion = await engine.ProvideCompletionItemsAsync(completionModel, completionAt, new(), CancellationToken.None);
        AssertEx.True(completion!.Suggestions.Any(i => i.Label == "function") && completion.Suggestions.All(i => i.Label != "interface"),
            "untitled JavaScript uses a .js native snapshot, not TypeScript keywords");
        var (snippetModel, snippetAt) = Marked("typescript", "const greet=(name:string):void=>{}; gre|");
        var snippet = (await engine.ProvideCompletionItemsAsync(snippetModel, snippetAt, new(), CancellationToken.None))!.Suggestions.Single(i => i.Label == "greet");
        AssertEx.Equal("greet(${1:name})$0", snippet.SnippetText!, "arrow parameter snippet");
        AssertEx.Equal("editor.action.triggerParameterHints", snippet.CommandId!, "snippet acceptance requests parameter hints");
        var (stringModel, stringAt) = Marked("typescript", "function f(name:string):void{} f('x|y')");
        AssertEx.True(await engine.ProvideSignatureHelpAsync(stringModel, stringAt, new(SignatureHelpTriggerKind.ContentChange), CancellationToken.None) is null,
            "automatic hints do not reopen inside a string");
        AssertEx.True(await engine.ProvideSignatureHelpAsync(stringModel, stringAt, new(), CancellationToken.None) is not null,
            "manual hints can be requested inside a string argument");
        var (foldModel, _) = Marked("typescript", "function f() {\r\n if(true) {\r\n  f();\r\n }\r\n}|");
        var folds = await engine.ProvideFoldingRangesAsync(foldModel, new(), CancellationToken.None);
        AssertEx.True(folds is { Count: 2 } && folds[0].StartLineNumber == 1 && folds[0].EndLineNumber == 4,
            "native folding uses editor one-based inclusive rows and excludes closing-brace line");
        var callbackCount = 0;
        var limited = await engine.ProvideFoldingRangesAsync(foldModel, new(1, uri => { AssertEx.Equal(foldModel.Uri, uri, "limited URI"); ++callbackCount; }), CancellationToken.None);
        AssertEx.Equal(1, limited!.Count, "native fold range limit");
        AssertEx.Equal(1, callbackCount, "limit callback exactly once");
        AssertEx.Equal(0, (await engine.ProvideFoldingRangesAsync(foldModel, new(0), CancellationToken.None))!.Count, "zero folding limit");
        var cancelled = false;
        try { await engine.ProvideSignatureHelpAsync(model, at, new(), new CancellationToken(true)); }
        catch (OperationCanceledException) { cancelled = true; }
        AssertEx.True(cancelled, "signature cancellation before native call");
        var invalidLimit = false;
        try { await engine.ProvideFoldingRangesAsync(foldModel, new(-1), CancellationToken.None); }
        catch (ArgumentOutOfRangeException) { invalidLimit = true; }
        AssertEx.True(invalidLimit, "negative folding limit rejected");
        var (invalid, _) = Marked("typescript", "const value=(1+2;|");
        AssertEx.True((await engine.ProvideDiagnosticsAsync(invalid, CancellationToken.None))!.Any(d => d.Message.Contains("Unclosed delimiter", StringComparison.Ordinal)),
            "basic bracket diagnostics traverse the managed bridge");
        var languages = new MonacoLanguageService();
        var registration = new TypeScriptLanguagePackage().Register(languages);
        try
        {
            AssertEx.True(languages.IsSignatureHelpTriggerCharacter(model, "(") && languages.IsSignatureHelpTriggerCharacter(model, ",") &&
                languages.IsSignatureHelpRetriggerCharacter(model, ")"), "package trigger registration");
            AssertEx.True(languages.HasFoldingRangeProvider(model), "native script folding is installed");
            AssertEx.True(await languages.ProvideSignatureHelpAsync(model, at, new()) is not null, "dispatcher reaches native signature provider");
            var (jsx, _) = Marked("javascriptreact", "const x=<div/>;|");
            AssertEx.True(!languages.HasFoldingRangeProvider(jsx), "unsupported JSX does not install authoritative empty folding");
        }
        finally { registration.Dispose(); languages.Packages.Dispose(); }
        AssertEx.True(!languages.IsSignatureHelpTriggerCharacter(model, "(") && !languages.HasFoldingRangeProvider(model),
            "unregistration removes both service hooks");
    }

    private static void NativeSourceDependencyRegression()
    {
        var uri = new Uri("file:///workspace/module.ts");
        var original = new Dictionary<Uri, string> { [uri] = "export class Original {}" };
        var options = new TypeScriptLanguageOptions { Documents = original };
        original[uri] = "changed outside service";
        AssertEx.Equal("export class Original {}", options.Documents[uri], "TypeScript options copy input snapshots");
        var first = options.Documents;
        options.SetDocuments(new Dictionary<Uri, string> { [uri] = "export class Updated {}" });
        AssertEx.Equal("export class Original {}", first[uri], "old TypeScript snapshot remains stable");
        AssertEx.Equal("export class Updated {}", options.Documents[uri], "snapshot replacement takes effect");

        var directory = Path.Combine(Path.GetTempPath(), "monaco-clang-bundle-" + Guid.NewGuid().ToString("N"));
        var oldLibrary = Environment.GetEnvironmentVariable("MONACONATIVE_LIBCLANG");
        var oldRoot = Environment.GetEnvironmentVariable("MONACONATIVE_LLVM_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("MONACONATIVE_LIBCLANG", null);
            Environment.SetEnvironmentVariable("MONACONATIVE_LLVM_ROOT", null);
            var root = Path.Combine(directory, "clang");
            var library = Path.Combine(root, "bin", "libclang.dll");
            var resources = Path.Combine(root, "lib", "clang", "17");
            Directory.CreateDirectory(Path.GetDirectoryName(library)!);
            Directory.CreateDirectory(Path.Combine(resources, "include"));
            WritePeFixture(library);
            File.WriteAllText(Path.Combine(resources, "include", "stddef.h"), "// fixture");
            void Receipt(string architecture, string relative = "bin/libclang.dll") =>
                File.WriteAllText(Path.Combine(root, "monaco-upstream.json"), System.Text.Json.JsonSerializer.Serialize(new
                {
                    format = 1, component = "llvm", architecture, library = relative,
                    resourceDirectory = "lib/clang/17"
                }));
            void WritePeFixture(string path)
            {
                using var stream = File.Create(path);
                using var writer = new BinaryWriter(stream);
                writer.Write((ushort)0x5a4d);
                stream.Position = 0x3c; writer.Write(64);
                stream.Position = 64; writer.Write(0x00004550u);
                writer.Write((ushort)(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
                    System.Runtime.InteropServices.Architecture.Arm64 ? 0xaa64 : 0x8664));
            }
            var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
            Receipt(arch);
            var located = CppToolchain.Locate(new(), directory);
            AssertEx.Equal(library, located.LibraryPath!, "app-bundled Clang library discovery");
            AssertEx.Equal(resources, located.ResourceDirectory!, "matching Clang resource discovery");
            Receipt("not-this-architecture");
            var rejected = false;
            try { CppToolchain.Locate(new() { ToolchainRoot = root }); }
            catch (BadImageFormatException) { rejected = true; }
            AssertEx.True(rejected, "mismatched bundle architecture rejected");
            Receipt(arch, "../escape.dll");
            rejected = false;
            try { CppToolchain.Locate(new() { ToolchainRoot = root }); }
            catch (InvalidDataException) { rejected = true; }
            AssertEx.True(rejected, "receipt path escape rejected");
            File.WriteAllText(Path.Combine(root, "monaco-upstream.json"), "{}");
            rejected = false;
            try { CppToolchain.Locate(new() { ToolchainRoot = root }); }
            catch (InvalidDataException) { rejected = true; }
            AssertEx.True(rejected, "invalid receipt produces a configuration error");
            Receipt(arch);
            var different = Path.Combine(directory, "explicit-libclang.dll");
            WritePeFixture(different);
            located = CppToolchain.Locate(new() { LibClangPath = different, ToolchainRoot = root });
            AssertEx.Equal(different, located.LibraryPath!, "explicit Clang library wins");
            AssertEx.True(located.ResourceDirectory is null, "unrelated bundle must not supply mismatched headers");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MONACONATIVE_LIBCLANG", oldLibrary);
            Environment.SetEnvironmentVariable("MONACONATIVE_LLVM_ROOT", oldRoot);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void NativeSourcePortRegression()
    {
        ILanguagePackage[] packages = { new JsonLanguagePackage(), new HtmlLanguagePackage(),
            new CssLanguagePackage(), new TypeScriptLanguagePackage(), new CSharpLanguagePackage(), new CppLanguagePackage() };
        foreach (var package in packages)
        {
            AssertEx.True(package is not ILspLanguagePackage, $"{package.Id}: built-in package must not spawn a server");
            AssertEx.True(package.SourcePortStatus != LanguageSourcePortStatus.CompletePort,
                $"{package.Id}: incomplete language engines must not claim complete port status");
        }
        AssertEx.Equal(LanguageSourcePortStatus.PartialPort, new JsonLanguagePackage().SourcePortStatus, "JSON partial source status");
        AssertEx.Equal(LanguageSourcePortStatus.PartialPort, new HtmlLanguagePackage().SourcePortStatus, "HTML partial source status");
        AssertEx.Equal(LanguageSourcePortStatus.PartialPort, new CssLanguagePackage().SourcePortStatus, "CSS partial source status");
        AssertEx.Equal(LanguageSourcePortStatus.InProcessUpstream, new CSharpLanguagePackage().SourcePortStatus, "C# uses in-process Roslyn, not a server or a full vendored source port");
        AssertEx.Equal(LanguageSourcePortStatus.PartialPort, new TypeScriptLanguagePackage().SourcePortStatus, "TypeScript scanner is ported; declaration service is a bounded subset");
        AssertEx.Equal(LanguageSourcePortStatus.InProcessUpstream, new CppLanguagePackage().SourcePortStatus, "Clang C API is integrated in-process, not fully vendored");
        var snapshot = new LanguageTextSnapshot("A😀\r\nB\n");
        AssertEx.Equal(5, snapshot.OffsetAt(new(2, 1)), "CRLF plus surrogate pair uses UTF-16 offsets");
        AssertEx.Equal(3, snapshot.OffsetAt(new(1, 99)), "line end excludes CRLF");
        AssertEx.Equal(new LanguagePosition(2, 1), snapshot.PositionAt(5), "second line offset");
        AssertEx.Equal(new LanguagePosition(3, 1), snapshot.PositionAt(7), "empty final line");

        var scanner = new JsoncScanner("{\r\n  \"emoji\": \"\\uD83D\\uDE00\", /* note */ \"n\": -1.5e2}", ignoreTrivia: true);
        AssertEx.Equal(JsoncSyntaxKind.OpenBraceToken, scanner.Scan(), "JSON brace");
        AssertEx.Equal(JsoncSyntaxKind.StringLiteral, scanner.Scan(), "JSON property");
        AssertEx.Equal(1, scanner.TokenStartLine, "JSON line number is zero-based");
        AssertEx.Equal(2, scanner.TokenStartCharacter, "JSON column is zero-based");
        AssertEx.Equal("emoji", scanner.TokenValue, "JSON property spelling");
        AssertEx.Equal(JsoncSyntaxKind.ColonToken, scanner.Scan(), "JSON colon");
        AssertEx.Equal(JsoncSyntaxKind.StringLiteral, scanner.Scan(), "JSON escaped string");
        AssertEx.Equal("😀", scanner.TokenValue, "JSON unicode surrogate escapes");
        AssertEx.Equal(14, scanner.TokenLength, "escaped token raw UTF-16 length");
        AssertEx.Equal(JsoncSyntaxKind.CommaToken, scanner.Scan(), "JSON comma");
        AssertEx.Equal(JsoncSyntaxKind.StringLiteral, scanner.Scan(), "JSON skip comment");
        scanner.Scan();
        AssertEx.Equal(JsoncSyntaxKind.NumericLiteral, scanner.Scan(), "JSON number");
        AssertEx.Equal("-1.5e2", scanner.TokenValue, "JSON exponent");
        var malformed = new JsoncScanner("1e+");
        AssertEx.Equal(JsoncSyntaxKind.NumericLiteral, malformed.Scan(), "invalid exponent recovers as number");
        AssertEx.Equal(JsoncScanError.UnexpectedEndOfNumber, malformed.TokenError, "invalid exponent error");
        AssertEx.Equal("1", malformed.TokenValue, "upstream incomplete exponent value");
        AssertEx.Equal(3, malformed.TokenLength, "invalid exponent raw span");
        malformed = new JsoncScanner("\"\\q\"");
        malformed.Scan();
        AssertEx.Equal(JsoncScanError.InvalidEscapeCharacter, malformed.TokenError, "invalid escape error");
        malformed = new JsoncScanner("/* unfinished");
        AssertEx.Equal(JsoncSyntaxKind.BlockCommentTrivia, malformed.Scan(), "unfinished comment token");
        AssertEx.Equal(JsoncScanError.UnexpectedEndOfComment, malformed.TokenError, "unfinished comment error");
        AssertEx.Equal(JsoncSyntaxKind.EOF, malformed.Scan(), "recovery reaches EOF");
        var cancelled = false;
        try { new JsoncScanner("{}", cancellationToken: new CancellationToken(true)).Scan(); }
        catch (OperationCanceledException) { cancelled = true; }
        AssertEx.True(cancelled, "scanner cancellation is observed");
    }

    private static string ApplyJsonEdits(string text, IReadOnlyList<JsoncFormatEdit> edits)
    {
        // Work from right to left so offsets always address the original snapshot.
        foreach (var edit in edits.OrderByDescending(edit => edit.Offset).ThenByDescending(edit => edit.Length))
        {
            AssertEx.True(edit.Offset >= 0 && edit.Length >= 0 && edit.Offset + edit.Length <= text.Length, "formatter edit bounds");
            text = text.Remove(edit.Offset, edit.Length).Insert(edit.Offset, edit.Content);
        }
        return text;
    }

    private static void NativeFormattingRegression()
    {
        var compact = "{\"a\":[1,2],\"b\":true}";
        var options = new JsoncFormattingOptions(TabSize: 2);
        var formatted = ApplyJsonEdits(compact, JsoncFormatter.Format(compact, options: options));
        AssertEx.Equal("{\n  \"a\": [\n    1,\n    2\n  ],\n  \"b\": true\n}", formatted, "JSON native formatting");
        AssertEx.Equal(0, JsoncFormatter.Format(formatted, options: options).Count, "JSON formatting idempotence");
        var comments = "{\"a\":1,// keep this\r\n\"b\":2,}";
        var withComments = ApplyJsonEdits(comments, JsoncFormatter.Format(comments, options: options));
        AssertEx.True(withComments.Contains("// keep this\r\n", StringComparison.Ordinal), "JSONC comments and CRLF preserved");
        AssertEx.True(withComments.Contains("\"b\": 2,", StringComparison.Ordinal), "trailing comma preserved");
        AssertEx.Equal(0, JsoncFormatter.Format(withComments, options: options).Count, "JSONC formatting idempotence");
        var tabs = ApplyJsonEdits("{\"a\":1}", JsoncFormatter.Format("{\"a\":1}", options: new(InsertSpaces: false, InsertFinalNewline: true)));
        AssertEx.Equal("{\n\t\"a\": 1\n}\n", tabs, "tab indentation and final newline");
        var partial = "{\n  \"a\":1,\n  \"b\":2\n}";
        var start = partial.IndexOf("\"a\"", StringComparison.Ordinal);
        var ranged = ApplyJsonEdits(partial, JsoncFormatter.Format(partial, new(start, 6), options));
        AssertEx.True(ranged.Contains("\"a\": 1", StringComparison.Ordinal), "range formatting selected property");
        AssertEx.True(ranged.Contains("\"b\":2", StringComparison.Ordinal), "range formatting leaves other line alone");
        foreach (var input in new[] { "/*", "{\"x\":", "\"\\q", "}", "  ", "{\n//unfinished" })
            foreach (var edit in JsoncFormatter.Format(input))
                AssertEx.True(edit.Offset >= 0 && edit.Length >= 0 && edit.Offset + edit.Length <= input.Length, "malformed JSON edit bounds");
    }

    private static void NativeHtmlCssScannerRegression()
    {
        var html = new HtmlScanner("<div\n class=\"x\"><script>const v = 1 < 2;</script></div>");
        var tokens = new List<(HtmlTokenType Type, string Text)>();
        for (var i = 0; i < 100; i++)
        {
            var type = html.Scan();
            if (type == HtmlTokenType.EOS) break;
            tokens.Add((type, html.TokenText));
        }
        AssertEx.True(tokens.Any(token => token == (HtmlTokenType.AttributeName, "class")), "multi-line HTML attribute");
        AssertEx.True(tokens.Any(token => token == (HtmlTokenType.AttributeValue, "\"x\"")), "HTML quoted value");
        AssertEx.True(tokens.Any(token => token == (HtmlTokenType.Script, "const v = 1 < 2;")), "script body stays one scanner token");
        AssertEx.Equal(HtmlTokenType.EOS, html.Scan(), "HTML scanner terminates");
        html = new HtmlScanner("<div<span>", emitPseudoCloseTags: true);
        html.Scan(); html.Scan();
        AssertEx.Equal(HtmlTokenType.StartTagClose, html.Scan(), "pseudo close recovers incomplete start tag");
        AssertEx.Equal(0, html.TokenLength, "pseudo close is zero width");
        AssertEx.Equal(HtmlTokenType.StartTagOpen, html.Scan(), "pseudo close still allows progress");
        html = new HtmlScanner("<!--x--><style>a{color:red}</style>");
        AssertEx.Equal(HtmlTokenType.StartCommentTag, html.Scan(), "HTML comment open");
        AssertEx.Equal(HtmlTokenType.Comment, html.Scan(), "HTML comment content");
        AssertEx.Equal(HtmlTokenType.EndCommentTag, html.Scan(), "HTML comment close");
        html.Scan(); html.Scan(); html.Scan();
        AssertEx.Equal(HtmlTokenType.Styles, html.Scan(), "style body scanner state");
        AssertEx.Equal("/* x */", HtmlScanner.ConvertScriptContentToJavaScript("<!--x-->"), "HTML script comment conversion");

        var css = new CssScanner("/*note*/a{width:12PX;margin:-.5em;color:\\72 ed;}") { IgnoreComment = false };
        AssertEx.Equal(CssTokenType.Comment, css.Scan().Type, "CSS comments can be emitted");
        var cssTokens = new List<CssToken>();
        for (var i = 0; i < 100; i++)
        {
            var token = css.Scan();
            if (token.Type == CssTokenType.EOF) break;
            cssTokens.Add(token);
        }
        AssertEx.True(cssTokens.Any(token => token.Type == CssTokenType.Length && token.Text == "12PX"), "CSS case-insensitive unit class, original token text");
        AssertEx.True(cssTokens.Any(token => token.Type == CssTokenType.EMS && token.Text == "-.5em"), "CSS signed decimal dimension");
        AssertEx.True(cssTokens.Any(token => token.Type == CssTokenType.Ident && token.Text == "red"), "CSS identifier unicode escape");
        css.SetSource("u+00A0-00FF");
        AssertEx.Equal("u", css.Scan().Text, "unicode prefix consumed by caller");
        AssertEx.Equal(CssTokenType.UnicodeRange, css.TryScanUnicode()!.Type, "CSS unicode range");
        css.SetSource("\"unterminated\n");
        AssertEx.Equal(CssTokenType.BadString, css.Scan().Type, "CSS bad string recovery");
        css.SetSource(".5 50% 1cqw");
        AssertEx.Equal(CssTokenType.Num, css.Scan().Type, "CSS leading decimal");
        AssertEx.Equal(CssTokenType.Percentage, css.Scan().Type, "CSS percentage");
        AssertEx.Equal(CssTokenType.ContainerQueryLength, css.Scan().Type, "CSS container query unit");
    }

    private static async Task NativeProvidersRegressionAsync()
    {
        var html = new HtmlSyntaxCompletionProvider();
        var completion = await html.ProvideCompletionItemsAsync(new FakeModel("html", "<button", " cl"), new(2, 4), new(), default);
        AssertEx.True(completion!.Suggestions.Any(item => item.Label == "class"), "HTML completion spans lines");
        completion = await html.ProvideCompletionItemsAsync(new FakeModel("html", "<!--", "<bu"), new(2, 4), new(), default);
        AssertEx.Equal(0, completion!.Suggestions.Count, "HTML completion suppressed inside multi-line comment");
        var css = new CssSyntaxCompletionProvider();
        completion = await css.ProvideCompletionItemsAsync(new FakeModel("css", "a {", "  dis"), new(2, 6), new(), default);
        AssertEx.True(completion!.Suggestions.Any(item => item.Label == "display"), "CSS completion spans lines");
        completion = await css.ProvideCompletionItemsAsync(new FakeModel("css", "a { /*", " dis"), new(2, 5), new(), default);
        AssertEx.Equal(0, completion!.Suggestions.Count, "CSS completion suppressed inside multi-line comment");
        var json = new JsonSyntaxCompletionProvider();
        completion = await json.ProvideCompletionItemsAsync(new FakeModel("jsonc", "[1, "), new(1, 5), new(), default);
        AssertEx.True(completion!.Suggestions.Any(item => item.Label == "true") && completion.Suggestions.All(item => item.Kind != CompletionItemKind.Property), "JSON array comma is value context");
        completion = await json.ProvideCompletionItemsAsync(new FakeModel("jsonc", "{ /*", "tr"), new(2, 3), new(), default);
        AssertEx.Equal(0, completion!.Suggestions.Count, "JSONC completion suppressed inside multi-line comment");
        var service = new MonacoLanguageService(registerBuiltins: false);
        using var registration = service.Packages.Install(new JsonLanguagePackage());
        AssertEx.True(service.IsCompletionTriggerCharacter(new FakeModel("json", "{}"), ":"), "native package trigger character");
        var formatting = await new JsoncFormattingProvider().ProvideDocumentFormattingEditsAsync(
            new FakeModel("json", "{\"x\":1}"), new FormattingOptions(2), default);
        AssertEx.True(formatting is { Count: > 0 }, "native JSON provider returns direct edits");
    }

    // Compatibility utility only: this test resolves a command but never launches it.
    private static void LegacyTransportResolution()
    {
        var directory = Path.Combine(Path.GetTempPath(), "monaco-lsp-profile-" + Guid.NewGuid().ToString("N"));
        try
        {
            var package = Path.Combine(directory, "node_modules", "@test", "server");
            Directory.CreateDirectory(package);
            File.WriteAllText(Path.Combine(package, "package.json"), "{\"bin\":{\"test-lsp\":\"server.js\"}}");
            File.WriteAllText(Path.Combine(package, "server.js"), "// fixture; never executed");
            var profile = new LanguageServerProfile("test-lsp", new[] { "--stdio" }, "fixture") { NpmPackage = "@test/server" };
            var launch = LanguageServerLauncher.ResolveCommand(profile, null, null, directory);
            AssertEx.Equal("node", launch.Executable, "local npm launch bypasses cmd.exe");
            AssertEx.True(launch.Arguments[0].EndsWith("server.js", StringComparison.Ordinal), "npm bin field resolved");
            AssertEx.Equal("--stdio", launch.Arguments[1], "argument boundaries preserved");
            AssertEx.Equal("dotnet", LanguageServerLauncher.ResolveCommand(profile, "server.dll", null, directory).Executable, "DLL launch via dotnet");
            AssertEx.Equal("node", LanguageServerLauncher.ResolveCommand(profile, "server.mjs", null, directory).Executable, "JS entry override");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    public static async Task RunAsync()
    {
        IntegrationRequirementsRegression();
        NativeBinaryValidationTests.Run();
        await LanguagePackageCompletionAsync();
        LanguageRegistryStacking();
        LanguageConfigurationStacking();
        EnterIndentationParity();
        SnippetSessionRegression();
        CompletionRankingRegression();
        NativeSourceDependencyRegression();
        UpstreamPortTests.Run();
        NativeSourcePortRegression();
        NativeFormattingRegression();
        NativeTreeRegression();
        await NativeSchemaRegressionAsync();
        await NativeHtmlCssContextRegressionAsync();
        await NativeCSharpRegressionAsync();
        await NativeCompilerBridgeRegressionAsync();
        NativeHtmlCssScannerRegression();
        await NativeProvidersRegressionAsync();
        LegacyTransportResolution();
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

    private static async Task LanguagePackageCompletionAsync()
    {
        var service = new MonacoLanguageService();
        AssertEx.True(service.GetLanguages().Count == 1 && service.Languages.IsRegistered("plaintext"), "core must only install its plaintext foundation");
        var packages = new ILanguagePackage[] { new JsonLanguagePackage(), new HtmlLanguagePackage(), new CssLanguagePackage(),
            new TypeScriptLanguagePackage(), new CSharpLanguagePackage(), new CppLanguagePackage() };
        foreach (var package in packages) service.Packages.Install(package);
        AssertEx.Equal(13, service.GetLanguages().Count, "plaintext + each subpackage's language IDs");
        AssertEx.True(service.Languages.IsRegistered("typescriptreact"), "TypeScript React language must be available");
        AssertEx.True(service.Languages.IsRegistered("jsonc"), "JSONC language must be available");

        async Task Check(string languageId, string text, string expected, int? column = null)
        {
            var model = new FakeModel(languageId, text);
            var completed = await service.ProvideCompletionItemsAsync(model, new LanguagePosition(1, column ?? text.Length + 1), new CompletionContext());
            AssertEx.True(completed.Suggestions.Any(item => item.Label == expected), $"{languageId}: expected syntax completion '{expected}' for '{text}'");
            AssertEx.True(completed.Suggestions.Single(item => item.Label == expected).Range is not null, "completion must specify an exact replacement range");
        }
        await Check("cpp", "int test() { ret", "return");
        await Check("csharp", "pub", "public");
        await Check("javascript", "con", "const");
        await Check("typescript", "inter", "interface");
        await Check("json", "{\"valid\": tr", "true");
        await Check("jsonc", "{\"valid\": fal", "false");
        await Check("html", "<bu", "button");
        await Check("html", "<button cl", "class");
        await Check("css", "div { dis", "display");

        // Isolate fallback precedence from the independently registered native compiler.
        var precedence = new MonacoLanguageService();
        using var fallback = precedence.RegisterCompletionItemProvider(LanguageSelector.ForLanguage("cpp"), new WordCompletionProvider(new[] { "return" }));
        using var manual = precedence.RegisterCompletionItemProvider(LanguageSelector.ForLanguage("cpp"), new CompletionProvider("host-authoritative"));
        var authoritative = await precedence.ProvideCompletionItemsAsync(new FakeModel("cpp", "ret"), new LanguagePosition(1, 4), new CompletionContext());
        AssertEx.True(authoritative.Suggestions.Count == 1 && authoritative.Suggestions[0].Label == "host-authoritative", "semantic candidates suppress word fallback candidates");

        var detached = service.Packages.Installed.First(item => item.Id == "cpp");
        AssertEx.True(detached.LanguageIds.Count == 1, "packages expose supported language IDs");
        AssertEx.Equal(LanguageSourcePortStatus.InProcessUpstream, detached.SourcePortStatus, "C++ semantic provider uses in-process Clang");
        service.Packages.Dispose();
        AssertEx.True(service.GetLanguages().Count == 0, "disposing all packages must release language and provider registrations");
    }

    private static void SampleFormattingSmoke()
    {
        var options = new FormattingOptions();
        var code = "const value = \"{escaped}\";\nfunction demo() {\nconsole.log(value);\n}\n";
        var formatted = BasicSourceFormattingProvider.Format(code, options, BasicSourceFormattingMode.BraceLanguage);
        AssertEx.True(formatted.Contains("    console.log(value);", StringComparison.Ordinal), "JavaScript indentation");
        AssertEx.True(formatted.Contains("\"{escaped}\"", StringComparison.Ordinal), "Quoted braces preserved");
        AssertEx.Equal(formatted, BasicSourceFormattingProvider.Format(formatted, options, BasicSourceFormattingMode.BraceLanguage), "JS formatter idempotent");
        var html = "<!DOCTYPE html>\n<html>\n<body>\n<p>Hi</p>\n</body>\n</html>\n";
        var formattedHtml = BasicSourceFormattingProvider.Format(html, options, BasicSourceFormattingMode.Html);
        AssertEx.True(formattedHtml.Contains("        <p>Hi</p>", StringComparison.Ordinal), "HTML tag nesting");
        AssertEx.Equal(formattedHtml, BasicSourceFormattingProvider.Format(formattedHtml, options, BasicSourceFormattingMode.Html), "HTML formatter idempotent");
        var cpp = BasicSourceFormattingProvider.Format("#include <vector>\nint main() {\nreturn 0;\n}", options, BasicSourceFormattingMode.BraceLanguage);
        AssertEx.True(cpp.Contains("    return 0;", StringComparison.Ordinal), "C++ formatting");
        var css = BasicSourceFormattingProvider.Format(".card {\ncolor: red;\n}", options, BasicSourceFormattingMode.BraceLanguage);
        AssertEx.True(css.Contains("    color: red;", StringComparison.Ordinal), "CSS formatting");
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

    private static void EnterIndentationParity()
    {
        var service = new MonacoLanguageService();
        using var installed = service.Packages.Install(new CSharpLanguagePackage());
        var configuration = service.Languages.GetLanguageConfiguration("csharp");
        AssertEx.True(configuration is not null, "C# language configuration must be registered");

        var nested = EnterIndentation.Create("    if (enabled) {", "    if (enabled) {".Length + 1, configuration);
        AssertEx.Equal("\n        ", nested.Text, "unclosed block increases indentation");
        var aligned = EnterIndentation.Create("        Console.WriteLine(value);", "        Console.WriteLine(value);".Length + 1, configuration);
        AssertEx.Equal("\n        ", aligned.Text, "completed statement keeps its indentation");
        var closing = EnterIndentation.Create("    }", "    }".Length + 1, configuration);
        AssertEx.Equal("\n    ", closing.Text, "completed closing brace stays on the same level");
        var bracePair = EnterIndentation.Create("    {}", 6, configuration);
        AssertEx.Equal("\n        \n    ", bracePair.Text, "Enter between braces is IndentOutdent");
        AssertEx.Equal(9, bracePair.CaretOffset, "cursor stays on the inner brace line");
        var crlf = EnterIndentation.Create("    Foo();", "    Foo();".Length + 1, configuration, "\r\n");
        AssertEx.Equal("\r\n    ", crlf.Text, "CRLF indentation follows the document EOL");
        var noBraces = EnterIndentation.Create("    if (ready)", "    if (ready)".Length + 1, configuration);
        AssertEx.Equal("\n        ", noBraces.Text, "unbraced control statement indents next line");
        var once = EnterIndentation.Create("        Go();", "        Go();".Length + 1, configuration,
            previousLine: "    if (ready)");
        AssertEx.Equal("\n    ", once.Text, "indent-next-line returns to the parent's level after body");
        var comment = EnterIndentation.Create("    // hello world", "    // hello world".Length + 1, configuration);
        AssertEx.Equal("\n    ", comment.Text, "comment ending at EOL keeps indentation without a new marker");
        AssertEx.Equal("    ", EnterIndentation.Unindent("        ", 4), "electric closing delimiter removes one level");
        AssertEx.True(service.IsCompletionTriggerCharacter(new FakeModel("csharp", "Console."), "."),
            "in-process Roslyn supplies the member-access trigger without LSP");
        service.Packages.Dispose();
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
        var tokenizer = new MonarchTokenizer(CppLanguagePackage.CreateCppMonarch());
        var first = tokenizer.TokenizeLine("int value = 1; /* comment");
        AssertEx.True(first.Tokens.Any(t => t.Type == "keyword"), "keyword cases should resolve through @keywords");
        AssertEx.Equal("comment", first.EndState.Current, "multiline comment should preserve end state");
        var second = tokenizer.TokenizeLine("continued */ return value;", first.EndState);
        AssertEx.Equal("root", second.EndState.Current, "@pop should restore previous tokenizer state");
        AssertEx.True(second.Tokens.Any(t => t.Type == "keyword"), "tokenization should resume after multiline comment");

        var json = new MonarchTokenizer(JsonLanguagePackage.CreateJsonMonarch(false));
        var jsonLine = json.TokenizeLine("{\"ok\":true,\"n\":12}");
        AssertEx.True(jsonLine.Tokens.Any(t => t.Type == "string"), "JSON string content should tokenize");
        AssertEx.True(jsonLine.Tokens.Any(t => t.Type == "keyword"), "JSON keywords should tokenize");
        AssertEx.True(jsonLine.Tokens.Any(t => t.Type == "number"), "JSON numbers should tokenize");
    }

    private static void SampleLanguagesTokenizerSmoke()
    {
        var service = new MonacoLanguageService();
        service.Packages.Install(new HtmlLanguagePackage());
        service.Packages.Install(new TypeScriptLanguagePackage());
        service.Packages.Install(new CSharpLanguagePackage());
        service.Packages.Install(new CppLanguagePackage());
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
        service.Packages.Install(new CppLanguagePackage());
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
        service.Packages.Install(new CppLanguagePackage());
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

// Optional dependencies may be skipped by unit-only runs. Acceptance jobs explicitly
// require their libraries before entering the suite, so a missing library cannot pass.
internal readonly record struct IntegrationRequirements(bool Native, bool LibClang)
{
    public static IntegrationRequirements Parse(IEnumerable<string> args)
    {
        bool native = false, clang = false;
        foreach (var arg in args)
        {
            switch (arg)
            {
                case "--require-native": native = true; break;
                case "--require-libclang": native = true; clang = true; break;
                default: throw new ArgumentException($"Unknown test argument: {arg}");
            }
        }
        return new(native, clang);
    }

    public void Validate(Func<string, string?> environment, Func<string, bool> fileExists)
    {
        if (Native) RequireFile("MONACONATIVE_NATIVE_LIBRARY", environment, fileExists);
        if (LibClang) RequireFile("MONACONATIVE_LIBCLANG", environment, fileExists);
    }

    private static void RequireFile(string name, Func<string, string?> environment, Func<string, bool> fileExists)
    {
        var path = environment(name);
        if (string.IsNullOrWhiteSpace(path) || !fileExists(path))
            throw new InvalidOperationException($"Required integration dependency {name} is missing or is not a file: {path ?? "<unset>"}. No integration tests were run.");
    }
}

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        IntegrationRequirements requirements;
        try
        {
            requirements = IntegrationRequirements.Parse(args);
            requirements.Validate(Environment.GetEnvironmentVariable, File.Exists);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Language test configuration failed: {error.Message}");
            return 2;
        }
        Console.WriteLine($"Language test requirements: native={requirements.Native}, libclang={requirements.LibClang}");
        await Tests.RunAsync();
        return 0;
    }
}
