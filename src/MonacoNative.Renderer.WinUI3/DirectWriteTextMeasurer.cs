using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;

namespace MonacoNative.Renderer.WinUI3;

/// <summary>
/// DirectWrite-backed text measurement through Win2D. CanvasTextLayout maps to IDWriteTextLayout,
/// so the widths used by the native ViewModel are the same widths used by the renderer.
/// </summary>
internal sealed class DirectWriteTextMeasurer : IDisposable
{
    private const float LayoutWidth = 1_000_000f;

    private sealed class Entry
    {
        public required float Width { get; init; }
        public long Stamp { get; set; }
    }

    private readonly CanvasDevice _device;
    private readonly CanvasTextFormat _format;
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private long _stamp;

    public DirectWriteTextMeasurer(CanvasDevice device, CanvasTextFormat format, int capacity = 2048)
    {
        _device = device;
        _format = format;
        _capacity = Math.Max(32, capacity);
    }

    public double Measure(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        if (_cache.TryGetValue(text, out var cached))
        {
            cached.Stamp = ++_stamp;
            return cached.Width;
        }

        using var layout = new CanvasTextLayout(_device, text, _format, LayoutWidth, 4096f);
        var width = (float)Math.Max(0, layout.LayoutBoundsIncludingTrailingWhitespace.Width);
        _cache[text] = new Entry { Width = width, Stamp = ++_stamp };
        Trim();
        return width;
    }

    public void Clear() => _cache.Clear();

    private void Trim()
    {
        while (_cache.Count > _capacity)
        {
            var oldest = _cache.MinBy(pair => pair.Value.Stamp);
            if (oldest.Value is null)
                break;
            _cache.Remove(oldest.Key);
        }
    }

    public void Dispose() => Clear();
}
