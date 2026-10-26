using MonacoNative.Core;
using MonacoNative.Language;

namespace MonacoNative.Renderer.WinUI3;

public sealed partial class MonacoEditor
{
    /// <summary>
    /// Enter belongs to the editor's language configuration (onEnter/indentationRules),
    /// not to LSP completion. Apply the newline and its caret placement atomically.
    /// </summary>
    private void InsertAutoIndentedNewLine()
    {
        if (_isReadOnly) return;
        var eol = _model.EndOfLine == MonacoEndOfLine.CrLf ? "\r\n" : "\n";
        if (_secondarySelections.Count != 0)
        {
            // Each secondary caret may be in a different syntax context; do not apply
            // the primary caret's indentation blindly to every one.
            ReplaceSelection(eol);
            return;
        }

        var (start, end) = OrderedSelectionOffsets();
        var cursor = _model.GetPositionAt(start);
        var line = _model.GetLineContent(cursor.LineNumber);
        var previous = cursor.LineNumber > 1 ? _model.GetLineContent(cursor.LineNumber - 1) : null;
        var config = _languages.Languages.GetLanguageConfiguration(_model.LanguageId);
        var edit = EnterIndentation.Create(line, cursor.Column, config, eol, TabSize, previous);
        _model.Replace(start, end, edit.Text);
        _anchor = _active = _model.GetPositionAt(start + (nuint)edit.CaretOffset);
        AfterProjectionChanged(true);
    }

    /// <summary>Electric closing delimiter: remove one indentation level on a blank indented line.</summary>
    private bool TryInsertOutdentedClosingDelimiter(char character)
    {
        if (_isReadOnly || character is not ('}' or ']' or ')') || _secondarySelections.Count > 0)
            return false;
        var (start, end) = OrderedSelectionOffsets();
        if (start != end) return false;
        var position = _model.GetPositionAt(start);
        var content = _model.GetLineContent(position.LineNumber);
        var count = position.Column - 1;
        if (count == 0 || content[..count].Any(ch => ch is not (' ' or '\t')))
            return false;
        var configuration = _languages.Languages.GetLanguageConfiguration(_model.LanguageId);
        if (configuration?.Brackets.Any(pair => pair.Close == character.ToString()) != true)
            return false;
        var leading = content[..count];
        var reduced = EnterIndentation.Unindent(leading, TabSize);
        if (reduced.Length == leading.Length) return false;
        var beginning = _model.GetOffsetAt(new MonacoPosition(position.LineNumber, 1));
        var replacement = reduced + character;
        _model.Replace(beginning, start, replacement);
        _anchor = _active = _model.GetPositionAt(beginning + (nuint)replacement.Length);
        AfterProjectionChanged(true);
        return true;
    }
}
