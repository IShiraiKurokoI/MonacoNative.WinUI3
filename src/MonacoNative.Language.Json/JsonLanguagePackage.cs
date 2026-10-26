using MonacoNative.Language;

namespace MonacoNative.Language.Json;

public sealed class JsonLanguagePackage : ILanguagePackage
{
    public string Id => "json";
    public JsonSchemaRegistry Schemas { get; } = new();
    public IReadOnlyList<string> LanguageIds { get; } = new[] { "json", "jsonc" };
    public LanguageSourcePortStatus SourcePortStatus => LanguageSourcePortStatus.PartialPort;

    public IDisposable Register(MonacoLanguageService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        var regs = new LanguageRegistrations();
        try
        {
            regs.Add(service.Register(new LanguageExtensionPoint("json", Aliases: new[] { "JSON" }, Extensions: new[] { ".json" }, Mimetypes: new[] { "application/json" })));
            regs.Add(service.Register(new LanguageExtensionPoint("jsonc", Aliases: new[] { "JSON with Comments" }, Extensions: new[] { ".jsonc" })));
            var config = new LanguageConfiguration
            {
                Brackets = new[] { new CharacterPair("{", "}"), new CharacterPair("[", "]") },
                AutoClosingPairs = new[] { new AutoClosingPair("{", "}"), new AutoClosingPair("[", "]"), new AutoClosingPair("\"", "\"", "string") },
                WordPattern = @"[A-Za-z_]\w*",
                IncreaseIndentPattern = @"^.*[\{\[]\s*$",
                DecreaseIndentPattern = @"^\s*[}\]].*$"
            };
            regs.Add(service.SetLanguageConfiguration("json", config));
            regs.Add(service.SetLanguageConfiguration("jsonc", new LanguageConfiguration
            {
                Comments = new CommentRule("//", new CharacterPair("/*", "*/")), Brackets = config.Brackets,
                AutoClosingPairs = config.AutoClosingPairs, WordPattern = config.WordPattern,
                IncreaseIndentPattern = config.IncreaseIndentPattern, DecreaseIndentPattern = config.DecreaseIndentPattern
            }));
            foreach (var (id, comments) in new[] { ("json", false), ("jsonc", true) })
            {
                regs.Add(service.SetMonarchTokensProvider(id, CreateJsonMonarch(comments)));
                var native = new JsonNativeLanguageService(Schemas);
                regs.Add(service.RegisterCompletionItemProvider(LanguageSelector.ForLanguage(id), native));
                regs.Add(service.RegisterHoverProvider(LanguageSelector.ForLanguage(id), native));
                regs.Add(service.RegisterFoldingRangeProvider(LanguageSelector.ForLanguage(id), new JsonFolding()));
                regs.Add(service.RegisterDocumentDiagnosticsProvider(LanguageSelector.ForLanguage(id), native));
                var formatter = new JsoncFormattingProvider();
                regs.Add(service.RegisterDocumentFormattingEditProvider(LanguageSelector.ForLanguage(id), formatter));
                regs.Add(service.RegisterDocumentRangeFormattingEditProvider(LanguageSelector.ForLanguage(id), formatter));
            }
            return regs;
        }
        catch { regs.Dispose(); throw; }
    }

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
