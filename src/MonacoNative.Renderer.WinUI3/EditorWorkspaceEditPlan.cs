using MonacoNative.Core;
using MonacoNative.Documents;
using MonacoNative.Language;

namespace MonacoNative.Renderer.WinUI3;

/// <summary>Prevalidated edits for one open document; no XAML or input dependencies.</summary>
internal sealed class EditorWorkspaceEditPlan
{
    private readonly (nuint Start, nuint End, string Text)[] _edits;
    private readonly ulong _version;
    private readonly Uri _uri;
    private readonly string _languageId;
    public MonacoEditorDocument Document { get; }

    public EditorWorkspaceEditPlan(MonacoEditorDocument document, IEnumerable<WorkspaceTextEdit> edits)
    {
        Document = document;
        if (document.IsClosed) throw new InvalidOperationException("A workspace edit targets a closed document.");
        if (document.IsReadOnly) throw new InvalidOperationException($"{document.DisplayName} is read-only.");
        _version = document.Model.VersionId;
        _uri = document.Uri;
        _languageId = document.LanguageId;
        _edits = edits.Select(edit =>
        {
            var range = edit.Range;
            var model = document.Model;
            if (range.StartLineNumber < 1 || range.EndLineNumber < range.StartLineNumber ||
                range.EndLineNumber > (int)model.LineCount || range.StartColumn < 1 || range.EndColumn < 1 ||
                range.StartColumn > model.GetLineMaxColumn(range.StartLineNumber) ||
                range.EndColumn > model.GetLineMaxColumn(range.EndLineNumber) ||
                (range.StartLineNumber == range.EndLineNumber && range.StartColumn > range.EndColumn))
                throw new ArgumentException($"A workspace edit contains an invalid range for {document.DisplayName}.", nameof(edits));
            return (Start: model.GetOffsetAt(new(range.StartLineNumber, range.StartColumn)),
                End: model.GetOffsetAt(new(range.EndLineNumber, range.EndColumn)), Text: edit.Text ?? string.Empty);
        }).OrderBy(edit => edit.Start).ThenBy(edit => edit.End).ToArray();
        for (var i = 1; i < _edits.Length; ++i)
            if (_edits[i].Start < _edits[i - 1].End || _edits[i].Start == _edits[i - 1].Start)
                throw new ArgumentException($"A workspace edit contains overlapping ranges for {document.DisplayName}.", nameof(edits));
    }

    public void ValidateCurrent()
    {
        if (Document.IsClosed || Document.IsReadOnly || Document.Model.VersionId != _version ||
            !EditorDocumentUriComparer.Instance.Equals(Document.Uri, _uri) || Document.LanguageId != _languageId)
            throw new InvalidOperationException($"{Document.DisplayName} changed before the workspace edit could be applied.");
    }

    public void Apply()
    {
        // Applying another document's plan can invoke host model-change callbacks.
        // Recheck here in case such a callback changed this target after the initial
        // all-document preflight but before its own first mutation.
        ValidateCurrent();
        for (var i = _edits.Length - 1; i >= 0; --i)
        {
            var edit = _edits[i];
            Document.Model.Replace(edit.Start, edit.End, edit.Text);
        }
    }
}
