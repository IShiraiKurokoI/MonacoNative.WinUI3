param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$Version = '0.1.0'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

foreach ($script in @(
    'test-package-managed-consumers.ps1',
    'test-package-cpp-consumers.ps1',
    'test-package-core-runtime.ps1'
)) {
    & (Join-Path $root "scripts\$script") -Configuration $Configuration -Version $Version
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
Write-Host 'NuGet consumer tests PASS'
