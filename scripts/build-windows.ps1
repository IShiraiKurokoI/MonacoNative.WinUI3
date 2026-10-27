param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('x64')]
    [string]$Platform = 'x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'MonacoNative.sln'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'vswhere.exe was not found.' }
$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild.exe was not found.' }

Write-Host "Building MonacoNative.sln ($Configuration|$Platform)"
& $msbuild $solution /restore /m /t:Build /p:Configuration=$Configuration /p:Platform=$Platform /v:minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$nativeDll = Join-Path $root "artifacts\native\$Platform\$Configuration\MonacoNative.Core.Native.dll"
$sampleDll = Join-Path $root "src\MonacoNative.SampleApp\bin\$Platform\$Configuration\net10.0-windows10.0.26100.0\MonacoNative.SampleApp.dll"
$tsfTests = Join-Path $root "artifacts\native-input-tests\$Platform\$Configuration\MonacoNative.NativeInput.Tsf.Tests.exe"
foreach ($required in @($nativeDll, $sampleDll, $tsfTests)) {
    if (-not (Test-Path $required)) { throw "Windows solution build did not produce required artifact: $required" }
}

Write-Host 'Windows solution build PASS'
