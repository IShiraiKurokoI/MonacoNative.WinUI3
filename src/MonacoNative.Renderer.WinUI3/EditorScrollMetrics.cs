namespace MonacoNative.Renderer.WinUI3;

/// <summary>Scroll content sizing in device-independent pixels.</summary>
internal static class EditorScrollMetrics
{
    // The UserControl's ActualWidth includes its chrome. ScrollViewer.ViewportWidth
    // excludes that chrome and any scrollbar that consumes layout space.
    internal static double ViewportWidth(double viewportWidth, double scrollHostWidth, double fallbackWidth)
    {
        if (double.IsFinite(viewportWidth) && viewportWidth > 0)
            return double.IsFinite(scrollHostWidth) && scrollHostWidth > 0
                ? Math.Min(viewportWidth, scrollHostWidth) : viewportWidth;
        if (double.IsFinite(scrollHostWidth) && scrollHostWidth > 0)
            return scrollHostWidth;
        return double.IsFinite(fallbackWidth) ? Math.Max(1, fallbackWidth) : 1;
    }

    internal static double ExtentWidth(bool wordWrap, double viewportWidth, double textStart,
        double trailingPadding, double observedLineWidth)
    {
        // An extent that is one or two DIPs wider than the viewport creates a
        // draggable scrollbar even when no text overflows (notably with borders).
        var viewport = double.IsFinite(viewportWidth) ? Math.Max(1, viewportWidth) : 1;
        if (wordWrap)
            return viewport;
        var glyphWidth = double.IsFinite(observedLineWidth) ? Math.Max(0, observedLineWidth) : 0;
        return Math.Max(viewport, textStart + glyphWidth + trailingPadding);
    }
}
