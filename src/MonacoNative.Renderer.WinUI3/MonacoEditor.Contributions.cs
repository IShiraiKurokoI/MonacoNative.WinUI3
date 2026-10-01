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
    }

    public void ShowFind(bool replace)
    {
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

    public async Task TriggerSuggestAsync(CancellationToken cancellationToken = default)
    {
        var result = await _languages.ProvideCompletionItemsAsync(
            _languageDocument,
            ToLanguagePosition(_active),
            new CompletionContext(CompletionTriggerKind.Invoke),
            cancellationToken);
        if (result.Suggestions.Count == 0)
        {
            HideContributionPopup();
            return;
        }

        ShowAnchoredPopup(_active, "editor.action.triggerSuggest");
        foreach (var item in result.Suggestions.Take(40))
        {
            var resolved = item;
            AddPopupButton(CompletionLabel(item), async () =>
            {
                var providerItem = resolved;
                await ApplyCompletionItemAsync(providerItem, cancellationToken);
            });
        }
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

    public async Task TriggerParameterHintsAsync(CancellationToken cancellationToken = default)
    {
        var help = await _languages.ProvideSignatureHelpAsync(
            _languageDocument,
            ToLanguagePosition(_active),
            new SignatureHelpContext(SignatureHelpTriggerKind.Invoke),
            cancellationToken);
        if (help is null || help.Signatures.Count == 0)
        {
            HideContributionPopup();
            return;
        }

        var signatureIndex = Math.Clamp(help.ActiveSignature, 0, help.Signatures.Count - 1);
        var signature = help.Signatures[signatureIndex];
        ShowAnchoredPopup(_active, "editor.action.triggerParameterHints");
        AddPopupText(signature.Label, emphasized: true);
        if (!string.IsNullOrWhiteSpace(signature.Documentation))
            AddPopupText(signature.Documentation!);
        if (signature.Parameters is { Count: > 0 })
        {
            var parameterIndex = Math.Clamp(signature.ActiveParameter ?? help.ActiveParameter, 0, signature.Parameters.Count - 1);
            var parameter = signature.Parameters[parameterIndex];
            AddPopupText($"{parameterIndex + 1}/{signature.Parameters.Count}  {parameter.Label}", emphasized: true);
            if (!string.IsNullOrWhiteSpace(parameter.Documentation))
                AddPopupText(parameter.Documentation!);
        }
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

    private async Task ApplyCompletionItemAsync(CompletionItem item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var range = item.Range is null ? GetWordRangeAtCaret() : ToMonacoRange(item.Range.Value.Normalize());
        if (item.AdditionalTextEdits is { Count: > 0 })
        {
            var edits = new List<LanguageTextEdit>(item.AdditionalTextEdits)
            {
                new(ToLanguageRange(range), item.InsertText ?? item.Label),
            };
            ApplyLanguageTextEdits(edits);
        }
        else
        {
            ReplaceRangeAndMoveCaret(range, item.InsertText ?? item.Label);
        }
        if (item.Command is not null) await item.Command(cancellationToken);
        HideContributionPopup();
        Focus(FocusState.Programmatic);
    }

    private void ReplaceRangeAndMoveCaret(MonacoRange range, string text)
    {
        if (_isReadOnly) return;
        var start = _model.GetOffsetAt(Normalize(range.StartLineNumber, range.StartColumn));
        var end = _model.GetOffsetAt(Normalize(range.EndLineNumber, range.EndColumn));
        if (end < start) (start, end) = (end, start);
        _model.Replace(start, end, text ?? string.Empty);
        var target = start + (nuint)(text?.Length ?? 0);
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
        var line = _model.GetLineContent(_active.LineNumber);
        var index = Math.Clamp(_active.Column - 1, 0, line.Length);
        var start = index;
        var end = index;
        while (start > 0 && IsWordCharacter(line[start - 1])) start--;
        while (end < line.Length && IsWordCharacter(line[end])) end++;
        return new MonacoRange(_active.LineNumber, start + 1, _active.LineNumber, end + 1);
    }

    private static bool IsWordCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';

    private void ShowAnchoredPopup(MonacoPosition anchor, string titleKey)
    {
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
        var source = new CancellationTokenSource();
        _contributionRefreshCts = source;
        _ = RefreshContributionStateAsync(full, source.Token);
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

    private void OnContributionsSelectionChanged() => ScheduleContributionRefresh(full: false);

    private void OnContributionsModelChanged()
    {
        using var methodSample = _methodProfiler.Measure("OnContributionsModelChanged");
        ClearInlineCompletion(updateProjection: false);
        ScheduleContributionRefresh(full: true);
        if (!_findBulkEdit && FindWidget.Visibility == Visibility.Visible)
            RefreshFindMatches(selectNearest: false);
    }

    private void OnContributionsViewportChanged()
    {
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

    private void DisposeContributions()
    {
        _contributionRefreshTimer.Stop();
        _contributionRefreshTimer.Tick -= ContributionRefreshTimer_Tick;
        _contributionRefreshCts?.Cancel();
        _contributionRefreshCts?.Dispose();
        _contributionRefreshCts = null;
        ClearInlineCompletion(updateProjection: false);
        ClearCodeLensControls();
        HideContributionPopup();
    }
}
