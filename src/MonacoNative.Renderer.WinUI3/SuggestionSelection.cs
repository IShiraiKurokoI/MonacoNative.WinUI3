namespace MonacoNative.Renderer.WinUI3;

/// <summary>
/// Focus-free navigation for the completion overlay. The editor keeps keyboard focus;
/// selecting an entry must never move the text caret.
/// </summary>
internal sealed class SuggestionSelection
{
    public int Count { get; private set; }
    public int SelectedIndex { get; private set; } = -1;

    public void Reset(int count)
    {
        Count = Math.Max(0, count);
        SelectedIndex = Count == 0 ? -1 : 0;
    }

    public void Move(int delta)
    {
        if (Count == 0) return;
        SelectedIndex = (int)(((long)SelectedIndex + delta % Count + Count) % Count);
    }

    public void Page(int delta)
    {
        if (Count == 0) return;
        SelectedIndex = (int)Math.Clamp((long)SelectedIndex + delta, 0L, Count - 1L);
    }

    public void Select(int index)
    {
        if (index >= 0 && index < Count) SelectedIndex = index;
    }
}
