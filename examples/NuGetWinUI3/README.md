# WinUI 3 NuGet consumer

This is a standalone unpackaged WinUI 3 application that uses `MonacoNative.WinUI3`
exclusively through NuGet. It has no project reference to the MonacoNative source tree.
The application shows one editor and releases it when the window closes.

From the repository root, build the packages first:

```powershell
./scripts/pack-windows.ps1 -Configuration Release -Version 0.1.0

dotnet restore examples/NuGetWinUI3/MonacoNative.NuGetExample.csproj `
    --configfile tests/PackageConsumerSmoke/NuGet.Config `
    -p:Platform=x64 -p:MonacoNativePackageVersion=0.1.0

dotnet build examples/NuGetWinUI3/MonacoNative.NuGetExample.csproj `
    -c Release -p:Platform=x64 -p:MonacoNativePackageVersion=0.1.0 --no-restore
```

Use `ARM64` instead of `x64` on an ARM64 Windows computer. Cross-compiling is
supported, but must not be counted as device validation. Once the package is
published to a feed, replace the local `NuGet.Config` and package version with
your own normal NuGet configuration. See [Packaging](../../doc/PACKAGING.md) for
runtime assets, public API and upgrade considerations.
