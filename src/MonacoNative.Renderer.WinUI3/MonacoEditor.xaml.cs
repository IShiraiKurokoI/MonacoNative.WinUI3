using System.Diagnostics;
using System.Globalization;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using MonacoNative.Core;
using MonacoNative.I18n;
using MonacoNative.Input;
using MonacoNative.Language;
using MonacoNative.Language.BracketPairs;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.ViewManagement;

namespace MonacoNative.Renderer.WinUI3;

/// <summary>
/// WinUI renderer. Text layout is native-projected, measured by DirectWrite through
/// CanvasTextLayout, and painted into a low-latency CanvasSwapChainPanel.
/// </summary>
public sealed partial class MonacoEditor : UserControl, IDisposable
{
    [Flags]
    private enum RenderDirty
    {
        None = 0,
        Text = 1 << 0,
        Selection = 1 << 1,
        Caret = 1 << 2,
        Diagnostics = 1 << 3,
        Decorations = 1 << 4,
        Brackets = 1 << 5,
        Overview = 1 << 6,
        All = Text | Selection | Caret | Diagnostics | Decorations | Brackets | Overview,
    }

    private const double GutterWidth = 58.0;
    private const double LeftPadding = 10.0;
    private const double RightPadding = 14.0;
    private const double VerticalPadding = 8.0;
    private const double DefaultLineHeight = 22.0;
    private const double MinimapWidth = 86.0;
    private const double OverviewRulerWidth = 8.0;
    private const int TabSize = 4;

    private Color _backgroundColor = Color.FromArgb(255, 30, 30, 30);
    private Color _foregroundColor = Color.FromArgb(255, 212, 212, 212);
    private Color _gutterColor = Color.FromArgb(255, 133, 133, 133);
    private Color _selectionColor = Color.FromArgb(150, 38, 79, 120);
    private Color _currentLineColor = Color.FromArgb(55, 255, 255, 255);
    private Color _caretColor = Color.FromArgb(255, 255, 255, 255);
    private Color _compositionColor = Color.FromArgb(255, 255, 255, 255);
    private Color _compositionTargetColor = Color.FromArgb(255, 86, 156, 214);
    private Color _whitespaceColor = Color.FromArgb(120, 150, 150, 150);
    private Color _indentGuideColor = Color.FromArgb(90, 120, 120, 120);
    private Color _overviewColor = Color.FromArgb(210, 0, 122, 204);
    private Color _minimapBackgroundColor = Color.FromArgb(255, 25, 25, 25);
    private Color _minimapForegroundColor = Color.FromArgb(150, 135, 135, 135);
    // Monaco/VS Code exposes six bracket-pair highlight levels. The concrete colors are
    // renderer/theme details, but keeping six slots preserves the upstream nesting cycle.
    private readonly Color[] _bracketColors =
    {
        Color.FromArgb(255, 255, 215, 0),
        Color.FromArgb(255, 218, 112, 214),
        Color.FromArgb(255, 23, 159, 255),
        Color.FromArgb(255, 79, 177, 188),
        Color.FromArgb(255, 151, 194, 108),
        Color.FromArgb(255, 171, 178, 192),
    };

    private readonly MonacoTextModel _model = new("", "cpp");
    private readonly MonacoViewModel _view;
    private readonly DispatcherTimer _caretTimer;
    private readonly AccessibilitySettings _accessibilitySettings = new();
    private readonly UISettings _uiSettings = new();
    private readonly DispatcherTimer _accessibilityTimer;
    private bool? _lastHighContrast;
    private string _lastHighContrastScheme = string.Empty;
    private ElementTheme _lastTheme = ElementTheme.Default;
    private readonly MinimapRasterizer _minimap = new();
    private readonly RendererPerformanceCounters _rendererPerformance = new();
    private readonly EditorMethodProfiler _methodProfiler = new();
    private InputCursor? _textPointerCursor;
    private InputCursor? _arrowPointerCursor;
    private InputCursor? _minimapPointerCursor;
    private EditorPointerCursorKind? _currentPointerCursor;
    private readonly BracketPairsTree _bracketPairs;
    private IReadOnlyDictionary<int, IReadOnlyList<BracketTreeInfo>> _visibleBracketsByModelLine = new Dictionary<int, IReadOnlyList<BracketTreeInfo>>();
    private readonly MonacoLocalizationService _localization = new();
    private readonly EditorCommandService _commands;
    private IEditorTextInputController? _textInput;
    private readonly MenuFlyout _contextMenu = new();
    private bool _executingContextMenuCommand;
    /// <summary>Raised when a context-menu action changes an editor view option.</summary>
    public event EventHandler? EditorViewOptionsChanged;
    /// <summary>XAML command policies: context-menu visibility and shortcut execution.</summary>
    public System.Collections.ObjectModel.ObservableCollection<EditorContextMenuItemOptions> ContextMenuItemOptions
    {
        get => (System.Collections.ObjectModel.ObservableCollection<EditorContextMenuItemOptions>)GetValue(ContextMenuItemOptionsProperty);
        set => SetValue(ContextMenuItemOptionsProperty, value);
    }
    public static readonly DependencyProperty ContextMenuItemOptionsProperty = DependencyProperty.Register(
        nameof(ContextMenuItemOptions),
        typeof(System.Collections.ObjectModel.ObservableCollection<EditorContextMenuItemOptions>),
        typeof(MonacoEditor), new PropertyMetadata(null));
    private readonly DispatcherTimer _chordTimer;
    private readonly DispatcherTimer _decorationIdleTimer;

    private CanvasDevice? _device;
    private CanvasSwapChain? _swapChain;
    private CanvasRenderTarget? _textLayer;
    private readonly SelectionViewPart _selectionViewPart;
    private readonly DecorationsViewPart _decorationsViewPart;
    private readonly DiagnosticsViewPart _diagnosticsViewPart;
    private readonly BracketViewPart _bracketViewPart;
    private readonly OverviewViewPart _overviewViewPart;
    private readonly CaretViewPart _caretViewPart;
    private float _renderLayerWidth;
    private float _renderLayerHeight;
    private float _renderLayerDpi;
    private CanvasTextFormat? _textFormat;
    private DirectWriteTextMeasurer? _measurer;
    private LineLayoutCache? _layoutCache;

    private MonacoPosition _anchor = new(1, 1);
    private MonacoPosition _active = new(1, 1);
    private bool _dragSelecting;
    private bool _caretVisible = true;
    private bool _loaded;
    private bool _wordWrap = true;
    private bool _showWhitespace;
    private bool _showIndentGuides = true;
    private bool _showMinimap = true;
    private bool _showOverviewRuler = true;
    private bool _bracketPairColorization = true;
    private double _lineHeight = DefaultLineHeight;
    private double _maxObservedLineWidth;
    private ulong _lastRenderedVersion;
    private float? _preferredCaretX;
    private bool _disposed;
    private bool _modelRefreshQueued;
    private int _lastKnownViewLineCount;
    private bool _renderQueued;
    private RenderDirty _pendingRenderDirty = RenderDirty.All;
    private int _decorationDirtyFromLine = 1;
    private bool _isReadOnly;
    private readonly Utf16CharacterAccumulator _fallbackCharacterAccumulator = new();

    public MonacoEditor()
    {
        InitializeComponent();
        ContextMenuItemOptions = new System.Collections.ObjectModel.ObservableCollection<EditorContextMenuItemOptions>();
        _languages = new MonacoLanguageService(registerBuiltins: true);
        _languageDocument = new MonacoLanguageDocument(_model);
        _languageTokens = new IncrementalTokenizationCache(_languageDocument, _languages);
        _bracketPairs = new BracketPairsTree(_languageDocument, _languageTokens, _languages.Languages);
        _languages.Languages.TokenizationChanged += Languages_TokenizationChanged;
        _languages.Languages.LanguageConfigurationChanged += Languages_LanguageConfigurationChanged;
        _languages.DiagnosticsChanged += Languages_DiagnosticsChanged;
        _commands = new EditorCommandService(_localization);
        _selectionViewPart = new SelectionViewPart(
            (ds, context) =>
            {
                DrawCurrentLine(ds, context.ScrollTop);
                DrawSelection(ds, context.FirstViewLine, context.LastViewLine, context.ScrollTop, context.ScrollLeft);
            });
        _decorationsViewPart = new DecorationsViewPart(
            (ds, context) =>
            {
                DrawDiffDecorations(ds, context.FirstViewLine, context.LastViewLine, context.ScrollTop, context.ScrollLeft);
            });
        _diagnosticsViewPart = new DiagnosticsViewPart(
            (ds, context) =>
            {
                DrawLanguageDiagnostics(ds, context.FirstViewLine, context.LastViewLine, context.ScrollTop, context.ScrollLeft);
            });
        _bracketViewPart = new BracketViewPart(
            (ds, context) =>
            {
                DrawBracketDecorations(ds, context.FirstViewLine, context.LastViewLine, context.ScrollTop, context.ScrollLeft);
            });
        _overviewViewPart = new OverviewViewPart(
            (ds, context) =>
            {
                DrawOverviewRuler(ds, context.ScrollTop);
            });
        _caretViewPart = new CaretViewPart(
            (ds, context) =>
            {
                DrawCompositionDecorations(ds, context.FirstViewLine, context.LastViewLine, context.ScrollTop, context.ScrollLeft);
                DrawCaret(ds, context.ScrollTop, context.ScrollLeft);
            });
        _view = new MonacoViewModel(_model);
        _view.SetLineHeight(_lineHeight);
        _lastKnownViewLineCount = _view.ViewLineCount;
        _model.Changed += Model_Changed;
        _accessibilityTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _accessibilityTimer.Tick += AccessibilityTimer_Tick;
        RefreshAccessibilityPalette(force: true);
        ActualThemeChanged += (_, _) =>
        {
            if (_disposed)
                return;
            RefreshAccessibilityPalette(force: true);
        };
        _caretTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _caretTimer.Tick += (_, _) =>
        {
            if (!_loaded || !HasEditorInputFocus)
                return;
            _caretVisible = !_caretVisible;
            Render(RenderDirty.Caret);
        };
        _chordTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _chordTimer.Tick += (_, _) =>
        {
            _chordTimer.Stop();
            _commands.ResetChord();
        };
        _decorationIdleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _decorationIdleTimer.Tick += DecorationIdleTimer_Tick;
        _contributionRefreshTimer.Tick += ContributionRefreshTimer_Tick;
        _lspRefreshTimer.Tick += LspRefreshTimer_Tick;
        _foldRefreshTimer.Tick += FoldRefreshTimer_Tick;
        if (Resources["EditorMenuFlyoutPresenterStyle"] is Style menuPresenterStyle)
            _contextMenu.MenuFlyoutPresenterStyle = menuPresenterStyle;
        _contextMenu.Opening += ContextMenu_Opening;
        _contextMenu.Closed += ContextMenu_Closed;
        _localization.CultureChanged += Localization_CultureChanged;
        SurfaceRoot.ContextFlyout = _contextMenu;
        // Descendant ScrollViewer input can mark PointerMoved handled. Track hover even then.
        SurfaceRoot.AddHandler(UIElement.PointerMovedEvent,
            new PointerEventHandler(SurfaceRoot_PointerMoved), true);
        RegisterDefaultCommands();
        InitializeContributions();
        RegisterContributionCommands();
        _commands.IsCommandAllowed = id => GetCommandAvailability(id) != EditorCommandAvailability.Disabled;
        UpdateCommandContext();
    }

    public event EventHandler? ModelChanged;
    public event EventHandler? SelectionChanged;
    public event EventHandler? UiLanguageChanged;
    public event EventHandler? ImeStateChanged;
    public event EventHandler? ViewportChanged;
    public event EventHandler? PointerCursorChanged;

    public MonacoTextModel Model => _model;
    public MonacoViewModel ViewModel => _view;
    public ulong VersionId => _model.VersionId;
    public nuint Length => _model.Length;
    public nuint LineCount => _model.LineCount;
    public bool CanUndo => _model.CanUndo;
    public bool CanRedo => _model.CanRedo;
    public double VerticalScrollOffset => ScrollHost.VerticalOffset;
    internal RendererPerformanceSnapshot CaptureRendererPerformanceSnapshot(bool reset = false) =>
        _rendererPerformance.Snapshot(reset);
    /// <summary>Disabled by default: captures inclusive/self time and managed allocations for instrumented methods.</summary>
    public bool MethodProfilingEnabled
    {
        get => _methodProfiler.Enabled;
        set => _methodProfiler.Enabled = value;
    }

    /// <summary>Export method-level UI-thread measurements to JSON and CSV (paths returned).</summary>
    public (string Json, string Csv) ExportMethodProfileReport(string directory, bool reset = false) =>
        _methodProfiler.Export(directory, reset);

    public string PointerCursorState => (_currentPointerCursor ?? EditorPointerCursorKind.Arrow).ToString();
    public double HorizontalScrollOffset => ScrollHost.HorizontalOffset;
    public double ScrollableHeight => ScrollHost.ScrollableHeight;
    public double ScrollableWidth => ScrollHost.ScrollableWidth;
    public double LineHeight => _lineHeight;
    public double ContentVerticalPadding => VerticalPadding;
    public double TextGutterWidth => GutterWidth + LeftPadding;

    public string UiLanguage
    {
        get => _localization.CultureName;
        set => _localization.SetCulture(value);
    }

    public IReadOnlyList<string> SupportedUiLanguages => MonacoLocalizationService.SupportedCultures;
    public string GetLocalizedString(string key) => _localization.GetString(key);
    public bool IsImeAvailable => _textInput?.IsAvailable == true;
    public bool IsImeComposing => _textInput?.IsComposing == true;
    public ImeInputCapability ImeCapability => _textInput?.Capability ?? ImeInputCapability.BasicCharacterFallback;
    public ImeInputBackend ActiveInputBackend => _textInput?.Backend ?? ImeInputBackend.None;
    public string InputLanguageTag => _textInput?.InputLanguageTag ?? string.Empty;

    public bool IsReadOnly
    {
        get => _isReadOnly;
        set
        {
            if (_isReadOnly == value) return;
            _isReadOnly = value;
            _textInput?.SetReadOnly(value);
            UpdateCommandContext();
        }
    }

    public string LanguageId
    {
        get => _model.LanguageId;
        set
        {
            if (string.Equals(_model.LanguageId, value, StringComparison.OrdinalIgnoreCase)) return;
            _model.LanguageId = value; // Raises a full model invalidation event.
            _languageTokens.Clear();
            _bracketPairs.HandleLanguageConfigurationChanged(value);
            _layoutCache?.Clear();
            ScheduleDecorationRefresh(1, immediate: true);
            Render(RenderDirty.Text | RenderDirty.Brackets);
            UpdateCommandContext();
        }
    }

    public string Text
    {
        get => _model.Text;
        set
        {
            _model.Text = value ?? string.Empty;
            ClearSecondarySelections();
            _anchor = _active = new MonacoPosition(1, 1);
            _preferredCaretX = null;
            AfterProjectionChanged(true);
        }
    }

    public MonacoEndOfLine EndOfLine
    {
        get => _model.EndOfLine;
        set
        {
            if (_model.EndOfLine == value)
                return;
            _model.EndOfLine = value;
            AfterProjectionChanged(true);
        }
    }

    public bool WordWrap
    {
        get => _wordWrap;
        set
        {
            if (_wordWrap == value)
                return;
            _wordWrap = value;
            UpdateCommandContext();
            UpdateProjectionMetrics();
        }
    }

    public bool ShowWhitespace
    {
        get => _showWhitespace;
        set { if (_showWhitespace == value) return; _showWhitespace = value; UpdateCommandContext(); Render(RenderDirty.Text); }
    }

    public bool ShowIndentGuides
    {
        get => _showIndentGuides;
        set { if (_showIndentGuides == value) return; _showIndentGuides = value; Render(RenderDirty.Text); }
    }

    public bool ShowMinimap
    {
        get => _showMinimap;
        set
        {
            if (_showMinimap == value) return;
            _showMinimap = value;
            UpdateFindWidgetPlacement();
            if (value) ScheduleDecorationRefresh(1, immediate: true);
            UpdateCommandContext();
            UpdateProjectionMetrics();
        }
    }

    public bool ShowOverviewRuler
    {
        get => _showOverviewRuler;
        set { if (_showOverviewRuler == value) return; _showOverviewRuler = value; UpdateFindWidgetPlacement(); UpdateProjectionMetrics(); }
    }

    public bool BracketPairColorization
    {
        get => _bracketPairColorization;
        set
        {
            if (_bracketPairColorization == value) return;
            _bracketPairColorization = value;
            _layoutCache?.Clear();
            if (value) ScheduleDecorationRefresh(1, immediate: true);
            Render(RenderDirty.Text);
        }
    }

    public MonacoRange Selection
    {
        get => new(_anchor.LineNumber, _anchor.Column, _active.LineNumber, _active.Column);
        set
        {
            ClearSecondarySelections();
            _anchor = Normalize(value.StartLineNumber, value.StartColumn);
            _active = Normalize(value.EndLineNumber, value.EndColumn);
            _preferredCaretX = null;
            UpdateCommandContext();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            OnContributionsSelectionChanged();
            RaiseAccessibilitySelectionChanged();
            _textInput?.NotifySelectionChanged();
            _textInput?.NotifyLayoutChanged();
            RevealCaret();
            ResetCaretBlink();
            Render(RenderDirty.Selection | RenderDirty.Brackets | RenderDirty.Overview | RenderDirty.Caret);
        }
    }

    public string GetLineContent(int lineNumber) => _model.GetLineContent(lineNumber);
    public int GetLineMaxColumn(int lineNumber) => _model.GetLineMaxColumn(lineNumber);
    public IReadOnlyList<MonacoRange> FindMatches(string query, bool matchCase = true, bool wholeWord = false) =>
        _model.FindMatches(query, matchCase, wholeWord);

    public nuint ReplaceAll(string query, string replacement, bool matchCase = true, bool wholeWord = false, nuint limit = 1000)
    {
        var count = _model.ReplaceAll(query, replacement, matchCase, wholeWord, limit);
        if (count > 0)
            AfterProjectionChanged(true);
        return count;
    }

    public MonacoBracketMatch? FindMatchingBracket(MonacoPosition position)
    {
        var match = _bracketPairs.FindMatchingBracket(new LanguagePosition(position.LineNumber, position.Column));
        if (match is null) return null;
        return new MonacoBracketMatch(
            new MonacoPosition(match.Bracket.StartLineNumber, match.Bracket.StartColumn),
            new MonacoPosition(match.Match.StartLineNumber, match.Match.StartColumn));
    }

    public void SetScrollPosition(double? horizontalOffset, double? verticalOffset, bool disableAnimation = true)
    {
        ScrollHost.ChangeView(horizontalOffset, verticalOffset, null, disableAnimation);
    }

    public void RevealRange(MonacoRange range, MonacoRevealStrategy strategy = MonacoRevealStrategy.Minimal, double padding = 0)
    {
        var top = _view.GetScrollTopToReveal(
            range,
            Math.Max(1, ActualHeight - (VerticalPadding * 2)),
            ScrollHost.VerticalOffset,
            strategy,
            padding);
        ScrollHost.ChangeView(null, top, null, true);
    }

    public void Undo()
    {
        var anchorOffset = _model.GetOffsetAt(_anchor);
        var activeOffset = _model.GetOffsetAt(_active);
        if (!_model.Undo())
            return;
        RestoreSelectionOffsets(anchorOffset, activeOffset);
        AfterProjectionChanged(true);
    }

    public void Redo()
    {
        var anchorOffset = _model.GetOffsetAt(_anchor);
        var activeOffset = _model.GetOffsetAt(_active);
        if (!_model.Redo())
            return;
        RestoreSelectionOffsets(anchorOffset, activeOffset);
        AfterProjectionChanged(true);
    }

    public void RefreshProjection()
    {
        _view.RebuildWithTextMeasurer();
        _layoutCache?.Clear();
        _maxObservedLineWidth = 0;
        AfterProjectionChanged(false);
    }

    private void Model_Changed(object? sender, MonacoModelChangedEventArgs e)
    {
        using var methodSample = _methodProfiler.Measure("Model_Changed");
        if (e.IsFullInvalidation)
        {
            _languageTokens.Clear();
            _bracketPairs.HandleContentChanged(new TextEditInfo(BracketLength.Zero, BracketLength.Zero, BracketLength.Zero), fullInvalidation: true);
        }
        else
        {
            _languageTokens.ApplyModelChange(e.StartLineNumber, e.OldEndLineNumber, e.NewEndLineNumber);
            var start = BracketLength.From(e.StartLineNumber - 1, e.StartColumn - 1);
            var oldEnd = BracketLength.From(e.OldEndLineNumber - 1, e.OldEndColumn - 1);
            var newEnd = BracketLength.From(e.NewEndLineNumber - 1, e.NewEndColumn - 1);
            _bracketPairs.HandleContentChanged(new TextEditInfo(start, oldEnd, BracketLength.Diff(start.LineCount, start.ColumnCount, newEnd.LineCount, newEnd.ColumnCount)), fullInvalidation: false);
        }
        var previousViewLineCount = _lastKnownViewLineCount > 0 ? _lastKnownViewLineCount : _view.ViewLineCount;
        var currentViewLineCount = _view.ViewLineCount;
        if (_layoutCache is not null)
        {
            if (e.IsFullInvalidation)
            {
                _layoutCache.Clear();
            }
            else
            {
                var startModelLine = Math.Clamp(e.StartLineNumber, 1, checked((int)_model.LineCount));
                var endModelLine = Math.Clamp(e.NewEndLineNumber, startModelLine, checked((int)_model.LineCount));
                var firstViewLine = _view.ModelToView(new MonacoPosition(startModelLine, 1)).LineNumber;
                var lastViewLine = _view.ModelToView(new MonacoPosition(endModelLine, _model.GetLineMaxColumn(endModelLine))).LineNumber;
                var newAffectedViewLines = Math.Max(0, lastViewLine - firstViewLine + 1);
                var viewLineDelta = currentViewLineCount - previousViewLineCount;
                var oldAffectedViewLines = newAffectedViewLines - viewLineDelta;
                if (oldAffectedViewLines >= 0)
                    _layoutCache.ApplySplice(firstViewLine, oldAffectedViewLines, newAffectedViewLines);
                else
                    _layoutCache.InvalidateFrom(firstViewLine);
            }
        }
        _lastKnownViewLineCount = currentViewLineCount;
        _maxObservedLineWidth = 0;
        RaiseAccessibilityTextChanged();
        _minimap.NotifyModelChanged(e, checked((int)_model.LineCount));
        ScheduleDecorationRefresh(Math.Max(1, e.StartLineNumber));
        OnFoldingModelChanged(e);
        ScheduleFoldingRefresh();
        if (_textInput is { IsAvailable: true } textInput && !textInput.IsApplyingServerUpdate)
        {
            // MonacoTextModel raises Changed synchronously from Replace(). Queue the
            // text-service notification so editor-owned selection updates complete first.
            var change = e;
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                if (!_disposed)
                    _textInput?.NotifyModelChanged(change);
            });
        }
        UpdateCommandContext();
        ModelChanged?.Invoke(this, EventArgs.Empty);
        OnContributionsModelChanged();
        ScheduleLspRefresh();

        if (!_loaded || _modelRefreshQueued)
            return;
        _modelRefreshQueued = true;
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed || !_modelRefreshQueued)
                return;
            _modelRefreshQueued = false;
            UpdateExtent();
            Render();
        });
    }

    private void ScheduleDecorationRefresh(int modelLine, bool immediate = false)
    {
        using var methodSample = _methodProfiler.Measure("ScheduleDecorationRefresh");
        _decorationDirtyFromLine = Math.Min(_decorationDirtyFromLine, Math.Max(1, modelLine));
        if (!_loaded || _disposed)
            return;

        _decorationIdleTimer.Stop();
        _decorationIdleTimer.Interval = TimeSpan.FromMilliseconds(immediate ? 16 : 140);
        _decorationIdleTimer.Start();
    }

    private void DecorationIdleTimer_Tick(object? sender, object e)
    {
        using var methodSample = _methodProfiler.Measure("DecorationIdleTimer_Tick");
        _decorationIdleTimer.Stop();
        if (_disposed || !_loaded)
            return;

        var requestedVersion = _model.VersionId;
        var dirtyLine = _decorationDirtyFromLine == int.MaxValue ? 1 : _decorationDirtyFromLine;
        _decorationDirtyFromLine = int.MaxValue;

        // The full-document bracket/minimap pass is intentionally low-priority. Incoming
        // keyboard/pointer work can run first; if the document changes before this callback
        // begins, discard the stale request and debounce again.
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_disposed || !_loaded)
                return;
            if (_model.VersionId != requestedVersion)
            {
                ScheduleDecorationRefresh(dirtyLine);
                return;
            }

            // Bracket matching is a model semantic and must become token-aware even when
            // bracket-pair colorization is disabled. Monaco keeps matching independent of
            // the colorization setting; only the visual query is gated by that option.
            _bracketPairs.CompleteTokenization();
            if (_showMinimap)
                _minimap.Ensure(_model);

            if (_layoutCache is not null && _view.ViewLineCount > 0)
            {
                var line = Math.Clamp(dirtyLine, 1, checked((int)_model.LineCount));
                var firstViewLine = _view.ModelToView(new MonacoPosition(line, 1)).LineNumber;
                _layoutCache.InvalidateFrom(firstViewLine);
            }
            Render(RenderDirty.Text | RenderDirty.Brackets);
        });
    }

    private void AccessibilityTimer_Tick(object? sender, object e)
    {
        RefreshAccessibilityPalette();
    }

    private void RefreshAccessibilityPalette(bool force = false)
    {
        if (_disposed)
            return;

        // AccessibilitySettings itself is supported for desktop apps, but Microsoft
        // explicitly documents HighContrastChanged as unsupported there. Poll the
        // supported state properties at low frequency instead of subscribing to that
        // event. The timer runs on the UI dispatcher, so palette/layout updates stay
        // on the XAML thread.
        var highContrast = _accessibilitySettings.HighContrast;
        var scheme = highContrast ? (_accessibilitySettings.HighContrastScheme ?? string.Empty) : string.Empty;
        // ActualTheme can lag one frame behind a RequestedTheme change on the host.
        // Explicit editor requests take precedence; Default inherits the effective theme.
        var theme = RequestedTheme == ElementTheme.Default ? ActualTheme : RequestedTheme;
        if (!force &&
            _lastHighContrast == highContrast &&
            string.Equals(_lastHighContrastScheme, scheme, StringComparison.Ordinal) &&
            _lastTheme == theme)
        {
            return;
        }

        _lastHighContrast = highContrast;
        _lastHighContrastScheme = scheme;
        _lastTheme = theme;
        UpdatePalette(highContrast, theme);
        _layoutCache?.Clear();
        if (_loaded)
            Render(RenderDirty.All);
    }

    /// <summary>Force synchronization after host changes RequestedTheme.</summary>
    public void RefreshEditorTheme() => RefreshAccessibilityPalette(force: true);

    private void UpdatePalette(bool highContrast, ElementTheme theme)
    {
        Color chromeBorder;
        Color stickyBackground;
        Color widgetBackground;
        Color widgetBorder;

        if (highContrast)
        {
            var background = _uiSettings.GetColorValue(UIColorType.Background);
            var foreground = _uiSettings.GetColorValue(UIColorType.Foreground);
            var accent = _uiSettings.GetColorValue(UIColorType.Accent);
            _backgroundColor = background;
            _foregroundColor = foreground;
            _gutterColor = foreground;
            _selectionColor = Color.FromArgb(145, accent.R, accent.G, accent.B);
            _currentLineColor = Color.FromArgb(40, foreground.R, foreground.G, foreground.B);
            _caretColor = foreground;
            _compositionColor = foreground;
            _compositionTargetColor = accent;
            _whitespaceColor = Color.FromArgb(180, foreground.R, foreground.G, foreground.B);
            _indentGuideColor = Color.FromArgb(135, foreground.R, foreground.G, foreground.B);
            _overviewColor = accent;
            _minimapBackgroundColor = background;
            _minimapForegroundColor = Color.FromArgb(190, foreground.R, foreground.G, foreground.B);
            _bracketColors[0] = foreground;
            _bracketColors[1] = accent;
            _bracketColors[2] = foreground;
            _bracketColors[3] = accent;
            _bracketColors[4] = foreground;
            _bracketColors[5] = accent;
            chromeBorder = foreground;
            stickyBackground = background;
            widgetBackground = background;
            widgetBorder = foreground;
            UpdateLanguagePalette(highContrast: true);
        }
        else if (theme == ElementTheme.Light)
        {
            _backgroundColor = Color.FromArgb(255, 255, 255, 255);
            _foregroundColor = Color.FromArgb(255, 36, 36, 36);
            _gutterColor = Color.FromArgb(255, 110, 110, 110);
            _selectionColor = Color.FromArgb(140, 173, 214, 255);
            _currentLineColor = Color.FromArgb(36, 0, 0, 0);
            _caretColor = Color.FromArgb(255, 17, 17, 17);
            _compositionColor = Color.FromArgb(255, 36, 36, 36);
            _compositionTargetColor = Color.FromArgb(255, 0, 102, 204);
            _whitespaceColor = Color.FromArgb(110, 150, 150, 150);
            _indentGuideColor = Color.FromArgb(90, 185, 185, 185);
            _overviewColor = Color.FromArgb(210, 0, 102, 204);
            _minimapBackgroundColor = Color.FromArgb(255, 248, 248, 248);
            _minimapForegroundColor = Color.FromArgb(145, 120, 120, 120);
            _bracketColors[0] = Color.FromArgb(255, 4, 81, 165);
            _bracketColors[1] = Color.FromArgb(255, 121, 94, 38);
            _bracketColors[2] = Color.FromArgb(255, 125, 56, 135);
            _bracketColors[3] = Color.FromArgb(255, 0, 122, 122);
            _bracketColors[4] = Color.FromArgb(255, 61, 111, 61);
            _bracketColors[5] = Color.FromArgb(255, 107, 61, 61);
            chromeBorder = Color.FromArgb(255, 215, 215, 220);
            stickyBackground = Color.FromArgb(242, 255, 255, 255);
            widgetBackground = Color.FromArgb(246, 255, 255, 255);
            widgetBorder = Color.FromArgb(255, 200, 200, 205);
            UpdateLanguagePalette(highContrast: false);
        }
        else
        {
            _backgroundColor = Color.FromArgb(255, 30, 30, 30);
            _foregroundColor = Color.FromArgb(255, 212, 212, 212);
            _gutterColor = Color.FromArgb(255, 133, 133, 133);
            _selectionColor = Color.FromArgb(150, 38, 79, 120);
            _currentLineColor = Color.FromArgb(55, 255, 255, 255);
            _caretColor = Color.FromArgb(255, 255, 255, 255);
            _compositionColor = Color.FromArgb(255, 212, 212, 212);
            _compositionTargetColor = Color.FromArgb(255, 86, 156, 214);
            _whitespaceColor = Color.FromArgb(120, 150, 150, 150);
            _indentGuideColor = Color.FromArgb(90, 120, 120, 120);
            _overviewColor = Color.FromArgb(210, 0, 122, 204);
            _minimapBackgroundColor = Color.FromArgb(255, 25, 25, 25);
            _minimapForegroundColor = Color.FromArgb(150, 135, 135, 135);
            _bracketColors[0] = Color.FromArgb(255, 255, 215, 0);
            _bracketColors[1] = Color.FromArgb(255, 218, 112, 214);
            _bracketColors[2] = Color.FromArgb(255, 23, 159, 255);
            _bracketColors[3] = Color.FromArgb(255, 79, 177, 188);
            _bracketColors[4] = Color.FromArgb(255, 151, 194, 108);
            _bracketColors[5] = Color.FromArgb(255, 171, 178, 192);
            chromeBorder = Color.FromArgb(255, 63, 63, 70);
            stickyBackground = Color.FromArgb(242, 30, 30, 30);
            widgetBackground = Color.FromArgb(246, 37, 37, 38);
            widgetBorder = Color.FromArgb(255, 88, 88, 88);
            UpdateLanguagePalette(highContrast: false);
        }

        EditorChrome.Background = new SolidColorBrush(_backgroundColor);
        EditorChrome.BorderBrush = new SolidColorBrush(chromeBorder);
        StickyScrollHost.Background = new SolidColorBrush(stickyBackground);
        StickyScrollHost.BorderBrush = new SolidColorBrush(chromeBorder);
        FindWidget.Background = new SolidColorBrush(widgetBackground);
        FindWidget.BorderBrush = new SolidColorBrush(widgetBorder);
    }

    private IEditorTextInputController CreateTextInputController()
    {
        // The production runtime is TSF-only now. If native initialization fails we still keep
        // the controller instance alive so the editor can continue using its built-in basic
        // CharacterReceived path instead of reviving the old hidden TextBox proxy.
        var candidate = new NativeTsfInputController(
            _model,
            GetNativeTsfHwnd,
            GetNativeTsfSelection,
            SetNativeTsfSelection,
            NativeTsfPointToAcp,
            NativeTsfTextRect,
            NativeTsfScreenRect,
            NativeTsfHasLayout,
            DispatcherQueue);
        _ = candidate.Initialize();
        return candidate;
    }

    private void AttachTextInputController(IEditorTextInputController controller, bool restoreFocus)
    {
        _textInput = controller;
        _textInput.InputLanguageChanged += TextInput_StateChanged;
        _textInput.CompositionStateChanged += TextInput_StateChanged;
        _textInput.SetReadOnly(_isReadOnly);

        if (!restoreFocus)
            return;

        // Native TSF owns the editor HWND and therefore requires shell focus on the editor.
        if (!_textInput.TreatAdapterFocusAsEditorFocus && FocusState == FocusState.Unfocused)
            Focus(FocusState.Programmatic);
        _textInput.NotifyFocusEnter();
    }

    private void DetachTextInputController(bool notifyFocusLeave)
    {
        if (_textInput is null)
            return;
        var previous = _textInput;
        _textInput = null;
        previous.InputLanguageChanged -= TextInput_StateChanged;
        previous.CompositionStateChanged -= TextInput_StateChanged;
        if (notifyFocusLeave)
            previous.NotifyFocusLeave();
        previous.Dispose();
    }

    private void Editor_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
            return;
        _loaded = true;
        RefreshAccessibilityPalette(force: true);
        _accessibilityTimer.Start();
        EnsureGraphicsResources();
        if (_textInput is null)
            AttachTextInputController(CreateTextInputController(), FocusState != FocusState.Unfocused);
        UpdateProjectionMetrics();
        _caretTimer.Start();
        ScheduleDecorationRefresh(1, immediate: true);
        ScheduleFoldingRefresh();
        Render();
        ScheduleContributionRefresh(full: true);
    }

    private void Editor_Unloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        _caretTimer.Stop();
        _accessibilityTimer.Stop();
        _decorationIdleTimer.Stop();
        _foldRefreshTimer.Stop();
        _contributionRefreshTimer.Stop();
        _lspRefreshTimer.Stop();
    }

    private void Editor_GotFocus(object sender, RoutedEventArgs e)
    {
        _textInput?.NotifyFocusEnter();
        UpdateCommandContext();
        ResetCaretBlink();
        Render(RenderDirty.Caret);
    }

    private void Editor_LostFocus(object sender, RoutedEventArgs e)
    {
        // Native TSF keeps focus on the editor itself, so a real focus move (find box,
        // toolbar, etc.) must deactivate the TSF document instead of being mistaken for
        // an internal adapter focus hop.
        if (_textInput is { TreatAdapterFocusAsEditorFocus: true, HasInputFocus: true })
            return;
        _textInput?.NotifyFocusLeave();
        _fallbackCharacterAccumulator.Reset();
        _caretVisible = false;
        _commands.ResetChord();
        _chordTimer.Stop();
        UpdateCommandContext();
        Render(RenderDirty.Caret);
    }

    private void Editor_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateFindWidgetPlacement();
        if (!_loaded)
            return;
        EnsureSwapChain();
        UpdateProjectionMetrics();
        _textInput?.NotifyLayoutChanged();
        RaiseAccessibilityLayoutInvalidated();
        OnContributionsViewportChanged();
        Render();
    }

    private void EnsureGraphicsResources()
    {
        if (_device is null)
        {
            _device = CanvasDevice.GetSharedDevice();
            _device.DeviceLost += Device_DeviceLost;
        }
        if (_textFormat is null)
        {
            _textFormat = new CanvasTextFormat
            {
                FontFamily = "Consolas",
                FontSize = 14,
                WordWrapping = CanvasWordWrapping.NoWrap,
                IncrementalTabStop = 32,
            };
        }
        _measurer ??= new DirectWriteTextMeasurer(_device, _textFormat);
        _layoutCache ??= new LineLayoutCache(_device, _textFormat, directionResolver: ResolveTextDirection);
        _view.SetTextMeasurer(_measurer.Measure);
        EnsureSwapChain();
    }

    private void EnsureSwapChain()
    {
        if (_device is null || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var width = (float)Math.Max(1, ActualWidth);
        var height = (float)Math.Max(1, ActualHeight);
        var dpi = (float)(96.0 * (XamlRoot?.RasterizationScale ?? 1.0));
        if (_swapChain is null)
        {
            _swapChain = new CanvasSwapChain(_device, width, height, dpi);
            SwapChainPanel.SwapChain = _swapChain;
        }
        else
        {
            var size = _swapChain.Size;
            if (Math.Abs(size.Width - width) > 0.5 || Math.Abs(size.Height - height) > 0.5 || Math.Abs(_swapChain.Dpi - dpi) > 0.5)
                _swapChain.ResizeBuffers(width, height, dpi);
        }
    }

    private void Device_DeviceLost(CanvasDevice sender, object args)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed)
                return;
            ReleaseGraphicsResources();
            if (_loaded)
            {
                EnsureGraphicsResources();
                UpdateProjectionMetrics();
            }
        });
    }

    private void ReleaseGraphicsResources()
    {
        if (_device is not null)
            _device.DeviceLost -= Device_DeviceLost;
        SwapChainPanel.SwapChain = null;
        _layoutCache?.Dispose();
        _layoutCache = null;
        _measurer?.Dispose();
        _measurer = null;
        _textFormat?.Dispose();
        _textFormat = null;
        _swapChain?.Dispose();
        _swapChain = null;
        _textLayer?.Dispose();
        _textLayer = null;
        _selectionViewPart.ReleaseGraphicsResources();
        _decorationsViewPart.ReleaseGraphicsResources();
        _diagnosticsViewPart.ReleaseGraphicsResources();
        _bracketViewPart.ReleaseGraphicsResources();
        _overviewViewPart.ReleaseGraphicsResources();
        _caretViewPart.ReleaseGraphicsResources();
        _renderLayerWidth = 0;
        _renderLayerHeight = 0;
        _renderLayerDpi = 0;
        _pendingRenderDirty = RenderDirty.All;
        // GetSharedDevice returns a shared object; do not dispose it here.
        _device = null;
    }

    private void UpdateProjectionMetrics()
    {
        using var methodSample = _methodProfiler.Measure("UpdateProjectionMetrics");
        if (!_loaded || _measurer is null)
            return;
        var overlayWidth = (_showMinimap ? MinimapWidth : 0.0) + (_showOverviewRuler ? OverviewRulerWidth : 0.0);
        var textWidth = Math.Max(1.0, ActualWidth - overlayWidth - GutterWidth - LeftPadding - RightPadding);
        _view.SetLineHeight(_lineHeight);
        _view.SetWrapWidth(_wordWrap ? textWidth : 0.0);
        _layoutCache?.Clear();
        _measurer.Clear();
        _maxObservedLineWidth = 0;
        UpdateExtent();
        Render();
    }

    private void UpdateExtent()
    {
        using var methodSample = _methodProfiler.Measure("UpdateExtent");
        var contentWidth = _wordWrap
            ? Math.Max(1, ActualWidth)
            : Math.Max(ActualWidth, GutterWidth + LeftPadding + RightPadding + _maxObservedLineWidth);
        ScrollExtent.Width = contentWidth;
        ScrollExtent.Height = Math.Max(1, _view.TotalHeight + (VerticalPadding * 2));
        _lastKnownViewLineCount = _view.ViewLineCount;
    }

    private void ScrollHost_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        using var methodSample = _methodProfiler.Measure("ScrollHost_ViewChanged");
        _textInput?.NotifyLayoutChanged();
        RaiseAccessibilityLayoutInvalidated();
        OnContributionsViewportChanged();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
        Render();
    }

    private void Render(RenderDirty dirty = RenderDirty.All)
    {
        using var methodSample = _methodProfiler.Measure("Render");
        if (_disposed || !_loaded || dirty == RenderDirty.None)
            return;

        _pendingRenderDirty |= dirty;
        _rendererPerformance.NoteRenderRequest(_renderQueued);
        if (_renderQueued)
            return;

        _renderQueued = true;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            _renderQueued = false;
            if (_disposed || !_loaded)
                return;

            var pending = _pendingRenderDirty;
            _pendingRenderDirty = RenderDirty.None;
            try
            {
                RenderFrame(pending);
            }
            catch (Exception ex) when (_device is not null && _device.IsDeviceLost(ex.HResult))
            {
                _device.RaiseDeviceLost();
            }
        }))
        {
            _renderQueued = false;
        }
    }

    private void RenderFrame(RenderDirty dirty)
    {
        using var methodSample = _methodProfiler.Measure("RenderFrame");
        if (!_loaded || _swapChain is null || _layoutCache is null || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var frameStart = Stopwatch.GetTimestamp();

        if (EnsureRenderLayers())
            dirty |= RenderDirty.All;

        if (_textLayer is null)
            return;

        if (_lastRenderedVersion != _model.VersionId)
            _lastRenderedVersion = _model.VersionId;

        var scrollTop = ScrollHost.VerticalOffset;
        var scrollLeft = ScrollHost.HorizontalOffset;
        var first = Math.Max(1, _view.GetViewLineAtVerticalOffset(Math.Max(0, scrollTop - VerticalPadding)) - 2);
        var last = Math.Min(_view.ViewLineCount, _view.GetViewLineAtVerticalOffset(scrollTop + ActualHeight) + 2);

        if ((dirty & RenderDirty.Text) != 0)
        {
            var textStart = Stopwatch.GetTimestamp();
            var linesVisited = RebuildTextLayer(first, last, scrollTop, scrollLeft);
            _rendererPerformance.NoteTextLayer(Stopwatch.GetTimestamp() - textStart, linesVisited);
        }
        if ((dirty & RenderDirty.Selection) != 0)
            _selectionViewPart.Invalidate();
        if ((dirty & RenderDirty.Decorations) != 0)
            _decorationsViewPart.Invalidate();
        if ((dirty & RenderDirty.Diagnostics) != 0)
            _diagnosticsViewPart.Invalidate();
        if ((dirty & RenderDirty.Brackets) != 0)
            _bracketViewPart.Invalidate();
        if ((dirty & RenderDirty.Overview) != 0)
            _overviewViewPart.Invalidate();
        if ((dirty & RenderDirty.Caret) != 0)
            _caretViewPart.Invalidate();

        var viewPartContext = new EditorViewPartContext(
            first,
            last,
            scrollTop,
            scrollLeft,
            ActualWidth,
            ActualHeight);
        _selectionViewPart.Prepare(_device!, viewPartContext);
        _decorationsViewPart.Prepare(_device!, viewPartContext);
        _diagnosticsViewPart.Prepare(_device!, viewPartContext);
        _bracketViewPart.Prepare(_device!, viewPartContext);
        _overviewViewPart.Prepare(_device!, viewPartContext);
        _caretViewPart.Prepare(_device!, viewPartContext);

        using var ds = _swapChain.CreateDrawingSession(_backgroundColor);
        _selectionViewPart.Composite(ds);
        _decorationsViewPart.Composite(ds);
        ds.DrawImage(_textLayer);
        _bracketViewPart.Composite(ds);
        _overviewViewPart.Composite(ds);
        _diagnosticsViewPart.Composite(ds);
        _caretViewPart.Composite(ds);
        _swapChain.Present();

        if (!_wordWrap && _maxObservedLineWidth + GutterWidth + LeftPadding + RightPadding > ScrollExtent.Width)
            UpdateExtent();

        _rendererPerformance.NoteFrame(Stopwatch.GetTimestamp() - frameStart);
    }

    private bool EnsureRenderLayers()
    {
        using var methodSample = _methodProfiler.Measure("EnsureRenderLayers");
        if (_device is null || ActualWidth <= 0 || ActualHeight <= 0)
            return false;

        var width = (float)Math.Max(1, ActualWidth);
        var height = (float)Math.Max(1, ActualHeight);
        var dpi = (float)(96.0 * (XamlRoot?.RasterizationScale ?? 1.0));
        if (_textLayer is not null &&
            Math.Abs(_renderLayerWidth - width) <= 0.5f &&
            Math.Abs(_renderLayerHeight - height) <= 0.5f &&
            Math.Abs(_renderLayerDpi - dpi) <= 0.5f)
        {
            return false;
        }

        _textLayer?.Dispose();
        _textLayer = new CanvasRenderTarget(_device, width, height, dpi);
        _renderLayerWidth = width;
        _renderLayerHeight = height;
        _renderLayerDpi = dpi;
        return true;
    }

    private int RebuildTextLayer(int first, int last, double scrollTop, double scrollLeft)
    {
        using var methodSample = _methodProfiler.Measure("RebuildTextLayer");
        if (_textLayer is null || _layoutCache is null)
            return 0;

        using var ds = _textLayer.CreateDrawingSession();
        ds.Clear(Color.FromArgb(0, 0, 0, 0));
        PrepareVisibleBracketInfo(first, last);
        var linesVisited = 0;

        for (var lineNumber = first; lineNumber <= last; lineNumber++)
        {
            linesVisited++;
            var line = _view.GetViewLine(lineNumber);
            var y = VerticalPadding + line.VerticalOffset - scrollTop;
            if (y + _lineHeight < 0 || y > ActualHeight)
                continue;

            var layout = _layoutCache.GetOrCreate(lineNumber, line.Text, (float)_lineHeight, l =>
            {
                ApplyLanguageTokenColors(l, line);
                ApplyBracketPairColors(l, line);
            });
            _maxObservedLineWidth = Math.Max(_maxObservedLineWidth, layout.LayoutBoundsIncludingTrailingWhitespace.Width);
            if (_showIndentGuides)
                DrawIndentGuides(ds, line, layout, y, scrollLeft);
            if (line.WrappedIndex == 0)
            {
                if (_showFolding && _foldRanges.ContainsKey(line.ModelLineNumber))
                    ds.DrawText(_collapsedFolds.Contains(line.ModelLineNumber) ? "▸" : "▾",
                        (float)(GutterWidth - 17), (float)y, _gutterColor, _textFormat!);
                ds.DrawText(
                    line.ModelLineNumber.ToString(CultureInfo.InvariantCulture),
                    6f,
                    (float)y,
                    _gutterColor,
                    _textFormat!);
            }
            else
            {
                ds.DrawText("↪", (float)(GutterWidth - 18), (float)y, _gutterColor, _textFormat!);
            }
            ds.DrawTextLayout(layout, (float)(GutterWidth + LeftPadding - scrollLeft), (float)y, _foregroundColor);
            if (_showWhitespace)
                DrawWhitespace(ds, line, layout, y, scrollLeft);
        }

        DrawMinimap(ds, scrollTop);
        return linesVisited;
    }

    private void UpdateFindWidgetPlacement()
    {
        if (FindWidget is null) return;
        // Never cover the minimap or its overview ruler, regardless of theme.
        var right = (_showMinimap ? MinimapWidth : 0.0) +
            (_showOverviewRuler ? OverviewRulerWidth : 0.0) + 12.0;
        FindWidget.Margin = new Thickness(0, 8, right, 0);
        // Keep all controls inside the editor on narrow windows.
        var available = Math.Max(0, ActualWidth - right - 20.0);
        FindWidget.Width = Math.Min(536.0, available);
    }

    private double MinimapLeft => Math.Max(0, ActualWidth - (_showMinimap ? MinimapWidth : 0.0));
    private double OverviewLeft => Math.Max(0, MinimapLeft - (_showOverviewRuler ? OverviewRulerWidth : 0.0));
    private double TextViewportRight => _showOverviewRuler ? OverviewLeft : (_showMinimap ? MinimapLeft : ActualWidth);

    private void PrepareVisibleBracketInfo(int firstViewLine, int lastViewLine)
    {
        using var methodSample = _methodProfiler.Measure("PrepareVisibleBracketInfo");
        if (!_bracketPairColorization || _view.ViewLineCount == 0)
        {
            _visibleBracketsByModelLine = new Dictionary<int, IReadOnlyList<BracketTreeInfo>>();
            return;
        }

        var firstModelLine = _view.GetViewLine(firstViewLine).ModelLineNumber;
        var lastModelLine = _view.GetViewLine(lastViewLine).ModelLineNumber;
        var range = new LanguageRange(
            firstModelLine, 1,
            lastModelLine, _model.GetLineMaxColumn(lastModelLine));
        _visibleBracketsByModelLine = _bracketPairs.GetBracketsInRange(range)
            .GroupBy(info => info.Range.StartLineNumber)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<BracketTreeInfo>)group.ToArray());
    }

    private void ApplyBracketPairColors(CanvasTextLayout layout, MonacoViewLineInfo line)
    {
        using var methodSample = _methodProfiler.Measure("ApplyBracketPairColors");
        if (!_bracketPairColorization || line.Text.Length == 0 ||
            !_visibleBracketsByModelLine.TryGetValue(line.ModelLineNumber, out var brackets))
            return;

        foreach (var bracket in brackets)
        {
            var startModel = new MonacoPosition(bracket.Range.StartLineNumber, bracket.Range.StartColumn);
            var endModel = new MonacoPosition(bracket.Range.EndLineNumber, bracket.Range.EndColumn);
            var startView = _view.ModelToView(startModel);
            var endView = _view.ModelToView(endModel);
            if (startView.LineNumber != line.ViewLineNumber)
                continue;
            var startIndex = Math.Clamp(startView.Column - 1, 0, line.Text.Length);
            var endIndex = endView.LineNumber == line.ViewLineNumber
                ? Math.Clamp(endView.Column - 1, startIndex, line.Text.Length)
                : Math.Min(line.Text.Length, startIndex + 1);
            var length = Math.Max(1, endIndex - startIndex);
            if (startIndex + length > line.Text.Length)
                length = line.Text.Length - startIndex;
            if (length <= 0) continue;
            layout.SetColor(
                startIndex,
                length,
                bracket.IsInvalid
                    ? _syntaxInvalidColor
                    : _bracketColors[Math.Abs(bracket.NestingLevel) % _bracketColors.Length]);
        }
    }

    private void DrawIndentGuides(CanvasDrawingSession ds, MonacoViewLineInfo line, CanvasTextLayout layout, double y, double scrollLeft)
    {
        using var methodSample = _methodProfiler.Measure("DrawIndentGuides");
        if (line.WrappedIndex != 0 || line.Text.Length == 0)
            return;
        var visualColumn = 0;
        for (var i = 0; i < line.Text.Length; i++)
        {
            var ch = line.Text[i];
            if (ch is not (' ' or '\t'))
                break;
            var before = visualColumn;
            visualColumn = ch == '\t' ? ((visualColumn / TabSize) + 1) * TabSize : visualColumn + 1;
            if ((visualColumn / TabSize) == (before / TabSize))
                continue;
            var x = GutterWidth + LeftPadding + CaretX(layout, line.Text, i + 1) - scrollLeft;
            if (x >= GutterWidth && x < TextViewportRight)
                ds.DrawLine((float)x, (float)y, (float)x, (float)(y + _lineHeight), _indentGuideColor, 1f);
        }
    }

    private void DrawWhitespace(CanvasDrawingSession ds, MonacoViewLineInfo line, CanvasTextLayout layout, double y, double scrollLeft)
    {
        using var methodSample = _methodProfiler.Measure("DrawWhitespace");
        var baselineY = (float)(y + (_lineHeight * 0.55));
        for (var i = 0; i < line.Text.Length; i++)
        {
            var ch = line.Text[i];
            if (ch is not (' ' or '\t'))
                continue;
            var x1 = (float)(GutterWidth + LeftPadding + CaretX(layout, line.Text, i) - scrollLeft);
            var x2 = (float)(GutterWidth + LeftPadding + CaretX(layout, line.Text, i + 1) - scrollLeft);
            if (x2 < GutterWidth || x1 > TextViewportRight)
                continue;
            if (ch == ' ')
            {
                var center = x1 + ((x2 - x1) * 0.5f);
                ds.FillRectangle(center, baselineY, 1.25f, 1.25f, _whitespaceColor);
            }
            else
            {
                var end = Math.Max(x1 + 3f, x2 - 3f);
                ds.DrawLine(x1 + 1f, baselineY, end, baselineY, _whitespaceColor, 1f);
                ds.DrawLine(end - 3f, baselineY - 2f, end, baselineY, _whitespaceColor, 1f);
                ds.DrawLine(end - 3f, baselineY + 2f, end, baselineY, _whitespaceColor, 1f);
            }
        }
    }

    private void DrawBracketDecorations(CanvasDrawingSession ds, int first, int last, double scrollTop, double scrollLeft)
    {
        using var methodSample = _methodProfiler.Measure("DrawBracketDecorations");
        var match = _bracketPairs.FindMatchingBracket(new LanguagePosition(_active.LineNumber, _active.Column));
        if (match is null)
        {
            var offset = _model.GetOffsetAt(_active);
            if (offset > 0)
            {
                var previous = _model.GetPositionAt(offset - (nuint)1);
                match = _bracketPairs.FindMatchingBracket(new LanguagePosition(previous.LineNumber, previous.Column));
            }
        }
        if (match is null || _layoutCache is null)
            return;

        var firstPoint = BracketScreenPoint(match.Bracket, first, last, scrollTop, scrollLeft);
        var secondPoint = BracketScreenPoint(match.Match, first, last, scrollTop, scrollLeft);
        if (firstPoint.HasValue) DrawBracketBox(ds, firstPoint.Value);
        if (secondPoint.HasValue) DrawBracketBox(ds, secondPoint.Value);

        if (firstPoint.HasValue && secondPoint.HasValue && firstPoint.Value.ViewLine != secondPoint.Value.ViewLine)
        {
            var x = Math.Min(firstPoint.Value.X, secondPoint.Value.X) + 1f;
            var y1 = Math.Min(firstPoint.Value.Y, secondPoint.Value.Y) + (float)_lineHeight;
            var y2 = Math.Max(firstPoint.Value.Y, secondPoint.Value.Y);
            if (y2 > y1) ds.DrawLine(x, y1, x, y2, _indentGuideColor, 1f);
        }
    }

    private (float X, float Y, float Width, int ViewLine)? BracketScreenPoint(LanguageRange range, int first, int last, double scrollTop, double scrollLeft)
    {
        if (_layoutCache is null) return null;
        var startView = _view.ModelToView(new MonacoPosition(range.StartLineNumber, range.StartColumn));
        var endView = _view.ModelToView(new MonacoPosition(range.EndLineNumber, range.EndColumn));
        if (startView.LineNumber < first || startView.LineNumber > last) return null;
        var line = _view.GetViewLine(startView.LineNumber);
        var startIndex = Math.Clamp(startView.Column - 1, 0, line.Text.Length);
        var endIndex = endView.LineNumber == startView.LineNumber
            ? Math.Clamp(endView.Column - 1, startIndex, line.Text.Length)
            : Math.Min(line.Text.Length, startIndex + 1);
        var layout = _layoutCache.GetOrCreate(line.ViewLineNumber, line.Text, (float)_lineHeight, l =>
        {
            ApplyLanguageTokenColors(l, line);
            ApplyBracketPairColors(l, line);
        });
        var x1 = CaretX(layout, line.Text, startIndex);
        var x2 = CaretX(layout, line.Text, endIndex);
        return ((float)(GutterWidth + LeftPadding + x1 - scrollLeft),
            (float)(VerticalPadding + line.VerticalOffset - scrollTop), Math.Max(3f, x2 - x1), line.ViewLineNumber);
    }

    private void DrawBracketBox(CanvasDrawingSession ds, (float X, float Y, float Width, int ViewLine) point) =>
        ds.DrawRectangle(point.X, point.Y + 1f, point.Width, (float)Math.Max(2, _lineHeight - 2), _overviewColor, 1f);

    private void DrawOverviewRuler(CanvasDrawingSession ds, double scrollTop)
    {
        using var methodSample = _methodProfiler.Measure("DrawOverviewRuler");
        if (!_showOverviewRuler || ActualHeight <= 0 || _view.TotalHeight <= 0)
            return;
        var x = (float)OverviewLeft;
        var width = (float)OverviewRulerWidth;
        ds.FillRectangle(x, 0, width, (float)ActualHeight, Color.FromArgb(80, _backgroundColor.R, _backgroundColor.G, _backgroundColor.B));

        var activeView = _view.ModelToView(_active);
        var activeY = _view.GetViewLine(activeView.LineNumber).VerticalOffset;
        var markerY = (float)Math.Clamp(activeY / _view.TotalHeight * ActualHeight, 0, Math.Max(0, ActualHeight - 3));
        ds.FillRectangle(x, markerY, width, 3f, _overviewColor);

        var (start, end) = OrderedSelectionOffsets();
        if (start != end)
        {
            var startView = _view.ModelToView(_model.GetPositionAt(start));
            var endView = _view.ModelToView(_model.GetPositionAt(end));
            var y1 = _view.GetViewLine(startView.LineNumber).VerticalOffset / _view.TotalHeight * ActualHeight;
            var y2 = (_view.GetViewLine(endView.LineNumber).VerticalOffset + _lineHeight) / _view.TotalHeight * ActualHeight;
            ds.FillRectangle(x + 1f, (float)Math.Min(y1, y2), Math.Max(1f, width - 2f), (float)Math.Max(2, Math.Abs(y2 - y1)), _selectionColor);
        }

        _ = scrollTop;
    }

    private void DrawMinimap(CanvasDrawingSession ds, double scrollTop)
    {
        using var methodSample = _methodProfiler.Measure("DrawMinimap");
        if (!_showMinimap)
            return;
        if (!_minimap.IsCurrent(_model.VersionId))
        {
            ds.FillRectangle((float)MinimapLeft, 0, (float)MinimapWidth, (float)Math.Max(1, ActualHeight), _minimapBackgroundColor);
            return;
        }
        _minimap.Draw(
            ds,
            (float)MinimapLeft,
            (float)MinimapWidth,
            (float)Math.Max(1, ActualHeight),
            scrollTop,
            Math.Max(1, _view.TotalHeight + (VerticalPadding * 2)),
            ActualHeight,
            _minimapBackgroundColor,
            _minimapForegroundColor,
            _overviewColor);
    }

    private void DrawCurrentLine(CanvasDrawingSession ds, double scrollTop)
    {
        var viewPosition = _view.ModelToView(_active);
        var y = VerticalPadding + _view.GetViewLine(viewPosition.LineNumber).VerticalOffset - scrollTop;
        if (y + _lineHeight >= 0 && y <= ActualHeight)
            ds.FillRectangle(0, (float)y, (float)Math.Max(0, TextViewportRight), (float)_lineHeight, _currentLineColor);
    }

    private void DrawSelection(CanvasDrawingSession ds, int first, int last, double scrollTop, double scrollLeft)
    {
        using var methodSample = _methodProfiler.Measure("DrawSelection");
        if (_layoutCache is null)
            return;
        // Search highlights are painted only for visible matches and only while Find is open.
        // Binary-search into the sorted match array to avoid O(document matches) scrolling.
        if (IsFindOpen && _findMatches.Count > 0 && first <= last)
        {
            var firstModel = _view.GetViewLine(first).ModelLineNumber;
            var lastModel = _view.GetViewLine(last).ModelLineNumber;
            var lo = 0;
            var hi = _findMatches.Count;
            while (lo < hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (_findMatches[mid].EndLineNumber < firstModel) lo = mid + 1;
                else hi = mid;
            }
            var rendered = 0;
            for (var i = lo; i < _findMatches.Count && rendered < 350; i++)
            {
                var match = _findMatches[i];
                if (match.StartLineNumber > lastModel) break;
                DrawSelectionRange(ds,
                    new MonacoPosition(match.StartLineNumber, match.StartColumn),
                    new MonacoPosition(match.EndLineNumber, match.EndColumn),
                    first, last, scrollTop, scrollLeft,
                    Color.FromArgb(105, 212, 169, 62));
                rendered++;
            }
        }
        foreach (var selection in EnumerateSelectionStates())
            DrawSelectionRange(ds, selection.Anchor, selection.Active, first, last, scrollTop, scrollLeft, _selectionColor);
    }

    private void DrawSelectionRange(CanvasDrawingSession ds, MonacoPosition anchor, MonacoPosition active, int first, int last, double scrollTop, double scrollLeft, Color fillColor)
    {
        using var methodSample = _methodProfiler.Measure("DrawSelectionRange");
        if (_layoutCache is null)
            return;
        var a = _model.GetOffsetAt(anchor);
        var b = _model.GetOffsetAt(active);
        var startOffset = Math.Min(a, b);
        var endOffset = Math.Max(a, b);
        if (startOffset == endOffset)
            return;

        var start = _model.GetPositionAt(startOffset);
        var end = _model.GetPositionAt(endOffset);
        var startView = _view.ModelToView(start);
        var endView = _view.ModelToView(end);
        var firstSelected = Math.Min(startView.LineNumber, endView.LineNumber);
        var lastSelected = Math.Max(startView.LineNumber, endView.LineNumber);

        for (var lineNumber = Math.Max(first, firstSelected); lineNumber <= Math.Min(last, lastSelected); lineNumber++)
        {
            var line = _view.GetViewLine(lineNumber);
            var layout = _layoutCache.GetOrCreate(lineNumber, line.Text, (float)_lineHeight);
            var startIndex = lineNumber == startView.LineNumber ? Math.Clamp(startView.Column - 1, 0, line.Text.Length) : 0;
            var endIndex = lineNumber == endView.LineNumber ? Math.Clamp(endView.Column - 1, 0, line.Text.Length) : line.Text.Length;
            if (endIndex < startIndex)
                (startIndex, endIndex) = (endIndex, startIndex);
            var y = VerticalPadding + line.VerticalOffset - scrollTop;
            var regions = TextRangeRegions(layout, line.Text, startIndex, endIndex);
            if (regions.Length == 0)
            {
                var x1 = CaretX(layout, line.Text, startIndex);
                var x2 = CaretX(layout, line.Text, endIndex);
                ds.FillRectangle(
                    (float)(GutterWidth + LeftPadding + Math.Min(x1, x2) - scrollLeft),
                    (float)y,
                    Math.Max(1f, Math.Abs(x2 - x1)),
                    (float)_lineHeight,
                    fillColor);
                continue;
            }

            // A logical range can occupy several disjoint visual regions in bidi text.
            foreach (var region in regions)
            {
                var bounds = region.LayoutBounds;
                ds.FillRectangle(
                    (float)(GutterWidth + LeftPadding + bounds.X - scrollLeft),
                    (float)(y + bounds.Y),
                    Math.Max(1f, (float)bounds.Width),
                    Math.Max(1f, (float)bounds.Height),
                    fillColor);
            }
        }
    }

    private void DrawCompositionDecorations(CanvasDrawingSession ds, int first, int last, double scrollTop, double scrollLeft)
    {
        using var methodSample = _methodProfiler.Measure("DrawCompositionDecorations");
        if (_textInput is not { IsComposing: true } textInput || _layoutCache is null)
            return;

        foreach (var segment in textInput.CompositionSegments)
        {
            var startOffset = (nuint)Math.Clamp(segment.Range.Start, 0, checked((int)Math.Min(_model.Length, (nuint)int.MaxValue)));
            var endOffset = (nuint)Math.Clamp(segment.Range.End, checked((int)startOffset), checked((int)Math.Min(_model.Length, (nuint)int.MaxValue)));
            if (startOffset == endOffset)
                continue;

            var startView = _view.ModelToView(_model.GetPositionAt(startOffset));
            var endView = _view.ModelToView(_model.GetPositionAt(endOffset));
            var firstLine = Math.Max(first, Math.Min(startView.LineNumber, endView.LineNumber));
            var lastLine = Math.Min(last, Math.Max(startView.LineNumber, endView.LineNumber));
            var color = segment.IsTarget ? _compositionTargetColor : _compositionColor;

            for (var lineNumber = firstLine; lineNumber <= lastLine; ++lineNumber)
            {
                var line = _view.GetViewLine(lineNumber);
                var layout = _layoutCache.GetOrCreate(lineNumber, line.Text, (float)_lineHeight);
                var boundaryOffsets = new int[line.Text.Length + 1];
                for (var boundary = 0; boundary <= line.Text.Length; ++boundary)
                {
                    var modelPosition = _view.ViewToModel(new MonacoViewPosition(lineNumber, boundary + 1));
                    boundaryOffsets[boundary] = checked((int)Math.Min(
                        _model.GetOffsetAt(modelPosition),
                        (nuint)int.MaxValue));
                }

                // Injected/inlay text has visual boundaries whose model offset does not
                // advance. Split around those intervals so IME underlines decorate only
                // authoritative model text, never projection-only hints.
                foreach (var fragment in ImeProjectionFragments.GetModelBackedFragments(boundaryOffsets, segment.Range))
                {
                    var y = VerticalPadding + line.VerticalOffset - scrollTop + _lineHeight - 2.5;
                    var regions = TextRangeRegions(layout, line.Text, fragment.StartVisualOffset, fragment.EndVisualOffset);
                    if (regions.Length == 0)
                    {
                        var x1 = CaretX(layout, line.Text, fragment.StartVisualOffset);
                        var x2 = CaretX(layout, line.Text, fragment.EndVisualOffset);
                        DrawImeUnderline(ds,
                            (float)(GutterWidth + LeftPadding + Math.Min(x1, x2) - scrollLeft),
                            (float)(GutterWidth + LeftPadding + Math.Max(x1, x2) - scrollLeft),
                            (float)y,
                            segment.UnderlineStyle,
                            color,
                            segment.IsTarget);
                        continue;
                    }
                    foreach (var region in regions)
                    {
                        var bounds = region.LayoutBounds;
                        var left = GutterWidth + LeftPadding + bounds.X - scrollLeft;
                        var right = left + Math.Max(2.0, bounds.Width);
                        DrawImeUnderline(ds, (float)left, (float)right, (float)y, segment.UnderlineStyle, color, segment.IsTarget);
                    }
                }
            }
        }
    }

    private static void DrawImeUnderline(
        CanvasDrawingSession ds,
        float x1,
        float x2,
        float y,
        ImeUnderlineStyle style,
        Color color,
        bool target)
    {
        if (x2 <= x1 || style == ImeUnderlineStyle.None)
            return;
        var thickness = target || style is ImeUnderlineStyle.Thick or ImeUnderlineStyle.HeavyWave or
            ImeUnderlineStyle.ThickDash or ImeUnderlineStyle.ThickDotted or ImeUnderlineStyle.ThickLongDash ? 2f : 1f;

        if (style == ImeUnderlineStyle.Double)
        {
            ds.DrawLine(x1, y - 2f, x2, y - 2f, color, thickness);
            ds.DrawLine(x1, y, x2, y, color, thickness);
            return;
        }

        if (style == ImeUnderlineStyle.DoubleWave)
        {
            DrawWave(ds, x1, x2, y - 2.5f, color, thickness);
            DrawWave(ds, x1, x2, y, color, thickness);
            return;
        }

        if (style is ImeUnderlineStyle.Dotted or ImeUnderlineStyle.ThickDotted)
        {
            DrawPattern(ds, x1, x2, y, color, thickness, 1f, 3f);
            return;
        }

        if (style is ImeUnderlineStyle.Dash or ImeUnderlineStyle.ThickDash)
        {
            DrawPattern(ds, x1, x2, y, color, thickness, 6f, 3f);
            return;
        }

        if (style is ImeUnderlineStyle.LongDash or ImeUnderlineStyle.ThickLongDash)
        {
            DrawPattern(ds, x1, x2, y, color, thickness, 9f, 3f);
            return;
        }

        if (style is ImeUnderlineStyle.DashDot or ImeUnderlineStyle.ThickDashDot)
        {
            DrawPattern(ds, x1, x2, y, color, thickness, 6f, 2f, 1f, 2f);
            return;
        }

        if (style is ImeUnderlineStyle.DashDotDot or ImeUnderlineStyle.ThickDashDotDot)
        {
            DrawPattern(ds, x1, x2, y, color, thickness, 6f, 2f, 1f, 2f, 1f, 2f);
            return;
        }

        if (style is ImeUnderlineStyle.Wave or ImeUnderlineStyle.HeavyWave)
        {
            DrawWave(ds, x1, x2, y, color, thickness);
            return;
        }

        // Undefined, Single, Words and Thin intentionally degrade to a single
        // baseline when the platform does not provide enough lexical information
        // to split a Words underline safely. The IME segment boundary remains exact.
        ds.DrawLine(x1, y, x2, y, color, thickness);
    }

    private static void DrawWave(
        CanvasDrawingSession ds, float x1, float x2, float y, Color color, float thickness)
    {
        var x = x1;
        var up = true;
        while (x < x2)
        {
            var next = Math.Min(x + 3f, x2);
            ds.DrawLine(x, up ? y : y - 2f, next, up ? y - 2f : y, color, thickness);
            up = !up;
            x = next;
        }
    }

    private static void DrawPattern(
        CanvasDrawingSession ds, float x1, float x2, float y, Color color, float thickness, params float[] lengths)
    {
        if (lengths.Length == 0)
            return;
        var x = x1;
        var index = 0;
        var draw = true;
        while (x < x2)
        {
            var length = Math.Max(0.5f, lengths[index % lengths.Length]);
            var next = Math.Min(x + length, x2);
            if (draw)
                ds.DrawLine(x, y, next, y, color, thickness);
            draw = !draw;
            index++;
            x = next;
        }
    }

    private bool HasEditorInputFocus =>
        FocusState != FocusState.Unfocused || _textInput?.HasInputFocus == true;

    private void DrawCaret(CanvasDrawingSession ds, double scrollTop, double scrollLeft)
    {
        using var methodSample = _methodProfiler.Measure("DrawCaret");
        if (!_caretVisible || !HasEditorInputFocus || _layoutCache is null)
            return;
        foreach (var selection in EnumerateSelectionStates())
            DrawCaretAt(ds, selection.Active, scrollTop, scrollLeft);
    }

    private void DrawCaretAt(CanvasDrawingSession ds, MonacoPosition position, double scrollTop, double scrollLeft)
    {
        using var methodSample = _methodProfiler.Measure("DrawCaretAt");
        if (_layoutCache is null)
            return;
        var viewPosition = _view.ModelToView(position);
        var line = _view.GetViewLine(viewPosition.LineNumber);
        var y = VerticalPadding + line.VerticalOffset - scrollTop;
        if (y + _lineHeight < 0 || y > ActualHeight)
            return;
        var layout = _layoutCache.GetOrCreate(line.ViewLineNumber, line.Text, (float)_lineHeight);
        var x = CaretX(layout, line.Text, Math.Clamp(viewPosition.Column - 1, 0, line.Text.Length));
        ds.FillRectangle(
            (float)(GutterWidth + LeftPadding + x - scrollLeft),
            (float)y,
            1.5f,
            (float)_lineHeight,
            _caretColor);
    }

    private static float CaretX(CanvasTextLayout layout, string text, int index)
    {
        if (text.Length == 0)
            return 0;
        index = Math.Clamp(index, 0, text.Length);
        return index == text.Length
            ? layout.GetCaretPosition(text.Length - 1, true).X
            : layout.GetCaretPosition(index, false).X;
    }

    private static CanvasTextLayoutRegion[] TextRangeRegions(CanvasTextLayout layout, string text, int startIndex, int endIndex)
    {
        startIndex = Math.Clamp(startIndex, 0, text.Length);
        endIndex = Math.Clamp(endIndex, startIndex, text.Length);
        return endIndex <= startIndex
            ? Array.Empty<CanvasTextLayoutRegion>()
            : layout.GetCharacterRegions(startIndex, endIndex - startIndex);
    }

    private void SurfaceRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        using var methodSample = _methodProfiler.Measure("SurfaceRoot_PointerPressed");
        Focus(FocusState.Pointer);
        var pointerPoint = e.GetCurrentPoint(SurfaceRoot);
        var point = pointerPoint.Position;

        if (pointerPoint.Properties.IsRightButtonPressed)
        {
            var contextPosition = HitTest(point.X, point.Y);
            var offset = _model.GetOffsetAt(contextPosition);
            var (selectionStart, selectionEnd) = OrderedSelectionOffsets();
            if (selectionStart == selectionEnd || offset < selectionStart || offset > selectionEnd)
            {
                ClearSecondarySelections();
                _anchor = _active = contextPosition;
                _preferredCaretX = null;
                OnSelectionChanged();
            }
            return; // ContextFlyout handles mouse, pen, touch and Shift+F10 uniformly.
        }

        if (!pointerPoint.Properties.IsRightButtonPressed && _showFolding && point.X >= 38 && point.X < GutterWidth)
        {
            var viewLine = _view.GetViewLineAtVerticalOffset(Math.Max(0, ScrollHost.VerticalOffset + point.Y - VerticalPadding));
            if (viewLine >= 1 && viewLine <= _view.ViewLineCount)
            {
                var line = _view.GetViewLine(viewLine);
                if (line.WrappedIndex == 0 && ToggleFold(line.ModelLineNumber))
                {
                    e.Handled = true;
                    return;
                }
            }
        }
        if (_showMinimap && point.X >= MinimapLeft)
        {
            var ratio = ActualHeight <= 0 ? 0 : Math.Clamp(point.Y / ActualHeight, 0, 1);
            var target = Math.Clamp((ratio * _view.TotalHeight) - (ActualHeight / 2), 0, ScrollHost.ScrollableHeight);
            ScrollHost.ChangeView(null, target, null, true);
            e.Handled = true;
            return;
        }
        var position = HitTest(point.X, point.Y);
        if (IsKeyDown(VirtualKey.Menu))
        {
            AddSecondaryCaret(position);
            e.Handled = true;
            return;
        }
        ClearSecondarySelections();
        _dragSelecting = true;
        SetMouseCursor(EditorPointerCursorKind.Text);
        SurfaceRoot.CapturePointer(e.Pointer);
        _anchor = _active = position;
        _preferredCaretX = null;
        OnSelectionChanged();
        e.Handled = true;
    }

    private void SurfaceRoot_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        UpdateMouseCursor(e);

    private void SurfaceRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // A captured selection should keep its I-beam until the capture ends.
        if (!_disposed && !_dragSelecting)
            SetMouseCursor(EditorPointerCursorKind.Arrow);
    }

    private void UpdateMouseCursor(PointerRoutedEventArgs e)
    {
        if (_disposed || e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse)
            return;
        var point = e.GetCurrentPoint(SurfaceRoot).Position;
        // Native flyouts, find controls and code-lens elements must not inherit the text cursor.
        var source = e.OriginalSource as DependencyObject;
        var overOverlay = IsChildOf(source, FindWidget) || IsChildOf(source, ContributionLayer);
        var cursor = _dragSelecting
            ? EditorPointerCursorKind.Text
            : EditorPointerCursorPolicy.Resolve(point.X, point.Y, ActualWidth, ActualHeight,
                GutterWidth + LeftPadding, TextViewportRight, MinimapLeft, _showMinimap, overOverlay);
        if (!_dragSelecting && !overOverlay && _showFolding && point.X >= 38 && point.X < GutterWidth &&
            point.Y >= 0 && point.Y < ActualHeight && _view.ViewLineCount > 0)
        {
            var viewLine = _view.GetViewLineAtVerticalOffset(Math.Max(0, ScrollHost.VerticalOffset + point.Y - VerticalPadding));
            if (viewLine >= 1 && viewLine <= _view.ViewLineCount)
            {
                var line = _view.GetViewLine(viewLine);
                if (line.WrappedIndex == 0 && _foldRanges.ContainsKey(line.ModelLineNumber))
                    cursor = EditorPointerCursorKind.Folding;
            }
        }
        SetMouseCursor(cursor);
    }

    private static bool IsChildOf(DependencyObject? source, DependencyObject ancestor)
    {
        // Only called for the hover event, not for each rendered line or typed character.
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, ancestor))
                return true;
        return false;
    }

    private void SetMouseCursor(EditorPointerCursorKind cursor)
    {
        if (_disposed || _currentPointerCursor == cursor)
            return;
        _currentPointerCursor = cursor;
        ProtectedCursor = cursor switch
        {
            EditorPointerCursorKind.Text => _textPointerCursor ??= InputSystemCursor.Create(InputSystemCursorShape.IBeam),
            EditorPointerCursorKind.Minimap or EditorPointerCursorKind.Folding => _minimapPointerCursor ??= InputSystemCursor.Create(InputSystemCursorShape.Hand),
            _ => _arrowPointerCursor ??= InputSystemCursor.Create(InputSystemCursorShape.Arrow),
        };
        PointerCursorChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SurfaceRoot_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        using var methodSample = _methodProfiler.Measure("SurfaceRoot_PointerMoved");
        UpdateMouseCursor(e);
        if (!_dragSelecting)
            return;
        var point = e.GetCurrentPoint(SurfaceRoot).Position;
        var position = HitTest(point.X, point.Y);
        if (position != _active) // Avoid rerendering on a move inside the same caret column.
        {
            _active = position;
            _preferredCaretX = null;
            OnSelectionChanged();
        }
        e.Handled = true;
    }

    private void SurfaceRoot_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        var wasSelecting = _dragSelecting;
        _dragSelecting = false;
        if (wasSelecting && !_disposed)
        {
            OnContributionsSelectionChanged();
            _textInput?.NotifyLayoutChanged();
        }
        if (!_disposed)
            UpdateMouseCursor(e);
    }

    private void SurfaceRoot_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        using var methodSample = _methodProfiler.Measure("SurfaceRoot_PointerReleased");
        var wasSelecting = _dragSelecting;
        _dragSelecting = false;
        SurfaceRoot.ReleasePointerCapture(e.Pointer);
        if (wasSelecting)
        {
            OnContributionsSelectionChanged();
            _textInput?.NotifyLayoutChanged();
        }
        UpdateMouseCursor(e);
        e.Handled = true;
    }

    private MonacoPosition HitTest(double x, double y)
    {
        using var methodSample = _methodProfiler.Measure("HitTest");
        if (_layoutCache is null)
            return _active;
        var docY = Math.Max(0, ScrollHost.VerticalOffset + y - VerticalPadding);
        var lineNumber = _view.GetViewLineAtVerticalOffset(docY);
        var line = _view.GetViewLine(lineNumber);
        var layout = _layoutCache.GetOrCreate(lineNumber, line.Text, (float)_lineHeight);
        var localX = Math.Max(0, ScrollHost.HorizontalOffset + x - GutterWidth - LeftPadding);
        var column = NearestCaretColumn(layout, line.Text, (float)localX);
        return _view.ViewToModel(new MonacoViewPosition(lineNumber, column));
    }

    private static int NearestCaretColumn(CanvasTextLayout layout, string text, float x)
    {
        if (text.Length == 0)
            return 1;

        // Bidi layouts are not monotonic in logical UTF-16 order, so binary searching
        // caret X positions is incorrect for mixed RTL/LTR text. Evaluate grapheme-safe
        // candidates directly; pointer hit testing is infrequent enough that O(n) is fine.
        var bestOffset = 0;
        var bestDistance = Math.Abs(CaretX(layout, text, 0) - x);
        for (var offset = 0; offset < text.Length; )
        {
            var distance = Math.Abs(CaretX(layout, text, offset) - x);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestOffset = offset;
            }
            var next = MonacoUnicode.NextGraphemeBoundary(text, offset);
            offset = next > offset ? next : offset + 1;
        }
        var endDistance = Math.Abs(CaretX(layout, text, text.Length) - x);
        if (endDistance < bestDistance)
            bestOffset = text.Length;
        return bestOffset + 1;
    }

    private void RegisterDefaultCommands()
    {
        static EditorKeybinding[] Keys(params EditorKeybinding[] values) => values;
        static ValueTask Done(Action action)
        {
            action();
            return ValueTask.CompletedTask;
        }

        _commands.Register(new EditorCommandDefinition(
            "undo", "undo", "menu.category.edit", "editorTextFocus && canUndo",
            () => Done(Undo), true, 10, Keys(new EditorKeybinding("Ctrl+Z", 100))));
        _commands.Register(new EditorCommandDefinition(
            "redo", "redo", "menu.category.edit", "editorTextFocus && canRedo",
            () => Done(Redo), true, 20, Keys(new EditorKeybinding("Ctrl+Y", 100), new EditorKeybinding("Ctrl+Shift+Z", 100))));
        _commands.Register(new EditorCommandDefinition(
            "editor.action.clipboardCutAction", "editor.action.clipboardCutAction", "menu.category.clipboard", "editorTextFocus && hasSelection && !editorReadonly",
            () => Done(() => { CopySelection(); DeleteSelection(); }), true, 10, Keys(new EditorKeybinding("Ctrl+X", 100))));
        _commands.Register(new EditorCommandDefinition(
            "editor.action.clipboardCopyAction", "editor.action.clipboardCopyAction", "menu.category.clipboard", "editorTextFocus && hasSelection",
            () => Done(CopySelection), true, 20, Keys(new EditorKeybinding("Ctrl+C", 100))));
        _commands.Register(new EditorCommandDefinition(
            "editor.action.clipboardPasteAction", "editor.action.clipboardPasteAction", "menu.category.clipboard", "editorTextFocus && !editorReadonly",
            () => new ValueTask(PasteAsync()), true, 30, Keys(new EditorKeybinding("Ctrl+V", 100))));
        _commands.Register(new EditorCommandDefinition(
            "editor.action.selectAll", "editor.action.selectAll", "menu.category.clipboard", "editorTextFocus",
            () => Done(SelectAll), true, 40, Keys(new EditorKeybinding("Ctrl+A", 100))));

        RegisterNavigationCommand("cursorLeft", "Left", () => MoveHorizontal(-1, false));
        RegisterNavigationCommand("cursorLeftSelect", "Shift+Left", () => MoveHorizontal(-1, true));
        RegisterNavigationCommand("cursorRight", "Right", () => MoveHorizontal(1, false));
        RegisterNavigationCommand("cursorRightSelect", "Shift+Right", () => MoveHorizontal(1, true));
        RegisterNavigationCommand("cursorWordLeft", "Ctrl+Left", () => MoveWordHorizontal(-1, false));
        RegisterNavigationCommand("cursorWordLeftSelect", "Ctrl+Shift+Left", () => MoveWordHorizontal(-1, true));
        RegisterNavigationCommand("cursorWordRight", "Ctrl+Right", () => MoveWordHorizontal(1, false));
        RegisterNavigationCommand("cursorWordRightSelect", "Ctrl+Shift+Right", () => MoveWordHorizontal(1, true));
        RegisterNavigationCommand("cursorUp", "Up", () => MoveVertical(-1, false));
        RegisterNavigationCommand("cursorUpSelect", "Shift+Up", () => MoveVertical(-1, true));
        RegisterNavigationCommand("cursorDown", "Down", () => MoveVertical(1, false));
        RegisterNavigationCommand("cursorDownSelect", "Shift+Down", () => MoveVertical(1, true));
        RegisterNavigationCommand("cursorHome", "Home", () => MoveLineBoundary(false, false));
        RegisterNavigationCommand("cursorHomeSelect", "Shift+Home", () => MoveLineBoundary(false, true));
        RegisterNavigationCommand("cursorEnd", "End", () => MoveLineBoundary(true, false));
        RegisterNavigationCommand("cursorEndSelect", "Shift+End", () => MoveLineBoundary(true, true));

        _commands.Register(new EditorCommandDefinition(
            "deleteLeft", "deleteLeft", "menu.category.editor", "editorTextFocus && !editorReadonly",
            () => Done(Backspace), false, 0, Keys(new EditorKeybinding("Back", 100))));
        _commands.Register(new EditorCommandDefinition(
            "deleteRight", "deleteRight", "menu.category.editor", "editorTextFocus && !editorReadonly",
            () => Done(DeleteForward), false, 0, Keys(new EditorKeybinding("Delete", 100))));
        _commands.Register(new EditorCommandDefinition(
            "deleteWordLeft", "deleteWordLeft", "menu.category.editor", "editorTextFocus && !editorReadonly",
            () => Done(() => DeleteWord(backward: true)), false, 0, Keys(new EditorKeybinding("Ctrl+Back", 100))));
        _commands.Register(new EditorCommandDefinition(
            "deleteWordRight", "deleteWordRight", "menu.category.editor", "editorTextFocus && !editorReadonly",
            () => Done(() => DeleteWord(backward: false)), false, 0, Keys(new EditorKeybinding("Ctrl+Delete", 100))));
        _commands.Register(new EditorCommandDefinition(
            "monacoNative.type.lineBreak", "monacoNative.type.lineBreak", "menu.category.editor", "editorTextFocus && !editorReadonly",
            () => Done(() => ReplaceSelection(_model.EndOfLine == MonacoEndOfLine.CrLf ? "\r\n" : "\n")), false, 0, Keys(new EditorKeybinding("Enter", 100))));
        _commands.Register(new EditorCommandDefinition(
            "monacoNative.type.tab", "monacoNative.type.tab", "menu.category.editor", "editorTextFocus && !editorReadonly",
            () => Done(() => ReplaceSelection("    ")), false, 0, Keys(new EditorKeybinding("Tab", 100))));

        _commands.Register(new EditorCommandDefinition(
            "editor.action.addCommentLine", "editor.action.addCommentLine", "menu.category.editor", "editorTextFocus && !editorReadonly",
            () => Done(() => ChangeLineComment(remove: false)), true, 20, Keys(new EditorKeybinding("Ctrl+K Ctrl+C", 200))));
        _commands.Register(new EditorCommandDefinition(
            "editor.action.removeCommentLine", "editor.action.removeCommentLine", "menu.category.editor", "editorTextFocus && !editorReadonly",
            () => Done(() => ChangeLineComment(remove: true)), true, 30, Keys(new EditorKeybinding("Ctrl+K Ctrl+U", 200))));

        _commands.Register(new EditorCommandDefinition(
            "editor.action.toggleWordWrap", "editor.action.toggleWordWrap", "menu.category.view", "editorTextFocus",
            () => Done(() => WordWrap = !WordWrap), true, 10, Keys(new EditorKeybinding("Alt+Z", 100))));
        _commands.Register(new EditorCommandDefinition(
            "editor.action.toggleRenderWhitespace", "editor.action.toggleRenderWhitespace", "menu.category.view", "editorTextFocus",
            () => Done(() => ShowWhitespace = !ShowWhitespace), true, 20, Array.Empty<EditorKeybinding>()));
        _commands.Register(new EditorCommandDefinition(
            "editor.action.toggleMinimap", "editor.action.toggleMinimap", "menu.category.view", "editorTextFocus",
            () => Done(() => ShowMinimap = !ShowMinimap), true, 30, Array.Empty<EditorKeybinding>()));
        _commands.Register(new EditorCommandDefinition(
            "monacoNative.toggleIndentGuides", "monacoNative.toggleIndentGuides", "menu.category.view", "editorTextFocus",
            () => Done(() => ShowIndentGuides = !ShowIndentGuides), true, 40, Array.Empty<EditorKeybinding>()));
        _commands.Register(new EditorCommandDefinition(
            "monacoNative.toggleFolding", "monacoNative.toggleFolding", "menu.category.view", "editorTextFocus",
            () => Done(() => ShowFolding = !ShowFolding), true, 50, Array.Empty<EditorKeybinding>()));
        _commands.Register(new EditorCommandDefinition(
            "monacoNative.toggleOverviewRuler", "monacoNative.toggleOverviewRuler", "menu.category.view", "editorTextFocus",
            () => Done(() => ShowOverviewRuler = !ShowOverviewRuler), true, 60, Array.Empty<EditorKeybinding>()));
        _commands.Register(new EditorCommandDefinition(
            "monacoNative.toggleBracketColors", "monacoNative.toggleBracketColors", "menu.category.view", "editorTextFocus",
            () => Done(() => BracketPairColorization = !BracketPairColorization), true, 70, Array.Empty<EditorKeybinding>()));
        _commands.Register(new EditorCommandDefinition(
            "monacoNative.toggleCodeLens", "monacoNative.toggleCodeLens", "menu.category.view", "editorTextFocus",
            () => Done(() => CodeLensEnabled = !CodeLensEnabled), true, 80, Array.Empty<EditorKeybinding>()));
        _commands.Register(new EditorCommandDefinition(
            "monacoNative.toggleInlineCompletions", "monacoNative.toggleInlineCompletions", "menu.category.view", "editorTextFocus",
            () => Done(() => InlineCompletionsEnabled = !InlineCompletionsEnabled), true, 90, Array.Empty<EditorKeybinding>()));
        _commands.Register(new EditorCommandDefinition(
            "monacoNative.toggleStickyScroll", "monacoNative.toggleStickyScroll", "menu.category.view", "editorTextFocus",
            () => Done(() => StickyScrollEnabled = !StickyScrollEnabled), true, 100, Array.Empty<EditorKeybinding>()));
    }

    private void RegisterNavigationCommand(string id, string chord, Action action)
    {
        _commands.Register(new EditorCommandDefinition(
            id, id, "menu.category.editor", "editorTextFocus",
            () =>
            {
                action();
                return ValueTask.CompletedTask;
            },
            false, 0, new[] { new EditorKeybinding(chord, 100) }));
    }

    private void UpdateCommandContext()
    {
        using var methodSample = _methodProfiler.Measure("UpdateCommandContext");
        if (_disposed)
            return;
        var (start, end) = OrderedSelectionOffsets();
        var hasAnySelection = start != end || _secondarySelections.Any(selection =>
            _model.GetOffsetAt(selection.Anchor) != _model.GetOffsetAt(selection.Active));
        _commands.SetContext("editorTextFocus", _executingContextMenuCommand || FocusState != FocusState.Unfocused);
        _commands.SetContext("editorReadonly", _isReadOnly);
        _commands.SetContext("hasSelection", hasAnySelection);
        _commands.SetContext("canUndo", _model.CanUndo);
        _commands.SetContext("canRedo", _model.CanRedo);
        _commands.SetContext("languageId", _model.LanguageId);
        _commands.SetContext("wordWrap", _wordWrap);
        _commands.SetContext("showWhitespace", _showWhitespace);
        _commands.SetContext("showMinimap", _showMinimap);
    }


    private void Localization_CultureChanged(object? sender, EventArgs e)
    {
        // Context-menu items are rebuilt on opening, so clearing here guarantees no
        // stale labels survive if a flyout was materialized before the locale switch.
        _contextMenu.Items.Clear();
        RefreshContributionLocalization();
        UiLanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Returns the effective three-state access policy for a registered command.</summary>
    public EditorCommandAvailability GetCommandAvailability(string commandId)
    {
        _commands.TryGetCommand(commandId, out var definition);
        var option = ContextMenuItemOptions.FirstOrDefault(setting => setting.CommandId == commandId);
        if (option is not null && option.Mode != EditorCommandAvailability.Default)
            return option.Mode;
        var access = EditorCommandAccessPolicy.DefaultFor(commandId,
            definition?.ShowInContextMenu ?? false, GetMenuToggleState(commandId).HasValue);
        // Respect explicitly supplied legacy menu-only properties without treating
        // the default value of Visibility=Visible as an override for advanced commands.
        if (option is not null && option.ReadLocalValue(EditorContextMenuItemOptions.VisibilityProperty) != DependencyProperty.UnsetValue)
            access = option.Visibility == Visibility.Visible ? EditorCommandAvailability.ContextMenuAndShortcut
                : EditorCommandAvailability.ShortcutOnly;
        return access;
    }

    /// <summary>
    /// Show or hide a toggle command in the context menu from host code. This changes
    /// only its menu surface: toolbar controls and command shortcuts keep working.
    /// To forbid keyboard execution too, set the option's Mode to Disabled instead.
    /// </summary>
    public void SetContextMenuToggleVisibility(string commandId, bool visible)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        if (GetMenuToggleState(commandId) is null)
            throw new ArgumentException("Command is not a context-menu toggle.", nameof(commandId));
        var option = ContextMenuItemOptions.FirstOrDefault(o => o.CommandId == commandId);
        if (option is null)
        {
            option = new EditorContextMenuItemOptions { CommandId = commandId };
            ContextMenuItemOptions.Add(option);
        }
        option.Mode = visible ? EditorCommandAvailability.ContextMenuAndShortcut
                              : EditorCommandAvailability.ShortcutOnly;
    }

    /// <summary>Advanced non-toggle actions available to configure in the sample or XAML.</summary>
    public IReadOnlyList<(string CommandId, string Label)> GetConfigurableCommands() =>
        _commands.Commands.Where(command => command.ShowInContextMenu &&
                !EditorCommandAccessPolicy.IsBasic(command.Id) && !GetMenuToggleState(command.Id).HasValue &&
                !command.Id.StartsWith("monacoNative.action.toggle", StringComparison.Ordinal))
            .OrderBy(command => command.MenuOrder)
            .Select(command => (command.Id, _commands.GetLabel(command)))
            .ToArray();

    private void ContextMenu_Opening(object? sender, object e)
    {
        UpdateCommandContext();
        // Flyout steals focus. Menu invocation still operates on this editor and must
        // keep editorTextFocus true, while read-only/selection/undo guards remain intact.
        _commands.SetContext("editorTextFocus", true);
        _contextMenu.Items.Clear();
        string? lastCategory = null;
        foreach (var command in _commands.Commands
            .Where(command => command.ShowInContextMenu ||
                ContextMenuItemOptions.Any(option => option.CommandId == command.Id &&
                    option.Mode == EditorCommandAvailability.ContextMenuAndShortcut))
            .OrderBy(command => command.CategoryKey, StringComparer.Ordinal)
            .ThenBy(command => command.MenuOrder).ThenBy(command => command.Id, StringComparer.Ordinal))
        {
            if (!EditorCommandAccessPolicy.ShowInMenu(GetCommandAvailability(command.Id)))
                continue;
            var options = ContextMenuItemOptions.FirstOrDefault(option => option.CommandId == command.Id);
            var enabled = (options?.IsEnabled ?? true) && _commands.ContextMatches(command.When);
            var isToggle = GetMenuToggleState(command.Id);
            MenuFlyoutItemBase item;
            if (isToggle is bool checkedState)
            {
                item = new ToggleMenuFlyoutItem
                {
                    Text = _commands.GetLabel(command),
                    IsChecked = checkedState,
                    IsEnabled = enabled,
                    Tag = command.Id,
                    KeyboardAcceleratorTextOverride = ShortcutLabel(command),
                };
                ((ToggleMenuFlyoutItem)item).Click += (_, _) => _ = ExecuteContextMenuCommandAsync(command.Id);
            }
            else
            {
                item = new MenuFlyoutItem
                {
                    Text = _commands.GetLabel(command),
                    IsEnabled = enabled,
                    Tag = command.Id,
                    Style = Resources["EditorMenuFlyoutItemStyle"] as Style,
                    KeyboardAcceleratorTextOverride = ShortcutLabel(command),
                };
                ((MenuFlyoutItem)item).Click += (_, _) => _ = ExecuteContextMenuCommandAsync(command.Id);
            }
            AutomationProperties.SetAutomationId(item, "EditorContextMenu_" + command.Id);
            if (lastCategory is not null && !string.Equals(lastCategory, command.CategoryKey, StringComparison.Ordinal))
                _contextMenu.Items.Add(new MenuFlyoutSeparator());
            lastCategory = command.CategoryKey;
            _contextMenu.Items.Add(item);
        }
    }

    private void ContextMenu_Closed(object? sender, object e)
    {
        if (!_disposed && !_executingContextMenuCommand)
            UpdateCommandContext();
    }

    private bool? GetMenuToggleState(string id) => id switch
    {
        "editor.action.toggleWordWrap" => WordWrap,
        "editor.action.toggleRenderWhitespace" => ShowWhitespace,
        "editor.action.toggleMinimap" => ShowMinimap,
        "monacoNative.toggleIndentGuides" => ShowIndentGuides,
        "monacoNative.toggleFolding" => ShowFolding,
        "monacoNative.toggleOverviewRuler" => ShowOverviewRuler,
        "monacoNative.toggleBracketColors" => BracketPairColorization,
        "monacoNative.toggleCodeLens" => CodeLensEnabled,
        "monacoNative.toggleInlineCompletions" => InlineCompletionsEnabled,
        "monacoNative.toggleStickyScroll" => StickyScrollEnabled,
        _ => null,
    };

    private async Task ExecuteContextMenuCommandAsync(string commandId)
    {
        _executingContextMenuCommand = true;
        try
        {
            UpdateCommandContext();
            await _commands.TryExecuteAsync(commandId);
            if (GetMenuToggleState(commandId).HasValue)
                EditorViewOptionsChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _executingContextMenuCommand = false;
            if (!_disposed)
            {
                Focus(FocusState.Programmatic);
                UpdateCommandContext();
            }
        }
    }

    private static string ShortcutLabel(EditorCommandDefinition command)
    {
        var binding = command.Keybindings.OrderByDescending(value => value.Weight).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(binding.Chord))
            return string.Empty;

        return string.Join(" ", binding.Chord.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(stroke =>
        {
            var separator = stroke.LastIndexOf('+');
            var prefix = separator >= 0 ? stroke[..(separator + 1)] : string.Empty;
            var key = separator >= 0 ? stroke[(separator + 1)..] : stroke;
            key = key switch
            {
                "OemPeriod" => ".",
                "Back" => "Backspace",
                _ => key,
            };
            return prefix + key;
        }));
    }

    private void SelectAll()
    {
        ClearSecondarySelections();
        _anchor = new MonacoPosition(1, 1);
        _active = _model.GetPositionAt(_model.Length);
        _preferredCaretX = null;
        OnSelectionChanged();
    }

    private void ChangeLineComment(bool remove)
    {
        if (_isReadOnly)
            return;
        var (startOffset, endOffset) = OrderedSelectionOffsets();
        var start = _model.GetPositionAt(startOffset);
        var end = _model.GetPositionAt(endOffset);
        var firstLine = start.LineNumber;
        var lastLine = end.LineNumber;
        if (endOffset > startOffset && end.Column == 1 && lastLine > firstLine)
            --lastLine;

        var prefix = LineCommentPrefix(_model.LanguageId);
        using var transaction = _model.BeginUndoTransaction();
        for (var line = lastLine; line >= firstLine; --line)
        {
            var text = _model.GetLineContent(line);
            var indentation = 0;
            while (indentation < text.Length && (text[indentation] == ' ' || text[indentation] == '\t'))
                ++indentation;
            var lineStart = _model.GetOffsetAt(new MonacoPosition(line, indentation + 1));
            if (remove)
            {
                if (!text.Substring(indentation).StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                var removeLength = prefix.Length;
                if (indentation + removeLength < text.Length && text[indentation + removeLength] == ' ')
                    ++removeLength;
                _model.Replace(lineStart, lineStart + (nuint)removeLength, string.Empty);
            }
            else
            {
                _model.Replace(lineStart, lineStart, prefix + " ");
            }
        }
        transaction.Complete();
        _anchor = new MonacoPosition(firstLine, 1);
        _active = new MonacoPosition(lastLine, _model.GetLineMaxColumn(lastLine));
        AfterProjectionChanged(true);
        OnSelectionChanged();
    }

    private string LineCommentPrefix(string languageId)
    {
        var configured = _languages.Languages.GetLanguageConfiguration(languageId)?.Comments?.LineComment;
        if (!string.IsNullOrEmpty(configured))
            return configured;
        return languageId.ToLowerInvariant() switch
        {
            "python" or "shellscript" or "ruby" or "yaml" => "#",
            "sql" or "lua" => "--",
            _ => "//",
        };
    }

    private async void Editor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // A TSF-aware application must offer the Win32-style key message to ITfKeystrokeMgr
        // before application commands/text handling. If an active text service eats it,
        // stop here; otherwise Monaco keybindings/CharacterReceived continue normally.
        var tsfKey = e.OriginalKey == VirtualKey.None ? e.Key : e.OriginalKey;
        if (_textInput?.TryHandleKeyDown(
                (uint)tsfKey,
                BuildWin32KeyMessageLParam(e.KeyStatus, keyUp: false)) == true)
        {
            e.Handled = true;
            return;
        }

        // Printable text is delivered by CharacterReceived. Do not send the overwhelmingly
        // common unmodified text-key path through the native command resolver first.
        var ctrl = IsKeyDown(VirtualKey.Control);
        var alt = IsKeyDown(VirtualKey.Menu);
        var meta = IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows);
        var altGr = ctrl && alt && !meta;
        // Ctrl+Alt is treated as AltGr by the existing IME policy. If TSF did not eat it,
        // leave it on the text/CharacterReceived path rather than stealing it as a command.
        if (!_commands.InChordMode && altGr)
            return;
        // Ordinary printable input bypasses the resolver, matching Monaco's fast path. Once a
        // chord is in flight, however, every subsequent keypress must be offered to the resolver
        // because a valid second/third chord is not required to carry Ctrl/Alt/Meta.
        if (!_commands.InChordMode && !ctrl && !alt && !meta && !NeedsEditorKeybindingRouting(e.Key))
            return;

        var modifiers = MonacoKeyModifiers.None;
        if (ctrl) modifiers |= MonacoKeyModifiers.Ctrl;
        if (IsKeyDown(VirtualKey.Shift)) modifiers |= MonacoKeyModifiers.Shift;
        if (alt) modifiers |= MonacoKeyModifiers.Alt;
        if (meta) modifiers |= MonacoKeyModifiers.Meta;

        var keyName = NormalizeVirtualKey(e.Key);
        var result = await _commands.ResolveAndExecuteAsync(keyName, modifiers);
        switch (result.Kind)
        {
        case MonacoKeybindingResultKind.Command:
            _chordTimer.Stop();
            UpdateCommandContext();
            e.Handled = result.PreventDefault;
            break;
        case MonacoKeybindingResultKind.MoreChordsNeeded:
            _chordTimer.Stop();
            _chordTimer.Start();
            e.Handled = true;
            break;
        case MonacoKeybindingResultKind.ChordCancelled:
            _chordTimer.Stop();
            e.Handled = true;
            break;
        case MonacoKeybindingResultKind.NoMatch:
        default:
            break;
        }
    }

    private void Editor_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        var tsfKey = e.OriginalKey == VirtualKey.None ? e.Key : e.OriginalKey;
        if (_textInput?.TryHandleKeyUp(
                (uint)tsfKey,
                BuildWin32KeyMessageLParam(e.KeyStatus, keyUp: true)) == true)
            e.Handled = true;
    }

    private static uint BuildWin32KeyMessageLParam(Windows.UI.Core.CorePhysicalKeyStatus status, bool keyUp)
    {
        // CorePhysicalKeyStatus exposes the fields encoded in WM_KEYDOWN/WM_KEYUP lParam.
        var repeat = Math.Clamp(status.RepeatCount, 1u, 0xFFFFu);
        uint value = repeat | ((status.ScanCode & 0xFFu) << 16);
        if (status.IsExtendedKey) value |= 1u << 24;
        if (status.IsMenuKeyDown) value |= 1u << 29;
        if (status.WasKeyDown || keyUp) value |= 1u << 30;
        if (status.IsKeyReleased || keyUp) value |= 1u << 31;
        return value;
    }

    private static bool NeedsEditorKeybindingRouting(VirtualKey key)
    {
        if (key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down or
            VirtualKey.Home or VirtualKey.End or VirtualKey.PageUp or VirtualKey.PageDown or
            VirtualKey.Back or VirtualKey.Delete or VirtualKey.Enter or VirtualKey.Tab or
            VirtualKey.Escape or VirtualKey.Insert)
            return true;

        var value = (int)key;
        return value is >= 0x70 and <= 0x87; // F1..F24
    }

    private static string NormalizeVirtualKey(VirtualKey key)
    {
        // WinUI exposes the Win32 VK identity, including OEM keys whose enum ToString() can be a
        // numeric value. Keep those physical/logical identities stable across keyboard layouts;
        // KeybindingResolver.Normalize canonicalizes aliases on the native side as a second gate.
        return (int)key switch
        {
            0xBA => "OemSemicolon",
            0xBB => "OemPlus",
            0xBC => "OemComma",
            0xBD => "OemMinus",
            0xBE => "OemPeriod",
            0xBF => "OemQuestion",
            0xC0 => "OemTilde",
            0xDB => "OemOpenBrackets",
            0xDC => "OemPipe",
            0xDD => "OemCloseBrackets",
            0xDE => "OemQuotes",
            0xE2 => "Oem102",
            _ => key.ToString(),
        };
    }

    private void Editor_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        using var methodSample = _methodProfiler.Measure("Editor_CharacterReceived");
        if (_textInput?.SuppressCharacterReceived == true)
            return; // The active text service explicitly owns this character delivery path.
        var ctrl = IsKeyDown(VirtualKey.Control);
        var alt = IsKeyDown(VirtualKey.Menu);
        var meta = IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows);
        var altGr = ctrl && alt && !meta;
        if (_isReadOnly || meta || ((ctrl || alt) && !altGr))
            return;
        var ch = e.Character;
        if (char.IsControl(ch))
            return;
        var text = _fallbackCharacterAccumulator.Push(ch);
        if (!string.IsNullOrEmpty(text))
            ReplaceSelection(text);
        e.Handled = true;
    }

    private static bool IsKeyDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    private void MoveHorizontal(int direction, bool extend)
    {
        using var methodSample = _methodProfiler.Measure("MoveHorizontal");
        var text = _model.Text;
        if (_secondarySelections.Count > 0)
        {
            TransformSelections(position =>
            {
                var offset = checked((int)_model.GetOffsetAt(position));
                var target = direction < 0 ? PreviousTextElementOffset(text, offset) : NextTextElementOffset(text, offset);
                return _model.GetPositionAt((nuint)target);
            }, extend);
            return;
        }
        var offset = _model.GetOffsetAt(_active);
        var target = direction < 0 ? PreviousTextElementOffset(text, (int)offset) : NextTextElementOffset(text, (int)offset);
        SetActive(_model.GetPositionAt((nuint)target), extend);
    }

    private void MoveWordHorizontal(int direction, bool extend)
    {
        var text = _model.Text;
        MonacoPosition Move(MonacoPosition position)
        {
            var offset = checked((int)_model.GetOffsetAt(position));
            var target = direction < 0
                ? MonacoUnicode.PreviousWordBoundary(text, offset)
                : MonacoUnicode.NextWordBoundary(text, offset);
            return _model.GetPositionAt((nuint)target);
        }

        if (_secondarySelections.Count > 0)
        {
            TransformSelections(Move, extend);
            return;
        }
        SetActive(Move(_active), extend);
    }

    private void MoveVertical(int delta, bool extend)
    {
        using var methodSample = _methodProfiler.Measure("MoveVertical");
        if (_secondarySelections.Count > 0)
        {
            TransformSelections(position =>
            {
                var currentPosition = _view.ModelToView(position);
                var targetLineNumber = Math.Clamp(currentPosition.LineNumber + delta, 1, _view.ViewLineCount);
                if (_layoutCache is null)
                {
                    var fallback = _view.GetViewLineText(targetLineNumber);
                    return _view.ViewToModel(new MonacoViewPosition(targetLineNumber, Math.Clamp(currentPosition.Column, 1, fallback.Length + 1)));
                }
                var sourceLine = _view.GetViewLine(currentPosition.LineNumber);
                var sourceLayout = _layoutCache.GetOrCreate(currentPosition.LineNumber, sourceLine.Text, (float)_lineHeight);
                var desiredX = CaretX(sourceLayout, sourceLine.Text, Math.Clamp(currentPosition.Column - 1, 0, sourceLine.Text.Length));
                var targetLine = _view.GetViewLine(targetLineNumber);
                var targetLayout = _layoutCache.GetOrCreate(targetLineNumber, targetLine.Text, (float)_lineHeight);
                var targetColumn = NearestCaretColumn(targetLayout, targetLine.Text, desiredX);
                return _view.ViewToModel(new MonacoViewPosition(targetLineNumber, targetColumn));
            }, extend);
            return;
        }

        var current = _view.ModelToView(_active);
        var targetLine = Math.Clamp(current.LineNumber + delta, 1, _view.ViewLineCount);
        if (_layoutCache is null)
        {
            var fallbackText = _view.GetViewLineText(targetLine);
            SetActive(_view.ViewToModel(new MonacoViewPosition(targetLine, Math.Clamp(current.Column, 1, fallbackText.Length + 1))), extend, preservePreferredX: true);
            return;
        }

        if (!_preferredCaretX.HasValue)
        {
            var currentLine = _view.GetViewLine(current.LineNumber);
            var currentLayout = _layoutCache.GetOrCreate(current.LineNumber, currentLine.Text, (float)_lineHeight);
            _preferredCaretX = CaretX(currentLayout, currentLine.Text, Math.Clamp(current.Column - 1, 0, currentLine.Text.Length));
        }

        var target = _view.GetViewLine(targetLine);
        var targetLayout = _layoutCache.GetOrCreate(targetLine, target.Text, (float)_lineHeight);
        var targetColumn = NearestCaretColumn(targetLayout, target.Text, _preferredCaretX.Value);
        SetActive(_view.ViewToModel(new MonacoViewPosition(targetLine, targetColumn)), extend, preservePreferredX: true);
    }

    private void MoveLineBoundary(bool end, bool extend)
    {
        if (_secondarySelections.Count > 0)
        {
            TransformSelections(position =>
            {
                var current = _view.ModelToView(position);
                var text = _view.GetViewLineText(current.LineNumber);
                return _view.ViewToModel(new MonacoViewPosition(current.LineNumber, end ? text.Length + 1 : 1));
            }, extend);
            return;
        }
        var current = _view.ModelToView(_active);
        var text = _view.GetViewLineText(current.LineNumber);
        SetActive(_view.ViewToModel(new MonacoViewPosition(current.LineNumber, end ? text.Length + 1 : 1)), extend);
    }

    private void SetActive(MonacoPosition position, bool extend, bool preservePreferredX = false)
    {
        _active = position;
        if (!extend)
            _anchor = position;
        if (!preservePreferredX)
            _preferredCaretX = null;
        OnSelectionChanged();
    }

    private void Backspace()
    {
        using var methodSample = _methodProfiler.Measure("Backspace");
        if (_isReadOnly)
            return;
        if (_secondarySelections.Count > 0)
        {
            ReplaceMultipleSelections(string.Empty, deleteBackward: true);
            return;
        }
        var (start, end) = OrderedSelectionOffsets();
        if (start != end)
        {
            ReplaceSelection(string.Empty);
            return;
        }
        if (start == 0)
            return;
        var text = _model.Text;
        var previous = PreviousTextElementOffset(text, checked((int)start));
        _model.Replace((nuint)previous, start, string.Empty);
        _anchor = _active = _model.GetPositionAt((nuint)previous);
        AfterProjectionChanged(true);
    }

    private void DeleteForward()
    {
        using var methodSample = _methodProfiler.Measure("DeleteForward");
        if (_isReadOnly)
            return;
        if (_secondarySelections.Count > 0)
        {
            ReplaceMultipleSelections(string.Empty, deleteForward: true);
            return;
        }
        var (start, end) = OrderedSelectionOffsets();
        if (start != end)
        {
            ReplaceSelection(string.Empty);
            return;
        }
        if (start >= _model.Length)
            return;
        var text = _model.Text;
        var next = NextTextElementOffset(text, checked((int)start));
        _model.Replace(start, (nuint)next, string.Empty);
        _anchor = _active = _model.GetPositionAt(start);
        AfterProjectionChanged(true);
    }

    private void DeleteWord(bool backward)
    {
        if (_isReadOnly)
            return;
        if (_secondarySelections.Count > 0)
        {
            ReplaceMultipleSelections(string.Empty, deleteBackward: backward, deleteForward: !backward, byWord: true);
            return;
        }

        var (start, end) = OrderedSelectionOffsets();
        if (start != end)
        {
            ReplaceSelection(string.Empty);
            return;
        }
        if ((backward && start == 0) || (!backward && end >= _model.Length))
            return;

        var text = _model.Text;
        if (backward)
        {
            var previous = MonacoUnicode.PreviousWordBoundary(text, checked((int)start));
            _model.Replace((nuint)previous, start, string.Empty);
            _anchor = _active = _model.GetPositionAt((nuint)previous);
        }
        else
        {
            var next = MonacoUnicode.NextWordBoundary(text, checked((int)end));
            _model.Replace(end, (nuint)next, string.Empty);
            _anchor = _active = _model.GetPositionAt(end);
        }
        AfterProjectionChanged(true);
    }

    private void DeleteSelection()
    {
        if (_isReadOnly)
            return;
        var (start, end) = OrderedSelectionOffsets();
        if (start != end)
            ReplaceSelection(string.Empty);
    }

    private void ReplaceSelection(string text)
    {
        using var methodSample = _methodProfiler.Measure("ReplaceSelection");
        if (_isReadOnly)
            return;
        if (_secondarySelections.Count > 0)
        {
            ReplaceMultipleSelections(text);
            return;
        }
        var (start, end) = OrderedSelectionOffsets();
        _model.Replace(start, end, text);
        var newOffset = start + (nuint)text.Length;
        _anchor = _active = _model.GetPositionAt(newOffset);
        AfterProjectionChanged(true);
    }

    private void CopySelection()
    {
        if (_secondarySelections.Count > 0)
        {
            var combined = GetMultiSelectionClipboardText();
            if (combined.Length == 0) return;
            var multiPackage = new DataPackage();
            multiPackage.SetText(combined);
            Clipboard.SetContent(multiPackage);
            return;
        }
        var (start, end) = OrderedSelectionOffsets();
        if (start == end)
            return;
        var package = new DataPackage();
        package.SetText(_model.GetTextRange(start, end - start));
        Clipboard.SetContent(package);
    }

    private async Task PasteAsync()
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Text))
            return;
        ReplaceSelection(await content.GetTextAsync());
    }

    private (nuint Start, nuint End) OrderedSelectionOffsets()
    {
        var a = _model.GetOffsetAt(_anchor);
        var b = _model.GetOffsetAt(_active);
        return a <= b ? (a, b) : (b, a);
    }


    private void SetSelectionOffsetsFromTextInput(nuint start, nuint end)
    {
        var max = _model.Length;
        start = Math.Min(start, max);
        end = Math.Min(end, max);
        ClearSecondarySelections();
        _anchor = _model.GetPositionAt(start);
        _active = _model.GetPositionAt(end);
        _preferredCaretX = null;
        UpdateCommandContext();
        ResetCaretBlink();
        RevealCaret();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        RaiseAccessibilitySelectionChanged();
    }

    private nint GetNativeTsfHwnd()
    {
        try
        {
            var root = XamlRoot;
            if (root is null)
                return 0;
            return Microsoft.UI.Win32Interop.GetWindowFromWindowId(root.ContentIslandEnvironment.AppWindowId);
        }
        catch
        {
            return 0;
        }
    }

    private (int Start, int End, bool ActiveEndAtStart) GetNativeTsfSelection()
    {
        using var methodSample = _methodProfiler.Measure("GetNativeTsfSelection");
        var anchor = checked((int)Math.Min(_model.GetOffsetAt(_anchor), (nuint)int.MaxValue));
        var active = checked((int)Math.Min(_model.GetOffsetAt(_active), (nuint)int.MaxValue));
        return anchor <= active
            ? (anchor, active, false)
            : (active, anchor, true);
    }

    private void SetNativeTsfSelection(int start, int end, bool activeEndAtStart)
    {
        using var methodSample = _methodProfiler.Measure("SetNativeTsfSelection");
        var max = checked((int)Math.Min(_model.Length, (nuint)int.MaxValue));
        start = Math.Clamp(start, 0, max);
        end = Math.Clamp(end, start, max);
        ClearSecondarySelections();
        if (activeEndAtStart && start != end)
        {
            _anchor = _model.GetPositionAt((nuint)end);
            _active = _model.GetPositionAt((nuint)start);
        }
        else
        {
            _anchor = _model.GetPositionAt((nuint)start);
            _active = _model.GetPositionAt((nuint)end);
        }
        _preferredCaretX = null;
        OnSelectionChanged();
    }

    private bool NativeTsfHasLayout() =>
        _loaded && _layoutCache is not null && XamlRoot is not null &&
        ActualWidth > 0 && ActualHeight > 0 && _view.ViewLineCount > 0;

    private (bool Success, int Acp) NativeTsfPointToAcp(int screenX, int screenY, uint flags)
    {
        if (!NativeTsfHasLayout() || XamlRoot is null)
            return (false, 0);
        try
        {
            var rootPoint = XamlRoot.CoordinateConverter.ConvertScreenToLocal(
                new Windows.Graphics.PointInt32(screenX, screenY));
            var rootContent = XamlRoot.Content;
            var toEditor = rootContent.TransformToVisual(SurfaceRoot);
            var editorPoint = toEditor.TransformPoint(rootPoint);
            var position = HitTest(editorPoint.X, editorPoint.Y);
            var offset = checked((int)Math.Min(_model.GetOffsetAt(position), (nuint)int.MaxValue));
            return (true, offset);
        }
        catch
        {
            return (false, 0);
        }
    }

    private (bool Success, NativeTsfRect Rect, bool Clipped) NativeTsfTextRect(int start, int end)
    {
        if (!NativeTsfHasLayout() || XamlRoot is null || _layoutCache is null)
            return (false, default, false);

        var max = checked((int)Math.Min(_model.Length, (nuint)int.MaxValue));
        start = Math.Clamp(start, 0, max);
        end = Math.Clamp(end, start, max);
        var startView = _view.ModelToView(_model.GetPositionAt((nuint)start));
        var endView = _view.ModelToView(_model.GetPositionAt((nuint)end));
        var firstLine = Math.Min(startView.LineNumber, endView.LineNumber);
        var lastLine = Math.Max(startView.LineNumber, endView.LineNumber);

        Windows.Foundation.Rect? bounds = null;
        for (var lineNumber = firstLine; lineNumber <= lastLine; ++lineNumber)
        {
            var line = _view.GetViewLine(lineNumber);
            var layout = _layoutCache.GetOrCreate(lineNumber, line.Text, (float)_lineHeight);
            var startIndex = lineNumber == startView.LineNumber ? Math.Clamp(startView.Column - 1, 0, line.Text.Length) : 0;
            var endIndex = lineNumber == endView.LineNumber ? Math.Clamp(endView.Column - 1, 0, line.Text.Length) : line.Text.Length;
            if (endIndex < startIndex)
                (startIndex, endIndex) = (endIndex, startIndex);

            var regions = TextRangeRegions(layout, line.Text, startIndex, endIndex);
            var y = VerticalPadding + line.VerticalOffset - ScrollHost.VerticalOffset;
            if (regions.Length == 0)
            {
                var x1 = CaretX(layout, line.Text, startIndex);
                var x2 = CaretX(layout, line.Text, endIndex);
                var rect = new Windows.Foundation.Rect(
                    GutterWidth + LeftPadding + Math.Min(x1, x2) - ScrollHost.HorizontalOffset,
                    y,
                    Math.Max(2, Math.Abs(x2 - x1)),
                    Math.Max(2, _lineHeight));
                bounds = bounds.HasValue ? Union(bounds.Value, rect) : rect;
            }
            else
            {
                foreach (var region in regions)
                {
                    var r = region.LayoutBounds;
                    var rect = new Windows.Foundation.Rect(
                        GutterWidth + LeftPadding + r.X - ScrollHost.HorizontalOffset,
                        y + r.Y,
                        Math.Max(2, r.Width),
                        Math.Max(2, r.Height));
                    bounds = bounds.HasValue ? Union(bounds.Value, rect) : rect;
                }
            }
        }

        var local = bounds ?? new Windows.Foundation.Rect(
            GutterWidth + LeftPadding,
            VerticalPadding,
            2,
            Math.Max(2, _lineHeight));
        var clipped = local.X < 0 || local.Y < 0 || local.Right > ActualWidth || local.Bottom > ActualHeight;
        var screen = ConvertEditorLocalRectToCoreTextBounds(local).Screen;
        var rectValue = new NativeTsfRect(
            (int)Math.Floor(screen.X),
            (int)Math.Floor(screen.Y),
            (int)Math.Ceiling(screen.Right),
            (int)Math.Ceiling(screen.Bottom));
        return (true, rectValue, clipped);
    }

    private (bool Success, NativeTsfRect Rect) NativeTsfScreenRect()
    {
        if (XamlRoot is null || ActualWidth <= 0 || ActualHeight <= 0)
            return (false, default);
        var screen = ConvertEditorLocalRectToCoreTextBounds(
            new Windows.Foundation.Rect(0, 0, ActualWidth, ActualHeight)).Screen;
        return (true, new NativeTsfRect(
            (int)Math.Floor(screen.X),
            (int)Math.Floor(screen.Y),
            (int)Math.Ceiling(screen.Right),
            (int)Math.Ceiling(screen.Bottom)));
    }

    private (Windows.Foundation.Rect Screen, Windows.Foundation.Rect VisualPixels) ConvertEditorLocalRectToCoreTextBounds(
        Windows.Foundation.Rect localRect)
    {
        if (XamlRoot is null)
            return (localRect, localRect);

        var rootContent = XamlRoot.Content;
        var transform = TransformToVisual(rootContent);
        var topLeft = transform.TransformPoint(new Windows.Foundation.Point(localRect.X, localRect.Y));
        var bottomRight = transform.TransformPoint(new Windows.Foundation.Point(localRect.Right, localRect.Bottom));
        var rootRect = new Windows.Foundation.Rect(
            Math.Min(topLeft.X, bottomRight.X),
            Math.Min(topLeft.Y, bottomRight.Y),
            Math.Max(1, Math.Abs(bottomRight.X - topLeft.X)),
            Math.Max(1, Math.Abs(bottomRight.Y - topLeft.Y)));

        // LayoutBoundsVisualPixels is viewport-relative and rasterized. LayoutBounds is
        // absolute screen space. XamlRoot's coordinate converter supplies the latter and
        // already applies rasterization scale; the former is the root-local rect scaled
        // into visual pixels without the top-level window screen offset.
        var scale = Math.Max(0.01, XamlRoot.RasterizationScale);
        var visualPixels = new Windows.Foundation.Rect(
            rootRect.X * scale,
            rootRect.Y * scale,
            Math.Max(1, rootRect.Width * scale),
            Math.Max(1, rootRect.Height * scale));
        var screen = XamlRoot.CoordinateConverter.ConvertLocalToScreen(rootRect);
        var screenRect = new Windows.Foundation.Rect(
            screen.X,
            screen.Y,
            Math.Max(1, screen.Width),
            Math.Max(1, screen.Height));
        return (screenRect, visualPixels);
    }

    private static Windows.Foundation.Rect Union(Windows.Foundation.Rect a, Windows.Foundation.Rect b)
    {
        var left = Math.Min(a.X, b.X);
        var top = Math.Min(a.Y, b.Y);
        var right = Math.Max(a.Right, b.Right);
        var bottom = Math.Max(a.Bottom, b.Bottom);
        return new Windows.Foundation.Rect(left, top, right - left, bottom - top);
    }

    private void TextInput_StateChanged(object? sender, EventArgs e)
    {
        if (_textInput?.IsComposing == true)
        {
            _commands.ResetChord();
            _chordTimer.Stop();
        }
        ImeStateChanged?.Invoke(this, EventArgs.Empty);
        _textInput?.NotifyLayoutChanged();
        RaiseAccessibilityLayoutInvalidated();
        Render(RenderDirty.Caret);

    }

    private static int PreviousTextElementOffset(string text, int offset) =>
        MonacoUnicode.PreviousGraphemeBoundary(text, offset);

    private static int NextTextElementOffset(string text, int offset) =>
        MonacoUnicode.NextGraphemeBoundary(text, offset);

    private MonacoPosition Normalize(int line, int column)
    {
        line = Math.Clamp(line, 1, checked((int)_model.LineCount));
        column = Math.Clamp(column, 1, _model.GetLineMaxColumn(line));
        return new MonacoPosition(line, column);
    }

    private void RestoreSelectionOffsets(nuint anchorOffset, nuint activeOffset)
    {
        var max = _model.Length;
        _anchor = _model.GetPositionAt(anchorOffset <= max ? anchorOffset : max);
        _active = _model.GetPositionAt(activeOffset <= max ? activeOffset : max);
    }

    private void OnSelectionChanged()
    {
        using var methodSample = _methodProfiler.Measure("OnSelectionChanged");
        UpdateCommandContext();
        ResetCaretBlink();
        RevealCaret();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        // Drag selection fires on every character boundary. Language widgets are
        // refreshed on release, not by canceling/recreating tasks while dragging.
        if (!_dragSelecting) OnContributionsSelectionChanged();
        RaiseAccessibilitySelectionChanged();
        _textInput?.NotifySelectionChanged();
        if (!_dragSelecting) _textInput?.NotifyLayoutChanged();
        Render(RenderDirty.Selection | RenderDirty.Brackets | RenderDirty.Overview | RenderDirty.Caret);
    }

    private void RevealCaret()
    {
        using var methodSample = _methodProfiler.Measure("RevealCaret");
        if (!_loaded || ActualHeight <= 0)
            return;
        var range = new MonacoRange(_active.LineNumber, _active.Column, _active.LineNumber, _active.Column);
        var top = _view.GetScrollTopToReveal(range, Math.Max(1, ActualHeight - (VerticalPadding * 2)), ScrollHost.VerticalOffset, MonacoRevealStrategy.Minimal, _lineHeight);
        if (Math.Abs(top - ScrollHost.VerticalOffset) > 0.5)
            ScrollHost.ChangeView(null, top, null, true);
    }

    private void ResetCaretBlink()
    {
        _caretVisible = true;
        if (_loaded)
        {
            _caretTimer.Stop();
            _caretTimer.Start();
        }
    }

    private void AfterProjectionChanged(bool notifyModel)
    {
        using var methodSample = _methodProfiler.Measure("AfterProjectionChanged");
        _ = notifyModel; // Kept for source compatibility with existing call sites.
        _modelRefreshQueued = false;
        _preferredCaretX = null;
        UpdateExtent();
        RevealCaret();
        ResetCaretBlink();
        _textInput?.NotifyLayoutChanged();
        Render();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _loaded = false;
        _caretTimer.Stop();
        _accessibilityTimer.Stop();
        _accessibilityTimer.Tick -= AccessibilityTimer_Tick;
        _chordTimer.Stop();
        _decorationIdleTimer.Stop();
        _decorationIdleTimer.Tick -= DecorationIdleTimer_Tick;
        _foldRefreshTimer.Stop();
        _foldRefreshTimer.Tick -= FoldRefreshTimer_Tick;
        _contextMenu.Opening -= ContextMenu_Opening;
        _contextMenu.Closed -= ContextMenu_Closed;
        _localization.CultureChanged -= Localization_CultureChanged;
        _languages.Languages.TokenizationChanged -= Languages_TokenizationChanged;
        _languages.Languages.LanguageConfigurationChanged -= Languages_LanguageConfigurationChanged;
        _languages.DiagnosticsChanged -= Languages_DiagnosticsChanged;
        SurfaceRoot.ContextFlyout = null;
        SurfaceRoot.RemoveHandler(UIElement.PointerMovedEvent, new PointerEventHandler(SurfaceRoot_PointerMoved));
        ProtectedCursor = null;
        _textPointerCursor?.Dispose();
        _minimapPointerCursor?.Dispose();
        _arrowPointerCursor?.Dispose();
        DetachTextInputController(notifyFocusLeave: false);
        DisposeLspBinding();
        DisposeContributions();
        _commands.Dispose();
        _model.Changed -= Model_Changed;
        ReleaseGraphicsResources();
        _view.Dispose();
        _model.Dispose();
        GC.SuppressFinalize(this);
    }
}
