# Run on a real, unlocked Windows desktop with a fixed display configuration.
# UIA text observation is not an input-to-present (photon) measurement.
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateRange(5, 500)][int]$Iterations = 40,
    [string]$ReportPath = 'artifacts/tests/desktop-telemetry.json',
    [switch]$RequireGpuCounters
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not [IO.Path]::IsPathRooted($ReportPath)) { $ReportPath = Join-Path $root $ReportPath }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $ReportPath) | Out-Null
if (-not [Environment]::UserInteractive) { throw 'Desktop performance tests require an unlocked Windows session.' }
$exe = Join-Path $root "src/MonacoNative.SampleApp/bin/x64/$Configuration/net10.0-windows10.0.26100.0/MonacoNative.SampleApp.exe"
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Build the sample before measuring: $exe" }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class MonacoTelemetryWindow {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
}
'@

function FindElement([System.Windows.Automation.AutomationElement]$parent, [string]$property, [object]$value) {
    $prop = if ($property -eq 'ProcessId') { [System.Windows.Automation.AutomationElement]::ProcessIdProperty }
            else { [System.Windows.Automation.AutomationElement]::AutomationIdProperty }
    $condition = New-Object System.Windows.Automation.PropertyCondition($prop, $value)
    return $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function GetGpuBytes([int]$processId) {
    try {
        $rows = @(Get-CimInstance -ClassName Win32_PerfFormattedData_GPUPerformanceCounters_GPUProcessMemory -ErrorAction Stop |
            Where-Object { $_.Name -match "(^|_)pid_$($processId)(_\b|_)" })
        if ($rows.Count -eq 0) { return $null }
        $dedicated = [long]0
        $shared = [long]0
        $validCounters = 0
        foreach ($row in $rows) {
            $dedicatedProperty = $row.PSObject.Properties['DedicatedUsage']
            $sharedProperty = $row.PSObject.Properties['SharedUsage']
            if ($null -eq $dedicatedProperty -and $null -eq $sharedProperty) { continue }
            if ($null -ne $dedicatedProperty) { $dedicated += [long]$row.DedicatedUsage }
            if ($null -ne $sharedProperty) { $shared += [long]$row.SharedUsage }
            $validCounters++
        }
        # A driver exposing an empty or incompatible counter class is not
        # evidence of zero GPU usage; treat this sample as unavailable.
        if ($validCounters -eq 0) { return $null }
        return [pscustomobject]@{ dedicatedBytes = $dedicated; sharedBytes = $shared }
    }
    catch { return $null }
}
function Percentile([double[]]$values, [double]$q) {
    if ($values.Length -eq 0) { return $null }
    $ordered = @($values | Sort-Object)
    $index = [Math]::Max(0, [Math]::Min($ordered.Count - 1, [int][Math]::Ceiling($q * $ordered.Count) - 1))
    return [Math]::Round([double]$ordered[$index], 3)
}

$started = [DateTime]::UtcNow
$status = 'FAIL'
$failure = $null
$process = $null
$latencies = New-Object System.Collections.Generic.List[double]
$gpuSamples = New-Object System.Collections.Generic.List[object]
$peakWorkingSet = [long]0
try {
    $process = Start-Process -FilePath $exe -PassThru
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $window = $null
    $editor = $null
    while ($timer.Elapsed.TotalSeconds -lt 25) {
        $process.Refresh()
        if ($process.HasExited) { throw "Sample exited during startup ($($process.ExitCode))." }
        $window = FindElement ([System.Windows.Automation.AutomationElement]::RootElement) 'ProcessId' $process.Id
        if ($window) { $editor = FindElement $window 'AutomationId' 'MonacoEditorSurface' }
        if ($editor) { break }
        Start-Sleep -Milliseconds 100
    }
    if (-not $editor) { throw 'Editor UIA peer did not appear.' }
    [MonacoTelemetryWindow]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
    $editor.SetFocus()
    Start-Sleep -Milliseconds 350
    $textPattern = [System.Windows.Automation.TextPattern]$editor.GetCurrentPattern(
        [System.Windows.Automation.TextPattern]::Pattern)
    $previous = $textPattern.DocumentRange.GetText(-1)
    $memory = GetGpuBytes $process.Id
    if ($null -ne $memory) { $gpuSamples.Add($memory) }
    for ($i = 0; $i -lt $Iterations; ++$i) {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        [System.Windows.Forms.SendKeys]::SendWait('x')
        $updated = $false
        while ($watch.ElapsedMilliseconds -lt 2500) {
            $current = $textPattern.DocumentRange.GetText(-1)
            if ($current.Length -eq $previous.Length + 1) {
                $latencies.Add($watch.Elapsed.TotalMilliseconds)
                $previous = $current
                $updated = $true
                break
            }
            Start-Sleep -Milliseconds 5
        }
        if (-not $updated) { throw "UIA text did not reflect keystroke $($i + 1) within 2.5 seconds." }
        $process.Refresh()
        $peakWorkingSet = [Math]::Max($peakWorkingSet, $process.WorkingSet64)
        if (($i % 10) -eq 9) {
            $memory = GetGpuBytes $process.Id
            if ($null -ne $memory) { $gpuSamples.Add($memory) }
        }
    }
    $memory = GetGpuBytes $process.Id
    if ($null -ne $memory) { $gpuSamples.Add($memory) }
    if ($RequireGpuCounters -and $gpuSamples.Count -eq 0) {
        throw 'GPU process memory counters are unavailable on this device/driver.'
    }
    if (-not $process.CloseMainWindow() -or -not $process.WaitForExit(15000) -or $process.ExitCode -ne 0) {
        throw 'Sample did not close cleanly after telemetry collection.'
    }
    $status = 'PASS'
}
catch {
    $failure = $_.Exception.Message
    Write-Warning "Desktop telemetry FAIL: $failure"
}
finally {
    if ($process) {
        try { if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null } } catch {}
        $process.Dispose()
    }
    $l = @($latencies.ToArray())
    $gpu = @($gpuSamples.ToArray())
    [ordered]@{
        schemaVersion = 1; status = $status; configuration = $Configuration;
        startedAtUtc = $started.ToString('o'); finishedAtUtc = [DateTime]::UtcNow.ToString('o');
        iterationsRequested = $Iterations; iterationsObserved = $latencies.Count;
        measurement = 'sendkeys-to-uia-observed-text';
        uiTextObservedP50Ms = Percentile $l 0.50;
        uiTextObservedP95Ms = Percentile $l 0.95;
        uiTextObservedP99Ms = Percentile $l 0.99;
        gpuCounterStatus = if ($gpu.Count -gt 0) { 'AVAILABLE' } else { 'NOT_AVAILABLE' };
        gpuDedicatedPeakBytes = if ($gpu.Count -gt 0) { ($gpu | Measure-Object -Property dedicatedBytes -Maximum).Maximum } else { $null };
        gpuSharedPeakBytes = if ($gpu.Count -gt 0) { ($gpu | Measure-Object -Property sharedBytes -Maximum).Maximum } else { $null };
        peakWorkingSetBytes = $peakWorkingSet;
        measuresFramePresentation = $false; measuresRetainedGpuResources = $false;
        failure = $failure
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ReportPath -Encoding utf8
    Write-Host "Desktop telemetry: $ReportPath ($status)"
}
if ($status -ne 'PASS') { exit 1 }
