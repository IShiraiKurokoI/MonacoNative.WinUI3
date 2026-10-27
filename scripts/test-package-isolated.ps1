# Restore, compile and execute the package-only Core consumer outside the source tree.
# This deliberately starts with an empty NuGet cache; it does not claim to test
# Windows installation/uninstallation or an interactive WinUI deployment.
[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')][string]$Platform = 'x64',
    [string]$Version = '0.1.0',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$PackageDirectory = '',
    [string]$ReportPath = 'artifacts/tests/isolated-package.json',
    [switch]$KeepWorkDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $PackageDirectory) { $PackageDirectory = Join-Path $root 'artifacts/packages' }
if (-not [IO.Path]::IsPathRooted($PackageDirectory)) { $PackageDirectory = Join-Path $root $PackageDirectory }
$PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
$package = Join-Path $PackageDirectory "MonacoNative.WinUI3.$Version.nupkg"
if (-not (Test-Path -LiteralPath $package -PathType Leaf)) { throw "Package is not present: $package" }
if (-not [IO.Path]::IsPathRooted($ReportPath)) { $ReportPath = Join-Path $root $ReportPath }
$ReportPath = [IO.Path]::GetFullPath($ReportPath)
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $ReportPath) | Out-Null

$arch = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
if ($arch -ine $Platform) { throw "Isolated runtime test requires native $Platform process; running $arch." }
$workBase = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$work = Join-Path $workBase ('MonacoNative-Consumer-' + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $root 'tests/PackageConsumerSmoke/CoreConsumer'
$oldPackages = $env:NUGET_PACKAGES
$oldHttpCache = $env:NUGET_HTTP_CACHE_PATH
$oldCliHome = $env:DOTNET_CLI_HOME
$started = [DateTime]::UtcNow
$status = 'FAIL'
$failure = $null
$exitCode = $null
$stdoutFile = Join-Path (Split-Path -Parent $ReportPath) "isolated-$($Platform.ToLowerInvariant())-stdout.log"
$stderrFile = Join-Path (Split-Path -Parent $ReportPath) "isolated-$($Platform.ToLowerInvariant())-stderr.log"
try {
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    Copy-Item -LiteralPath (Join-Path $source 'CoreConsumer.csproj') -Destination $work
    Copy-Item -LiteralPath (Join-Path $source 'Program.cs') -Destination $work
    $safePath = [System.Security.SecurityElement]::Escape($PackageDirectory)
    $nuget = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="candidate" value="$safePath" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@
    $config = Join-Path $work 'NuGet.Config'
    [IO.File]::WriteAllText($config, $nuget, [Text.UTF8Encoding]::new($false))
    $env:NUGET_PACKAGES = Join-Path $work 'packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $work 'http-cache'
    $env:DOTNET_CLI_HOME = Join-Path $work 'cli-home'
    $project = Join-Path $work 'CoreConsumer.csproj'
    & dotnet restore $project --configfile $config "-p:Platform=$Platform" "-p:MonacoNativePackageVersion=$Version"
    if ($LASTEXITCODE -ne 0) { throw "Isolated NuGet restore failed ($LASTEXITCODE)." }
    & dotnet build $project -c $Configuration --no-restore "-p:Platform=$Platform" "-p:MonacoNativePackageVersion=$Version"
    if ($LASTEXITCODE -ne 0) { throw "Isolated consumer build failed ($LASTEXITCODE)." }
    $rid = 'win-' + $Platform.ToLowerInvariant()
    $output = Join-Path $work "bin/$Platform/$Configuration/net10.0-windows10.0.26100.0/$rid"
    $exe = Join-Path $output 'CoreConsumer.exe'
    $native = Join-Path $output 'MonacoNative.Core.Native.dll'
    foreach ($path in @($exe, $native)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "NuGet consumer asset missing: $path" }
    }
    Remove-Item -LiteralPath $stdoutFile, $stderrFile -ErrorAction SilentlyContinue
    $process = Start-Process -FilePath $exe -WorkingDirectory $output -NoNewWindow -PassThru `
        -RedirectStandardOutput $stdoutFile -RedirectStandardError $stderrFile
    try {
        if (-not $process.WaitForExit(45000)) {
            $process.Kill($true)
            throw 'Isolated Core consumer timed out after 45 seconds.'
        }
        $exitCode = $process.ExitCode
        if ($exitCode -ne 0) { throw "Isolated Core consumer exited $exitCode." }
    }
    finally { $process.Dispose() }
    $outputText = Get-Content -LiteralPath $stdoutFile -Raw -ErrorAction SilentlyContinue
    if ($outputText -notlike '*MonacoNative package smoke OK*') {
        throw 'Core smoke process did not produce its ABI/edit completion marker.'
    }
    $status = 'PASS'
    Write-Host "Isolated NuGet restore/build/native execution PASS ($Platform)."
}
catch {
    $failure = $_.Exception.Message
    Write-Warning "Isolated NuGet consumer FAIL: $failure"
}
finally {
    $env:NUGET_PACKAGES = $oldPackages
    $env:NUGET_HTTP_CACHE_PATH = $oldHttpCache
    $env:DOTNET_CLI_HOME = $oldCliHome
    [ordered]@{
        schemaVersion = 1; status = $status; platform = $Platform; processArchitecture = $arch;
        version = $Version; startedAtUtc = $started.ToString('o'); finishedAtUtc = [DateTime]::UtcNow.ToString('o');
        isolatedCache = $true; packageOnly = $true; nativeRuntimeExecuted = $status -eq 'PASS';
        interactiveWinUIExecuted = $false; processExitCode = $exitCode; failure = $failure
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
    if (-not $KeepWorkDirectory -and (Test-Path $work)) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
}
if ($status -ne 'PASS') { exit 1 }
