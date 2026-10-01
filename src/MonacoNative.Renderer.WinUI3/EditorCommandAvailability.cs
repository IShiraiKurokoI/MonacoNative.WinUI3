namespace MonacoNative.Renderer.WinUI3;

/// <summary>
/// Effective permissions for commands reached through the editor's command service.
/// Default derives from the built-in command category; explicitly setting a mode in XAML
/// overrides that default without changing keybinding registration or editing capabilities.
/// </summary>
public enum EditorCommandAvailability
{
    Default = 0,
    ContextMenuAndShortcut = 1,
    ShortcutOnly = 2,
    Disabled = 3,
}

internal static class EditorCommandAccessPolicy
{
    // Native editing/navigation commands do not become context-menu entries by default.
    // These are the fundamental immediate editing actions users expect on right click.
    private static readonly HashSet<string> BasicMenuCommands = new(StringComparer.Ordinal)
    {
        "undo", "redo", "editor.action.clipboardCutAction", "editor.action.clipboardCopyAction",
        "editor.action.clipboardPasteAction", "editor.action.selectAll",
    };

    public static bool IsBasic(string commandId) => BasicMenuCommands.Contains(commandId);

    public static EditorCommandAvailability DefaultFor(string commandId, bool menuCandidate, bool toggle)
    {
        _ = toggle; // Toggles are hidden in the context menu unless explicitly opted in.
        return (menuCandidate && IsBasic(commandId))
            ? EditorCommandAvailability.ContextMenuAndShortcut
            : EditorCommandAvailability.ShortcutOnly;
    }

    public static bool CanExecute(EditorCommandAvailability state) => state != EditorCommandAvailability.Disabled;
    public static bool ShowInMenu(EditorCommandAvailability state) => state == EditorCommandAvailability.ContextMenuAndShortcut;
}
