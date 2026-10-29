# Backwards-compatible name, but never a shortcut around the release gate.
[CmdletBinding()]
param(
    [ValidateSet('Release')][string]$Configuration = 'Release',
    [string]$PackageVersion = '0.8.0'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $root 'scripts/test-acceptance.ps1') -Mode Release -Configuration $Configuration -PackageVersion $PackageVersion
exit $LASTEXITCODE
