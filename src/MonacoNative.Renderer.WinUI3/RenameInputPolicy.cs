// Selection and acceptance behavior adapted from VS Code's RenameWidget (MIT).
// Copyright (c) Microsoft Corporation. See doc/licenses/VSCode-MIT.txt.
// Reference: c54303d6eb470af8bb6c25a321d742f7070639c5, renameWidget.ts.
namespace MonacoNative.Renderer.WinUI3;

internal readonly record struct RenameSelectionRange(int StartLine, int StartColumn, int EndLine, int EndColumn)
{
    public RenameSelectionRange Normalize() =>
        StartLine > EndLine || (StartLine == EndLine && StartColumn > EndColumn)
            ? new(EndLine, EndColumn, StartLine, StartColumn)
            : this;
}

/// <summary>Pure UTF-16 selection/acceptance rules shared by the native rename widget and tests.</summary>
internal static class RenameInputPolicy
{
    public static (int Start, int Length) GetSelection(
        int nameLength, RenameSelectionRange symbol, RenameSelectionRange selection)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nameLength);
        symbol = symbol.Normalize();
        selection = selection.Normalize();
        if (selection.StartLine == selection.EndLine && selection.StartColumn != selection.EndColumn &&
            selection.StartLine >= symbol.StartLine && selection.EndLine <= symbol.EndLine &&
            (selection.StartLine != symbol.StartLine || selection.StartColumn >= symbol.StartColumn) &&
            (selection.EndLine != symbol.EndLine || selection.EndColumn <= symbol.EndColumn))
        {
            var start = Math.Clamp(selection.StartColumn - symbol.StartColumn, 0, nameLength);
            var end = Math.Clamp(Math.Min(symbol.EndColumn, selection.EndColumn) - symbol.StartColumn, start, nameLength);
            return (start, end - start);
        }
        return (0, nameLength);
    }

    // Do not trim a provider's rename text: only use whitespace to decide whether to cancel.
    public static bool ShouldCancel(string currentName, string? newName) =>
        string.IsNullOrWhiteSpace(newName) || string.Equals(currentName, newName, StringComparison.Ordinal);
}
