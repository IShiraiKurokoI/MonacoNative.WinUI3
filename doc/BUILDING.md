# Building

[Home](../README.md) · [Design](TECHNICAL_NOTES.md) · [Testing](TESTING.md)

## Windows development environment

The checked-in projects target Windows 10 build 19041 or later / Windows 11, on x64 and ARM64. They use Windows SDK **10.0.26100.0**, .NET **8**, Windows App SDK **2.4.0**, and Win2D **1.4.0**. The C++/WinRT project references **3.0.260818.1** and requires the corresponding Visual Studio / MSVC / C++/WinRT tools. These are repository configuration values, not floating references to the latest releases.

Install the C++ and .NET desktop development workloads and required SDKs. Open `MonacoNative.sln`, select `Debug | x64`, restore packages, and choose `MonacoNative.SampleApp` as the startup project. The sample is an unpackaged WinUI 3 desktop application.

```powershell
# Build the native core, managed projection, WinRT component, and WinUI sample.
./scripts/build-windows.ps1 -Configuration Release -Platform x64
# Non-interactive tests, including required native language integration.
./scripts/test-windows.ps1 -Configuration Release
# Requires an unlocked interactive desktop; not a headless CI substitute.
./scripts/test-ui.ps1 -Configuration Release -SkipBuild
```

Normal Visual Studio builds do not require Python, Ninja, or an upstream source checkout. Maintenance and test scripts have their own prerequisites; see [testing](TESTING.md).

## Language dependencies

C# unconditionally references the official `Microsoft.CodeAnalysis.CSharp.Features [4.11.0]` package. C++ references `libclang.runtime.win-x64` and `libclang.runtime.win-arm64 [17.0.4]`. Built-in services execute in process; they do not need Node, tsserver, Roslyn Language Server, or clangd.

The source archive contains code and notices, not the large NuGet binaries. The first build needs a package source or local cache. libclang is not a complete C++ toolchain: matching resource headers, system / standard-library includes, and project macros still require configuration. See [C++ language services](LANGUAGE_SERVICES.md).

`reference/upstream-sources.lock.json` and `scripts/upstream.py` remain maintenance references, outside the default build. The old Roslyn source-mode configuration is not imported. `enable-roslyn-source` is disabled; the disable command removes recognized legacy generated configuration.

## What normal builds check

Compilation, deployment inspection, and runtime tests are separate. A read-only MSBuild task validates native deployment after build and publish.

| Build path | Checks performed |
| --- | --- |
| `MonacoNative.Core.Native.vcxproj` | After a Windows build, inspect the generated DLL: PE structure, target architecture, and all **93 core plus 6 language ABI exports**. |
| Sample build / executable WinUI package consumer | After output copying, inspect deployed Core.Native and libclang; require all **43 libclang symbols** used by the bridge. |
| Executable consumer of the C++ language package | Inspect deployed libclang after build and publish. This package does not itself supply Core.Native. |
| Publish, including `--no-build` | Inspect the actual publish directory, not just the previous build output. The sample explicitly includes Core.Native in its publish file list. |
| Strict Windows language test build | The explicitly selected Windows RID enables the same libclang deployment check before test execution. |

The task uses MSBuild's `RoslynCodeTaskFactory`; it does not launch Python, PowerShell, a language server, or the DLL being inspected. Its source is shipped with the build targets. Export lists are checked against the ABI manifest and bridge source by the existing packaging regression script.

A missing DLL, invalid/truncated PE data, wrong machine type, conflicting Platform / PlatformTarget / RID, or missing required export fails the build with **`MNATIVE001`**. The root output DLL takes precedence over `runtimes/win-<architecture>/native/`; a wrong root DLL is not hidden by a valid fallback. No global LLVM installation or PATH search is used.

The architecture comes from the target configuration, not the build host. Inspecting an ARM64 DLL during an x64 build does not execute ARM64 code. Design-time builds are excluded. Shared AnyCPU library builds are not forced to deploy application dependencies. Visual Studio's up-to-date check can skip MSBuild entirely; use **Rebuild** to force inspection after manually changing output files.

A successful message looks like:

```text
Native dependency check PASS: .../libclang.dll (x64; PE and 43 required exports; not a runtime test)
```

These checks do **not** call `mn_abi_version`, validate returned structure sizes, load transitive DLL dependencies, verify binary signatures or hashes, or test C++ semantics. NuGet version ranges pin the packages; export inspection does not attest the version of a manually substituted DLL. Runtime ABI compatibility is still checked by `MonacoNativeAbi.EnsureCompatible()`, and integration tests exercise the real bridge.

The current deployment path expects loose native DLLs. Single-file publishing is explicitly rejected with `MNATIVE002`, rather than being reported as verified. Static checks do not validate resource-header versions, system SDK completeness, or STL compatibility. An explicit `MonacoClangResourceDirectory` is checked for `include/stddef.h` and copied using the existing layout.

Implementation references: [MSBuild inline tasks](https://learn.microsoft.com/en-us/visualstudio/msbuild/msbuild-roslyncodetaskfactory), [native NuGet assets](https://learn.microsoft.com/en-us/nuget/create-packages/native-files-in-net-packages), and [PE format](https://learn.microsoft.com/en-us/windows/win32/debug/pe-format).

## Portable core

```bash
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release -DMONACONATIVE_WARNINGS_AS_ERRORS=ON
cmake --build build --parallel
ctest --test-dir build --output-on-failure
```

Linux / macOS can build the portable C++ core. This does not verify WinUI, MSVC, XAML, or Windows TSF. `-DMONACONATIVE_ENABLE_SANITIZERS=ON` enables ASan / UBSan on supported GCC / Clang toolchains. The CMake build and CTest remain separate operations; MSBuild's PE deployment targets do not run in the CMake build.

## Packaging and architecture

Use `scripts/pack-windows.ps1` for x64 / ARM64 distribution builds. The aggregate packages carry the README, technical documents, third-party notices, and local license texts. Retain notices from the actual dependency packages as well.

Choose the consuming process architecture, not the developer machine architecture. `MonacoNativeAbi.EnsureCompatible()` requires an exact managed/native ABI match. Do not mix DLLs from different builds. Strict language integration runs only in a matching Windows process; cross-building is not an execution result.
