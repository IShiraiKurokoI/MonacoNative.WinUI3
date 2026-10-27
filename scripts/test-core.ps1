param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$BuildOnly,
    [switch]$TestOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$build = Join-Path $root 'build-core'

if ($BuildOnly -and $TestOnly) { throw 'BuildOnly and TestOnly cannot both be set.' }
if (-not $TestOnly) {
    if (Test-Path $build) { Remove-Item $build -Recurse -Force }
    cmake -S $root -B $build -DMONACONATIVE_WARNINGS_AS_ERRORS=ON
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    cmake --build $build --config $Configuration --parallel
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# Verify the same staged ABI binary in both build-only and test-only mode.
$abiDir = Join-Path $build "tests\NativeAbiTests\$Configuration"
$abiExe = Join-Path $abiDir 'MonacoNative.NativeAbi.Tests.exe'
$abiDll = Join-Path $abiDir 'MonacoNative.Core.Native.dll'
foreach ($required in @($abiExe, $abiDll)) {
    if (-not (Test-Path $required -PathType Leaf)) {
        throw "Native Core/ABI stage requires the Build layer first. Missing: $required"
    }
}
Write-Host "[ABI] Executable and native DLL staged together in $abiDir"

if ($BuildOnly) {
    Write-Host 'Native Core build and staged ABI dependency verification PASS.'
    exit 0
}

$results = Join-Path $build 'ctest-results.xml'
ctest --test-dir $build --output-on-failure -C $Configuration --output-junit $results
exit $LASTEXITCODE
