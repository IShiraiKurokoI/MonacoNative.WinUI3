param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$Version = '0.8.0'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

& (Join-Path $root 'scripts\pack-windows.ps1') -Configuration $Configuration -Version $Version
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& (Join-Path $root 'scripts\test-package-consumers.ps1') -Configuration $Configuration -Version $Version
exit $LASTEXITCODE
