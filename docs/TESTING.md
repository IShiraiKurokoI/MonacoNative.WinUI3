# Testing & profiling

## Portable suites

Run `scripts/test-all.sh` on a compatible Linux host, or `scripts/test-windows.ps1` on Windows. Renderer logic tests link in `EditorPointerCursorPolicy.cs` and `EditorMethodProfiler.cs`, exercising text/gutter/overview/minimap/overlay/outside pointer classification, profiler enable/disable, nested timing, managed allocations, reset and JSON/CSV export. Native CTest coverage includes the C++ model, view and ABI.

## Actual interactive UI test

Run in **Windows PowerShell 5.1** inside an **unlocked desktop session** (not Windows service, SSH session, or headless CI):

```powershell
./scripts/test-ui.ps1 -Configuration Release            # build then test
./scripts/test-ui.ps1 -Configuration Release -SkipBuild # use existing built SampleApp
./scripts/test-ui.ps1 -SkipBuild -OutputDirectory C:\Temp\MonacoUiEvidence
```

The test launches the real `MonacoNative.SampleApp.exe` and drives it with Microsoft UI Automation (`UIAutomationClient`) and OS mouse/keyboard events. Assertions include the UIA Document peer, sample button and text, hover I-beam/arrow/minimap states, direct typing, undo/redo, pointer-drag selection, profiler activation, JSON/CSV export and report contents. A failing assertion returns nonzero and writes the failure message. For cursor fidelity, verify native visible cursor glyph manually on your device as part of release validation; the automated test checks the editor's cursor state and OS input pathway, but not an image-diff of the displayed hardware pointer.

Artifacts:
- `artifacts/ui-tests/ui-test-report.json`: pass/fail, completed checks, process CPU time and peak working set.
- `artifacts/ui-tests/method-profiles/method-profile-*.json` and `.csv`: live method measurements.

UI automation is intentionally separate from `test-windows.ps1` to avoid accidental failures in CI runners without an interactive desktop. Set `MONACONATIVE_PROFILE_DIR` to override the JSON/CSV output folder of the SampleApp.

## Manual method profiling

Launch `MonacoNative.SampleApp`, enable `Profile: ON` in its toolbar, reproduce editing/scrolling/navigation or rendering symptoms, then select `Export`. Data goes by default to `%LOCALAPPDATA%\MonacoNative\Profiles`; the sample status strip shows the exported JSON path. Sort CSV by `TotalExclusiveMs`, examine `Calls`, `P95InclusiveMs` and `TotalAllocatedBytes` and compare to the uninstrumented baseline. A method appearing large in inclusive time may simply be a parent invoking other instrumented methods; use exclusive time to focus on its own code. These metrics are **not** native per-method CPU samples, GPU memory, or retained heap bytes.

## Renderer and core benchmarks

Keep running `scripts/run-benchmarks.ps1` and `scripts/run-renderer-benchmarks.ps1` for deterministic work counts and performance drift alongside opt-in runtime profiles. The existing benchmark reference JSON files remain under `reference/` and `artifacts/benchmarks/` to preserve reproducible regression checks.

## UI regression coverage added

`RendererLogicTests` now includes a linear-time fallback folding range test for nested braces, indentation, quoted literals and region markers. The desktop test additionally simulates a click on the line-6 namespace fold glyph, verifies collapse/expand through the accessibility status telemetry, invokes Select All via the actual context flyout, and switches dark → light → dark through the theme ComboBox. Color/pixel appearance still needs manual confirmation, as theme telemetry is a state check rather than a GPU swap-chain image-diff.

## Asset-browser / Find regression

`LanguageTests` includes tokenizer smoke cases for HTML, JavaScript, C# and C++. `RendererLogicTests` includes regex match options, selection boundaries, invalid regex, capture substitutions, and preserve-case behavior, independent of WinUI. The Windows interactive UI suite additionally changes the sample selector and checks the backing text and language ID via UIA; it expands Find to Replace while preserving a typed query, verifies sidebar feature toggles and independent menu Show/Enable options, and checks the regex control. These additional managed and UI tests **must be run on Windows with .NET 8 and Windows App SDK installed**. Portable C++ CTest does not compile WinUI controls or exercise their XAML event hookups.

### Command access coverage

Windows UI automation opens **View Options** and checks the `ToggleMenuFlyoutItem` state. It also configures `editor.action.addCommentLine` in the left command-policy combobox, asserts that `ContextMenuAndShortcut` exposes a real context menu item, and that `Disabled` hides it and prevents the registered `Ctrl+K Ctrl+C` chord from modifying the editor. `ShortcutOnly` remains the default for advanced non-toggle actions. Basic actions retain their right-click entries.

C# source under `Assets/Samples` is removed from MSBuild `Compile` items and remains a copied `Content` asset. This prevents a sample's `Main` method from introducing CS0017.

## Continuous integration test groups

The CI workflow reports independent steps by tested behavior, rather than by
historical development milestones. Native CTest cases are grouped by their
existing labels, so all cases remain covered while failures are easier to
identify:

| Group | CTest command | Scope |
| --- | --- | --- |
| Core | `ctest --test-dir build -L '^core$' --output-on-failure` | Text model, cursor, folding, Unicode, commands, parity fixtures |
| ABI | `ctest --test-dir build -L '^abi$' --output-on-failure` | Current native ABI 1.2.0, exported functions, lifetime and conversion contracts |
| Input | `ctest --test-dir build -L '^input$' --output-on-failure` | Text-store backend and Windows TSF |

Windows builds the native test binaries once and runs these groups in separate
CTest steps. Managed input/internationalization, accessibility, language, LSP,
renderer-cache logic, offscreen Win2D golden output, renderer budgets, managed
interop, and native performance each have their own CI steps. NuGet package
building, managed consumers, C++/WinRT consumers, and headless core runtime
smoke tests likewise report their outcomes independently.

Renderer benchmark outputs are resolved to an absolute path beneath
`artifacts/benchmarks/` before the renderer executable starts, since its
working directory is the benchmark project directory. The benchmark JSON uses
camelCase keys, including `suite` and `results`, matching the Python budget
checker. The release validation still requires the Windows renderer evidence
for an unconditional pass; non-Windows runs may report `CONDITIONAL` without
claiming a full release certification.
