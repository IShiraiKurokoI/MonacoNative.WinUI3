param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateSet('x64', 'ARM64')][string]$Platform = 'x64',
    [string]$NativeLibrary
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ($env:OS -ne 'Windows_NT') { throw 'This acceptance entry point tests the Windows NuGet runtime assets.' }
$rid = 'win-' + $Platform.ToLowerInvariant()
$processArch = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
if ($processArch -ine $Platform) {
    throw "Run this test in a $Platform process on a matching Windows host (current: $processArch). Cross-building is not execution."
}
if ([string]::IsNullOrWhiteSpace($NativeLibrary)) {
    $NativeLibrary = Join-Path $root "build-core\tests\NativeAbiTests\$Configuration\MonacoNative.Core.Native.dll"
}
if (-not (Test-Path -LiteralPath $NativeLibrary -PathType Leaf)) {
    throw "Build Core.Native first with scripts/test-core.ps1 -Configuration $Configuration -BuildOnly. Missing: $NativeLibrary"
}
$nativePath = (Resolve-Path -LiteralPath $NativeLibrary).Path
$project = Join-Path $root 'tests\LanguageTests\MonacoNative.Language.Tests.csproj'
$output = Join-Path $root "artifacts\language-tests\$rid\$Configuration"
# A clean test output prevents a stale DLL from masking a broken NuGet deployment.
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
dotnet build $project -c $Configuration -r $rid --self-contained false --output $output
if ($LASTEXITCODE -ne 0) { throw "Language test build failed with exit code $LASTEXITCODE" }
$clangPath = $null
foreach ($candidate in @((Join-Path $output 'libclang.dll'), (Join-Path $output "runtimes\$rid\native\libclang.dll"))) {
    if (Test-Path -LiteralPath $candidate -PathType Leaf) { $clangPath = (Resolve-Path -LiteralPath $candidate).Path; break }
}
if (-not $clangPath) { throw "NuGet did not deploy libclang.runtime.$rid [17.0.4] to $output. No global LLVM installation is used as a fallback." }
$assembly = Join-Path $output 'MonacoNative.Language.Tests.dll'
if (-not (Test-Path -LiteralPath $assembly -PathType Leaf)) { throw "Missing language test assembly: $assembly" }
$oldNative = $env:MONACONATIVE_NATIVE_LIBRARY
$oldClang = $env:MONACONATIVE_LIBCLANG
try {
    $env:MONACONATIVE_NATIVE_LIBRARY = $nativePath
    $env:MONACONATIVE_LIBCLANG = $clangPath
    Write-Host "[Language integration] Core.Native: $nativePath"
    Write-Host "[Language integration] NuGet libclang: $clangPath"
    dotnet $assembly --require-native --require-libclang
    if ($LASTEXITCODE -ne 0) { throw "Required language integration tests failed with exit code $LASTEXITCODE" }
}
finally {
    $env:MONACONATIVE_NATIVE_LIBRARY = $oldNative
    $env:MONACONATIVE_LIBCLANG = $oldClang
}
Write-Host 'Required Windows language integration PASS (not interactive WinUI certification).'
