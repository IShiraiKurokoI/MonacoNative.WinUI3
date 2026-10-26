using MonacoNative.Language;

namespace MonacoNative.Language.Cpp;

public sealed class CppLanguagePackage : ILanguagePackage
{
    public string Id => "cpp";
    public IReadOnlyList<string> LanguageIds { get; } = new[] { "cpp" };
    public LanguageSourcePortStatus SourcePortStatus => LanguageSourcePortStatus.InProcessUpstream;

    public CppLanguageOptions Options { get; init; } = new();

    public IDisposable Register(MonacoLanguageService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        var regs = new LanguageRegistrations();
        try
        {
            regs.Add(service.Register(new LanguageExtensionPoint("cpp", Aliases: new[] { "C++", "cpp" },
                Extensions: new[] { ".cpp", ".cc", ".cxx", ".hpp", ".h" }, Mimetypes: new[] { "text/x-c++src" })));
            var pairs = new[] { new CharacterPair("{", "}"), new CharacterPair("[", "]"), new CharacterPair("(", ")") };
            regs.Add(service.SetLanguageConfiguration("cpp", new LanguageConfiguration
            {
                Comments = new CommentRule("//", new CharacterPair("/*", "*/")),
                Brackets = pairs,
                AutoClosingPairs = new[] { new AutoClosingPair("{", "}"), new AutoClosingPair("[", "]"), new AutoClosingPair("(", ")"), new AutoClosingPair("\"", "\"", "string"), new AutoClosingPair("'", "'", "string") },
                SurroundingPairs = pairs.Concat(new[] { new CharacterPair("\"", "\""), new CharacterPair("'", "'") }).ToArray(),
                WordPattern = @"[A-Za-z_]\w*",
                IncreaseIndentPattern = @"^.*\{\s*$",
                DecreaseIndentPattern = @"^\s*[}\])].*$",
                IndentNextLinePattern = @"^\s*(?:(?:else\s+)?if|for|while|switch)\s*\(.*\)\s*$|^\s*else\s*$",
                UnIndentedLinePattern = @"^\s*#"
            }));
            regs.Add(service.SetMonarchTokensProvider("cpp", CreateCppMonarch()));
            regs.Add(service.RegisterCompletionItemProvider(LanguageSelector.ForLanguage("cpp"), new WordCompletionProvider(CppKeywords)));
            var engine = new CppNativeLanguageService(Options);
            regs.Add(engine);
            var selector = LanguageSelector.ForLanguage("cpp");
            regs.Add(service.RegisterCompletionItemProvider(selector, engine));
            regs.Add(service.RegisterHoverProvider(selector, engine));
            regs.Add(service.RegisterDefinitionProvider(selector, engine));
            regs.Add(service.RegisterReferenceProvider(selector, engine));
            regs.Add(service.RegisterDocumentDiagnosticsProvider(selector, engine));
            var formatter = new BasicSourceFormattingProvider(BasicSourceFormattingMode.BraceLanguage);
            regs.Add(service.RegisterDocumentFormattingEditProvider(selector, formatter));
            regs.Add(service.RegisterDocumentRangeFormattingEditProvider(selector, formatter));
            return regs;
        }
        catch { regs.Dispose(); throw; }
    }

    private static readonly string[] CppKeywords =
    {
        "alignas", "alignof", "auto", "bool", "break", "case", "catch", "char", "char16_t", "char32_t",
        "class", "const", "constexpr", "consteval", "constinit", "continue", "co_await", "co_return", "co_yield",
        "decltype", "default", "delete", "do", "double", "else", "enum", "explicit", "export", "extern", "false",
        "float", "for", "friend", "goto", "if", "inline", "int", "long", "namespace", "new", "noexcept", "nullptr",
        "operator", "private", "protected", "public", "requires", "return", "short", "signed", "sizeof", "static",
        "struct", "switch", "template", "this", "throw", "true", "try", "typedef", "typename", "union", "unsigned",
        "using", "virtual", "void", "volatile", "wchar_t", "while"
    };

    public static MonarchLanguage CreateCppMonarch() => new()
    {
        DefaultToken = string.Empty,
        WordLists = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal) { ["keywords"] = CppKeywords },
        Tokenizer = new Dictionary<string, IReadOnlyList<MonarchRule>>(StringComparer.Ordinal)
        {
            ["root"] = new MonarchRule[]
            {
                MonarchRule.IncludeState("@whitespace"),
                MonarchRule.Match(@"[A-Za-z_]\w*", new MonarchAction("identifier", Cases: new Dictionary<string,string> { ["@keywords"] = "keyword", ["@default"] = "identifier" })),
                MonarchRule.Match(@"0[xX][0-9a-fA-F]+|\d+(?:\.\d+)?(?:[eE][+-]?\d+)?", "number"),
                MonarchRule.Match("\"", "string.quote", "@stringDouble"),
                MonarchRule.Match("'", "string.quote", "@stringSingle"),
                MonarchRule.Match(@"[{}()\[\]]", "delimiter.bracket"),
                MonarchRule.Match(@"[=><!~?:&|+\-*/^%]+", "operator"),
                MonarchRule.Match(@"[;,\.]", "delimiter"),
            },
            ["whitespace"] = new MonarchRule[]
            {
                MonarchRule.Match(@"[ \t\r\n]+", "white"),
                MonarchRule.Match(@"//.*$", "comment"),
                MonarchRule.Match(@"/\*", "comment", "@comment"),
            },
            ["comment"] = new MonarchRule[]
            {
                MonarchRule.Match(@"[^*]+", "comment"),
                MonarchRule.Match(@"\*/", "comment", "@pop"),
                MonarchRule.Match(@"\*", "comment"),
            },
            ["stringDouble"] = new MonarchRule[]
            {
                MonarchRule.Match("[^\\\\\"]+", "string"),
                MonarchRule.Match(@"\\.", "string.escape"),
                MonarchRule.Match("\"", "string.quote", "@pop"),
            },
            ["stringSingle"] = new MonarchRule[]
            {
                MonarchRule.Match("[^\\']+", "string"),
                MonarchRule.Match(@"\\.", "string.escape"),
                MonarchRule.Match("'", "string.quote", "@pop"),
            },
        }
    };
}
