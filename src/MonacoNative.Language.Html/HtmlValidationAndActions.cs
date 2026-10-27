using MonacoNative.Language;

namespace MonacoNative.Language.Html;

/// <summary>Checks enumerated HTML attributes and offers conservative tag repairs.</summary>
public sealed class HtmlValidationAndActions : IDocumentDiagnosticsProvider, ICodeActionProvider
{
    private readonly HtmlCustomData _data;
    public HtmlValidationAndActions(HtmlCustomData data) => _data = data;
    public IReadOnlyList<string> ProvidedCodeActionKinds { get; } = new[] { "quickfix" };

    private sealed record AttributeIssue(int Start, int End, string Tag, string Attribute,
        string Actual, IReadOnlyList<string> Allowed, char Quote);

    private IEnumerable<AttributeIssue> FindInvalidAttributes(string text, CancellationToken ct)
    {
        var scanner = new HtmlScanner(text, cancellationToken: ct);
        string tag = "", attribute = "";
        while (scanner.Scan() != HtmlTokenType.EOS)
        {
            ct.ThrowIfCancellationRequested();
            if (scanner.TokenType == HtmlTokenType.StartTag) tag = scanner.TokenText;
            else if (scanner.TokenType == HtmlTokenType.AttributeName) attribute = scanner.TokenText;
            else if (scanner.TokenType == HtmlTokenType.AttributeValue && tag.Length > 0 && attribute.Length > 0)
            {
                var raw = scanner.TokenText;
                var quote = raw.Length > 1 && raw[0] is '\'' or '"' ? raw[0] : '\0';
                var actual = quote == '\0' ? raw : raw.Trim(quote);
                // Browsing-context names and script MIME types are extensible HTML values,
                // not closed enums even if built-in completion offers common suggestions.
                if ((tag.Equals("a", StringComparison.OrdinalIgnoreCase) && attribute.Equals("target", StringComparison.OrdinalIgnoreCase)) ||
                    (tag.Equals("script", StringComparison.OrdinalIgnoreCase) && attribute.Equals("type", StringComparison.OrdinalIgnoreCase)))
                { attribute = ""; continue; }
                var definition = _data.AttributesFor(tag)
                    .FirstOrDefault(item => item.Name.Equals(attribute, StringComparison.OrdinalIgnoreCase));
                if (definition?.Values is { Count: > 0 } allowed && actual.Length > 0 &&
                    !actual.Contains("{{", StringComparison.Ordinal) && !actual.Contains("${", StringComparison.Ordinal) &&
                    !allowed.Contains(actual, StringComparer.OrdinalIgnoreCase))
                    yield return new AttributeIssue(scanner.TokenOffset + (quote == '\0' ? 0 : 1),
                        scanner.TokenEnd - (quote == '\0' ? 0 : 1), tag, attribute, actual, allowed, quote);
                attribute = "";
            }
            else if (scanner.TokenType is HtmlTokenType.StartTagClose or HtmlTokenType.StartTagSelfClose)
                attribute = "";
        }
    }

    public ValueTask<IReadOnlyList<LanguageDiagnostic>?> ProvideDiagnosticsAsync(ILanguageTextModel model,
        CancellationToken ct)
    {
        var snapshot = new LanguageTextSnapshot(model.GetValue());
        var diagnostics = FindInvalidAttributes(snapshot.Text, ct).Select(issue =>
            new LanguageDiagnostic(snapshot.Range(issue.Start, issue.End),
                "Unsupported value '" + issue.Actual + "' for " + issue.Tag + "." + issue.Attribute + ".",
                DiagnosticSeverity.Warning, "HtmlAttributeValue", "html-custom-data")).ToList();
        return ValueTask.FromResult<IReadOnlyList<LanguageDiagnostic>?>(diagnostics);
    }

    public ValueTask<IReadOnlyList<CodeAction>?> ProvideCodeActionsAsync(ILanguageTextModel model,
        LanguageRange range, CodeActionContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (context.Only is not null && !"quickfix".StartsWith(context.Only, StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult<IReadOnlyList<CodeAction>?>(Array.Empty<CodeAction>());
        var snapshot = new LanguageTextSnapshot(model.GetValue());
        var at = snapshot.OffsetAt(new(range.StartLineNumber, range.StartColumn));
        var actions = new List<CodeAction>();
        foreach (var issue in FindInvalidAttributes(snapshot.Text, ct))
        {
            if (at < issue.Start || at > issue.End) continue;
            foreach (var allowed in issue.Allowed.Take(15))
                actions.Add(new CodeAction("Use '" + allowed + "'", "quickfix",
                    new[] { new LanguageTextEdit(snapshot.Range(issue.Start, issue.End), allowed) }));
        }
        var document = HtmlParser.Parse(snapshot.Text, createNodesForOrphanEndTags: true, cancellationToken: ct);
        var node = document.FindNodeAt(at);
        if (node.Tag is { Length: > 0 } tag && !HtmlParser.VoidElements.Contains(tag))
        {
            if (!node.Closed && node.StartTagEnd is not null && node.EndTagStart is null &&
                node.End == snapshot.Text.Length)
                actions.Add(new CodeAction("Insert closing </" + tag + ">", "quickfix",
                    new[] { new LanguageTextEdit(snapshot.Range(snapshot.Text.Length, snapshot.Text.Length), "</" + tag + ">") }));
            // Orphan end tags appear as synthetic nodes with a closing tag but no opening tag.
            if (node.EndTagStart == node.Start && node.Closed)
                actions.Add(new CodeAction("Remove unmatched closing tag", "quickfix",
                    new[] { new LanguageTextEdit(snapshot.Range(node.Start, node.End), "") }));
        }
        return ValueTask.FromResult<IReadOnlyList<CodeAction>?>(actions);
    }
}
