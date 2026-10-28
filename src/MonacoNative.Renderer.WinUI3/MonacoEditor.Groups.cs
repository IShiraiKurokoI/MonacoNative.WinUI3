using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using MonacoNative.Documents;
using Windows.Foundation;

namespace MonacoNative.Renderer.WinUI3;

public sealed partial class MonacoEditor
{
    private sealed class EditorWorkspace
    {
        internal readonly HashSet<MonacoEditor> Editors = new();
    }

    private EditorWorkspace? _workspace;
    private MonacoEditor? _groupOwner;
    private List<MonacoEditor>? _ownedGroups;
    private MonacoEditor? _inGroupSplit;
    private Thumb? _inGroupSash;
    private static readonly HashSet<Window> DocumentWindows = new();
    private EditorWorkspace Workspace
    {
        get
        {
            if (_workspace is null)
            {
                _workspace = new EditorWorkspace();
                _workspace.Editors.Add(this);
            }
            return _workspace;
        }
    }
    private MonacoEditor GroupOwner => _groupOwner ?? this;
    public IReadOnlyList<MonacoEditor> EditorGroups => GroupOwner._ownedGroups ??= new() { GroupOwner };
    public event EventHandler<MonacoEditor>? EditorGroupCreated;

    private IEnumerable<MonacoEditor> WindowEditors() => GroupOwner.EditorGroups.Where(g => !g.IsDisposed)
        .SelectMany(g => g._inGroupSplit is { IsDisposed: false } inner ? new[] { g, inner } : new[] { g });

    private IEnumerable<MonacoEditorDocument> WorkspaceDocuments => Workspace.Editors.Where(e => !e.IsDisposed)
        .SelectMany(e => e._documentItems).Where(d => !d.IsClosed)
        .GroupBy(d => d.Uri, EditorDocumentUriComparer.Instance).Select(g => g.First());

    private MonacoEditorDocument? FindWorkspaceDocument(Uri uri) => WorkspaceDocuments.FirstOrDefault(
        d => EditorDocumentUriComparer.Instance.Equals(d.Uri, uri));

    public async Task<MonacoEditorDocument> OpenLinkedDocumentAsync(MonacoEditorDocument source,
        CancellationToken cancellationToken = default)
    {
        VerifyDocumentAccess();
        ArgumentNullException.ThrowIfNull(source);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.ShutdownToken);
        await _documentOperationGate.WaitAsync(linked.Token);
        try
        {
            VerifyDocumentAccess();
            ObjectDisposedException.ThrowIf(source.IsClosed, source);
            if (TryGetOpenDocument(source.Uri) is { } existing)
            {
                if (!existing.SharesBuffer(source))
                    throw new InvalidOperationException("This group already has a different working copy of the same URI.");
                ActivateDocument(existing);
                return existing;
            }
            var replaced = !IsMultiTabEnabled || (_documentItems.Count == 1 && ActiveDocument.IsImplicit && !ActiveDocument.IsDirty)
                ? ActiveDocument : null;
            if (replaced is not null && !await CanCloseDocumentAsync(replaced, linked.Token))
                throw new OperationCanceledException("Opening the document was canceled.", linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            var document = source.CreateLinkedView();
            AddDocument(document);
            ActivateDocument(document);
            if (replaced is not null) RemoveDocument(replaced);
            SetDocumentPinned(document, source.IsPinned);
            DocumentOpened?.Invoke(this, new(document));
            return document;
        }
        finally { _documentOperationGate.Release(); }
    }

    private void RemoveTransferredDocument(MonacoEditorDocument document)
    {
        if (document.IsClosed) return;
        VerifyOpenDocument(document);
        if (document.ViewCount < 2) throw new InvalidOperationException("The destination must retain the document before it is moved.");
        _transferringDocument = true;
        try { CloseDocumentCore(document); }
        finally { _transferringDocument = false; }
    }

    private MonacoEditor CreateRelatedEditor(bool showTabs = true)
    {
        var editor = new MonacoEditor
        {
            RequestedTheme = RequestedTheme, ShowTabs = showTabs && ShowTabs, IsMultiTabEnabled = showTabs,
            IsReadOnly = _hostIsReadOnly,
            UiLanguage = UiLanguage, WordWrap = WordWrap, ShowMinimap = ShowMinimap,
            ShowWhitespace = ShowWhitespace, ShowIndentGuides = ShowIndentGuides,
            WorkspaceRootPath = WorkspaceRootPath, SaveAsProviderAsync = SaveAsProviderAsync,
            ConfirmDocumentCloseAsync = ConfirmDocumentCloseAsync,
            ExternalWorkspaceEditHandler = ExternalWorkspaceEditHandler,
            ReopenEditorWithAsync = ReopenEditorWithAsync, RevealInExplorerViewAsync = RevealInExplorerViewAsync,
            ShareDocumentAsync = ShareDocumentAsync, OpenInTerminalAsync = OpenInTerminalAsync,
            RefactorPreviewDocumentReaderAsync = RefactorPreviewDocumentReaderAsync,
        };
        editor._workspace?.Editors.Remove(editor);
        editor._workspace = Workspace;
        Workspace.Editors.Add(editor);
        editor.EditorChrome.CornerRadius = new CornerRadius(0);
        editor.EditorChrome.BorderThickness = new Thickness(0);
        GroupOwner.EditorGroupCreated?.Invoke(GroupOwner, editor);
        return editor;
    }

    private MonacoEditor CreateEditorGroup(EditorGroupDirection direction)
    {
        if (!IsMultiTabEnabled) throw new InvalidOperationException("Editor groups require multi-tab mode.");
        var owner = GroupOwner;
        _ = owner.EditorGroups;
        FrameworkElement current = ReferenceEquals(owner, this) ? GroupRoot : this;
        if (current.Parent is not Grid parent) throw new InvalidOperationException("The editor group is not attached to its layout.");
        var editor = CreateRelatedEditor();
        editor._groupOwner = owner;
        var vertical = direction is EditorGroupDirection.Up or EditorGroupDirection.Down;
        var before = direction is EditorGroupDirection.Up or EditorGroupDirection.Left;
        var layout = new Grid();
        var row = Grid.GetRow(current); var column = Grid.GetColumn(current);
        parent.Children.Remove(current);
        Grid.SetRow(layout, row); Grid.SetColumn(layout, column);
        parent.Children.Add(layout);
        Grid.SetRow(current, 0); Grid.SetColumn(current, 0);
        if (vertical)
        {
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 60 });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 60 });
            Grid.SetRow(current, before ? 2 : 0); Grid.SetRow(editor, before ? 0 : 2);
        }
        else
        {
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 100 });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 100 });
            Grid.SetColumn(current, before ? 2 : 0); Grid.SetColumn(editor, before ? 0 : 2);
        }
        var sash = CreateLayoutSash(layout, vertical);
        if (vertical) Grid.SetRow(sash, 1); else Grid.SetColumn(sash, 1);
        layout.Children.Add(current); layout.Children.Add(sash); layout.Children.Add(editor);
        owner._ownedGroups!.Add(editor);
        UpdateWorkspaceSnapshots();
        return editor;
    }

    private Thumb CreateLayoutSash(Grid layout, bool vertical)
    {
        var sash = new Thumb { Background = new SolidColorBrush(_selectionColor), Opacity = 0.25 };
        sash.DragDelta += (_, args) =>
        {
            var total = vertical ? layout.ActualHeight - 4 : layout.ActualWidth - 4;
            if (total <= 0) return;
            var current = vertical ? layout.RowDefinitions[0].ActualHeight : layout.ColumnDefinitions[0].ActualWidth;
            var next = Math.Clamp(current + (vertical ? args.VerticalChange : args.HorizontalChange),
                Math.Min(60, total / 2), Math.Max(total / 2, total - 60));
            if (vertical)
            {
                layout.RowDefinitions[0].Height = new GridLength(next / total, GridUnitType.Star);
                layout.RowDefinitions[2].Height = new GridLength(1 - next / total, GridUnitType.Star);
            }
            else
            {
                layout.ColumnDefinitions[0].Width = new GridLength(next / total, GridUnitType.Star);
                layout.ColumnDefinitions[2].Width = new GridLength(1 - next / total, GridUnitType.Star);
            }
        };
        return sash;
    }

    public async Task<MonacoEditor> SplitEditorAsync(EditorGroupDirection direction,
        MonacoEditorDocument? document = null)
    {
        document ??= ActiveDocument;
        VerifyOpenDocument(document);
        KeepDocumentOpen(document);
        SaveActiveDocumentViewState();
        var group = CreateEditorGroup(direction);
        try { await group.OpenLinkedDocumentAsync(document); return group; }
        catch { RemoveEmptyEditorGroup(group); throw; }
    }

    private async Task SplitDocumentsAsync(IReadOnlyList<MonacoEditorDocument> documents, EditorGroupDirection direction)
    {
        var open = documents.Where(d => !d.IsClosed).ToArray();
        if (open.Length == 0) return;
        var group = await SplitEditorAsync(direction, open[0]);
        try
        {
            foreach (var document in open.Skip(1).Where(d => !d.IsClosed))
                await group.OpenLinkedDocumentAsync(document);
        }
        finally { GroupOwner.RemoveEmptyEditorGroup(group); }
    }

    private MonacoEditor? FindAdjacentGroup(EditorGroupDirection direction)
    {
        var owner = GroupOwner;
        FrameworkElement Element(MonacoEditor e) => e == owner ? e.GroupRoot : e;
        Rect Bounds(MonacoEditor e)
        {
            var element = Element(e);
            return element.TransformToVisual(owner.WorkbenchRoot).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        var source = Bounds(this);
        var x = source.X + source.Width / 2; var y = source.Y + source.Height / 2;
        return EditorGroups.Where(e => e != this && !e.IsDisposed).Select(e => (Editor: e, Bounds: Bounds(e)))
            .Select(p => (p.Editor, DX: p.Bounds.X + p.Bounds.Width / 2 - x, DY: p.Bounds.Y + p.Bounds.Height / 2 - y))
            .Where(p => direction switch
            {
                EditorGroupDirection.Left => p.DX < -1, EditorGroupDirection.Right => p.DX > 1,
                EditorGroupDirection.Up => p.DY < -1, _ => p.DY > 1,
            })
            .OrderBy(p => direction is EditorGroupDirection.Left or EditorGroupDirection.Right
                ? Math.Abs(p.DX) + 3 * Math.Abs(p.DY) : Math.Abs(p.DY) + 3 * Math.Abs(p.DX))
            .Select(p => p.Editor).FirstOrDefault();
    }

    private async Task MoveDocumentsToGroupAsync(IReadOnlyList<MonacoEditorDocument> documents, EditorGroupDirection direction)
    {
        var open = documents.Where(d => !d.IsClosed).ToArray();
        if (open.Length == 0) return;
        SaveActiveDocumentViewState();
        var target = FindAdjacentGroup(direction) ?? CreateEditorGroup(direction);
        try
        {
            foreach (var document in open.Where(d => !d.IsClosed))
            {
                await target.OpenLinkedDocumentAsync(document);
                if (!document.IsClosed) RemoveTransferredDocument(document);
            }
        }
        finally { GroupOwner.RemoveEmptyEditorGroup(target); }
    }

    private void RemoveEmptyEditorGroup(MonacoEditor editor)
    {
        var owner = GroupOwner;
        if (editor.IsDisposed || !EditorGroupLifecyclePolicy.ShouldRemoveEmptyGroup(editor == owner,
            editor._documentItems.Select(d => (IsImplicit: d.IsImplicit, IsDirty: d.IsDirty)))) return;
        if (editor.Parent is Grid split && split.Parent is Grid parent)
        {
            var other = split.Children.OfType<FrameworkElement>().FirstOrDefault(e => e != editor && e is not Thumb);
            var row = Grid.GetRow(split);
            var column = Grid.GetColumn(split);
            if (other is not null)
                split.Children.Remove(other);
            parent.Children.Remove(split);
            if (other is not null)
            {
                Grid.SetRow(other, row); Grid.SetColumn(other, column);
                parent.Children.Add(other);
            }
        }
        owner._ownedGroups?.Remove(editor);
        editor.Dispose();
    }

    private async Task ToggleSplitInGroupAsync(MonacoEditorDocument document)
    {
        VerifyOpenDocument(document);
        if (_inGroupSplit is not null) { CloseInGroupSplit(); return; }
        ActivateDocument(document);
        SaveActiveDocumentViewState();
        var other = CreateRelatedEditor(showTabs: false);
        other._groupOwner = GroupOwner;
        _inGroupSplit = other;
        try
        {
            DocumentContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            DocumentContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            DocumentContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _inGroupSash = CreateLayoutSash(DocumentContentGrid, vertical: false);
            Grid.SetColumn(_inGroupSash, 1); Grid.SetColumn(other, 2);
            DocumentContentGrid.Children.Add(_inGroupSash);
            DocumentContentGrid.Children.Add(other);
            await other.OpenLinkedDocumentAsync(document);
        }
        catch { CloseInGroupSplit(); throw; }
    }

    private void CloseInGroupSplit()
    {
        if (_inGroupSplit is not { } editor) return;
        _inGroupSplit = null;
        DocumentContentGrid.Children.Remove(editor);
        if (_inGroupSash is not null) DocumentContentGrid.Children.Remove(_inGroupSash);
        _inGroupSash = null;
        DocumentContentGrid.ColumnDefinitions.Clear();
        editor.Dispose();
    }

    private async Task OpenDocumentsInNewWindowAsync(IReadOnlyList<MonacoEditorDocument> documents, bool move)
    {
        var open = documents.Where(d => !d.IsClosed).ToArray();
        if (open.Length == 0) return;
        SaveActiveDocumentViewState();
        var editor = CreateRelatedEditor();
        var window = new Window { Content = editor, Title = open[0].DisplayName };
        DocumentWindows.Add(window);
        var approved = false;
        var confirming = false;
        window.AppWindow.Closing += async (_, args) =>
        {
            if (approved) return;
            args.Cancel = true;
            if (confirming) return;
            confirming = true;
            try
            {
                approved = await editor.ConfirmCloseAllDocumentsAsync();
                if (approved) window.Close();
            }
            catch (Exception error)
            {
                await editor.RunDocumentUiOperationAsync(() => Task.FromException(error));
            }
            finally { confirming = false; }
        };
        window.Closed += (_, _) => { DocumentWindows.Remove(window); editor.Dispose(); };
        try
        {
            window.Activate();
            foreach (var document in open) await editor.OpenLinkedDocumentAsync(document);
            if (move) foreach (var document in open) RemoveTransferredDocument(document);
        }
        catch { approved = true; window.Close(); throw; }
    }

    private void DisposeEditorGroups()
    {
        CloseInGroupSplit();
        if (_groupOwner is null && _ownedGroups is not null)
            foreach (var group in _ownedGroups.Where(g => g != this).ToArray()) group.Dispose();
        _groupOwner?._ownedGroups?.Remove(this);
        _workspace?.Editors.Remove(this);
        if (_tabDragId is { } id) TabDrags.Remove(id);
        _tabDragId = null;
        _closedTabs.Clear();
    }
}
