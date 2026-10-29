#!/usr/bin/env python3
"""Prevent accidental restoration of CI Windows startup hangs.

Windows ABI CTest executables must have their DLL staged beside them; a
missing-DLL GUI can block a headless runner before the test's main(). The
Core-only package consumer must not activate the transitive WindowsAppSDK UI.
Required language integration must not silently skip missing native dependencies.
"""

from pathlib import Path
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
UNIFIED_COMMAND = "run: ./scripts/test-acceptance.ps1 -Mode Automated -Configuration ${{ matrix.configuration }} -RunPackages -PackageVersion 0.8.0-ci"


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
    require(UNIFIED_COMMAND in workflow and "configuration: [Debug, Release]" in workflow,
            "Windows CI must run Debug and Release through the same acceptance entry point")
    require(workflow.index("run: ./scripts/install-windows-app-runtime-ci.ps1") <
            workflow.index(UNIFIED_COMMAND),
            "Windows runtime must be installed before running the renderer probes")
    require("windows-renderer-headless-diagnostics" in workflow and
            "renderer-current-*.png" in workflow,
            "Windows CI must upload render logs and a visually reviewable PNG")
    acceptance = (ROOT / "scripts/test-acceptance.ps1").read_text(encoding="utf-8")
    windows = (ROOT / "scripts/test-windows.ps1").read_text(encoding="utf-8")
    core = (ROOT / "scripts/test-core.ps1").read_text(encoding="utf-8")
    layered = (ROOT / "scripts/test.ps1").read_text(encoding="utf-8")
    require("scripts/test.ps1" in acceptance.replace("\\", "/") and "-Layer All" in acceptance,
            "Acceptance must invoke the common layered Windows test runner")
    required_layers = ("Build", "Core", "Services", "WinUI", "Integration", "Packages")
    require(all(f"'{layer}'" in layered for layer in required_layers),
            "Layered runner must keep compile, native, services, WinUI and package results distinct")
    for entry in ("test-core.ps1", "build-windows.ps1", "test-portable-managed.ps1",
                  "test-language-integration.ps1", "test-renderer-golden.ps1",
                  "run-renderer-benchmarks.ps1", "test-native-build-validation.ps1",
                  "test-package.ps1", "check-parity-release.py"):
        require(entry in layered, f"Missing required test entry point from layered runner: {entry}")
    require("-RequireGoldenBaseline:$strict" in acceptance and
            "-RequireBaseline:$RequireGoldenBaseline" in layered,
            "Release must require approved renderer baseline")
    require("ctest --test-dir $build --output-on-failure" in core and "[switch]$TestOnly" in core,
            "The native runner must support separate build and CTest stages")
    require("-Mode Release" in (ROOT / ".github/workflows/release.yml").read_text(encoding="utf-8"),
            "Release workflow must run the strict acceptance entry point")
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
    require("$RequireBaseline" in golden_script and "--baseline" in golden_script and "renderer-current-${dpi}dpi.png" in golden_script,
            "Release golden test must check approved baseline and emit actual PNG")

    # Strict language acceptance is separate from optional unit-only runs.
    language_runner = (ROOT / "scripts/test-language-integration.ps1").read_text(encoding="utf-8")
    require("--require-native --require-libclang" in language_runner,
            "Windows language acceptance must require both native dependencies")
    require("dotnet build $project" in language_runner and "-r $rid" in language_runner,
            "Windows language acceptance must test RID-specific NuGet outputs")
    require("Remove-Item -LiteralPath $output" in language_runner,
            "Clear dedicated language-test outputs so stale libraries cannot mask failed deployment")
    require("No global LLVM installation is used as a fallback" in language_runner,
            "NuGet deployment tests must not silently consume a globally installed LLVM")
    require("$env:MONACONATIVE_NATIVE_LIBRARY = $oldNative" in language_runner and
            "$env:MONACONATIVE_LIBCLANG = $oldClang" in language_runner,
            "Language test overrides must be restored after the strict test run")
    require("test-language-integration.ps1" in layered and "test-core.ps1" in layered and
            layered.index("'Build' {") < layered.index("'Services' {"),
            "Windows test runner must build native dependencies before services integration")
    require('MONACONATIVE_NATIVE_LIBRARY="$PWD/build/src/MonacoNative.Core.Native/libMonacoNative.Core.Native.so" '
            'dotnet run --project tests/LanguageTests/MonacoNative.Language.Tests.csproj -c Release -- --require-native' in workflow,
            "Linux GCC acceptance must not skip its built native bridge")
    language_tests = (ROOT / "tests/LanguageTests/Program.cs").read_text(encoding="utf-8")
    require("IntegrationRequirements.Parse(args)" in language_tests and
            "requirements.Validate(Environment.GetEnvironmentVariable, File.Exists)" in language_tests,
            "Required dependency preflight must run before language tests")
    require(language_tests.index("requirements.Validate(Environment.GetEnvironmentVariable, File.Exists)") <
            language_tests.index("await Tests.RunAsync();"), "Validate dependencies before optional skip branches")
    require("IntegrationRequirementsRegression();" in language_tests,
            "Required/optional/invalid argument cases must remain in the existing managed test suite")

    acceptance_runner = (ROOT / 'scripts/test-acceptance.ps1').read_text(encoding='utf-8')
    require("-Platform ARM64 -CoreOnly" in layered and "scripts/test.ps1" in acceptance_runner,
            'Packages layer must cross-check the ARM64 native DLL using the shipping validator')
    validator = (ROOT / 'scripts/test-native-build-validation.ps1').read_text(encoding='utf-8')
    require("[switch]$CoreOnly" in validator,
            'Cross-host ARM64 validation must explicitly skip publish fixture requiring a deployed sample')
    require(validator.count("$global:LASTEXITCODE = 0") >= 2,
            "Expected-negative MSBuild cases must clear their exit code after the validation suite passes")

    print("Windows headless smoke guards OK: unified, DLL staging, baseline-gated Win2D PNG, watchdog, logs and native language integration")


if __name__ == "__main__":
    try:
        main()
    except (AssertionError, ValueError, OSError, ET.ParseError) as exc:
        print(f"Windows headless smoke guard FAILED: {exc}", file=sys.stderr)
        sys.exit(1)
