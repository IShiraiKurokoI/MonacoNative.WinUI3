using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MonacoNative.Core;
using MonacoNative.Language;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace MonacoNative.Renderer.WinUI3;

public sealed class LanguageLocationRequestedEventArgs : EventArgs
{
    public LanguageLocationRequestedEventArgs(LanguageLocation location) => Location = location;
    public LanguageLocation Location { get; }
}

public sealed class WorkspaceTextEditRequestedEventArgs : EventArgs
{
    public WorkspaceTextEditRequestedEventArgs(WorkspaceTextEdit edit) => Edit = edit;
    public WorkspaceTextEdit Edit { get; }
}

public sealed partial class MonacoEditor
{
    private Border? _contributionPopup;
    private StackPanel? _contributionPopupContent;
    private Button? _lightBulbButton;
    private CancellationTokenSource? _contributionRefreshCts;
    private Task? _contributionRefreshTask;
    // Reuse one UI-thread timer instead of allocating and canceling Task.Delay for every
    // selection movement (TaskCanceledException otherwise fires on every drag update).
    private readonly DispatcherTimer _contributionRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(70) };
    private bool _contributionRefreshFull;
    private IReadOnlyList<CodeAction> _cachedCodeActions = Array.Empty<CodeAction>();
    private readonly List<(CodeLens Lens, Button Button)> _codeLensControls = new();
    private IReadOnlyList<MonacoRange> _findMatches = Array.Empty<MonacoRange>();
    private int _findMatchIndex = -1;
    private IReadOnlyList<EditorFindEngine.Hit> _regexFindHits = Array.Empty<EditorFindEngine.Hit>();
    private int _findSelectionStart;
    private int _findSelectionEnd;
    private bool _findSelectionSnapshotAvailable;
    private bool _findBulkEdit;
    public bool IsFindOpen => FindWidget.Visibility == Visibility.Visible;
    public bool IsReplaceOpen => IsFindOpen && ReplaceRow.Visibility == Visibility.Visible;
    private ulong _inlineCompletionDecorationId;
    private InlineCompletion? _activeInlineCompletion;
    private bool _inlineCompletionsEnabled = true;
    private bool _codeLensEnabled = true;
    private bool _stickyScrollEnabled = true;
    private bool _suggestOnTypeEnabled = true;
    private bool _suppressAutoSuggest;
    private CancellationTokenSource? _suggestCts;
    private Task? _suggestTask;
    private readonly DispatcherTimer _suggestTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };

    private Border? _parameterHintsPopup;
    private StackPanel? _parameterHintsContent;
    private CancellationTokenSource? _parameterHintsCts;
    private Task? _parameterHintsTask;
    private readonly DispatcherTimer _parameterHintsTimer = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private long _parameterHintsRequestSequence;
    private SignatureHelp? _activeParameterHelp;
    private MonacoPosition _parameterHintsAnchor;
    private bool _parameterHintsEnabled = true;
    public bool ParameterHintsEnabled
    {
        get => _parameterHintsEnabled;
        set { _parameterHintsEnabled = value; if (!value) HideParameterHints(); }
    }

    /// <summary>Show language completions while typing. Ctrl+Space remains available.</summary>
    public bool SuggestOnTypeEnabled
    {
        get => _suggestOnTypeEnabled;
        set
        {
            _suggestOnTypeEnabled = value;
            if (!value) CancelAutomaticSuggestions();
        }
    }

    public event EventHandler<LanguageLocationRequestedEventArgs>? LanguageLocationRequested;
    public event EventHandler<WorkspaceTextEditRequestedEventArgs>? WorkspaceTextEditRequested;

    public bool InlineCompletionsEnabled
    {
        get => _inlineCompletionsEnabled;
        set
        {
            if (_inlineCompletionsEnabled == value) return;
            _inlineCompletionsEnabled = value;
            if (!value) ClearInlineCompletion();
            else ScheduleContributionRefresh(full: false);
        }
    }

    public bool CodeLensEnabled
    {
        get => _codeLensEnabled;
        set
        {
            if (_codeLensEnabled == value) return;
            _codeLensEnabled = value;
            if (!value) ClearCodeLensControls();
            else ScheduleContributionRefresh(full: true);
        }
    }

    public bool StickyScrollEnabled
    {
        get => _stickyScrollEnabled;
        set
        {
            if (_stickyScrollEnabled == value) return;
            _stickyScrollEnabled = value;
            UpdateStickyScroll();
        }
    }

    private void InitializeContributions()
    {
        _contributionPopupContent = new StackPanel { Spacing = 2 };
        _contributionPopup = new Border
        {
            Background = Brush(246, 37, 37, 38),
            BorderBrush = Brush(255, 88, 88, 88),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8),
            MaxWidth = 540,
            MaxHeight = 340,
            Visibility = Visibility.Collapsed,
            Child = new ScrollViewer
            {
                MaxHeight = 320,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _contributionPopupContent,
            },
        };
        ContributionLayer.Children.Add(_contributionPopup);
        InitializeSuggestWidget();
        _parameterHintsContent = new StackPanel { Spacing = 2 };
        _parameterHintsPopup = new Border
        {
            Visibility = Visibility.Collapsed, CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1), Padding = new Thickness(6),
            MaxWidth = 540, MaxHeight = 240, IsHitTestVisible = false,
            Child = _parameterHintsContent,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_parameterHintsPopup, "Parameter hints");
        ContributionLayer.Children.Add(_parameterHintsPopup);
        ApplyParameterHintsPalette();
        _parameterHintsTimer.Tick += ParameterHintsTimer_Tick;

        _lightBulbButton = new Button
        {
            Content = "💡",
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Visibility = Visibility.Collapsed,
        };
        _lightBulbButton.Click += async (_, _) => await ShowCodeActionsAsync();
        ContributionLayer.Children.Add(_lightBulbButton);
        RefreshContributionLocalization();
        _suggestTimer.Tick += SuggestTimer_Tick;
    }

    private void RegisterContributionCommands()
    {
        static EditorKeybinding[] Keys(params EditorKeybinding[] values) => values;

        _commands.Register(new EditorCommandDefinition(
            "actions.find", "actions.find", "menu.category.edit", "editorTextFocus",
            () => { ShowFind(replace: false); return ValueTask.CompletedTask; }, true, 50,
            Keys(new EditorKeybinding("Ctrl+F", 300))));
        _commands.Register(new EditorCommandDefinition(
            "editor.action.startFindReplaceAction", "editor.action.startFindReplaceAction", "menu.category.edit", "editorTextFocus && !editorReadonly",
            () => { ShowFind(replace: true); return ValueTask.CompletedTask; }, true, 60,
            Keys(new EditorKeybinding("Ctrl+H", 300))));
        _commands.Register(new EditorCommandDefinition(
            "editor.action.triggerSuggest", "editor.action.triggerSuggest", "menu.category.editor", "editorTextFocus && !editorReadonly",
            () => new ValueTask(TriggerSuggestAsync()), true, 100,
            Keys(new EditorKeybinding("Ctrl+Space", 400))));
        _commands.Register(new EditorCommandDefinition(
            "editor.action.showHover", "editor.action.showHover", "menu.category.editor", "editorTextFocus",
            () => new ValueTask(ShowHoverAsync(_active)), true, 110,
            Keys(new EditorKeybinding("Ctrl+K Ctrl+I", 400))));
        _commands.Register(new EditorCommandDefinition(
            "editor.action.triggerParameterHints", "editor.action.triggerParameterHints", "menu.category.editor", "editorTextFocus",
            () => new ValueTask(TriggerParameterHintsAsync()), true, 120,
            Keys(new EditorKeybinding("Ctrl+Shift+Space", 400))));
        _commands.Register(new EditorCommandDefinition(
            "editor.action.rename", "editor.action.rename", "menu.category.editor", "editorTextFocus && !editorReadonly",
            () => new ValueTask(TriggerRenameAsync()), true, 130,
            Keys(new EditorKeybinding("F2", 400))));
        _commands.Register(new EditorCommandDefinition(
            "editor.action.referenceSearch.trigger", "editor.action.referenceSearch.trigger", "menu.category.editor", "editorTextFocus",
            () => new ValueTask(ShowReferencesAsync()), true, 140,
            Keys(new EditorKeybinding("Shift+F12", 400))));
        _commands.Register(new EditorCommandDefinition(
            "editor.action.revealDefinition", "editor.action.revealDefinition", "menu.category.editor", "editorTextFocus",
            () => new ValueTask(GoToDefinitionAsync()), true, 145,
            Keys(new EditorKeybinding("F12", 400))));
        _commands.Register(new EditorCommandDefinition(
            "editor.action.formatDocument", "editor.action.formatDocument", "menu.category.editor", "editorTextFocus && !editorReadonly",
            () => new ValueTask(FormatDocumentAsync()), true, 147,
            Keys(new EditorKeybinding("Shift+Alt+F", 400))));
        _commands.Register(new EditorCommandDefinition(
            "editor.action.formatSelection", "editor.action.formatSelection", "menu.category.editor", "editorTextFocus && hasSelection && !editorReadonly",
            () => new ValueTask(FormatSelectionAsync()), true, 148, Array.Empty<EditorKeybinding>()));
        _commands.Register(new EditorCommandDefinition(
            "editor.action.quickFix", "editor.action.quickFix", "menu.category.editor", "editorTextFocus && !editorReadonly",
            () => new ValueTask(ShowCodeActionsAsync()), true, 150,
            Keys(new EditorKeybinding("Ctrl+OemPeriod", 400))));
        _commands.Register(new EditorCommandDefinition(
            "monacoNative.action.showColorPicker", "monacoNative.action.showColorPicker", "menu.category.editor", "editorTextFocus && !editorReadonly",
            () => new ValueTask(ShowColorPickerAsync()), true, 160, Array.Empty<EditorKeybinding>()));
        _commands.Register(new EditorCommandDefinition(
            "editor.action.inlineSuggest.trigger", "editor.action.inlineSuggest.trigger", "menu.category.editor", "editorTextFocus && !editorReadonly",
            () => new ValueTask(RefreshInlineCompletionAsync(explicitTrigger: true)), false, 0, Array.Empty<EditorKeybinding>()));
        _commands.Register(new EditorCommandDefinition(
            "editor.action.inlineSuggest.commit", "editor.action.inlineSuggest.commit", "menu.category.editor", "editorTextFocus && inlineSuggestionVisible && !editorReadonly",
            () => new ValueTask(AcceptInlineCompletionAsync()), false, 0,
            Keys(new EditorKeybinding("Tab", 800))));
        _commands.Register(new EditorCommandDefinition(
            "monacoNative.action.toggleStickyScroll", "monacoNative.action.toggleStickyScroll", "menu.category.view", "editorTextFocus",
            () => { StickyScrollEnabled = !StickyScrollEnabled; return ValueTask.CompletedTask; }, true, 50, Array.Empty<EditorKeybinding>()));
        _commands.Register(new EditorCommandDefinition(
            "monacoNative.action.toggleCodeLens", "monacoNative.action.toggleCodeLens", "menu.category.view", "editorTextFocus",
            () => { CodeLensEnabled = !CodeLensEnabled; return ValueTask.CompletedTask; }, true, 60, Array.Empty<EditorKeybinding>()));
        _commands.Register(new EditorCommandDefinition(
            "monacoNative.hideContributionWidget", "monacoNative.hideContributionWidget", "menu.category.editor", "editorTextFocus && contributionPopupVisible",
            () => { HideContributionPopup(); return ValueTask.CompletedTask; }, false, 0,
            Keys(new EditorKeybinding("Escape", 1000))));
        _commands.Register(new EditorCommandDefinition(
            "monacoNative.hideParameterHints", "editor.action.triggerParameterHints", "menu.category.editor",
            "editorTextFocus && parameterHintsVisible && !suggestWidgetVisible",
            () => { HideParameterHints(); return ValueTask.CompletedTask; }, false, 0,
            Keys(new EditorKeybinding("Escape", 900))));
    }

    public void ShowFind(bool replace)
    {
        HideParameterHints();
        HideSuggestWidget();
        // Ctrl+F and Ctrl+H use the same widget; switching never discards the current query.
        var alreadyOpen = IsFindOpen;
        FindReplaceExpander.IsChecked = replace;
        ReplaceRow.Visibility = replace ? Visibility.Visible : Visibility.Collapsed;
        FindReplaceChevron.Glyph = replace ? "\uE70D" : "\uE76C";
        FindWidget.Visibility = Visibility.Visible;
        UpdateFindWidgetPlacement();
        RefreshContributionLocalization();
        if (!alreadyOpen)
            _findSelectionSnapshotAvailable = _anchor != _active;
        if (!alreadyOpen && _findSelectionSnapshotAvailable)
        {
            var (start, end) = OrderedSelectionOffsets();
            var selected = _model.GetTextRange(start, end - start);
            if (!selected.Contains('\r') && !selected.Contains('\n'))
                FindTextBox.Text = selected;
            _findSelectionStart = checked((int)start);
            _findSelectionEnd = checked((int)end);
        }
        RefreshFindMatches(selectNearest: true);
        FindTextBox.Focus(FocusState.Programmatic);
        FindTextBox.SelectAll();
    }

    public void HideFind()
    {
        FindWidget.Visibility = Visibility.Collapsed;
        _findSelectionSnapshotAvailable = false;
        FindInSelection.IsChecked = false;
        _regexFindHits = Array.Empty<EditorFindEngine.Hit>();
        _findMatches = Array.Empty<MonacoRange>();
        _findMatchIndex = -1;
        Render(RenderDirty.Selection);
    }

    private void FindReplaceExpander_Changed(object sender, RoutedEventArgs e)
    {
        if (ReplaceRow is null || FindReplaceExpander is null) return;
        var expanded = FindReplaceExpander.IsChecked == true;
        ReplaceRow.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        FindReplaceChevron.Glyph = expanded ? "\uE70D" : "\uE76C";
    }

    private void FindInSelection_Changed(object sender, RoutedEventArgs e)
    {
        if (FindTextBox is null || FindInSelection is null || !IsFindOpen) return;
        if (FindInSelection.IsChecked == true && !_findSelectionSnapshotAvailable)
        {
            var (start, end) = OrderedSelectionOffsets();
            if (start == end)
            {
                FindInSelection.IsChecked = false;
                FindStatusMessage.Text = Local("monacoNative.find.selectTextFirst");
                return;
            }
            _findSelectionStart = checked((int)start);
            _findSelectionEnd = checked((int)end);
            _findSelectionSnapshotAvailable = true;
        }
        RefreshFindMatches(selectNearest: true);
    }

    public Task TriggerSuggestAsync(CancellationToken cancellationToken = default)
    {
        CancelAutomaticSuggestions();
        return ShowSuggestionsAsync(new CompletionContext(CompletionTriggerKind.Invoke), automatic: false, cancellationToken);
    }

    private async Task ShowSuggestionsAsync(CompletionContext context, bool automatic, CancellationToken cancellationToken)
    {
        var request = ++_suggestRequestSequence;
        var caret = _active;
        var version = _model.VersionId;
        var uri = DocumentUri; var language = LanguageId;
        var result = await _languages.ProvideCompletionItemsAsync(
            _languageDocument, ToLanguagePosition(caret), context, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed || request != _suggestRequestSequence || version != _model.VersionId ||
            !caret.Equals(_active) || uri != DocumentUri || language != LanguageId) return;
        if (result.Suggestions.Count == 0)
        {
            HideSuggestWidget();
            return;
        }
        _completionListIncomplete = result.IsIncomplete;
        _completionListLine = caret.LineNumber;
        _completionContinuationColumn = caret.Column;
        _completionContinuationVersion = version;
        ShowSuggestWidget(result.Suggestions, caret, version);
    }

    private void ScheduleAutomaticSuggestions()
    {
        CancelAutomaticSuggestions();
        if (!_suggestOnTypeEnabled || _suppressAutoSuggest || _disposed || !_loaded || _isReadOnly ||
            !_languages.Languages.IsRegistered(_model.LanguageId) || _model.LanguageId == "plaintext") return;
        // ModelChanged fires before the caller updates the caret; inspect the final caret
        // when the timer fires rather than rejecting the first typed character here.
        _suggestTimer.Interval = TimeSpan.FromMilliseconds(100);
        _suggestTimer.Start();
    }

    private void CancelAutomaticSuggestions()
    {
        _suggestTimer.Stop();
        _suggestCts?.Cancel();
        _suggestCts?.Dispose();
        _suggestCts = null;
    }

    private void SuggestTimer_Tick(object? sender, object e)
    {
        _suggestTimer.Stop();
        if (!_suggestOnTypeEnabled || _disposed || !_loaded || _isReadOnly) return;
        var row = Math.Clamp(_active.LineNumber, 1, checked((int)_model.LineCount));
        var line = _model.GetLineContent(row);
        var offset = Math.Clamp(_active.Column - 1, 0, line.Length);
        if (offset <= 0) return;
        var last = line[offset - 1];
        var trigger = last.ToString();
        var isServerTrigger = _languages.IsCompletionTriggerCharacter(_languageDocument, trigger);
        // On '.' still request semantic completion from an attached server even when
        // it did not advertise the character as a dedicated trigger. Never fabricate
        // members from the offline keyword provider when no server is attached.
        var semanticMemberAccess = last == '.' && _lspBinding is not null;
        if (!isServerTrigger && !semanticMemberAccess && !char.IsLetterOrDigit(last) &&
            last is not ('_' or '$' or '<' or ':' or '/' or '"' or '-'))
            return;
        var context = isServerTrigger
            ? new CompletionContext(CompletionTriggerKind.TriggerCharacter, trigger)
            : new CompletionContext(_completionListIncomplete && _completionListLine == row
                ? CompletionTriggerKind.TriggerForIncompleteCompletions : CompletionTriggerKind.Invoke);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ShutdownToken);
        _suggestCts = cts;
        _suggestTask = _lifetime.Track(ShowAutomaticSuggestionsAsync(context, cts.Token));
    }

    private async Task ShowAutomaticSuggestionsAsync(CompletionContext context, CancellationToken cancellationToken)
    {
        try { await ShowSuggestionsAsync(context, automatic: true, cancellationToken); }
        catch (OperationCanceledException) { }
        catch { /* Optional language providers must not fail the editor's input loop. */ }
    }

    public async Task ShowHoverAsync(MonacoPosition position, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(position.LineNumber, position.Column);
        var hover = await _languages.ProvideHoverAsync(_languageDocument, ToLanguagePosition(normalized), cancellationToken);
        if (hover is null || hover.Contents.Count == 0)
        {
            HideContributionPopup();
            return;
        }

        ShowAnchoredPopup(normalized, "editor.action.showHover");
        foreach (var content in hover.Contents)
            AddPopupText(content.Value);
    }

    public Task TriggerParameterHintsAsync(CancellationToken cancellationToken = default)
    {
        CancelParameterHintsRequest();
        _parameterHintsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.ShutdownToken);
        return ShowParameterHintsAsync(new SignatureHelpContext(SignatureHelpTriggerKind.Invoke), _parameterHintsCts.Token);
    }

    private void CancelParameterHintsRequest()
    {
        ++_parameterHintsRequestSequence;
        _parameterHintsTimer.Stop();
        _parameterHintsCts?.Cancel();
        _parameterHintsCts?.Dispose();
        _parameterHintsCts = null;
    }

    private void HideParameterHints()
    {
        CancelParameterHintsRequest();
        _activeParameterHelp = null;
        if (_parameterHintsPopup?.Visibility != Visibility.Visible) return;
        _parameterHintsPopup.Visibility = Visibility.Collapsed;
        _parameterHintsContent?.Children.Clear();
        _commands.SetContext("parameterHintsVisible", false);
    }

    private void ScheduleAutomaticParameterHints(bool retriggerOnly = false)
    {
        CancelParameterHintsRequest();
        if (!_parameterHintsEnabled || _disposed || !_loaded || _isReadOnly || _textInput?.IsComposing == true)
        { HideParameterHints(); return; }
        if (retriggerOnly && _activeParameterHelp is null) return;
        _parameterHintsTimer.Start();
    }

    private void ParameterHintsTimer_Tick(object? sender, object e)
    {
        _parameterHintsTimer.Stop();
        if (!_parameterHintsEnabled || _disposed || !_loaded || _textInput?.IsComposing == true) return;
        var row = Math.Clamp(_active.LineNumber, 1, checked((int)_model.LineCount));
        var line = _model.GetLineContent(row);
        var column = Math.Clamp(_active.Column - 1, 0, line.Length);
        var character = column > 0 ? line[column - 1].ToString() : string.Empty;
        var trigger = _languages.IsSignatureHelpTriggerCharacter(_languageDocument, character);
        var retrigger = _activeParameterHelp is not null;
        if (!trigger && !retrigger) return;
        var kind = trigger || (retrigger && _languages.IsSignatureHelpRetriggerCharacter(_languageDocument, character))
            ? SignatureHelpTriggerKind.TriggerCharacter : SignatureHelpTriggerKind.ContentChange;
        _parameterHintsCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ShutdownToken);
        _parameterHintsTask = _lifetime.Track(ShowAutomaticParameterHintsAsync(new SignatureHelpContext(kind,
            kind == SignatureHelpTriggerKind.TriggerCharacter ? character : null, retrigger, _activeParameterHelp), _parameterHintsCts.Token));
    }

    private async Task ShowAutomaticParameterHintsAsync(SignatureHelpContext context, CancellationToken cancellationToken)
    {
        try { await ShowParameterHintsAsync(context, cancellationToken); }
        catch (OperationCanceledException) { }
        catch { if (!cancellationToken.IsCancellationRequested) HideParameterHints(); }
    }

    private async Task ShowParameterHintsAsync(SignatureHelpContext context, CancellationToken cancellationToken)
    {
        var request = ++_parameterHintsRequestSequence;
        var caret = _active;
        var version = _model.VersionId;
        var uri = DocumentUri;
        var language = LanguageId;
        var help = await _languages.ProvideSignatureHelpAsync(_languageDocument, ToLanguagePosition(caret), context, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed || !_loaded || request != _parameterHintsRequestSequence || version != _model.VersionId ||
            !caret.Equals(_active) || uri != DocumentUri || language != LanguageId || _textInput?.IsComposing == true) return;
        if (help is null || help.Signatures.Count == 0)
        { HideParameterHints(); return; }
        if (_parameterHintsPopup is null || _parameterHintsContent is null) return;
        _activeParameterHelp = help;
        _parameterHintsAnchor = caret;
        _parameterHintsContent.Children.Clear();
        var selected = Math.Clamp(help.ActiveSignature, 0, help.Signatures.Count - 1);
        var signature = help.Signatures[selected];
        void Text(string text, bool emphasized = false) => _parameterHintsContent.Children.Add(new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 500,
            Foreground = new SolidColorBrush(_suggestForeground),
            FontFamily = new FontFamily("Consolas"),
            FontWeight = emphasized ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Margin = new Thickness(4, 2, 4, 2),
        });
        Text((help.Signatures.Count > 1 ? $"{selected + 1}/{help.Signatures.Count}  " : string.Empty) + signature.Label, true);
        if (!string.IsNullOrWhiteSpace(signature.Documentation)) Text(signature.Documentation!);
        if (signature.Parameters is { Count: > 0 } parameters)
        {
            var index = signature.ActiveParameter ?? help.ActiveParameter;
            // Out-of-range is an intentional upstream sentinel: do not falsely
            // highlight the last parameter for an extra argument or middle rest.
            if (index >= 0 && index < parameters.Count)
            {
                Text($"{index + 1}/{parameters.Count}  {parameters[index].Label}", true);
                if (!string.IsNullOrWhiteSpace(parameters[index].Documentation)) Text(parameters[index].Documentation!);
            }
        }
        ApplyParameterHintsPalette();
        _parameterHintsPopup.Visibility = Visibility.Visible;
        _commands.SetContext("parameterHintsVisible", true);
        PositionParameterHints();
        // No Focus(), no HideSuggestWidget(): parameter hints and completion may coexist.
    }

    private void ApplyParameterHintsPalette()
    {
        if (_parameterHintsPopup is null) return;
        _parameterHintsPopup.Background = _suggestPopup?.Background ?? Brush(255, 37, 37, 38);
        _parameterHintsPopup.BorderBrush = _suggestPopup?.BorderBrush ?? Brush(255, 69, 69, 69);
        if (_parameterHintsContent is not null)
            foreach (var block in _parameterHintsContent.Children.OfType<TextBlock>())
                block.Foreground = new SolidColorBrush(_suggestForeground);
    }

    private void PositionParameterHints()
    {
        if (_parameterHintsPopup?.Visibility != Visibility.Visible) return;
        var width = Math.Max(120, Math.Min(540, ActualWidth - GutterWidth - 8));
        _parameterHintsPopup.MaxWidth = width;
        _parameterHintsPopup.Measure(new Size(width, 240));
        var height = Math.Min(240, _parameterHintsPopup.DesiredSize.Height);
        var point = GetViewportPoint(_parameterHintsAnchor, belowLine: false);
        var x = Math.Clamp(point.X, GutterWidth, Math.Max(GutterWidth, ActualWidth - width - 4));
        var y = point.Y - height - 4;
        if (y < 4)
        {
            y = point.Y + _lineHeight + 4;
            if (IsSuggestWidgetVisible && _suggestPopup is not null)
            {
                var suggestTop = Canvas.GetTop(_suggestPopup);
                var suggestHeight = _suggestPopup.ActualHeight > 0 ? _suggestPopup.ActualHeight : _suggestPopup.DesiredSize.Height;
                y = Math.Max(y, suggestTop + suggestHeight + 4);
            }
        }
        Canvas.SetLeft(_parameterHintsPopup, x);
        Canvas.SetTop(_parameterHintsPopup, Math.Clamp(y, 4, Math.Max(4, ActualHeight - height - 4)));
    }

    public async Task TriggerRenameAsync(CancellationToken cancellationToken = default)
    {
        if (_isReadOnly) return;
        var position = ToLanguagePosition(_active);
        var location = await _languages.ResolveRenameLocationAsync(_languageDocument, position, cancellationToken);
        var wordRange = location?.Range ?? ToLanguageRange(GetWordRangeAtCaret());
        if (!string.IsNullOrWhiteSpace(location?.RejectReason))
        {
            ShowAnchoredPopup(_active, "editor.action.rename");
            AddPopupText(location!.RejectReason!);
            return;
        }

        var range = wordRange.Normalize();
        var initial = location?.Text ?? _model.GetValueInRange(ToMonacoRange(range));
        ShowAnchoredPopup(_active, "editor.action.rename");
        var box = new TextBox { Text = initial, MinWidth = 240, PlaceholderText = Local("monacoNative.rename.placeholder") };
        _contributionPopupContent!.Children.Add(box);
        box.SelectAll();
        box.Focus(FocusState.Programmatic);
        box.KeyDown += async (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                var edit = await _languages.ProvideRenameEditsAsync(_languageDocument, position, box.Text, cancellationToken);
                if (edit is not null)
                    await ApplyWorkspaceEditAsync(edit, cancellationToken);
                HideContributionPopup();
                Focus(FocusState.Programmatic);
            }
            else if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                HideContributionPopup();
                Focus(FocusState.Programmatic);
            }
        };
    }

    public async Task ShowReferencesAsync(CancellationToken cancellationToken = default)
    {
        var references = await _languages.ProvideReferencesAsync(
            _languageDocument,
            ToLanguagePosition(_active),
            new ReferenceContext(IncludeDeclaration: true),
            cancellationToken);
        if (references.Count == 0)
        {
            HideContributionPopup();
            return;
        }

        ShowAnchoredPopup(_active, "editor.action.referenceSearch.trigger");
        AddPopupText(LocalFormat("monacoNative.references.count", references.Count), emphasized: true);
        foreach (var location in references.Take(100))
        {
            var captured = location;
            AddPopupButton(LocationLabel(location), () =>
            {
                NavigateToLocation(captured);
                return Task.CompletedTask;
            });
        }
    }

    public async Task ShowCodeActionsAsync(CancellationToken cancellationToken = default)
    {
        var actions = _cachedCodeActions.Count > 0
            ? _cachedCodeActions
            : await ProvideCodeActionsAsync(cancellationToken: cancellationToken);
        if (actions.Count == 0)
        {
            HideContributionPopup();
            return;
        }

        ShowAnchoredPopup(_active, "editor.action.quickFix");
        foreach (var action in actions)
        {
            var captured = action;
            var label = action.IsPreferred ? $"★ {action.Title}" : action.Title;
            if (!string.IsNullOrWhiteSpace(action.DisabledReason))
                label += $" — {action.DisabledReason}";
            AddPopupButton(label, async () =>
            {
                if (string.IsNullOrWhiteSpace(captured.DisabledReason))
                    await ApplyCodeActionAsync(captured, cancellationToken);
                HideContributionPopup();
            }, enabled: string.IsNullOrWhiteSpace(action.DisabledReason));
        }
    }

    public async Task ShowColorPickerAsync(CancellationToken cancellationToken = default)
    {
        if (_isReadOnly) return;
        var colors = await _languages.ProvideDocumentColorsAsync(_languageDocument, cancellationToken);
        var info = colors.FirstOrDefault(c => RangeContains(c.Range, ToLanguagePosition(_active)));
        if (info is null)
        {
            HideContributionPopup();
            return;
        }

        var clamped = info.Color.Clamp();
        var picker = new ColorPicker
        {
            Color = Color.FromArgb(ToByte(clamped.Alpha), ToByte(clamped.Red), ToByte(clamped.Green), ToByte(clamped.Blue)),
            IsAlphaEnabled = true,
        };
        ShowAnchoredPopup(_active, "monacoNative.action.showColorPicker");
        _contributionPopupContent!.Children.Add(picker);
        AddPopupButton(Local("monacoNative.action.applyColor"), async () =>
        {
            var color = picker.Color;
            var updated = info with
            {
                Color = new LanguageColor(color.R / 255.0, color.G / 255.0, color.B / 255.0, color.A / 255.0),
            };
            var presentations = await _languages.ProvideColorPresentationsAsync(_languageDocument, updated, cancellationToken);
            var presentation = presentations.FirstOrDefault();
            if (presentation is null) return;
            var edits = new List<LanguageTextEdit>();
            if (presentation.TextEdit is not null) edits.Add(presentation.TextEdit);
            else edits.Add(new LanguageTextEdit(info.Range, presentation.Label));
            if (presentation.AdditionalTextEdits is not null) edits.AddRange(presentation.AdditionalTextEdits);
            ApplyLanguageTextEdits(edits);
            HideContributionPopup();
        });
    }

    public async Task RefreshInlineCompletionAsync(bool explicitTrigger = false, CancellationToken cancellationToken = default)
    {
        if (!_inlineCompletionsEnabled || _isReadOnly)
        {
            ClearInlineCompletion();
            return;
        }

        var completions = await _languages.ProvideInlineCompletionsAsync(
            _languageDocument,
            ToLanguagePosition(_active),
            new InlineCompletionContext(explicitTrigger ? InlineCompletionTriggerKind.Explicit : InlineCompletionTriggerKind.Automatic),
            cancellationToken);
        var item = completions.Items.FirstOrDefault();
        ClearInlineCompletion(updateProjection: false);
        if (item is null || string.IsNullOrEmpty(item.InsertText)) return;

        _activeInlineCompletion = item;
        _inlineCompletionDecorationId = _view.AddInjectedText(
            _active,
            item.InsertText,
            MonacoInjectedTextAffinity.MoveAfterInsertion,
            20_000);
        _commands.SetContext("inlineSuggestionVisible", _inlineCompletionDecorationId != 0);
        if (_inlineCompletionDecorationId != 0)
            AfterProjectionChanged(false);
    }

    public async Task AcceptInlineCompletionAsync(CancellationToken cancellationToken = default)
    {
        var item = _activeInlineCompletion;
        if (item is null) return;
        var range = item.Range is null
            ? new MonacoRange(_active.LineNumber, _active.Column, _active.LineNumber, _active.Column)
            : ToMonacoRange(item.Range.Value.Normalize());
        ClearInlineCompletion(updateProjection: false);
        ReplaceRangeAndMoveCaret(range, item.InsertText);
        if (item.Command is not null)
            await item.Command(cancellationToken);
    }

    public async Task RefreshCodeLensAsync(CancellationToken cancellationToken = default)
    {
        ClearCodeLensControls();
        if (!_codeLensEnabled) return;
        var lenses = await _languages.ProvideCodeLensesAsync(_languageDocument, cancellationToken);
        foreach (var lens in lenses.Take(200))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (lens.Command is null) continue;
            var captured = lens;
            var button = new Button
            {
                Content = lens.Command.Title,
                Padding = new Thickness(4, 0, 4, 0),
                MinHeight = 18,
                FontSize = 11,
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };
            button.Click += async (_, _) =>
            {
                if (captured.Command?.Execute is not null)
                    await captured.Command.Execute(CancellationToken.None);
            };
            ContributionLayer.Children.Add(button);
            _codeLensControls.Add((lens, button));
        }
        PositionCodeLensControls();
    }

    public async Task ApplyWorkspaceEditAsync(WorkspaceEdit edit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var local = edit.Edits.Where(e => Equals(e.Uri, DocumentUri)).ToArray();
        var external = edit.Edits.Where(e => !Equals(e.Uri, DocumentUri)).ToArray();
        if (local.Length > 0)
        {
            ApplyLanguageTextEdits(local.Select(e => new LanguageTextEdit(e.Range, e.Text)).ToArray());
        }
        foreach (var item in external)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WorkspaceTextEditRequested?.Invoke(this, new WorkspaceTextEditRequestedEventArgs(item));
        }
        await Task.CompletedTask;
    }

    /// <summary>Choose insertion (default) or full replacement for LSP InsertReplaceEdit.</summary>
    public bool SuggestReplaceMode { get; set; }

    private async Task ApplyCompletionItemAsync(CompletionItem item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_isReadOnly || _disposed) return;
        var version = _model.VersionId;
        var uri = DocumentUri; var language = LanguageId;
        var caret = _active;
        if (item.Resolve is not null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try { item = await item.Resolve(timeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { if (item.RequireResolvedEdit) return; }
            // A delayed resolver must never apply edits to a newer document/caret.
            if (_disposed || _isReadOnly || version != _model.VersionId || caret != _active || uri != DocumentUri || language != LanguageId) return;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (item.RequireResolvedEdit) return;
        var selectedRange = SuggestReplaceMode ? item.ReplaceRange ?? item.Range : item.InsertRange ?? item.Range;
        var range = selectedRange is null ? GetWordRangeAtCaret() : ToMonacoRange(selectedRange.Value.Normalize());
        var start = checked((int)_model.GetOffsetAt(new MonacoPosition(range.StartLineNumber, range.StartColumn)));
        var end = checked((int)_model.GetOffsetAt(new MonacoPosition(range.EndLineNumber, range.EndColumn)));
        var raw = AdjustCompletionIndentation(item.SnippetText ?? item.InsertText ?? item.Label, range, item.InsertTextMode);
        var expansion = item.SnippetText is null ? null : SnippetParser.Parse(raw, SnippetVariables());
        var text = expansion?.Text ?? raw;
        var edits = new List<(int Start, int End, string Text, bool Main)> { (start, end, text, true) };
        foreach (var edit in item.AdditionalTextEdits ?? Array.Empty<LanguageTextEdit>())
        {
            var r = edit.Range.Normalize();
            edits.Add((checked((int)_model.GetOffsetAt(new MonacoPosition(r.StartLineNumber, r.StartColumn))),
                checked((int)_model.GetOffsetAt(new MonacoPosition(r.EndLineNumber, r.EndColumn))), edit.Text, false));
        }
        var ordered = edits.OrderBy(e => e.Start).ThenBy(e => e.End).ToArray();
        for (var i = 1; i < ordered.Length; i++)
            if (ordered[i].Start < ordered[i - 1].End || ordered[i].Start == ordered[i - 1].Start)
                throw new InvalidOperationException("Completion edits overlap; no changes were applied.");
        var insertionStart = start + edits.Where(e => !e.Main && e.End <= start).Sum(e => e.Text.Length - (e.End - e.Start));
        _suppressAutoSuggest = true;
        try
        {
            if (expansion is not null || edits.Count > 1) CancelSnippetSession();
            using var transaction = _model.BeginUndoTransaction();
            try
            {
                foreach (var edit in ordered.Reverse()) _model.Replace((nuint)edit.Start, (nuint)edit.End, edit.Text);
                _anchor = _active = _model.GetPositionAt((nuint)(insertionStart + text.Length));
                FlushSnippetMirrors();
                if (expansion is not null) StartSnippetSession(expansion, insertionStart);
                else if (item.CaretOffset is { } cursor)
                    _anchor = _active = _model.GetPositionAt((nuint)(insertionStart + Math.Clamp(cursor, 0, text.Length)));
                transaction.Complete();
            }
            catch { transaction.Rollback(); CancelSnippetSession(); throw; }
            AfterProjectionChanged(true);
            OnSelectionChanged();
            HideContributionPopup();
            if (item.CommandId == "editor.action.triggerSuggest") await TriggerSuggestAsync(cancellationToken);
            else if (item.CommandId == "editor.action.triggerParameterHints") await TriggerParameterHintsAsync(cancellationToken);
            else if (item.Command is not null) await item.Command(cancellationToken);
            Focus(FocusState.Programmatic);
        }
        finally { _suppressAutoSuggest = false; }
    }

    private void ReplaceRangeAndMoveCaret(MonacoRange range, string text, int? caretOffset = null)
    {
        if (_isReadOnly) return;
        var start = _model.GetOffsetAt(Normalize(range.StartLineNumber, range.StartColumn));
        var end = _model.GetOffsetAt(Normalize(range.EndLineNumber, range.EndColumn));
        if (end < start) (start, end) = (end, start);
        _model.Replace(start, end, text ?? string.Empty);
        var target = start + (nuint)Math.Clamp(caretOffset ?? (text?.Length ?? 0), 0, text?.Length ?? 0);
        _anchor = _active = _model.GetPositionAt(target <= _model.Length ? target : _model.Length);
        AfterProjectionChanged(true);
        OnSelectionChanged();
    }

    private void ApplyLanguageTextEdits(IReadOnlyList<LanguageTextEdit> edits)
    {
        if (_isReadOnly || edits.Count == 0) return;
        var normalized = edits.Select(edit =>
        {
            var range = edit.Range.Normalize();
            var start = _model.GetOffsetAt(Normalize(range.StartLineNumber, range.StartColumn));
            var end = _model.GetOffsetAt(Normalize(range.EndLineNumber, range.EndColumn));
            return (Start: Math.Min(start, end), End: Math.Max(start, end), Text: edit.Text ?? string.Empty);
        }).OrderByDescending(e => e.Start).ThenByDescending(e => e.End).ToArray();

        using var transaction = _model.BeginUndoTransaction();
        try
        {
            foreach (var edit in normalized)
                _model.Replace(edit.Start, edit.End, edit.Text);
            transaction.Complete();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
        AfterProjectionChanged(true);
    }

    private MonacoRange GetWordRangeAtCaret()
    {
        var word = _model.GetWordRangeAt(_active);
        if (word is null && _active.Column > 1)
            word = _model.GetWordRangeAt(new MonacoPosition(_active.LineNumber, _active.Column - 1));
        return word ?? new MonacoRange(_active.LineNumber, _active.Column, _active.LineNumber, _active.Column);
    }

    private void ShowAnchoredPopup(MonacoPosition anchor, string titleKey)
    {
        HideParameterHints();
        HideSuggestWidget();
        if (_contributionPopup is null || _contributionPopupContent is null) return;
        _contributionPopupContent.Children.Clear();
        AddPopupText(Local(titleKey), emphasized: true);
        _contributionPopup.Visibility = Visibility.Visible;
        var point = GetViewportPoint(anchor, belowLine: true);
        Canvas.SetLeft(_contributionPopup, Math.Clamp(point.X, GutterWidth, Math.Max(GutterWidth, ActualWidth - 560)));
        Canvas.SetTop(_contributionPopup, Math.Clamp(point.Y, 4, Math.Max(4, ActualHeight - 350)));
        _commands.SetContext("contributionPopupVisible", true);
    }

    private void HideContributionPopup()
    {
        if (_contributionPopup is not null)
            _contributionPopup.Visibility = Visibility.Collapsed;
        _contributionPopupContent?.Children.Clear();
        _commands.SetContext("contributionPopupVisible", false);
    }

    private void AddPopupText(string text, bool emphasized = false)
    {
        if (_contributionPopupContent is null) return;
        _contributionPopupContent.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = Brush(255, 224, 224, 224),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 500,
            FontWeight = emphasized ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Margin = new Thickness(4, 2, 4, 2),
        });
    }

    private void AddPopupButton(string label, Func<Task> action, bool enabled = true)
    {
        if (_contributionPopupContent is null) return;
        var button = new Button
        {
            Content = label,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            IsEnabled = enabled,
            Padding = new Thickness(8, 5, 8, 5),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        button.Click += async (_, _) => await action();
        _contributionPopupContent.Children.Add(button);
    }

    private Point GetViewportPoint(MonacoPosition position, bool belowLine)
    {
        var normalized = Normalize(position.LineNumber, position.Column);
        var viewPosition = _view.ModelToView(normalized);
        var line = _view.GetViewLine(viewPosition.LineNumber);
        var x = 0f;
        if (_layoutCache is not null)
        {
            var layout = _layoutCache.GetOrCreate(line.ViewLineNumber, line.Text, (float)_lineHeight);
            x = CaretX(layout, line.Text, Math.Clamp(viewPosition.Column - 1, 0, line.Text.Length));
        }
        return new Point(
            GutterWidth + LeftPadding + x - ScrollHost.HorizontalOffset,
            VerticalPadding + line.VerticalOffset - ScrollHost.VerticalOffset + (belowLine ? _lineHeight : 0));
    }

    private void NavigateToLocation(LanguageLocation location)
    {
        HideContributionPopup();
        if (Equals(location.Uri, DocumentUri))
        {
            Selection = ToMonacoRange(location.Range.Normalize());
            Focus(FocusState.Programmatic);
            return;
        }
        LanguageLocationRequested?.Invoke(this, new LanguageLocationRequestedEventArgs(location));
    }

    private string LocationLabel(LanguageLocation location)
    {
        var range = location.Range.Normalize();
        var prefix = Equals(location.Uri, DocumentUri)
            ? Local("monacoNative.references.currentDocument")
            : location.Uri.ToString();
        var preview = string.Empty;
        if (Equals(location.Uri, DocumentUri) && range.StartLineNumber >= 1 && range.StartLineNumber <= (int)_model.LineCount)
            preview = _model.GetLineContent(range.StartLineNumber).Trim();
        return $"{prefix}:{range.StartLineNumber}:{range.StartColumn}  {preview}".TrimEnd();
    }

    private static string CompletionLabel(CompletionItem item) =>
        string.IsNullOrWhiteSpace(item.Detail) ? item.Label : $"{item.Label}    {item.Detail}";

    public Task RefreshContributionsAsync(CancellationToken cancellationToken = default) =>
        RefreshContributionStateAsync(full: true, cancellationToken);

    private void ScheduleContributionRefresh(bool full)
    {
        if (_disposed || !_loaded) return;
        _contributionRefreshFull |= full;
        _contributionRefreshTimer.Stop();
        _contributionRefreshTimer.Interval = TimeSpan.FromMilliseconds(_contributionRefreshFull ? 180 : 70);
        _contributionRefreshTimer.Start();
    }

    private void ContributionRefreshTimer_Tick(object? sender, object e)
    {
        _contributionRefreshTimer.Stop();
        if (_disposed || !_loaded) return;
        var full = _contributionRefreshFull;
        _contributionRefreshFull = false;
        // Cancellation applies only to *running provider operations*, not to queued delays.
        _contributionRefreshCts?.Cancel();
        _contributionRefreshCts?.Dispose();
        var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ShutdownToken);
        _contributionRefreshCts = source;
        _contributionRefreshTask = _lifetime.Track(RefreshContributionStateAsync(full, source.Token));
    }

    private async Task RefreshContributionStateAsync(bool full, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RefreshLightBulbAsync(cancellationToken);
            if (_inlineCompletionsEnabled)
                await RefreshInlineCompletionAsync(explicitTrigger: false, cancellationToken);
            if (full && _codeLensEnabled)
                await RefreshCodeLensAsync(cancellationToken);
            UpdateStickyScroll();
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Contribution providers are extension points. A failing presenter/provider must
            // not take down the editor surface; explicit API calls still propagate errors.
        }
    }

    private async Task RefreshLightBulbAsync(CancellationToken cancellationToken)
    {
        _cachedCodeActions = await ProvideCodeActionsAsync(cancellationToken: cancellationToken);
        if (_lightBulbButton is null) return;
        if (_cachedCodeActions.Count == 0 || _isReadOnly)
        {
            _lightBulbButton.Visibility = Visibility.Collapsed;
            return;
        }
        var point = GetViewportPoint(_active, belowLine: false);
        Canvas.SetLeft(_lightBulbButton, Math.Max(2, GutterWidth - 34));
        Canvas.SetTop(_lightBulbButton, Math.Clamp(point.Y, 0, Math.Max(0, ActualHeight - 30)));
        _lightBulbButton.Visibility = Visibility.Visible;
        ToolTipService.SetToolTip(_lightBulbButton, Local("editor.action.quickFix"));
    }

    private void ClearInlineCompletion(bool updateProjection = true)
    {
        var removed = false;
        if (_inlineCompletionDecorationId != 0)
        {
            _view.RemoveInjectedText(_inlineCompletionDecorationId);
            _inlineCompletionDecorationId = 0;
            removed = true;
        }
        _activeInlineCompletion = null;
        _commands.SetContext("inlineSuggestionVisible", false);
        if (removed && updateProjection && _loaded && !_disposed)
            AfterProjectionChanged(false);
    }

    private void ClearCodeLensControls()
    {
        foreach (var (_, button) in _codeLensControls)
            ContributionLayer.Children.Remove(button);
        _codeLensControls.Clear();
    }

    private void PositionCodeLensControls()
    {
        foreach (var (lens, button) in _codeLensControls)
        {
            var line = Math.Clamp(lens.Range.StartLineNumber, 1, checked((int)_model.LineCount));
            var point = GetViewportPoint(new MonacoPosition(line, 1), belowLine: false);
            var y = point.Y - 18;
            button.Visibility = y < -24 || y > ActualHeight ? Visibility.Collapsed : Visibility.Visible;
            Canvas.SetLeft(button, GutterWidth + LeftPadding);
            Canvas.SetTop(button, Math.Max(0, y));
        }
    }

    private void UpdateStickyScroll()
    {
        using var methodSample = _methodProfiler.Measure("UpdateStickyScroll");
        if (!_stickyScrollEnabled || !_loaded || _view.ViewLineCount <= 0)
        {
            StickyScrollHost.Visibility = Visibility.Collapsed;
            StickyScrollPanel.Children.Clear();
            return;
        }

        var firstViewLine = _view.GetViewLineAtVerticalOffset(Math.Max(0, ScrollHost.VerticalOffset - VerticalPadding));
        var topModelLine = _view.GetViewLine(Math.Clamp(firstViewLine, 1, _view.ViewLineCount)).ModelLineNumber;
        var scopes = ComputeStickyScopes(topModelLine).TakeLast(4).ToArray();
        StickyScrollPanel.Children.Clear();
        foreach (var (_, text) in scopes)
        {
            StickyScrollPanel.Children.Add(new Border
            {
                Background = Brush(242, 30, 30, 30),
                Padding = new Thickness(8, 2, 8, 2),
                Child = new TextBlock
                {
                    Text = text,
                    Foreground = Brush(255, 212, 212, 212),
                    FontFamily = new FontFamily("Cascadia Mono"),
                    FontSize = 13,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            });
        }
        StickyScrollHost.Visibility = scopes.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private IReadOnlyList<(int Line, string Text)> ComputeStickyScopes(int topModelLine)
    {
        var stack = new List<(int Line, string Text, int Indent)>();
        for (var lineNumber = 1; lineNumber < topModelLine; ++lineNumber)
        {
            var raw = _model.GetLineContent(lineNumber);
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;
            var indent = Indentation(raw);
            if (trimmed.StartsWith("}", StringComparison.Ordinal))
            {
                while (stack.Count > 0 && stack[^1].Indent >= indent)
                    stack.RemoveAt(stack.Count - 1);
            }
            while (stack.Count > 0 && stack[^1].Indent > indent)
                stack.RemoveAt(stack.Count - 1);

            var opensBrace = trimmed.EndsWith("{", StringComparison.Ordinal);
            var opensIndentedBlock = trimmed.EndsWith(":", StringComparison.Ordinal);
            if (!opensBrace && !opensIndentedBlock) continue;
            while (stack.Count > 0 && stack[^1].Indent >= indent)
                stack.RemoveAt(stack.Count - 1);
            stack.Add((lineNumber, trimmed, indent));
        }
        return stack.Select(s => (s.Line, s.Text)).ToArray();
    }

    private static int Indentation(string text)
    {
        var value = 0;
        foreach (var ch in text)
        {
            if (ch == ' ') value++;
            else if (ch == '\t') value += TabSize;
            else break;
        }
        return value;
    }

    private void OnContributionsSelectionChanged()
    {
        CancelAutomaticSuggestions();
        ScheduleAutomaticParameterHints(retriggerOnly: true);
        var continuation = _completionListIncomplete && _completionContinuationVersion == _model.VersionId &&
            _active.LineNumber == _completionListLine && _active.Column == _completionContinuationColumn;
        HideSuggestWidget(preserveCompletionSession: continuation);
        ScheduleContributionRefresh(full: false);
    }

    private void OnContributionsModelChanged()
    {
        using var methodSample = _methodProfiler.Measure("OnContributionsModelChanged");
        ClearInlineCompletion(updateProjection: false);
        HideSuggestWidget(preserveCompletionSession: true);
        ScheduleContributionRefresh(full: true);
        // ModelChanged fires before the edit's final caret update. Queue suggestions after
        // the selection transaction, otherwise OnSelectionChanged immediately cancels them.
        var editVersion = _model.VersionId;
        if (!_suppressAutoSuggest && _textInput?.IsComposing != true)
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                if (!_disposed && _model.VersionId == editVersion)
                {
                    ScheduleAutomaticSuggestions();
                    ScheduleAutomaticParameterHints();
                }
            });
        if (!_findBulkEdit && FindWidget.Visibility == Visibility.Visible)
            RefreshFindMatches(selectNearest: false);
    }

    private void OnContributionsViewportChanged()
    {
        PositionSuggestWidget();
        PositionParameterHints();
        PositionCodeLensControls();
        UpdateStickyScroll();
        if (_lightBulbButton?.Visibility == Visibility.Visible)
        {
            var point = GetViewportPoint(_active, belowLine: false);
            Canvas.SetTop(_lightBulbButton, Math.Clamp(point.Y, 0, Math.Max(0, ActualHeight - 30)));
        }
    }

    private void RefreshFindMatches(bool selectNearest)
    {
        using var methodSample = _methodProfiler.Measure("RefreshFindMatches");
        if (FindTextBox is null || FindMatchCase is null || FindRegex is null) return;
        var query = FindTextBox.Text ?? string.Empty;
        var inSelection = FindInSelection.IsChecked == true;
        var rangeStart = inSelection ? _findSelectionStart : 0;
        var rangeEnd = inSelection ? _findSelectionEnd : int.MaxValue;
        _regexFindHits = Array.Empty<EditorFindEngine.Hit>();
        string? error = null;
        if (string.IsNullOrEmpty(query))
            _findMatches = Array.Empty<MonacoRange>();
        else if (FindRegex.IsChecked == true)
        {
            var result = EditorFindEngine.SearchRegex(_model.Text, query,
                FindMatchCase.IsChecked == true, FindWholeWord.IsChecked == true,
                rangeStart, rangeEnd);
            _regexFindHits = result.Hits;
            error = result.Error;
            _findMatches = _regexFindHits.Select(hit =>
            {
                var start = _model.GetPositionAt((nuint)hit.Start);
                var end = _model.GetPositionAt((nuint)(hit.Start + hit.Length));
                return new MonacoRange(start.LineNumber, start.Column, end.LineNumber, end.Column);
            }).ToArray();
        }
        else
        {
            var native = _model.FindMatches(query,
                FindMatchCase.IsChecked == true, FindWholeWord.IsChecked == true, 10_000);
            _findMatches = inSelection
                ? native.Where(match =>
                {
                    var first = checked((int)_model.GetOffsetAt(new MonacoPosition(match.StartLineNumber, match.StartColumn)));
                    var last = checked((int)_model.GetOffsetAt(new MonacoPosition(match.EndLineNumber, match.EndColumn)));
                    return first >= rangeStart && last <= rangeEnd;
                }).ToArray()
                : native;
        }
        if (_findMatches.Count == 0)
            _findMatchIndex = -1;
        else if (selectNearest)
        {
            var activeOffset = _model.GetOffsetAt(_active);
            var next = 0;
            for (var i = 0; i < _findMatches.Count; ++i)
            {
                var offset = _model.GetOffsetAt(new MonacoPosition(_findMatches[i].StartLineNumber, _findMatches[i].StartColumn));
                if (offset >= activeOffset) { next = i; break; }
            }
            _findMatchIndex = next;
            SelectFindMatch(_findMatchIndex);
        }
        else
            _findMatchIndex = Math.Clamp(_findMatchIndex, 0, _findMatches.Count - 1);
        FindStatusMessage.Text = error is null ? string.Empty : Local("monacoNative.find.invalidRegex");
        UpdateFindWidgetState();
        Render(RenderDirty.Selection);
    }

    private void UpdateFindWidgetState()
    {
        FindMatchCount.Text = string.IsNullOrEmpty(FindTextBox.Text) ? "—" :
            _findMatches.Count == 0 ? Local("monacoNative.find.noResults") :
            LocalFormat("monacoNative.find.matchCount", _findMatchIndex + 1, _findMatches.Count);
        FindNextButton.IsEnabled = FindPreviousButton.IsEnabled = _findMatches.Count != 0;
        ReplaceOneButton.IsEnabled = !_isReadOnly && _findMatches.Count != 0;
        ReplaceAllButton.IsEnabled = !_isReadOnly && _findMatches.Count != 0;
    }

    private void SelectFindMatch(int index)
    {
        if (_findMatches.Count == 0) return;
        _findMatchIndex = (index % _findMatches.Count + _findMatches.Count) % _findMatches.Count;
        Selection = _findMatches[_findMatchIndex];
        UpdateFindWidgetState();
    }

    private void FindTextBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshFindMatches(selectNearest: true);
    private void FindOption_Changed(object sender, RoutedEventArgs e) => RefreshFindMatches(selectNearest: true);
    private void FindPrevious_Click(object sender, RoutedEventArgs e) => SelectFindMatch(_findMatchIndex - 1);
    private void FindNext_Click(object sender, RoutedEventArgs e) => SelectFindMatch(_findMatchIndex + 1);
    private void FindClose_Click(object sender, RoutedEventArgs e) { HideFind(); Focus(FocusState.Programmatic); }

    private void FindTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            SelectFindMatch(IsKeyDown(VirtualKey.Shift) ? _findMatchIndex - 1 : _findMatchIndex + 1);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            HideFind();
            Focus(FocusState.Programmatic);
            e.Handled = true;
        }
    }

    private void ReplaceTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            HideFind();
            Focus(FocusState.Programmatic);
            e.Handled = true;
        }
    }

    private string ReplacementForMatch(int index, string replacement)
    {
        var range = _findMatches[index];
        var start = _model.GetOffsetAt(new MonacoPosition(range.StartLineNumber, range.StartColumn));
        var end = _model.GetOffsetAt(new MonacoPosition(range.EndLineNumber, range.EndColumn));
        var original = _model.GetTextRange(start, end - start);
        var match = FindRegex.IsChecked == true && index < _regexFindHits.Count
            ? _regexFindHits[index].RegexMatch : null;
        return EditorFindEngine.ExpandReplacement(original, replacement, match, ReplacePreserveCase.IsChecked == true);
    }

    private void ReplaceOne_Click(object sender, RoutedEventArgs e)
    {
        if (_isReadOnly || _findMatches.Count == 0) return;
        var index = Math.Clamp(_findMatchIndex, 0, _findMatches.Count - 1);
        var range = _findMatches[index];
        var replacement = ReplacementForMatch(index, ReplaceTextBox.Text ?? string.Empty);
        ReplaceRangeAndMoveCaret(range, replacement);
        RefreshFindMatches(selectNearest: true);
    }

    private void ReplaceAll_Click(object sender, RoutedEventArgs e)
    {
        if (_isReadOnly || string.IsNullOrEmpty(FindTextBox.Text) || _findMatches.Count == 0) return;
        var replacement = ReplaceTextBox.Text ?? string.Empty;
        // Suppress expensive, repeated match recomputation in both the native and
        // managed replacement paths. Refresh once after the complete mutation.
        _findBulkEdit = true;
        try
        {
            if (FindRegex.IsChecked != true && ReplacePreserveCase.IsChecked != true && FindInSelection.IsChecked != true)
            {
                // Keep the optimized, native C++ ReplaceAll path for the common case.
                _model.ReplaceAll(FindTextBox.Text, replacement,
                    FindMatchCase.IsChecked == true, FindWholeWord.IsChecked == true, 10_000);
            }
            else
            {
                // Snapshot all replacements before mutation; apply right-to-left so offsets remain stable.
                var edits = new List<(nuint Start, nuint End, string Text)>(_findMatches.Count);
                for (var index = 0; index < _findMatches.Count; index++)
                {
                    var range = _findMatches[index];
                    var start = _model.GetOffsetAt(new MonacoPosition(range.StartLineNumber, range.StartColumn));
                    var end = _model.GetOffsetAt(new MonacoPosition(range.EndLineNumber, range.EndColumn));
                    edits.Add((start, end, ReplacementForMatch(index, replacement)));
                }
                using var transaction = _model.BeginUndoTransaction();
                for (var index = edits.Count - 1; index >= 0; index--)
                {
                    var edit = edits[index];
                    _model.Replace(edit.Start, edit.End, edit.Text);
                }
                transaction.Complete(); // Dispose otherwise rolls back the whole operation.
            }
        }
        finally { _findBulkEdit = false; }
        if (FindInSelection.IsChecked == true) FindInSelection.IsChecked = false;
        AfterProjectionChanged(true);
        RefreshFindMatches(selectNearest: false);
    }

    private void RefreshContributionLocalization()
    {
        FindTextBox.PlaceholderText = Local("actions.find");
        ReplaceTextBox.PlaceholderText = Local("editor.action.startFindReplaceAction");
        FindMatchCase.Content = "Aa";
        FindWholeWord.Content = "ab";
        FindRegex.Content = ".*";
        ReplacePreserveCase.Content = "AB";
        ToolTipService.SetToolTip(FindRegex, Local("monacoNative.find.regex"));
        ToolTipService.SetToolTip(FindInSelection, Local("monacoNative.find.inSelection"));
        ToolTipService.SetToolTip(ReplacePreserveCase, Local("monacoNative.find.preserveCase"));
        ToolTipService.SetToolTip(FindReplaceExpander, Local("monacoNative.find.toggleReplace"));
        ToolTipService.SetToolTip(FindMatchCase, Local("monacoNative.find.matchCase"));
        ToolTipService.SetToolTip(FindWholeWord, Local("monacoNative.find.wholeWord"));
        FindPreviousButton.Content = "↑";
        FindNextButton.Content = "↓";
        FindCloseButton.Content = "×";
        ToolTipService.SetToolTip(FindPreviousButton, Local("monacoNative.find.previous"));
        ToolTipService.SetToolTip(FindNextButton, Local("monacoNative.find.next"));
        ToolTipService.SetToolTip(FindCloseButton, Local("monacoNative.find.close"));
        ReplaceOneButton.Content = "↪";
        ReplaceAllButton.Content = "⇉";
        ToolTipService.SetToolTip(ReplaceOneButton, Local("monacoNative.find.replaceOne"));
        ToolTipService.SetToolTip(ReplaceAllButton, Local("monacoNative.find.replaceAll"));
        if (IsFindOpen) UpdateFindWidgetState();
        if (_lightBulbButton is not null)
            ToolTipService.SetToolTip(_lightBulbButton, Local("editor.action.quickFix"));
    }

    private string Local(string key) => _localization.GetString(key);
    private string LocalFormat(string key, params object?[] args) => _localization.Format(key, args);

    private static SolidColorBrush Brush(byte a, byte r, byte g, byte b) => new(Color.FromArgb(a, r, g, b));
    private static byte ToByte(double value) => (byte)Math.Round(Math.Clamp(value, 0.0, 1.0) * 255.0);
    private static bool RangeContains(LanguageRange range, LanguagePosition position)
    {
        var r = range.Normalize();
        return Compare(position, new LanguagePosition(r.StartLineNumber, r.StartColumn)) >= 0 &&
               Compare(position, new LanguagePosition(r.EndLineNumber, r.EndColumn)) <= 0;
    }
    private static int Compare(LanguagePosition a, LanguagePosition b) =>
        a.LineNumber != b.LineNumber ? a.LineNumber.CompareTo(b.LineNumber) : a.Column.CompareTo(b.Column);
    private static MonacoRange ToMonacoRange(LanguageRange range) => new(range.StartLineNumber, range.StartColumn, range.EndLineNumber, range.EndColumn);

    private Task[] GetPendingBackgroundOperations()
    {
        return new[]
        {
            _nativeDiagnosticsTask, _foldingRefreshTask, _lspRefreshTask,
            _contributionRefreshTask, _suggestTask, _parameterHintsTask
        }.Where(task => task is { IsCompleted: false }).Cast<Task>().Distinct().ToArray();
    }

    private void DisposeContributions()
    {
        HideParameterHints();
        _parameterHintsTimer.Tick -= ParameterHintsTimer_Tick;
        CancelAutomaticSuggestions();
        _suggestTimer.Tick -= SuggestTimer_Tick;
        _contributionRefreshTimer.Stop();
        _contributionRefreshTimer.Tick -= ContributionRefreshTimer_Tick;
        _contributionRefreshCts?.Cancel();
        _contributionRefreshCts?.Dispose();
        _contributionRefreshCts = null;
        ClearInlineCompletion(updateProjection: false);
        ClearCodeLensControls();
        HideSuggestWidget();
        HideContributionPopup();
    }
}
