param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$Version = '0.1.0'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# Use a NuGet.Config with two named sources instead of repeated -s arguments.
# On Windows, the latter mis-parsed https:// as a relative file source (NU1301).
$nugetConfig = Join-Path $root 'tests\PackageConsumerSmoke\NuGet.Config'
$core = Join-Path $root 'tests\PackageConsumerSmoke\CoreConsumer\CoreConsumer.csproj'
$winui = Join-Path $root 'tests\PackageConsumerSmoke\WinUIConsumer\WinUIConsumer.csproj'
$example = Join-Path $root 'examples\NuGetWinUI3\MonacoNative.NuGetExample.csproj'

foreach ($platform in @('x64','ARM64')) {
    Write-Host "Restoring/building package consumers for $platform"
    dotnet restore $core --configfile $nugetConfig -p:Platform=$platform -p:MonacoNativePackageVersion=$Version
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet build $core -c $Configuration --no-restore -p:Platform=$platform -p:MonacoNativePackageVersion=$Version
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    dotnet restore $winui --configfile $nugetConfig -p:Platform=$platform -p:MonacoNativePackageVersion=$Version
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet build $winui -c $Configuration --no-restore -p:Platform=$platform -p:MonacoNativePackageVersion=$Version
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    # Public example must also compile as a package-only consumer, not just the smoke fixture.
    dotnet restore $example --configfile $nugetConfig -p:Platform=$platform -p:MonacoNativePackageVersion=$Version
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet build $example -c $Configuration --no-restore -p:Platform=$platform -p:MonacoNativePackageVersion=$Version
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    if ($platform -eq 'x64') {
        $consumerOutput = Join-Path (Split-Path $winui -Parent) "bin\$platform\$Configuration"
        $rendererDll = Get-ChildItem $consumerOutput -Recurse -Filter 'MonacoNative.Renderer.WinUI3.dll' -ErrorAction SilentlyContinue | Select-Object -First 1
        $nativeDll = Get-ChildItem $consumerOutput -Recurse -Filter 'MonacoNative.Core.Native.dll' -ErrorAction SilentlyContinue | Select-Object -First 1
        $pri = Get-ChildItem $consumerOutput -Recurse -Filter '*.pri' -ErrorAction SilentlyContinue | Select-Object -First 1
        $xbf = Get-ChildItem $consumerOutput -Recurse -Filter '*.xbf' -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $rendererDll -or -not $nativeDll -or -not $pri -or -not $xbf) {
            throw 'WinUI package consumer output is missing renderer/native/PRI/XBF assets.'
        }
    }
}


