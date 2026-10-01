namespace MonacoNative.Language;

/// <summary>
/// Lightweight built-in lexical highlighting for SampleApp languages. These are Monarch-like
/// tokenizers, not parsers or language servers; richer semantic features require LSP providers.
/// </summary>
public static partial class BuiltinLanguages
{
    private static readonly string[] JavaScriptKeywords =
    {
        "async", "await", "break", "case", "catch", "class", "const", "continue", "debugger", "default",
        "delete", "do", "else", "export", "extends", "false", "finally", "for", "from", "function",
        "if", "import", "in", "instanceof", "let", "new", "null", "of", "return", "static", "super",
        "switch", "this", "throw", "true", "try", "typeof", "undefined", "var", "void", "while", "yield"
    };

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

    private static void RegisterAdditionalLanguages(MonacoLanguageService service)
    {
        service.Register(new LanguageExtensionPoint("html", Aliases: new[] { "HTML" }, Extensions: new[] { ".html", ".htm" }, Mimetypes: new[] { "text/html" }));
        service.Register(new LanguageExtensionPoint("javascript", Aliases: new[] { "JavaScript", "js" }, Extensions: new[] { ".js", ".mjs", ".cjs" }, Mimetypes: new[] { "text/javascript" }));
        service.Register(new LanguageExtensionPoint("csharp", Aliases: new[] { "C#", "CSharp" }, Extensions: new[] { ".cs" }, Mimetypes: new[] { "text/x-csharp" }));

        var braces = new[] { new CharacterPair("{", "}"), new CharacterPair("[", "]"), new CharacterPair("(", ")") };
        var bracesAndQuotes = new[] { new AutoClosingPair("{", "}"), new AutoClosingPair("[", "]"), new AutoClosingPair("(", ")"), new AutoClosingPair("\"", "\"", "string"), new AutoClosingPair("'", "'", "string") };
        foreach (var id in new[] { "javascript", "csharp" })
            service.SetLanguageConfiguration(id, new LanguageConfiguration
            {
                Comments = new CommentRule("//", new CharacterPair("/*", "*/")),
                Brackets = braces, AutoClosingPairs = bracesAndQuotes,
                SurroundingPairs = braces.Concat(new[] { new CharacterPair("\"", "\""), new CharacterPair("'", "'") }).ToArray(),
                WordPattern = @"[A-Za-z_$]\w*"
            });
        service.SetLanguageConfiguration("html", new LanguageConfiguration
        {
            Comments = new CommentRule(BlockComment: new CharacterPair("<!--", "-->")),
            Brackets = new[] { new CharacterPair("<", ">") },
            AutoClosingPairs = new[] { new AutoClosingPair("\"", "\""), new AutoClosingPair("'", "'") },
            WordPattern = @"[A-Za-z][A-Za-z0-9:_-]*"
        });
        service.SetMonarchTokensProvider("javascript", CreateScriptMonarch(JavaScriptKeywords));
        service.SetMonarchTokensProvider("csharp", CreateScriptMonarch(CSharpKeywords));
        service.SetMonarchTokensProvider("html", CreateHtmlMonarch());
    }

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

    private static MonarchLanguage CreateHtmlMonarch() => new()
    {
        IgnoreCase = true,
        Tokenizer = new Dictionary<string, IReadOnlyList<MonarchRule>>(StringComparer.Ordinal)
        {
            ["root"] = new MonarchRule[]
            {
                MonarchRule.Match(@"<!--", "comment", "@comment"),
                MonarchRule.Match(@"<!DOCTYPE[^>]*>", "keyword"),
                MonarchRule.Match(@"</?[A-Za-z][\w:-]*", "keyword", "@tag"),
                MonarchRule.Match(@"&[#A-Za-z0-9]+;", "string.escape"),
                MonarchRule.Match(@"[^<&]+", ""),
            },
            ["tag"] = new MonarchRule[]
            {
                MonarchRule.Match(@"\s+", "white"),
                MonarchRule.Match(@"/?>", "delimiter.bracket", "@pop"),
                MonarchRule.Match(@"[A-Za-z_:][\w:.-]*", "type"),
                MonarchRule.Match(@"=", "operator"),
                MonarchRule.Match("\"", "string.quote", "@double"),
                MonarchRule.Match("'", "string.quote", "@single"),
            },
            ["double"] = new MonarchRule[]
            {
                MonarchRule.Match("[^\"]+", "string"),
                MonarchRule.Match("\"", "string.quote", "@pop"),
            },
            ["single"] = new MonarchRule[]
            {
                MonarchRule.Match("[^']+", "string"),
                MonarchRule.Match("'", "string.quote", "@pop"),
            },
            ["comment"] = new MonarchRule[]
            {
                MonarchRule.Match(@"-->", "comment", "@pop"),
                MonarchRule.Match(@"[^-]+", "comment"),
                MonarchRule.Match(@"-", "comment"),
            }
        }
    };
}
