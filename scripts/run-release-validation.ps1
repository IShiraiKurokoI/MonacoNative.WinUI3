param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# Collect fresh native core quick/full benchmark evidence for the release gate.
& (Join-Path $root 'scripts\run-benchmarks.ps1') -Profile quick -Output (Join-Path $root 'artifacts\benchmarks\core-quick.json')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& (Join-Path $root 'scripts\run-benchmarks.ps1') -Profile full -Output (Join-Path $root 'artifacts\benchmarks\core-full.json')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Reuse the Windows renderer evidence produced by test-windows.ps1 when present; otherwise
# produce it here. It remains a strict part of the final certification.
$rendererEvidence = Join-Path $root 'artifacts\benchmarks\renderer-quick.json'
if (-not (Test-Path $rendererEvidence)) {
    & (Join-Path $root 'scripts\run-renderer-benchmarks.ps1') -Profile quick -Output $rendererEvidence
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

python (Join-Path $root 'scripts\check-parity-release.py') --output (Join-Path $root 'artifacts\release\parity-report.json')
exit $LASTEXITCODE
