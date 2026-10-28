using MonacoNative.Language;
using MonacoNative.Documents;
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

    private sealed record ShortcutSchemeOption(EditorShortcutScheme Value, string Label)
    {
        public override string ToString() => Label;
    }

    private bool _refreshingThemeOptions;
    private bool _refreshingShortcutSchemes;
    private bool _suppressSampleSelection;
    private bool _updatingFeatureToggles;
    private bool _updatingMenuOptions;
    private readonly SemaphoreSlim _sampleLoadLock = new(1, 1);
    private CancellationTokenSource? _sampleLoadCancellation;
    private bool _closing;


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
        new("JSON", "demo.json", "json"),
        new("CSS", "demo.css", "css"),
        new("TypeScript", "demo.ts", "typescript"),
    };

    public MainWindow()
    {
        InitializeComponent();
        Editor.NativeLanguageServiceFailed += (_, _) => { if (!_closing && !Editor.IsDisposed) ProfileReportHint.Text = Editor.LastNativeLanguageServiceError?.Message ?? string.Empty; };
        var jsonPackage = Editor.Languages.Packages.Installed.OfType<MonacoNative.Language.Json.JsonLanguagePackage>().Single();
        jsonPackage.Schemas.Register(new Uri("monaco://schemas/sample-package"), """
            {"type":"object","required":["name","version"],"properties":{
              "$schema":{"type":"string"},
              "name":{"type":"string","description":"Sample package name."},
              "version":{"type":"string","default":"1.0.0"},
              "private":{"type":"boolean","default":true},
              "description":{"type":"string"},
              "scripts":{"type":"object","properties":{"start":{"type":"string"},"test":{"type":"string"},"build":{"type":"string"}}},
              "license":{"type":"string","enum":["MIT","Apache-2.0","BSD-3-Clause"]}
            }}
            """, "**/demo.json");
        UiLanguageBox.ItemsSource = Editor.SupportedUiLanguages;
        UiLanguageBox.SelectedItem = Editor.UiLanguage;
        Editor.UiLanguageChanged += Editor_UiLanguageChanged;
        Editor.ModelChanged += (_, _) => UpdateStatus();
        Editor.ActiveDocumentChanged += (_, _) => { UpdateStatus(); UpdateLanguageServiceStatus(); };
        Editor.DocumentOpened += (_, _) => UpdateStatus();
        Editor.DocumentClosed += (_, _) => UpdateStatus();
        Editor.DocumentSaved += (_, _) => UpdateStatus();
        Editor.DocumentOperationFailed += (_, args) => ProfileReportHint.Text = args.Exception.Message;
        Editor.FoldingChanged += (_, _) => UpdateStatus();
        Editor.Loaded += (_, _) => UpdateStatus();
        Editor.GotFocus += (_, _) => UpdateStatus();
        Editor.ImeStateChanged += (_, _) => UpdateStatus();
        Editor.PointerCursorChanged += (_, _) => UpdateStatus();
        Editor.EditorViewOptionsChanged += (_, _) => SynchronizeFeatureToggles();
        RefreshShortcutSchemeOptions();
        Closed += async (_, _) =>
        {
            if (_closing) return;
            _closing = true;
            // A sample load is no longer useful after the window closes. Cancel it rather
            // than holding application shutdown on file I/O or language initialization.
            _sampleLoadCancellation?.Cancel();
            await Editor.DisposeAsync();
        };
        InitializeMenuOptions();
        SynchronizeFeatureToggles();
        SampleBox.ItemsSource = Samples;
        _suppressSampleSelection = true;
        SampleBox.SelectedIndex = 2; // Keep the sample selector on the C# sample.
        _suppressSampleSelection = false;
        ApplyLocalization();
        ApplyTheme(ElementTheme.Dark);
        RefreshThemeOptions();
        UpdateStatus();
        _ = LoadInitialSamplesAsync();
    }

    private void ShortcutSchemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_closing || _refreshingShortcutSchemes || Editor is null || Editor.IsDisposed) return;
        if (ShortcutSchemeBox.SelectedItem is ShortcutSchemeOption option)
            Editor.ShortcutScheme = option.Value;
    }

    private void RefreshShortcutSchemeOptions()
    {
        if (_closing || Editor.IsDisposed) return;
        _refreshingShortcutSchemes = true;
        try
        {
            var selectedScheme = Editor.ShortcutScheme;
            var choices = new[]
            {
                new ShortcutSchemeOption(EditorShortcutScheme.VSCode,
                    Editor.GetLocalizedString("monacoNative.sample.shortcuts.vscode")),
                new ShortcutSchemeOption(EditorShortcutScheme.VisualStudio,
                    Editor.GetLocalizedString("monacoNative.sample.shortcuts.visualStudio"))
            };
            ShortcutSchemeBox.ItemsSource = choices;
            ShortcutSchemeBox.SelectedItem = choices.First(option => option.Value == selectedScheme);
        }
        finally { _refreshingShortcutSchemes = false; }
    }

    private void Undo_Click(object sender, RoutedEventArgs e) { Editor.Undo(); UpdateStatus(); }
    private void Redo_Click(object sender, RoutedEventArgs e) { Editor.Redo(); UpdateStatus(); }

    private void LoadSample_Click(object sender, RoutedEventArgs e) => LoadSelectedSample();

    private void SampleBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressSampleSelection && Editor is not null) LoadSelectedSample();
    }

    private async Task LoadInitialSamplesAsync()
    {
        if (_closing || Editor.IsDisposed) return;
        _sampleLoadCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _sampleLoadCancellation = cancellation;
        try
        {
            await _sampleLoadLock.WaitAsync(cancellation.Token);
            try
            {
                // Start with three real file-backed tabs to make cross-language tab
                // switching and drag-reorder behavior immediately discoverable.
                foreach (var sample in new[]
                {
                    Samples.Single(item => item.LanguageId == "css"),
                    Samples.Single(item => item.LanguageId == "html"),
                    Samples.Single(item => item.LanguageId == "csharp"),
                })
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (_closing || Editor.IsDisposed) return;
                    var path = ResolveSamplePath(sample);
                    await Editor.OpenDocumentAsync(new FileDocumentProvider(path, sample.LanguageId),
                        cancellation.Token, focusEditor: false);
                }
                UpdateLanguageServiceStatus();
                UpdateStatus();
            }
            finally { _sampleLoadLock.Release(); }
        }
        catch (OperationCanceledException) { /* A newer sample selection superseded startup loading. */ }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested && !_closing && !Editor.IsDisposed)
                ProfileReportHint.Text = string.Format(Editor.GetLocalizedString("monacoNative.sample.loadError"), ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_sampleLoadCancellation, cancellation)) _sampleLoadCancellation = null;
            cancellation.Dispose();
        }
    }

    private static string ResolveSamplePath(SampleAsset sample) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Assets", "Samples", sample.FileName));

    private async void LoadSelectedSample()
    {
        if (_closing || SampleBox.SelectedItem is not SampleAsset sample) return;
        _sampleLoadCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _sampleLoadCancellation = cancellation;
        try
        {
            await _sampleLoadLock.WaitAsync(cancellation.Token);
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var path = ResolveSamplePath(sample);
                if (sample.LanguageId == "typescript")
                {
                    // The host supplies module snapshots; the native service never crawls disk.
                    var modulePath = Path.Combine(Path.GetDirectoryName(path)!, "demo.types.ts");
                    var moduleText = await File.ReadAllTextAsync(modulePath, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (_closing || Editor.IsDisposed) return;
                    var package = Editor.Languages.Packages.Installed
                        .OfType<MonacoNative.Language.TypeScript.TypeScriptLanguagePackage>().Single();
                    package.Options.SetDocuments(new Dictionary<Uri, string>
                    {
                        [new Uri(modulePath)] = moduleText
                    });
                }
                await Editor.OpenDocumentAsync(new FileDocumentProvider(path, sample.LanguageId), cancellation.Token,
                    focusEditor: false);
                cancellation.Token.ThrowIfCancellationRequested();
                if (_closing || Editor.IsDisposed) return;
                // Source assets are already formatted. Do not mutate them using the demo's
                // simplified formatter on every load; the Format button is explicit.
                // Built-in providers execute directly in-process. Loading a sample must
                // never locate, install or start a language server or a script runtime.
                UpdateLanguageServiceStatus();
                UpdateStatus();
            }
            finally { _sampleLoadLock.Release(); }
        }
        catch (OperationCanceledException) { /* A newer sample load superseded this one. */ }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested && !_closing && !Editor.IsDisposed)
                ProfileReportHint.Text = string.Format(Editor.GetLocalizedString("monacoNative.sample.loadError"), ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_sampleLoadCancellation, cancellation)) _sampleLoadCancellation = null;
            cancellation.Dispose();
        }
    }

    private async void FormatSample_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || Editor.IsDisposed) return;
        try { await Editor.FormatDocumentAsync(); }
        catch (OperationCanceledException) when (_closing || Editor.IsDisposed) { }
        catch (Exception exception)
        {
            if (!_closing && !Editor.IsDisposed)
                ProfileReportHint.Text = string.Format(Editor.GetLocalizedString("monacoNative.sample.formatError"), exception.Message);
        }
    }

    private void Find_Click(object sender, RoutedEventArgs e) => Editor.ShowFind(replace: false);
    private void Replace_Click(object sender, RoutedEventArgs e) => Editor.ShowFind(replace: true);

    private void UpdateLanguageServiceStatus()
    {
        if (_closing || !Editor.TryGetLanguageStatus(out var language)) return;
        LanguageServiceStatus.Text = string.Format(CultureInfo.CurrentCulture,
            Editor.GetLocalizedString("monacoNative.sample.languageStatus"), language.LanguageId,
            Editor.GetLocalizedString(language.HasTokenizer ? "monacoNative.sample.highlightOn" : "monacoNative.sample.highlightOff"),
            Editor.GetLocalizedString(language.SourcePortStatus switch
            {
                LanguageSourcePortStatus.InProcessUpstream => "monacoNative.sample.nativeInProcess",
                LanguageSourcePortStatus.PartialPort => "monacoNative.sample.nativePartial",
                LanguageSourcePortStatus.CompletePort => "monacoNative.sample.nativeComplete",
                _ => "monacoNative.sample.nativeBaseline"
            }));
    }

    private IEnumerable<ToggleMenuFlyoutItem> FeatureSwitches()
    {
        return new[]
        {
            WordWrapToggle, WhitespaceToggle, MinimapToggle, IndentGuidesToggle, FoldingToggle,
            OverviewToggle, BracketColorsToggle, CodeLensToggle, InlineCompletionToggle, StickyScrollToggle,
            ShowTabsToggle, MultiTabToggle,
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
        "showTabs" => Editor.ShowTabs,
        "multiTab" => Editor.IsMultiTabEnabled,
        _ => false
    };

    private async void EditorFeature_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _updatingFeatureToggles || Editor is null || Editor.IsDisposed || sender is not ToggleMenuFlyoutItem toggle || toggle.Tag is not string key)
            return;
        // WinUI's ToggleMenuFlyoutItem.Click can observe the check visual before
        // its new state is committed. Toggle from the editor's authoritative
        // current value, never from the menu item's possibly stale IsChecked.
        var enabled = !GetFeatureState(key);
        if (key == "multiTab")
        {
            try { await Editor.SetMultiTabEnabledAsync(enabled); }
            catch (OperationCanceledException) { }
            catch (Exception error) { ProfileReportHint.Text = error.Message; }
            if (_closing || Editor.IsDisposed) return;
        }
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
            case "showTabs": Editor.ShowTabs = enabled; break;
        }
        // Keep the menu check and editor state synchronized in this same Click.
        toggle.IsChecked = GetFeatureState(key);
        // Some view options control XAML overlays and do not explicitly request
        // a full paint. Commit the effective state before the flyout closes.
        Editor.CommitViewOptions();
        UpdateStatus();
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            // Also reconcile if the MenuFlyoutItem template updates IsChecked
            // after its Click handlers have run.
            SynchronizeFeatureToggles();
        });
    }

    private void TabBarToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_closing || _updatingFeatureToggles || Editor is null || Editor.IsDisposed || TabBarToggle is null) return;
        var showTabs = TabBarToggle.IsChecked == true;
        if (Editor.ShowTabs != showTabs) Editor.ShowTabs = showTabs;
        Editor.CommitViewOptions();
        SynchronizeFeatureToggles();
        UpdateStatus();
    }

    private void SynchronizeFeatureToggles()
    {
        if (_closing || Editor is null || Editor.IsDisposed || WordWrapToggle is null) return;
        _updatingFeatureToggles = true;
        try
        {
            foreach (var toggle in FeatureSwitches())
            {
                if (toggle.Tag is string key)
                    toggle.IsChecked = GetFeatureState(key);
            }
            TabBarToggle.IsChecked = Editor.ShowTabs;
            TabBarToggle.Content = Editor.GetLocalizedString("monacoNative.sample.tabBarToggle");
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
        if (_closing || Editor.IsDisposed) return;
        ApplyLocalization();
        UpdateStatus();
    }

    private void ApplyLocalization()
    {
        if (_closing || Editor.IsDisposed) return;
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
        KeyboardShortcutsHeader.Text = Editor.GetLocalizedString("monacoNative.sample.keyboardShortcuts");
        RefreshShortcutSchemeOptions();
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
        SynchronizeFeatureToggles();
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
        // The renderer owns model measurements and refuses queries during shutdown.
        // This window only formats those measurements for its demo status bar.
        if (_closing || !Editor.TryGetDocumentStatus(out var document) ||
            !Editor.TryGetLanguageStatus(out var language))
            return;
        var input = string.IsNullOrWhiteSpace(Editor.InputLanguageTag)
            ? Editor.GetLocalizedString("monacoNative.sample.input.none")
            : Editor.InputLanguageTag;
        StatusText.Text = string.Format(
            CultureInfo.CurrentCulture,
            Editor.GetLocalizedString("monacoNative.sample.status"),
            document.LineCount,
            document.Length,
            document.CharacterCount,
            input);
        // Stable, nonlocalized accessibility telemetry for end-to-end UI test assertions.
        // Does not expose source text or modify the editor's UIA Text provider.
        AutomationProperties.SetName(StatusText,
            $"MonacoNativeTestState;Length={document.Length};Version={document.VersionId};" +
            $"Cursor={Editor.PointerCursorState};Profile={(Editor.MethodProfilingEnabled ? "on" : "off")};" +
            $"Theme={Editor.RequestedTheme};Folds={Editor.AvailableFoldCount};Collapsed={Editor.CollapsedFoldCount};" +
            $"Language={language.LanguageId};Backend=Native;PortStatus={language.SourcePortStatus};" +
            $"DocumentCount={Editor.Documents.Count};ActiveDocumentUri={Uri.EscapeDataString(Editor.DocumentUri.AbsoluteUri)};" +
            $"IsDirty={Editor.ActiveDocument.IsDirty};ShowTabs={Editor.ShowTabs};MultiTab={Editor.IsMultiTabEnabled}");
    }
}
