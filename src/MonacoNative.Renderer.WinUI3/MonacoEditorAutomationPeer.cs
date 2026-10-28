using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Automation.Text;
using MonacoNative.Accessibility;
using Windows.Foundation;

namespace MonacoNative.Renderer.WinUI3;

/// <summary>UI Automation peer for the custom-rendered editor.</summary>
internal sealed class MonacoEditorAutomationPeer : FrameworkElementAutomationPeer, ITextProvider2, ITextEditProvider
{
    private readonly MonacoEditor _editor;

    public MonacoEditorAutomationPeer(MonacoEditor owner) : base(owner) => _editor = owner;

    protected override object? GetPatternCore(PatternInterface patternInterface) => patternInterface switch
    {
        PatternInterface.Text or PatternInterface.Text2 or PatternInterface.TextEdit => this,
        _ => base.GetPatternCore(patternInterface),
    };

    protected override string GetClassNameCore() => nameof(MonacoEditor);
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;
    protected override string GetLocalizedControlTypeCore() => _editor.GetLocalizedString("monacoNative.accessibility.editor.controlType");
    protected override string GetNameCore() => _editor.GetLocalizedString("monacoNative.accessibility.editor.name");
    protected override string GetHelpTextCore() => _editor.GetLocalizedString("monacoNative.accessibility.editor.help");
    protected override bool IsKeyboardFocusableCore() => true;
    protected override bool IsControlElementCore() => true;
    protected override bool IsContentElementCore() => true;

    public ITextRangeProvider DocumentRange => CreateRange(0, _editor.AccessibilityLength);
    public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.Multiple;

    public ITextRangeProvider[] GetSelection() =>
        _editor.AccessibilitySelectionOffsetRanges.Select(range => (ITextRangeProvider)CreateRange(range.Start, range.End)).ToArray();

    public ITextRangeProvider[] GetVisibleRanges() =>
        _editor.AccessibilityVisibleRanges.Select(range => (ITextRangeProvider)CreateRange(range.Start, range.End)).ToArray();

    public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement) => null!;

    public ITextRangeProvider RangeFromPoint(Point screenLocation)
    {
        var offset = _editor.AccessibilityOffsetFromScreenPoint(screenLocation);
        return CreateRange(offset, offset);
    }

    public ITextRangeProvider GetCaretRange(out bool isActive)
    {
        isActive = _editor.AccessibilityHasKeyboardFocus;
        var (_, end) = _editor.AccessibilitySelectionOffsets;
        return CreateRange(end, end);
    }

    public ITextRangeProvider RangeFromAnnotation(IRawElementProviderSimple annotationElement) => null!;

    public ITextRangeProvider GetActiveComposition()
    {
        var range = _editor.AccessibilityCompositionRange;
        return range.HasValue ? CreateRange(range.Value.Start, range.Value.End) : null!;
    }

    public ITextRangeProvider GetConversionTarget()
    {
        var range = _editor.AccessibilityConversionTargetRange;
        return range.HasValue ? CreateRange(range.Value.Start, range.Value.End) : null!;
    }

    internal MonacoTextRangeProvider CreateRange(int start, int end)
    {
        // A UIA client may retain a range across a tab switch. Never reinterpret its
        // offsets as a selection in the newly active document.
        var model = _editor.Model;
        string ReadDocument()
        {
            if (_editor.IsDisposed || !ReferenceEquals(_editor.Model, model))
                throw new InvalidOperationException("The text range belongs to a document that is no longer active.");
            return model.Text;
        }
        return new(this, _editor, new AccessibleTextRange(ReadDocument, model, start, end));
    }

    internal IRawElementProviderSimple RawProvider => ProviderFromPeer(this);

    internal void RaiseTextChanged()
    {
        if (AutomationPeer.ListenerExists(AutomationEvents.TextPatternOnTextChanged))
            RaiseAutomationEvent(AutomationEvents.TextPatternOnTextChanged);
        if (AutomationPeer.ListenerExists(AutomationEvents.TextEditTextChanged))
            RaiseAutomationEvent(AutomationEvents.TextEditTextChanged);
    }

    internal void RaiseSelectionChanged()
    {
        if (AutomationPeer.ListenerExists(AutomationEvents.TextPatternOnTextSelectionChanged))
            RaiseAutomationEvent(AutomationEvents.TextPatternOnTextSelectionChanged);
    }

    internal void RaiseLayoutInvalidated()
    {
        if (AutomationPeer.ListenerExists(AutomationEvents.LayoutInvalidated))
            RaiseAutomationEvent(AutomationEvents.LayoutInvalidated);
    }

    internal object? GetTextAttributeValue(int attributeId) => attributeId switch
    {
        (int)AutomationTextAttributesEnum.FontNameAttribute => "Consolas",
        (int)AutomationTextAttributesEnum.FontSizeAttribute => 14.0,
        (int)AutomationTextAttributesEnum.IsReadOnlyAttribute => _editor.IsReadOnly,
        (int)AutomationTextAttributesEnum.IsHiddenAttribute => false,
        (int)AutomationTextAttributesEnum.IsItalicAttribute => false,
        (int)AutomationTextAttributesEnum.CultureAttribute => GetCultureLcid(),
        _ => null,
    };

    private int GetCultureLcid()
    {
        try
        {
            var name = _editor.UiLanguage == "qps-ploc" ? "en-US" : _editor.UiLanguage;
            return CultureInfo.GetCultureInfo(name).LCID;
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.GetCultureInfo("en-US").LCID;
        }
    }
}

internal sealed class MonacoTextRangeProvider : ITextRangeProvider2
{
    private readonly MonacoEditorAutomationPeer _peer;
    private readonly MonacoEditor _editor;
    private readonly AccessibleTextRange _range;

    internal MonacoTextRangeProvider(MonacoEditorAutomationPeer peer, MonacoEditor editor, AccessibleTextRange range)
    {
        _peer = peer;
        _editor = editor;
        _range = range;
    }

    public void AddToSelection() => _editor.AccessibilityAddSelection(_range.Start, _range.End);
    public ITextRangeProvider Clone() => new MonacoTextRangeProvider(_peer, _editor, _range.Clone());

    public bool Compare(ITextRangeProvider textRangeProvider) =>
        textRangeProvider is MonacoTextRangeProvider other && _range.Compare(other._range);

    public int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider textRangeProvider, TextPatternRangeEndpoint targetEndpoint)
    {
        var other = RequireRange(textRangeProvider);
        return _range.CompareEndpoints(MapEndpoint(endpoint), other._range, MapEndpoint(targetEndpoint));
    }

    public void ExpandToEnclosingUnit(TextUnit unit) => _range.ExpandToEnclosingUnit(MapUnit(unit));

    public ITextRangeProvider FindAttribute(int attributeId, object value, bool backward)
    {
        _ = backward;
        var current = _peer.GetTextAttributeValue(attributeId);
        return current is not null && Equals(current, value) ? Clone() : null!;
    }

    public ITextRangeProvider FindText(string text, bool backward, bool ignoreCase)
    {
        var found = _range.FindText(text, backward, ignoreCase);
        return found is null ? null! : new MonacoTextRangeProvider(_peer, _editor, found);
    }

    public object GetAttributeValue(int attributeId) => _peer.GetTextAttributeValue(attributeId)!;

    public void GetBoundingRectangles(out double[] returnValue) =>
        returnValue = _editor.AccessibilityBoundingRectangles(_range.Start, _range.End);

    public IRawElementProviderSimple[] GetChildren() => Array.Empty<IRawElementProviderSimple>();
    public IRawElementProviderSimple GetEnclosingElement() => _peer.RawProvider;
    public string GetText(int maxLength) => _range.GetText(maxLength);
    public int Move(TextUnit unit, int count) => _range.Move(MapUnit(unit), count);

    public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, ITextRangeProvider textRangeProvider, TextPatternRangeEndpoint targetEndpoint)
    {
        var other = RequireRange(textRangeProvider);
        _range.MoveEndpointByRange(MapEndpoint(endpoint), other._range, MapEndpoint(targetEndpoint));
    }

    public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count) =>
        _range.MoveEndpointByUnit(MapEndpoint(endpoint), MapUnit(unit), count);

    public void RemoveFromSelection() => _editor.AccessibilityRemoveSelection(_range.Start, _range.End);

    public void ScrollIntoView(bool alignToTop) => _editor.AccessibilityScrollRangeIntoView(_range.Start, _range.End, alignToTop);
    public void Select() => _editor.AccessibilitySetSelection(_range.Start, _range.End);
    public void ShowContextMenu() => _editor.AccessibilityShowContextMenu(_range.End);

    private MonacoTextRangeProvider RequireRange(ITextRangeProvider range) =>
        range as MonacoTextRangeProvider ?? throw new ArgumentException("Range belongs to another text provider.", nameof(range));

    private static AccessibleTextEndpoint MapEndpoint(TextPatternRangeEndpoint endpoint) => endpoint switch
    {
        TextPatternRangeEndpoint.Start => AccessibleTextEndpoint.Start,
        TextPatternRangeEndpoint.End => AccessibleTextEndpoint.End,
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint)),
    };

    private static AccessibleTextUnit MapUnit(TextUnit unit) => unit switch
    {
        TextUnit.Character => AccessibleTextUnit.Character,
        TextUnit.Format => AccessibleTextUnit.Format,
        TextUnit.Word => AccessibleTextUnit.Word,
        TextUnit.Line => AccessibleTextUnit.Line,
        TextUnit.Paragraph => AccessibleTextUnit.Paragraph,
        TextUnit.Page => AccessibleTextUnit.Page,
        TextUnit.Document => AccessibleTextUnit.Document,
        _ => AccessibleTextUnit.Character,
    };
}
