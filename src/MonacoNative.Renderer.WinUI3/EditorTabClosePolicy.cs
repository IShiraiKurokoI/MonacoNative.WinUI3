namespace MonacoNative.Renderer.WinUI3;

internal static class EditorTabClosePolicy
{
    internal static T[] GetTargets<T>(EditorTabCloseScope scope, IReadOnlyList<T> documents, T context,
        IReadOnlyCollection<T> selection, Func<T, bool> isPinned, Func<T, bool> isDirty) where T : class
    {
        var chosen = selection.ToHashSet();
        var contextIndex = -1;
        for (var index = 0; index < documents.Count; index++)
        {
            if (EqualityComparer<T>.Default.Equals(documents[index], context))
            {
                contextIndex = index;
                break;
            }
        }

        return documents.Where((document, index) => scope switch
        {
            EditorTabCloseScope.Selected => chosen.Contains(document),
            EditorTabCloseScope.Others => !chosen.Contains(document) && !isPinned(document),
            EditorTabCloseScope.ToRight => index > contextIndex && !isPinned(document),
            EditorTabCloseScope.Saved => !isDirty(document) && !isPinned(document),
            EditorTabCloseScope.All => !isPinned(document),
            _ => false,
        }).ToArray();
    }

    internal static bool CanCloseOthers<T>(IReadOnlyList<T> documents, T context,
        IReadOnlyCollection<T> selection, Func<T, bool> isPinned, Func<T, bool> isDirty) where T : class =>
        GetTargets(EditorTabCloseScope.Others, documents, context, selection, isPinned, isDirty).Length > 0;

    internal static bool CanCloseToRight<T>(IReadOnlyList<T> documents, T context, int selectionCount,
        Func<T, bool> isPinned, Func<T, bool> isDirty) where T : class =>
        selectionCount == 1 && GetTargets(EditorTabCloseScope.ToRight, documents, context,
            Array.Empty<T>(), isPinned, isDirty).Length > 0;
}

internal static class EditorGroupLifecyclePolicy
{
    internal static bool ShouldRemoveEmptyGroup(bool isOwner, IEnumerable<(bool IsImplicit, bool IsDirty)> documents) =>
        !isOwner && documents.All(document => document.IsImplicit && !document.IsDirty);
}
