param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$BuildOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$build = Join-Path $root 'build-core'

if (Test-Path $build) {
    Remove-Item $build -Recurse -Force
}

cmake -S $root -B $build -DMONACONATIVE_WARNINGS_AS_ERRORS=ON
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
cmake --build $build --config $Configuration --parallel
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Catch a missing Native DLL before launching CTest in headless Windows.
# The ABI test's POST_BUILD step must stage it beside the executable.
$abiDir = Join-Path $build "tests\NativeAbiTests\$Configuration"
$abiExe = Join-Path $abiDir 'MonacoNative.NativeAbi.Tests.exe'
$abiDll = Join-Path $abiDir 'MonacoNative.Core.Native.dll'
foreach ($required in @($abiExe, $abiDll)) {
    if (-not (Test-Path $required -PathType Leaf)) {
        throw "Windows ABI smoke test dependency missing: $required"
    }
}
Write-Host "[ABI] Executable and native DLL staged together in $abiDir"

if ($BuildOnly) {
    Write-Host 'Native CMake build and ABI DLL preflight PASS; CTest suites run in separate CI steps.'
    exit 0
}

$results = Join-Path $build 'ctest-results.xml'
ctest --test-dir $build --output-on-failure -C $Configuration --output-junit $results
exit $LASTEXITCODE
