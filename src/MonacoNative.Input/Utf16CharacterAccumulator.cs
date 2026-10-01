namespace MonacoNative.Input;

/// <summary>
/// Reassembles UTF-16 surrogate pairs delivered as individual CharacterReceived events.
/// A stray half is replaced with U+FFFD so the editor never stores an unmatched surrogate.
/// </summary>
public sealed class Utf16CharacterAccumulator
{
    private char? _highSurrogate;

    public string? Push(char value)
    {
        if (char.IsHighSurrogate(value))
        {
            _highSurrogate = value;
            return null;
        }

        if (char.IsLowSurrogate(value))
        {
            if (_highSurrogate is char high)
            {
                _highSurrogate = null;
                return string.Concat(high, value);
            }
            return "\uFFFD";
        }

        if (_highSurrogate is not null)
        {
            _highSurrogate = null;
            return "\uFFFD" + value;
        }
        return value.ToString();
    }

    public string? Flush()
    {
        if (_highSurrogate is null)
            return null;
        _highSurrogate = null;
        return "\uFFFD";
    }

    public void Reset() => _highSurrogate = null;
}
