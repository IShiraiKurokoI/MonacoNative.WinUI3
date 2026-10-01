# Building

## Windows prerequisites

Windows 10 (19041+) or 11, Visual Studio with MSVC/C++ Desktop workload, Windows SDK 10.0.26100.0, .NET 8 SDK, Windows App SDK 2.4 and Win2D 1.4 NuGet restore. For the C++/WinRT distribution project, the existing project requires an appropriate up-to-date Visual Studio C++/WinRT toolchain. The sample is an unpackaged WinUI 3 desktop app.

```powershell
# Preferred Visual Studio/MSBuild solution build including the native core DLL.
./scripts/build-windows.ps1 -Configuration Release -Platform x64
# Full non-UI test suite, builds sample as well.
./scripts/test-windows.ps1 -Configuration Release
# Real mouse + keyboard + UIA integration suite; session must be unlocked.
./scripts/test-ui.ps1 -Configuration Release -SkipBuild
```

Open `MonacoNative.sln` and choose the sample as the startup project to debug. Build and package x64/ARM64 distributions with the existing `scripts/pack-windows.ps1`; see parameters in that script. The portable core uses CMake / CTest / Ninja. Linux can build the portable C++ core and run CTest, but cannot build WinUI 3, MSVC, XAML, or TSF sources.

Avoid treating a successful portable test run as evidence of a successful WinUI Windows build: the latter needs actual MSBuild/XAML/Win2D compilation on Windows.
