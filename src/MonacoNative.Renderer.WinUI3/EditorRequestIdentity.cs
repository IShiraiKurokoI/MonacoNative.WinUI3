using MonacoNative.Core;

namespace MonacoNative.Renderer.WinUI3;

/// <summary>
/// Identifies the document activation that issued an asynchronous editor request.
/// Model identity distinguishes closed/reopened resources, while Activation distinguishes
/// leaving a tab and returning to the same model before an old request completes.
/// </summary>
internal readonly record struct EditorRequestIdentity(MonacoTextModel Model, long Activation,
    Uri Uri, string LanguageId, ulong Version, MonacoPosition Anchor, MonacoPosition Caret)
{
    public bool Matches(EditorRequestIdentity current, bool checkSelection = false, bool checkVersion = true) =>
        ReferenceEquals(Model, current.Model) && Activation == current.Activation &&
        Uri == current.Uri && LanguageId == current.LanguageId &&
        (!checkVersion || Version == current.Version) &&
        (!checkSelection || Anchor == current.Anchor && Caret == current.Caret);
}
