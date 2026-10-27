"""Portable guards for the separate hosted and physical-device CI stages."""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8")


class DeviceCiChecks(unittest.TestCase):
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
