namespace MonacoNative.Renderer.WinUI3;

/// <summary>Keyboard shortcuts for the editor's built-in commands.</summary>
public enum EditorShortcutScheme
{
    VSCode,
    VisualStudio,
}

/// <summary>A host-defined shortcut. When can narrow the editor context for a binding.</summary>
public sealed record EditorShortcutBinding(string Chord, int Weight = 1000, string? When = null);

/// <summary>
/// Profile-specific shortcuts. Null means use the command's shared binding;
/// an empty list explicitly unbinds the command in that profile.
/// </summary>
internal static class EditorShortcutProfiles
{
    private static readonly IReadOnlyDictionary<string, string[]> VSCode = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["editor.action.formatDocument"] = ["Shift+Alt+F"],
        ["editor.action.formatSelection"] = ["Ctrl+K Ctrl+F"],
        ["editor.action.toggleLineComment"] = ["Ctrl+OemQuestion"],
        ["editor.action.toggleWordWrap"] = ["Alt+Z"],
        ["editor.action.showHover"] = ["Ctrl+K Ctrl+I"],
        ["monacoNative.action.deleteLine"] = ["Ctrl+Shift+K"],
        ["editor.action.showNextParameterHint"] = ["Alt+Down"],
        ["editor.action.showPrevParameterHint"] = ["Alt+Up"],
    };

    private static readonly IReadOnlyDictionary<string, string[]> VisualStudio = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["editor.action.formatDocument"] = ["Ctrl+K Ctrl+D"],
        ["editor.action.formatSelection"] = ["Ctrl+K Ctrl+F"],
        ["editor.action.toggleLineComment"] = [],
        ["editor.action.toggleWordWrap"] = ["Ctrl+E Ctrl+W"],
        ["editor.action.showHover"] = ["Ctrl+K Ctrl+I"],
        ["monacoNative.action.deleteLine"] = ["Ctrl+Shift+L"],
        ["editor.action.showNextParameterHint"] = ["Down"],
        ["editor.action.showPrevParameterHint"] = ["Up"],
    };

    public static IReadOnlyList<string>? Resolve(EditorShortcutScheme scheme, string commandId)
    {
        var map = scheme == EditorShortcutScheme.VisualStudio ? VisualStudio : VSCode;
        return map.TryGetValue(commandId, out var chords) ? chords : null;
    }
}

/// <summary>Converts keybinding chord tokens to the labels shown in editor menus.</summary>
internal static class EditorShortcutLabelFormatter
{
    public static string Format(string? chord)
    {
        if (string.IsNullOrWhiteSpace(chord)) return string.Empty;
        return string.Join(" ", chord.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(stroke =>
        {
            var separator = stroke.LastIndexOf('+');
            var prefix = separator >= 0 ? stroke[..(separator + 1)] : string.Empty;
            var key = separator >= 0 ? stroke[(separator + 1)..] : stroke;
            key = key switch
            {
                "OemPeriod" => ".",
                "OemQuestion" => "/",
                "Back" => "Backspace",
                _ => key,
            };
            return prefix + key;
        }));
    }
}
