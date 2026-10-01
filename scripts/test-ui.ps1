[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$OutputDirectory = '',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$params = @{ Configuration=$Configuration; SkipBuild=$SkipBuild }
if ($OutputDirectory) { $params.OutputDirectory = $OutputDirectory }
& (Join-Path $root 'tests\UiAutomationTests\Run-EditorUi.ps1') @params
exit $LASTEXITCODE
