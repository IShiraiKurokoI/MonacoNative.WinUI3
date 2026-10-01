namespace MonacoNative.Input;

public readonly record struct ImeTextUpdatePlan(
    ImeTextRange ReplaceRange,
    string Text,
    ImeTextRange NewSelection,
    ImeTextRange CompositionRange);

/// <summary>
/// Normalizes text-server updates against the current UTF-16 ACP length. Keeping this
/// policy portable makes surrogate/CJK/emoji boundary behavior testable without a live IME.
/// </summary>
public static class ImeTextUpdatePlanner
{
    public static ImeTextUpdatePlan Create(
        int modelLength,
        int replaceStart,
        int replaceEnd,
        string? text,
        int selectionStart,
        int selectionEnd)
    {
        modelLength = Math.Max(0, modelLength);
        var replace = new ImeTextRange(replaceStart, replaceEnd).Normalize(modelLength);
        var replacement = text ?? string.Empty;
        var resultingLength = checked(modelLength - replace.Length + replacement.Length);
        var selection = new ImeTextRange(selectionStart, selectionEnd).Normalize(resultingLength);
        var composition = new ImeTextRange(replace.Start, replace.Start + replacement.Length).Normalize(resultingLength);
        return new ImeTextUpdatePlan(replace, replacement, selection, composition);
    }
}
