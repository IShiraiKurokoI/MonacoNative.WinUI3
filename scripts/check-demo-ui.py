#!/usr/bin/env python3
"""Regression guard for demo assets, i18n coverage, find layout, and toggle-menu policy."""
from pathlib import Path
from xml.etree import ElementTree as ET
import json
import re

root = Path(__file__).resolve().parents[1]
app_dir = root / 'src/MonacoNative.SampleApp'
renderer = root / 'src/MonacoNative.Renderer.WinUI3'
sample = ET.parse(app_dir / 'MainWindow.xaml')
editor = ET.parse(renderer / 'MonacoEditor.xaml')
xns = '{http://schemas.microsoft.com/winfx/2006/xaml}'
find = lambda parent, name: next(n for n in parent.iter() if n.attrib.get(xns+'Name') == name)
view = find(sample.getroot(), 'ViewOptionsButton')
assert '▾' not in view.attrib['Content'], 'DropDownButton already renders its arrow'
widget = find(editor.getroot(), 'FindWidget')
assert int(widget.attrib['Width']) < 600
assert find(editor.getroot(), 'FindInSelection').attrib['Grid.Column'] == '5'
assert find(editor.getroot(), 'ReplaceRow').attrib['Grid.Row'] == '1'
assert len([n for n in find(editor.getroot(), 'ReplaceRow').iter()
            if n.attrib.get(xns+'Name') == 'ReplaceTextBox']) == 1
source = (renderer / 'MonacoEditor.xaml.cs').read_text()
assert 'UpdateFindWidgetPlacement' in source and 'MinimapWidth' in source
assert 'SetContextMenuToggleVisibility' in source
assert 'EditorCommandAvailability.ShortcutOnly' in (renderer / 'EditorCommandAvailability.cs').read_text()
assert 'IsBasic(commandId)' in (renderer / 'EditorCommandAvailability.cs').read_text()
assert 'IsBasic(commandId) || toggle' not in (renderer / 'EditorCommandAvailability.cs').read_text()
assert 'FindReplaceChevron.Glyph' in (renderer / 'MonacoEditor.Contributions.cs').read_text()
source_sample = (app_dir / 'MainWindow.xaml.cs').read_text()
assert 'RegisterDocumentFormattingEditProvider' in source_sample
assert 'await Editor.FormatDocumentAsync()' in source_sample
csproj = (app_dir / 'MonacoNative.SampleApp.csproj').read_text()
assert '<Compile Remove="Assets\\Samples\\**\\*.cs"' in csproj
for name in ('demo.html','demo.js','demo.cs','demo.cpp'):
    assert (app_dir / 'Assets/Samples' / name).is_file(), name

sample_keys = {
    'viewOptions','example','commandAccess','commandAccessDescription','findButton','replaceButton',
    'advancedPolicy','advancedPolicyDescription','languageServices','modeBoth','modeShortcut',
    'modeDisabled','profileOn','profileOff','formatButton','formatError','languageStatus',
    'highlightOn','highlightOff','lspConnected','lspDisconnected','profileSuccess','profileError',
    'loadError','wordWrap','whitespace','minimap','indentGuides','folding','overview',
    'bracketColors','codeLens','inlineCompletion','stickyScroll'
}
find_keys={'noResults','matchCount','regex','inSelection','preserveCase','toggleReplace','selectTextFirst','invalidRegex'}
for culture in ('en-US','zh-CN','ja-JP','ko-KR'):
    data=json.loads((root / 'src/MonacoNative.I18n/Locales' / (culture+'.json')).read_text())
    missing={f'monacoNative.sample.{key}' for key in sample_keys} | {f'monacoNative.find.{key}' for key in find_keys}
    assert not (missing - data.keys()), f'{culture} missing translations: {missing - data.keys()}'
    assert all(bool(value.strip()) for value in data.values()), f'{culture}: empty translations'
print('PASS: compact find placement, minimap protection, localized view options, sample formatting and toggle defaults')
