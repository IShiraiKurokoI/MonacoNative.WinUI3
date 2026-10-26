using MonacoNative.Language;

namespace MonacoNative.Language.Css;

public sealed class CssLanguagePackage : ILanguagePackage
{
    public string Id => "css";
    public CssCustomData Data { get; } = new();
    public IReadOnlyList<string> LanguageIds { get; } = new[] { "css", "scss", "less" };
    public LanguageSourcePortStatus SourcePortStatus => LanguageSourcePortStatus.PartialPort;

    public IDisposable Register(MonacoLanguageService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        var regs = new LanguageRegistrations();
        try
        {
            foreach (var (id, extension) in new[] { ("css", ".css"), ("scss", ".scss"), ("less", ".less") })
            {
                regs.Add(service.Register(new LanguageExtensionPoint(id, Aliases: new[] { id.ToUpperInvariant() }, Extensions: new[] { extension })));
                regs.Add(service.SetLanguageConfiguration(id, new LanguageConfiguration
                {
                    Comments = new CommentRule(LineComment: id == "css" ? null : "//", BlockComment: new CharacterPair("/*", "*/")),
                    Brackets = new[] { new CharacterPair("{", "}"), new CharacterPair("(", ")") },
                    AutoClosingPairs = new[] { new AutoClosingPair("{", "}"), new AutoClosingPair("(", ")"), new AutoClosingPair("\"", "\""), new AutoClosingPair("'", "'") },
                    WordPattern = @"[-_A-Za-z][-_A-Za-z0-9]*",
                    IncreaseIndentPattern = @"^.*\{\s*$",
                    DecreaseIndentPattern = @"^\s*}.*$"
                }));
                regs.Add(service.SetMonarchTokensProvider(id, CreateCssMonarch()));
                var native = new CssSyntaxCompletionProvider(Data);
                var selector = LanguageSelector.ForLanguage(id);
                regs.Add(service.RegisterCompletionItemProvider(selector, native));
                regs.Add(service.RegisterHoverProvider(selector, native));
                regs.Add(service.RegisterFoldingRangeProvider(selector, new CssFolding()));
                regs.Add(service.RegisterDefinitionProvider(selector, native));
                if (id == "css") regs.Add(service.RegisterDocumentDiagnosticsProvider(selector, native));
                var formatter = new BasicSourceFormattingProvider(BasicSourceFormattingMode.BraceLanguage);
                regs.Add(service.RegisterDocumentFormattingEditProvider(selector, formatter));
                regs.Add(service.RegisterDocumentRangeFormattingEditProvider(selector, formatter));
            }
            return regs;
        }
        catch { regs.Dispose(); throw; }
    }

    public static MonarchLanguage CreateCssMonarch() => new()
    {
        Tokenizer = new Dictionary<string, IReadOnlyList<MonarchRule>>(StringComparer.Ordinal)
        {
            ["root"] = new MonarchRule[]
            {
                MonarchRule.Match(@"\s+", "white"),
                MonarchRule.Match(@"/\*", "comment", "@comment"),
                MonarchRule.Match(@"@[A-Za-z-]+", "keyword"),
                MonarchRule.Match(@"#[a-fA-F0-9]{3,8}\b", "number.hex"),
                MonarchRule.Match(@"[.#][\w-]+", "type"),
                MonarchRule.Match(@"[-_A-Za-z][\w-]*(?=\s*:)", "attribute.name"),
                MonarchRule.Match(@"[-_A-Za-z][\w-]*", "identifier"),
                MonarchRule.Match(@"-?\d+(?:\.\d+)?(?:px|em|rem|vh|vw|%)?", "number"),
                MonarchRule.Match("\"", "string.quote", "@double"),
                MonarchRule.Match("'", "string.quote", "@single"),
                MonarchRule.Match(@"[{}()]", "delimiter.bracket"),
                MonarchRule.Match(@"[:;,>+*~=]", "delimiter"),
            },
            ["comment"] = new MonarchRule[]
            {
                MonarchRule.Match(@"[^*]+", "comment"), MonarchRule.Match(@"\*/", "comment", "@pop"), MonarchRule.Match(@"\*", "comment")
            },
            ["double"] = new MonarchRule[]
            {
                MonarchRule.Match("[^\\\"]+", "string"), MonarchRule.Match("\"", "string.quote", "@pop")
            },
            ["single"] = new MonarchRule[]
            {
                MonarchRule.Match("[^']+", "string"), MonarchRule.Match("'", "string.quote", "@pop")
            }
        }
    };
}
