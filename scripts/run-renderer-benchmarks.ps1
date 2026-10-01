param(
    [ValidateSet('quick','full')]
    [string]$Profile = 'quick',
    [string]$Output = ''
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $Root "artifacts/benchmarks/renderer-$Profile.json"
} elseif (-not [System.IO.Path]::IsPathRooted($Output)) {
    # The child EXE runs from its own project folder, not the repository root.
    # Resolve relative outputs first so the benchmark and budget guard agree.
    $Output = Join-Path $Root $Output
}
$Output = [System.IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Output) | Out-Null

Write-Host "[Renderer performance] Starting headless Win2D renderer benchmark ($Profile)."
& (Join-Path $Root 'scripts\invoke-headless-renderer.ps1') `
    -Project 'benchmarks\RendererBenchmarks\MonacoNative.Renderer.Benchmarks.csproj' `
    -ExecutableName 'MonacoNative.Renderer.Benchmarks' -Configuration Release `
    -TimeoutSeconds 180 -LogName "renderer-benchmark-$Profile" `
    -ProgramArguments @('--profile', $Profile, '--output', $Output)
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

python (Join-Path $Root 'scripts\check-benchmark-results.py') $Output `
    --budgets (Join-Path $Root 'reference\performance-budgets-renderer.json')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Renderer benchmark result: $Output"
