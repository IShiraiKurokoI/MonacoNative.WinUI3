param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# Managed input, accessibility, language, LSP and renderer-cache tests.
& (Join-Path $root 'scripts\test-portable-managed.ps1') -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Portable/native CTest suite under the Windows compiler.
& (Join-Path $root 'scripts\test-core.ps1') -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Build the actual x64 MSVC DLL, managed wrapper, and WinUI sample.
& (Join-Path $root 'scripts\build-windows.ps1') -Configuration $Configuration -Platform x64
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Golden image fingerprint: own CI step and failure logs.
& (Join-Path $root 'scripts\test-renderer-golden.ps1') -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Deterministic renderer work-count benchmark. Wall-clock metrics are emitted for
# trend analysis, while the gate checks only machine-independent operation counts.
& (Join-Path $root 'scripts\run-renderer-benchmarks.ps1') -Profile quick `
    -Output (Join-Path $root 'artifacts\benchmarks\renderer-quick.json')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Execute P/Invoke smoke tests against the produced Windows DLL.
dotnet run --project (Join-Path $root 'tests\ManagedTests\MonacoNative.Managed.Tests.csproj') `
    -c $Configuration -p:Platform=x64
exit $LASTEXITCODE
