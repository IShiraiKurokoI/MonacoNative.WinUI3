param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$Version = '0.1.0'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$core = Join-Path $root 'tests\PackageConsumerSmoke\CoreConsumer\CoreConsumer.csproj'

# Run the already-built console app directly (no further restore, no implicit
# dotnet-run launch behavior). Keep the output even if Windows displays a
# headless dialog or stalls in DLL loading, and kill it after a short timeout.
$consumerDir = Join-Path (Split-Path -Parent $core) "bin\x64\$Configuration\net8.0-windows10.0.26100.0\win-x64"
$consumerExe = Join-Path $consumerDir 'CoreConsumer.exe'
$consumerDll = Join-Path $consumerDir 'MonacoNative.Core.Native.dll'
foreach ($required in @($consumerExe, $consumerDll)) {
    if (-not (Test-Path $required -PathType Leaf)) {
        throw "Package smoke executable/native DLL not deployed: $required"
    }
}
$logsDir = Join-Path $root 'artifacts\logs'
New-Item -ItemType Directory -Path $logsDir -Force | Out-Null
$stdoutLog = Join-Path $logsDir 'package-core-consumer-stdout.log'
$stderrLog = Join-Path $logsDir 'package-core-consumer-stderr.log'
# Start-Process doesn't overwrite files reliably on repeated local runs.
Remove-Item -LiteralPath $stdoutLog, $stderrLog -ErrorAction SilentlyContinue
Write-Host "Running x64 package CoreConsumer (45s timeout) from $consumerDir"
$process = Start-Process -FilePath $consumerExe -WorkingDirectory $consumerDir `
    -NoNewWindow -PassThru -RedirectStandardOutput $stdoutLog -RedirectStandardError $stderrLog
$timedOut = $false
try {
    if (-not $process.WaitForExit(45000)) {
        $timedOut = $true
        # Kill the entire child tree, even when startup spawned a GUI helper.
        $process.Kill($true)
        $null = $process.WaitForExit(5000)
    }
} finally {
    if (Test-Path $stdoutLog) { Get-Content $stdoutLog | ForEach-Object { Write-Host "[core stdout] $_" } }
    if (Test-Path $stderrLog) { Get-Content $stderrLog | ForEach-Object { Write-Host "[core stderr] $_" } }
}
if ($timedOut) {
    throw "Package CoreConsumer blocked for 45s. Logs: $logsDir; check Main-entered marker for pre-Main WindowsAppSDK/DLL loader dialogs."
}
if ($process.ExitCode -ne 0) { throw "Package CoreConsumer exited with code $($process.ExitCode). Logs: $logsDir" }
Write-Host 'Package CoreConsumer smoke test passed.'
