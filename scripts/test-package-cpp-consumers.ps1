param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$Version = '0.1.0'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$nugetConfig = Join-Path $root 'tests\PackageConsumerSmoke\NuGet.Config'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild was not found.' }
$cpp = Join-Path $root 'tests\PackageConsumerSmoke\CppWinRTConsumer\CppWinRTConsumer.vcxproj'
foreach ($platform in @('x64','ARM64')) {
    Write-Host "Building C++/WinRT NuGet consumer for $platform"
    & $msbuild $cpp /restore /m /p:Configuration=Release /p:Platform=$platform /p:MonacoNativePackageVersion=$Version "/p:RestoreConfigFile=$nugetConfig"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

