using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MonacoNative.Documents;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.System;

namespace MonacoNative.Renderer.WinUI3;

public sealed partial class MonacoEditor
{
    private const string TabDragFormat = "MonacoNative.DocumentTabs";
    private static readonly Dictionary<string, TabDragSession> TabDrags = new();
    private sealed class TabDragSession
    {
        internal required MonacoEditor Source;
        internal required MonacoEditorDocument[] Documents;
        internal required MonacoEditorDocument[] OriginalOrder;
        internal bool Transferred;
    }

    private readonly HashSet<MonacoEditorDocument> _selectedTabs = new();
    private readonly Stack<(IEditorDocumentProvider Provider, int Index, bool Pinned)> _closedTabs = new();
    private MonacoEditorDocument? _tabSelectionAnchor;
    private string? _tabDragId;
    private bool _transferringDocument;
    private bool _showingEmptyGroup;
    private TextBlock? _emptyGroupMessage;

    public event EventHandler? DocumentOrderChanged;
    public string? WorkspaceRootPath { get; set; }
    public Func<MonacoEditorDocument, CancellationToken, Task<IEditorDocumentProvider?>>? SaveAsProviderAsync { get; set; }
    public Func<MonacoEditorDocument, Task>? ReopenEditorWithAsync { get; set; }
    public Func<MonacoEditorDocument, Task>? RevealInExplorerViewAsync { get; set; }
    public Func<MonacoEditorDocument, Task>? ShareDocumentAsync { get; set; }
    public Func<MonacoEditorDocument, Task>? OpenInTerminalAsync { get; set; }

    public IReadOnlyList<MonacoEditorDocument> SelectedDocuments =>
        _documentItems.Where(_selectedTabs.Contains).ToArray();

    private void AttachDocumentTab(TabViewItem tab, MonacoEditorDocument document)
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) => BuildDocumentTabMenu(menu, document);
        tab.ContextFlyout = menu;
        tab.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            var point = e.GetCurrentPoint(tab);
            if (point.Properties.IsMiddleButtonPressed)
            {
                e.Handled = true;
                _ = RunDocumentUiOperationAsync(async () => { await CloseDocumentAsync(document); });
                return;
            }
            if (point.Properties.IsRightButtonPressed)
            {
                if (!_selectedTabs.Contains(document)) SelectOnlyTab(document);
                return;
            }
            if (!point.Properties.IsLeftButtonPressed) return;
            if (IsKeyDown(VirtualKey.Shift) && _tabSelectionAnchor is { } anchor)
            {
                var a = _documentItems.IndexOf(anchor);
                var b = _documentItems.IndexOf(document);
                _selectedTabs.Clear();
                foreach (var item in _documentItems.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1))
                    _selectedTabs.Add(item);
                RefreshTabSelection();
            }
            else if (IsKeyDown(VirtualKey.Control))
            {
                if (!_selectedTabs.Add(document)) _selectedTabs.Remove(document);
                _tabSelectionAnchor = document;
                RefreshTabSelection();
                e.Handled = true;
            }
            else if (!_selectedTabs.Contains(document)) SelectOnlyTab(document);
        }), true);
        tab.DoubleTapped += (_, e) => { KeepDocumentOpen(document); e.Handled = true; };
        tab.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.W && IsKeyDown(VirtualKey.Control))
            {
                e.Handled = true;
                _ = RunDocumentUiOperationAsync(async () => { await CloseDocumentAsync(document); });
            }
        };
    }

    private void SelectOnlyTab(MonacoEditorDocument document)
    {
        _selectedTabs.Clear();
        _selectedTabs.Add(document);
        _tabSelectionAnchor = document;
        RefreshTabSelection();
    }

    private void RefreshTabSelection()
    {
        foreach (var pair in _documentTabs)
        {
            pair.Value.BorderThickness = _selectedTabs.Count > 1 && _selectedTabs.Contains(pair.Key)
                ? new Thickness(0, 2, 0, 0) : new Thickness(0);
            pair.Value.BorderBrush = new SolidColorBrush(_selectionColor);
        }
    }

    private MonacoEditorDocument[] TabContextSelection(MonacoEditorDocument context) =>
        _selectedTabs.Contains(context)
            ? _documentItems.Where(_selectedTabs.Contains).ToArray() : new[] { context };

    private void BuildDocumentTabMenu(MenuFlyout menu, MonacoEditorDocument context)
    {
        menu.Items.Clear();
        if (context.IsClosed || IsDisposed) return;
        var selected = TabContextSelection(context);
        MenuFlyoutItem Add(IList<MenuFlyoutItemBase> items, string label, string id, Func<Task> action,
            bool enabled = true, string shortcut = "")
        {
            var item = new MenuFlyoutItem
            {
                Text = label, IsEnabled = enabled, KeyboardAcceleratorTextOverride = shortcut,
                MinHeight = 24, Padding = new Thickness(12, 2, 12, 2), CornerRadius = new CornerRadius(0),
            };
            AutomationProperties.SetAutomationId(item, id);
            item.Click += async (_, _) => await RunDocumentUiOperationAsync(async () =>
            {
                if (!context.IsClosed) await action();
            });
            items.Add(item);
            return item;
        }
        void Command(string label, string id, Func<Task> action, bool enabled = true, string shortcut = "") =>
            Add(menu.Items, label, id, action, enabled, shortcut);
        Task Close(EditorTabCloseScope scope) => CloseTabsAsync(scope, context, selected);
        var documents = _documentItems.ToArray();
        Command(Local("monacoNative.tabs.close"), "workbench.action.closeActiveEditor", () => Close(EditorTabCloseScope.Selected), shortcut: "Ctrl+W");
        Command(Local("monacoNative.tabs.closeOthers"), "workbench.action.closeOtherEditors", () => Close(EditorTabCloseScope.Others),
            EditorTabClosePolicy.CanCloseOthers(documents, context, selected, d => d.IsPinned, d => d.IsDirty));
        Command(Local("monacoNative.tabs.closeToRight"), "workbench.action.closeEditorsToTheRight", () => Close(EditorTabCloseScope.ToRight),
            EditorTabClosePolicy.CanCloseToRight(documents, context, selected.Length, d => d.IsPinned, d => d.IsDirty));
        Command(Local("monacoNative.tabs.closeSaved"), "workbench.action.closeUnmodifiedEditors", () => Close(EditorTabCloseScope.Saved));
        Command(Local("monacoNative.tabs.closeAll"), "workbench.action.closeEditorsInGroup", () => Close(EditorTabCloseScope.All));
        if (selected.Any(d => d.IsDirty))
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            Command(Local("monacoNative.tabs.save"), "workbench.action.files.save", async () =>
            {
                foreach (var document in selected.Where(d => !d.IsClosed && !d.IsReadOnly))
                    await SaveDocumentAsync(document);
            }, selected.Any(d => !d.IsReadOnly), "Ctrl+S");
            Command(Local("monacoNative.tabs.saveAs"), "workbench.action.files.saveAs", () => SaveDocumentAsFromTabAsync(context), selected.Length == 1);
        }
        if (selected.Length == 2)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            Command(Local("monacoNative.tabs.compareSelected"), "compareSelected", () => ShowDocumentComparisonAsync(selected[0], selected[1]));
        }
        if (selected.All(d => d.Uri.IsFile))
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            Command(Local("monacoNative.tabs.copyPath"), "copyFilePath", () => CopyDocumentPathsAsync(selected, relative: false));
            Command(Local("monacoNative.tabs.copyRelativePath"), "copyRelativeFilePath", () => CopyDocumentPathsAsync(selected, relative: true));
            menu.Items.Add(new MenuFlyoutSeparator());
            Command(Local("monacoNative.tabs.revealFileInExplorer"), "revealFileInOS", () => RevealFileAsync(context));
            if (RevealInExplorerViewAsync is { } reveal)
                Command(Local("monacoNative.tabs.revealInExplorerView"), "revealInExplorer", () => reveal(context), selected.Length == 1);
            if (OpenInTerminalAsync is { } terminal)
                Command(Local("monacoNative.tabs.openInIntegratedTerminal"), "openInTerminal", () => terminal(context), selected.Length == 1);
        }
        if (ReopenEditorWithAsync is { } reopen)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            Command(Local("monacoNative.tabs.reopenEditorWith"), "workbench.action.reopenWithEditor", () => reopen(context), selected.Length == 1);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        Command(Local("monacoNative.tabs.keepOpen"), "workbench.action.keepEditor", () =>
        {
            foreach (var document in selected) KeepDocumentOpen(document);
            return Task.CompletedTask;
        }, selected.Any(d => d.IsPreview));
        Command(Local(context.IsPinned ? "monacoNative.tabs.unpin" : "monacoNative.tabs.pin"),
            context.IsPinned ? "workbench.action.unpinEditor" : "workbench.action.pinEditor", () =>
        {
            var pinned = !context.IsPinned;
            foreach (var document in selected) SetDocumentPinned(document, pinned);
            return Task.CompletedTask;
        });
        if (IsMultiTabEnabled)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            Command(Local("monacoNative.tabs.splitRight"), "workbench.action.splitEditor", () => SplitDocumentsAsync(selected, EditorGroupDirection.Right));
            var split = new MenuFlyoutSubItem { Text = Local("monacoNative.tabs.splitAndMove") };
            AutomationProperties.SetAutomationId(split, "workbench.action.splitAndMove");
            menu.Items.Add(split);
            foreach (var direction in Enum.GetValues<EditorGroupDirection>())
            {
                var captured = direction;
                var label = Local(direction switch
                {
                    EditorGroupDirection.Up => "monacoNative.tabs.splitUp",
                    EditorGroupDirection.Down => "monacoNative.tabs.splitDown",
                    EditorGroupDirection.Left => "monacoNative.tabs.splitLeft",
                    _ => "monacoNative.tabs.splitRight",
                });
                Add(split.Items, label, "workbench.action.splitEditor" + direction,
                    () => SplitDocumentsAsync(selected, captured));
            }
            split.Items.Add(new MenuFlyoutSeparator());
            foreach (var direction in Enum.GetValues<EditorGroupDirection>())
            {
                var captured = direction;
                var name = direction switch
                {
                    EditorGroupDirection.Up => "Above",
                    EditorGroupDirection.Down => "Below",
                    EditorGroupDirection.Left => "Left",
                    _ => "Right",
                };
                var label = Local(direction switch
                {
                    EditorGroupDirection.Up => "monacoNative.tabs.moveAbove",
                    EditorGroupDirection.Down => "monacoNative.tabs.moveBelow",
                    EditorGroupDirection.Left => "monacoNative.tabs.moveLeft",
                    _ => "monacoNative.tabs.moveRight",
                });
                Add(split.Items, label, "workbench.action.moveEditorTo" + name + "Group",
                    () => MoveDocumentsToGroupAsync(selected, captured));
            }
            split.Items.Add(new MenuFlyoutSeparator());
            Add(split.Items, Local(_inGroupSplit is null ? "monacoNative.tabs.splitInGroup" : "monacoNative.tabs.joinInGroup"),
                "workbench.action.toggleSplitEditorInGroup",
                () => ToggleSplitInGroupAsync(context), selected.Length == 1);
            menu.Items.Add(new MenuFlyoutSeparator());
            Command(Local("monacoNative.tabs.moveIntoNewWindow"), "workbench.action.moveEditorToNewWindow", () => OpenDocumentsInNewWindowAsync(selected, move: true));
            Command(Local("monacoNative.tabs.copyIntoNewWindow"), "workbench.action.copyEditorToNewWindow", () => OpenDocumentsInNewWindowAsync(selected, move: false));
        }
        if (ShareDocumentAsync is { } share)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            Command(Local("monacoNative.tabs.share"), "workbench.action.share", () => share(context), selected.Length == 1);
        }
    }

    public void KeepDocumentOpen(MonacoEditorDocument document)
    {
        VerifyOpenDocument(document);
        document.SetPreview(false);
    }

    public void SetDocumentPinned(MonacoEditorDocument document, bool pinned)
    {
        VerifyOpenDocument(document);
        if (document.IsPinned == pinned) return;
        document.SetPinned(pinned);
        var remaining = _documentItems.Where(d => d != document).ToList();
        remaining.Insert(remaining.TakeWhile(d => d.IsPinned).Count(), document);
        ApplyTabOrder(remaining);
    }

    public void MoveDocument(MonacoEditorDocument document, int index)
    {
        VerifyOpenDocument(document);
        KeepDocumentOpen(document);
        ApplyTabOrder(EditorTabOrder.MoveBlock(_documentItems, new[] { document }, index, d => d.IsPinned));
    }

    private void ApplyTabOrder(IReadOnlyList<MonacoEditorDocument> ordered)
    {
        if (ordered.Count != _documentItems.Count || ordered.Distinct().Count() != ordered.Count ||
            ordered.Any(d => !_documentItems.Contains(d)))
            throw new ArgumentException("A tab order must contain each open document exactly once.", nameof(ordered));
        var selected = DocumentTabView.SelectedItem;
        var previous = _synchronizingDocumentTabs;
        _synchronizingDocumentTabs = true;
        try
        {
            for (var i = 0; i < ordered.Count; i++)
            {
                var current = _documentItems.IndexOf(ordered[i]);
                if (current != i) _documentItems.Move(current, i);
                var tab = _documentTabs[ordered[i]];
                var visual = DocumentTabView.TabItems.IndexOf(tab);
                if (visual != i)
                {
                    DocumentTabView.TabItems.Remove(tab);
                    DocumentTabView.TabItems.Insert(i, tab);
                }
            }
            DocumentTabView.SelectedItem = selected;
        }
        finally { _synchronizingDocumentTabs = previous; }
        DocumentOrderChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> CloseTabsAsync(EditorTabCloseScope scope, MonacoEditorDocument? context = null,
        IReadOnlyCollection<MonacoEditorDocument>? selection = null, CancellationToken cancellationToken = default)
    {
        VerifyDocumentAccess();
        context ??= ActiveDocument;
        VerifyOpenDocument(context);
        var chosen = (selection ?? new[] { context }).ToHashSet();
        var targets = EditorTabClosePolicy.GetTargets(scope, _documentItems.ToArray(), context, chosen,
            document => document.IsPinned, document => document.IsDirty);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.ShutdownToken);
        await _documentOperationGate.WaitAsync(linked.Token);
        try
        {
            var approved = new List<(MonacoEditorDocument Document, ulong Version)>();
            foreach (var document in targets)
            {
                if (document.IsClosed || !await CanCloseDocumentAsync(document, linked.Token)) return false;
                approved.Add((document, document.Model.VersionId));
            }
            linked.Token.ThrowIfCancellationRequested();
            if (IsDisposed || approved.Any(p => p.Document.IsClosed || p.Document.Model.VersionId != p.Version)) return false;
            if (scope is EditorTabCloseScope.Others or EditorTabCloseScope.ToRight)
                foreach (var keep in chosen.Where(d => !d.IsClosed)) KeepDocumentOpen(keep);
            foreach (var item in approved) CloseDocumentCore(item.Document);
            return true;
        }
        finally { _documentOperationGate.Release(); }
    }

    public async Task<MonacoEditorDocument?> ReopenClosedDocumentAsync(CancellationToken cancellationToken = default)
    {
        VerifyDocumentAccess();
        if (!_closedTabs.TryPop(out var entry)) return null;
        try
        {
            var document = await OpenDocumentAsync(entry.Provider, cancellationToken);
            SetDocumentPinned(document, entry.Pinned);
            MoveDocument(document, entry.Index);
            return document;
        }
        catch { _closedTabs.Push(entry); throw; }
    }

    private void RememberClosedTab(MonacoEditorDocument document)
    {
        _selectedTabs.Remove(document);
        if (_tabSelectionAnchor == document) _tabSelectionAnchor = null;
        if (_transferringDocument || document.IsImplicit || IsDisposed) return;
        if (_closedTabs.Count >= 32)
        {
            var recent = _closedTabs.Take(31).Reverse().ToArray();
            _closedTabs.Clear();
            foreach (var item in recent) _closedTabs.Push(item);
        }
        _closedTabs.Push((document.Provider, _documentItems.IndexOf(document), document.IsPinned));
    }

    private void DocumentTabView_TabDragStarting(TabView sender, TabViewTabDragStartingEventArgs args)
    {
        if (!IsMultiTabEnabled || args.Tab.Tag is not MonacoEditorDocument document || document.IsClosed)
        { args.Cancel = true; return; }
        SaveActiveDocumentViewState();
        var selected = TabContextSelection(document);
        foreach (var item in selected) KeepDocumentOpen(item);
        _tabDragId = Guid.NewGuid().ToString("N");
        TabDrags[_tabDragId] = new TabDragSession { Source = this, Documents = selected, OriginalOrder = _documentItems.ToArray() };
        args.Data.Properties[TabDragFormat] = _tabDragId;
        args.Data.RequestedOperation = DataPackageOperation.Move | DataPackageOperation.Copy;
    }

    private void DocumentTabView_TabDragCompleted(TabView sender, TabViewTabDragCompletedEventArgs args)
    {
        if (_tabDragId is not { } id) return;
        _tabDragId = null;
        if (!TabDrags.Remove(id, out var session) || IsDisposed || session.Transferred) return;
        var original = session.OriginalOrder.Where(d => !d.IsClosed && _documentItems.Contains(d)).ToArray();
        if (original.Length != _documentItems.Count) return;
        if (args.DropResult == DataPackageOperation.None)
        { ApplyTabOrder(original); return; }
        var visual = sender.TabItems.OfType<TabViewItem>().Select(tab => tab.Tag).OfType<MonacoEditorDocument>().ToArray();
        var dragged = args.Tab.Tag as MonacoEditorDocument;
        var target = Array.IndexOf(visual, dragged);
        var insertion = visual.Take(Math.Max(0, target)).Count(d => !session.Documents.Contains(d));
        var moving = session.Documents.Where(_documentItems.Contains).ToArray();
        ApplyTabOrder(EditorTabOrder.MoveBlock(original, moving, insertion, d => d.IsPinned));
        SelectDocumentTab(ActiveDocument);
    }

    private static TabDragSession? GetTabDrag(DragEventArgs args) =>
        args.DataView.Properties.TryGetValue(TabDragFormat, out var value) && value is string id &&
        TabDrags.TryGetValue(id, out var session) ? session : null;

    private void DocumentTabView_TabStripDragOver(object sender, DragEventArgs args)
    {
        var session = GetTabDrag(args);
        if (!IsMultiTabEnabled || session is null || session.Source == this || !session.Source.DispatcherQueue.HasThreadAccess) return;
        args.AcceptedOperation = IsKeyDown(VirtualKey.Control) ? DataPackageOperation.Copy : DataPackageOperation.Move;
        args.Handled = true;
    }

    private async void DocumentTabView_TabStripDrop(object sender, DragEventArgs args)
    {
        var session = GetTabDrag(args);
        if (!IsMultiTabEnabled || session is null || session.Source == this || !session.Source.DispatcherQueue.HasThreadAccess) return;
        var deferral = args.GetDeferral();
        args.Handled = true;
        var copy = IsKeyDown(VirtualKey.Control);
        var point = args.GetPosition(DocumentTabView);
        var index = _documentItems.Count;
        for (var i = 0; i < _documentItems.Count; i++)
        {
            var tab = _documentTabs[_documentItems[i]];
            var bounds = tab.TransformToVisual(DocumentTabView).TransformBounds(new Rect(0, 0, tab.ActualWidth, tab.ActualHeight));
            if (point.X < bounds.X + bounds.Width / 2) { index = i; break; }
        }
        try
        {
            await RunDocumentUiOperationAsync(async () =>
            {
                session.Transferred = true;
                foreach (var document in session.Documents.Where(d => !d.IsClosed))
                {
                    var opened = await OpenLinkedDocumentAsync(document);
                    MoveDocument(opened, index++);
                    if (!copy) session.Source.RemoveTransferredDocument(document);
                }
                session.Transferred = true;
            });
        }
        finally { deferral.Complete(); }
    }

    private Task CopyDocumentPathsAsync(IEnumerable<MonacoEditorDocument> documents, bool relative)
    {
        var paths = documents.Select(document =>
        {
            var path = document.Uri.IsFile ? document.Uri.LocalPath : document.Uri.AbsoluteUri;
            return relative && document.Uri.IsFile && !string.IsNullOrWhiteSpace(WorkspaceRootPath)
                ? Path.GetRelativePath(WorkspaceRootPath, path) : path;
        });
        var data = new DataPackage();
        data.SetText(string.Join(Environment.NewLine, paths));
        Clipboard.SetContent(data);
        return Task.CompletedTask;
    }

    private static Task RevealFileAsync(MonacoEditorDocument document)
    {
        if (!document.Uri.IsFile) return Task.CompletedTask;
        var start = new System.Diagnostics.ProcessStartInfo("explorer.exe")
        { UseShellExecute = true, Arguments = "/select,\"" + document.Uri.LocalPath + "\"" };
        System.Diagnostics.Process.Start(start);
        return Task.CompletedTask;
    }

    private nint GetOwnerWindowHandle()
    {
        if (XamlRoot is null) throw new InvalidOperationException("The editor must be attached to a window.");
        return Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
    }

    private async Task SaveDocumentAsFromTabAsync(MonacoEditorDocument document)
    {
        IEditorDocumentProvider? destination;
        if (SaveAsProviderAsync is { } choose)
            destination = await choose(document, _lifetime.ShutdownToken);
        else
        {
            var picker = new FileSavePicker { SuggestedFileName = document.DisplayName };
            var extension = Path.GetExtension(document.DisplayName);
            if (string.IsNullOrWhiteSpace(extension)) extension = ".txt";
            picker.FileTypeChoices.Add("Text file", new List<string> { extension });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, GetOwnerWindowHandle());
            var file = await picker.PickSaveFileAsync();
            destination = file is null ? null : new FileDocumentProvider(file.Path, document.LanguageId);
        }
        if (destination is not null && !document.IsClosed)
            await SaveDocumentAsAsync(document, destination, _lifetime.ShutdownToken);
    }

    private void ShowEmptyDocumentGroup()
    {
        _showingEmptyGroup = true;
        SurfaceRoot.Visibility = Visibility.Collapsed;
        if (_documentTabs.TryGetValue(ActiveDocument, out var tab)) tab.Visibility = Visibility.Collapsed;
        if (_emptyGroupMessage is null)
        {
            _emptyGroupMessage = new TextBlock
            {
                Text = "Open a file or create a new text file (Ctrl+N).", FontSize = 13, Opacity = 0.6,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            DocumentContentGrid.Children.Add(_emptyGroupMessage);
        }
        _emptyGroupMessage.Visibility = Visibility.Visible;
        LeaveEditorTextInput();
    }

    private void ShowDocumentGroup()
    {
        _showingEmptyGroup = false;
        if (_emptyGroupMessage is not null) _emptyGroupMessage.Visibility = Visibility.Collapsed;
        if (_documentTabs.TryGetValue(ActiveDocument, out var tab)) tab.Visibility = Visibility.Visible;
        SurfaceRoot.Visibility = Visibility.Visible;
    }
}
