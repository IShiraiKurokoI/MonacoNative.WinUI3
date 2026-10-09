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
assert app is not None
print('PASS: Find/Replace, opacity, routed input, themes, transactional resize, fast selection, view option commits')
