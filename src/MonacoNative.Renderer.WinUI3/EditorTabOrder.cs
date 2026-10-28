namespace MonacoNative.Renderer.WinUI3;

internal static class EditorTabOrder
{
    internal static T[] MoveBlock<T>(IReadOnlyList<T> current, IReadOnlyCollection<T> moved,
        int insertionIndex, Func<T, bool> isPinned) where T : notnull
    {
        var selected = moved.ToHashSet();
        if (current.Distinct().Count() != current.Count || selected.Count != moved.Count ||
            selected.Any(item => !current.Contains(item)))
            throw new ArgumentException("A tab move must contain distinct open documents.");
        var remaining = current.Where(item => !selected.Contains(item)).ToList();
        var block = current.Where(selected.Contains).ToArray();
        remaining.InsertRange(Math.Clamp(insertionIndex, 0, remaining.Count), block);
        // Sticky tabs never become ordinary tabs merely by crossing the boundary.
        return remaining.Where(isPinned).Concat(remaining.Where(item => !isPinned(item))).ToArray();
    }
}

public enum EditorGroupDirection { Up, Down, Left, Right }
public enum EditorTabCloseScope { Selected, Others, ToRight, Saved, All }
