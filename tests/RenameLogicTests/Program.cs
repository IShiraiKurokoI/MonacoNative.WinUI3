using MonacoNative.Renderer.WinUI3;
using MonacoNative.Language;

var passed = 0;
void Equal<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{label}: expected {expected}, got {actual}");
    passed++;
}

var symbol = new RenameSelectionRange(4, 8, 4, 18);
Equal((0, 10), RenameInputPolicy.GetSelection(10, symbol, new(4, 12, 4, 12)), "caret selects whole name");
Equal((2, 5), RenameInputPolicy.GetSelection(10, symbol, new(4, 10, 4, 15)), "selected substring survives F2");
Equal((2, 5), RenameInputPolicy.GetSelection(10, symbol, new(4, 15, 4, 10)), "backward selection");
Equal((0, 10), RenameInputPolicy.GetSelection(10, symbol, new(3, 10, 4, 15)), "multiline selection selects whole name");
Equal((0, 10), RenameInputPolicy.GetSelection(10, symbol, new(4, 6, 4, 10)), "partially outside selection selects whole name");
Equal((0, 10), RenameInputPolicy.GetSelection(10, symbol, new(5, 10, 5, 15)), "other line selects whole name");
Equal((0, 10), RenameInputPolicy.GetSelection(10, symbol, new(4, 8, 4, 18)), "whole-name selection");
Equal((2, 1), RenameInputPolicy.GetSelection(3, symbol, new(4, 10, 4, 15)), "provider placeholder length clamps selection");
Equal((0, 0), RenameInputPolicy.GetSelection(0, symbol, new(4, 10, 4, 15)), "empty provider placeholder");
var unicodeName = "𐐀变量x";
Equal((2, 2), RenameInputPolicy.GetSelection(unicodeName.Length, new(1, 1, 1, 6), new(1, 3, 1, 5)), "UTF-16 non-BMP prefix");
Equal(true, RenameInputPolicy.ShouldCancel("name", "name"), "unchanged cancels");
Equal(false, RenameInputPolicy.ShouldCancel("name", "Name"), "case-sensitive rename applies");
Equal(true, RenameInputPolicy.ShouldCancel("name", null), "null cancels");
Equal(true, RenameInputPolicy.ShouldCancel("name", " \t\u3000"), "Unicode whitespace cancels");
Equal(false, RenameInputPolicy.ShouldCancel("name", " newName "), "nonblank provider text is not silently trimmed");


void Throws<T>(Action action, string label) where T : Exception
{
    try { action(); }
    catch (T) { passed++; return; }
    throw new InvalidOperationException($"{label}: expected {typeof(T).Name}");
}
string Order(string[] input, string[] selected, int index) =>
    string.Join(",", EditorTabOrder.MoveBlock(input, selected, index, tab => tab.StartsWith('!')));
Equal("b,c,a,d", Order(["a", "b", "c", "d"], ["a"], 2), "single-tab move");
Equal("a,b,d,c,e", Order(["a", "b", "c", "d", "e"], ["d", "b"], 1), "multi-tab drag retains source order");
Equal("!p,!q,a,b", Order(["!p", "!q", "a", "b"], ["a"], 0), "ordinary tab cannot cross sticky boundary");
Equal("!q,!p,a,b", Order(["!p", "!q", "a", "b"], ["!p"], 3), "sticky tab remains sticky after drag");
Equal("!q,!p,b,a", Order(["!p", "!q", "a", "b"], ["!p", "a"], 2), "mixed-selection move preserves both partitions");
Equal("a,b", Order(["a", "b"], ["b", "a"], 4), "moving every tab preserves order");
Equal("a,b", Order(["a", "b"], [], -1), "empty selection");
Throws<ArgumentException>(() => Order(["a", "b"], ["c"], 0), "foreign tab rejected");
Throws<ArgumentException>(() => Order(["a", "b"], ["a", "a"], 0), "duplicate selection rejected");
Throws<ArgumentException>(() => Order(["a", "a"], ["a"], 0), "duplicate source tab rejected");

var uri = new Uri("inmemory://preview/source.cs");
WorkspaceTextEdit Edit(int sl, int sc, int el, int ec, string text) => new(uri, new(sl, sc, el, ec), text);
EditorRefactorPreview Preview(string text, params WorkspaceTextEdit[] edits) => new(uri, text, "csharp", "source.cs", edits);
var preview = Preview("alpha();\r\nalpha();\r\n", Edit(1, 1, 1, 6, "beta"), Edit(2, 1, 2, 6, "beta"));
Equal("beta();\r\nbeta();\r\n", preview.BuildModifiedText(), "CRLF and trailing newline preserved");
Equal("alpha();\r\nalpha();\r\n", preview.OriginalText, "preview leaves original immutable");
Equal((bool?)true, preview.Included, "all changes checked");
preview.Changes[1].Included = false;
Equal("beta();\r\nalpha();\r\n", preview.BuildModifiedText(), "unchecked occurrence stays unchanged");
Equal((bool?)null, preview.Included, "parent becomes indeterminate");
preview.SetIncluded(false);
Equal(preview.OriginalText, preview.BuildModifiedText(), "deselecting file restores source snapshot");
Equal((bool?)false, preview.Included, "parent unchecked");
preview.SetIncluded(true);
Equal((bool?)true, preview.Included, "checking parent selects all children");
Equal("😀bar", Preview("😀foo", Edit(1, 3, 1, 6, "bar")).BuildModifiedText(), "UTF-16 range after surrogate pair");
Equal("aXc", Preview("a\r\nb\r\nc", Edit(1, 2, 3, 1, "X")).BuildModifiedText(), "multiline replacement");
Equal("a\rB\rc", Preview("a\rb\rc", Edit(2, 1, 2, 2, "B")).BuildModifiedText(), "standalone CR line endings");
Equal("x", Preview("", Edit(1, 1, 1, 1, "x")).BuildModifiedText(), "insertion into empty source");
Equal("c", Preview("abc", Edit(1, 1, 1, 2, ""), Edit(1, 2, 1, 3, "")).BuildModifiedText(), "adjacent changes are valid");
Throws<ArgumentOutOfRangeException>(() => Preview("a", Edit(1, 1, 2, 1, "b")), "invalid line rejected");
Throws<ArgumentOutOfRangeException>(() => Preview("a\r\n", Edit(1, 1, 1, 4, "b")), "columns cannot include line ending");
Throws<ArgumentException>(() => Preview("abc", Edit(1, 3, 1, 1, "b")), "reversed range rejected");
Throws<ArgumentException>(() => Preview("abc", Edit(1, 1, 1, 3, "x"), Edit(1, 2, 1, 4, "y")), "overlap rejected");
Throws<ArgumentException>(() => Preview("abc", Edit(1, 1, 1, 1, "x"), Edit(1, 1, 1, 1, "y")), "ambiguous same-offset inserts rejected");
Throws<ArgumentException>(() => Preview("abc", new WorkspaceTextEdit(new Uri("inmemory://preview/other.cs"), new(1, 1, 1, 2), "x")), "wrong URI rejected");
Console.WriteLine($"Editor interaction policies: {passed} assertions passed.");
