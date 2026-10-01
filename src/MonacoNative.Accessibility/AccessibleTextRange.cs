using System.Globalization;
using System.Text;

namespace MonacoNative.Accessibility;

/// <summary>
/// Platform-neutral UIA-like text range using UTF-16 ACP offsets. The range reads the
/// current document text lazily so it can be used by WinUI UI Automation providers and
/// deterministic tests without taking a second editor buffer.
/// </summary>
public sealed class AccessibleTextRange
{
    private readonly Func<string> _textProvider;
    private readonly object _documentToken;
    private int _start;
    private int _end;

    public AccessibleTextRange(Func<string> textProvider, object documentToken, int start, int end)
    {
        _textProvider = textProvider ?? throw new ArgumentNullException(nameof(textProvider));
        _documentToken = documentToken ?? throw new ArgumentNullException(nameof(documentToken));
        Set(start, end);
    }

    public int Start { get { Normalize(); return _start; } }
    public int End { get { Normalize(); return _end; } }
    public bool IsDegenerate => Start == End;

    public AccessibleTextRange Clone() => new(_textProvider, _documentToken, Start, End);

    public void Set(int start, int end)
    {
        var length = _textProvider().Length;
        start = Math.Clamp(start, 0, length);
        end = Math.Clamp(end, 0, length);
        if (end < start)
            (start, end) = (end, start);
        _start = start;
        _end = end;
    }

    public bool Compare(AccessibleTextRange other)
    {
        ValidateSameDocument(other);
        return Start == other.Start && End == other.End;
    }

    public int CompareEndpoints(AccessibleTextEndpoint endpoint, AccessibleTextRange other, AccessibleTextEndpoint otherEndpoint)
    {
        ValidateSameDocument(other);
        var left = endpoint == AccessibleTextEndpoint.Start ? Start : End;
        var right = otherEndpoint == AccessibleTextEndpoint.Start ? other.Start : other.End;
        return left.CompareTo(right);
    }

    public string GetText(int maxLength = -1)
    {
        var text = _textProvider();
        Normalize(text.Length);
        var length = _end - _start;
        if (maxLength >= 0)
            length = Math.Min(length, maxLength);
        return length <= 0 ? string.Empty : text.Substring(_start, length);
    }

    public AccessibleTextRange? FindText(string value, bool backward, bool ignoreCase)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = _textProvider();
        Normalize(text.Length);
        if (value.Length == 0)
            return new AccessibleTextRange(_textProvider, _documentToken, backward ? _end : _start, backward ? _end : _start);

        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var haystack = text.Substring(_start, _end - _start);
        var index = backward ? haystack.LastIndexOf(value, comparison) : haystack.IndexOf(value, comparison);
        if (index < 0)
            return null;
        var start = _start + index;
        return new AccessibleTextRange(_textProvider, _documentToken, start, start + value.Length);
    }

    public void ExpandToEnclosingUnit(AccessibleTextUnit unit)
    {
        var text = _textProvider();
        Normalize(text.Length);
        if (unit is AccessibleTextUnit.Document or AccessibleTextUnit.Format or AccessibleTextUnit.Page)
        {
            _start = 0;
            _end = text.Length;
            return;
        }

        var spans = TextUnits.GetSpans(text, unit);
        if (spans.Count == 0)
        {
            _start = _end = 0;
            return;
        }

        var firstPosition = _start == text.Length && text.Length > 0 ? text.Length - 1 : _start;
        var lastPosition = _end > _start ? _end - 1 : firstPosition;
        var first = TextUnits.FindContaining(spans, firstPosition);
        var last = TextUnits.FindContaining(spans, lastPosition);
        _start = first.Start;
        _end = last.End;
    }

    /// <summary>Moves this range by whole units and returns the actual number moved.</summary>
    public int Move(AccessibleTextUnit unit, int count)
    {
        if (count == 0)
        {
            ExpandToEnclosingUnit(unit);
            return 0;
        }

        var text = _textProvider();
        Normalize(text.Length);
        if (unit is AccessibleTextUnit.Document or AccessibleTextUnit.Format or AccessibleTextUnit.Page)
        {
            if (count > 0 && _end < text.Length)
            {
                _start = _end = text.Length;
                return 1;
            }
            if (count < 0 && _start > 0)
            {
                _start = _end = 0;
                return -1;
            }
            return 0;
        }

        var spans = TextUnits.GetSpans(text, unit);
        if (spans.Count == 0)
            return 0;

        var currentPos = _start == text.Length && text.Length > 0 ? text.Length - 1 : _start;
        var index = TextUnits.FindContainingIndex(spans, currentPos);
        var target = Math.Clamp(index + count, 0, spans.Count - 1);
        var actual = target - index;
        if (actual == 0)
            return 0;
        _start = spans[target].Start;
        _end = spans[target].End;
        return actual;
    }

    public int MoveEndpointByUnit(AccessibleTextEndpoint endpoint, AccessibleTextUnit unit, int count)
    {
        if (count == 0)
            return 0;
        var text = _textProvider();
        Normalize(text.Length);
        var current = endpoint == AccessibleTextEndpoint.Start ? _start : _end;
        var boundaries = TextUnits.GetBoundaries(text, unit);
        var target = current;
        var actual = 0;
        for (var i = 0; i < Math.Abs(count); i++)
        {
            var next = count > 0
                ? TextUnits.NextBoundary(boundaries, target)
                : TextUnits.PreviousBoundary(boundaries, target);
            if (next == target)
                break;
            target = next;
            actual += Math.Sign(count);
        }

        if (endpoint == AccessibleTextEndpoint.Start)
        {
            _start = target;
            if (_start > _end)
                _end = _start;
        }
        else
        {
            _end = target;
            if (_end < _start)
                _start = _end;
        }
        return actual;
    }

    public void MoveEndpointByRange(AccessibleTextEndpoint endpoint, AccessibleTextRange other, AccessibleTextEndpoint otherEndpoint)
    {
        ValidateSameDocument(other);
        var value = otherEndpoint == AccessibleTextEndpoint.Start ? other.Start : other.End;
        if (endpoint == AccessibleTextEndpoint.Start)
        {
            _start = value;
            if (_start > _end)
                _end = _start;
        }
        else
        {
            _end = value;
            if (_end < _start)
                _start = _end;
        }
    }

    private void Normalize() => Normalize(_textProvider().Length);

    private void Normalize(int length)
    {
        _start = Math.Clamp(_start, 0, length);
        _end = Math.Clamp(_end, 0, length);
        if (_end < _start)
            _end = _start;
    }

    private void ValidateSameDocument(AccessibleTextRange other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!ReferenceEquals(_documentToken, other._documentToken))
            throw new ArgumentException("Text ranges belong to different documents.", nameof(other));
    }
}

internal readonly record struct TextSpan(int Start, int End)
{
    public int Length => End - Start;
}

internal static class TextUnits
{
    public static IReadOnlyList<TextSpan> GetSpans(string text, AccessibleTextUnit unit) => unit switch
    {
        AccessibleTextUnit.Character => CharacterSpans(text),
        AccessibleTextUnit.Word => WordSpans(text),
        AccessibleTextUnit.Line or AccessibleTextUnit.Paragraph => LineSpans(text),
        _ => text.Length == 0 ? Array.Empty<TextSpan>() : new[] { new TextSpan(0, text.Length) },
    };

    public static int[] GetBoundaries(string text, AccessibleTextUnit unit)
    {
        if (unit is AccessibleTextUnit.Document or AccessibleTextUnit.Format or AccessibleTextUnit.Page)
            return text.Length == 0 ? new[] { 0 } : new[] { 0, text.Length };
        var spans = GetSpans(text, unit);
        if (spans.Count == 0)
            return new[] { 0 };
        var values = new List<int>(spans.Count + 1) { spans[0].Start };
        foreach (var span in spans)
        {
            if (values[^1] != span.Start)
                values.Add(span.Start);
            if (values[^1] != span.End)
                values.Add(span.End);
        }
        return values.ToArray();
    }

    public static TextSpan FindContaining(IReadOnlyList<TextSpan> spans, int position) => spans[FindContainingIndex(spans, position)];

    public static int FindContainingIndex(IReadOnlyList<TextSpan> spans, int position)
    {
        if (spans.Count == 0)
            return 0;
        for (var i = 0; i < spans.Count; ++i)
        {
            if (position >= spans[i].Start && position < spans[i].End)
                return i;
        }
        return position <= spans[0].Start ? 0 : spans.Count - 1;
    }

    public static int NextBoundary(int[] boundaries, int current)
    {
        foreach (var boundary in boundaries)
            if (boundary > current)
                return boundary;
        return boundaries[^1];
    }

    public static int PreviousBoundary(int[] boundaries, int current)
    {
        for (var i = boundaries.Length - 1; i >= 0; --i)
            if (boundaries[i] < current)
                return boundaries[i];
        return boundaries[0];
    }

    private static IReadOnlyList<TextSpan> CharacterSpans(string text)
    {
        if (text.Length == 0)
            return Array.Empty<TextSpan>();
        var starts = StringInfo.ParseCombiningCharacters(text);
        var boundaries = new List<int>(starts.Length + 1);
        foreach (var start in starts)
        {
            // Treat CRLF as one logical character/unit for editor navigation.
            if (start > 0 && start < text.Length && text[start - 1] == '\r' && text[start] == '\n')
                continue;
            boundaries.Add(start);
        }
        if (boundaries.Count == 0 || boundaries[0] != 0)
            boundaries.Insert(0, 0);
        boundaries.Add(text.Length);
        var spans = new List<TextSpan>(boundaries.Count - 1);
        for (var i = 0; i + 1 < boundaries.Count; ++i)
            if (boundaries[i + 1] > boundaries[i])
                spans.Add(new TextSpan(boundaries[i], boundaries[i + 1]));
        return spans;
    }

    private enum WordClass { Word, Space, Newline, Punctuation }

    private static IReadOnlyList<TextSpan> WordSpans(string text)
    {
        if (text.Length == 0)
            return Array.Empty<TextSpan>();
        var chars = CharacterSpans(text);
        var result = new List<TextSpan>();
        var start = chars[0].Start;
        var last = Classify(text, chars[0]);
        var end = chars[0].End;
        for (var i = 1; i < chars.Count; ++i)
        {
            var cls = Classify(text, chars[i]);
            if (cls != last || cls == WordClass.Punctuation)
            {
                result.Add(new TextSpan(start, end));
                start = chars[i].Start;
                last = cls;
            }
            end = chars[i].End;
        }
        result.Add(new TextSpan(start, end));
        return result;
    }

    private static WordClass Classify(string text, TextSpan span)
    {
        var value = text.AsSpan(span.Start, span.Length);
        if (value.SequenceEqual("\r\n") || value.SequenceEqual("\r") || value.SequenceEqual("\n"))
            return WordClass.Newline;
        var rune = Rune.GetRuneAt(text, span.Start);
        if (Rune.IsWhiteSpace(rune))
            return WordClass.Space;
        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or
            UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or
            UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber or
            UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark ||
            rune.Value == '_')
            return WordClass.Word;
        return WordClass.Punctuation;
    }

    private static IReadOnlyList<TextSpan> LineSpans(string text)
    {
        if (text.Length == 0)
            return Array.Empty<TextSpan>();
        var result = new List<TextSpan>();
        var start = 0;
        for (var i = 0; i < text.Length; ++i)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                    ++i;
                result.Add(new TextSpan(start, i + 1));
                start = i + 1;
            }
            else if (text[i] == '\n')
            {
                result.Add(new TextSpan(start, i + 1));
                start = i + 1;
            }
        }
        if (start < text.Length)
            result.Add(new TextSpan(start, text.Length));
        else if (start == text.Length && result.Count == 0)
            result.Add(new TextSpan(0, 0));
        return result;
    }
}
