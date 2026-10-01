# Tests

The test suites cover portable C++ text/view algorithms, native ABI, managed input/language/LSP behavior, Win2D rendering and interactive WinUI behavior.

- `CoreTests`, `NativeAbiTests`: CMake/CTest core correctness and ABI.
- `InputTests`, `LanguageTests`, `LspTests`, `AccessibilityTests`, `ManagedTests`: .NET functional suites.
- `RendererLogicTests`: pure minimap/layout logic, pointer cursor classification and method profiler aggregation/export.
- `RendererGoldenTests`: off-screen Win2D/DirectWrite rendering verification (Windows).
- `UiAutomationTests`: real Windows desktop/UI Automation input and profiler export smoke test.

Commands and supported environments: [`../docs/TESTING.md`](../docs/TESTING.md). UI tests require an unlocked Windows desktop; do not run in headless CI.
