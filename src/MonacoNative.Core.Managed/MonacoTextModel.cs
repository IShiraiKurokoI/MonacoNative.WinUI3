using System.Text;

namespace MonacoNative.Core;

public enum MonacoEndOfLine
{
    Lf = 0,
    CrLf = 1,
}

public readonly record struct MonacoPosition(int LineNumber, int Column);
public readonly record struct MonacoRange(int StartLineNumber, int StartColumn, int EndLineNumber, int EndColumn);
public readonly record struct MonacoBracketMatch(MonacoPosition Bracket, MonacoPosition Match);

public sealed class MonacoModelChangedEventArgs : EventArgs
{
    public MonacoModelChangedEventArgs(nuint startOffset, nuint oldEndOffset, nuint newEndOffset, int startLineNumber, bool isFullInvalidation, ulong versionId)
        : this(startOffset, oldEndOffset, newEndOffset, startLineNumber, 1, startLineNumber, 1, startLineNumber, 1, isFullInvalidation, versionId)
    {
    }

    public MonacoModelChangedEventArgs(
        nuint startOffset,
        nuint oldEndOffset,
        nuint newEndOffset,
        int startLineNumber,
        int startColumn,
        int oldEndLineNumber,
        int oldEndColumn,
        int newEndLineNumber,
        int newEndColumn,
        bool isFullInvalidation,
        ulong versionId)
    {
        StartOffset = startOffset;
        OldEndOffset = oldEndOffset;
        NewEndOffset = newEndOffset;
        StartLineNumber = startLineNumber;
        StartColumn = startColumn;
        OldEndLineNumber = oldEndLineNumber;
        OldEndColumn = oldEndColumn;
        NewEndLineNumber = newEndLineNumber;
        NewEndColumn = newEndColumn;
        IsFullInvalidation = isFullInvalidation;
        VersionId = versionId;
    }

    public nuint StartOffset { get; }
    public nuint OldEndOffset { get; }
    public nuint NewEndOffset { get; }
    public int StartLineNumber { get; }
    public int StartColumn { get; }
    public int OldEndLineNumber { get; }
    public int OldEndColumn { get; }
    public int NewEndLineNumber { get; }
    public int NewEndColumn { get; }
    public bool IsFullInvalidation { get; }
    public ulong VersionId { get; }
}

public sealed class MonacoTextModel : IDisposable
{
    private nint _handle;

    public event EventHandler<MonacoModelChangedEventArgs>? Changed;

    public MonacoTextModel(string text = "", string languageId = "plaintext")
    {
        _handle = NativeMethods.mn_model_create(text ?? string.Empty, languageId ?? "plaintext");
        if (_handle == 0)
            throw new InvalidOperationException("Failed to create MonacoNative text model.");
    }

    public nuint Length => NativeMethods.mn_model_length(Handle);
    public nuint LineCount => NativeMethods.mn_model_line_count(Handle);
    public ulong VersionId => NativeMethods.mn_model_version_id(Handle);
    public bool CanUndo => NativeMethods.mn_model_can_undo(Handle) != 0;
    public bool CanRedo => NativeMethods.mn_model_can_redo(Handle) != 0;
    public bool InUndoTransaction => NativeMethods.mn_model_in_undo_transaction(Handle) != 0;

    public string Text
    {
        get => GetText();
        set
        {
            var oldLength = Length;
            if (NativeMethods.mn_model_set_text(Handle, value ?? string.Empty) == 0)
                throw new InvalidOperationException("Native SetText failed.");
            RaiseChanged(0, oldLength, Length, 1, isFullInvalidation: true);
        }
    }

    public string LanguageId
    {
        get => GetLanguage();
        set => SetLanguage(value);
    }

    public MonacoEndOfLine EndOfLine
    {
        get
        {
            var value = NativeMethods.mn_model_get_eol(Handle);
            return value switch
            {
                0 => MonacoEndOfLine.Lf,
                1 => MonacoEndOfLine.CrLf,
                _ => throw new InvalidOperationException("Native GetEOL failed."),
            };
        }
        set
        {
            var oldLength = Length;
            if (NativeMethods.mn_model_set_eol(Handle, (int)value) == 0)
                throw new InvalidOperationException("Native SetEOL failed.");
            RaiseChanged(0, oldLength, Length, 1, isFullInvalidation: true);
        }
    }

    public void SetLanguage(string languageId)
    {
        if (NativeMethods.mn_model_set_language(Handle, languageId ?? "plaintext") == 0)
            throw new InvalidOperationException("Native SetLanguage failed.");
        RaiseChanged(0, Length, Length, 1, isFullInvalidation: true);
    }

    public unsafe string GetLineContent(int lineNumber)
    {
        if (lineNumber < 1)
            throw new ArgumentOutOfRangeException(nameof(lineNumber));

        var required = NativeMethods.mn_model_copy_line(Handle, lineNumber, null, 0);
        if (required == 0)
            throw new ArgumentOutOfRangeException(nameof(lineNumber));
        if (required == 1)
            return string.Empty;

        var chars = new char[checked((int)required)];
        fixed (char* ptr = chars)
        {
            if (NativeMethods.mn_model_copy_line(Handle, lineNumber, ptr, required) == 0)
                throw new InvalidOperationException("Native GetLineContent failed.");
        }
        return new string(chars, 0, checked((int)required - 1));
    }

    public int GetLineMaxColumn(int lineNumber)
    {
        if (lineNumber < 1)
            throw new ArgumentOutOfRangeException(nameof(lineNumber));
        var result = NativeMethods.mn_model_line_max_column(Handle, lineNumber);
        if (result <= 0)
            throw new ArgumentOutOfRangeException(nameof(lineNumber));
        return result;
    }

    public unsafe string GetTextRange(nuint start, nuint length)
    {
        if (start > Length)
            throw new ArgumentOutOfRangeException(nameof(start));

        var required = NativeMethods.mn_model_copy_text_range(Handle, start, length, null, 0);
        if (required == 0)
            throw new InvalidOperationException("Native GetTextRange failed.");
        if (required == 1)
            return string.Empty;

        var chars = new char[checked((int)required)];
        fixed (char* ptr = chars)
        {
            if (NativeMethods.mn_model_copy_text_range(Handle, start, length, ptr, required) == 0)
                throw new InvalidOperationException("Native GetTextRange failed.");
        }
        return new string(chars, 0, checked((int)required - 1));
    }

    public string GetValueInRange(MonacoRange range)
    {
        var start = GetOffsetAt(new MonacoPosition(range.StartLineNumber, range.StartColumn));
        var end = GetOffsetAt(new MonacoPosition(range.EndLineNumber, range.EndColumn));
        if (end < start)
            (start, end) = (end, start);
        return GetTextRange(start, end - start);
    }

    public MonacoPosition GetPositionAt(nuint offset)
    {
        if (NativeMethods.mn_model_get_position_at(Handle, offset, out var line, out var column) == 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        return new MonacoPosition(line, column);
    }

    public nuint GetOffsetAt(MonacoPosition position)
    {
        if (NativeMethods.mn_model_get_offset_at(Handle, position.LineNumber, position.Column, out var offset) == 0)
            throw new ArgumentOutOfRangeException(nameof(position));
        return offset;
    }

    public void Replace(nuint start, nuint end, string text)
    {
        var startPosition = GetPositionAt(start);
        var oldEndPosition = GetPositionAt(end);
        var replacement = text ?? string.Empty;
        var insertedLength = (nuint)replacement.Length;
        if (NativeMethods.mn_model_replace(Handle, start, end, replacement) == 0)
            throw new ArgumentOutOfRangeException(nameof(start), "Native Replace failed.");
        var newEndOffset = start + insertedLength;
        var newEndPosition = GetPositionAt(newEndOffset);
        RaiseChanged(
            start, end, newEndOffset,
            startPosition.LineNumber, startPosition.Column,
            oldEndPosition.LineNumber, oldEndPosition.Column,
            newEndPosition.LineNumber, newEndPosition.Column,
            isFullInvalidation: false);
    }

    public unsafe IReadOnlyList<MonacoRange> FindMatches(string query, bool matchCase = true, bool wholeWord = false, nuint limit = 1000)
    {
        if (string.IsNullOrEmpty(query) || limit == 0)
            return Array.Empty<MonacoRange>();

        var count = NativeMethods.mn_model_find_matches(Handle, query, matchCase ? 1 : 0, wholeWord ? 1 : 0, limit, null, 0);
        if (count == 0)
            return Array.Empty<MonacoRange>();

        var native = new NativeMethods.NativeRange[checked((int)count)];
        fixed (NativeMethods.NativeRange* ptr = native)
        {
            var written = NativeMethods.mn_model_find_matches(Handle, query, matchCase ? 1 : 0, wholeWord ? 1 : 0, limit, ptr, count);
            if (written != count)
                throw new InvalidOperationException("Native FindMatches returned an inconsistent result count.");
        }

        var result = new MonacoRange[native.Length];
        for (var i = 0; i < native.Length; i++)
        {
            result[i] = new MonacoRange(native[i].StartLine, native[i].StartColumn, native[i].EndLine, native[i].EndColumn);
        }
        return result;
    }

    public nuint ReplaceAll(string query, string replacement, bool matchCase = true, bool wholeWord = false, nuint limit = 1000)
    {
        if (string.IsNullOrEmpty(query) || limit == 0)
            return 0;
        var oldLength = Length;
        var count = NativeMethods.mn_model_replace_all(Handle, query, replacement ?? string.Empty, matchCase ? 1 : 0, wholeWord ? 1 : 0, limit);
        if (count > 0)
            RaiseChanged(0, oldLength, Length, 1, isFullInvalidation: true);
        return count;
    }

    public unsafe MonacoBracketMatch? FindMatchingBracket(MonacoPosition position)
    {
        NativeMethods.NativeBracketMatch native = default;
        var status = NativeMethods.mn_model_find_matching_bracket(Handle, position.LineNumber, position.Column, &native);
        return status switch
        {
            1 => new MonacoBracketMatch(
                new MonacoPosition(native.BracketLine, native.BracketColumn),
                new MonacoPosition(native.MatchLine, native.MatchColumn)),
            0 => null,
            _ => throw new InvalidOperationException("Native FindMatchingBracket failed."),
        };
    }

    public bool Undo()
    {
        var oldLength = Length;
        if (NativeMethods.mn_model_undo(Handle) == 0)
            return false;
        RaiseChanged(0, oldLength, Length, 1, isFullInvalidation: true);
        return true;
    }

    public bool Redo()
    {
        var oldLength = Length;
        if (NativeMethods.mn_model_redo(Handle) == 0)
            return false;
        RaiseChanged(0, oldLength, Length, 1, isFullInvalidation: true);
        return true;
    }

    public MonacoUndoTransaction BeginUndoTransaction()
    {
        if (NativeMethods.mn_model_begin_undo_transaction(Handle) == 0)
            throw new InvalidOperationException("Failed to begin undo transaction.");
        return new MonacoUndoTransaction(this);
    }

    public void PushUndoStop()
    {
        if (NativeMethods.mn_model_push_undo_stop(Handle) == 0)
            throw new InvalidOperationException("Failed to push undo stop.");
    }

    internal void CompleteUndoTransaction()
    {
        if (NativeMethods.mn_model_end_undo_transaction(Handle) == 0)
            throw new InvalidOperationException("Failed to complete undo transaction.");
    }

    internal bool RollbackUndoTransaction()
    {
        var oldLength = Length;
        if (NativeMethods.mn_model_rollback_undo_transaction(Handle) == 0)
            return false;
        RaiseChanged(0, oldLength, Length, 1, isFullInvalidation: true);
        return true;
    }

    private void RaiseChanged(nuint startOffset, nuint oldEndOffset, nuint newEndOffset, int startLineNumber, bool isFullInvalidation) =>
        RaiseChanged(startOffset, oldEndOffset, newEndOffset, startLineNumber, 1, startLineNumber, 1, startLineNumber, 1, isFullInvalidation);

    private void RaiseChanged(
        nuint startOffset,
        nuint oldEndOffset,
        nuint newEndOffset,
        int startLineNumber,
        int startColumn,
        int oldEndLineNumber,
        int oldEndColumn,
        int newEndLineNumber,
        int newEndColumn,
        bool isFullInvalidation) =>
        Changed?.Invoke(this, new MonacoModelChangedEventArgs(
            startOffset, oldEndOffset, newEndOffset, startLineNumber, startColumn, oldEndLineNumber, oldEndColumn, newEndLineNumber, newEndColumn, isFullInvalidation, VersionId));

    private unsafe string GetText()
    {
        var required = NativeMethods.mn_model_copy_text(Handle, null, 0);
        if (required == 0)
            throw new InvalidOperationException("Native GetText failed.");
        if (required == 1)
            return string.Empty;

        var chars = new char[checked((int)required)];
        fixed (char* ptr = chars)
        {
            if (NativeMethods.mn_model_copy_text(Handle, ptr, required) == 0)
                throw new InvalidOperationException("Native GetText failed.");
        }
        return new string(chars, 0, checked((int)required - 1));
    }

    private unsafe string GetLanguage()
    {
        var required = NativeMethods.mn_model_copy_language(Handle, null, 0);
        if (required == 0)
            throw new InvalidOperationException("Native GetLanguage failed.");
        if (required == 1)
            return string.Empty;

        var bytes = new byte[checked((int)required)];
        fixed (byte* ptr = bytes)
        {
            if (NativeMethods.mn_model_copy_language(Handle, ptr, required) == 0)
                throw new InvalidOperationException("Native GetLanguage failed.");
        }
        return Encoding.UTF8.GetString(bytes, 0, checked((int)required - 1));
    }


    internal void NotifyNativeTsfEdit(
        nuint startOffset,
        nuint oldEndOffset,
        nuint newEndOffset,
        int startLineNumber,
        int startColumn,
        int oldEndLineNumber,
        int oldEndColumn,
        int newEndLineNumber,
        int newEndColumn) =>
        RaiseChanged(
            startOffset,
            oldEndOffset,
            newEndOffset,
            startLineNumber,
            startColumn,
            oldEndLineNumber,
            oldEndColumn,
            newEndLineNumber,
            newEndColumn,
            isFullInvalidation: false);

    internal nint NativeHandle => Handle;

    private nint Handle => _handle != 0 ? _handle : throw new ObjectDisposedException(nameof(MonacoTextModel));

    public void Dispose()
    {
        if (_handle == 0)
            return;
        NativeMethods.mn_model_destroy(_handle);
        _handle = 0;
        GC.SuppressFinalize(this);
    }

    ~MonacoTextModel()
    {
        if (_handle != 0)
            NativeMethods.mn_model_destroy(_handle);
    }
}


public sealed class MonacoUndoTransaction : IDisposable
{
    private MonacoTextModel? _model;

    internal MonacoUndoTransaction(MonacoTextModel model) => _model = model;

    public void PushUndoStop() => (_model ?? throw new ObjectDisposedException(nameof(MonacoUndoTransaction))).PushUndoStop();

    public void Complete()
    {
        var model = _model ?? throw new ObjectDisposedException(nameof(MonacoUndoTransaction));
        model.CompleteUndoTransaction();
        _model = null;
    }

    public void Rollback()
    {
        var model = _model ?? throw new ObjectDisposedException(nameof(MonacoUndoTransaction));
        if (!model.RollbackUndoTransaction())
            throw new InvalidOperationException("Failed to roll back undo transaction.");
        _model = null;
    }

    public void Dispose()
    {
        var model = _model;
        if (model is null)
            return;
        _model = null;
        if (!model.RollbackUndoTransaction())
            throw new InvalidOperationException("Failed to roll back incomplete undo transaction.");
    }
}
