using MonacoNative.Language;

namespace MonacoNative.Language.TypeScript;

public sealed class TypeScriptLanguagePackage : ILanguagePackage
{
    public TypeScriptLanguageOptions Options { get; init; } = new();
    public string Id => "typescript";
    public IReadOnlyList<string> LanguageIds { get; } = new[] { "javascript", "javascriptreact", "typescript", "typescriptreact" };
    public LanguageSourcePortStatus SourcePortStatus => LanguageSourcePortStatus.PartialPort;

    public IDisposable Register(MonacoLanguageService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        var regs = new LanguageRegistrations();
        try
        {
            var jsIds = new[] { ("javascript", ".js", "JavaScript"), ("javascriptreact", ".jsx", "JavaScript React"),
                ("typescript", ".ts", "TypeScript"), ("typescriptreact", ".tsx", "TypeScript React") };
            var braces = new[] { new CharacterPair("{", "}"), new CharacterPair("[", "]"), new CharacterPair("(", ")") };
            var engine = new TypeScriptNativeLanguageService(Options);
            regs.Add(engine);
            foreach (var (id, extension, alias) in jsIds)
            {
                var extensions = id == "javascript" ? new[] { ".js", ".mjs", ".cjs" } : new[] { extension };
                regs.Add(service.Register(new LanguageExtensionPoint(id, Aliases: new[] { alias }, Extensions: extensions)));
                regs.Add(service.SetLanguageConfiguration(id, new LanguageConfiguration
                {
                    Comments = new CommentRule("//", new CharacterPair("/*", "*/")), Brackets = braces,
                    AutoClosingPairs = new[] { new AutoClosingPair("{", "}"), new AutoClosingPair("[", "]"), new AutoClosingPair("(", ")"), new AutoClosingPair("\"", "\"", "string"), new AutoClosingPair("'", "'", "string") },
                    SurroundingPairs = braces.Concat(new[] { new CharacterPair("\"", "\""), new CharacterPair("'", "'") }).ToArray(),
                    WordPattern = @"[A-Za-z_$][\w$]*",
                    IncreaseIndentPattern = @"^.*\{\s*$",
                    DecreaseIndentPattern = @"^\s*[}\])].*$",
                    IndentNextLinePattern = @"^\s*(?:(?:else\s+)?if|for|while)\s*\(.*\)\s*$|^\s*else\s*$"
                }));
                regs.Add(service.SetMonarchTokensProvider(id, CreateScriptMonarch(id.StartsWith("typescript", StringComparison.Ordinal) ? TypeScriptKeywords : JavaScriptKeywords)));
                regs.Add(service.RegisterCompletionItemProvider(LanguageSelector.ForLanguage(id),
                    new WordCompletionProvider(id.StartsWith("typescript", StringComparison.Ordinal) ? TypeScriptKeywords : JavaScriptKeywords, new[] { "console", "document", "window", "Promise", "Map", "Set", "Array", "String", "Object" })));
                var selector = LanguageSelector.ForLanguage(id);
                regs.Add(service.RegisterCompletionItemProvider(selector, engine));
                regs.Add(service.RegisterHoverProvider(selector, engine));
                regs.Add(service.RegisterDefinitionProvider(selector, engine));
                // JSX/TSX retain baseline highlighting; do not install an authoritative
                // empty native folding provider for a syntax this subset cannot parse.
                if (id is "javascript" or "typescript")
                {
                    regs.Add(service.RegisterSignatureHelpProvider(selector, engine));
                    regs.Add(service.RegisterFoldingRangeProvider(selector, engine));
                }
                regs.Add(service.RegisterDocumentDiagnosticsProvider(selector, engine));
                if (id is "javascript" or "typescript")
                {
                    var formatter = new BasicSourceFormattingProvider(BasicSourceFormattingMode.BraceLanguage);
                    regs.Add(service.RegisterDocumentFormattingEditProvider(selector, formatter));
                    regs.Add(service.RegisterDocumentRangeFormattingEditProvider(selector, formatter));
                }
            }
            return regs;
        }
        catch { regs.Dispose(); throw; }
    }

    private static readonly string[] JavaScriptKeywords =
    {
        "async", "await", "break", "case", "catch", "class", "const", "continue", "debugger", "default",
        "delete", "do", "else", "export", "extends", "false", "finally", "for", "from", "function",
        "if", "import", "in", "instanceof", "let", "new", "null", "of", "return", "static", "super",
        "switch", "this", "throw", "true", "try", "typeof", "undefined", "var", "void", "while", "yield"
    };
    private static readonly string[] TypeScriptKeywords = JavaScriptKeywords.Concat(new[] {
        "any", "as", "asserts", "bigint", "boolean", "declare", "enum", "implements", "infer", "interface", "is",
        "keyof", "module", "namespace", "never", "number", "override", "private", "protected", "public", "readonly",
        "require", "satisfies", "string", "symbol", "type", "unique", "unknown", "abstract", "constructor"
    }).Distinct(StringComparer.Ordinal).ToArray();

    private static MonarchLanguage CreateScriptMonarch(IReadOnlyCollection<string> keywords) => new()
    {
        WordLists = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal) { ["keywords"] = keywords },
        Tokenizer = new Dictionary<string, IReadOnlyList<MonarchRule>>(StringComparer.Ordinal)
        {
            ["root"] = new MonarchRule[]
            {
                MonarchRule.Match(@"[ \t]+", "white"),
                MonarchRule.Match(@"//.*$", "comment"),
                MonarchRule.Match(@"/\*", "comment", "@comment"),
                MonarchRule.Match("@\"", "string.quote", "@double"),
                MonarchRule.Match("\"", "string.quote", "@double"),
                MonarchRule.Match("'", "string.quote", "@single"),
                MonarchRule.Match("`", "string.quote", "@template"),
                MonarchRule.Match(@"#[A-Za-z_]+.*$", "comment"),
                MonarchRule.Match(@"[A-Za-z_$][\w$]*", new MonarchAction("identifier", Cases: new Dictionary<string, string> { ["@keywords"] = "keyword", ["@default"] = "identifier" })),
                MonarchRule.Match(@"0[xX][\da-fA-F]+|\d+(?:\.\d+)?(?:[eE][+-]?\d+)?", "number"),
                MonarchRule.Match(@"[{}()\[\]]", "delimiter.bracket"),
                MonarchRule.Match(@"[=><!~?:&|+\-*/^%]+", "operator"),
                MonarchRule.Match(@"[,.;]", "delimiter"),
            },
            ["comment"] = new MonarchRule[]
            {
                MonarchRule.Match(@"[^*]+", "comment"),
                MonarchRule.Match(@"\*/", "comment", "@pop"),
                MonarchRule.Match(@"\*", "comment"),
            },
            ["double"] = new MonarchRule[]
            {
                MonarchRule.Match("[^\\\\\"]+", "string"),
                MonarchRule.Match(@"\\.", "string.escape"),
                MonarchRule.Match("\"", "string.quote", "@pop"),
            },
            ["single"] = new MonarchRule[]
            {
                MonarchRule.Match("[^\\\\']+", "string"),
                MonarchRule.Match(@"\\.", "string.escape"),
                MonarchRule.Match("'", "string.quote", "@pop"),
            },
            ["template"] = new MonarchRule[]
            {
                MonarchRule.Match("[^\\\\`]+", "string"),
                MonarchRule.Match(@"\\.", "string.escape"),
                MonarchRule.Match("`", "string.quote", "@pop"),
            },
        }
    };
}
