#!/usr/bin/env python3
"""Guard the .NET target framework and the functional Windows test entry point."""
from __future__ import annotations

import json
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
WINDOWS_TFM = "net10.0-windows10.0.26100.0"
LAYERS = ("Build", "Core", "Services", "WinUI", "Integration", "Packages")


def find_errors(root: Path) -> list[str]:
    errors: list[str] = []
    projects = [p for p in root.rglob("*.csproj") if not {"bin", "obj"}.intersection(p.parts)]
    if not projects:
        errors.append("no managed projects found")
    for project in projects:
        try:
            node = ET.parse(project).getroot().find("./PropertyGroup/TargetFramework")
        except ET.ParseError as exc:
            errors.append(f"invalid XML in {project}: {exc}")
            continue
        tfm = node.text if node is not None else None
        if tfm not in ("net10.0", WINDOWS_TFM):
            errors.append(f"{project.relative_to(root)} has unsupported TargetFramework {tfm!r}")

    sdk_path = root / "global.json"
    try:
        sdk = json.loads(sdk_path.read_text(encoding="utf-8"))
        if not sdk["sdk"]["version"].startswith("10.0."):
            errors.append("global.json must select the .NET 10 SDK")
    except (OSError, KeyError, ValueError) as exc:
        errors.append(f"global.json must specify an SDK version: {exc}")

    files = {
        "NuGet manifest": root / "packaging/MonacoNative.WinUI3.nuspec",
        "NuGet asset validator": root / "scripts/check-package.py",
        "Windows build": root / "scripts/build-windows.ps1",
        "WinUI UIA runner": root / "tests/UiAutomationTests/Run-EditorUi.ps1",
        "CI": root / ".github/workflows/ci.yml",
        "release workflow": root / ".github/workflows/release.yml",
    }
    for label, path in files.items():
        try:
            data = path.read_text(encoding="utf-8")
            if "net8.0" in data or "8.0.x" in data:
                errors.append(f"{label} still refers to .NET 8: {path.relative_to(root)}")
            if label in ("NuGet manifest", "NuGet asset validator", "Windows build", "WinUI UIA runner") and WINDOWS_TFM not in data:
                errors.append(f"{label} does not reference the Windows .NET 10 output")
            if label in ("CI", "release workflow") and "dotnet-version: '10.0.x'" not in data:
                errors.append(f"{label} is not configured for .NET 10")
        except OSError as exc:
            errors.append(f"missing {label}: {exc}")

    runner_path = root / "scripts/test.ps1"
    try:
        runner = runner_path.read_text(encoding="utf-8")
        for name in LAYERS:
            if f"Invoke-Layer '{name}'" not in runner:
                errors.append(f"test.ps1 is missing the {name} stage")
        if any(flag not in runner for flag in ("'PASS'", "'FAIL'", "'NOT_RUN'", "'PARTIAL'")):
            errors.append("test.ps1 must report PASS, FAIL, NOT_RUN and PARTIAL")
    except OSError as exc:
        errors.append(f"missing test.ps1: {exc}")

    example_path = root / "examples/NuGetWinUI3/MonacoNative.NuGetExample.csproj"
    try:
        example = example_path.read_text(encoding="utf-8")
        if 'PackageReference Include="MonacoNative.WinUI3"' not in example:
            errors.append("standalone example must reference the NuGet package")
        if "<ProjectReference" in example:
            errors.append("standalone example must not reference source projects")
    except OSError as exc:
        errors.append(f"missing standalone example: {exc}")
    return errors


def main() -> int:
    errors = find_errors(ROOT)
    if errors:
        for message in errors:
            print(f"FRAMEWORK TEST FAILED: {message}", file=sys.stderr)
        return 1
    print("Framework and layered testing source check PASS")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
