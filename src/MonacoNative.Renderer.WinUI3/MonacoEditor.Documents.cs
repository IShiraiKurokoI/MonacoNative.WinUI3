using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using MonacoNative.Core;
using MonacoNative.Documents;
using MonacoNative.Language;
using MonacoNative.Language.BracketPairs;

namespace MonacoNative.Renderer.WinUI3;

public sealed partial class MonacoEditor
{
    private readonly ObservableCollection<MonacoEditorDocument> _documentItems = new();
    private ReadOnlyObservableCollection<MonacoEditorDocument>? _documentsView;
    private readonly Dictionary<MonacoEditorDocument, TabViewItem> _documentTabs = new();
    private readonly SemaphoreSlim _documentOperationGate = new(1, 1);
    private MonacoEditorDocument? _activeDocument;
    private bool _synchronizingDocumentTabs;
    private bool _documentsInitialized;
    private int _untitledSequence;
    private long _documentActivationSequence;
    private NativeLanguageDocument[] _csharpDocumentSnapshots = Array.Empty<NativeLanguageDocument>();
    private NativeLanguageDocument[] _cppDocumentSnapshots = Array.Empty<NativeLanguageDocument>();
    private NativeLanguageDocument[] _typescriptDocumentSnapshots = Array.Empty<NativeLanguageDocument>();

    /// <summary>The native tab strip is independent of the ability to keep multiple documents open.</summary>
    public bool ShowTabs
    {
        get => (bool)GetValue(ShowTabsProperty);
        set => SetValue(ShowTabsProperty, value);
    }
    public static readonly DependencyProperty ShowTabsProperty = DependencyProperty.Register(
        nameof(ShowTabs), typeof(bool), typeof(MonacoEditor),
        new PropertyMetadata(true, OnDocumentTabOptionsChanged));

    /// <summary>
    /// False enables single-document replacement with normal dirty-close confirmation.
    /// To disable while several documents are open, use SetMultiTabEnabledAsync first.
    /// </summary>
    public bool IsMultiTabEnabled
    {
        get => (bool)GetValue(IsMultiTabEnabledProperty);
        set
        {
            if (!value && (_documentItems.Count > 1 || (_groupOwner is null && _ownedGroups?.Count > 1)))
                throw new InvalidOperationException("Close the other documents or use SetMultiTabEnabledAsync before disabling multiple tabs.");
            SetValue(IsMultiTabEnabledProperty, value);
        }
    }
    public static readonly DependencyProperty IsMultiTabEnabledProperty = DependencyProperty.Register(
        nameof(IsMultiTabEnabled), typeof(bool), typeof(MonacoEditor),
        new PropertyMetadata(true, OnDocumentTabOptionsChanged));

    public ReadOnlyObservableCollection<MonacoEditorDocument> Documents =>
        _documentsView ??= new ReadOnlyObservableCollection<MonacoEditorDocument>(_documentItems);

    public MonacoEditorDocument ActiveDocument
    {
        get => _activeDocument ?? throw new InvalidOperationException("The editor has not been initialized.");
        set => ActivateDocument(value);
    }

    public event EventHandler<EditorDocumentEventArgs>? DocumentOpened;
    public event EventHandler<EditorDocumentEventArgs>? ActiveDocumentChanged;
    public event EventHandler<EditorDocumentClosingEventArgs>? DocumentClosing;
    public event EventHandler<EditorDocumentEventArgs>? DocumentClosed;
    public event EventHandler<EditorDocumentEventArgs>? DocumentSaved;
    /// <summary>Errors from built-in tab buttons/shortcuts. Explicit async API calls throw instead.</summary>
    public event EventHandler<EditorDocumentOperationFailedEventArgs>? DocumentOperationFailed;
    public Exception? LastDocumentOperationError { get; private set; }

    /// <summary>Optional host confirmation UI. Called on the UI thread for dirty documents.</summary>
    public Func<MonacoEditorDocument, CancellationToken, Task<DocumentCloseAction>>? ConfirmDocumentCloseAsync { get; set; }

    /// <summary>
    /// Applies unopened workspace targets. Return true only after every supplied edit succeeds.
    /// The host owns persistence and rollback for those external resources.
    /// </summary>
    public Func<WorkspaceEdit, CancellationToken, Task<bool>>? ExternalWorkspaceEditHandler { get; set; }

    private double EditorViewportWidth => SurfaceRoot.ActualWidth;
    private double EditorViewportHeight => SurfaceRoot.ActualHeight;

    private static void OnDocumentTabOptionsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var editor = (MonacoEditor)sender;
        if (args.Property == IsMultiTabEnabledProperty && args.NewValue is false && (editor._documentItems.Count > 1 || (editor._groupOwner is null && editor._ownedGroups?.Count > 1)))
        {
            editor.SetValue(IsMultiTabEnabledProperty, true);
            throw new InvalidOperationException("Use SetMultiTabEnabledAsync to resolve open documents before disabling multiple tabs.");
        }
        editor.UpdateDocumentTabVisibility();
        if (args.Property == ShowTabsProperty && editor._groupOwner is null && editor._ownedGroups is not null)
            foreach (var child in editor._ownedGroups.Where(child => child != editor)) child.ShowTabs = editor.ShowTabs;
    }

    private void InitializeDocuments()
    {
        var provider = new MemoryDocumentProvider(_model.Text, NextUntitledName(), _model.LanguageId);
        _languageDocument.Uri = provider.Uri;
        var initial = new MonacoEditorDocument(provider, _model, _view, _languageDocument, _model.Text)
        { IsImplicit = true, WorkspaceText = _model.Text };
        _activeDocument = initial;
        AddDocument(initial);
        _model.Changed -= Model_Changed;
        _model.Changed += Model_Changed;
        _documentsInitialized = true;
        SelectDocumentTab(initial);
        UpdateDocumentTabVisibility();
        RegisterDocumentCommands();
    }

    private string NextUntitledName() => $"Untitled-{++_untitledSequence}";

    private void VerifyDocumentAccess()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (!DispatcherQueue.HasThreadAccess)
            throw new InvalidOperationException("Editor document operations must run on the owning UI thread.");
    }

    private void VerifyOpenDocument(MonacoEditorDocument document)
    {
        VerifyDocumentAccess();
        ArgumentNullException.ThrowIfNull(document);
        if (document.IsClosed || !_documentItems.Contains(document))
            throw new ArgumentException("The document is not open in this editor.", nameof(document));
    }

    internal MonacoEditorDocument? TryGetOpenDocument(Uri uri) => _documentItems.FirstOrDefault(
        document => EditorDocumentUriComparer.Instance.Equals(document.Uri, uri));

    public Task<MonacoEditorDocument> OpenFileAsync(string path, string? languageId = null,
        CancellationToken cancellationToken = default) =>
        OpenDocumentAsync(new FileDocumentProvider(path, languageId), cancellationToken);

    public Task<MonacoEditorDocument> OpenMemoryAsync(string text = "", string? displayName = null,
        string? languageId = null, Uri? uri = null, CancellationToken cancellationToken = default) =>
        OpenDocumentAsync(new MemoryDocumentProvider(text, displayName ?? NextUntitledName(), languageId, uri), cancellationToken);

    public Task<MonacoEditorDocument> OpenPreviewDocumentAsync(IEditorDocumentProvider provider,
        CancellationToken cancellationToken = default) => OpenDocumentAsync(provider, cancellationToken, preview: true);

    /// <summary>
    /// Opens or activates a provider URI. Loading failure leaves every existing document intact.
    /// In single-document mode a canceled replacement throws OperationCanceledException.
    /// </summary>
    public async Task<MonacoEditorDocument> OpenDocumentAsync(IEditorDocumentProvider provider,
        CancellationToken cancellationToken = default, bool focusEditor = true, bool preview = false)
    {
        VerifyDocumentAccess();
        ArgumentNullException.ThrowIfNull(provider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.ShutdownToken);
        var token = linked.Token;
        await _documentOperationGate.WaitAsync(token);
        try
        {
            VerifyDocumentAccess();
            if (TryGetOpenDocument(provider.Uri) is { } existing)
            {
                if (!preview) existing.SetPreview(false);
                ActivateDocument(existing, focusEditor);
                return existing;
            }
            var shared = FindWorkspaceDocument(provider.Uri);
            var content = shared is null ? await provider.ReadAsync(token) :
                new EditorDocumentContent(shared.Model.Text, shared.LanguageId);
            token.ThrowIfCancellationRequested();
            VerifyDocumentAccess();
            // Hosts can still change active documents while an asynchronous provider loads.
            var replaced = !IsMultiTabEnabled || (_documentItems.Count == 1 &&
                ActiveDocument.IsImplicit && !ActiveDocument.IsDirty && ActiveDocument.Model.Length == 0)
                ? ActiveDocument : preview ? _documentItems.FirstOrDefault(d => d.IsPreview && !d.IsDirty && !d.IsPinned) : null;
            if (replaced is not null && !await CanCloseDocumentAsync(replaced, token))
                throw new OperationCanceledException("Opening the document was canceled.", token);
            token.ThrowIfCancellationRequested();
            VerifyDocumentAccess();
            // Legacy hosts can synchronously change a memory document's URI while
            // provider I/O or their close-confirmation callback is awaiting.
            if (TryGetOpenDocument(provider.Uri) is { } openedDuringLoad)
            {
                ActivateDocument(openedDuringLoad, focusEditor);
                return openedDuringLoad;
            }
            if (shared is { IsClosed: true })
            {
                shared = FindWorkspaceDocument(provider.Uri);
                if (shared is null) content = await provider.ReadAsync(token);
                token.ThrowIfCancellationRequested();
                VerifyDocumentAccess();
            }
            var document = shared is { IsClosed: false } ? shared.CreateLinkedView() : CreateDocument(provider, content);
            document.SetPreview(preview);
            AddDocument(document);
            ActivateDocument(document, focusEditor);
            if (replaced is not null) RemoveDocument(replaced);
            DocumentOpened?.Invoke(this, new(document));
            return document;
        }
        finally { _documentOperationGate.Release(); }
    }

    private MonacoEditorDocument CreateDocument(IEditorDocumentProvider provider, EditorDocumentContent content)
    {
        var language = content.LanguageId ?? provider.LanguageId ?? EditorDocumentLanguages.InferFromPath(provider.DisplayName);
        var model = new MonacoTextModel(content.Text, language ?? "plaintext");
        try
        {
            var view = new MonacoViewModel(model);
            return new MonacoEditorDocument(provider, model, view, new MonacoLanguageDocument(model, provider.Uri), content.Text)
            { WorkspaceText = content.Text };
        }
        catch { model.Dispose(); throw; }
    }

    private void AddDocument(MonacoEditorDocument document)
    {
        document.SetReadOnly(_hostIsReadOnly);
        _synchronizingDocumentTabs = true;
        try
        {
            _documentItems.Add(document);
            var tab = new TabViewItem { Tag = document, IsClosable = true, CornerRadius = new CornerRadius(0) };
            AttachDocumentTab(tab, document);
            _documentTabs.Add(document, tab);
            document.PropertyChanged += Document_PropertyChanged;
            document.Model.Changed += DocumentModel_Changed;
            UpdateDocumentTab(document);
            DocumentTabView.TabItems.Add(tab);
        }
        finally { _synchronizingDocumentTabs = false; }
        UpdateWorkspaceSnapshots();
    }

    public void ActivateDocument(MonacoEditorDocument document) => ActivateDocument(document, focusEditor: true);

    private void ActivateDocument(MonacoEditorDocument document, bool focusEditor)
    {
        VerifyOpenDocument(document);
        if (ReferenceEquals(document, _activeDocument))
        {
            HideTransientEditor();
            ShowDocumentGroup();
            SelectDocumentTab(document);
            if (focusEditor && _loaded) Focus(FocusState.Programmatic);
            return;
        }
        if (!IsRenamePreviewOpen) CancelRenameSession();
        CloseInGroupSplit();
        HideTransientEditor();
        DetachTextInputController(notifyFocusLeave: true);
        SaveActiveDocumentViewState();
        CancelDocumentFeatures();
        _model.Changed -= Model_Changed;
        if (_model.UndoSelectionProvider == CaptureUndoSelections) _model.UndoSelectionProvider = null;
        _view.SetTextMeasurer(null);

        _activeDocument = document;
        ShowDocumentGroup();
        _model = document.Model;
        _view = document.ViewModel;
        _languageDocument = document.LanguageDocument;
        // Inactive models may be changed through their public API or workspace edits.
        // Recreate constructor-bound caches instead of trusting equal version numbers.
        _languageTokens = new IncrementalTokenizationCache(_languageDocument, _languages);
        _bracketPairs = new BracketPairsTree(_languageDocument, _languageTokens, _languages.Languages);
        if (focusEditor) _model.UndoSelectionProvider = CaptureUndoSelections;
        _model.Changed += Model_Changed;
        _anchor = Normalize(document.Anchor.LineNumber, document.Anchor.Column);
        _active = Normalize(document.Active.LineNumber, document.Active.Column);
        _secondarySelections.Clear();
        foreach (var selection in document.SecondarySelections)
            _secondarySelections.Add((Normalize(selection.Anchor.LineNumber, selection.Anchor.Column),
                Normalize(selection.Active.LineNumber, selection.Active.Column)));
        _foldRanges = new SortedDictionary<int, int>(document.FoldRanges);
        _collapsedFolds.Clear();
        foreach (var line in document.CollapsedFolds) _collapsedFolds.Add(line);
        _isReadOnly = document.IsReadOnly;
        _preferredCaretX = null;
        _highlightedWord = string.Empty;
        InvalidateWordHighlights();
        _minimap.Invalidate();
        _layoutCache?.Clear();
        _maxObservedLineWidth = 0;
        _lastProjectionViewportWidth = _lastProjectionWrapWidth = _lastProjectionLineHeight = double.NaN;
        _lastRenderedVersion = 0;
        _modelRefreshQueued = false;
        _visibleBracketsByModelLine = new Dictionary<int, IReadOnlyList<BracketTreeInfo>>();
        _findSelectionSnapshotAvailable = false;
        _findMatches = Array.Empty<MonacoRange>();
        _regexFindHits = Array.Empty<EditorFindEngine.Hit>();
        _findMatchIndex = -1;
        _languageDiagnostics = _languages.GetDiagnostics(document.Uri);
        _languageDiagnosticIndex = new LineRangeIndex<LanguageDiagnostic>(_languageDiagnostics,
            static diagnostic => (diagnostic.Range.StartLineNumber, diagnostic.Range.EndLineNumber));
        ClearDragHitTestCache();
        _dragSelecting = _dragSelectingByWord = false;
        _fallbackCharacterAccumulator.Reset();
        SetDiffDecorations(document.DiffLines, document.DiffText);
        SelectDocumentTab(document);
        UpdateWorkspaceSnapshots();
        var activation = ++_documentActivationSequence;
        if (_loaded)
        {
            EnsureGraphicsResources();
            UpdateProjectionMetrics();
            AttachTextInputController(CreateTextInputController(), restoreFocus: focusEditor || IsEditorTextFocused);
            RestoreDocumentScroll(document, activation);
            if (focusEditor) Focus(FocusState.Programmatic);
            ScheduleDecorationRefresh(1, immediate: true);
            ScheduleFoldingRefresh();
            ScheduleContributionRefresh(full: true);
            ScheduleNativeDiagnostics();
        }
        _lastKnownViewLineCount = _view.ViewLineCount;
        if (IsFindOpen) RefreshFindMatches(selectNearest: false);
        UpdateCommandContext();
        ResetCaretBlink();
        RaiseAccessibilityTextChanged();
        RaiseAccessibilitySelectionChanged();
        DiagnosticsChanged?.Invoke(this, EventArgs.Empty);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        ModelChanged?.Invoke(this, EventArgs.Empty);
        ActiveDocumentChanged?.Invoke(this, new(document));
        Render(RenderDirty.All);
    }

    private void SaveActiveDocumentViewState()
    {
        if (_activeDocument is not { } current) return;
        current.Anchor = _anchor;
        current.Active = _active;
        current.SecondarySelections.Clear();
        current.SecondarySelections.AddRange(_secondarySelections);
        current.ScrollTop = ScrollHost.VerticalOffset;
        current.ScrollLeft = ScrollHost.HorizontalOffset;
        current.FoldRanges = new SortedDictionary<int, int>(_foldRanges);
        current.CollapsedFolds.Clear();
        current.CollapsedFolds.UnionWith(_collapsedFolds);
        current.DiffLines = _diffLineDecorations;
        current.DiffText = _diffTextDecorations;
        current.WorkspaceText = _model.Text;
    }

    private void RestoreDocumentScroll(MonacoEditorDocument document, long activation)
    {
        ScrollHost.ChangeView(document.ScrollLeft, document.ScrollTop, null, true);
        // Extent changes take effect during XAML layout. Retry on the next dispatcher
        // turn, but never scroll a later tab because a previous restore was queued.
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (IsDisposed || activation != _documentActivationSequence || _activeDocument != document) return;
            ScrollHost.ChangeView(document.ScrollLeft, document.ScrollTop, null, true);
            _textInput?.NotifyLayoutChanged();
        });
    }

    private void CancelDocumentFeatures()
    {
        CancelNativeDiagnostics();
        CancelFoldingRefresh();
        CancelAutomaticSuggestions();
        HideSuggestWidget();
        HideParameterHints();
        HideContributionPopup();
        CancelSnippetSession();
        InvalidateLanguageFeatureRequests();
        DetachLspBindingForDocumentChange();
        _contributionRefreshCts?.Cancel();
        _contributionRefreshTimer.Stop();
        _foldRefreshTimer.Stop();
        _decorationIdleTimer.Stop();
        ClearInlineCompletion(updateProjection: false);
        ClearCodeLensControls();
        ClearLanguageInlayHints();
        ClearSemanticTokens();
        _cachedCodeActions = Array.Empty<CodeAction>();
        StickyScrollPanel.Children.Clear();
        StickyScrollHost.Visibility = Visibility.Collapsed;
        _commands.ResetChord();
        _chordTimer.Stop();
    }

    public async Task SaveDocumentAsync(MonacoEditorDocument? document = null, CancellationToken cancellationToken = default)
    {
        VerifyDocumentAccess();
        document ??= ActiveDocument;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.ShutdownToken);
        await _documentOperationGate.WaitAsync(linked.Token);
        try { await SaveDocumentCoreAsync(document, linked.Token); }
        finally { _documentOperationGate.Release(); }
    }

    private async Task SaveDocumentCoreAsync(MonacoEditorDocument document, CancellationToken token)
    {
        VerifyOpenDocument(document);
        if (document.IsReadOnly) throw new InvalidOperationException("The document is read-only.");
        token.ThrowIfCancellationRequested();
        await document.SaveGate.WaitAsync(token);
        try
        {
            VerifyOpenDocument(document);
            var text = document.Model.Text;
            await document.Provider.WriteAsync(text, token);
            if (IsDisposed || document.IsClosed) return;
            document.MarkSaved(text);
            document.SetPreview(false);
            DocumentSaved?.Invoke(this, new(document));
        }
        finally { document.SaveGate.Release(); }
    }

    /// <summary>
    /// Writes a snapshot to a caller-selected destination and changes document identity only
    /// after a successful save. The caller is responsible for choosing/confirming that destination.
    /// </summary>
    public async Task SaveDocumentAsAsync(MonacoEditorDocument document, IEditorDocumentProvider destination,
        CancellationToken cancellationToken = default)
    {
        VerifyOpenDocument(document);
        ArgumentNullException.ThrowIfNull(destination);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.ShutdownToken);
        await _documentOperationGate.WaitAsync(linked.Token);
        try
        {
            VerifyOpenDocument(document);
            if (EditorDocumentUriComparer.Instance.Equals(document.Provider.Uri, destination.Uri))
            { await SaveDocumentCoreAsync(document, linked.Token); return; }
            await document.SaveGate.WaitAsync(linked.Token);
            try
            {
                VerifyOpenDocument(document);
                if (FindWorkspaceDocument(destination.Uri) is { } other && !other.SharesBuffer(document))
                    throw new InvalidOperationException("The destination is already open in another tab.");
                if (destination.IsReadOnly) throw new InvalidOperationException("The destination is read-only.");
                _ = await destination.ReadAsync(linked.Token);
                VerifyOpenDocument(document);
                var text = document.Model.Text;
                await destination.WriteAsync(text, linked.Token);
                if (IsDisposed || document.IsClosed) return;
                if (document == _activeDocument) { CancelRenameSession(); CancelDocumentFeatures(); }
                _languages.ClearDiagnostics(document.Uri);
                document.SetProvider(destination);
                document.SetReadOnly(_hostIsReadOnly);
                document.MarkSaved(text);
                document.SetPreview(false);
                document.WorkspaceText = document.Model.Text;
                if (document == _activeDocument)
                {
                    _isReadOnly = document.IsReadOnly;
                    _textInput?.SetReadOnly(_isReadOnly || !IsEditorTextFocused);
                    ScheduleNativeDiagnostics();
                    ScheduleFoldingRefresh();
                    ScheduleContributionRefresh(full: true);
                    UpdateCommandContext();
                }
                UpdateWorkspaceSnapshots();
                DocumentSaved?.Invoke(this, new(document));
            }
            finally { document.SaveGate.Release(); }
        }
        finally { _documentOperationGate.Release(); }
    }

    public async Task<bool> CloseDocumentAsync(MonacoEditorDocument? document = null,
        CancellationToken cancellationToken = default)
    {
        VerifyDocumentAccess();
        document ??= ActiveDocument;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.ShutdownToken);
        await _documentOperationGate.WaitAsync(linked.Token);
        try
        {
            VerifyOpenDocument(document);
            if (!await CanCloseDocumentAsync(document, linked.Token)) return false;
            linked.Token.ThrowIfCancellationRequested();
            CloseDocumentCore(document);
            return true;
        }
        finally { _documentOperationGate.Release(); }
    }

    private async Task<bool> CanCloseDocumentAsync(MonacoEditorDocument document, CancellationToken token,
        bool forcePrompt = false, bool suppressPrompt = false)
    {
        VerifyOpenDocument(document);
        var request = new EditorDocumentClosingEventArgs(document);
        DocumentClosing?.Invoke(this, request);
        if (request.Cancel || request.Action == DocumentCloseAction.Cancel) return false;
        var inlineViews = _inGroupSplit?._documentItems.Count(d => d.SharesBuffer(document)) ?? 0;
        if (!document.IsDirty || suppressPrompt || (!forcePrompt && document.ViewCount > 1 + inlineViews)) return true;
        var decisionVersion = document.Model.VersionId;
        var action = request.Action;
        if (action == DocumentCloseAction.Prompt)
        {
            if (ConfirmDocumentCloseAsync is { } confirm)
                action = await confirm(document, token);
            else if (XamlRoot is not null)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    RequestedTheme = RequestedTheme,
                    Title = LocalFormat("monacoNative.documents.saveChanges", document.DisplayName),
                    Content = Local(document.Provider is MemoryDocumentProvider
                        ? "monacoNative.documents.memoryWarning" : "monacoNative.documents.unsavedWarning"),
                    PrimaryButtonText = Local(document.Provider is MemoryDocumentProvider
                        ? "monacoNative.documents.saveMemory" : "monacoNative.documents.save"),
                    SecondaryButtonText = Local("monacoNative.documents.discard"),
                    CloseButtonText = Local("monacoNative.documents.cancel"),
                    DefaultButton = ContentDialogButton.Primary,
                    IsPrimaryButtonEnabled = !document.IsReadOnly,
                };
                AutomationProperties.SetAutomationId(dialog, "EditorDocumentCloseDialog");
                using var registration = token.Register(() => { _ = DispatcherQueue.TryEnqueue(() => dialog.Hide()); });
                var result = await dialog.ShowAsync();
                token.ThrowIfCancellationRequested();
                action = result switch
                {
                    ContentDialogResult.Primary => DocumentCloseAction.Save,
                    ContentDialogResult.Secondary => DocumentCloseAction.Discard,
                    _ => DocumentCloseAction.Cancel,
                };
            }
            else return false; // Headless or not loaded: never silently discard edits.
        }
        token.ThrowIfCancellationRequested();
        VerifyOpenDocument(document);
        if (action == DocumentCloseAction.Save)
        {
            await SaveDocumentCoreAsync(document, token);
            return !document.IsClosed && !document.IsDirty;
        }
        // A host confirmation callback can yield while the user or a background feature
        // edits this buffer. A discard decision must not silently cover those newer edits.
        return action == DocumentCloseAction.Discard && document.Model.VersionId == decisionVersion;
    }

    private void CloseDocumentCore(MonacoEditorDocument document)
    {
        if (_activeDocument == document)
        {
            var index = _documentItems.IndexOf(document);
            var next = _documentItems.Where(d => d != document).ElementAtOrDefault(Math.Max(0, index - 1));
            if (next is null)
            {
                var provider = new MemoryDocumentProvider("", NextUntitledName(), "plaintext");
                next = CreateDocument(provider, new EditorDocumentContent("", "plaintext"));
                next.IsImplicit = true;
                AddDocument(next);
            }
            ActivateDocument(next);
        }
        RemoveDocument(document);
        if (_documentItems.Count == 1 && ActiveDocument.IsImplicit && !ActiveDocument.IsDirty)
            ShowEmptyDocumentGroup();
        GroupOwner.RemoveEmptyEditorGroup(this);
    }

    private void RemoveDocument(MonacoEditorDocument document)
    {
        RememberClosedTab(document);
        document.PropertyChanged -= Document_PropertyChanged;
        document.Model.Changed -= DocumentModel_Changed;
        _synchronizingDocumentTabs = true;
        try
        {
            if (_documentTabs.Remove(document, out var tab)) DocumentTabView.TabItems.Remove(tab);
            _documentItems.Remove(document);
        }
        finally { _synchronizingDocumentTabs = false; }
        _languages.ClearDiagnostics(document.Uri);
        document.Close();
        UpdateWorkspaceSnapshots();
        DocumentClosed?.Invoke(this, new(document));
    }

    /// <summary>Closes other tabs through the same save/cancel workflow before disabling multi-tab mode.</summary>
    public async Task<bool> SetMultiTabEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        VerifyDocumentAccess();
        if (enabled) { IsMultiTabEnabled = true; return true; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.ShutdownToken);
        var groups = (_groupOwner is null ? WindowEditors() : new[] { this }).ToArray();
        var acquired = new List<MonacoEditor>();
        try
        {
            foreach (var group in groups)
            { await group._documentOperationGate.WaitAsync(linked.Token); acquired.Add(group); }
            VerifyDocumentAccess();
            var keep = ActiveDocument;
            var targets = groups.SelectMany(group => group._documentItems.Select(document => (Editor: group, Document: document)))
                .Where(p => p.Document != keep).ToArray();
            if (!await ConfirmDocumentSetAsync(targets, linked.Token)) return false;
            foreach (var target in targets)
                if (!target.Document.IsClosed) target.Editor.CloseDocumentCore(target.Document);
            CloseInGroupSplit();
            if (_groupOwner is null && _ownedGroups is not null)
                foreach (var child in _ownedGroups.Where(g => g != this).ToArray()) RemoveEmptyEditorGroup(child);
            ActivateDocument(keep);
            IsMultiTabEnabled = false;
            return true;
        }
        finally { foreach (var group in acquired.AsEnumerable().Reverse()) group._documentOperationGate.Release(); }
    }

    /// <summary>Resolves unsaved changes in this window without removing any document views.</summary>
    public async Task<bool> ConfirmCloseAllDocumentsAsync(CancellationToken cancellationToken = default)
    {
        VerifyDocumentAccess();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.ShutdownToken);
        var groups = WindowEditors().ToArray();
        var acquired = new List<MonacoEditor>();
        try
        {
            foreach (var group in groups)
            { await group._documentOperationGate.WaitAsync(linked.Token); acquired.Add(group); }
            var targets = groups.SelectMany(group => group._documentItems.Select(document => (Editor: group, Document: document))).ToArray();
            return await ConfirmDocumentSetAsync(targets, linked.Token) &&
                groups.SequenceEqual(WindowEditors()) && targets.Length == groups.Sum(g => g._documentItems.Count);
        }
        finally { foreach (var group in acquired.AsEnumerable().Reverse()) group._documentOperationGate.Release(); }
    }

    private async Task<bool> ConfirmDocumentSetAsync(
        IReadOnlyList<(MonacoEditor Editor, MonacoEditorDocument Document)> targets, CancellationToken token)
    {
        var asked = new HashSet<MonacoTextModel>();
        var versions = new List<(MonacoEditorDocument Document, ulong Version)>();
        foreach (var target in targets)
        {
            var document = target.Document;
            if (document.IsClosed || target.Editor.IsDisposed) return false;
            var closingViews = targets.Count(p => p.Document.SharesBuffer(document));
            var needsPrompt = document.ViewCount <= closingViews && asked.Add(document.Model);
            if (!await target.Editor.CanCloseDocumentAsync(document, token, forcePrompt: needsPrompt, suppressPrompt: !needsPrompt)) return false;
            versions.Add((document, document.Model.VersionId));
        }
        token.ThrowIfCancellationRequested();
        return !IsDisposed && versions.All(p => !p.Document.IsClosed && p.Document.Model.VersionId == p.Version);
    }

    private void DocumentModel_Changed(object? sender, MonacoModelChangedEventArgs args)
    {
        var document = _documentItems.FirstOrDefault(d => ReferenceEquals(d.Model, sender));
        if (document is null || document == _activeDocument) return;
        // An inactive model can be changed by a cross-file rename or a host. Fold/selection
        // positions from before that edit are invalid, but its native undo history survives.
        document.WorkspaceText = document.Model.Text;
        document.CollapsedFolds.Clear();
        document.FoldRanges.Clear();
        document.ViewModel.SetHiddenAreas(Array.Empty<MonacoHiddenArea>());
        document.SecondarySelections.Clear();
        UpdateWorkspaceSnapshots();
    }

    private void UpdateWorkspaceSnapshots()
    {
        var open = WorkspaceDocuments.ToArray();
        var csharp = open.Where(d => d.LanguageId == "csharp").Select(d => new NativeLanguageDocument(d.Uri, d.Model.Text)).ToArray();
        var cpp = open.Where(d => d.LanguageId is "cpp" or "c").Select(d => new NativeLanguageDocument(d.Uri, d.Model.Text)).ToArray();
        var typescript = open.Where(d => d.LanguageId is "javascript" or "typescript" or "javascriptreact" or "typescriptreact")
            .Select(d => new NativeLanguageDocument(d.Uri, d.Model.Text)).ToArray();
        foreach (var editor in Workspace.Editors.Where(e => !e.IsDisposed))
        {
            Volatile.Write(ref editor._csharpDocumentSnapshots, csharp);
            Volatile.Write(ref editor._cppDocumentSnapshots, cpp);
            Volatile.Write(ref editor._typescriptDocumentSnapshots, typescript);
        }
    }

    private IReadOnlyList<NativeLanguageDocument> GetTypeScriptWorkspaceDocuments()
    {
        // Preserve explicitly configured modules (for example demo.types.ts), while
        // open unsaved tabs take precedence over those host-supplied snapshots.
        var configured = _languages.Packages.Installed
            .OfType<MonacoNative.Language.TypeScript.TypeScriptLanguagePackage>().FirstOrDefault()?.Options.Documents;
        var documents = configured is null ? new Dictionary<Uri, string>() : new Dictionary<Uri, string>(configured);
        foreach (var document in Volatile.Read(ref _typescriptDocumentSnapshots)) documents[document.Uri] = document.Text;
        return documents.Select(pair => new NativeLanguageDocument(pair.Key, pair.Value)).ToArray();
    }

    private void Document_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is not MonacoEditorDocument document || document.IsClosed) return;
        UpdateDocumentTab(document);
        if (args.PropertyName is null)
        {
            document.SetReadOnly(_hostIsReadOnly);
            if (document == _activeDocument)
            {
                _isReadOnly = document.IsReadOnly;
                _textInput?.SetReadOnly(_isReadOnly || !IsEditorTextFocused);
                CancelDocumentFeatures();
            }
        }
        if (args.PropertyName is null or nameof(MonacoEditorDocument.LanguageId))
        {
            document.WorkspaceText = document.Model.Text;
            UpdateWorkspaceSnapshots();
        }
    }

    private void UpdateDocumentTab(MonacoEditorDocument document)
    {
        if (!_documentTabs.TryGetValue(document, out var tab)) return;
        var label = document.DisplayName + (document.IsDirty ? " (unsaved)" : "") + (document.IsPinned ? " (pinned)" : "");
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(CodiconPresenter.Create(CompletionItemKind.File, new Microsoft.UI.Xaml.Media.SolidColorBrush(_foregroundColor)));
        header.Children.Add(new TextBlock
        {
            Text = document.DisplayName, FontSize = 13, Margin = new Thickness(0, -1.5, 0, 0),
            FontStyle = document.IsPreview ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (document.IsDirty) header.Children.Add(new TextBlock { Text = "●", FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
        if (document.IsPinned) header.Children.Add(new FontIcon
        {
            Glyph = "\uE718", FontSize = 10, Width = 11, Height = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -1, 0, 0),
        });
        if (document.IsReadOnly) header.Children.Add(new FontIcon { Glyph = "\uE72E", FontSize = 12, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center });
        tab.Header = header;
        tab.IsClosable = !document.IsPinned;
        AutomationProperties.SetName(tab, label);
        AutomationProperties.SetAutomationId(tab, "DocumentTab_" + document.Uri.AbsoluteUri);
        ToolTipService.SetToolTip(tab, document.Uri.IsFile ? document.Uri.LocalPath : document.Uri.AbsoluteUri);
    }

    private void SelectDocumentTab(MonacoEditorDocument document)
    {
        _synchronizingDocumentTabs = true;
        try { DocumentTabView.SelectedItem = _documentTabs[document]; }
        finally { _synchronizingDocumentTabs = false; }
    }

    private void UpdateDocumentTabVisibility()
    {
        if (DocumentTabView is null) return;
        DocumentTabView.Visibility = ShowTabs ? Visibility.Visible : Visibility.Collapsed;
        DocumentTabView.IsAddTabButtonVisible = IsMultiTabEnabled;
        DocumentTabView.CanDragTabs = IsMultiTabEnabled;
        DocumentTabView.CanReorderTabs = IsMultiTabEnabled;
        DocumentTabView.AllowDropTabs = IsMultiTabEnabled;
        QueueViewportResize();
    }

    private void DocumentTabView_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_documentsInitialized || _synchronizingDocumentTabs || IsDisposed || _tabDragId is not null) return;
        if (ReferenceEquals(DocumentTabView.SelectedItem, _transientEditorTab)) { ShowTransientEditor(); return; }
        if (DocumentTabView.SelectedItem is TabViewItem { Tag: MonacoEditorDocument document })
        {
            HideTransientEditor();
            var modifier = IsKeyDown(Windows.System.VirtualKey.Control) || IsKeyDown(Windows.System.VirtualKey.Shift);
            if (!modifier && !_selectedTabs.Contains(document)) SelectOnlyTab(document);
            ActivateDocument(document, focusEditor: !modifier);
        }
    }

    private async void DocumentTabView_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab == _transientEditorTab) { CloseTransientEditor(); return; }
        if (args.Tab.Tag is MonacoEditorDocument document)
            await RunDocumentUiOperationAsync(async () => { await CloseDocumentAsync(document); });
    }

    private async void DocumentTabView_AddTabButtonClick(TabView sender, object args) =>
        await RunDocumentUiOperationAsync(async () => { await OpenMemoryAsync(); });

    private async Task RunDocumentUiOperationAsync(Func<Task> operation)
    {
        try { await operation(); LastDocumentOperationError = null; }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (IsDisposed) return;
            LastDocumentOperationError = error;
            DocumentOperationFailed?.Invoke(this, new(error));
            if (XamlRoot is not null)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot, RequestedTheme = RequestedTheme,
                    Title = Local("monacoNative.documents.operationFailed"), Content = error.Message,
                    CloseButtonText = Local("monacoNative.documents.ok"),
                };
                try { await dialog.ShowAsync(); }
                catch (Exception dialogError) { System.Diagnostics.Debug.WriteLine(dialogError); }
            }
        }
    }

    private void RegisterDocumentCommands()
    {
        void Register(string id, string chord, Func<Task> action) => _commands.Register(new EditorCommandDefinition(
            id, id, "menu.category.editor", "editorTextFocus",
            () => new ValueTask(RunDocumentUiOperationAsync(action)), false, 0,
            new[] { new EditorKeybinding(chord, 500) }));
        Register("workbench.action.files.newUntitledFile", "Ctrl+N", async () => { await OpenMemoryAsync(); });
        Register("workbench.action.files.save", "Ctrl+S", () => SaveDocumentAsync());
        Register("workbench.action.closeActiveEditor", "Ctrl+W", async () => { await CloseDocumentAsync(); });
        Register("workbench.action.nextEditor", "Ctrl+Tab", () => { ActivateAdjacentDocument(1); return Task.CompletedTask; });
        Register("workbench.action.previousEditor", "Ctrl+Shift+Tab", () => { ActivateAdjacentDocument(-1); return Task.CompletedTask; });
        Register("workbench.action.reopenClosedEditor", "Ctrl+Shift+T", async () => { await ReopenClosedDocumentAsync(); });
        Register("workbench.action.files.saveAs", "Ctrl+Shift+S", () => SaveDocumentAsFromTabAsync(ActiveDocument));
        Register("workbench.action.closeEditorsInGroup", "Ctrl+K Ctrl+W", async () => { await CloseTabsAsync(EditorTabCloseScope.All); });
        Register("workbench.action.closeUnmodifiedEditors", "Ctrl+K Ctrl+U", async () => { await CloseTabsAsync(EditorTabCloseScope.Saved); });
        Register("workbench.action.keepEditor", "Ctrl+K Enter", () => { KeepDocumentOpen(ActiveDocument); return Task.CompletedTask; });
        Register("workbench.action.splitEditor", "Ctrl+\\", async () => { await SplitEditorAsync(EditorGroupDirection.Right); });
    }

    private void ActivateAdjacentDocument(int delta)
    {
        if (_documentItems.Count < 2 || !IsMultiTabEnabled) return;
        var next = (_documentItems.IndexOf(ActiveDocument) + delta + _documentItems.Count) % _documentItems.Count;
        ActivateDocument(_documentItems[next]);
    }

    private void DisposeDocuments()
    {
        CloseTransientEditorCore();
        DisposeEditorGroups();
        foreach (var document in _documentItems)
        {
            document.PropertyChanged -= Document_PropertyChanged;
            document.Model.Changed -= DocumentModel_Changed;
            document.Close();
        }
        _synchronizingDocumentTabs = true;
        DocumentTabView.TabItems.Clear();
        _documentTabs.Clear();
        _selectedTabs.Clear();
        _documentItems.Clear();
        _activeDocument = null;
        if (_workspace is not null) UpdateWorkspaceSnapshots();
        // Operations may still unwind after shutdown, so do not dispose their semaphore here.
    }
}
