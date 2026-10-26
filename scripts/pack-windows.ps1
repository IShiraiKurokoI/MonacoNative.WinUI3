param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$Version = '0.1.0'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$packages = Join-Path $root 'artifacts\packages'
New-Item -ItemType Directory -Force -Path $packages | Out-Null

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'vswhere.exe was not found.' }
$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild was not found.' }

function Invoke-MSBuild([string]$Project, [string]$Platform) {
    Write-Host "Building $Project ($Configuration|$Platform)"
    & $msbuild (Join-Path $root $Project) /restore /m /p:Configuration=$Configuration /p:Platform=$Platform
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
function Invoke-DotnetBuild([string]$Project, [string]$Platform = '') {
    $args = @('build', (Join-Path $root $Project), '-c', $Configuration)
    if ($Platform) { $args += "-p:Platform=$Platform" }
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# Native artifacts for both supported architectures.
Invoke-MSBuild 'src\MonacoNative.Core.Native\MonacoNative.Core.Native.vcxproj' 'x64'
Invoke-MSBuild 'src\MonacoNative.Core.Native\MonacoNative.Core.Native.vcxproj' 'ARM64'
Invoke-MSBuild 'src\MonacoNative.WinRT.Component\MonacoNative.WinRT.Component.vcxproj' 'x64'
Invoke-MSBuild 'src\MonacoNative.WinRT.Component\MonacoNative.WinRT.Component.vcxproj' 'ARM64'

foreach ($platform in @('x64', 'ARM64')) {
    $winrtOut = Join-Path $root "artifacts\winrt\$platform\$Configuration"
    foreach ($required in @('MonacoNative.WinRT.Component.dll', 'MonacoNative.WinRT.winmd')) {
        $path = Join-Path $winrtOut $required
        if (-not (Test-Path $path)) {
            throw "C++/WinRT build did not produce required artifact: $path"
        }
    }
}

# Portable managed dependencies, then the architecture-aware Windows projection/UI.
Invoke-DotnetBuild 'src\MonacoNative.EditorServices\MonacoNative.EditorServices.csproj'
Invoke-DotnetBuild 'src\MonacoNative.Language\MonacoNative.Language.csproj'
Invoke-DotnetBuild 'src\MonacoNative.Language.Json\MonacoNative.Language.Json.csproj'
Invoke-DotnetBuild 'src\MonacoNative.Language.Html\MonacoNative.Language.Html.csproj'
Invoke-DotnetBuild 'src\MonacoNative.Language.Css\MonacoNative.Language.Css.csproj'
Invoke-DotnetBuild 'src\MonacoNative.Language.TypeScript\MonacoNative.Language.TypeScript.csproj'
Invoke-DotnetBuild 'src\MonacoNative.Language.CSharp\MonacoNative.Language.CSharp.csproj'
Invoke-DotnetBuild 'src\MonacoNative.Language.Cpp\MonacoNative.Language.Cpp.csproj'
Invoke-DotnetBuild 'src\MonacoNative.Core.Managed\MonacoNative.Core.Managed.csproj' 'x64'
Invoke-DotnetBuild 'src\MonacoNative.Renderer.WinUI3\MonacoNative.Renderer.WinUI3.csproj' 'x64'
Invoke-DotnetBuild 'src\MonacoNative.Core.Managed\MonacoNative.Core.Managed.csproj' 'ARM64'
Invoke-DotnetBuild 'src\MonacoNative.Renderer.WinUI3\MonacoNative.Renderer.WinUI3.csproj' 'ARM64'

python (Join-Path $root 'scripts\check-abi-contract.py')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
python (Join-Path $root 'scripts\check-winrt-package-metadata.py')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Packing MonacoNative.WinUI3 $Version"
dotnet pack (Join-Path $root 'packaging\MonacoNative.WinUI3.Package.csproj') -c $Configuration -p:PackageVersion=$Version -o $packages
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Packing MonacoNative.WinRT.CoreHost $Version"
dotnet pack (Join-Path $root 'packaging\MonacoNative.WinRT.CoreHost.Package.csproj') -c $Configuration -p:PackageVersion=$Version -o $packages
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$uiPackage = Join-Path $packages "MonacoNative.WinUI3.$Version.nupkg"
$winrtPackage = Join-Path $packages "MonacoNative.WinRT.CoreHost.$Version.nupkg"
python (Join-Path $root 'scripts\check-package.py') $uiPackage
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
python (Join-Path $root 'scripts\check-package.py') $winrtPackage
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Packages created in $packages"
