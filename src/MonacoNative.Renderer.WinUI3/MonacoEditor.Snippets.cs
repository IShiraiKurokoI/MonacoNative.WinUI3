using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using MonacoNative.Core;
using MonacoNative.Language;
using Windows.System;

namespace MonacoNative.Renderer.WinUI3;

public sealed partial class MonacoEditor
{
    private SnippetSession? _snippetSession;
    private bool _applyingSnippetMirrors;
    private bool _snippetMirrorsQueued;
    private bool _selectingSnippetStop;

    public bool IsInSnippetMode => _snippetSession?.IsActive == true;

    private void CancelSnippetSession()
    {
        _snippetSession?.Cancel();
        _snippetSession = null;
        _snippetMirrorsQueued = false;
        _commands.SetContext("inSnippetMode", false);
    }

    private void StartSnippetSession(SnippetExpansion expansion, int offset)
    {
        CancelSnippetSession();
        _snippetSession = new SnippetSession(expansion, offset);
        _commands.SetContext("inSnippetMode", true);
        SelectSnippetStop(_snippetSession.Current);
    }

    private void SelectSnippetStop(SnippetPlaceholder? stop)
    {
        if (stop is null) { CancelSnippetSession(); return; }
        _selectingSnippetStop = true;
        try
        {
            ClearSecondarySelections();
            _anchor = _model.GetPositionAt((nuint)stop.Start);
            _active = _model.GetPositionAt((nuint)stop.End);
            AfterProjectionChanged(true);
            OnSelectionChanged();
            if (stop.Index == 0) CancelSnippetSession();
        }
        finally { _selectingSnippetStop = false; }
    }

    private bool TryHandleSnippetKeyDown(KeyRoutedEventArgs e)
    {
        if (!IsInSnippetMode || _isReadOnly || _textInput?.IsComposing == true) return false;
        if (IsKeyDown(VirtualKey.Control) || IsKeyDown(VirtualKey.Menu) ||
            IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows)) return false;
        if (e.Key == VirtualKey.Escape)
        { CancelSnippetSession(); e.Handled = true; return true; }
        if (e.Key != VirtualKey.Tab) return false;
        FlushSnippetMirrors();
        HideSuggestWidget();
        SelectSnippetStop(_snippetSession?.Move(IsKeyDown(VirtualKey.Shift)));
        e.Handled = true;
        return true;
    }

    private void OnSnippetSelectionChanged()
    {
        if (_selectingSnippetStop || _applyingSnippetMirrors || _snippetMirrorsQueued || !IsInSnippetMode) return;
        var (start, end) = OrderedSelectionOffsets();
        if (!_snippetSession!.ContainsSelection(checked((int)start), checked((int)end))) CancelSnippetSession();
    }

    private void OnSnippetModelChanged(MonacoModelChangedEventArgs change)
    {
        if (_applyingSnippetMirrors || !IsInSnippetMode) return;
        if (change.IsFullInvalidation) { CancelSnippetSession(); return; }
        var text = _model.GetTextRange(change.StartOffset, change.NewEndOffset - change.StartOffset);
        if (!_snippetSession!.ObserveEdit(checked((int)change.StartOffset),
            checked((int)(change.OldEndOffset - change.StartOffset)), text))
        { CancelSnippetSession(); return; }
        if (_snippetMirrorsQueued) return;
        _snippetMirrorsQueued = true;
        // TSF may deliver edits outside the managed ReplaceSelection path. Defer mirrors
        // until its selection transaction has ended; never request a nested TSF write lock.
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed) return;
            FlushSnippetMirrors();
        });
    }

    private void FlushSnippetMirrors()
    {
        if (!_snippetMirrorsQueued || _applyingSnippetMirrors) return;
        _snippetMirrorsQueued = false;
        if (!IsInSnippetMode) return;
        var session = _snippetSession!;
        var anchor = checked((int)_model.GetOffsetAt(_anchor));
        var caret = checked((int)_model.GetOffsetAt(_active));
        var changed = false;
        var transaction = _model.InUndoTransaction ? null : _model.BeginUndoTransaction();
        _applyingSnippetMirrors = true;
        try
        {
            foreach (var group in session.LinkedGroups)
            {
                var edits = session.GetLinkedEdits(group, (start, length) => _model.GetTextRange((nuint)start, (nuint)length));
                foreach (var edit in edits)
                {
                    session.ApplyLinkedEdit(edit);
                    _model.Replace((nuint)edit.Start, (nuint)(edit.Start + edit.Length), edit.Text);
                    anchor = RebaseSnippetCaret(anchor, edit);
                    caret = RebaseSnippetCaret(caret, edit);
                    changed = true;
                }
            }
            _anchor = _model.GetPositionAt((nuint)anchor);
            _active = _model.GetPositionAt((nuint)caret);
            transaction?.Complete();
        }
        catch
        {
            transaction?.Rollback();
            CancelSnippetSession();
            throw;
        }
        finally { transaction?.Dispose(); _applyingSnippetMirrors = false; }
        if (changed) AfterProjectionChanged(true);
    }

    private static int RebaseSnippetCaret(int offset, SnippetEdit edit) => offset >= edit.Start + edit.Length
        ? offset + edit.Text.Length - edit.Length : offset <= edit.Start ? offset : edit.Start + edit.Text.Length;

    // Keep normal typing/deletion and its linked copies in the same undo transaction.
    private void ReplaceWithSnippetTracking(nuint start, nuint end, string text)
    {
        var transaction = IsInSnippetMode && !_model.InUndoTransaction ? _model.BeginUndoTransaction() : null;
        try
        {
            _model.Replace(start, end, text);
            _anchor = _active = _model.GetPositionAt(start + (nuint)text.Length);
            FlushSnippetMirrors();
            transaction?.Complete();
        }
        catch { transaction?.Rollback(); throw; }
        finally { transaction?.Dispose(); }
    }

    private IReadOnlyDictionary<string, string> SnippetVariables()
    {
        var uri = _languageDocument.Uri;
        var path = uri.IsFile ? uri.LocalPath : uri.AbsolutePath;
        var (start, end) = OrderedSelectionOffsets();
        var now = DateTime.Now;
        return new Dictionary<string, string>
        {
            ["TM_FILENAME"] = System.IO.Path.GetFileName(path),
            ["TM_FILENAME_BASE"] = System.IO.Path.GetFileNameWithoutExtension(path),
            ["TM_DIRECTORY"] = System.IO.Path.GetDirectoryName(path) ?? "",
            ["TM_FILEPATH"] = path,
            ["TM_SELECTED_TEXT"] = _model.GetTextRange(start, end - start),
            ["TM_CURRENT_LINE"] = _model.GetLineContent(_active.LineNumber),
            ["TM_CURRENT_WORD"] = SuggestWordPrefix(_active),
            ["TM_LINE_INDEX"] = (_active.LineNumber - 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["TM_LINE_NUMBER"] = _active.LineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["CURRENT_YEAR"] = now.ToString("yyyy"), ["CURRENT_YEAR_SHORT"] = now.ToString("yy"),
            ["CURRENT_MONTH"] = now.ToString("MM"), ["CURRENT_DATE"] = now.ToString("dd"),
            ["CURRENT_HOUR"] = now.ToString("HH"), ["CURRENT_MINUTE"] = now.ToString("mm"),
            ["CURRENT_SECOND"] = now.ToString("ss"), ["UUID"] = Guid.NewGuid().ToString(),
        };
    }

    private string AdjustCompletionIndentation(string text, MonacoRange range, int mode)
    {
        if (mode != 2 || !text.Contains('\n')) return text;
        var line = _model.GetLineContent(range.StartLineNumber);
        var indent = new string(line.TakeWhile(c => c is ' ' or '\t').ToArray());
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var firstIndent = lines[0].TakeWhile(c => c is ' ' or '\t').Count();
        for (var i = 1; i < lines.Length; i++)
        {
            var removable = Math.Min(firstIndent, lines[i].TakeWhile(c => c is ' ' or '\t').Count());
            lines[i] = indent + lines[i][removable..];
        }
        return string.Join(text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n", lines);
    }
}
