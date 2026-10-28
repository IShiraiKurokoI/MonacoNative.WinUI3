using System.Text;
using MonacoNative.Documents;
using MonacoNative.Language;

namespace MonacoNative.Renderer.WinUI3;

internal sealed class EditorPreviewChange
{
    internal EditorPreviewChange(WorkspaceTextEdit edit, int start, int end)
    { Edit = edit; Start = start; End = end; }

    internal WorkspaceTextEdit Edit { get; }
    internal int Start { get; }
    internal int End { get; }
    internal bool Included { get; set; } = true;
}

/// <summary>A frozen source snapshot. Checking changes never mutates the working copy.</summary>
internal sealed class EditorRefactorPreview
{
    private readonly int[] _lineStarts;
    private readonly int[] _lineEnds;
    internal Uri Uri { get; }
    internal string OriginalText { get; }
    internal string LanguageId { get; }
    internal string DisplayName { get; }
    internal IReadOnlyList<EditorPreviewChange> Changes { get; }
    internal bool? Included => Changes.All(c => c.Included) ? true : Changes.Any(c => c.Included) ? null : false;

    internal EditorRefactorPreview(Uri uri, string text, string languageId, string displayName,
        IEnumerable<WorkspaceTextEdit> edits)
    {
        Uri = uri;
        OriginalText = text;
        LanguageId = languageId;
        DisplayName = displayName;
        var starts = new List<int> { 0 };
        var ends = new List<int>();
        for (var i = 0; i < text.Length; ++i)
        {
            if (text[i] is not ('\r' or '\n')) continue;
            ends.Add(i);
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') ++i;
            starts.Add(i + 1);
        }
        ends.Add(text.Length);
        _lineStarts = starts.ToArray();
        _lineEnds = ends.ToArray();
        Changes = edits.Select(edit =>
        {
            if (!EditorDocumentUriComparer.Instance.Equals(uri, edit.Uri))
                throw new ArgumentException("Preview edits must target the same resource.", nameof(edits));
            var r = edit.Range;
            var start = OffsetAt(r.StartLineNumber, r.StartColumn);
            var end = OffsetAt(r.EndLineNumber, r.EndColumn);
            if (end < start) throw new ArgumentException("A preview range is reversed.", nameof(edits));
            return new EditorPreviewChange(edit, start, end);
        }).OrderBy(c => c.Start).ThenBy(c => c.End).ToArray();
        for (var i = 1; i < Changes.Count; ++i)
            if (Changes[i].Start < Changes[i - 1].End || Changes[i].Start == Changes[i - 1].Start)
                throw new ArgumentException("Preview edits overlap.", nameof(edits));
    }

    private int OffsetAt(int line, int column)
    {
        if (line < 1 || line > _lineStarts.Length || column < 1 ||
            column > _lineEnds[line - 1] - _lineStarts[line - 1] + 1)
            throw new ArgumentOutOfRangeException(nameof(line), "A preview range is outside the source snapshot.");
        return _lineStarts[line - 1] + column - 1;
    }

    internal string BuildModifiedText()
    {
        var builder = new StringBuilder(OriginalText.Length);
        var offset = 0;
        foreach (var change in Changes.Where(c => c.Included))
        {
            builder.Append(OriginalText, offset, change.Start - offset);
            builder.Append(change.Edit.Text);
            offset = change.End;
        }
        builder.Append(OriginalText, offset, OriginalText.Length - offset);
        return builder.ToString();
    }

    internal void SetIncluded(bool included)
    {
        foreach (var change in Changes) change.Included = included;
    }

    internal (string Before, string Removed, string Added, string After) Snippet(EditorPreviewChange change)
    {
        if (!Changes.Contains(change)) throw new ArgumentException("The change does not belong to this preview.", nameof(change));
        var line = change.Edit.Range.StartLineNumber - 1;
        var start = Math.Max(_lineStarts[line], change.Start - 60);
        var end = Math.Min(_lineEnds[change.Edit.Range.EndLineNumber - 1], change.End + 100);
        return (OriginalText[start..change.Start].TrimStart(),
            OneLine(OriginalText[change.Start..change.End]), OneLine(change.Edit.Text ?? string.Empty),
            OriginalText[change.End..end]);
    }

    private static string OneLine(string text) => text.Replace("\r\n", "↵").Replace('\r', '↵').Replace('\n', '↵');
}
