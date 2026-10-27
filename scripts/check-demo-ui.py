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
assert 'RegisterDocumentFormattingEditProvider' not in source_sample, 'sample must use language-package formatters'
assert 'await Editor.FormatDocumentAsync()' in source_sample
csproj = (app_dir / 'MonacoNative.SampleApp.csproj').read_text()
assert '<Compile Remove="Assets\\Samples\\**\\*.cs"' not in csproj, 'C# sample must be project-backed for Roslyn semantic completion'
assert 'ResolveSamplePath' in source_sample
assert 'ConnectLanguagePackageServerAsync' not in source_sample
assert 'LanguageServerLauncher' not in source_sample
assert '_sampleLanguageServer' not in source_sample
assert 'MONACO_' not in source_sample
assert 'nativePortWarning' not in source_sample, 'sample must not show the source-port warning in the status bar'
assert 'GetNativePortStatus' not in source_sample, 'host must not inspect language package internals'
# The shortcut picker belongs to SampleApp, but its captions are localized and
# switching UI language must not silently reset the active renderer keymap.
assert 'x:Name="KeyboardShortcutsHeader"' in (app_dir / 'MainWindow.xaml').read_text(encoding='utf-8')
assert 'ShortcutSchemeOption' in source_sample
assert 'RefreshShortcutSchemeOptions();' in source_sample
assert 'Editor.ShortcutScheme = option.Value' in source_sample
assert 'var selectedScheme = Editor.ShortcutScheme' in source_sample
assert 'Editor.TryGetLanguageStatus(out var language)' in source_sample
assert 'public bool TryGetLanguageStatus(out EditorLanguageStatus status)' in (renderer / 'MonacoEditor.Status.cs').read_text()
assert 'SampleBox.SelectedIndex = 2' in source_sample, 'initial example must remain the preformatted C# sample'
for name in ('demo.html','demo.js','demo.cs','demo.cpp','demo.json','demo.css','demo.ts'):
    assert (app_dir / 'Assets/Samples' / name).is_file(), name

# Structural regression guard, not a C# compiler or a behavior-equivalence test.
# Built-in packages must register native contributions, not external launch profiles.
for language in ('Json', 'Html', 'Css', 'TypeScript', 'CSharp', 'Cpp'):
    package_path = root / f'src/MonacoNative.Language.{language}/{language}LanguagePackage.cs'
    package_source = package_path.read_text(encoding='utf-8')
    assert f'{language}LanguagePackage : ILanguagePackage' in package_source, language
    assert 'ILspLanguagePackage' not in package_source, language
    assert 'LanguageServerProfile' not in package_source, language
    assert 'Process.Start' not in package_source and 'StdioLspTransport' not in package_source, language
    assert 'CompletePort' not in package_source, f'{language}: do not claim a complete source port'
    expected = 'InProcessUpstream' if language in ('CSharp', 'Cpp') else 'PartialPort'
    assert f'LanguageSourcePortStatus.{expected}' in package_source, language
for language, scanner in (('Json', 'JsoncScanner'), ('Html', 'HtmlScanner'), ('Css', 'CssScanner')):
    directory = root / f'src/MonacoNative.Language.{language}'
    source_scanner = (directory / (scanner + '.cs')).read_text(encoding='utf-8')
    assert 'Copyright (c) Microsoft Corporation' in source_scanner, scanner
    assert 'MIT License' in source_scanner, scanner
    assert 'CancellationToken' in source_scanner, scanner
    provider = (directory / f'{language}SyntaxCompletionProvider.cs').read_text(encoding='utf-8')
    if language == 'Css':
        assert 'CssDocument.Parse(' in provider and 'CssDialectScanner.Create(languageId, text, cancellationToken)' in (directory / 'CssDocument.cs').read_text(), 'CSS tree must dispatch the document dialect scanner'
        dialects = (directory / 'CssDialectScanner.cs').read_text()
        for implementation in ('new CssScanner(', 'new ScssScanner(', 'new LessScanner('):
            assert implementation in dialects, implementation
    else:
        assert f'new {scanner}(' in provider, f'{language}: scanner must be used, not dead source'
    assert 'new LanguageTextSnapshot(' in provider, f'{language}: multi-line UTF-16 mapping'
json_package = (root / 'src/MonacoNative.Language.Json/JsonLanguagePackage.cs').read_text(encoding='utf-8')
assert 'RegisterDocumentFormattingEditProvider' in json_package
assert 'RegisterDocumentRangeFormattingEditProvider' in json_package
assert 'new JsoncFormattingProvider()' in json_package
assert 'new JsonNativeLanguageService(Schemas)' in json_package
assert 'RegisterDocumentDiagnosticsProvider' in json_package
sharp_package = (root / 'src/MonacoNative.Language.CSharp/CSharpLanguagePackage.cs').read_text()
assert 'new CSharpNativeLanguageService(Options)' in sharp_package
assert 'new WordCompletionProvider' not in sharp_package
sharp_engine = (root / 'src/MonacoNative.Language.CSharp/CSharpNativeLanguageService.cs').read_text()
assert 'CompletionService.GetService(document)' in sharp_engine
assert 'RequireResolvedEdit: true' in sharp_engine
assert 'RequireResolvedEdit = false' in sharp_engine
for package_dir in root.glob('src/MonacoNative.Language.*'):
    for source_path in package_dir.glob('*.cs'):
        implementation = source_path.read_text()
        assert 'Process.Start(' not in implementation and 'new Process(' not in implementation
        assert 'StdioLspTransport' not in implementation and 'LanguageServerLauncher' not in implementation
assert 's.LanguageId is "html" or "javascript" or "csharp"' not in source_sample
assert 'ScheduleNativeDiagnostics' in source and 'CancelNativeDiagnostics' in source
language_ui = (renderer / 'MonacoEditor.Language.cs').read_text()
assert 'snapshot.Matches(_languageDocument)' in language_ui
assert 'LanguageModelSnapshot.Capture' in language_ui

print('PASS: native-only built-in package declarations, scanner/provider wiring and explicit partial status')

# Formatting belongs to language packages rather than SampleApp.
for language in ('Html', 'Css', 'Cpp'):
    package = (root / f'src/MonacoNative.Language.{language}/{language}LanguagePackage.cs').read_text()
    assert 'RegisterDocumentFormattingEditProvider' in package, f'{language}: document formatter missing'
    assert 'RegisterDocumentRangeFormattingEditProvider' in package, f'{language}: range formatter missing'
ts_package = (root / 'src/MonacoNative.Language.TypeScript/TypeScriptLanguagePackage.cs').read_text()
assert 'RegisterDocumentFormattingEditProvider' in ts_package and 'RegisterDocumentRangeFormattingEditProvider' in ts_package
assert (root / 'src/MonacoNative.Language/BasicSourceFormatter.cs').is_file()
assert not (root / 'src/MonacoNative.SampleApp/DemoSampleFormattingProvider.cs').exists()
print('PASS: language-package formatting providers replace sample-only formatters')

sample_keys = {
    'viewOptions','example','commandAccess','commandAccessDescription','keyboardShortcuts','shortcuts.vscode',
    'shortcuts.visualStudio','findButton','replaceButton',
    'advancedPolicy','advancedPolicyDescription','languageServices','modeBoth','modeShortcut',
    'modeDisabled','profileOn','profileOff','formatButton','formatError','languageStatus',
    'highlightOn','highlightOff','nativeInProcess','nativePartial','nativeBaseline','nativeComplete','nativePortWarning','profileSuccess','profileError',
    'loadError','wordWrap','whitespace','minimap','indentGuides','folding','overview',
    'bracketColors','codeLens','inlineCompletion','stickyScroll'
}
find_keys={'noResults','matchCount','regex','inSelection','preserveCase','toggleReplace','selectTextFirst','invalidRegex'}
for culture in ('en-US','zh-CN','ja-JP','ko-KR'):
    data=json.loads((root / 'src/MonacoNative.EditorServices/I18n/Locales' / (culture+'.json')).read_text())
    missing={f'monacoNative.sample.{key}' for key in sample_keys} | {f'monacoNative.find.{key}' for key in find_keys}
    assert not (missing - data.keys()), f'{culture} missing translations: {missing - data.keys()}'
    assert all(bool(value.strip()) for value in data.values()), f'{culture}: empty translations'
# Scroll extent is owned by the renderer, not by the SampleApp parent layout.
editor_xaml = (renderer / 'MonacoEditor.xaml').read_text(encoding='utf-8')
scroll_source = (renderer / 'EditorScrollMetrics.cs').read_text(encoding='utf-8')
assert 'HorizontalScrollMode="Auto"' in editor_xaml
assert 'LayoutUpdated="ScrollHost_LayoutUpdated"' in editor_xaml
assert 'EditorScrollMetrics.ExtentWidth(' in source
assert 'ScrollHost.ViewportWidth' in source
assert 'if (wordWrap)' in scroll_source and 'return viewport;' in scroll_source
assert 'ScrollExtent.Width = contentWidth;' not in source
print('PASS: renderer-owned viewport extent and localized keyboard shortcut picker')
print('PASS: compact find placement, minimap protection, localized view options and toggle defaults')

# Both new engines must remain in-process and inside the existing native project.
native_project = (root / 'src/MonacoNative.Core.Native/MonacoNative.Core.Native.vcxproj').read_text()
native_cmake = (root / 'src/MonacoNative.Core.Native/CMakeLists.txt').read_text()
for name in ('ClangLanguageEngine.cpp', 'TypeScriptLanguageEngine.cpp'):
    assert name in native_project and name in native_cmake, name
for language, service in (('Cpp', 'CppNativeLanguageService'), ('TypeScript', 'TypeScriptNativeLanguageService')):
    source = (root / f'src/MonacoNative.Language.{language}/{language}LanguagePackage.cs').read_text()
    assert f'new {service}(' in source
    for provider in ('CompletionItem', 'Hover', 'Definition', 'DocumentDiagnostics'):
        assert f'Register{provider}Provider' in source
    for native in (root / f'src/MonacoNative.Language.{language}/Native').glob('*'):
        text = native.read_text()
        assert 'system(' not in text and 'popen(' not in text and 'CreateProcess' not in text
bridge = (root / 'src/MonacoNative.Language/NativeCompilerService.cs').read_text()
for required in ('SafeHandleZeroOrMinusOneIsInvalid', 'mn_language_free', 'Encoding.UTF8.GetCharCount', 'Task.Run', 'EnsureAbi', 'NATIVE_UNAVAILABLE'):
    assert required in bridge, required
print('PASS: in-process Clang/TypeScript provider wiring, ABI lifetime and byte-to-UTF-16 conversion guards')

# Fixed binary dependencies and source-ported tests must be actual build inputs.
sharp_project = ET.parse(root / 'src/MonacoNative.Language.CSharp/MonacoNative.Language.CSharp.csproj')
sharp_refs = {item.attrib['Include']: item.attrib['Version'] for item in sharp_project.iter('PackageReference')}
assert sharp_refs['Microsoft.CodeAnalysis.CSharp.Features'] == '[4.11.0]'
assert all('Roslyn' not in item.attrib['Include'] for item in sharp_project.iter('ProjectReference'))
cpp_project = ET.parse(root / 'src/MonacoNative.Language.Cpp/MonacoNative.Language.Cpp.csproj')
cpp_refs = {item.attrib['Include']: item.attrib['Version'] for item in cpp_project.iter('PackageReference')}
for rid in ('win-x64', 'win-arm64'):
    assert cpp_refs['libclang.runtime.' + rid] == '[17.0.4]'
assert 'MonacoLLVMRoot' not in csproj
assert 'UpstreamPortTests.Run();' in (root / 'tests/LanguageTests/Program.cs').read_text()
for part in ('Json', 'Html', 'Css'):
    assert (root / f'tests/LanguageTests/UpstreamPortTests.{part}.cs').is_file()
assert 'UpstreamScannerTests.cpp' in (root / 'tests/NativeAbiTests/CMakeLists.txt').read_text()
assert 'RegisterRenameProvider' in (root / 'src/MonacoNative.Language.Html/HtmlLanguagePackage.cs').read_text()
print('PASS: exact precompiled dependency versions, RID assets, ported test build inputs and HTML rename registration')
