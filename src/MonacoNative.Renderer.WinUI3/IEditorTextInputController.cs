using MonacoNative.Core;
using MonacoNative.Input;

namespace MonacoNative.Renderer.WinUI3;

/// <summary>
/// Platform text-input bridge consumed by the editor.  The editor owns the Monaco model,
/// selection, undo/redo semantics and rendering; an implementation only adapts the active
/// Windows text service to those editor-owned primitives.
/// </summary>
internal interface IEditorTextInputController : IDisposable
{
    event EventHandler? InputLanguageChanged;
    event EventHandler? CompositionStateChanged;

    ImeInputBackend Backend { get; }
    bool IsAvailable { get; }
    bool HasInputFocus { get; }
    ImeInputCapability Capability { get; }
    bool IsApplyingServerUpdate { get; }
    bool IsComposing { get; }
    ImeTextRange CompositionRange { get; }
    IReadOnlyList<ImeCompositionSegment> CompositionSegments { get; }
    ImeTextRange? ConversionTargetRange { get; }
    string InputLanguageTag { get; }
    bool SuppressCharacterReceived { get; }
    bool TreatAdapterFocusAsEditorFocus => false;

    bool Initialize();
    void SetReadOnly(bool value);
    void NotifyFocusEnter();
    void NotifyFocusLeave();
    void NotifySelectionChanged();
    void NotifyModelChanged(MonacoModelChangedEventArgs change);
    void NotifyLayoutChanged();

    // Native TSF applications must offer raw key messages to ITfKeystrokeMgr before
    // the application processes them. Other input adapters do not consume this route.
    bool TryHandleKeyDown(uint virtualKey, uint messageLParam) => false;
    bool TryHandleKeyUp(uint virtualKey, uint messageLParam) => false;
}
