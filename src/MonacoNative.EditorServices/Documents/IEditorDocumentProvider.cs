namespace MonacoNative.Documents;

/// <summary>
/// Owns the persistent content and identity of one editor document. Providers do not own
/// an editor's working copy: a tab becomes clean only after its requested write succeeds.
/// </summary>
public interface IEditorDocumentProvider
{
    Uri Uri { get; }
    string DisplayName { get; }
    string? LanguageId { get; }
    bool IsReadOnly { get; }

    Task<EditorDocumentContent> ReadAsync(CancellationToken cancellationToken = default);
    Task WriteAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>Text is returned verbatim, including its original line endings.</summary>
public sealed record EditorDocumentContent(string Text, string? LanguageId = null);

/// <summary>The saved resource changed since it was last read or successfully written.</summary>
public sealed class EditorDocumentConflictException : IOException
{
    public EditorDocumentConflictException(Uri uri, string message) : base(message) => Uri = uri;

    public Uri Uri { get; }
}
