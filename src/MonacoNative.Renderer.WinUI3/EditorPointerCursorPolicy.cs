namespace MonacoNative.Renderer.WinUI3;

// Pure geometry policy: no WinUI dependency, so it can be exercised in portable tests.
internal enum EditorPointerCursorKind { Arrow, Text, Minimap, Folding }

internal static class EditorPointerCursorPolicy
{
    internal static EditorPointerCursorKind Resolve(
        double x, double y, double width, double height,
        double textLeft, double textRight, double minimapLeft, bool showMinimap,
        bool overInteractiveOverlay = false)
    {
        if (overInteractiveOverlay || x < 0 || y < 0 || x >= width || y >= height)
            return EditorPointerCursorKind.Arrow;
        if (showMinimap && x >= minimapLeft)
            return EditorPointerCursorKind.Minimap;
        if (x < textLeft || x >= textRight)
            return EditorPointerCursorKind.Arrow;
        return EditorPointerCursorKind.Text; // Includes text-line trailing whitespace.
    }
}
