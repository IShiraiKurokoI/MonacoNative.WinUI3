using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using MonacoNative.Core;
using MonacoNative.Documents;
using MonacoNative.Language;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace MonacoNative.Renderer.WinUI3;

public sealed partial class MonacoEditor
{
    private Border? _renamePopup;
    private TextBox? _renameInput;
    private TextBlock? _renameHint;
    private Border? _renamePreview;
    private readonly Dictionary<Uri, (MonacoTextModel Model, ulong Version)> _renameWorkspaceVersions = new(EditorDocumentUriComparer.Instance);
    private CancellationTokenSource? _renameCts;
    private CancellationTokenRegistration _renameCancellationRegistration;
    private Task? _renameTask;
    private long _renameSequence;
    private MonacoTextModel? _renameModel;
    private ILanguageTextModel? _renameLanguageDocument;
    private Uri? _renameUri;
    private string _renameLanguage = string.Empty;
    private ulong _renameVersion;
    private MonacoPosition _renameCaret;
    private MonacoPosition _renameSelectionAnchor;
    private LanguageRange _renameRange;
    private string _renameOriginalName = string.Empty;
    private bool _renameInputComposing;
    private long _renameCompositionSequence;
    private bool _renameFocusPending;
    private bool _renameSubmitted;
    private long _renameApplyingSequence = -1;

    /// <summary>Whether Ctrl+Enter opens the native refactor preview. Defaults to true.</summary>
    public bool RenamePreviewEnabled { get; set; } = true;

    /// <summary>True only while the inline rename input is visible.</summary>
    public bool IsRenameOpen => _renamePopup?.Visibility == Visibility.Visible;

    /// <summary>True while pending edits are being reviewed, before they change any model.</summary>
    public bool IsRenamePreviewOpen => _refactorSessionActive;

    /// <summary>Resolve the symbol and focus an inline, VS Code-style rename input.</summary>
    public Task TriggerRenameAsync(CancellationToken cancellationToken = default)
    {
        if (IsDisposed || _isReadOnly) return Task.CompletedTask;
        _renameTask = _lifetime.Track(PrepareRenameInputAsync(cancellationToken));
        return _renameTask;
    }

    private async Task PrepareRenameInputAsync(CancellationToken cancellationToken)
    {
        var invokedFromContextMenu = _executingContextMenuCommand;
        CancelRenameSession();
        if (!_languages.HasRenameProvider(_languageDocument)) return;
        cancellationToken.ThrowIfCancellationRequested();
        _renameCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.ShutdownToken);
        var token = _renameCts.Token;
        var request = _renameSequence;
        _renameModel = _model;
        _renameLanguageDocument = LanguageModelSnapshot.Capture(_languageDocument);
        _renameUri = DocumentUri;
        _renameLanguage = LanguageId;
        _renameVersion = _model.VersionId;
        _renameCaret = _active;
        _renameSelectionAnchor = _anchor;
        _renameSubmitted = false;
        foreach (var document in WorkspaceDocuments)
            _renameWorkspaceVersions[document.Uri] = (document.Model, document.Model.VersionId);
        _renameCancellationRegistration = token.Register(() =>
            DispatcherQueue.TryEnqueue(() =>
            {
                if (request == _renameSequence) CancelRenameSession();
            }));

        try
        {
            var location = await _languages.ResolveRenameLocationAsync(
                _renameLanguageDocument, ToLanguagePosition(_renameCaret), token);
            if (!IsRenameSessionCurrent(request)) return;
            if (!string.IsNullOrWhiteSpace(location?.RejectReason))
            {
                ShowRenameMessage(location!.RejectReason!);
                return;
            }

            var range = (location?.Range ?? ToLanguageRange(GetWordRangeAtCaret())).Normalize();
            // LSP prepareRename can return only a range, with no placeholder text.
            var initial = !string.IsNullOrEmpty(location?.Text)
                ? location.Text : _model.GetValueInRange(ToMonacoRange(range));
            if (string.IsNullOrEmpty(initial))
            {
                CancelRenameSession();
                return;
            }
            _renameRange = range;
            _renameOriginalName = initial;
            HideContributionPopup();
            HideParameterHints();
            HideSuggestWidget();
            CancelAutomaticSuggestions();
            ClearInlineCompletion();
            CancelSnippetSession();
            EnsureRenameInput();
            RefreshRenamePalette();
            _renameInput!.Text = initial;
            _renameInput.FontFamily = new FontFamily(_textFormat?.FontFamily ?? "Consolas");
            _renameInput.FontSize = _textFormat?.FontSize ?? 14;
            _renameInput.Height = Math.Max(24, _lineHeight + 6);
            _renameHint!.FontSize = _renameInput.FontSize * 0.8;
            _renameHint.Text = Local("monacoNative.rename.hint");
            _renameHint.Visibility = RenamePreviewEnabled ? Visibility.Visible : Visibility.Collapsed;
            _renamePopup!.Padding = RenamePreviewEnabled ? new Thickness(4, 4, 4, 0) : new Thickness(0);
            // RenameWidget uses the resolved symbol range, not the placeholder length,
            // for input.size. Measure a monospace cell with the actual editor font.
            var inputColumns = Math.Max(20, Math.Floor(Math.Max(0, (double)range.EndColumn - range.StartColumn) * 1.1));
            var cellWidth = _measurer?.Measure("0") ?? _renameInput.FontSize * 0.6;
            var chromeWidth = _renamePopup.Padding.Left + _renamePopup.Padding.Right +
                _renamePopup.BorderThickness.Left + _renamePopup.BorderThickness.Right +
                _renameInput.Padding.Left + _renameInput.Padding.Right +
                _renameInput.BorderThickness.Left + _renameInput.BorderThickness.Right;
            _renamePopup.Width = Math.Max(200, inputColumns * cellWidth + chromeWidth);
            _renamePopup.Visibility = Visibility.Visible;
            _commands.SetContext("renameInputVisible", true);
            _renameInputComposing = false;
            _renameFocusPending = true;

            // The native ACP document must lose focus before the XAML TextBox creates
            // its own TSF context. Otherwise IME/CharacterReceived edits hit the model.
            LeaveEditorTextInput();
            RevealCaret();
            PositionRenameWidget();
            var selection = RenameInputPolicy.GetSelection(initial.Length,
                new(range.StartLineNumber, range.StartColumn, range.EndLineNumber, range.EndColumn),
                new(_anchor.LineNumber, _anchor.Column, _active.LineNumber, _active.Column));
            _renamePopup.UpdateLayout();
            var focusedImmediately = _renameInput.Focus(FocusState.Programmatic);
            _renameInput.Select(selection.Start, selection.Length);
            _renameFocusPending = !focusedImmediately;
            // A newly attached native control may not accept focus until the next layout turn.
            if (!focusedImmediately) _ = DispatcherQueue.TryEnqueue(() =>
            {
                if (request != _renameSequence || !IsRenameOpen) return;
                _renameFocusPending = false;
                var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
                if (focused is not null && !ReferenceEquals(focused, this) && !IsChildOf(focused, _renamePopup) &&
                    !(invokedFromContextMenu && focused is MenuFlyoutItemBase))
                {
                    CancelRenameSession();
                    return;
                }
                if (_renameInput.FocusState != FocusState.Unfocused) return;
                if (!_renameInput.Focus(FocusState.Programmatic))
                {
                    CancelRenameSession(restoreFocus: true);
                    return;
                }
                if (_renameInput.Text == initial) _renameInput.Select(selection.Start, selection.Length);
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (request == _renameSequence) CancelRenameSession();
        }
        catch (Exception error)
        {
            if (!IsDisposed && request == _renameSequence) ShowRenameMessage(error.Message);
        }
    }

    private void EnsureRenameInput()
    {
        if (_renamePopup is not null) return;
        _renameInput = new TextBox
        {
            MinWidth = 0, MinHeight = 0, Padding = new Thickness(3, 0, 3, 0),
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(4),
            AcceptsReturn = false, TextWrapping = TextWrapping.NoWrap,
            IsSpellCheckEnabled = false, IsTextPredictionEnabled = false,
            // Use a native TextBox editing surface without WinUI's underline/clear button.
            // ContentElement remains the actual native text/selection/IME host.
            Template = (ControlTemplate)XamlReader.Load("""
                <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="TextBox">
                    <Border x:Name="BorderElement" Background="{TemplateBinding Background}"
                        BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"
                        CornerRadius="{TemplateBinding CornerRadius}">
                        <ScrollViewer x:Name="ContentElement" IsTabStop="False" Padding="{TemplateBinding Padding}"
                            HorizontalScrollMode="Auto" VerticalScrollMode="Disabled"
                            HorizontalScrollBarVisibility="Hidden" VerticalScrollBarVisibility="Hidden"
                            VerticalAlignment="Center" ZoomMode="Disabled" />
                    </Border>
                </ControlTemplate>
                """),
        };
        AutomationProperties.SetAutomationId(_renameInput, "RenameInput");
        AutomationProperties.SetName(_renameInput, Local("monacoNative.rename.inputLabel"));
        _renameInput.KeyDown += RenameInput_KeyDown;
        _renameInput.TextCompositionStarted += (_, _) =>
        {
            ++_renameCompositionSequence;
            _renameInputComposing = true;
        };
        _renameInput.TextCompositionEnded += (_, _) =>
        {
            var request = _renameSequence;
            var composition = ++_renameCompositionSequence;
            // An IME may finish composition before the committing Enter/Escape bubbles.
            // Keep that same input turn owned by the IME; the next key can accept rename.
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                if (request == _renameSequence && composition == _renameCompositionSequence)
                    _renameInputComposing = false;
            });
        };
        _renameInput.LostFocus += (_, _) => QueueRenameBlurCheck();
        _renameHint = new TextBlock { Margin = new Thickness(0, 2, 0, 2), Opacity = 0.8 };
        var contents = new StackPanel();
        contents.Children.Add(_renameInput);
        contents.Children.Add(_renameHint);
        _renamePopup = new Border
        {
            Child = contents, CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed,
            Shadow = new ThemeShadow(), Translation = new System.Numerics.Vector3(0, 0, 16),
        };
        AutomationProperties.SetAutomationId(_renamePopup, "RenameWidget");
        Canvas.SetZIndex(_renamePopup, 100);
        ContributionLayer.Children.Add(_renamePopup);
    }

    private void RenameInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Enter/Escape belong to the IME until the native TextBox finishes composition.
        if (_renameInputComposing || e.Handled) return;
        var ctrl = IsKeyDown(VirtualKey.Control);
        var alt = IsKeyDown(VirtualKey.Menu);
        var meta = IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows);
        if (e.Key == VirtualKey.Escape && !ctrl && !alt && !meta)
        {
            e.Handled = true;
            CancelRenameSession(restoreFocus: true);
        }
        else if (e.Key == VirtualKey.Enter && !alt && !meta && !IsKeyDown(VirtualKey.Shift))
        {
            e.Handled = true;
            _renameTask = _lifetime.Track(AcceptRenameAsync(ctrl));
        }
    }

    /// <summary>Accept the current name; optionally review edits without applying them.</summary>
    public async Task AcceptRenameAsync(bool preview = false)
    {
        if (!IsRenameOpen || _renameInput is null || _renameCts is null || _renameSubmitted || _renameInputComposing) return;
        if (preview && !RenamePreviewEnabled) return;
        var newName = _renameInput.Text;
        if (RenameInputPolicy.ShouldCancel(_renameOriginalName, newName))
        {
            CancelRenameSession(restoreFocus: true);
            return;
        }
        var request = _renameSequence;
        var token = _renameCts.Token;
        _renameSubmitted = true;
        _renameFocusPending = false;
        _renamePopup!.Visibility = Visibility.Collapsed;
        _commands.SetContext("renameInputVisible", false);
        Focus(FocusState.Programmatic);
        try
        {
            var edit = await _languages.ProvideRenameEditsAsync(
                _renameLanguageDocument!, ToLanguagePosition(_renameCaret), newName, token);
            if (!IsRenameSessionCurrent(request)) return;
            if (edit is null || edit.Edits.Count == 0)
            {
                ShowRenameMessage(Local("monacoNative.rename.noEdits"));
                return;
            }
            if (!AreRenameWorkspaceVersionsCurrent(edit))
            {
                ShowRenameMessage(Local("monacoNative.rename.documentChanged"));
                return;
            }
            if (preview && RenamePreviewEnabled)
                await ShowRenamePreviewAsync(edit, newName, request, token);
            else
                await ApplyRenameEditAsync(edit, request, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (request == _renameSequence) CancelRenameSession();
        }
        catch (Exception error)
        {
            if (!IsDisposed && request == _renameSequence) ShowRenameMessage(error.Message);
        }
    }

    /// <summary>Cancel input or preview without changing document text.</summary>
    public void CancelRename() => CancelRenameSession(restoreFocus: true);

    private async Task ApplyRenameEditAsync(WorkspaceEdit edit, long request, CancellationToken token)
    {
        if (!IsRenameSessionCurrent(request) || !AreRenameWorkspaceVersionsCurrent(edit))
        {
            if (request == _renameSequence) ShowRenameMessage(Local("monacoNative.rename.documentChanged"));
            return;
        }
        _renameApplyingSequence = request;
        try
        {
            HideTransientEditor();
            Focus(FocusState.Programmatic);
            // Match VS Code: collapse to the active endpoint before the bulk edit.
            ClearSecondarySelections();
            _anchor = _active;
            _renameSelectionAnchor = _anchor;
            await ApplyWorkspaceEditAsync(edit, token);
            if (request == _renameSequence) CancelRenameSession(restoreFocus: true);
        }
        finally { if (_renameApplyingSequence == request) _renameApplyingSequence = -1; }
    }

    private bool IsRenameSessionCurrent(long request) =>
        !IsDisposed && request == _renameSequence && _renameCts is { IsCancellationRequested: false } &&
        (_refactorSessionActive || (!_isReadOnly && ReferenceEquals(_model, _renameModel) &&
            _model.VersionId == _renameVersion && Equals(DocumentUri, _renameUri) && LanguageId == _renameLanguage &&
            _active == _renameCaret && _anchor == _renameSelectionAnchor));

    private bool AreRenameWorkspaceVersionsCurrent(WorkspaceEdit edit)
    {
        foreach (var uri in edit.Edits.Select(item => item.Uri).Distinct())
        {
            if (!_renameWorkspaceVersions.TryGetValue(uri, out var snapshot)) continue;
            var current = FindWorkspaceDocument(uri);
            if (current is null || !ReferenceEquals(current.Model, snapshot.Model) || current.Model.VersionId != snapshot.Version)
                return false;
        }
        return true;
    }

    private void CancelRenameSession(bool restoreFocus = false)
    {
        ++_renameSequence;
        _renameFocusPending = false;
        _renameSubmitted = false;
        _renameInputComposing = false;
        ++_renameCompositionSequence;
        _renameCancellationRegistration.Dispose();
        _renameCancellationRegistration = default;
        var cancellation = _renameCts;
        _renameCts = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
        if (_renamePopup is not null) _renamePopup.Visibility = Visibility.Collapsed;
        CloseRefactorPreview();
        _renameWorkspaceVersions.Clear();
        _renameModel = null;
        _renameLanguageDocument = null;
        _renameUri = null;
        _commands.SetContext("renameInputVisible", false);
        if (restoreFocus && !IsDisposed && _loaded) Focus(FocusState.Programmatic);
    }

    private void OnRenameEditorStateChanged()
    {
        if (_renameCts is not null &&
            !(_renameApplyingSequence == _renameSequence && _applyingWorkspaceEdits) &&
            !IsRenameSessionCurrent(_renameSequence))
            CancelRenameSession();
    }

    private void ShowRenameMessage(string message)
    {
        var position = _active;
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        var restoreFocus = IsEditorTextFocused ||
            (_renamePopup is not null && IsChildOf(focused, _renamePopup)) ||
            (_renamePreview is not null && IsChildOf(focused, _renamePreview));
        CancelRenameSession(restoreFocus);
        ShowAnchoredPopup(position, "editor.action.rename");
        _contributionPopupContent?.Children.Clear();
        AddPopupText(message);
    }

    private void QueueRenameBlurCheck()
    {
        var request = _renameSequence;
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (IsDisposed || request != _renameSequence || _renameCts is null || _renameFocusPending || IsRenamePreviewOpen) return;
            var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            if ((_renamePopup is not null && IsChildOf(focused, _renamePopup)) ||
                (_renamePreview is not null && IsChildOf(focused, _renamePreview))) return;
            // The editor owns focus while prepare/rename is awaiting a provider. A real
            // focus move during that await must cancel, just as a move from the input does.
            if (!IsRenameOpen && !IsRenamePreviewOpen && (IsEditorTextFocused || _executingContextMenuCommand)) return;
            CancelRenameSession();
        });
    }

    private void PositionRenameWidget()
    {
        if (IsRenameOpen && _renamePopup is not null)
        {
            var anchor = GetViewportPoint(new(_renameRange.StartLineNumber, _renameRange.StartColumn), belowLine: false);
            if (anchor.Y + _lineHeight < 0 || anchor.Y > EditorViewportHeight)
            {
                CancelRenameSession(restoreFocus: true);
                return;
            }
            var availableWidth = Math.Max(1, EditorViewportWidth - 8);
            _renamePopup.MaxWidth = availableWidth;
            _renamePopup.Measure(new Size(availableWidth, double.PositiveInfinity));
            var width = Math.Min(_renamePopup.DesiredSize.Width, availableWidth);
            var height = _renamePopup.DesiredSize.Height;
            var left = Math.Clamp(anchor.X, 4, Math.Max(4, EditorViewportWidth - width - 4));
            var below = anchor.Y + _lineHeight;
            var top = below + height <= EditorViewportHeight - 4 ? below : anchor.Y - height;
            Canvas.SetLeft(_renamePopup, left);
            Canvas.SetTop(_renamePopup, Math.Clamp(top, 0, Math.Max(0, EditorViewportHeight - height)));
        }
    }

    private void RefreshRenamePalette()
    {
        var highContrast = _lastHighContrast == true;
        var light = _lastTheme == ElementTheme.Light;
        // VS Code c54303d6: theme-defaults/{light,dark}_modern.json. RenameWidget
        // uses editorWidget.background + widget.border for the outer shell, and
        // input.background/input.border/input.foreground for the native input.
        var foreground = new SolidColorBrush(_foregroundColor);
        var background = new SolidColorBrush(highContrast ? _backgroundColor :
            light ? Color.FromArgb(255, 248, 248, 248) : Color.FromArgb(255, 32, 32, 32)); // #F8F8F8 / #202020
        var border = new SolidColorBrush(highContrast ? _foregroundColor :
            light ? Color.FromArgb(255, 229, 229, 229) : Color.FromArgb(255, 49, 49, 49)); // #E5E5E5 / #313131
        foreach (var widget in new[] { _renamePopup, _renamePreview })
        {
            if (widget is null) continue;
            widget.Background = background;
            widget.BorderBrush = border;
        }
        if (_renameInput is not null)
        {
            _renameInput.Foreground = foreground;
            _renameInput.Background = new SolidColorBrush(highContrast ? _backgroundColor :
                light ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 49, 49, 49)); // #FFFFFF / #313131
            _renameInput.SelectionHighlightColor = new SolidColorBrush(_selectionColor);
            _renameInput.BorderBrush = new SolidColorBrush(highContrast ? _foregroundColor :
                light ? Color.FromArgb(255, 206, 206, 206) : Color.FromArgb(255, 60, 60, 60)); // #CECECE / #3C3C3C
            _renameInput.BorderThickness = new Thickness(1);
            AutomationProperties.SetName(_renameInput, Local("monacoNative.rename.inputLabel"));
        }
        if (_renameHint is not null)
        {
            _renameHint.Foreground = foreground;
            _renameHint.Text = Local("monacoNative.rename.hint");
        }
        RefreshRefactorPalette();
    }

    // FocusState on a UserControl is insufficient: GotFocus/LostFocus bubble from its
    // TextBoxes, buttons, tab headers and preview controls. Only the actual editor focus
    // owner is allowed to activate its ACP document or run document keybindings.
    private bool IsEditorTextFocused => XamlRoot is null
        ? FocusState != FocusState.Unfocused
        : ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), this);

    private bool ShouldRouteEditorInput(DependencyObject? source) =>
        IsEditorTextFocused && !IsRenameOpen && !_showingEmptyGroup &&
        _transientEditorHost?.Visibility != Visibility.Visible &&
        !IsChildOf(source, FindWidget) && !IsChildOf(source, ContributionLayer);

    private void LeaveEditorTextInput()
    {
        _textInput?.NotifyFocusLeave();
        _textInput?.SetReadOnly(true);
        _fallbackCharacterAccumulator.Reset();
        _caretVisible = false;
        _commands.ResetChord();
        _chordTimer.Stop();
        UpdateCommandContext();
        if (_loaded && !IsDisposed) Render(RenderDirty.Caret);
    }
}
