namespace MonacoNative.Documents;

/// <summary>
/// An independent in-memory resource. Initial text is its saved content; subsequent editor
/// changes remain in the tab's working copy until WriteAsync is called (for example by Save).
/// </summary>
public sealed class MemoryDocumentProvider : IEditorDocumentProvider
{
    private readonly object _gate = new();
    private string _text;

    public MemoryDocumentProvider(string text = "", string displayName = "Untitled",
        string? languageId = null, Uri? uri = null, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (uri is not null && !uri.IsAbsoluteUri)
            throw new ArgumentException("A document URI must be absolute.", nameof(uri));
        _text = text;
        DisplayName = displayName;
        Uri = uri ?? new Uri($"inmemory://model/{Guid.NewGuid():N}/{Uri.EscapeDataString(displayName)}");
        LanguageId = languageId ?? EditorDocumentLanguages.InferFromPath(displayName);
        IsReadOnly = isReadOnly;
    }

    public Uri Uri { get; }
    public string DisplayName { get; }
    public string? LanguageId { get; }
    public bool IsReadOnly { get; }

    /// <summary>The last successfully saved content, not the editor's unsaved working copy.</summary>
    public string Text { get { lock (_gate) return _text; } }

    public Task<EditorDocumentContent> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(new EditorDocumentContent(_text, LanguageId));
    }

    public Task WriteAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsReadOnly) throw new UnauthorizedAccessException($"'{DisplayName}' is read-only.");
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _text = text;
        }
        return Task.CompletedTask;
    }
}
