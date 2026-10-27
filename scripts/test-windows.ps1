# Backward-compatible entry point; the layered runner owns build/test order.
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$RequireGoldenBaseline
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $root 'scripts/test.ps1') -Layer All -Configuration $Configuration `
    -RequireGoldenBaseline:$RequireGoldenBaseline
exit $LASTEXITCODE
