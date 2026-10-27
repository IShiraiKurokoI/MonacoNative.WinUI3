using MonacoNative.Language;

namespace MonacoNative.Language.Html;

public sealed class HtmlLanguagePackage : ILanguagePackage
{
    public string Id => "html";
    public HtmlCustomData Data { get; } = new();
    public IReadOnlyList<string> LanguageIds { get; } = new[] { "html" };
    public LanguageSourcePortStatus SourcePortStatus => LanguageSourcePortStatus.PartialPort;

    public IDisposable Register(MonacoLanguageService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        var regs = new LanguageRegistrations();
        try
        {
            regs.Add(service.Register(new LanguageExtensionPoint("html", Aliases: new[] { "HTML" }, Extensions: new[] { ".html", ".htm" }, Mimetypes: new[] { "text/html" })));
            regs.Add(service.SetLanguageConfiguration("html", new LanguageConfiguration
            {
                Comments = new CommentRule(BlockComment: new CharacterPair("<!--", "-->")),
                Brackets = new[] { new CharacterPair("<", ">") },
                AutoClosingPairs = new[] { new AutoClosingPair("\"", "\""), new AutoClosingPair("'", "'") },
                WordPattern = @"[A-Za-z][A-Za-z0-9:_-]*",
                IncreaseIndentPattern = @"^\s*<(?!/|!|\?)[\w:-]+(?:\s+[^<>]*)?>(?!.*</)",
                DecreaseIndentPattern = @"^\s*</"
            }));
            regs.Add(service.SetMonarchTokensProvider("html", CreateHtmlMonarch()));
            var native = new HtmlSyntaxCompletionProvider(Data);
            regs.Add(service.RegisterCompletionItemProvider(LanguageSelector.ForLanguage("html"), native));
            regs.Add(service.RegisterHoverProvider(LanguageSelector.ForLanguage("html"), native));
            regs.Add(service.RegisterFoldingRangeProvider(LanguageSelector.ForLanguage("html"), new HtmlFolding()));
            regs.Add(service.RegisterRenameProvider(LanguageSelector.ForLanguage("html"), new HtmlTagRenameProvider()));
            // Embedded providers use the host registry; this package does not force JS/CSS dependencies.
            var embedded = new HtmlEmbeddedLanguageService(service);
            regs.Add(service.RegisterDocumentDiagnosticsProvider(LanguageSelector.ForLanguage("html"), embedded));
            regs.Add(service.RegisterDocumentSemanticTokensProvider(LanguageSelector.ForLanguage("html"), embedded));
            var validation = new HtmlValidationAndActions(Data);
            regs.Add(service.RegisterDocumentDiagnosticsProvider(LanguageSelector.ForLanguage("html"), validation));
            regs.Add(service.RegisterCodeActionProvider(LanguageSelector.ForLanguage("html"), validation));
            var formatter = new BasicSourceFormattingProvider(BasicSourceFormattingMode.Html);
            regs.Add(service.RegisterDocumentFormattingEditProvider(LanguageSelector.ForLanguage("html"), formatter));
            regs.Add(service.RegisterDocumentRangeFormattingEditProvider(LanguageSelector.ForLanguage("html"), formatter));
            return regs;
        }
        catch { regs.Dispose(); throw; }
    }

    public static MonarchLanguage CreateHtmlMonarch() => new()
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
