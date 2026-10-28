"""Portable guards for the separate hosted and physical-device CI stages."""
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8")


class DeviceCiChecks(unittest.TestCase):
    def test_native_msbuild_validation_fixture_is_well_formed(self):
        ET.parse(ROOT / 'tests/NativeValidationMsbuild/Validation.proj')

    def test_publish_validation_fixture_selects_matching_pe_machine(self):
        script = read('scripts/test-native-build-validation.ps1')
        self.assertIn('Get-PeMachine', script)
        self.assertIn('0xAA64', script)
        self.assertIn('0x8664', script)
        self.assertIn('$matchingClang', script)
        self.assertIn('Copy-Item -LiteralPath $selectedClang.FullName', script)
        self.assertNotIn('$clangMatches[0]', script)

    def test_arm64_is_native_execution_not_cross_build(self):
        ci = read('.github/workflows/ci.yml')
        script = read('scripts/test-native-arm64.ps1')
        self.assertIn('runs-on: windows-11-vs2026-arm', ci)
        self.assertIn('test-native-arm64.ps1', ci)
        self.assertIn('test-package-isolated.ps1 -Platform ARM64', ci)
        for required in ('ProcessArchitecture', "-A ARM64", 'ctest --test-dir',
                         'Managed.Tests.csproj', 'test-language-integration.ps1',
                         'test-native-build-validation.ps1'):
            self.assertIn(required, script)

    def test_nuget_consumer_is_outside_repository_with_empty_cache(self):
        script = read('scripts/test-package-isolated.ps1')
        for required in ('RUNNER_TEMP', 'NewGuid()', 'NUGET_PACKAGES',
                         'NUGET_HTTP_CACHE_PATH', 'CoreConsumer.csproj',
                         'CoreConsumer.exe', 'MonacoNative.Core.Native.dll',
                         "nativeRuntimeExecuted = $status -eq 'PASS'"):
            self.assertIn(required, script)
        self.assertIn('test-package-isolated.ps1 -Platform x64', read('.github/workflows/ci.yml'))

    def test_isolated_consumer_keeps_nuget_cache_out_of_xaml_globs(self):
        script = read('scripts/test-package-isolated.ps1')
        # SDK/WinUI's default XAML glob starts at the project directory.
        # NuGet cache includes MSBuild .xaml rules that are not application pages.
        self.assertIn("$projectDir = Join-Path $work 'consumer'", script)
        self.assertIn("$env:NUGET_PACKAGES = Join-Path $work 'packages'", script)
        self.assertIn("$env:NUGET_HTTP_CACHE_PATH = Join-Path $work 'http-cache'", script)
        self.assertIn("$env:DOTNET_CLI_HOME = Join-Path $work 'cli-home'", script)
        self.assertIn('New-Item -ItemType Directory -Force -Path $projectDir', script)
        self.assertIn("$project = Join-Path $projectDir 'CoreConsumer.csproj'", script)
        self.assertIn("Copy-Item -LiteralPath (Join-Path $source 'CoreConsumer.csproj') -Destination $projectDir", script)
        self.assertIn("Copy-Item -LiteralPath (Join-Path $source 'Program.cs') -Destination $projectDir", script)
        self.assertIn('$output = Join-Path $projectDir "bin/$Platform/$Configuration/', script)
        self.assertNotIn("$project = Join-Path $work 'CoreConsumer.csproj'", script)
        self.assertNotIn("Copy-Item -LiteralPath (Join-Path $source 'Program.cs') -Destination $work", script)

    def test_screen_pixel_mapping_has_one_coordinate_converter(self):
        source = read('src/MonacoNative.Renderer.WinUI3/MonacoEditor.Accessibility.cs')
        self.assertIn('CoordinateConverter.ConvertScreenToLocal', source)
        self.assertIn('XamlRoot.Content.TransformToVisual(SurfaceRoot)', source)
        self.assertIn('SurfaceRoot.TransformToVisual(XamlRoot.Content)', source)
        self.assertIn('CoordinateConverter.ConvertLocalToScreen(rootRect)', source)
        self.assertNotIn('(screenPoint.X - control.X) / scale', source)
        ui = read('tests/UiAutomationTests/Run-EditorUi.ps1')
        self.assertIn('RangeFromPoint', ui)
        self.assertIn('UIA physical-pixel range geometry round-trips', ui)

    def test_device_tests_dont_claim_manual_narrator_or_gpu_present(self):
        workflow = read('.github/workflows/device-validation.yml')
        self.assertIn('workflow_dispatch:', workflow)
        self.assertIn('self-hosted', workflow)
        self.assertIn('dpi-${{ inputs.dpi }}', workflow)
        self.assertIn('MONACONATIVE_EXPECTED_SCALE_PERCENT', workflow)
        telemetry = read('tests/UiAutomationTests/Measure-DesktopTelemetry.ps1')
        self.assertIn("measurement = 'sendkeys-to-uia-observed-text'", telemetry)
        self.assertIn('measuresFramePresentation = $false', telemetry)
        self.assertIn('RequireGpuCounters', telemetry)
        self.assertIn('NOT_AVAILABLE', telemetry)


if __name__ == '__main__':
    unittest.main()
