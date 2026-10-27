# Testing

[Home](../README.md) · [Building](BUILDING.md) · [Language services](LANGUAGE_SERVICES.md)

## Windows test layers

The main automated entry point is `scripts/test.ps1`. Each layer has a defined
scope and an explicit result. `All` executes layers in dependency order; individual
layers assume that earlier build dependencies are already present. The report
is written to `artifacts/tests/layer-report.json` (or `-ReportPath`), with
`PASS`, `FAIL` and `NOT_RUN` for each layer. Unselected stages do not count as
passing. A failed layer exits nonzero and stops subsequent layers.

| Layer | Scope |
| --- | --- |
| `Build` | Native CMake output and MSBuild solution: Core.Native, WinRT and WinUI sample |
| `Core` | CTest core/ABI/input and large-document lifecycle stress, managed Core ABI and Core quick/full budgets |
| `Services` | Portable input, accessibility, LSP, renderer logic and strict native language integration |
| `WinUI` | Win2D reference images and renderer budgets; UI Automation with `-IncludeUIAutomation` on an unlocked desktop |
| `Integration` | Real MSBuild linked DLL negative/positive checks and parity release evidence |
| `Packages` | NuGet pack, x64/ARM64 consumers, standalone WinUI example and ARM64 PE/export cross-build validation |

```powershell
# Full Windows x64 build and automated tests, including package consumers:
./scripts/test.ps1 -Layer All -Configuration Release -RunPackages
# Run just the language/service tests after compiling dependencies:
./scripts/test.ps1 -Layer Services -Configuration Debug
# Desktop input and UIA checks require an unlocked interactive session:
./scripts/test.ps1 -Layer WinUI -Configuration Release -IncludeUIAutomation
```

`-RequireGoldenBaseline` rejects missing approved reference captures; the strict
release gate supplies that switch automatically. `scripts/test-windows.ps1`
remains a compatibility wrapper. `scripts/test-acceptance.ps1` wraps the layered runner
with the required release and manual-acceptance evidence checks. On Linux,
`scripts/test-all.sh` remains the portable Core/services entry point.

## Advanced language-service regressions

The `LanguageTests` executable includes `AdvancedLanguageTests`: Roslyn rename,
organize imports, semantic tokens and inlay hints; libclang overload signatures and
active-file rename; HTML embedded semantic ranges, attribute repairs and closing
tags; and JSON conditional Schema diagnostics and safe code actions. Run the
Services layer on Windows with the installed .NET 10 SDK:

```powershell
./scripts/test.ps1 -Layer Services -Configuration Release
```

The native/libclang integration case requires both `MONACONATIVE_NATIVE_LIBRARY`
and `MONACONATIVE_LIBCLANG` to be set to the actual deployed binaries. The test
reports `SKIP` when those dependencies are absent; a skipped case is not evidence
that the packaged native provider works. Cross-platform CMake/CTest covers the
native core but does not compile the managed Roslyn/HTML/JSON providers.

## Windows acceptance and release evidence (Windows)

Run from the repository root in PowerShell:

```powershell
# Noninteractive Windows CI/repro: full native/managed/WinUI/headless renderer suite.
./scripts/test-acceptance.ps1 -Mode Automated -Configuration Release -RunPackages -PackageVersion 0.1.0-ci

# On an unlocked desktop with installed IMEs: UI Automation and an unapproved
# template for manual acceptance; this does NOT mark any manual case as PASS.
./scripts/test-acceptance.ps1 -Mode Interactive -Configuration Release -SkipUiBuild -PackageVersion 0.1.0

# Strict release: full automation + approved Windows baseline + same-version
# manual acceptance. No missing evidence is permitted.
./scripts/test-acceptance.ps1 -Mode Release -Configuration Release -PackageVersion 0.1.0
```

The legacy `run-release-validation.ps1` name is now a strict forwarding alias,
not a benchmark-only release shortcut. Linux has
`scripts/run-parity-benchmarks.sh`, whose report is always only conditional if
Windows evidence is unavailable. The runner reuses existing granular scripts and emits
`artifacts/release/acceptance-report.json` with independent stage outcomes. Normal
`Automated` and `Interactive` results are **CONDITIONAL** even if their invoked
tests pass; only `Release` can produce `PASS`/`releaseEligible=true`.
A failing invoked stage produces a failing report and nonzero exit. Every portable
source/tool check is also available via `python scripts/check-source.py`
(14 static/source guards plus dedicated evidence-tooling unit tests).

Windows CI calls `Automated -RunPackages`, uploads CTest, PNG, benchmark and
package logs. Release CI calls the **same** script in `Release` mode, which
refuses missing, stale or incomplete hand-verified evidence. Read
[Windows acceptance README](../reference/windows-acceptance/README.md) for the
manual matrix and [renderer golden README](../reference/render-goldens/README.md)
for approved snapshot capture. No manual PASS is synthesized by test code.

The raw Win2D snapshot confirms fonts/DirectWrite primitives, not the full
MonacoEditor drawing tree. Real visual theme, DPI, caret, IME candidate and
screen-reader behavior are desktop acceptance responsibilities.

### Native MSBuild integration (positive and negative)

The acceptance runner invokes `scripts/test-native-build-validation.ps1` *after*
`build-windows.ps1`. This calls the actual shipping MSBuild task against a
real linked Core.Native DLL and asserts that a valid x64 DLL succeeds while
a wrong machine, nonexistent export or nonexistent file fails. It also uses
real built/deployed Core.Native and libclang DLLs to exercise the shipping
`AfterTargets=Publish` validator via a fixture `Publish` with **no Build step**;
wrong architecture, missing deployment and unsupported single-file cases must
fail. The existing synthetic-PE tests still exercise edge-case parsing. After the package build, a second group checks the real linked ARM64 core
against the shipping validator with the positive/negative PE and export cases.
It does not execute the ARM64 DLL or an ARM64 publish fixture. This x64
fixture is not a full WinUI publish run; successful export inspection is not
proof of runtime DLL loading or semantic correctness.

## GitHub Actions: native ARM64, isolated packages and device testing

The standard [CI workflow](../.github/workflows/ci.yml) includes native ARM64
Windows jobs on `windows-11-vs2026-arm`. `scripts/test-native-arm64.ps1`
rejects an x64/emulated process, builds and runs ARM64 CTest, verifies the
linked ARM64 binary with the shipping MSBuild task, executes managed native ABI
checks, and runs required native/libclang language integration. Its JSON report
is `artifacts/tests/arm64-native.json`. Running on a native ARM64 hosted runner
is processor-level evidence, not an OEM ARM64 laptop or interactive WinUI test.

On x64 and ARM64, `scripts/test-package-isolated.ps1` starts an independent
Core consumer outside the repository with a **new empty NuGet package cache**.
It restores the versioned candidate package, builds the consumer, loads the
packaged native DLL, verifies the ABI, and performs an edit. Missing assets or
a wrong process architecture fail the test. Reports and process logs are
uploaded as CI artifacts. Existing package tests separately compile the WinUI
and C++/WinRT consumers. A temporary consumer and clean NuGet cache are not an
OS installation/upgrade/uninstallation test.

The [Desktop Device Validation workflow](../.github/workflows/device-validation.yml)
can be dispatched for 100%, 125%, 150%, or 200% scaling. It requires an
**unlocked, logged-in Windows x64 self-hosted runner** with the labels
`monaco-desktop` and `dpi-100`, `dpi-125`, `dpi-150`, or `dpi-200` as appropriate.
The DPI labels describe a real display configuration maintained on the runner;
GitHub Actions does not silently change monitor scaling or fabricate another
monitor. Configure the device with Visual Studio C++/Windows SDK build tools,
.NET 10, the Windows App Runtime, a GPU driver and a fixed display arrangement.
Run the self-hosted Actions agent inside the interactive user session rather
than as a Windows service, with the session kept unlocked throughout the job.
A matching device label must be registered before dispatch; otherwise the job
remains queued. The UIA test checks the window DPI, TextPattern bounds and
`RangeFromPoint` round-trip, then performs editor interactions and repeated
window shutdown. The `Measure-DesktopTelemetry.ps1` step records P50/P95/P99
**SendKeys-to-UIA-observed-text time**, peak working set and GPU process-memory
counter samples (when exposed by the driver). This is not key-to-present latency,
frame rendering time or retained GPU memory after control disposal. GPU counters
can be required for this device job; a missing counter then fails rather than
being reported as a zero-byte measurement.

The device workflow is manual-only because standard hosted VMs do not provide
a controlled, unlocked multi-monitor/Narrator/GPU environment. Real Narrator
highlight appearance, IME candidate rendering, multiple physical monitors,
GPU leak diagnosis and clean-machine Windows deployment remain manual or
specialized-device acceptance. These are **not** turned into `PASS` by CI.

### UI Automation coordinate regression

UIA `RangeFromPoint` receives physical screen pixels. The editor maps these
through `XamlRoot.CoordinateConverter.ConvertScreenToLocal` and the
`SurfaceRoot` visual transform. Visible text rectangles transform in the
opposite direction via `ConvertLocalToScreen`, using the same `SurfaceRoot`
origin. The UIA test checks that a visible identifier rectangle is inside the
editor and that its center maps back to the same word. Repeat this at multiple
DPI settings and on secondary monitors to detect Narrator selection-frame
misalignment. This verifies provider geometry; a Narrator overlay itself still
needs a visual check on a desktop device.

## Viewport and shortcut localization regression

`RendererLogicTests` checks horizontal extent sizing independently of WinUI:
the scrollable content width matches the actual ScrollViewer viewport when
text fits, but grows for a long unwrapped line. `ManagedTests` checks the
SampleApp shortcut headings and profile labels for all supported localizations.
`check-demo-ui.py` checks that the editor owns extent sizing and that the
SampleApp preserves the active shortcut scheme when rebuilding localized items.

When interactive UI automation is enabled, `scripts/test-ui.ps1` also opens and
normally closes the SampleApp three additional times, including an in-flight
sample load, writing `artifacts/ui-tests/window-lifecycle-report.json`. Use
`-WindowCycles 0` to omit this optional stress loop. This checks cross-process
startup/shutdown stability; it does not measure memory retained by multiple
editors inside one process.

Windows desktop acceptance includes short-line/no-overflow and long-line/real-
overflow scenarios with word wrap, vertical scrollbars, minimap, DPI and window
resizing. Portable tests cannot substitute for these visual/interactive checks.

## Editing interaction and lifecycle checks

The portable `LanguageTests` project includes regressions for explicit completion
commit characters, snippet choice parsing and linked placeholders, and retaining a
manually selected signature across retriggered help. These cover language and
session logic; they do not exercise WinUI focus handling or native TSF composition.

The editor provides a choice popup for snippet placeholders. Use **Up/Down** or
**Home/End** to change the highlighted option, **Enter** or a pointer click to
apply it, **Tab** to apply it and advance to the next stop, or **Escape** to
close the list. Parameter hints have clickable **previous/next overload** arrows and support
**Alt+Down** / **Alt+Up** while the popup is visible. The current argument
position, parameter count and simple literal types help choose an overload
when providers return a default index. The C# Roslyn provider also resolves
local-variable and expression types when selecting the active overload; its
managed regression tests cover both variables and numeric literals. User
navigation is preserved while editing the same argument. Completion commit
characters are respected
only when the item explicitly declares them and the input is delivered through
the direct character path; active TSF composition remains under TSF control.

The Windows acceptance matrix includes the combined completion/punctuation/undo,
snippet choices, overload navigation, and rapid document-switch cases. The IME
matrix separately checks punctuation during Pinyin composition. Run these on an
unlocked desktop; source-level regression checks cannot establish their runtime
behavior.

The `full` native core benchmark already exercises 100,000 document lines; it
measures model/view operations rather than WinUI frame rate, input P95, GPU
resource retention, or repeated control creation. Those require independent
Windows instrumentation and results from the target machine.

## Test groups

Source checks, C++ tests, managed tests, native bridge tests, Windows rendering tests, and interactive tests run as separate groups. Linux core tests do not cover Windows NuGet restore, DLL loading, XAML, or IME behavior.

Optional native dependencies can produce an explicit `SKIP` in unit-test mode. Required integration jobs must fail instead of silently skipping. A configured acceptance entry point is not evidence that it has run successfully.

## Build-time native inspection

Windows MSBuild builds inspect Core.Native after linking and the sample's deployed Core.Native/libclang after copying. Publish inspects its own output. The task checks PE structure, target architecture, and the required export names; see [building](BUILDING.md) for the full scope.

`NativeBinaryValidationTests.cs` compiles the **same inspector source** into the existing LanguageTests project. Synthetic PE fixtures cover x64/ARM64, malformed headers and exports, target conflicts, missing files, root-versus-RID lookup precedence, and file-handle release. These are project-owned tests, not an upstream suite, and do not execute Windows DLLs. They run with the managed command below on hosts with a .NET SDK.

The existing `check-packaging-platforms.py` checks target wiring, inclusion of the task in packages, and equality of required-export lists with the ABI manifest and Clang bridge source. This is a structural regression guard, not an MSBuild execution or a C# compiler.

Build-time inspection never replaces `mn_abi_version` / layout validation, Windows loader checks, or semantic integration tests. A cross-built ARM64 DLL can pass structural inspection without having been executed. CMake does not invoke these MSBuild targets, and Visual Studio may skip an up-to-date build; **Rebuild** forces the checks after manual output changes.

## C++ core and translated fixtures

```bash
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release -DMONACONATIVE_WARNINGS_AS_ERRORS=ON
cmake --build build --parallel
ctest --test-dir build --output-on-failure
```

| CTest label | Scope |
| --- | --- |
| `core` | Text model, cursors, folding, Unicode, commands, and frozen compatibility fixtures |
| `abi` | ABI 1.4.0, exports, lifetime, language extensions, and coordinate conversion |
| `input` | Portable text-store backends, plus TSF on Windows |

Use `-L '^core$'`, `'^abi$'`, or `'^input$'` for a group. NativeAbi retains individual cases such as `--case typescript_upstream`, `typescript_scanner_api`, `typescript_preprocess`, and `typescript_basic`. The translated C++ tests need neither Node nor Python. [Language services](LANGUAGE_SERVICES.md) distinguishes translated upstream assertions, differential fixtures, and project tests.

## Managed unit tests and required native integration

For managed unit tests:

```powershell
dotnet run --project tests/LanguageTests/MonacoNative.Language.Tests.csproj -c Release
dotnet run --project tests/LspTests/MonacoNative.Lsp.Tests.csproj -c Release
```

Without `MONACONATIVE_NATIVE_LIBRARY`, native-bridge tests report `SKIP`. Without `MONACONATIVE_LIBCLANG`, managed libclang scenarios are skipped. Such a run must not be described as all language bridges passing. Other managed projects are listed in `scripts/test-portable-managed.ps1`; `scripts/test-all.sh` is the Linux entry point.

Required native bridge example on Linux:

```bash
MONACONATIVE_NATIVE_LIBRARY="$PWD/build/src/MonacoNative.Core.Native/libMonacoNative.Core.Native.so" \
  dotnet run --project tests/LanguageTests/MonacoNative.Language.Tests.csproj -c Release -- --require-native
```

`--require-native` requires an existing Core.Native path. `--require-libclang` requires both Core.Native and libclang. Missing/empty paths, nonexistent files, and unknown arguments produce nonzero configuration failures, not optional-test mode. Existence is only a preflight; subsequent tests perform actual loading and semantic assertions.

Strict Windows entry point for the pinned NuGet runtime:

```powershell
./scripts/test-core.ps1 -Configuration Release -BuildOnly
./scripts/test-language-integration.ps1 -Configuration Release -Platform x64
```

This cleans the dedicated test output, builds LanguageTests for `win-x64` / `win-arm64`, checks the actual NuGet-deployed `libclang.dll`, and executes with both `--require-*` flags. It does not fall back to global LLVM. `-NativeLibrary` supplies a matching Core.Native build. Environment variables are restored afterward; cleanup is limited to `artifacts/language-tests/<rid>/<Configuration>`.

ARM64 execution requires a matching process/host and ARM64 Core.Native. An x64 CI result is not an ARM64 runtime result. This entry point validates LanguageTests, not every sample publish layout, package consumer, or WinUI interaction.

`test-windows.ps1` delegates to the layered runner. Windows CI uses the acceptance wrapper, which writes both a release-level report and a per-layer report. Linux GCC CI requires Core.Native; other unit-only jobs can still explicitly skip optional native dependencies.

## Interactive Windows tests

Run in **Windows PowerShell 5.1 with an unlocked desktop session**, not a service, SSH session, or ordinary headless CI:

```powershell
./scripts/test-ui.ps1 -Configuration Release
./scripts/test-ui.ps1 -Configuration Release -SkipBuild
./scripts/test-ui.ps1 -SkipBuild -OutputDirectory C:\Temp\MonacoUiEvidence
```

The script starts the actual sample and uses UI Automation plus OS mouse/keyboard events. It checks Document peers, text, selection, input, undo/redo, dragging, pointer state, themes, folding, sample switching, find/replace, command policy, and profiler export. Undo/redo compares real UIA TextPattern caret positions as well as text.

Existing native/ABI/input/managed suites cover multiple cursors, reversed selections, transactions, and TSF replacement caret snapshots. Desktop cases also check namespace-fold clicks, context-menu select-all, and theme changes. Theme state and pointer telemetry are not pixel diffs: cursor shape, fonts, DPI, and palette appearance still need inspection.

Find tests cover regex options, invalid expressions, capture replacement, preserve-case behavior, and selection bounds. UI tests check that expanding replacement preserves the query, sidebar controls, and sample backing text. Command-policy tests check actual menu/shortcut behavior for `ContextMenuAndShortcut`, `ShortcutOnly`, `Disabled`, and `Ctrl+K Ctrl+C`.

Language interactions still require acceptance coverage: `Console.` / C++ members, arrow keys and Enter, snippet Tab / Shift+Tab, parameter hints, IME composition, undo/redo, and theme/DPI combinations. Unexecuted checks must not be marked passed.

Reports go to `artifacts/ui-tests/ui-test-report.json` and JSON/CSV files under `artifacts/ui-tests/method-profiles/`. Failures return nonzero and record errors. Interactive tests run separately from `test-windows.ps1`.

## Profiling and benchmarks

The sample's `Profile: ON` is opt-in. Reproduce an action, then export to `%LOCALAPPDATA%\MonacoNative\Profiles`, or override with `MONACONATIVE_PROFILE_DIR`. Compare `TotalExclusiveMs`, `Calls`, `P95InclusiveMs`, and `TotalAllocatedBytes`, including an uninstrumented baseline.

These are UI-thread wall times and managed allocations for synchronous instrumented methods, not CPU samples, GPU memory, retained heap, or per-method native-core data. Process CPU and peak working set are separate UI-report metrics.

`RendererLogicTests` independently checks pointer geometry, cache invalidation, profiler aggregation/nesting/allocation/reset/export, fallback folding, and find behavior without actual WinUI drawing.

Use `scripts/run-benchmarks.ps1` / `.sh` and `scripts/run-renderer-benchmarks.ps1`; budgets are in `reference/`. Renderer output paths are resolved absolutely. JSON uses camelCase consistently with budget checks. Wall time is a trend; deterministic work counts are gates. Win2D goldens, budgets, package consumers and TSF remain logically distinct stages inside the single Windows desktop CI run. Non-Windows parity may be `CONDITIONAL`, not a full pass with Windows renderer evidence.

## Validation limits for this revision

Portable Python tooling and C++ CTest can be executed without Windows. Any
results described in the delivery notes apply only to those actual runs. The
.NET SDK, Windows PowerShell, MSBuild, Win2D and unlocked Windows desktop are
unavailable on Linux; CI configuration and test source do **not** prove Windows
success. Release stays blocked until real Windows golden and human acceptance
evidence are checked in, then the strict Windows gate actually passes.

## Closing a window with parameter hints visible

The sample status handler uses `MonacoEditor.TryGetDocumentStatus` and
`TryGetLanguageStatus`, which return `false` once editor shutdown starts.
The editor caches character counts by document version and owns all native-model
reads. Host UI code only renders the returned snapshot; it must not read
`MonacoTextModel` after a window-close event.
UI event dispatch and the parameter-help provider may complete during shutdown;
no sample status refresh is allowed after the editor begins disposing.
The Windows interaction matrix includes closing the window with parameter
hints visible and during an outstanding signature-help request. The normal
interactive UIA suite also verifies that the sample closes cleanly. A source
check or an ordinary close test does not replace the open-popup manual case.

## C++ formatting

C++ document/range formatting uses the official LLVM `clang-format` executable
instead of the shared brace-indentation fallback. The formatter is an optional
local tool; its absence results in no edits, not an attempt to reformat C++ using
heuristics. See [C++ language services](LANGUAGE_SERVICES.md) for configuration.
