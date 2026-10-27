# Repeated real-window lifetime smoke on an unlocked Windows desktop.
# Each process owns its own renderer, text model, TSF session and language services.
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateRange(1, 100)][int]$Runs = 3,
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts\ui-tests' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$exe = Join-Path $root "src\MonacoNative.SampleApp\bin\x64\$Configuration\net10.0-windows10.0.26100.0\MonacoNative.SampleApp.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "Sample executable missing: $exe" }
if (-not [Environment]::UserInteractive) { throw 'Window lifecycle checks require an unlocked interactive desktop.' }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$records = [System.Collections.Generic.List[object]]::new()
$failure = $null
$started = [DateTime]::UtcNow
try {
    for ($i = 1; $i -le $Runs; $i++) {
        $process = Start-Process -FilePath $exe -PassThru
        try {
            $timer = [Diagnostics.Stopwatch]::StartNew()
            $window = $null
            $editor = $null
            while ($timer.Elapsed.TotalSeconds -lt 20) {
                $process.Refresh()
                if ($process.HasExited) { throw "Sample exited early on run $i (exit code $($process.ExitCode))" }
                $windowCondition = New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
                $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
                    [System.Windows.Automation.TreeScope]::Children, $windowCondition)
                if ($window) {
                    $editorCondition = New-Object System.Windows.Automation.PropertyCondition(
                        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'MonacoEditorSurface')
                    $editor = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $editorCondition)
                    if ($editor) { break }
                }
                Start-Sleep -Milliseconds 150
            }
            if (-not $editor) { throw "Sample editor UIA peer not ready in run $i" }
            # Close while a new sample load/language refresh can still be pending.
            if (($i % 2) -eq 0) {
                $loadCondition = New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'LoadSampleButton')
                $load = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $loadCondition)
                if ($load) {
                    $invoke = $load.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
                    $invoke.Invoke()
                }
            }
            $peakBeforeClose = $process.PeakWorkingSet64
            if (-not $process.CloseMainWindow()) { throw "Sample did not accept CloseMainWindow on run $i" }
            if (-not $process.WaitForExit(15000)) { throw "Sample did not exit after close on run $i" }
            if ($process.ExitCode -ne 0) { throw "Sample exited with code $($process.ExitCode) on run $i" }
            $timer.Stop()
            $records.Add([pscustomobject]@{
                iteration = $i; status = 'PASS'; exitCode = $process.ExitCode;
                elapsedMs = [math]::Round($timer.Elapsed.TotalMilliseconds, 2);
                peakWorkingSetBeforeCloseBytes = $peakBeforeClose
            })
            Write-Host "PASS: window cycle $i/$Runs closed normally"
        }
        finally {
            try { if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null } } catch {}
            $process.Dispose()
        }
    }
}
catch {
    $failure = $_.Exception.Message
    Write-Error -Message $failure -ErrorAction Continue
}
finally {
    $report = [ordered]@{
        schemaVersion = 1; configuration = $Configuration; startedAtUtc = $started.ToString('o');
        completedAtUtc = [DateTime]::UtcNow.ToString('o');
        status = if ($failure) { 'FAIL' } else { 'PASS' };
        requestedRuns = $Runs; completedRuns = $records.Count;
        error = $failure; runs = @($records.ToArray())
    }
    $reportPath = Join-Path $OutputDirectory 'window-lifecycle-report.json'
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding UTF8
    Write-Host "Window lifecycle report: $reportPath"
}
if ($failure) { exit 1 }
exit 0
