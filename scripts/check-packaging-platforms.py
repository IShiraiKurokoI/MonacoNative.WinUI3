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


# The deployed-binary checks must be real build/publish targets, not just docs.
import json
import re
ns = {"m": "http://schemas.microsoft.com/developer/msbuild/2003"}
validation = ET.parse(root / "packaging/buildTransitive/MonacoNative.NativeValidation.targets").getroot()
core_exports = set(json.loads((root / "reference/native-abi.json").read_text())["exports"])
core_exports.update(re.findall(r"MN_CALL\s+(mn_\w+)\(", (root / "src/MonacoNative.Core.Native/MonacoNative.Language.Native.h").read_text()))
clang_exports = set(re.findall(r"API\([^,\n]+,\s*(clang_\w+)", (root / "src/MonacoNative.Language.Cpp/Native/ClangLanguageEngine.cpp").read_text()))
assert set(validation.find("m:PropertyGroup/m:_MonacoNativeCoreExports", ns).text.split(";")) == core_exports
assert set(validation.find("m:PropertyGroup/m:_MonacoNativeClangExports", ns).text.split(";")) == clang_exports
targets = ET.parse(root / "packaging/buildTransitive/MonacoNative.ClangRuntime.targets").getroot()
for name, after, output in (("MonacoNativeCheckBuildDependencies", "Build", "$(TargetDir)"),
                            ("MonacoNativeCheckPublishDependencies", "Publish", "$(PublishDir)")):
    target = targets.find(f"m:Target[@Name='{name}']", ns)
    assert target is not None and target.get("AfterTargets") == after
    assert "DesignTimeBuild" in target.get("Condition", "") and "OutputType" in target.get("Condition", "")
    tasks = target.findall("m:MonacoNative.Build.MonacoNativeValidateBinary", ns)
    assert {t.get("FileName") for t in tasks} == {"libclang.dll", "MonacoNative.Core.Native.dll"}
    assert all(t.get("DirectoryPath") == output and t.get("RequiredExports") for t in tasks)
source = (root / "packaging/buildTransitive/MonacoNative.NativeBinaryCheck.cs").read_text()
assert "LoadLibrary(" not in source and "NativeLibrary.Load(" not in source
assert "FindDeployedLibrary" in source and "Conflicting Platform" in source
sample = ET.parse(root / "src/MonacoNative.SampleApp/MonacoNative.SampleApp.csproj").getroot()
assert sample.find("PropertyGroup/MonacoNativeValidateCore").text == "true"
assert sample.find("PropertyGroup/MonacoNativeValidationDependsOn").text == "CopyNativeCore"
assert sample.find("Target[@Name='IncludeNativeCoreForPublish']/ItemGroup/ResolvedFileToPublish") is not None
native = ET.parse(root / "src/MonacoNative.Core.Native/MonacoNative.Core.Native.vcxproj").getroot()
assert native.find("m:Target[@Name='MonacoNativeCheckLinkedCore']", ns) is not None
for name in ("MonacoNative.NativeValidation.targets", "MonacoNative.NativeBinaryCheck.cs"):
    assert name in (root / "packaging/MonacoNative.WinUI3.nuspec").read_text()
    assert name in (root / "src/MonacoNative.Language.Cpp/MonacoNative.Language.Cpp.csproj").read_text()
assert "NativeBinaryValidationTests.Run();" in (root / "tests/LanguageTests/Program.cs").read_text()
print(f"Native binary build/publish wiring and export manifests OK ({len(core_exports)} core/language, {len(clang_exports)} Clang exports; structural check only)")
