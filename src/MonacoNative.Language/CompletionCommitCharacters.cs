namespace MonacoNative.Language;

/// <summary>
/// Applies the completion item's explicit character-commit policy. Character commits
/// are never inferred from punctuation alone: providers must opt in per item.
/// </summary>
public static class CompletionCommitCharacters
{
    public static bool Accepts(CompletionItem item, string text)
    {
        ArgumentNullException.ThrowIfNull(item);
        return text.Length == 1 && !char.IsControl(text[0]) && !char.IsSurrogate(text[0]) &&
            item.CommitCharacters?.Contains(text, StringComparer.Ordinal) == true;
    }

    /// <summary>
    /// Appends the typed character once. The caller applies the resulting text in the
    /// same undo transaction as the completion, including any additional edits.
    /// </summary>
    public static string AppendOnce(string insertion, string commitCharacter)
    {
        ArgumentNullException.ThrowIfNull(insertion);
        ArgumentNullException.ThrowIfNull(commitCharacter);
        if (commitCharacter.Length != 1 || char.IsControl(commitCharacter[0]) || char.IsSurrogate(commitCharacter[0]))
            throw new ArgumentException("A commit character must be a single printable UTF-16 code unit.", nameof(commitCharacter));
        return insertion.EndsWith(commitCharacter, StringComparison.Ordinal) ? insertion : insertion + commitCharacter;
    }
}
