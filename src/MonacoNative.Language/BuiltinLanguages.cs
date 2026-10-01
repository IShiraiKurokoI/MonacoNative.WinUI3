namespace MonacoNative.Language;

public static partial class BuiltinLanguages
{
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

    public static void RegisterDefaults(MonacoLanguageService service)
    {
        service.Register(new LanguageExtensionPoint("plaintext", Aliases: new[] { "Plain Text", "text" }, Extensions: new[] { ".txt" }, Mimetypes: new[] { "text/plain" }));
        service.Register(new LanguageExtensionPoint("cpp", Aliases: new[] { "C++", "cpp" }, Extensions: new[] { ".cpp", ".cc", ".cxx", ".hpp", ".h" }, Mimetypes: new[] { "text/x-c++src" }));
        service.Register(new LanguageExtensionPoint("json", Aliases: new[] { "JSON", "json" }, Extensions: new[] { ".json" }, Mimetypes: new[] { "application/json" }));
        service.Register(new LanguageExtensionPoint("jsonc", Aliases: new[] { "JSON with Comments", "jsonc" }, Extensions: new[] { ".jsonc" }));

        var pairs = new[] { new CharacterPair("{", "}"), new CharacterPair("[", "]"), new CharacterPair("(", ")") };
        service.SetLanguageConfiguration("cpp", new LanguageConfiguration
        {
            Comments = new CommentRule("//", new CharacterPair("/*", "*/")),
            Brackets = pairs,
            AutoClosingPairs = new[] { new AutoClosingPair("{", "}"), new AutoClosingPair("[", "]"), new AutoClosingPair("(", ")"), new AutoClosingPair("\"", "\"", "string"), new AutoClosingPair("'", "'", "string") },
            SurroundingPairs = pairs.Concat(new[] { new CharacterPair("\"", "\""), new CharacterPair("'", "'") }).ToArray(),
            WordPattern = @"[A-Za-z_]\w*"
        });
        service.SetLanguageConfiguration("json", new LanguageConfiguration
        {
            Brackets = new[] { new CharacterPair("{", "}"), new CharacterPair("[", "]") },
            AutoClosingPairs = new[] { new AutoClosingPair("{", "}"), new AutoClosingPair("[", "]"), new AutoClosingPair("\"", "\"", "string") },
            WordPattern = @"[A-Za-z_]\w*"
        });
        service.SetLanguageConfiguration("jsonc", service.Languages.GetLanguageConfiguration("json")!);

        var cpp = CreateCppMonarch();
        service.SetMonarchTokensProvider("cpp", cpp);
        service.SetMonarchTokensProvider("json", CreateJsonMonarch(false));
        service.SetMonarchTokensProvider("jsonc", CreateJsonMonarch(true));
        RegisterAdditionalLanguages(service);
    }

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

    public static MonarchLanguage CreateJsonMonarch(bool comments) => new()
    {
        DefaultToken = string.Empty,
        WordLists = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal) { ["keywords"] = new[] { "true", "false", "null" } },
        Tokenizer = new Dictionary<string, IReadOnlyList<MonarchRule>>(StringComparer.Ordinal)
        {
            ["root"] = new MonarchRule[]
            {
                MonarchRule.Match(@"[ \t\r\n]+", "white"),
                MonarchRule.Match("\"", "string.quote", "@string"),
                MonarchRule.Match(@"-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?", "number"),
                MonarchRule.Match(@"[A-Za-z_]\w*", new MonarchAction("identifier", Cases: new Dictionary<string,string> { ["@keywords"] = "keyword", ["@default"] = "invalid" })),
                MonarchRule.Match(@"[{}\[\]]", "delimiter.bracket"),
                MonarchRule.Match(@"[:,]", "delimiter"),
                MonarchRule.Match(comments ? @"//.*$" : @"(?!)", "comment"),
                MonarchRule.Match(comments ? @"/\*" : @"(?!)", "comment", "@comment"),
            },
            ["string"] = new MonarchRule[]
            {
                MonarchRule.Match("[^\\\\\"]+", "string"),
                MonarchRule.Match("\\\\(?:[\"\\\\/bfnrt]|u[0-9a-fA-F]{4})", "string.escape"),
                MonarchRule.Match("\"", "string.quote", "@pop"),
                MonarchRule.Match(@"\\.", "string.invalid"),
            },
            ["comment"] = new MonarchRule[]
            {
                MonarchRule.Match(@"[^*]+", "comment"),
                MonarchRule.Match(@"\*/", "comment", "@pop"),
                MonarchRule.Match(@"\*", "comment"),
            },
        }
    };
}
