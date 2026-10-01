#!/usr/bin/env python3
"""Static guard for SampleApp WinUI resource initialization (runtime UI test remains required)."""
from __future__ import annotations

import re
import sys
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "src/MonacoNative.SampleApp/App.xaml"
MAIN = ROOT / "src/MonacoNative.SampleApp/MainWindow.xaml"
UI = "http://schemas.microsoft.com/winfx/2006/xaml/presentation"
X = "http://schemas.microsoft.com/winfx/2006/xaml"
Q = lambda name: f"{{{UI}}}{name}"
KEY = f"{{{X}}}Key"


def main() -> int:
    app = ET.parse(APP).getroot()
    ET.parse(MAIN)  # malformed XAML should also be caught before Windows build
    dictionary = app.find(f"{Q('Application.Resources')}/{Q('ResourceDictionary')}")
    assert dictionary is not None, "SampleApp is missing its application resource dictionary"
    merged = dictionary.find(Q('ResourceDictionary.MergedDictionaries'))
    assert merged is not None, "WinUI control resources must be merged in App.xaml"
    controls = [node for node in merged if node.tag == "{using:Microsoft.UI.Xaml.Controls}XamlControlsResources"]
    assert len(controls) == 1, "Merge exactly one Microsoft.UI.Xaml.Controls.XamlControlsResources"

    themes = dictionary.find(Q('ResourceDictionary.ThemeDictionaries'))
    assert themes is not None, "SampleApp theme dictionaries missing"
    dictionaries = {item.get(KEY): item for item in themes}
    assert {'Light', 'Dark', 'Default'} <= dictionaries.keys(), "Missing theme dictionary"
    theme_keys = {name: {item.get(KEY) for item in d} for name, d in dictionaries.items()}
    app_brushes = theme_keys['Light']
    for theme in ('Dark', 'Default'):
        assert theme_keys[theme] == app_brushes, f"{theme} theme is missing brush keys"

    app_style_keys = {node.get(KEY) for node in dictionary if node.get(KEY)}
    text = MAIN.read_text(encoding='utf-8')
    for ref in re.findall(r'\{ThemeResource ([^} ]+)\}', text):
        # This sample currently only uses application-owned theme brushes in MainWindow.
        assert ref in app_brushes, f"MainWindow references missing theme brush: {ref}"
    for ref in re.findall(r'\{StaticResource ([^} ]+)\}', text):
        assert ref in app_style_keys, f"MainWindow references missing application style: {ref}"
    print("PASS: WinUI default control resources merged; all SampleApp theme and style references resolve")
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except (AssertionError, ET.ParseError) as exc:
        sys.exit(f"FAIL: {exc}")
