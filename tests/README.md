# Tests

The test suites cover portable C++ text/view algorithms, native ABI, managed input/language/LSP behavior, Win2D rendering and interactive WinUI behavior.

- `CoreTests`, `NativeAbiTests`: CMake/CTest core correctness and ABI.
- `InputTests`, `LanguageTests`, `LspTests`, `AccessibilityTests`, `ManagedTests`: .NET functional suites.
- `RendererLogicTests`: pure minimap/layout logic, pointer cursor classification and method profiler aggregation/export.
- `RendererGoldenTests`: off-screen Win2D/DirectWrite rendering verification (Windows).
- `UiAutomationTests`: real Windows desktop/UI Automation input and profiler export smoke test.

`LanguageTests/AdvancedLanguageTests.cs` covers the additional Roslyn, libclang,
HTML and JSON Schema providers. The libclang case is optional outside strict
integration; production acceptance must run it against packaged native DLLs.

For Windows, `scripts/test.ps1 -Layer All -RunPackages` runs the layers in dependency order.
Each layer can also be selected individually: `Build`, `Core`, `Services`,
`WinUI`, `Integration`, and `Packages`. A detailed JSON report records stages
which have not run.

Commands and supported environments: [`../doc/TESTING.md`](../doc/TESTING.md). UI tests require an unlocked Windows desktop; do not run in headless CI.

## Hosted ARM64 and desktop device checks

`../scripts/test-native-arm64.ps1` runs ARM64 CTest, managed ABI,
libclang-language integration and linked DLL validation in a native ARM64
Windows process. `../scripts/test-package-isolated.ps1` runs the packaged
Core consumer outside the repository with an empty NuGet cache. The main CI
workflow executes these checks on hosted Windows x64/ARM64 runners.

`UiAutomationTests/Run-EditorUi.ps1` includes UIA physical-pixel coordinate
round-trip assertions. `UiAutomationTests/Measure-DesktopTelemetry.ps1`
records real-keyboard-to-UIA observation times, working set, and optional GPU
memory counters. These device checks require an unlocked interactive Windows
session; see [Testing](../doc/TESTING.md) for runner setup and limitations.
