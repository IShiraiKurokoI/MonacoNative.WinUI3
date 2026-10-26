using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MonacoNative.Core;
using MonacoNative.Language;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace MonacoNative.Renderer.WinUI3;

/// <summary>
/// Completion suggestions use their own focus-free presenter, not the generic
/// hover/code-action button popup. Only MonacoEditor owns keyboard input, so an
/// open suggestion list does not break TSF, caret movement or text composition.
/// </summary>
public sealed partial class MonacoEditor
{
    private const double SuggestRowHeight = 26;
    private const double SuggestMaxHeight = 286;
    private const int SuggestOverscanRows = 6;
    private readonly SuggestionSelection _suggestSelection = new();
    private Border? _suggestPopup;
    private ScrollViewer? _suggestScroller;
    private StackPanel? _suggestRowPanel;
    private readonly List<(int Index, Border Row)> _suggestRowBorders = new();
    private int _suggestRenderedStart = -1;
    private int _suggestRenderedEnd = -1;
    private IReadOnlyList<CompletionItem> _suggestItems = Array.Empty<CompletionItem>();
    private MonacoPosition _suggestAnchor;
    private ulong _suggestDocumentVersion;
    private long _suggestRequestSequence;
    private string _suggestFilterPrefix = string.Empty;
    private readonly CompletionRanking _completionRanking = new();
    private IReadOnlyList<CompletionMatch> _suggestMatches = Array.Empty<CompletionMatch>();
    private bool _completionListIncomplete;
    private int _completionListLine;
    private int _completionContinuationColumn;
    private ulong _completionContinuationVersion;
    public CompletionRankingOptions SuggestOptions => _completionRanking.Options;
    private Color _suggestSelectedBackground = Color.FromArgb(255, 9, 71, 113);
    private Color _suggestForeground = Color.FromArgb(255, 212, 212, 212);
    private Color _suggestMutedForeground = Color.FromArgb(255, 163, 163, 163);
    private Color _suggestHighlightForeground = Color.FromArgb(255, 255, 255, 255);

    private bool IsSuggestWidgetVisible => _suggestPopup?.Visibility == Visibility.Visible && _suggestSelection.Count > 0;

    private void InitializeSuggestWidget()
    {
        _suggestRowPanel = new StackPanel { Spacing = 0 };
        _suggestScroller = new ScrollViewer
        {
            Content = _suggestRowPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollMode = ScrollMode.Enabled,
            ZoomMode = ZoomMode.Disabled,
        };
        // Render only viewport rows plus overscan. LSP servers may return thousands
        // of items; the list must remain complete without creating thousands of UI controls.
        _suggestScroller.ViewChanged += (_, _) =>
        {
            if (IsSuggestWidgetVisible) RebuildSuggestRows();
        };
        _suggestPopup = new Border
        {
            Visibility = Visibility.Collapsed,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            Child = _suggestScroller,
            IsHitTestVisible = true,
        };
        AutomationProperties.SetName(_suggestPopup, "IntelliSense suggestions");
        ContributionLayer.Children.Add(_suggestPopup);
        ApplySuggestWidgetPalette(_lastHighContrast == true, _lastTheme);
    }

    private void ShowSuggestWidget(IReadOnlyList<CompletionItem> items, MonacoPosition caret, ulong version)
    {
        if (_suggestPopup is null || _suggestRowPanel is null || _disposed || _isReadOnly ||
            _textInput?.IsComposing == true) return;
        HideContributionPopup();
        var beforeCaret = _model.GetLineContent(caret.LineNumber);
        beforeCaret = beforeCaret[..Math.Clamp(caret.Column - 1, 0, beforeCaret.Length)];
        _suggestMatches = _completionRanking.Rank(items, beforeCaret, caret.LineNumber);
        _suggestItems = _suggestMatches.Select(match => match.Item).ToArray();
        _suggestAnchor = caret;
        _suggestDocumentVersion = version;
        _suggestFilterPrefix = SuggestWordPrefix(caret);
        _suggestSelection.Reset(_suggestItems.Count);
        _suggestSelection.Select(_completionRanking.Select(_suggestMatches, _model.LanguageId, _suggestFilterPrefix));
        if (_suggestSelection.Count == 0) { HideSuggestWidget(); return; }
        _suggestRenderedStart = _suggestRenderedEnd = -1;
        RebuildSuggestRows();
        _suggestPopup.Visibility = Visibility.Visible;
        _commands.SetContext("suggestWidgetVisible", true);
        PositionSuggestWidget();
        PositionParameterHints();
        _suggestScroller?.ChangeView(null, 0, null, disableAnimation: true);
        // Do not call Focus() on any row or on the ScrollViewer. MonacoEditor is
        // the text input target even while a row is selected with the arrow keys.
    }

    private void HideSuggestWidget(bool preserveCompletionSession = false)
    {
        if (!preserveCompletionSession) _completionListIncomplete = false;
        _suggestRequestSequence++; // Prevent slower older LSP responses reopening the widget.
        if (_suggestPopup?.Visibility != Visibility.Visible && _suggestItems.Count == 0)
            return; // Selection changes are hot-path; do not rebuild command context.
        if (_suggestPopup is not null)
            _suggestPopup.Visibility = Visibility.Collapsed;
        _suggestItems = Array.Empty<CompletionItem>();
        _suggestSelection.Reset(0);
        _suggestRowBorders.Clear();
        _suggestRenderedStart = _suggestRenderedEnd = -1;
        _suggestRowPanel?.Children.Clear();
        _commands.SetContext("suggestWidgetVisible", false);
    }

    private void TrackIncompleteCompletionEdit(MonacoModelChangedEventArgs change)
    {
        if (!_completionListIncomplete) return;
        var singleLine = !change.IsFullInvalidation && change.StartLineNumber == _completionListLine &&
            change.OldEndLineNumber == _completionListLine && change.NewEndLineNumber == _completionListLine &&
            change.OldEndColumn == _completionContinuationColumn;
        var inserted = singleLine ? _model.GetTextRange(change.StartOffset, change.NewEndOffset - change.StartOffset) : "";
        if (!singleLine || inserted.Any(c => !char.IsLetterOrDigit(c) && c is not ('_' or '$')))
        { _completionListIncomplete = false; return; }
        _completionContinuationColumn = change.NewEndColumn;
        _completionContinuationVersion = _model.VersionId;
    }

    /// <summary>Reposition (or dismiss) when the editor scrolls/resizes.</summary>
    private void PositionSuggestWidget()
    {
        if (!IsSuggestWidgetVisible || _suggestPopup is null) return;
        if (ActualWidth < 120 || ActualHeight < 70) { HideSuggestWidget(); return; }

        var point = GetViewportPoint(_suggestAnchor, belowLine: true);
        // When the anchor scrolls outside the viewport, never leave a floating
        // completion list detached from the cursor.
        if (point.Y < -_lineHeight || point.Y > ActualHeight + _lineHeight)
        {
            HideSuggestWidget();
            return;
        }

        var width = Math.Min(440, Math.Max(100, ActualWidth - 12));
        var height = Math.Min(SuggestMaxHeight, Math.Min(_suggestSelection.Count * SuggestRowHeight + 6, ActualHeight - 12));
        _suggestPopup.Width = width;
        _suggestPopup.Height = height;
        var x = Math.Clamp(point.X, 4, Math.Max(4, ActualWidth - width - 4));
        var y = point.Y;
        var above = point.Y - _lineHeight - height;
        if (y + height > ActualHeight - 4 && above >= 4)
            y = above;
        else
            y = Math.Clamp(y, 4, Math.Max(4, ActualHeight - height - 4));
        Canvas.SetLeft(_suggestPopup, x);
        Canvas.SetTop(_suggestPopup, y);
    }

    /// <summary>
    /// Called after TSF has had first refusal of a key. All recognized keys are
    /// consumed synchronously, before the normal editor movement/newline handler.
    /// </summary>
    private bool TryHandleSuggestWidgetKeyDown(KeyRoutedEventArgs e)
    {
        if (!IsSuggestWidgetVisible || _isReadOnly || _textInput?.IsComposing == true) return false;
        if (IsKeyDown(VirtualKey.Control) || IsKeyDown(VirtualKey.Menu) ||
            IsKeyDown(VirtualKey.Shift) || IsKeyDown(VirtualKey.LeftWindows) ||
            IsKeyDown(VirtualKey.RightWindows))
            return false;

        switch (e.Key)
        {
            case VirtualKey.Down: _suggestSelection.Move(1); break;
            case VirtualKey.Up: _suggestSelection.Move(-1); break;
            case VirtualKey.PageDown: _suggestSelection.Page(9); break;
            case VirtualKey.PageUp: _suggestSelection.Page(-9); break;
            case VirtualKey.Home: _suggestSelection.Select(0); break;
            case VirtualKey.End: _suggestSelection.Select(_suggestSelection.Count - 1); break;
            case VirtualKey.Enter:
            case VirtualKey.Tab:
                e.Handled = true;
                _ = CommitSelectedSuggestionAsync();
                return true;
            case VirtualKey.Escape:
                HideSuggestWidget();
                e.Handled = true;
                return true;
            default: return false;
        }
        e.Handled = true;
        UpdateSuggestSelectionVisuals(ensureVisible: true);
        return true;
    }

    private async Task CommitSelectedSuggestionAsync()
    {
        if (!IsSuggestWidgetVisible || _isReadOnly || _disposed) return;
        var index = _suggestSelection.SelectedIndex;
        if (index < 0 || index >= _suggestItems.Count) return;
        if (_model.VersionId != _suggestDocumentVersion || !_active.Equals(_suggestAnchor))
        {
            HideSuggestWidget();
            return;
        }
        var item = _suggestItems[index];
        _completionRanking.Remember(_model.LanguageId, _suggestFilterPrefix, item);
        CancelAutomaticSuggestions();
        HideSuggestWidget(); // Hide before editing; no stale popup survives model change.
        try
        {
            await ApplyCompletionItemAsync(item, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // External LSP commands may fail; keep editor input usable regardless.
            System.Diagnostics.Debug.WriteLine($"Suggestion commit failed: {ex}");
            Focus(FocusState.Programmatic);
        }
    }

    private void RebuildSuggestRows()
    {
        if (_suggestRowPanel is null || _suggestScroller is null || _suggestItems.Count == 0) return;
        var viewport = _suggestScroller.ViewportHeight > 0 ? _suggestScroller.ViewportHeight : SuggestMaxHeight;
        var first = Math.Clamp((int)(_suggestScroller.VerticalOffset / SuggestRowHeight) - SuggestOverscanRows,
            0, _suggestItems.Count);
        var last = Math.Min(_suggestItems.Count,
            first + (int)Math.Ceiling(viewport / SuggestRowHeight) + SuggestOverscanRows * 2);
        if (first == _suggestRenderedStart && last == _suggestRenderedEnd) return;
        _suggestRenderedStart = first;
        _suggestRenderedEnd = last;
        _suggestRowPanel.Children.Clear();
        _suggestRowBorders.Clear();
        if (first > 0)
            _suggestRowPanel.Children.Add(new Border { Height = SuggestRowHeight * first });
        for (var i = first; i < last; i++)
        {
            var index = i;
            var item = _suggestItems[i];
            var row = new Border
            {
                Height = SuggestRowHeight,
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(5, 0, 7, 0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };
            var columns = new Grid { ColumnSpacing = 7 };
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var (_, symbolColor) = SuggestKindSymbol(item.Kind);
            var glyph = CodiconPresenter.Create(item.Kind,
                new SolidColorBrush(_lastHighContrast == true ? _suggestForeground : symbolColor));
            Grid.SetColumn(glyph, 0);
            columns.Children.Add(glyph);

            var label = new TextBlock
            {
                Foreground = new SolidColorBrush(_suggestForeground),
                FontSize = 13,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsTextSelectionEnabled = false,
            };
            AppendLabelWithMatchHighlight(label, item.Label, _suggestMatches[i].Matches);
            if (item.Deprecated) label.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
            Grid.SetColumn(label, 1);
            columns.Children.Add(label);

            if (!string.IsNullOrWhiteSpace(item.LabelDescription ?? item.LabelDetail ?? item.Detail))
            {
                var detail = new TextBlock
                {
                    Text = item.LabelDescription ?? item.LabelDetail ?? item.Detail,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(_suggestMutedForeground),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 145,
                };
                Grid.SetColumn(detail, 2);
                columns.Children.Add(detail);
            }
            row.Child = columns;
            if (!string.IsNullOrEmpty(item.Documentation))
                ToolTipService.SetToolTip(row, item.Documentation);
            AutomationProperties.SetName(row, $"{item.Label}, {item.Kind}");
            row.PointerMoved += (_, e) =>
            {
                // WinUI can raise PointerMoved when a newly opened popup appears under a
                // stationary cursor. Only real pointer motion is allowed to change the
                // keyboard-selected completion item.
                if (!HasMouseActuallyMoved(e) || _suggestSelection.SelectedIndex == index || !IsSuggestWidgetVisible) return;
                _suggestSelection.Select(index);
                UpdateSuggestSelectionVisuals(ensureVisible: false);
            };
            row.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                _suggestSelection.Select(index);
                _ = CommitSelectedSuggestionAsync();
            };
            _suggestRowBorders.Add((index, row));
            _suggestRowPanel.Children.Add(row);
        }
        if (last < _suggestItems.Count)
            _suggestRowPanel.Children.Add(new Border { Height = SuggestRowHeight * (_suggestItems.Count - last) });
        UpdateSuggestSelectionVisuals(ensureVisible: false);
    }

    private void UpdateSuggestSelectionVisuals(bool ensureVisible)
    {
        foreach (var (index, row) in _suggestRowBorders)
            row.Background = index == _suggestSelection.SelectedIndex
                ? new SolidColorBrush(_suggestSelectedBackground)
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        if (!ensureVisible || _suggestScroller is null || _suggestSelection.SelectedIndex < 0) return;
        var top = SuggestRowHeight * _suggestSelection.SelectedIndex;
        var bottom = top + SuggestRowHeight;
        var offset = _suggestScroller.VerticalOffset;
        var visibleHeight = _suggestScroller.ViewportHeight;
        if (visibleHeight <= 0) return;
        if (top < offset)
            _suggestScroller.ChangeView(null, top, null, disableAnimation: true);
        else if (bottom > offset + visibleHeight)
            _suggestScroller.ChangeView(null, Math.Max(0, bottom - visibleHeight), null, disableAnimation: true);
    }

    // VS Code symbolIcon colors; high contrast overrides them at the call site.
    private static (string Symbol, Color Foreground) SuggestKindSymbol(CompletionItemKind kind)
    {
        var color = kind switch
        {
            CompletionItemKind.Method or CompletionItemKind.Function or CompletionItemKind.Constructor => Color.FromArgb(255, 180, 149, 211),
            CompletionItemKind.Class or CompletionItemKind.Struct or CompletionItemKind.Enum or CompletionItemKind.Event => Color.FromArgb(255, 238, 194, 110),
            CompletionItemKind.Field or CompletionItemKind.Variable or CompletionItemKind.Interface or CompletionItemKind.EnumMember => Color.FromArgb(255, 117, 187, 222),
            _ => Color.FromArgb(255, 204, 204, 204),
        };
        return (CodiconPresenter.Name(kind), color);
    }

    private void AppendLabelWithMatchHighlight(TextBlock block, string label, IReadOnlyList<int> matches)
    {
        var positions = matches.ToHashSet();
        for (var i = 0; i < label.Length;)
        {
            var matched = positions.Contains(i); var end = i + 1;
            while (end < label.Length && positions.Contains(end) == matched) end++;
            block.Inlines.Add(new Run
            {
                Text = label[i..end], FontWeight = matched ? FontWeights.Bold : FontWeights.Normal,
                Foreground = new SolidColorBrush(matched ? _suggestHighlightForeground : _suggestForeground),
            });
            i = end;
        }
    }

    private string SuggestWordPrefix(MonacoPosition position)
    {
        if (position.LineNumber < 1 || position.LineNumber > (int)_model.LineCount) return string.Empty;
        var line = _model.GetLineContent(position.LineNumber);
        var end = Math.Clamp(position.Column - 1, 0, line.Length);
        var start = end;
        while (start > 0 && (char.IsLetterOrDigit(line[start - 1]) || line[start - 1] is '_' or '$'))
            start--;
        return line[start..end];
    }

    private void ApplySuggestWidgetPalette(bool highContrast, ElementTheme theme)
    {
        if (_suggestPopup is null) return;
        Color background, border;
        if (highContrast)
        {
            background = _uiSettings.GetColorValue(UIColorType.Background);
            border = _uiSettings.GetColorValue(UIColorType.Foreground);
            _suggestForeground = border;
            _suggestMutedForeground = border;
            _suggestHighlightForeground = border;
            _suggestSelectedBackground = _uiSettings.GetColorValue(UIColorType.Accent);
        }
        else if (theme == ElementTheme.Light)
        {
            background = Color.FromArgb(255, 248, 248, 248);
            border = Color.FromArgb(255, 206, 206, 206);
            _suggestForeground = Color.FromArgb(255, 51, 51, 51);
            _suggestMutedForeground = Color.FromArgb(255, 113, 113, 113);
            _suggestHighlightForeground = Color.FromArgb(255, 0, 105, 204);
            _suggestSelectedBackground = Color.FromArgb(255, 216, 232, 247);
        }
        else
        {
            background = Color.FromArgb(255, 37, 37, 38); // VS Code Dark+
            border = Color.FromArgb(255, 69, 69, 69);
            _suggestForeground = Color.FromArgb(255, 212, 212, 212);
            _suggestMutedForeground = Color.FromArgb(255, 155, 155, 155);
            _suggestHighlightForeground = Color.FromArgb(255, 255, 255, 255);
            _suggestSelectedBackground = Color.FromArgb(255, 9, 71, 113);
        }
        _suggestPopup.Background = new SolidColorBrush(background);
        _suggestPopup.BorderBrush = new SolidColorBrush(border);
        if (IsSuggestWidgetVisible)
        {
            _suggestRenderedStart = _suggestRenderedEnd = -1;
            RebuildSuggestRows();
        }
        ApplyParameterHintsPalette();
    }
}
