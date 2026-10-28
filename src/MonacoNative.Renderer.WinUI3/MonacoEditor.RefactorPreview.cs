using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
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
    private sealed class RefactorFileView
    {
        internal required EditorRefactorPreview File;
        internal required Grid Container;
        internal required Border Body;
        internal required ToggleButton Header;
        internal required CheckBox Include;
        internal required TreeViewNode Node;
        internal readonly Dictionary<EditorPreviewChange, CheckBox> Checks = new();
        internal MonacoDiffEditor? Diff;
        internal MonacoRange? RevealRange;
        internal bool Expanded = true;
    }

    private readonly List<RefactorFileView> _refactorFiles = new();
    private readonly Dictionary<TreeViewNode, (RefactorFileView File, EditorPreviewChange? Change)> _refactorNodes = new();
    private TreeView? _refactorTree;
    private Button? _refactorApply;
    private TextBlock? _refactorStatus;
    private ScrollViewer? _multiDiffScroll;
    private StackPanel? _multiDiffFiles;
    private Grid? _transientEditorHost;
    private TabViewItem? _transientEditorTab;
    private MonacoDiffEditor? _comparisonEditor;
    private MonacoEditor? _previewOwner;
    private bool _refactorSessionActive;
    private bool _refactorApplying;
    private bool _refreshingRefactorChecks;
    private bool _transientIsRefactor;
    private bool _updatingPreviewViewport;
    private ControlTemplate? _previewCheckTemplate;

    /// <summary>Reads unopened, non-file resources for a refactor preview.</summary>
    public Func<Uri, CancellationToken, Task<EditorDocumentContent>>? RefactorPreviewDocumentReaderAsync { get; set; }

    private async Task<EditorDocumentContent> ReadRefactorSourceAsync(Uri uri, CancellationToken token)
    {
        if (FindWorkspaceDocument(uri) is { } document)
            return new(document.Model.Text, document.LanguageId);
        if (RefactorPreviewDocumentReaderAsync is { } read) return await read(uri, token);
        if (uri.IsFile) return await new FileDocumentProvider(uri.LocalPath).ReadAsync(token);
        throw new InvalidOperationException($"No preview reader is registered for '{uri.Scheme}' resources.");
    }

    private async Task ShowRenamePreviewAsync(WorkspaceEdit edit, string newName, long request, CancellationToken token)
    {
        var files = new List<EditorRefactorPreview>();
        foreach (var group in edit.Edits.GroupBy(e => e.Uri, EditorDocumentUriComparer.Instance))
        {
            var source = await ReadRefactorSourceAsync(group.Key, token);
            if (!IsRenameSessionCurrent(request)) return;
            var open = FindWorkspaceDocument(group.Key);
            files.Add(new EditorRefactorPreview(group.Key, source.Text,
                source.LanguageId ?? open?.LanguageId ?? EditorDocumentLanguages.InferFromPath(group.Key.AbsolutePath) ?? "plaintext",
                open?.DisplayName ?? (group.Key.IsFile ? Path.GetFileName(group.Key.LocalPath) : group.Key.AbsoluteUri), group));
        }
        if (!AreRenameWorkspaceVersionsCurrent(edit))
            throw new InvalidOperationException("A document changed while the refactor preview was being prepared.");
        if (GroupOwner._previewOwner is { } previous && previous != this) previous.CancelRename();
        GroupOwner._previewOwner = this;
        CloseTransientEditorCore();
        CloseInGroupSplit();
        _refactorSessionActive = true;
        _transientIsRefactor = true;
        CreateRefactorPanel();
        _multiDiffFiles = new StackPanel { Spacing = 0 };
        _multiDiffScroll = new ScrollViewer
        {
            Content = _multiDiffFiles, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled, IsTabStop = false,
        };
        _multiDiffScroll.ViewChanged += (_, _) => UpdatePreviewViewport();
        _multiDiffScroll.SizeChanged += (_, _) => UpdatePreviewViewport();
        CreateTransientEditor("Refactor Preview", _multiDiffScroll);
        for (var index = 0; index < files.Count; ++index) AddRefactorFile(files[index], index < 10);
        _refactorStatus!.Text = $"{edit.Edits.Count} changes in {files.Count} files";
        AutomationProperties.SetName(_refactorTree!, $"Rename {_renameOriginalName} to {newName}: {edit.Edits.Count} changes");
        RefreshRefactorChecks();
        RefreshRefactorPalette();
        LeaveEditorTextInput();
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsDisposed && request == _renameSequence && _refactorTree is not null)
            {
                UpdatePreviewViewport();
                if (_refactorTree.RootNodes.Count > 0) _refactorTree.SelectedNode = _refactorTree.RootNodes[0];
                _refactorTree.Focus(FocusState.Programmatic);
            }
        });
    }

    private void CreateRefactorPanel()
    {
        var owner = GroupOwner;
        var root = owner.WorkbenchRoot;
        root.RowDefinitions.Clear();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 60 });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(35) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) });
        _renamePreview = new Border
        {
            Child = body, BorderThickness = new Thickness(0, 1, 0, 0), CornerRadius = new CornerRadius(0),
            Height = Math.Clamp(root.ActualHeight * 0.35, 160, 320),
        };
        AutomationProperties.SetAutomationId(_renamePreview, "RenamePreview");
        Grid.SetRow(_renamePreview, 1);
        root.Children.Add(_renamePreview);
        var resize = new Thumb { HorizontalAlignment = HorizontalAlignment.Stretch, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        resize.DragDelta += (_, e) =>
        {
            if (_renamePreview is null) return;
            _renamePreview.Height = Math.Clamp(_renamePreview.Height - e.VerticalChange, 120, Math.Max(120, root.ActualHeight - 80));
        };
        AutomationProperties.SetName(resize, "Resize Refactor Preview");
        body.Children.Add(resize);
        var header = new Grid { ColumnSpacing = 12, Padding = new Thickness(20, 0, 8, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = new SolidColorBrush(Color.FromArgb(255, 0, 122, 204)),
            Child = new TextBlock { Text = "REFACTOR PREVIEW", FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
        };
        header.Children.Add(title);
        _refactorStatus = new TextBlock { FontSize = 11, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(_refactorStatus, 1); header.Children.Add(_refactorStatus);
        var collapse = PreviewToolButton("Collapse All", CreatePreviewIcon("M4,1 L14,1 L14,11 M1,4 L11,4 L11,14 L1,14 Z M3,9 L9,9"), () =>
        {
            if (_refactorTree is null) return;
            foreach (var node in _refactorTree.RootNodes) node.IsExpanded = false;
        });
        Grid.SetColumn(collapse, 2); header.Children.Add(collapse);
        var close = PreviewToolButton("Close Refactor Preview", CreatePreviewIcon("M4,4 L12,12 M12,4 L4,12"), CancelRename);
        Grid.SetColumn(close, 3); header.Children.Add(close);
        Grid.SetRow(header, 1); body.Children.Add(header);
        _refactorTree = new TreeView
        {
            SelectionMode = TreeViewSelectionMode.Single, CanDragItems = false, CanReorderItems = false,
            AllowDrop = false, BorderThickness = new Thickness(0), Padding = new Thickness(8, 0, 8, 0),
        };
        var style = new Style(typeof(TreeViewItem));
        style.Setters.Add(new Setter(MinHeightProperty, 23.0));
        style.Setters.Add(new Setter(PaddingProperty, new Thickness(0)));
        _refactorTree.ItemContainerStyle = style;
        AutomationProperties.SetAutomationId(_refactorTree, "RefactorPreviewTree");
        _refactorTree.ItemInvoked += (_, e) =>
        {
            if (e.InvokedItem is TreeViewNode node) RevealRefactorNode(node);
            else if (_refactorTree.SelectedNode is { } selected) RevealRefactorNode(selected);
        };
        _refactorTree.SelectionChanged += (_, _) =>
        {
            if (_refactorTree?.SelectedNode is { } node) RevealRefactorNode(node);
        };
        _refactorTree.KeyDown += (_, e) =>
        {
            if (e.Handled || e.Key != VirtualKey.Space || IsKeyDown(VirtualKey.Control)) return;
            if (_refactorTree?.SelectedNode is not { } node || !_refactorNodes.TryGetValue(node, out var entry)) return;
            e.Handled = true;
            if (entry.Change is { } change) change.Included = !change.Included;
            else entry.File.File.SetIncluded(entry.File.File.Included != true);
            RefreshRefactorChecks(entry.File);
        };
        Grid.SetRow(_refactorTree, 2); body.Children.Add(_refactorTree);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(20, 10, 20, 10) };
        _refactorApply = new Button
        {
            Content = "Apply", MinHeight = 28, MinWidth = 72, Padding = new Thickness(12, 3, 12, 3), CornerRadius = new CornerRadius(2),
        };
        AutomationProperties.SetAutomationId(_refactorApply, "RenamePreviewApply");
        _refactorApply.Click += async (_, _) => await ApplyRefactorPreviewAsync();
        var discard = new Button
        {
            Content = "Discard", MinHeight = 28, MinWidth = 72, Padding = new Thickness(12, 3, 12, 3), CornerRadius = new CornerRadius(2),
        };
        AutomationProperties.SetAutomationId(discard, "RenamePreviewDiscard");
        discard.Click += (_, _) => CancelRename();
        footer.Children.Add(_refactorApply); footer.Children.Add(discard);
        Grid.SetRow(footer, 3); body.Children.Add(footer);
        _renamePreview.KeyDown += RefactorPreview_KeyDown;
        var menu = new MenuFlyout();
        var toggle = new MenuFlyoutItem { Text = "Toggle Change", KeyboardAcceleratorTextOverride = "Space" };
        toggle.Click += (_, _) =>
        {
            if (_refactorTree?.SelectedNode is not { } node || !_refactorNodes.TryGetValue(node, out var entry)) return;
            if (entry.Change is { } change) change.Included = !change.Included;
            else entry.File.File.SetIncluded(entry.File.File.Included != true);
            RefreshRefactorChecks(entry.File);
        };
        menu.Items.Add(toggle);
        _refactorTree.ContextFlyout = menu;
    }

    private static FrameworkElement CreatePreviewIcon(string path) => (FrameworkElement)XamlReader.Load(
        "<Path xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
        "Width=\"16\" Height=\"16\" Stretch=\"None\" Stroke=\"{ThemeResource TextFillColorPrimaryBrush}\" " +
        "StrokeThickness=\"1\" Data=\"" + path + "\" />");

    private static Button PreviewToolButton(string name, FrameworkElement icon, Action action)
    {
        var button = new Button
        {
            Content = icon, Width = 26, Height = 26, MinWidth = 0, MinHeight = 0,
            Padding = new Thickness(4), CornerRadius = new CornerRadius(0), BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        ToolTipService.SetToolTip(button, name);
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => action();
        return button;
    }

    private CheckBox PreviewCheckBox(string name, bool group = false)
    {
        _previewCheckTemplate ??= (ControlTemplate)XamlReader.Load("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="CheckBox">
                <Grid Width="16" Height="16" Background="Transparent">
                    <VisualStateManager.VisualStateGroups>
                        <VisualStateGroup x:Name="CheckStates">
                            <VisualState x:Name="Unchecked" />
                            <VisualState x:Name="Checked"><VisualState.Setters><Setter Target="CheckGlyph.Opacity" Value="1" /></VisualState.Setters></VisualState>
                            <VisualState x:Name="Indeterminate"><VisualState.Setters><Setter Target="MixedGlyph.Opacity" Value="1" /></VisualState.Setters></VisualState>
                        </VisualStateGroup>
                    </VisualStateManager.VisualStateGroups>
                    <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="3" />
                    <FontIcon x:Name="CheckGlyph" Glyph="&#xE73E;" FontSize="11" Foreground="{TemplateBinding Foreground}" Opacity="0" />
                    <Rectangle x:Name="MixedGlyph" Width="8" Height="2" Fill="{TemplateBinding Foreground}" Opacity="0" />
                </Grid>
            </ControlTemplate>
            """);
        var box = new CheckBox
        {
            Template = _previewCheckTemplate, IsThreeState = group, IsChecked = true,
            Width = 16, Height = 16, MinWidth = 16, MinHeight = 16, Padding = new Thickness(0),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 107, 107, 107)),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Foreground = new SolidColorBrush(_foregroundColor),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(box, name);
        return box;
    }

    private void AddRefactorFile(EditorRefactorPreview file, bool expanded)
    {
        var include = PreviewCheckBox("Include changes in " + file.DisplayName, group: true);
        var name = new TextBlock { Text = file.DisplayName, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Height = 23 };
        row.Children.Add(include);
        row.Children.Add(CodiconPresenter.Create(CompletionItemKind.File, new SolidColorBrush(_foregroundColor)));
        row.Children.Add(name);
        row.Children.Add(new TextBlock
        {
            Text = file.Uri.IsFile ? Path.GetDirectoryName(file.Uri.LocalPath) : file.Uri.AbsoluteUri,
            FontSize = 12, Opacity = 0.6, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        var node = new TreeViewNode { Content = row, IsExpanded = expanded };
        var container = new Grid();
        container.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new ToggleButton
        {
            Content = "⌄  " + file.DisplayName + "  •  " + file.Changes.Count + " changes", IsChecked = true,
            HorizontalContentAlignment = HorizontalAlignment.Left, HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 13, CornerRadius = new CornerRadius(0), BorderThickness = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(12, 4, 12, 4), MinHeight = 0,
        };
        ToolTipService.SetToolTip(header, file.Uri.IsFile ? file.Uri.LocalPath : file.Uri.AbsoluteUri);
        var body = new Border { Height = Math.Clamp((file.OriginalText.Count(c => c == '\n') + 1) * 20 + 24, 160, 480) };
        Grid.SetRow(body, 1); container.Children.Add(header); container.Children.Add(body);
        var view = new RefactorFileView { File = file, Container = container, Body = body, Header = header, Include = include, Node = node };
        _refactorFiles.Add(view); _refactorNodes.Add(node, (view, null));
        include.Click += (_, _) =>
        {
            if (_refreshingRefactorChecks) return;
            file.SetIncluded(file.Included != true);
            RefreshRefactorChecks(view);
        };
        header.Click += (_, _) =>
        {
            view.Expanded = header.IsChecked == true;
            body.Visibility = view.Expanded ? Visibility.Visible : Visibility.Collapsed;
            UpdatePreviewViewport();
        };
        foreach (var change in file.Changes)
        {
            var check = PreviewCheckBox($"Include {file.DisplayName}, line {change.Edit.Range.StartLineNumber}, column {change.Edit.Range.StartColumn}");
            var contents = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Height = 23 };
            contents.Children.Add(check);
            var snippet = file.Snippet(change);
            var text = new TextBlock { FontSize = 13, FontFamily = new FontFamily(_textFormat?.FontFamily ?? "Consolas"), VerticalAlignment = VerticalAlignment.Center };
            text.Inlines.Add(new Run { Text = snippet.Before });
            text.Inlines.Add(new Run { Text = snippet.Removed, Foreground = new SolidColorBrush(Color.FromArgb(255, 244, 135, 113)), TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough });
            text.Inlines.Add(new Run { Text = snippet.Added, Foreground = new SolidColorBrush(Color.FromArgb(255, 115, 182, 99)) });
            text.Inlines.Add(new Run { Text = snippet.After });
            contents.Children.Add(text);
            var child = new TreeViewNode { Content = contents };
            node.Children.Add(child);
            _refactorNodes.Add(child, (view, change));
            view.Checks.Add(change, check);
            check.Click += (_, _) =>
            {
                if (_refreshingRefactorChecks) return;
                change.Included = check.IsChecked == true;
                RefreshRefactorChecks(view);
            };
        }
        _refactorTree!.RootNodes.Add(node);
        _multiDiffFiles!.Children.Add(container);
    }

    private void RefreshRefactorChecks(RefactorFileView? changed = null)
    {
        _refreshingRefactorChecks = true;
        try
        {
            foreach (var view in _refactorFiles)
            {
                view.Include.IsChecked = view.File.Included;
                foreach (var check in view.Checks) check.Value.IsChecked = check.Key.Included;
                if ((changed is null || changed == view) && view.Diff is { } diff)
                    diff.ModifiedText = view.File.BuildModifiedText();
            }
            if (_refactorApply is not null)
                _refactorApply.IsEnabled = !_refactorApplying && _refactorFiles.Any(f => f.File.Changes.Any(c => c.Included));
        }
        finally { _refreshingRefactorChecks = false; }
    }

    private void RevealRefactorNode(TreeViewNode node)
    {
        if (!_refactorNodes.TryGetValue(node, out var entry)) return;
        ShowTransientEditor();
        entry.File.Expanded = true;
        entry.File.Header.IsChecked = true;
        entry.File.Body.Visibility = Visibility.Visible;
        var range = entry.Change?.Edit.Range ?? entry.File.File.Changes.FirstOrDefault()?.Edit.Range;
        if (range is { } r) entry.File.RevealRange = ToMonacoRange(r);
        if (_multiDiffFiles is not null && _multiDiffScroll is not null)
        {
            var y = entry.File.Container.TransformToVisual(_multiDiffFiles).TransformPoint(new Point()).Y;
            _multiDiffScroll.ChangeView(null, Math.Max(0, y), null, true);
        }
        EnsurePreviewDiff(entry.File);
        RevealPreviewRange(entry.File);
    }

    private void UpdatePreviewViewport()
    {
        if (_updatingPreviewViewport || IsDisposed || _multiDiffScroll is null || _multiDiffFiles is null ||
            _transientEditorHost?.Visibility != Visibility.Visible) return;
        _updatingPreviewViewport = true;
        try
        {
            foreach (var view in _refactorFiles)
            {
                var y = view.Container.TransformToVisual(_multiDiffScroll).TransformPoint(new Point()).Y;
                var visible = view.Expanded && y + view.Container.ActualHeight > -400 && y < _multiDiffScroll.ActualHeight + 400;
                if (visible) EnsurePreviewDiff(view);
                else if (view.Diff is { } old)
                {
                    view.Body.Child = null;
                    view.Diff = null;
                    old.Dispose();
                }
            }
        }
        finally { _updatingPreviewViewport = false; }
    }

    private void EnsurePreviewDiff(RefactorFileView view)
    {
        if (view.Diff is not null) return;
        var diff = CreateReadonlyDiff(view.File.OriginalText, view.File.BuildModifiedText(), view.File.LanguageId,
            view.File.DisplayName + " (Original)", view.File.DisplayName + " (Refactored)");
        view.Diff = diff;
        view.Body.Child = diff;
        diff.DiffUpdated += (_, _) => RevealPreviewRange(view);
    }

    private static void RevealPreviewRange(RefactorFileView view)
    {
        if (view.Diff is null || view.RevealRange is not { } range) return;
        view.Diff.OriginalEditorControl.Selection = range;
        view.Diff.OriginalEditorControl.RevealRange(range, MonacoRevealStrategy.Center);
    }

    private MonacoDiffEditor CreateReadonlyDiff(string before, string after, string language, string originalTitle, string modifiedTitle)
    {
        var diff = new MonacoDiffEditor
        {
            RequestedTheme = RequestedTheme, UiLanguage = UiLanguage, LanguageId = language,
            OriginalTitle = originalTitle, ModifiedTitle = modifiedTitle, OriginalText = before, ModifiedText = after,
            OriginalEditable = false, RenderSideBySide = true, HideUnchangedRegions = true,
        };
        diff.ConfigureRefactorPreview();
        return diff;
    }

    private async Task ApplyRefactorPreviewAsync()
    {
        if (!_refactorSessionActive || _refactorApplying || _renameCts is null) return;
        var selected = _refactorFiles.SelectMany(f => f.File.Changes.Where(c => c.Included)).Select(c => c.Edit).ToArray();
        if (selected.Length == 0) return;
        var request = _renameSequence;
        var token = _renameCts.Token;
        _refactorApplying = true;
        RefreshRefactorChecks();
        try
        {
            foreach (var view in _refactorFiles.Where(f => f.File.Changes.Any(c => c.Included)).ToArray())
            {
                var current = await ReadRefactorSourceAsync(view.File.Uri, token);
                if (!IsRenameSessionCurrent(request)) return;
                if (current.Text != view.File.OriginalText)
                    throw new EditorDocumentConflictException(view.File.Uri, $"Cannot apply refactoring because '{view.File.DisplayName}' has changed in the meantime.");
            }
            await ApplyRenameEditAsync(new WorkspaceEdit(selected), request, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!IsDisposed && request == _renameSequence)
            {
                if (XamlRoot is not null)
                {
                    var dialog = new ContentDialog
                    {
                        XamlRoot = XamlRoot, RequestedTheme = RequestedTheme, Title = "Refactor Preview",
                        Content = error.Message, CloseButtonText = "OK",
                    };
                    await dialog.ShowAsync();
                }
                else LastDocumentOperationError = error;
                if (error is EditorDocumentConflictException && request == _renameSequence) CancelRename();
            }
        }
        finally
        {
            if (request == _renameSequence) { _refactorApplying = false; RefreshRefactorChecks(); }
        }
    }

    private async void RefactorPreview_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || !_refactorSessionActive) return;
        if (e.Key == VirtualKey.Enter && IsKeyDown(VirtualKey.Control) && !IsKeyDown(VirtualKey.Shift) && !IsKeyDown(VirtualKey.Menu))
        { e.Handled = true; await ApplyRefactorPreviewAsync(); }
        // Escape returns to the editor. Discard/Close explicitly abandons the pending edit.
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            if (_refactorFiles.FirstOrDefault(f => f.Diff is not null)?.Diff is { } diff)
                diff.ModifiedEditorControl.Focus(FocusState.Programmatic);
        }
    }

    private void RefreshRefactorPalette()
    {
        var light = _lastTheme == ElementTheme.Light;
        var background = new SolidColorBrush(_lastHighContrast == true ? _backgroundColor :
            light ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 24, 24, 24));
        var foreground = new SolidColorBrush(_foregroundColor);
        var border = new SolidColorBrush(light ? Color.FromArgb(255, 229, 229, 229) : Color.FromArgb(255, 43, 43, 43));
        if (_renamePreview is not null) { _renamePreview.Background = background; _renamePreview.BorderBrush = border; }
        if (_refactorTree is not null) { _refactorTree.Background = background; _refactorTree.Foreground = foreground; }
        if (_transientEditorHost is not null) _transientEditorHost.Background = background;
        if (_refactorStatus is not null) _refactorStatus.Foreground = foreground;
        if (_refactorApply is not null)
        {
            _refactorApply.Background = new SolidColorBrush(Color.FromArgb(255, 0, 120, 212));
            _refactorApply.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
            _refactorApply.BorderBrush = _refactorApply.Background;
        }
        foreach (var view in _refactorFiles)
        {
            view.Header.Background = new SolidColorBrush(light ? Color.FromArgb(255, 243, 243, 243) : Color.FromArgb(255, 31, 31, 31));
            view.Header.Foreground = foreground; view.Header.BorderBrush = border;
            view.Include.Foreground = foreground;
            foreach (var check in view.Checks.Values) check.Foreground = foreground;
            if (view.Diff is not null) view.Diff.RequestedTheme = RequestedTheme;
        }
    }

    private void CreateTransientEditor(string title, UIElement content)
    {
        _transientEditorHost = new Grid { Background = new SolidColorBrush(_backgroundColor) };
        _transientEditorHost.Children.Add(content);
        // Intercept workbench shortcuts before the nested, read-only diff editors.
        // Their document lifecycle commands are disabled, but the owning tab still
        // needs to close and switch like any other editor tab.
        _transientEditorHost.PreviewKeyDown += TransientEditor_PreviewKeyDown;
        DocumentContentGrid.Children.Add(_transientEditorHost);
        _transientEditorTab = new TabViewItem { Header = title, IsClosable = true, CornerRadius = new CornerRadius(0) };
        AutomationProperties.SetAutomationId(_transientEditorTab, "RefactorPreviewEditorTab");
        _synchronizingDocumentTabs = true;
        try { DocumentTabView.TabItems.Add(_transientEditorTab); }
        finally { _synchronizingDocumentTabs = false; }
        ShowTransientEditor();
    }

    private void TransientEditor_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || !IsKeyDown(VirtualKey.Control) || IsKeyDown(VirtualKey.Menu)) return;
        if (e.Key == VirtualKey.W && !IsKeyDown(VirtualKey.Shift))
        {
            e.Handled = true;
            CloseTransientEditor();
            if (!IsDisposed && !_showingEmptyGroup) Focus(FocusState.Programmatic);
        }
        else if (e.Key == VirtualKey.Tab && _documentItems.Count > 0)
        {
            e.Handled = true;
            ActivateDocument(IsKeyDown(VirtualKey.Shift) ? _documentItems[^1] : _documentItems[0]);
        }
    }

    private void ShowTransientEditor()
    {
        if (_transientEditorHost is null || _transientEditorTab is null) return;
        _transientEditorHost.Visibility = Visibility.Visible;
        _transientEditorTab.Visibility = Visibility.Visible;
        SurfaceRoot.Opacity = 0;
        SurfaceRoot.IsHitTestVisible = false;
        if (_emptyGroupMessage is not null) _emptyGroupMessage.Visibility = Visibility.Collapsed;
        _synchronizingDocumentTabs = true;
        try { DocumentTabView.SelectedItem = _transientEditorTab; }
        finally { _synchronizingDocumentTabs = false; }
        LeaveEditorTextInput();
        _ = DispatcherQueue.TryEnqueue(UpdatePreviewViewport);
    }

    private void HideTransientEditor()
    {
        if (_transientEditorHost is not null) _transientEditorHost.Visibility = Visibility.Collapsed;
        SurfaceRoot.Opacity = 1;
        SurfaceRoot.IsHitTestVisible = true;
        if (_showingEmptyGroup && _emptyGroupMessage is not null) _emptyGroupMessage.Visibility = Visibility.Visible;
    }

    private void CloseTransientEditor()
    {
        // Closing the preview tab does not discard the pending refactor. Selecting a
        // change reopens it, just as reopening a transient multi-diff editor does.
        HideTransientEditor();
        if (_transientEditorTab is not null) _transientEditorTab.Visibility = Visibility.Collapsed;
        if (!IsDisposed) SelectDocumentTab(ActiveDocument);
        if (!_transientIsRefactor) CloseTransientEditorCore();
    }

    private void CloseTransientEditorCore()
    {
        HideTransientEditor();
        foreach (var view in _refactorFiles)
        {
            view.Body.Child = null;
            view.Diff?.Dispose();
            view.Diff = null;
        }
        _comparisonEditor?.Dispose(); _comparisonEditor = null;
        if (_transientEditorHost is not null) DocumentContentGrid.Children.Remove(_transientEditorHost);
        _transientEditorHost = null;
        if (_transientEditorTab is not null)
        {
            _synchronizingDocumentTabs = true;
            try { DocumentTabView.TabItems.Remove(_transientEditorTab); }
            finally { _synchronizingDocumentTabs = false; }
        }
        _transientEditorTab = null; _multiDiffScroll = null; _multiDiffFiles = null;
        if (!IsDisposed && _activeDocument is { IsClosed: false } active) SelectDocumentTab(active);
        _transientIsRefactor = false;
    }

    private void CloseRefactorPreview()
    {
        _refactorSessionActive = false;
        _refactorApplying = false;
        if (_transientIsRefactor) CloseTransientEditorCore();
        _refactorFiles.Clear(); _refactorNodes.Clear();
        _refactorTree = null; _refactorApply = null; _refactorStatus = null;
        if (_renamePreview is not null)
        {
            GroupOwner.WorkbenchRoot.Children.Remove(_renamePreview);
            _renamePreview = null;
        }
        if (GroupOwner._previewOwner == this)
        {
            GroupOwner._previewOwner = null;
            GroupOwner.WorkbenchRoot.RowDefinitions.Clear();
        }
    }

    internal void SetEmbeddedPreviewChrome()
    {
        EditorChrome.CornerRadius = new CornerRadius(0);
        EditorChrome.BorderThickness = new Thickness(0);
    }

    private Task ShowDocumentComparisonAsync(MonacoEditorDocument original, MonacoEditorDocument modified)
    {
        VerifyOpenDocument(original); VerifyOpenDocument(modified);
        CancelRenameSession(); CloseTransientEditorCore(); CloseInGroupSplit();
        _comparisonEditor = CreateReadonlyDiff(original.Model.Text, modified.Model.Text, modified.LanguageId,
            original.DisplayName, modified.DisplayName);
        CreateTransientEditor(original.DisplayName + " ↔ " + modified.DisplayName, _comparisonEditor);
        _comparisonEditor.ModifiedEditorControl.Focus(FocusState.Programmatic);
        return Task.CompletedTask;
    }
}
