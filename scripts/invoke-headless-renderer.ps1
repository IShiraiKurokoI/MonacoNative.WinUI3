param(
    [Parameter(Mandatory = $true)] [string]$Project,
    [Parameter(Mandatory = $true)] [string]$ExecutableName,
    [ValidateSet('Debug', 'Release')] [string]$Configuration = 'Release',
    [ValidateSet('x64', 'ARM64')] [string]$Platform = 'x64',
    [ValidateRange(1, 600)] [int]$TimeoutSeconds = 90,
    [Parameter(Mandatory = $true)] [string]$LogName,
    [string[]]$ProgramArguments = @()
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $root $Project
$diagnosticDir = Join-Path $root 'artifacts/ci-diagnostics'
New-Item -ItemType Directory -Force -Path $diagnosticDir | Out-Null
$stdoutPath = Join-Path $diagnosticDir "$LogName.stdout.log"
$stderrPath = Join-Path $diagnosticDir "$LogName.stderr.log"
# Ensure a rerun cannot show stale PASS/FAIL output from the previous attempt.
Remove-Item -LiteralPath $stdoutPath, $stderrPath -ErrorAction SilentlyContinue

Write-Host "[HEADLESS:$LogName] Building $Project ($Configuration/$Platform)"
& dotnet build $projectPath -c $Configuration "-p:Platform=$Platform" --verbosity minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Run the built EXE, not `dotnet run`: the latter can hide which process is
# waiting, and leaves a second launcher to kill when a modal UI blocks startup.
$projectDirectory = Split-Path -Parent $projectPath
$outputDirectory = Join-Path $projectDirectory "bin/$Platform/$Configuration"
$candidates = @(Get-ChildItem -Path $outputDirectory -File -Filter "$ExecutableName.exe" -Recurse -ErrorAction SilentlyContinue)
if ($candidates.Count -ne 1) {
    throw "[HEADLESS:$LogName] Expected one built EXE named $ExecutableName.exe under $outputDirectory; found $($candidates.Count)."
}
$exe = $candidates[0].FullName

# PowerShell Start-Process joins -ArgumentList entries. Quote every value to
# preserve spaces in checkout and artifact paths.
$argumentLine = ($ProgramArguments | ForEach-Object {
    '"' + $_.Replace('"', '\"') + '"'
}) -join ' '
Write-Host "[HEADLESS:$LogName] Starting $exe"
Write-Host "[HEADLESS:$LogName] Timeout: $TimeoutSeconds seconds; logs: $stdoutPath / $stderrPath"
$startParams = @{
    FilePath = $exe
    WorkingDirectory = $projectDirectory
    NoNewWindow = $true
    PassThru = $true
    RedirectStandardOutput = $stdoutPath
    RedirectStandardError = $stderrPath
}
if ($argumentLine) { $startParams.ArgumentList = $argumentLine }
$process = Start-Process @startParams
$timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
if ($timedOut) {
    Write-Host "[HEADLESS:$LogName] Timed out: terminating PID $($process.Id) and children."
    try { $process.Kill($true) } catch { Write-Warning "Terminating probe: $_" }
    [void]$process.WaitForExit(5000)
}
$process.Refresh()

# Always print the captured output even on failure, and save it as an artifact.
foreach ($file in @($stdoutPath, $stderrPath)) {
    Write-Host "[HEADLESS:$LogName] ===== $(Split-Path -Leaf $file) ====="
    if (Test-Path -PathType Leaf $file) {
        Get-Content -Path $file | ForEach-Object { Write-Host $_ }
    } else {
        Write-Host '(no output file produced)'
    }
}
if ($timedOut) {
    throw "[HEADLESS:$LogName] Test exceeded $TimeoutSeconds seconds. Check stdout for the 'Main entered' marker; if absent, it blocked before Main (runtime bootstrap or DLL loading)."
}
if ($process.ExitCode -ne 0) {
    throw "[HEADLESS:$LogName] Test exited with code $($process.ExitCode); see diagnostics above."
}
Write-Host "[HEADLESS:$LogName] PASS"
exit 0
