#!/usr/bin/env python3
"""Prevent accidental restoration of CI Windows startup hangs.

Windows ABI CTest executables must have their DLL staged beside them; a
missing-DLL GUI can block a headless runner before the test's main(). The
Core-only package consumer must not activate the transitive WindowsAppSDK UI.
"""

from pathlib import Path
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


def require(value: bool, problem: str) -> None:
    if not value:
        raise AssertionError(problem)


def main() -> None:
    cmake = (ROOT / "tests/NativeAbiTests/CMakeLists.txt").read_text(encoding="utf-8")
    require("if(WIN32)" in cmake, "ABI Windows-only guard is missing")
    require('"$<TARGET_FILE:MonacoNative.Core.Native>"' in cmake,
            "ABI CTest must stage exactly its own compiled native DLL")
    require('"$<TARGET_FILE_DIR:MonacoNative.NativeAbi.Tests>"' in cmake,
            "ABI CTest must copy DLL to its executable directory")
    require("copy_if_different" in cmake, "ABI native DLL copy command is missing")
    require("FIXTURES_SETUP MonacoNativeAbiLoaded" in cmake,
            "ABI loader canary fixture is missing")
    require("FIXTURES_REQUIRED MonacoNativeAbiLoaded" in cmake,
            "ABI scenarios must depend on the loader canary")

    root = ET.parse(ROOT / "tests/PackageConsumerSmoke/CoreConsumer/CoreConsumer.csproj").getroot()
    props = {item.tag: item.text for group in root.findall("PropertyGroup") for item in group}
    require(props.get("OutputType") == "Exe", "Core consumer must be a console exe")
    for key in ("WindowsAppSdkBootstrapInitialize", "WindowsAppSdkDeploymentManagerInitialize",
                "WindowsAppSDKBootstrapAutoInitializeOptions_OnNoMatch_ShowUI"):
        require(props.get(key) == "false", f"Core consumer must disable {key}")

    smoke_script = (ROOT / "scripts/test-package-core-runtime.ps1").read_text(encoding="utf-8")
    require("WaitForExit(45000)" in smoke_script, "Package consumer needs a 45s limit")
    require("CoreConsumer.exe" in smoke_script, "Package smoke must launch built console exe")
    require("dotnet run --project $core" not in smoke_script,
            "Do not re-enter dotnet run for package Core-only smoke")
    require("package-core-consumer-stderr.log" in smoke_script,
            "Core smoke must capture its diagnostic stderr")

    preflight_script = (ROOT / "scripts/test-core.ps1").read_text(encoding="utf-8")
    require("$abiDll" in preflight_script and "PathType Leaf" in preflight_script,
            "MSVC preflight must assert the staged ABI DLL exists")

    # Golden and benchmark probes genuinely exercise Win2D; unlike CoreConsumer,
    # they must keep runtime bootstrapping, but it must never display a modal UI.
    for project in ("tests/RendererGoldenTests/MonacoNative.RendererGolden.Tests.csproj",
                    "benchmarks/RendererBenchmarks/MonacoNative.Renderer.Benchmarks.csproj"):
        doc = ET.parse(ROOT / project).getroot()
        props = {item.tag: item.text for group in doc.findall("PropertyGroup") for item in group}
        require(props.get("WindowsPackageType") == "None", f"{project}: unpackaged test expected")
        require(props.get("WindowsAppSDKBootstrapAutoInitializeOptions_None") == "true",
                f"{project}: bootstrapper must use noninteractive options")
        require(props.get("WindowsAppSDKBootstrapAutoInitializeOptions_OnNoMatch_ShowUI") == "false",
                f"{project}: no missing-runtime dialog may block CI")
        require(props.get("WindowsAppSdkDeploymentManagerInitialize") == "false",
                f"{project}: deployment manager should not run for offscreen probes")
        package_versions = {item.get("Include"): item.get("Version") for item in doc.findall(".//PackageReference")}
        require(package_versions.get("Microsoft.WindowsAppSDK") == "2.4.0",
                f"{project}: runtime installer version must match the pinned SDK")

    workflow = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
    runtime = (ROOT / "scripts/install-windows-app-runtime-ci.ps1").read_text(encoding="utf-8")
    require("install-windows-app-runtime-ci.ps1" in workflow,
            "Windows job must install the required runtime before starting Win2D probes")
    require(workflow.index("run: ./scripts/install-windows-app-runtime-ci.ps1") <
            workflow.index("run: ./scripts/test-renderer-golden.ps1"),
            "Windows runtime must be installed before running the renderer tests")
    require("windows-renderer-headless-diagnostics" in workflow,
            "Windows CI must upload render-probe logs even when the probes fail")
    require('test-core.ps1 -Configuration Release -BuildOnly' in workflow,
            'MSVC binary build must be separate from core, ABI and input CTest steps')
    for label in ("core", "abi", "input"):
        require(f"-L '^{label}$'" in workflow,
                f"Windows CI must have an independently reported {label} CTest group")
    require('Join-Path $PWD "artifacts/benchmarks/renderer-quick.json"' in workflow,
            'Windows renderer CI must pass a rooted benchmark artifact path')
    require("2.4.0/windowsappruntimeinstall-x64.exe" in runtime and "'--quiet'" in runtime,
            "Runtime installer must be version-pinned and silent")
    require("WaitForExit(240000)" in runtime,
            "Runtime installer must have a time limit")

    runner = (ROOT / "scripts/invoke-headless-renderer.ps1").read_text(encoding="utf-8")
    require("WaitForExit($TimeoutSeconds * 1000)" in runner and "Kill($true)" in runner,
            "Renderer tests must be killable when they hang")
    require("RedirectStandardOutput" in runner and "RedirectStandardError" in runner,
            "Renderer tests must save stdout and stderr")
    golden_script = (ROOT / "scripts/test-renderer-golden.ps1").read_text(encoding="utf-8")
    benchmark_script = (ROOT / "scripts/run-renderer-benchmarks.ps1").read_text(encoding="utf-8")
    for name, content in (("Golden probe", golden_script), ("Renderer benchmarks", benchmark_script)):
        require("invoke-headless-renderer.ps1" in content and "dotnet run --project" not in content,
                f"{name} must directly run its EXE through the watchdog")

    print("Windows headless smoke guards OK: ABI DLL staging, WinAppSDK runtime, noninteractive Win2D probes, watchdog and logs")


if __name__ == "__main__":
    try:
        main()
    except (AssertionError, ValueError, OSError, ET.ParseError) as exc:
        print(f"Windows headless smoke guard FAILED: {exc}", file=sys.stderr)
        sys.exit(1)
