"""Regression checks for portable UTF-8 source validation on Windows code pages."""
import subprocess
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


class SourceEncodingTests(unittest.TestCase):
    def test_demo_ui_check_with_legacy_windows_code_page(self):
        script = (
            "import locale, runpy; "
            "locale.getencoding = lambda: 'cp1252'; "
            "runpy.run_path('scripts/check-demo-ui.py', run_name='__main__')"
        )
        result = subprocess.run([sys.executable, '-X', 'utf8=0', '-c', script],
                                cwd=ROOT, capture_output=True, text=True, encoding='utf-8')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('PASS: native-only built-in package declarations', result.stdout)


if __name__ == '__main__':
    unittest.main()
