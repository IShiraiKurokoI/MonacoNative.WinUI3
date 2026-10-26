# Testing

[Home](../README.md) · [Building](BUILDING.md) · [Language services](LANGUAGE_SERVICES.md)

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

`test-windows.ps1` builds native dependencies before managed groups and required language integration. Windows CI follows that order. Linux GCC CI requires Core.Native; other unit-only jobs can still explicitly skip optional native dependencies.

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

Use `scripts/run-benchmarks.ps1` / `.sh` and `scripts/run-renderer-benchmarks.ps1`; budgets are in `reference/`. Renderer output paths are resolved absolutely. JSON uses camelCase consistently with budget checks. Wall time is a trend; deterministic work counts are gates. Win2D goldens, budgets, package consumers, and TSF are separate CI steps. Non-Windows parity may be `CONDITIONAL`, not a full pass with Windows renderer evidence.

## Validation limits for this revision

This revision translates project documentation and adds build/publish native inspection; it does not add language semantics. The accompanying delivery report records the checks actually executed. The editing environment has no .NET SDK, PowerShell, MSBuild, or Windows desktop, so the new C# inspector tests, MSBuild task compilation, and Windows execution remain unverified here. Historical counts describe coverage, not execution evidence for this revision.
