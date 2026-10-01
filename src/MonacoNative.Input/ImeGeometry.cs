namespace MonacoNative.Input;

public readonly record struct ImeRect(double X, double Y, double Width, double Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public double Right => X + Math.Max(0, Width);
    public double Bottom => Y + Math.Max(0, Height);

    public static ImeRect Union(ImeRect a, ImeRect b)
    {
        if (a.IsEmpty) return b;
        if (b.IsEmpty) return a;
        var left = Math.Min(a.X, b.X);
        var top = Math.Min(a.Y, b.Y);
        var right = Math.Max(a.Right, b.Right);
        var bottom = Math.Max(a.Bottom, b.Bottom);
        return new ImeRect(left, top, right - left, bottom - top);
    }
}

public static class ImeGeometry
{
    public static ImeRect Union(IEnumerable<ImeRect> rectangles)
    {
        var result = default(ImeRect);
        foreach (var rect in rectangles)
            result = ImeRect.Union(result, rect);
        return result;
    }
}
