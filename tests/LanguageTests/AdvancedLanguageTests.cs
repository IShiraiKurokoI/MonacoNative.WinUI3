using MonacoNative.Language;
using MonacoNative.Language.CSharp;
using MonacoNative.Language.Cpp;
using MonacoNative.Language.Html;
using MonacoNative.Language.Json;

internal static class AdvancedLanguageTests
{
    private static LanguagePosition Position(string text, string needle, int delta = 0)
    {
        var at = text.IndexOf(needle, StringComparison.Ordinal);
        if (at < 0) throw new Exception("Missing fixture marker: " + needle);
        return new LanguageTextSnapshot(text).PositionAt(at + delta);
    }
    private static LanguageRange All(string text) => new(1, 1, new LanguageTextSnapshot(text).PositionAt(text.Length).LineNumber,
        new LanguageTextSnapshot(text).PositionAt(text.Length).Column);

    public static async Task RunAsync()
    {
        await CSharpAsync();
        await CppAsync();
        await HtmlAsync();
        await JsonAsync();
        Console.WriteLine("AdvancedLanguageTests: PASS");
    }

    private static async Task CSharpAsync()
    {
        var languages = new MonacoLanguageService(registerBuiltins: false);
        using var registration = languages.Packages.Install(new CSharpLanguagePackage());
        const string text = "using System.Linq;\nusing System;\nclass Item { int value; void Set(int x) { value = x; } }";
        var model = new FakeModel("csharp", text) { Uri = new Uri("file:///workspace/advanced.cs") };
        var actionList = await languages.ProvideCodeActionsAsync(model, All(text), new CodeActionContext(Only: "source.organizeImports"));
        AssertEx.True(actionList.Any(a => a.Title == "Organize usings" && a.Edits is { Count: > 0 }), "C# uses Roslyn organize imports");
        var at = Position(text, "value", 2);
        var location = await languages.ResolveRenameLocationAsync(model, at);
        AssertEx.True(location is not null && location.Text == "value", "Roslyn rename location");
        var edits = await languages.ProvideRenameEditsAsync(model, at, "renamed");
        AssertEx.True(edits is { Edits.Count: >= 2 } && edits.Edits.All(edit => edit.Text == "renamed"),
            "Roslyn rename updates both declaration and references");
        var tokens = await languages.ProvideDocumentSemanticTokensAsync(model);
        AssertEx.True(tokens is { Tokens.Count: > 0 } && tokens.Tokens.Any(t => t.TokenType == "class"),
            "Roslyn semantic tokens");
        const string call = "class A { void M(int length){} void Test(){ M(42); } }";
        var callModel = new FakeModel("csharp", call) { Uri = new Uri("file:///workspace/call.cs") };
        var hints = await languages.ProvideInlayHintsAsync(callModel, All(call));
        AssertEx.True(hints.Any(h => h.Kind == InlayHintKind.Parameter && h.Label == "length:"),
            "Roslyn parameter name hints");
        const string inferred = "class A { void M(){ var count = 42; } }";
        var inferredHints = await languages.ProvideInlayHintsAsync(
            new FakeModel("csharp", inferred) { Uri = new Uri("file:///workspace/inferred.cs") }, All(inferred));
        AssertEx.True(inferredHints.Any(h => h.Kind == InlayHintKind.Type && h.Label == ": int"),
            "Roslyn inferred type hints");
    }

    private static async Task CppAsync()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MONACONATIVE_NATIVE_LIBRARY")) ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MONACONATIVE_LIBCLANG")))
        {
            Console.WriteLine("Advanced C++ libclang integration: SKIP (native binaries not configured)");
            return;
        }
        var service = new MonacoLanguageService(registerBuiltins: false);
        using var registration = service.Packages.Install(new CppLanguagePackage());
        const string source = "int add(int x, int y); int test() { return add(1, 2); }";
        var model = new FakeModel("cpp", source) { Uri = new Uri("file:///workspace/advanced.cpp") };
        var help = await service.ProvideSignatureHelpAsync(model, Position(source, "2);"), new SignatureHelpContext());
        AssertEx.True(help is { Signatures.Count: > 0 } && help.ActiveParameter == 1 &&
            help.Signatures.Any(s => s.Label.Contains("add", StringComparison.Ordinal)), "libclang parameter help");
        var location = await service.ResolveRenameLocationAsync(model, Position(source, "add(1", 1));
        AssertEx.True(location?.Text == "add", "libclang rename location");
        var rename = await service.ProvideRenameEditsAsync(model, Position(source, "add(1", 1), "sum");
        AssertEx.True(rename is { Edits.Count: >= 2 }, "libclang source references for scoped rename");
    }

    private static async Task HtmlAsync()
    {
        var languages = new MonacoLanguageService(registerBuiltins: false);
        using var registration = languages.Packages.Install(new HtmlLanguagePackage());
        const string invalid = "<button type=\"unknown\">Save</button>";
        var model = new FakeModel("html", invalid) { Uri = new Uri("file:///workspace/demo.html") };
        var diagnostics = await languages.ProvideDiagnosticsAsync(model);
        AssertEx.True(diagnostics.Any(d => d.Code == "HtmlAttributeValue"), "HTML known enum attribute validation");
        var actions = await languages.ProvideCodeActionsAsync(model, new(1, 15, 1, 19), new CodeActionContext());
        AssertEx.True(actions.Any(a => a.Title == "Use 'submit'"), "HTML safe value correction");
        const string blocks = "<script>const x = 1;</script>\n<style>.item { color: red; }</style>";
        model = new FakeModel("html", blocks) { Uri = new Uri("file:///workspace/embedded.html") };
        var tokens = await languages.ProvideDocumentSemanticTokensAsync(model);
        AssertEx.True(tokens is { Tokens.Count: > 0 } && tokens.Tokens.Any(t => t.TokenType == "keyword") &&
            tokens.Tokens.Any(t => t.TokenType == "property"), "embedded script and style lexical ranges");
        const string unclosed = "<section>content";
        var repairs = await languages.ProvideCodeActionsAsync(new FakeModel("html", unclosed), new(1, 3, 1, 3), new CodeActionContext());
        AssertEx.True(repairs.Any(a => a.Title.Contains("</section>", StringComparison.Ordinal)), "HTML unclosed tag repair");
        const string linked = "<strong>content</strong>";
        var renameEdits = await languages.ProvideRenameEditsAsync(
            new FakeModel("html", linked), Position(linked, "strong", 2), "em");
        AssertEx.True(renameEdits is { Edits.Count: 2 } && renameEdits.Edits.All(e => e.Text == "em"),
            "paired HTML opening/closing tag rename");
    }

    private static async Task JsonAsync()
    {
        var package = new JsonLanguagePackage();
        package.Schemas.Register(new Uri("https://example.test/config.schema.json"), """
        {
          "type":"object",
          "properties":{"mode":{"enum":["dev","prod"]}},
          "if":{"properties":{"mode":{"const":"prod"}},"required":["mode"]},
          "then":{"required":["replicas"],"properties":{"replicas":{"type":"integer","default":3}}},
          "else":{"required":["debug"],"properties":{"debug":{"type":"boolean","default":true}}}
        }
        """, "*.json");
        var languages = new MonacoLanguageService(registerBuiltins: false);
        using var registration = languages.Packages.Install(package);
        const string production = "{\"mode\":\"prod\"}";
        var model = new FakeModel("json", production) { Uri = new Uri("file:///workspace/config.json") };
        var diagnostics = await languages.ProvideDiagnosticsAsync(model);
        AssertEx.True(diagnostics.Any(d => d.Message.Contains("replicas", StringComparison.Ordinal)) &&
            !diagnostics.Any(d => d.Message.Contains("debug", StringComparison.Ordinal)), "JSON conditional then branch");
        var actions = await languages.ProvideCodeActionsAsync(model, new(1, 1, 1, 2), new CodeActionContext());
        AssertEx.True(actions.Any(a => a.Title.Contains("replicas", StringComparison.Ordinal)), "JSON default property quick fix");
        const string development = "{\"mode\":\"dev\"}";
        model = new FakeModel("json", development) { Uri = new Uri("file:///workspace/config.json") };
        diagnostics = await languages.ProvideDiagnosticsAsync(model);
        AssertEx.True(diagnostics.Any(d => d.Message.Contains("debug", StringComparison.Ordinal)) &&
            !diagnostics.Any(d => d.Message.Contains("replicas", StringComparison.Ordinal)), "JSON conditional else branch");
        const string invalid = "{\"mode\":\"invalid\"}";
        model = new FakeModel("json", invalid) { Uri = new Uri("file:///workspace/config.json") };
        actions = await languages.ProvideCodeActionsAsync(model, new(1, 11, 1, 12), new CodeActionContext());
        AssertEx.True(actions.Any(a => a.Title == "Replace with \"prod\""), "JSON Schema enum correction");
        var dependencyPackage = new JsonLanguagePackage();
        dependencyPackage.Schemas.Register(new Uri("https://example.test/dependent.schema.json"), """
        {
          "type":"object",
          "properties":{"mode":{"type":"string"},"owner":{"type":"string","default":"team"}},
          "dependentRequired":{"mode":["owner"]},
          "dependentSchemas":{"mode":{"properties":{"mode":{"enum":["dev","prod"]}}}}
        }
        """, "*.config");
        var dependencyLanguages = new MonacoLanguageService(registerBuiltins: false);
        using var dependencyRegistration = dependencyLanguages.Packages.Install(dependencyPackage);
        var dependencyModel = new FakeModel("jsonc", "{\"mode\":\"invalid\"}")
            { Uri = new Uri("file:///workspace/project.config") };
        var dependentDiagnostics = await dependencyLanguages.ProvideDiagnosticsAsync(dependencyModel);
        AssertEx.True(dependentDiagnostics.Any(d => d.Message.Contains("owner", StringComparison.Ordinal)) &&
            dependentDiagnostics.Any(d => d.Message.Contains("enum", StringComparison.Ordinal)),
            "dependentRequired and dependentSchemas affect diagnostics");
        var dependentFixes = await dependencyLanguages.ProvideCodeActionsAsync(dependencyModel,
            new(1, 1, 1, 1), new CodeActionContext());
        AssertEx.True(dependentFixes.Any(a => a.Title == "Add required property 'owner'"),
            "dependentRequired inserts a missing property");
    }
}
