namespace MonacoNative.Input;

public readonly record struct ImeVisualFragment(int StartVisualOffset, int EndVisualOffset)
{
    public int Length => Math.Max(0, EndVisualOffset - StartVisualOffset);
}

/// <summary>
/// Separates real model-backed visual text from injected/inlay projection. The caller
/// supplies one model ACP offset per visual UTF-16 boundary. A visual code unit whose
/// right boundary does not advance the model offset is projection-only and must not be
/// decorated as IME composition text.
/// </summary>
public static class ImeProjectionFragments
{
    public static IReadOnlyList<ImeVisualFragment> GetModelBackedFragments(
        IReadOnlyList<int> boundaryModelOffsets,
        ImeTextRange modelRange)
    {
        ArgumentNullException.ThrowIfNull(boundaryModelOffsets);
        if (boundaryModelOffsets.Count < 2 || modelRange.IsEmpty)
            return Array.Empty<ImeVisualFragment>();

        var result = new List<ImeVisualFragment>();
        for (var visual = 0; visual + 1 < boundaryModelOffsets.Count; ++visual)
        {
            var left = boundaryModelOffsets[visual];
            var right = boundaryModelOffsets[visual + 1];
            if (right <= left)
                continue; // injected text or another non-model visual interval
            if (right <= modelRange.Start || left >= modelRange.End)
                continue;

            if (result.Count > 0 && result[^1].EndVisualOffset == visual)
            {
                var previous = result[^1];
                result[^1] = previous with { EndVisualOffset = visual + 1 };
            }
            else
            {
                result.Add(new ImeVisualFragment(visual, visual + 1));
            }
        }
        return result;
    }
}
