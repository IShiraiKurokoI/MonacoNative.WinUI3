using MonacoNative.Language;

namespace MonacoNative.Renderer.WinUI3;

public sealed partial class MonacoEditor
{
    private sealed record EditorRequestContext(EditorRequestIdentity Identity, LanguageModelSnapshot Snapshot)
    {
        public LanguagePosition Position => new(Identity.Caret.LineNumber, Identity.Caret.Column);
        public LanguageRange Selection => new LanguageRange(Identity.Anchor.LineNumber, Identity.Anchor.Column,
            Identity.Caret.LineNumber, Identity.Caret.Column).Normalize();
    }

    private EditorRequestIdentity CaptureEditorRequestIdentity() =>
        new(_model, _documentActivationSequence, DocumentUri, LanguageId, _model.VersionId, _anchor, _active);

    private EditorRequestContext CaptureEditorRequest()
    {
        VerifyDocumentAccess();
        return new(CaptureEditorRequestIdentity(), LanguageModelSnapshot.Capture(_languageDocument));
    }

    private bool IsEditorRequestCurrent(EditorRequestIdentity request,
        bool checkSelection = false, bool checkVersion = true) =>
        !IsDisposed && request.Matches(CaptureEditorRequestIdentity(), checkSelection, checkVersion);

    private bool IsEditorRequestCurrent(EditorRequestContext request,
        bool checkSelection = false, bool checkVersion = true) =>
        IsEditorRequestCurrent(request.Identity, checkSelection, checkVersion);
}
