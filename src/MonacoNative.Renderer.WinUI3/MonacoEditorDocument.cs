using System.ComponentModel;
using MonacoNative.Core;
using MonacoNative.Documents;

namespace MonacoNative.Renderer.WinUI3;

/// <summary>A document view. Split views share the buffer and undo history, not the view state.</summary>
public sealed class MonacoEditorDocument : INotifyPropertyChanged
{
    private readonly EditorDocumentBuffer _buffer;
    private bool _readOnly;
    private bool _isPinned;
    private bool _isPreview;

    internal MonacoEditorDocument(IEditorDocumentProvider provider, MonacoTextModel model,
        MonacoViewModel view, MonacoLanguageDocument languageDocument, string savedText)
        : this(new EditorDocumentBuffer(provider, model, savedText), view, languageDocument) { }

    private MonacoEditorDocument(EditorDocumentBuffer buffer, MonacoViewModel view, MonacoLanguageDocument languageDocument)
    {
        _buffer = buffer;
        ViewModel = view;
        LanguageDocument = languageDocument;
        buffer.Attach(this);
    }

    internal MonacoEditorDocument CreateLinkedView()
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        var view = new MonacoEditorDocument(_buffer, new MonacoViewModel(Model), new MonacoLanguageDocument(Model, Uri))
        {
            Anchor = Anchor, Active = Active, ScrollTop = ScrollTop, ScrollLeft = ScrollLeft,
            WorkspaceText = Model.Text, FoldRanges = new SortedDictionary<int, int>(FoldRanges),
            DiffLines = DiffLines, DiffText = DiffText,
        };
        view.SecondarySelections.AddRange(SecondarySelections);
        view.CollapsedFolds.UnionWith(CollapsedFolds);
        return view;
    }

    public IEditorDocumentProvider Provider => _buffer.Provider;
    public Uri Uri => LanguageDocument.Uri;
    public string DisplayName => Provider.DisplayName;
    public string LanguageId => Model.LanguageId;
    public MonacoTextModel Model => _buffer.Model;
    public MonacoViewModel ViewModel { get; }
    public bool IsDirty => _buffer.IsDirty;
    public bool IsReadOnly => _readOnly || Provider.IsReadOnly;
    public bool IsClosed { get; private set; }
    public bool IsPinned => _isPinned;
    public bool IsPreview => _isPreview;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal int ViewCount => _buffer.ViewCount;
    internal SemaphoreSlim SaveGate => _buffer.SaveGate;
    internal string SavedText => _buffer.SavedText;
    internal bool SharesBuffer(MonacoEditorDocument other) => ReferenceEquals(_buffer, other._buffer);
    internal MonacoLanguageDocument LanguageDocument { get; }
    internal MonacoPosition Anchor { get; set; } = new(1, 1);
    internal MonacoPosition Active { get; set; } = new(1, 1);
    internal List<(MonacoPosition Anchor, MonacoPosition Active)> SecondarySelections { get; } = new();
    internal SortedDictionary<int, int> FoldRanges { get; set; } = new();
    internal HashSet<int> CollapsedFolds { get; } = new();
    internal IReadOnlyList<MonacoDiffLineDecoration> DiffLines { get; set; } = Array.Empty<MonacoDiffLineDecoration>();
    internal IReadOnlyList<MonacoDiffTextDecoration> DiffText { get; set; } = Array.Empty<MonacoDiffTextDecoration>();
    internal double ScrollTop { get; set; }
    internal double ScrollLeft { get; set; }
    internal bool IsImplicit { get; set; }
    internal string WorkspaceText { get; set; } = string.Empty;

    internal void SetPinned(bool value)
    {
        if (_isPinned == value) return;
        _isPinned = value;
        if (value) SetPreview(false);
        Notify(nameof(IsPinned));
    }

    internal void SetPreview(bool value)
    {
        value &= !IsDirty && !IsPinned;
        if (_isPreview == value) return;
        _isPreview = value;
        Notify(nameof(IsPreview));
    }

    internal void RefreshDirtyState() => _buffer.RefreshDirtyState();
    internal void MarkSaved(string savedText) { IsImplicit = false; _buffer.MarkSaved(savedText); }
    internal void SetReadOnly(bool value) { _readOnly = value; Notify(nameof(IsReadOnly)); }
    internal void SetProvider(IEditorDocumentProvider provider) => _buffer.SetProvider(provider);
    internal void NotifyIdentityChanged() => Notify(null);

    internal void OnBufferChanged(string? propertyName)
    {
        if (propertyName is null)
        {
            LanguageDocument.Uri = Provider.Uri;
            _readOnly = false;
            IsImplicit = false;
        }
        if (IsDirty) SetPreview(false);
        Notify(propertyName);
    }

    private void Notify(string? name) => PropertyChanged?.Invoke(this, new(name));

    internal void Close()
    {
        if (IsClosed) return;
        IsClosed = true;
        ViewModel.Dispose();
        _buffer.Detach(this);
        Notify(nameof(IsClosed));
    }
}

internal sealed class EditorDocumentBuffer
{
    private readonly HashSet<MonacoEditorDocument> _views = new();
    private string _languageId;
    internal EditorDocumentBuffer(IEditorDocumentProvider provider, MonacoTextModel model, string savedText)
    {
        Provider = provider;
        Model = model;
        SavedText = savedText;
        _languageId = model.LanguageId;
        model.Changed += ModelChanged;
    }

    internal IEditorDocumentProvider Provider { get; private set; }
    internal MonacoTextModel Model { get; }
    internal string SavedText { get; private set; }
    internal bool IsDirty { get; private set; }
    internal int ViewCount => _views.Count;
    internal SemaphoreSlim SaveGate { get; } = new(1, 1);
    internal void Attach(MonacoEditorDocument view) => _views.Add(view);
    internal void Detach(MonacoEditorDocument view)
    {
        _views.Remove(view);
        if (_views.Count != 0) return;
        Model.Changed -= ModelChanged;
        Model.UndoSelectionProvider = null;
        Model.Dispose();
    }

    private void ModelChanged(object? sender, MonacoModelChangedEventArgs args)
    {
        RefreshDirtyState();
        if (_languageId == Model.LanguageId) return;
        _languageId = Model.LanguageId;
        Notify(nameof(MonacoEditorDocument.LanguageId));
    }

    internal void RefreshDirtyState()
    {
        var dirty = Model.Length != (nuint)SavedText.Length ||
            !string.Equals(Model.Text, SavedText, StringComparison.Ordinal);
        if (dirty == IsDirty) return;
        IsDirty = dirty;
        Notify(nameof(MonacoEditorDocument.IsDirty));
    }

    internal void MarkSaved(string text) { SavedText = text; RefreshDirtyState(); }
    internal void SetProvider(IEditorDocumentProvider provider) { Provider = provider; Notify(null); }
    private void Notify(string? name)
    {
        foreach (var view in _views.ToArray())
            if (!view.IsClosed) view.OnBufferChanged(name);
    }
}

public enum DocumentCloseAction { Prompt, Save, Discard, Cancel }

public sealed class EditorDocumentEventArgs : EventArgs
{
    public EditorDocumentEventArgs(MonacoEditorDocument document) => Document = document;
    public MonacoEditorDocument Document { get; }
}

public sealed class EditorDocumentClosingEventArgs : EventArgs
{
    public EditorDocumentClosingEventArgs(MonacoEditorDocument document) => Document = document;
    public MonacoEditorDocument Document { get; }
    public bool Cancel { get; set; }
    public DocumentCloseAction Action { get; set; } = DocumentCloseAction.Prompt;
}

public sealed class EditorDocumentOperationFailedEventArgs : EventArgs
{
    public EditorDocumentOperationFailedEventArgs(Exception exception) => Exception = exception;
    public Exception Exception { get; }
}
