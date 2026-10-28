[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$OutputDirectory = '',
    [switch]$SkipBuild,
    [ValidateRange(0, 100)][int]$WindowCycles = 3
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$params = @{ Configuration=$Configuration; SkipBuild=$SkipBuild }
if ($OutputDirectory) { $params.OutputDirectory = $OutputDirectory }
$global:LASTEXITCODE = 0
& (Join-Path $root 'tests\UiAutomationTests\Run-EditorUi.ps1') @params
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$global:LASTEXITCODE = 0
& (Join-Path $root 'tests\UiAutomationTests\Run-RenameUi.ps1') -Configuration $Configuration -SkipBuild
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$documentParams = @{ Configuration=$Configuration; SkipBuild=$true }
if ($OutputDirectory) { $documentParams.OutputDirectory = $OutputDirectory }
$global:LASTEXITCODE = 0
& (Join-Path $root 'tests\UiAutomationTests\Run-DocumentTabs.ps1') @documentParams
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($WindowCycles -gt 0) {
    $lifetimeParams = @{ Configuration=$Configuration; Runs=$WindowCycles }
    if ($OutputDirectory) { $lifetimeParams.OutputDirectory = $OutputDirectory }
    $global:LASTEXITCODE = 0
    & (Join-Path $root 'tests\UiAutomationTests\Run-WindowLifecycle.ps1') @lifetimeParams
    exit $LASTEXITCODE
}
exit 0
