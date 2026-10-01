# Install the Windows App SDK runtime required by the two framework-dependent
# Win2D renderer console probes. Hosted runners do not promise this runtime.
$ErrorActionPreference = 'Stop'
$version = '2.4.0' # Keep in sync with RendererGoldenTests and RendererBenchmarks.
$downloadUrl = 'https://aka.ms/windowsappsdk/2.4/2.4.0/windowsappruntimeinstall-x64.exe'
$installer = Join-Path $env:RUNNER_TEMP "WindowsAppRuntimeInstall-$version-x64.exe"

Write-Host "[WinAppSDK] Downloading official Windows App Runtime $version (x64)..."
Invoke-WebRequest -Uri $downloadUrl -OutFile $installer -TimeoutSec 120
if (-not (Test-Path -PathType Leaf $installer)) {
    throw "Windows App Runtime installer was not downloaded: $installer"
}

# Do not execute an installer whose publisher cannot be verified.
$signature = Get-AuthenticodeSignature -FilePath $installer
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft') {
    throw "Windows App Runtime installer signature invalid: $($signature.Status) / $($signature.SignerCertificate.Subject)"
}

Write-Host '[WinAppSDK] Installing runtime silently (--quiet), with a 4-minute limit...'
$process = Start-Process -FilePath $installer -ArgumentList '--quiet' -PassThru
if (-not $process.WaitForExit(240000)) {
    try { $process.Kill($true) } catch { Write-Warning "Installer shutdown: $_" }
    throw 'Windows App Runtime installer exceeded 240 seconds.'
}
$process.Refresh()
if ($process.ExitCode -ne 0) {
    throw "Windows App Runtime installation failed with exit code $($process.ExitCode)."
}
Write-Host '[WinAppSDK] Silent installation completed successfully.'
Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime*' |
    Select-Object Name, Version, Architecture |
    Format-Table -AutoSize | Out-String | Write-Host
