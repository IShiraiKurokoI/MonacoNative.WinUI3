using MonacoNative.Language;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using MonacoNative.Input;
using MonacoNative.Renderer.WinUI3;

namespace MonacoNative.SampleApp;

public sealed partial class MainWindow : Window
{
    private sealed record ThemeOption(ElementTheme Value, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly List<IDisposable> _sampleFormatRegistrations = new();
    private bool _refreshingThemeOptions;
    private bool _updatingFeatureToggles;
    private bool _updatingMenuOptions;

    private sealed record SampleAsset(string Name, string FileName, string LanguageId)
    {
        public override string ToString() => Name;
    }

    private sealed record AccessOption(EditorCommandAvailability Mode, string Label)
    {
        public override string ToString() => Label;
    }

    private AccessOption[] GetCommandModes() => new[]
    {
        new AccessOption(EditorCommandAvailability.ContextMenuAndShortcut, Editor.GetLocalizedString("monacoNative.sample.modeBoth")),
        new AccessOption(EditorCommandAvailability.ShortcutOnly, Editor.GetLocalizedString("monacoNative.sample.modeShortcut")),
        new AccessOption(EditorCommandAvailability.Disabled, Editor.GetLocalizedString("monacoNative.sample.modeDisabled")),
    };

    private static readonly SampleAsset[] Samples =
    {
        new("HTML", "demo.html", "html"),
        new("JavaScript", "demo.js", "javascript"),
        new("C#", "demo.cs", "csharp"),
        new("C++", "demo.cpp", "cpp"),
    };

    public MainWindow()
    {
        InitializeComponent();
        UiLanguageBox.ItemsSource = Editor.SupportedUiLanguages;
        UiLanguageBox.SelectedItem = Editor.UiLanguage;
        // Four sample-only formatting providers. Full LSP formatting remains optional.
        foreach (var sample in Samples)
            _sampleFormatRegistrations.Add(Editor.Languages.RegisterDocumentFormattingEditProvider(
                new LanguageSelector(sample.LanguageId), new DemoSampleFormattingProvider()));
        Editor.UiLanguageChanged += Editor_UiLanguageChanged;
        Editor.ModelChanged += (_, _) => UpdateStatus();
        Editor.FoldingChanged += (_, _) => UpdateStatus();
        Editor.Loaded += (_, _) => UpdateStatus();
        Editor.GotFocus += (_, _) => UpdateStatus();
        Editor.ImeStateChanged += (_, _) => UpdateStatus();
        Editor.PointerCursorChanged += (_, _) => UpdateStatus();
        Editor.EditorViewOptionsChanged += (_, _) => SynchronizeFeatureToggles();
        Closed += (_, _) =>
        {
            foreach (var registration in _sampleFormatRegistrations) registration.Dispose();
            Editor.Dispose();
        };
        InitializeMenuOptions();
        SynchronizeFeatureToggles();
        SampleBox.ItemsSource = Samples;
        SampleBox.SelectedIndex = 3; // C++ on first launch; all sample contents live in Assets.
        ApplyLocalization();
        ApplyTheme(ElementTheme.Dark);
        RefreshThemeOptions();
        UpdateStatus();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) { Editor.Undo(); UpdateStatus(); }
    private void Redo_Click(object sender, RoutedEventArgs e) { Editor.Redo(); UpdateStatus(); }

    private void LoadSample_Click(object sender, RoutedEventArgs e) => LoadSelectedSample();

    private void SampleBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Editor is not null) LoadSelectedSample();
    }

    private async void LoadSelectedSample()
    {
        if (SampleBox.SelectedItem is not SampleAsset sample) return;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Samples", sample.FileName);
            Editor.LanguageId = sample.LanguageId;
            Editor.DocumentUri = new Uri("file:///MonacoNativeDemo/" + sample.FileName);
            Editor.Text = File.ReadAllText(path);
            await Editor.FormatDocumentAsync(); // Built-in safe formatting on each sample switch.
            ProfileReportHint.Text = string.Empty;
        }
        catch (Exception ex)
        {
            ProfileReportHint.Text = string.Format(Editor.GetLocalizedString("monacoNative.sample.loadError"), ex.Message);
        }
        UpdateLanguageServiceStatus();
        UpdateStatus();
    }

    private async void FormatSample_Click(object sender, RoutedEventArgs e)
    {
        try { await Editor.FormatDocumentAsync(); }
        catch (Exception exception)
        {
            ProfileReportHint.Text = string.Format(Editor.GetLocalizedString("monacoNative.sample.formatError"), exception.Message);
        }
    }

    private void Find_Click(object sender, RoutedEventArgs e) => Editor.ShowFind(replace: false);
    private void Replace_Click(object sender, RoutedEventArgs e) => Editor.ShowFind(replace: true);

    private void UpdateLanguageServiceStatus()
    {
        var hasLexer = Editor.Languages.Languages.TryGetMonarchTokenizer(Editor.LanguageId, out _, out _);
        LanguageServiceStatus.Text = string.Format(CultureInfo.CurrentCulture,
            Editor.GetLocalizedString("monacoNative.sample.languageStatus"), Editor.LanguageId,
            Editor.GetLocalizedString(hasLexer ? "monacoNative.sample.highlightOn" : "monacoNative.sample.highlightOff"),
            Editor.GetLocalizedString(Editor.LanguageServerBinding is null ?
                "monacoNative.sample.lspDisconnected" : "monacoNative.sample.lspConnected"));
    }

    private IEnumerable<ToggleMenuFlyoutItem> FeatureSwitches()
    {
        return new[]
        {
            WordWrapToggle, WhitespaceToggle, MinimapToggle, IndentGuidesToggle, FoldingToggle,
            OverviewToggle, BracketColorsToggle, CodeLensToggle, InlineCompletionToggle, StickyScrollToggle,
        };
    }

    private bool GetFeatureState(string key) => key switch
    {
        "wordWrap" => Editor.WordWrap,
        "whitespace" => Editor.ShowWhitespace,
        "minimap" => Editor.ShowMinimap,
        "indentGuides" => Editor.ShowIndentGuides,
        "folding" => Editor.ShowFolding,
        "overview" => Editor.ShowOverviewRuler,
        "bracketColors" => Editor.BracketPairColorization,
        "codeLens" => Editor.CodeLensEnabled,
        "inlineCompletion" => Editor.InlineCompletionsEnabled,
        "stickyScroll" => Editor.StickyScrollEnabled,
        _ => false
    };

    private void EditorFeature_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingFeatureToggles || Editor is null || sender is not ToggleMenuFlyoutItem toggle || toggle.Tag is not string key)
            return;
        // WinUI's ToggleMenuFlyoutItem.Click can observe the check visual before
        // its new state is committed. Toggle from the editor's authoritative
        // current value, never from the menu item's possibly stale IsChecked.
        var enabled = !GetFeatureState(key);
        switch (key)
        {
            case "wordWrap": Editor.WordWrap = enabled; break;
            case "whitespace": Editor.ShowWhitespace = enabled; break;
            case "minimap": Editor.ShowMinimap = enabled; break;
            case "indentGuides": Editor.ShowIndentGuides = enabled; break;
            case "folding": Editor.ShowFolding = enabled; break;
            case "overview": Editor.ShowOverviewRuler = enabled; break;
            case "bracketColors": Editor.BracketPairColorization = enabled; break;
            case "codeLens": Editor.CodeLensEnabled = enabled; break;
            case "inlineCompletion": Editor.InlineCompletionsEnabled = enabled; break;
            case "stickyScroll": Editor.StickyScrollEnabled = enabled; break;
        }
        // Keep the menu check and editor state synchronized in this same Click.
        toggle.IsChecked = GetFeatureState(key);
        // Some view options control XAML overlays and do not explicitly request
        // a full paint. Commit the effective state before the flyout closes.
        Editor.CommitViewOptions();
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            // Also reconcile if the MenuFlyoutItem template updates IsChecked
            // after its Click handlers have run.
            SynchronizeFeatureToggles();
        });
    }

    private void SynchronizeFeatureToggles()
    {
        if (Editor is null || WordWrapToggle is null) return;
        _updatingFeatureToggles = true;
        try
        {
            foreach (var toggle in FeatureSwitches())
            {
                if (toggle.Tag is string key)
                    toggle.IsChecked = GetFeatureState(key);
            }
        }
        finally { _updatingFeatureToggles = false; }
    }

    private void InitializeMenuOptions()
    {
        _updatingMenuOptions = true;
        try
        {
            MenuOptionsPanel.Children.Clear();
            var commandModes = GetCommandModes();
            foreach (var (commandId, label) in Editor.GetConfigurableCommands())
            {
                // XAML-supplied items take precedence, all others inherit ShortcutOnly.
                var setting = Editor.ContextMenuItemOptions.FirstOrDefault(x => x.CommandId == commandId);
                if (setting is null)
                {
                    setting = new EditorContextMenuItemOptions { CommandId = commandId };
                    Editor.ContextMenuItemOptions.Add(setting);
                }
                var title = new TextBlock { Text = label, FontSize = 11, TextWrapping = TextWrapping.Wrap };
                var modeBox = new ComboBox
                {
                    MinWidth = 226, HorizontalAlignment = HorizontalAlignment.Stretch,
                    ItemsSource = commandModes, Tag = setting,
                };
                AutomationProperties.SetAutomationId(modeBox, "CommandMode_" + commandId);
                var effectiveMode = Editor.GetCommandAvailability(commandId);
                modeBox.SelectedItem = commandModes.First(option => option.Mode == effectiveMode);
                modeBox.SelectionChanged += CommandMode_Changed;
                MenuOptionsPanel.Children.Add(title);
                MenuOptionsPanel.Children.Add(modeBox);
            }
        }
        finally { _updatingMenuOptions = false; }
    }

    private void CommandMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingMenuOptions || sender is not ComboBox box ||
            box.Tag is not EditorContextMenuItemOptions setting ||
            box.SelectedItem is not AccessOption selection)
            return;
        setting.Mode = selection.Mode;
    }

    private void ProfilerToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (Editor is null)
            return;
        Editor.MethodProfilingEnabled = ProfilerToggle.IsChecked == true;
        ProfilerToggle.Content = Editor.GetLocalizedString(Editor.MethodProfilingEnabled ? "monacoNative.sample.profileOn" : "monacoNative.sample.profileOff");
        UpdateStatus();
    }

    private void ExportProfileButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // UI tests can override this location for deterministic artifact discovery.
            var directory = Environment.GetEnvironmentVariable("MONACONATIVE_PROFILE_DIR");
            if (string.IsNullOrWhiteSpace(directory))
                directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MonacoNative", "Profiles");
            var paths = Editor.ExportMethodProfileReport(directory);
            ProfileReportHint.Text = string.Format(Editor.GetLocalizedString("monacoNative.sample.profileSuccess"), paths.Json);
            ToolTipService.SetToolTip(ProfileReportHint, paths.Json);
        }
        catch (Exception exception)
        {
            ProfileReportHint.Text = string.Format(Editor.GetLocalizedString("monacoNative.sample.profileError"), exception.Message);
        }
    }

    private void UiLanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UiLanguageBox.SelectedItem is string culture)
            Editor.UiLanguage = culture;
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingThemeOptions || ThemeBox.SelectedItem is not ThemeOption option)
            return;
        ApplyTheme(option.Value);
    }

    private void RefreshThemeOptions()
    {
        _refreshingThemeOptions = true;
        try
        {
            var options = new[]
            {
                new ThemeOption(ElementTheme.Default, Editor.GetLocalizedString("monacoNative.sample.theme.system")),
                new ThemeOption(ElementTheme.Light, Editor.GetLocalizedString("monacoNative.sample.theme.light")),
                new ThemeOption(ElementTheme.Dark, Editor.GetLocalizedString("monacoNative.sample.theme.dark")),
            };
            ThemeBox.ItemsSource = options;
            var theme = AppRoot.RequestedTheme;
            foreach (var option in options)
            {
                if (option.Value == theme)
                {
                    ThemeBox.SelectedItem = option;
                    break;
                }
            }
        }
        finally
        {
            _refreshingThemeOptions = false;
        }
    }

    private void ApplyTheme(ElementTheme theme)
    {
        AppRoot.RequestedTheme = theme;
        Editor.RequestedTheme = theme;
        Editor.RefreshEditorTheme();
        Editor.FlushPendingRender();
        if (!_refreshingThemeOptions)
            RefreshThemeOptions();
        UpdateStatus();
    }

    private void Editor_UiLanguageChanged(object? sender, EventArgs e)
    {
        ApplyLocalization();
        UpdateStatus();
    }

    private void ApplyLocalization()
    {
        Title = Editor.GetLocalizedString("monacoNative.sample.windowTitle");
        UndoButton.Label = Editor.GetLocalizedString("undo");
        RedoButton.Label = Editor.GetLocalizedString("redo");
        LoadSampleButton.Label = Editor.GetLocalizedString("monacoNative.sample.action.loadSample");
        ExportProfileButton.Content = Editor.GetLocalizedString("monacoNative.sample.action.exportProfile");
        ViewOptionsButton.Content = Editor.GetLocalizedString("monacoNative.sample.viewOptions");
        SampleLabel.Text = Editor.GetLocalizedString("monacoNative.sample.example");
        FormatSampleButton.Content = Editor.GetLocalizedString("monacoNative.sample.formatButton");
        CommandAccessHeader.Text = Editor.GetLocalizedString("monacoNative.sample.commandAccess");
        CommandAccessDescription.Text = Editor.GetLocalizedString("monacoNative.sample.commandAccessDescription");
        FindSidebarButton.Content = Editor.GetLocalizedString("monacoNative.sample.findButton");
        ReplaceSidebarButton.Content = Editor.GetLocalizedString("monacoNative.sample.replaceButton");
        AdvancedPolicyHeader.Text = Editor.GetLocalizedString("monacoNative.sample.advancedPolicy");
        AdvancedPolicyDescription.Text = Editor.GetLocalizedString("monacoNative.sample.advancedPolicyDescription");
        LanguageServicesHeader.Text = Editor.GetLocalizedString("monacoNative.sample.languageServices");
        ProfilerToggle.Content = Editor.GetLocalizedString(Editor.MethodProfilingEnabled ?
            "monacoNative.sample.profileOn" : "monacoNative.sample.profileOff");
        foreach (var option in FeatureSwitches())
        {
            if (option.Tag is string key)
                option.Text = Editor.GetLocalizedString("monacoNative.sample." + key);
        }
        InitializeMenuOptions();
        UpdateLanguageServiceStatus();
        ThemeLabel.Text = Editor.GetLocalizedString("monacoNative.sample.theme");
        UiLanguageLabel.Text = Editor.GetLocalizedString("monacoNative.sample.uiLanguage");
        RefreshThemeOptions();
        if (!Equals(UiLanguageBox.SelectedItem, Editor.UiLanguage))
            UiLanguageBox.SelectedItem = Editor.UiLanguage;
    }

    private void UpdateStatus()
    {
        var imeKey = Editor.ImeCapability == ImeInputCapability.NativeTsf
            ? "monacoNative.sample.ime.nativeTsf"
            : "monacoNative.sample.ime.basicFallback";
        var ime = Editor.GetLocalizedString(imeKey);
        var input = string.IsNullOrWhiteSpace(Editor.InputLanguageTag)
            ? Editor.GetLocalizedString("monacoNative.sample.input.none")
            : Editor.InputLanguageTag;
        const string backendKey = "monacoNative.sample.backend.nativeTsf";
        StatusText.Text = string.Format(
            CultureInfo.CurrentCulture,
            Editor.GetLocalizedString("monacoNative.sample.status"),
            Editor.VersionId,
            Editor.LineCount,
            Editor.Length,
            ime,
            input,
            Editor.GetLocalizedString(backendKey));
        // Stable, nonlocalized accessibility telemetry for end-to-end UI test assertions.
        // Does not expose source text or modify the editor's UIA Text provider.
        AutomationProperties.SetName(StatusText,
            $"MonacoNativeTestState;Length={Editor.Length};Version={Editor.VersionId};" +
            $"Cursor={Editor.PointerCursorState};Profile={(Editor.MethodProfilingEnabled ? "on" : "off")};" +
            $"Theme={Editor.RequestedTheme};Folds={Editor.AvailableFoldCount};Collapsed={Editor.CollapsedFoldCount};" +
            $"Language={Editor.LanguageId};LSP={(Editor.LanguageServerBinding is null ? "off" : "on")}");
    }
}
