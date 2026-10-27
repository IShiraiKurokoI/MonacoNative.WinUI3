using MonacoNative.Language;

namespace MonacoNative.Language.CSharp;

public sealed class CSharpLanguagePackage : ILanguagePackage
{
    public string Id => "csharp";
    public IReadOnlyList<string> LanguageIds { get; } = new[] { "csharp" };
    public LanguageSourcePortStatus SourcePortStatus => LanguageSourcePortStatus.InProcessUpstream;
    public CSharpLanguageOptions Options { get; init; } = new();

    public IDisposable Register(MonacoLanguageService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        var regs = new LanguageRegistrations();
        try
        {
            regs.Add(service.Register(new LanguageExtensionPoint("csharp", Aliases: new[] { "C#", "CSharp" },
                Extensions: new[] { ".cs" }, Mimetypes: new[] { "text/x-csharp" })));
            var braces = new[] { new CharacterPair("{", "}"), new CharacterPair("[", "]"), new CharacterPair("(", ")") };
            regs.Add(service.SetLanguageConfiguration("csharp", new LanguageConfiguration
            {
                Comments = new CommentRule("//", new CharacterPair("/*", "*/")), Brackets = braces,
                AutoClosingPairs = new[] { new AutoClosingPair("{", "}"), new AutoClosingPair("[", "]"), new AutoClosingPair("(", ")"), new AutoClosingPair("\"", "\"", "string"), new AutoClosingPair("'", "'", "string") },
                SurroundingPairs = braces.Concat(new[] { new CharacterPair("\"", "\""), new CharacterPair("'", "'") }).ToArray(),
                WordPattern = @"[A-Za-z_$]\w*",
                IncreaseIndentPattern = @"^.*\{\s*$",
                DecreaseIndentPattern = @"^\s*[}\])].*$",
                IndentNextLinePattern = @"^\s*(?:(?:else\s+)?if\s*\(.*\)|(?:for|foreach|while|using|lock|fixed)\s*\(.*\)|else|do)\s*$",
                UnIndentedLinePattern = @"^\s*#",
                OnEnterRules = new[] { new OnEnterRule(@"^\s*//\s*\S", EnterIndentAction.None, AfterText: @"^\S", AppendText: "// ") }
            }));
            regs.Add(service.SetMonarchTokensProvider("csharp", CreateCSharpMonarch()));
            var engine = new CSharpNativeLanguageService(Options);
            regs.Add(engine);
            var selector = LanguageSelector.ForLanguage("csharp");
            regs.Add(service.RegisterCompletionItemProvider(selector, engine));
            regs.Add(service.RegisterHoverProvider(selector, engine));
            regs.Add(service.RegisterSignatureHelpProvider(selector, engine));
            regs.Add(service.RegisterDefinitionProvider(selector, engine));
            regs.Add(service.RegisterReferenceProvider(selector, engine));
            regs.Add(service.RegisterDocumentFormattingEditProvider(selector, engine));
            regs.Add(service.RegisterDocumentRangeFormattingEditProvider(selector, engine));
            regs.Add(service.RegisterDocumentDiagnosticsProvider(selector, engine));
            regs.Add(service.RegisterRenameProvider(selector, engine));
            regs.Add(service.RegisterCodeActionProvider(selector, engine));
            regs.Add(service.RegisterDocumentSemanticTokensProvider(selector, engine));
            regs.Add(service.RegisterInlayHintsProvider(selector, engine));
            return regs;
        }
        catch { regs.Dispose(); throw; }
    }

    private static readonly string[] CSharpKeywords =
    {
        "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char",
        "checked", "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "dynamic",
        "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "from", "get", "global", "goto", "if", "implicit", "in", "init", "int", "interface",
        "internal", "is", "join", "let", "lock", "long", "namespace", "new", "null", "object", "operator",
        "out", "override", "params", "partial", "private", "protected", "public", "readonly", "record", "ref",
        "required", "return", "sbyte", "sealed", "select", "set", "short", "sizeof", "stackalloc", "static",
        "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked",
        "unsafe", "ushort", "using", "var", "virtual", "void", "volatile", "when", "where", "while", "with"
    };

    public static MonarchLanguage CreateCSharpMonarch() => new()
    {
        WordLists = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal) { ["keywords"] = CSharpKeywords },
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
