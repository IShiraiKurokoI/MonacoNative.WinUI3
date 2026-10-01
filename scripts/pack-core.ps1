param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$Version = '0.1.0'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$packages = Join-Path $repo 'artifacts\packages'
New-Item -ItemType Directory -Force -Path $packages | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild was not found.' }

foreach ($platform in @('x64','ARM64')) {
    Write-Host "Building native core $Configuration|$platform..."
    & $msbuild (Join-Path $repo 'src\MonacoNative.Core.Native\MonacoNative.Core.Native.vcxproj') /restore /m /p:Configuration=$Configuration /p:Platform=$platform
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
}

Write-Host 'Building managed wrapper...'
dotnet build (Join-Path $repo 'src\MonacoNative.Core.Managed\MonacoNative.Core.Managed.csproj') -c $Configuration -p:Platform=x64
if ($LASTEXITCODE) { exit $LASTEXITCODE }

Write-Host 'Packing MonacoNative.Core NuGet (x64 + ARM64 native assets)...'
dotnet pack (Join-Path $repo 'src\MonacoNative.Core.Managed\MonacoNative.Core.Managed.csproj') -c $Configuration -p:Platform=x64 -p:PackageVersion=$Version -o $packages --no-build
exit $LASTEXITCODE
