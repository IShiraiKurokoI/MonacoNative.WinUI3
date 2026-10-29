# Functional test layers, ordered by dependency. Building and testing are separate stages.
[CmdletBinding()]
param(
    [ValidateSet('All', 'Build', 'Core', 'Services', 'WinUI', 'Integration', 'Packages')]
    [string]$Layer = 'All',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$RunPackages,
    [switch]$RequireGoldenBaseline,
    [switch]$IncludeUIAutomation,
    [string]$PackageVersion = '0.8.0',
    [string]$ReportPath = 'artifacts/tests/layer-report.json'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$report = if ([IO.Path]::IsPathRooted($ReportPath)) { $ReportPath } else { Join-Path $root $ReportPath }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $report) | Out-Null
$script:results = [System.Collections.Generic.List[object]]::new()
$script:failed = $false
$script:lastFailure = $null
$layers = @('Build', 'Core', 'Services', 'WinUI', 'Integration', 'Packages')
$selected = if ($Layer -eq 'All') { $layers } else { @($Layer) }

function Invoke-Layer([string]$Name, [scriptblock]$Command) {
    $start = [DateTime]::UtcNow
    Write-Host "[TEST:$Name] START"
    try {
        $global:LASTEXITCODE = 0
        & $Command
        if ($LASTEXITCODE -ne 0) { throw "$Name returned exit code $LASTEXITCODE" }
        $script:results.Add([pscustomobject]@{
            name = $Name; status = 'PASS'; startedAtUtc = $start.ToString('o');
            finishedAtUtc = [DateTime]::UtcNow.ToString('o')
        })
        Write-Host "[TEST:$Name] PASS"
    }
    catch {
        $script:results.Add([pscustomobject]@{
            name = $Name; status = 'FAIL'; startedAtUtc = $start.ToString('o');
            finishedAtUtc = [DateTime]::UtcNow.ToString('o'); message = $_.Exception.Message
        })
        throw
    }
}

try {
    foreach ($name in $selected) {
        switch ($name) {
            'Build' {
                Invoke-Layer 'Build' {
                    & (Join-Path $root 'scripts/test-core.ps1') -Configuration $Configuration -BuildOnly
                    if ($LASTEXITCODE -ne 0) { throw 'Core build failed.' }
                    & (Join-Path $root 'scripts/build-windows.ps1') -Configuration $Configuration -Platform x64
                }
            }
            'Core' {
                Invoke-Layer 'Core' {
                    & (Join-Path $root 'scripts/test-core.ps1') -Configuration $Configuration -TestOnly
                    if ($LASTEXITCODE -ne 0) { throw 'Core CTest failed.' }
                    dotnet run --project (Join-Path $root 'tests/ManagedTests/MonacoNative.Managed.Tests.csproj') -c $Configuration -p:Platform=x64
                    if ($LASTEXITCODE -ne 0) { throw 'Managed native ABI tests failed.' }
                    $previousNativeLibrary = $env:MONACONATIVE_NATIVE_LIBRARY
                    try {
                        $env:MONACONATIVE_NATIVE_LIBRARY = (Resolve-Path (Join-Path $root "build-core/tests/NativeAbiTests/$Configuration/MonacoNative.Core.Native.dll")).Path
                        dotnet run --project (Join-Path $root 'tests/DocumentModelTests/MonacoNative.DocumentModel.Tests.csproj') -c $Configuration
                        if ($LASTEXITCODE -ne 0) { throw 'Native document model tests failed.' }
                    }
                    finally { $env:MONACONATIVE_NATIVE_LIBRARY = $previousNativeLibrary }
                    & (Join-Path $root 'scripts/run-benchmarks.ps1') -Profile quick -Output 'artifacts/benchmarks/core-quick.json'
                    if ($LASTEXITCODE -ne 0) { throw 'Core quick performance budget failed.' }
                    & (Join-Path $root 'scripts/run-benchmarks.ps1') -Profile full -Output 'artifacts/benchmarks/core-full.json'
                }
            }
            'Services' {
                Invoke-Layer 'Services' {
                    & (Join-Path $root 'scripts/test-portable-managed.ps1') -Configuration $Configuration -SkipLanguageTests
                    if ($LASTEXITCODE -ne 0) { throw 'Portable services tests failed.' }
                    & (Join-Path $root 'scripts/test-language-integration.ps1') -Configuration $Configuration -Platform x64
                }
            }
            'WinUI' {
                Invoke-Layer 'WinUI' {
                    & (Join-Path $root 'scripts/test-renderer-golden.ps1') -Configuration $Configuration -RequireBaseline:$RequireGoldenBaseline
                    if ($LASTEXITCODE -ne 0) { throw 'Win2D reference rendering failed.' }
                    & (Join-Path $root 'scripts/run-renderer-benchmarks.ps1') -Profile quick `
                        -Output (Join-Path $root 'artifacts/benchmarks/renderer-quick.json')
                    if ($LASTEXITCODE -ne 0) { throw 'WinUI renderer benchmark failed.' }
                    if ($IncludeUIAutomation) {
                        & (Join-Path $root 'scripts/test-ui.ps1') -Configuration $Configuration -SkipBuild
                    }
                }
            }
            'Integration' {
                Invoke-Layer 'Integration' {
                    & (Join-Path $root 'scripts/test-native-build-validation.ps1') -Configuration $Configuration -Platform x64
                    if ($LASTEXITCODE -ne 0) { throw 'Native DLL MSBuild validation failed.' }
                    python (Join-Path $root 'scripts/check-parity-release.py') `
                        --output (Join-Path $root 'artifacts/release/parity-report.json')
                }
            }
            'Packages' {
                if ($Layer -eq 'All' -and -not $RunPackages) {
                    $script:results.Add([pscustomobject]@{
                        name = 'Packages'; status = 'NOT_RUN'; message = 'Pass -RunPackages to build and test NuGet consumers.'
                    })
                    continue
                }
                Invoke-Layer 'Packages' {
                    & (Join-Path $root 'scripts/test-package.ps1') -Configuration $Configuration -Version $PackageVersion
                    if ($LASTEXITCODE -ne 0) { throw 'NuGet consumers failed.' }
                    & (Join-Path $root 'scripts/test-native-build-validation.ps1') `
                        -Configuration $Configuration -Platform ARM64 -CoreOnly
                }
            }
        }
    }
}
catch {
    $script:failed = $true
    $script:lastFailure = $_.Exception.Message
    Write-Error -ErrorAction Continue "[TEST] $($script:lastFailure)"
}
finally {
    # An omitted layer is NOT_RUN, never a passing stage.
    foreach ($name in $layers) {
        if (@($script:results | Where-Object { $_.name -eq $name }).Count -eq 0) {
            $script:results.Add([pscustomobject]@{ name = $name; status = 'NOT_RUN' })
        }
    }
    $ordered = @($layers | ForEach-Object {
        $name = $_
        $script:results | Where-Object { $_.name -eq $name } | Select-Object -First 1
    })
    $overall = if ($script:failed) { 'FAIL' } elseif (@($ordered | Where-Object { $_.status -eq 'NOT_RUN' }).Count -gt 0) { 'PARTIAL' } else { 'PASS' }
    [ordered]@{
        schemaVersion = 1; configuration = $Configuration; selectedLayer = $Layer;
        generatedAtUtc = [DateTime]::UtcNow.ToString('o'); status = $overall;
        stages = $ordered; failure = $script:lastFailure
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $report -Encoding UTF8
    Write-Host "[TEST] $overall | Report: $report"
}
if ($script:failed) { exit 1 }
exit 0
