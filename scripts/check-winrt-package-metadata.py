#!/usr/bin/env python3
"""Fail fast on native WinRT NuGet metadata/targets layout regressions."""
from pathlib import Path
import sys
from xml.etree import ElementTree

root = Path(__file__).resolve().parents[1]
nuspec = root / "packaging/MonacoNative.WinRT.CoreHost.nuspec"
targets = root / "packaging/buildTransitive/MonacoNative.WinRT.CoreHost.targets"
metadata = ElementTree.parse(nuspec).getroot()

# The WinMD must be installed under build/native with its targets, avoiding
# both NuGet invalid references (NU5018) and automatic ref/native warnings (NU5131).
if metadata.findall(".//references"):
    sys.exit("WinRT package must not declare <references>: WinMD is a C++/WinRT build/native input, not a compile assembly (NU5018)")

files = {
    (item.attrib.get("src", "").replace("/", "\\"), item.attrib.get("target", "").replace("/", "\\"))
    for item in metadata.findall(".//files/file")
}
if any(destination.startswith(r"ref\native") for _, destination in files):
    sys.exit("WinRT metadata must not enter NuGet ref/native: causes NU5131")
if not any(destination == r"build\native\MonacoNative.WinRT.winmd" for _, destination in files):
    sys.exit("WinRT package must include build/native/MonacoNative.WinRT.winmd")
if not any(destination == r"build\native\MonacoNative.WinRT.CoreHost.targets" for _, destination in files):
    sys.exit("WinRT package must include build/native/MonacoNative.WinRT.CoreHost.targets")
# CoreHost declares a native NuGet dependency group, but intentionally has no
# managed lib/ref assemblies. The _._ marker pairs that group with lib/native
# without exposing a bogus WinMD assembly reference (NU5128/NU5131).
groups = metadata.findall(".//dependencies/group")
if not any(group.get("targetFramework") == "native" and any(
        item.get("id") == "Microsoft.Windows.CppWinRT" for item in group.findall("dependency"))
        for group in groups):
    sys.exit("CoreHost must retain its native C++/WinRT NuGet dependency")
if (r"empty\_._", r"lib\native\_._") not in files:
    sys.exit("CoreHost must include lib/native/_._ to match its native dependency group (NU5128)")
if (root / "packaging/empty/_._").read_bytes():
    sys.exit("NuGet lib/native/_._ marker must be empty")

project = ElementTree.parse(targets).getroot()
winmd_path = "$(MSBuildThisFileDirectory)MonacoNative.WinRT.winmd"
if winmd_path not in targets.read_text(encoding="utf-8"):
    sys.exit("WinRT .targets must resolve the WinMD next to its build/native file")
ns = "{http://schemas.microsoft.com/developer/msbuild/2003}"
references = project.findall(f".//{ns}Reference")
winmd_references = [ref for ref in references if ref.findtext(f"{ns}WinMDFile") == "true"]
if not any("MonacoNative.WinRT" in ref.attrib.get("Include", "") and
           "_MonacoNativeWinRTWinMD" in (ref.findtext(f"{ns}HintPath") or "")
           for ref in winmd_references):
    sys.exit("WinRT MSBuild targets must resolve the packaged WinMD, not the component DLL")
print("WinRT NuGet metadata/targets OK: WinMD in build/native, native dependency group matched by lib/native/_._")
