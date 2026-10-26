using MonacoNative.Language;

namespace MonacoNative.Lsp;

/// <summary>Compatibility facade. Hosts should retain SnippetText and use SnippetSession.</summary>
public static class LspSnippetText
{
    public readonly record struct Expanded(string Text, int? CaretOffset);
    public static string Expand(string snippet) => ExpandWithCursor(snippet).Text;
    public static Expanded ExpandWithCursor(string snippet)
    {
        var expansion = SnippetParser.Parse(snippet);
        return new(expansion.Text, expansion.InitialOffset);
    }
}
