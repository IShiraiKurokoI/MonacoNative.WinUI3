#!/usr/bin/env python3
"""Static regression checks for WinUI find UI, Modern theme and editor paint paths.

A Windows UI automation run is still required for visual/runtime validation.
"""
from pathlib import Path
import re
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
renderer = root / 'src/MonacoNative.Renderer.WinUI3'
source = (renderer / 'MonacoEditor.xaml.cs').read_text(encoding='utf-8')
language = (renderer / 'MonacoEditor.Language.cs').read_text(encoding='utf-8')
sample = (root / 'src/MonacoNative.SampleApp/MainWindow.xaml.cs').read_text(encoding='utf-8')
ns = '{http://schemas.microsoft.com/winfx/2006/xaml}'
editor = ET.parse(renderer / 'MonacoEditor.xaml').getroot()
app = ET.parse(root / 'src/MonacoNative.SampleApp/App.xaml').getroot()

def named(name):
    return next(x for x in editor.iter() if x.get(ns + 'Name') == name)

find = named('FindWidget')
outer = next(child for child in find if child.tag.endswith('Grid'))
replace = named('ReplaceRow')

def widths(grid):
    defs = next(child for child in grid if child.tag.endswith('Grid.ColumnDefinitions'))
    return [element.get('Width') for element in defs]

assert widths(outer) == widths(replace), 'Find and Replace column tracks must match'
assert outer.get('ColumnSpacing') == replace.get('ColumnSpacing')
assert named('FindTextBox').get('Height') == named('ReplaceTextBox').get('Height')
assert named('FindTextBox').get('Margin') == named('ReplaceTextBox').get('Margin')
assert named('FindTextBox').get('Padding') == named('ReplaceTextBox').get('Padding')
assert all(int(named(n).get('Height')) >= 30 for n in ('FindPreviousButton', 'FindNextButton', 'FindCloseButton', 'ReplaceOneButton', 'ReplaceAllButton'))
assert 'ApplyFindWidgetPalette(highContrast, theme)' in source
assert 'FindToggleVisualStateChanged' in source
assert '#1F1F1F' in source and '#ADD6FF' in source and '#264F78' in source
assert 'Modern Light' in source and 'Modern Dark' in source
assert 'VS Code Light Modern' in language
assert 'RefreshEditorTheme();\n        Editor.FlushPendingRender();' in sample
assert 'Editor.FlushPendingRender();' in sample
assert 'DispatcherQueuePriority.High' in source
resize_handler = source[source.index('private void Editor_SizeChanged'):source.index('private void EnsureGraphicsResources')]
assert 'ApplyViewportResize()' in resize_handler
assert 'DispatcherQueuePriority.High' in resize_handler, 'Coalesce resize on dispatcher'
assert 'FlushPendingRender();' in resize_handler
assert 'EnsureSwapChain();' not in resize_handler, 'Never clear the currently attached surface on SizeChanged'
assert '_swapChain.ResizeBuffers(' not in source, 'Do not resize presented surface in place'
render = source[source.index('private void RenderFrame'):source.index('private bool EnsureRenderLayers')]
assert 'new CanvasSwapChain(_device!, width, height, dpi)' in render
assert render.index('target.Present();') < render.index('SwapChainPanel.SwapChain = nextSurface'), 'Present before swapping surface'
assert 'nextSurface?.Dispose()' in render, 'Discard failed replacement without losing current frame'
assert 'CommitViewOptions()' in source and 'Render(RenderDirty.All);' in source
assert 'var enabled = !GetFeatureState(key);' in sample, 'The model state is authoritative at Click time'
assert 'Editor.CommitViewOptions();' in sample
assert 'QueueDragSelectionNotifications();' in source
assert 'GetCharacterRegions' in source and 'CanUseFastSelectionRectangle' in source
assert 'Revision' in (renderer / 'LineLayoutCache.cs').read_text(encoding='utf-8')
assert '_dragHitLayoutRevision == _layoutCache.Revision' in source
cache = (renderer / 'LineLayoutCache.cs').read_text(encoding='utf-8')
view = (root / 'src/MonacoNative.Core.Managed/MonacoViewModel.cs').read_text(encoding='utf-8')
assert 'public bool TryGetCached(' in cache
assert 'public double GetViewLineVerticalOffset(' in view
selection_range = source[source.index('private void DrawSelectionRange'):source.index('private void DrawCompositionDecorations')]
assert '_layoutCache.TryGetCached(lineNumber' in selection_range
assert 'GetViewLineVerticalOffset(lineNumber)' in selection_range
caret = source[source.index('private void DrawCaretAt'):source.index('private static float CaretX')]
assert '_layoutCache.TryGetCached(viewPosition.LineNumber' in caret
assert 'GetViewLineVerticalOffset(viewPosition.LineNumber)' in caret
release = source[source.index('private void SurfaceRoot_PointerReleased'):source.index('private MonacoPosition HitTest')]
assert '_textInput?.NotifyLayoutChanged();' not in release, 'UIA/TSF drag release should not block UI'
notification = source[source.index('private void QueueDragSelectionNotifications'):source.index('private void RevealCaret')]
assert '_textInput?.NotifyLayoutChanged();' in notification
hit = source[source.index('private static int NearestCaretColumn'):source.index('private void RegisterDefaultCommands')]
assert 'layout.HitTest(' in hit
assert 'for (' not in hit and 'while (' not in hit, 'HitTest must not scan entire lines'
assert 'const int context = 256' in hit
assert '_lastProjectionWrapWidth' in source
assert 'FinishDragSelectionNotifications()' in source
finish = source[source.index('private void FinishDragSelectionNotifications'):source.index('private void RevealCaret')]
assert 'QueueDragSelectionNotifications();' in finish
assert 'RaiseAccessibilitySelectionChanged();' in finish, 'Composition path notifies synchronously'
# Overlay opacity, routed-event isolation and Visual Studio build regression checks.
assert find.get('Background') == '#FF202020' and find.get('Opacity') == '1'
assert find.get('IsHitTestVisible') == 'True'
assert 'FindWidget.Background = new SolidColorBrush(Color.FromArgb(255,' in source
assert named('ReplaceTextBox').get('KeyDown') == 'ReplaceTextBox_KeyDown'
contributions = (renderer / 'MonacoEditor.Contributions.cs').read_text(encoding='utf-8')
assert 'private void ReplaceTextBox_KeyDown' in contributions
pressed = source[source.index('private void SurfaceRoot_PointerPressed'):source.index('private void SurfaceRoot_PointerEntered')]
assert pressed.index('IsPointerOverFindWidget(e)') < pressed.index('Focus(FocusState.Pointer)')
assert 'e.Handled = true;' in pressed[:pressed.index('Focus(FocusState.Pointer)')]
released = source[source.index('private void SurfaceRoot_PointerReleased'):source.index('private MonacoPosition HitTest')]
assert released.index('if (!_dragSelecting)') < released.index('SurfaceRoot.ReleasePointerCapture')
assert 'FindWidget.ActualWidth' in source and 'GetCurrentPoint(FindWidget)' in source
for handler, end in [('Editor_KeyDown', 'Editor_KeyUp'),
                     ('Editor_KeyUp', 'BuildWin32KeyMessageLParam'),
                     ('Editor_CharacterReceived', 'IsKeyDown')]:
    piece = source[source.index('private ' + ('async void ' if handler == 'Editor_KeyDown' else 'void ') + handler):]
    piece = piece[:piece.index('private ', 20)]
    assert 'IsFindOpen && IsChildOf(e.OriginalSource as DependencyObject, FindWidget)' in piece, handler
assert language.count('=> _lastHighContrast == true') == 3
assert '=> _lastHighContrast\n' not in language
unqualified = re.search(r'(?<!Microsoft\.UI\.Dispatching\.)\bDispatcherQueuePriority\.', source)
assert unqualified is None, 'DispatcherQueuePriority must be unambiguous'
# Static XML parse succeeds for both resource dictionaries and editor template.
# IntelliSense must be a dedicated, keyboard-driven overlay, not the generic button popup.
suggest = (renderer / 'MonacoEditor.SuggestWidget.cs').read_text(encoding='utf-8')
assert 'InitializeSuggestWidget()' in contributions
assert 'ShowSuggestWidget(result.Suggestions, caret, version)' in contributions
assert 'new Button' not in suggest, 'Suggest items must not steal focus via Buttons'
assert 'new ScrollViewer' in suggest and 'SuggestionSelection' in suggest
assert 'SuggestKindSymbol(item.Kind)' in suggest and 'AppendLabelWithMatchHighlight' in suggest
assert 'PositionSuggestWidget()' in contributions
assert '_suggestDocumentVersion' in suggest and '_suggestRequestSequence' in contributions
assert '_textInput?.IsComposing == true' in suggest
assert 'VirtualKey.Up' in suggest and 'VirtualKey.Down' in suggest
assert 'VirtualKey.Enter' in suggest and 'VirtualKey.Tab' in suggest
assert 'VirtualKey.Escape' in suggest and 'e.Handled = true' in suggest
assert 'CommitSelectedSuggestionAsync' in suggest
assert 'ViewportHeight' in suggest and 'SuggestOverscanRows' in suggest
assert 'HasMouseActuallyMoved(e)' in suggest, 'Stationary pointer must not replace keyboard suggestion selection'
assert '_lastObservedMousePosition' in source and '_hasObservedMousePosition' in source
assert 'ApplySuggestWidgetPalette(highContrast, theme)' in source
keydown = source[source.index('private async void Editor_KeyDown'):source.index('private void Editor_KeyUp')]
assert keydown.index('TryHandleKeyDown(') < keydown.index('TryHandleSuggestWidgetKeyDown(e)') < keydown.index('ResolveAndExecuteAsync('), 'TSF before suggest before default movement'
assert 'if (!_suppressAutoSuggest && _textInput?.IsComposing != true)' in contributions
assert app is not None
# Completion metadata, snippet routing and font-free upstream vectors.
snippets = (renderer / 'MonacoEditor.Snippets.cs').read_text(encoding='utf-8')
ranking = (root / 'src/MonacoNative.Language/CompletionRanking.cs').read_text(encoding='utf-8')
adapter = (root / 'src/MonacoNative.Language/Lsp/LspLanguageAdapter.cs').read_text(encoding='utf-8')
icons = (renderer / 'CodiconPresenter.cs').read_text(encoding='utf-8')
assert keydown.index('TryHandleSuggestWidgetKeyDown(e)') < keydown.index('TryHandleSnippetKeyDown(e)') < keydown.index('ResolveAndExecuteAsync(')
assert 'ObserveEdit' in snippets and 'GetLinkedEdits' in snippets and 'BeginUndoTransaction' in snippets
assert 'StartSnippetSession(expansion, insertionStart)' in contributions
assert 'insertionStart = start + edits.Where' in contributions
assert 'Completion edits overlap' in contributions
assert 'completionItem/resolve' in adapter and 'itemDefaults' in adapter and 'SnippetText' in (root / 'src/MonacoNative.Language/Providers.cs').read_text()
assert 'CodiconPresenter.Create(item.Kind' in suggest and 'XamlReader.Load' in icons
assert 'Symbol' in suggest and 'Segoe MDL2' not in suggest
assert 'CompletionFuzzyScore.Match' in ranking and 'WordDistance' in ranking and '_prefixMemory' in ranking
assert 'TrackIncompleteCompletionEdit(e)' in source
assert 'commitCharactersSupport = false' in (root / 'src/MonacoNative.Language/Lsp/LspClient.cs').read_text()
assert 'editor.action.triggerParameterHints' in contributions
for suffix in ('.ttf', '.otf', '.woff', '.woff2'):
    assert not list(root.rglob('*' + suffix)), 'No font files in source delivery'
# Native folding providers must be called using an immutable snapshot and discard stale results.
folding = (renderer / 'MonacoEditor.Folding.cs').read_text(encoding='utf-8')
service = (root / 'src/MonacoNative.Language/LanguageService.cs').read_text(encoding='utf-8')
assert 'LanguageModelSnapshot.Capture(_languageDocument)' in folding
assert 'ProvideFoldingRangesAsync(snapshot' in folding and 'Task.Run(' in folding
assert 'snapshot.Matches(_languageDocument)' in folding and 'ReferenceEquals(_foldingCts, source)' in folding
assert 'if (provided is null)' in folding, 'Empty provider output must not be replaced by heuristic ranges'
assert 'CancelFoldingRefresh();' in source
assert 'RegisterFoldingRangeProvider' in service and 'if (result is not null) return result' in service
for package in ('Json', 'Html', 'Css'):
    content = (root / f'src/MonacoNative.Language.{package}/{package}LanguagePackage.cs').read_text(encoding='utf-8')
    assert 'RegisterFoldingRangeProvider' in content
assert 'Folding();' in (root / 'tests/LanguageTests/UpstreamPortTests.cs').read_text(encoding='utf-8')
assert 'ScannerApiTests.cpp' in (root / 'tests/NativeAbiTests/CMakeLists.txt').read_text(encoding='utf-8')
print('PASS: editor, snippet, ranking, vector and native folding-provider source guards')

# Independent parameter hints: triggers and async guards, without focus stealing.
hints = contributions[contributions.index('private async Task ShowParameterHintsAsync'):contributions.index('private void ApplyParameterHintsPalette')]
assert '_parameterHintsRequestSequence' in hints and 'version != _model.VersionId' in hints
assert 'uri != DocumentUri' in hints and 'language != LanguageId' in hints and '!caret.Equals(_active)' in hints
hints_code = re.sub(r'//[^\n]*', '', hints)
assert 'HideSuggestWidget(' not in hints_code and 'Focus(' not in hints_code
assert 'IsSignatureHelpTriggerCharacter' in contributions and 'IsSignatureHelpRetriggerCharacter' in contributions
assert '_parameterHintsTimer.Tick -= ParameterHintsTimer_Tick' in contributions
lifetime = (root / 'src/MonacoNative.Renderer.WinUI3/EditorLifetime.cs').read_text(encoding='utf-8')
assert 'EditorLifetime _lifetime' in source and 'ShutdownGracePeriod' in source and '_lifetime.DrainAsync' in source
assert 'BeginShutdown()' in source and '_lifetime.BeginShutdown()' in source
assert 'Task Track(Task task)' in lifetime and 'BeginShutdown()' in lifetime and 'DrainAsync(TimeSpan timeout)' in lifetime
for tracked in ('_suggestTask = _lifetime.Track', '_parameterHintsTask = _lifetime.Track', '_contributionRefreshTask = _lifetime.Track'):
    assert tracked in contributions
assert '_foldingRefreshTask = _lifetime.Track' in folding
assert '_nativeDiagnosticsTask = _lifetime.Track' in (root / 'src/MonacoNative.Renderer.WinUI3/MonacoEditor.Language.cs').read_text(encoding='utf-8')
assert '_lspRefreshTask = _lifetime.Track' in (root / 'src/MonacoNative.Renderer.WinUI3/MonacoEditor.Lsp.cs').read_text(encoding='utf-8')
for partial in ('MonacoEditor.Contributions.cs', 'MonacoEditor.Folding.cs', 'MonacoEditor.Language.cs', 'MonacoEditor.Lsp.cs'):
    text = (root / f'src/MonacoNative.Renderer.WinUI3/{partial}').read_text(encoding='utf-8')
    assert '_lifetime.ShutdownToken' in text
assert 'await Editor.DisposeAsync();' in sample
assert '_closing = true;' in sample and '_sampleLoadCancellation?.Cancel()' in sample[sample.index('Closed +='):sample.index('InitializeMenuOptions()')]
lsp_session = (root / 'src/MonacoNative.Language/Lsp/LspDocumentSession.cs').read_text(encoding='utf-8')
async_dispose = lsp_session[lsp_session.index('public async ValueTask DisposeAsync()'):]
assert '_disposeCts.Cancel();' not in async_dispose.split('}', 1)[0], 'Async LSP disposal should drain rather than cancel the worker'
assert 'ScheduleAutomaticParameterHints();' in contributions
assert 'parameterHintsVisible && !suggestWidgetVisible' in contributions
assert 'RegisterSignatureHelpProvider' in (root / 'src/MonacoNative.Language.TypeScript/TypeScriptLanguagePackage.cs').read_text()
assert 'BasicEditingTests.cpp' in (root / 'tests/NativeAbiTests/CMakeLists.txt').read_text()
print('PASS: parameter-hints triggers, lifecycle and stale-result guards')

# Guard the source-level connections for editing interactions.
# These are structural checks; LanguageTests and Windows acceptance exercise behavior.
commit = (root / 'src/MonacoNative.Language/CompletionCommitCharacters.cs').read_text(encoding='utf-8')
signature = (root / 'src/MonacoNative.Language/SignatureHelpSelection.cs').read_text(encoding='utf-8')
lsp = (renderer / 'MonacoEditor.Lsp.cs').read_text(encoding='utf-8')
assert 'TryCommitSelectedSuggestionCharacter(text)' in source
assert 'CompletionCommitCharacters.Accepts(item, character)' in suggest
assert 'CommitSelectedSuggestionAsync(character)' in suggest
assert 'CompletionCommitCharacters.AppendOnce(text, commitCharacter)' in contributions
assert 'item.CommitCharacters?' in commit and 'insertion.EndsWith(commitCharacter' in commit
assert 'InitializeSnippetChoicePopup()' in contributions
assert 'ShowSnippetChoices(stop)' in snippets and 'CommitSnippetChoice(advance: true)' in snippets
assert 'ApplySnippetChoicePalette()' in suggest and 'ApplySnippetChoicePalette()' in snippets
assert 'TryHandleSnippetKeyDown(e)' in keydown and 'IsComposing == true' in snippets
assert 'CycleParameterHints(1)' in contributions and 'CycleParameterHints(-1)' in contributions
assert 'SignatureHelpSelection' in signature and 'previousLabel' in signature
assert '_parameterSignatureSelection.SetHelp(help, call)' in contributions
assert 'SignatureCallScanner.Scan(' in contributions and 'SignatureCallContext' in signature
assert 'CreateArrow("\\u25B2"' in contributions and 'CreateArrow("\\u25BC"' in contributions
assert 'AllowFocusOnInteraction = false' in contributions
assert 'DetachLspBindingForDocumentChange()' in language
assert 'request != _semanticTokensRequestSequence' in language
assert 'request != _inlayHintsRequestSequence' in language
assert 'request != _lspBindingRequestSequence' in lsp
print('PASS: completion, snippet choice and document-snapshot wiring guards')

# Shortcut profiles, host overrides and normal shutdown must stay wired to WinUI.
profile = (renderer / 'EditorShortcutProfiles.cs').read_text(encoding='utf-8')
commands = (renderer / 'EditorCommandService.cs').read_text(encoding='utf-8')
cpp_package = (root / 'src/MonacoNative.Language.Cpp/CppLanguagePackage.cs').read_text(encoding='utf-8')
cpp_formatter = (root / 'src/MonacoNative.Language.Cpp/CppClangFormatProvider.cs').read_text(encoding='utf-8')
assert 'public enum EditorShortcutScheme' in profile and 'VSCode,' in profile and 'VisualStudio,' in profile
assert 'ShortcutScheme { get; private set; } = EditorShortcutScheme.VSCode' in commands
assert '_overrides.TryGetValue' in commands and 'RebuildBindings();' in commands
assert 'public void SetShortcutOverride(' in source and 'public EditorShortcutScheme ShortcutScheme' in source
# Command-menu shortcut labels are resolved by the command service, not a static
# renderer helper reading an instance field (a previous CS0120 build regression).
assert source.count('KeyboardAcceleratorTextOverride = _commands.GetShortcutLabel(command),') == 2
assert 'private static string ShortcutLabel(' not in source
assert 'public string GetShortcutLabel(EditorCommandDefinition command)' in commands
assert 'EditorShortcutLabelFormatter.Format(preferred.Chord)' in commands
assert 'internal static class EditorShortcutLabelFormatter' in profile
# Document statistics and language-package introspection live in the renderer,
# not in the example application. The UI never reads a disposed model directly.
status = (renderer / 'MonacoEditor.Status.cs').read_text(encoding='utf-8')
assert 'public bool TryGetDocumentStatus(out EditorDocumentStatus status)' in status
assert 'public bool TryGetLanguageStatus(out EditorLanguageStatus status)' in status
assert 'if (IsDisposed) return false;' in status
assert 'Editor.TryGetDocumentStatus(out var document)' in sample
assert 'Editor.TryGetLanguageStatus(out var language)' in sample
assert 'Editor.VersionId' not in sample and 'Editor.LineCount' not in sample
assert 'MonacoTextStatistics.CountCharacters' not in sample
assert 'ShortcutSchemeBox_SelectionChanged' in sample
assert 'if (_closing || Editor.IsDisposed) return;' in sample
assert 'public bool IsDisposed => _disposed || _lifetime.IsStopping;' in source
assert 'if (IsDisposed || cancellationToken.IsCancellationRequested) return;' in contributions
assert 'new Grid { ColumnSpacing = 6' in contributions and 'of {help.Signatures.Count}' in contributions
assert 'new CppClangFormatProvider(' in cpp_package and 'BasicSourceFormattingProvider' not in cpp_package
assert '--assume-filename=' in cpp_formatter and '--lines=' in cpp_formatter and 'RedirectStandardInput = true' in cpp_formatter
print('PASS: profile overrides, overload alignment, shutdown guards, and LLVM formatter wiring')
