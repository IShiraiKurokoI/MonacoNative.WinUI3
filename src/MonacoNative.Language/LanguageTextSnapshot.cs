namespace MonacoNative.Language;

/// <summary>Immutable UTF-16 text/line map shared by in-process language implementations.</summary>
public sealed class LanguageTextSnapshot
{
    private readonly int[] _lineStarts;
    public string Text { get; }
    public LanguageTextSnapshot(string text)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                starts.Add(i + 1);
            }
            else if (text[i] == '\n') starts.Add(i + 1);
        _lineStarts = starts.ToArray();
    }
    public int OffsetAt(LanguagePosition position)
    {
        var row = Math.Clamp(position.LineNumber - 1, 0, _lineStarts.Length - 1);
        var end = row + 1 < _lineStarts.Length ? _lineStarts[row + 1] : Text.Length;
        while (end > _lineStarts[row] && (Text[end - 1] is '\r' or '\n')) end--;
        return _lineStarts[row] + Math.Clamp(position.Column - 1, 0, end - _lineStarts[row]);
    }
    public LanguagePosition PositionAt(int offset)
    {
        offset = Math.Clamp(offset, 0, Text.Length);
        var index = Array.BinarySearch(_lineStarts, offset);
        if (index < 0) index = ~index - 1;
        return new(index + 1, offset - _lineStarts[index] + 1);
    }
    public LanguageRange Range(int start, int end)
    {
        var a = PositionAt(start); var b = PositionAt(end);
        return new(a.LineNumber, a.Column, b.LineNumber, b.Column);
    }
}

/// <summary>Thread-safe request input captured before leaving the editor thread.</summary>
public sealed class LanguageModelSnapshot : ILanguageTextModel
{
    private readonly string _text;
    private readonly string[] _lines;
    public Uri Uri { get; }
    public string LanguageId { get; }
    public ulong VersionId { get; }
    public int LineCount => _lines.Length;
    public LanguageModelSnapshot(Uri uri, string languageId, ulong versionId, string text)
    {
        Uri = uri ?? throw new ArgumentNullException(nameof(uri));
        LanguageId = languageId ?? throw new ArgumentNullException(nameof(languageId));
        VersionId = versionId; _text = text ?? throw new ArgumentNullException(nameof(text));
        _lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
    }
    public static LanguageModelSnapshot Capture(ILanguageTextModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model as LanguageModelSnapshot ?? new(model.Uri, model.LanguageId, model.VersionId, model.GetValue());
    }
    public bool Matches(ILanguageTextModel model) => Uri == model.Uri && VersionId == model.VersionId && LanguageId == model.LanguageId;
    public string GetValue() => _text;
    public string GetLineContent(int lineNumber) => _lines[lineNumber - 1];
    public int GetLineMaxColumn(int lineNumber) => GetLineContent(lineNumber).Length + 1;
}
