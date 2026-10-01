param(
    [ValidateSet('quick','full')]
    [string]$Profile = 'quick',
    [string]$Output = ''
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $Root "artifacts/benchmarks/core-$Profile.json"
} elseif (-not [System.IO.Path]::IsPathRooted($Output)) {
    $Output = Join-Path $Root $Output
}
$Output = [System.IO.Path]::GetFullPath($Output)
$Build = Join-Path $Root 'build-benchmarks'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Output) | Out-Null

cmake -S $Root -B $Build -A x64 `
    -DMONACONATIVE_BUILD_TESTS=OFF `
    -DMONACONATIVE_BUILD_NATIVE_ABI=OFF `
    -DMONACONATIVE_BUILD_BENCHMARKS=ON `
    -DMONACONATIVE_WARNINGS_AS_ERRORS=ON
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
cmake --build $Build --config Release --target MonacoNative.Core.Benchmarks --parallel
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$Exe = Join-Path $Build 'benchmarks/CoreBenchmarks/Release/MonacoNative.Core.Benchmarks.exe'
& $Exe --profile $Profile --output $Output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
python (Join-Path $Root 'scripts/check-benchmark-results.py') $Output --budgets (Join-Path $Root 'reference/performance-budgets-core.json')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Benchmark result: $Output"
