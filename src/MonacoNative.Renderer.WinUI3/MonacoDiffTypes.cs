using MonacoNative.Core;

namespace MonacoNative.Renderer.WinUI3;

public enum MonacoDiffDecorationKind
{
    Inserted,
    Deleted,
    Modified,
    Moved,
}

public readonly record struct MonacoDiffLineDecoration(
    int StartLineNumber,
    int EndLineNumberExclusive,
    MonacoDiffDecorationKind Kind);

public readonly record struct MonacoDiffTextDecoration(
    MonacoRange Range,
    MonacoDiffDecorationKind Kind);
