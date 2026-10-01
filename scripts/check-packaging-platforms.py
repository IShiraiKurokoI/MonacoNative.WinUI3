#!/usr/bin/env python3
"""Validate portable managed assemblies and the Windows COM compile contract."""
from pathlib import Path
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
for rel in (
    "src/MonacoNative.Core.Managed/MonacoNative.Core.Managed.csproj",
    "src/MonacoNative.Renderer.WinUI3/MonacoNative.Renderer.WinUI3.csproj",
):
    project = ET.parse(root / rel).getroot()
    properties = {node.tag: node.text for group in project.findall("PropertyGroup") for node in group}
    assert properties.get("PlatformTarget") == "AnyCPU", (
        f"{rel}: shared NuGet lib must use AnyCPU to support x64 and ARM64 consumers"
    )

vcx = (root / "src/MonacoNative.Input.Native/MonacoNative.Input.Native.vcxproj").read_text(encoding="utf-8")
cmake = (root / "src/MonacoNative.Input.Native/CMakeLists.txt").read_text(encoding="utf-8")
assert "__INLINE_ISEQUAL_GUID" in vcx and "__INLINE_ISEQUAL_GUID" in cmake, (
    "Native TSF projects must use inline GUID comparison with ole32"
)
print("Package architecture and Windows COM warning guards OK")
