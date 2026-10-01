from pathlib import Path
import json
import sys

root = Path(__file__).resolve().parents[1]
checks = []

def check(value, message):
    checks.append((bool(value), message))

editor_xaml = (root / 'src/MonacoNative.Renderer.WinUI3/MonacoEditor.xaml').read_text(encoding='utf-8')
editor_cs = (root / 'src/MonacoNative.Renderer.WinUI3/MonacoEditor.xaml.cs').read_text(encoding='utf-8')
backend = (root / 'src/MonacoNative.Input/ImeInputBackend.cs').read_text(encoding='utf-8')
sample = (root / 'src/MonacoNative.SampleApp/MainWindow.xaml.cs').read_text(encoding='utf-8')

check('ImeProxyTextBox' not in editor_xaml, 'hidden ImeProxyTextBox removed from XAML')
check(not (root / 'src/MonacoNative.Renderer.WinUI3/TextBoxImeInputController.cs').exists(), 'TextBoxImeInputController source removed')
check(not (root / 'src/MonacoNative.Renderer.WinUI3/CoreTextInputController.cs').exists(), 'CoreTextInputController source removed')
check('CreateTextBoxInputController' not in editor_cs, 'TextBox controller factory removed')
check('InputBackendPreference' not in editor_cs and 'InputBackendChanged' not in editor_cs, 'runtime backend selector API/state removed')
check('NativeTsf = 1' in backend and 'TextBoxProxy' not in backend, 'backend enum contains Native TSF only')
check('InputBackendPreference' not in sample and 'InputBackendChanged' not in sample, 'SampleApp backend switching removed')
check(not list((root / 'reference').glob('*input*-evidence.template.json')), 'temporary input A/B evidence templates absent')
check(not list((root / 'scripts').glob('check-*input*-evidence.py')), 'temporary input A/B evidence checkers absent')

matrix = json.loads((root / 'reference/ime-interactive-matrix.json').read_text(encoding='utf-8'))
check(matrix.get('backendGate', {}).get('requiredBackends') == ['nativeTsf'], 'interactive matrix is native-TSF-only')
check(all(case.get('backends') == ['nativeTsf'] for case in matrix.get('cases', [])), 'all IME cases target native TSF only')

locale_dir = root / 'src/MonacoNative.I18n/Locales'
locale_keys = []
for path in sorted(locale_dir.glob('*.json')):
    locale_keys.append((path.name, set(json.loads(path.read_text(encoding='utf-8')).keys())))
check(bool(locale_keys) and all(keys == locale_keys[0][1] for _, keys in locale_keys[1:]), 'locale key sets are identical')

failed = [message for ok, message in checks if not ok]
for ok, message in checks:
    print(f"[{'PASS' if ok else 'FAIL'}] {message}")
print(f"TSF-only guards: {len(checks)-len(failed)}/{len(checks)} passed")
if failed:
    sys.exit(1)
