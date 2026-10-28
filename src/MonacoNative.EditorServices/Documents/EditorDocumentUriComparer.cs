namespace MonacoNative.Documents;

/// <summary>
/// Compares file document identities using this platform's path rules. Other schemes use
/// normal URI equality. File URI fragments and queries do not change the underlying file.
/// Symbolic links and hard links are intentionally not resolved.
/// </summary>
public sealed class EditorDocumentUriComparer : IEqualityComparer<Uri>
{
    public static EditorDocumentUriComparer Instance { get; } = new();

    private static readonly StringComparer FilePathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private EditorDocumentUriComparer() { }

    public bool Equals(Uri? x, Uri? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null) return false;
        if (x.IsAbsoluteUri && y.IsAbsoluteUri && x.IsFile && y.IsFile)
            return FilePathComparer.Equals(Path.GetFullPath(x.LocalPath), Path.GetFullPath(y.LocalPath));
        return x.Equals(y);
    }

    public int GetHashCode(Uri obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return obj.IsAbsoluteUri && obj.IsFile
            ? FilePathComparer.GetHashCode(Path.GetFullPath(obj.LocalPath))
            : obj.GetHashCode();
    }
}
