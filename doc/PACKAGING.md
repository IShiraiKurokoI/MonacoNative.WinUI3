# NuGet consumption and compatibility

[Home](../README.md) · [Building](BUILDING.md) · [Testing](TESTING.md)

## Supported configuration

The WinUI package targets `net10.0-windows10.0.26100.0` and carries x64 and
ARM64 native assets. It is intended for unpackaged WinUI 3 desktop applications
running on Windows 10 build 19041 or later. The current package dependencies
include Windows App SDK 2.4.0, Win2D 1.4.0, Roslyn 4.11.0 and libclang 17.0.4.
The .NET target was upgraded separately from the Windows App SDK/Win2D version
pins to isolate compatibility changes. The new target still needs a verified
Windows restore, build, runtime and package-consumer run before release.

The native ABI remains separately versioned and validated by
`MonacoNativeAbi.EnsureCompatible()`. A package version is not an ABI guarantee:
keep the managed DLLs and both native binaries from the same package build.

## Minimal application

[`examples/NuGetWinUI3`](../examples/NuGetWinUI3/README.md) contains an
independent unpackaged WinUI application. Its only MonacoNative dependency is
`<PackageReference Include="MonacoNative.WinUI3" ... />`; no project references
or build-time files are taken from the source tree. See its README for local
package restore and build commands. The package smoke fixtures are smaller
and run in the automated `Packages` test layer.

In XAML, declare `xmlns:mn="using:MonacoNative.Renderer.WinUI3"` and add
`<mn:MonacoEditor x:Name="Editor" />`. In code-behind, use `Editor.Text`
to set the document content. Dispose the editor when the hosting window closes
so the native model, TSF connection and rendering resources are released.

## Runtime files and deployment

The package places managed assemblies, XAML resources (`.pri` and `.xbf`),
`MonacoNative.Core.Native.dll`, and language runtime dependencies in the
consumer output. The build-transitive targets validate PE architecture and
required exports. The normal runtime still requires the Windows App Runtime,
and the consuming process must match the native architecture.

`Publish` is validated independently from `Build`, including publish-without-build.
Single-file deployment is not supported by the current native validation targets.
For a clean-machine installation test, use a new Windows user profile or VM
without development tools and confirm both the native dependency search path
and the installed Windows App Runtime. Cross-building ARM64 does not count as
running the package on ARM64 hardware.

## Upgrade and troubleshooting

Rebuild rather than reusing an old `bin` directory after changing the package
version or target framework. NuGet and generated XAML assets are framework-
specific; mixing `net8.0` artifacts with `net10.0` output is not supported.

A build failure mentioning `MNATIVE001` usually indicates a missing or wrong-
architecture native binary or missing exports. `MNATIVE002` means that the
selected deployment format is not supported. A loader failure at runtime can
also result from an unavailable Windows App Runtime or its transitive native
files, even if static PE validation succeeded. Consult
[Building](BUILDING.md) and run the `Integration` and `Packages` test layers.

The public editor surface is `MonacoEditor` and the managed Core APIs in
`MonacoNative.Core`. Editor UI state is owned by the WinUI thread; interact
with UI controls on their dispatcher. The project does not currently guarantee
binary compatibility of all public APIs across minor prerelease versions.
Changes to them must be covered by a package-consumer test before a release.
