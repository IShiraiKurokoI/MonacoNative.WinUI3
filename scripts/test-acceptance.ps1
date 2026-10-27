# One public Windows desktop acceptance entry point. Existing granular scripts remain
# reusable in CI and diagnostics; no tests are silently skipped in release mode.
[CmdletBinding()]
param(
    [ValidateSet('Automated', 'Interactive', 'Release')]
    [string]$Mode = 'Automated',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$RunPackages,
    [string]$PackageVersion = '0.1.0',
    [switch]$SkipUiBuild
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$reportPath = Join-Path $root 'artifacts/release/acceptance-report.json'
New-Item -Path (Split-Path -Parent $reportPath) -ItemType Directory -Force | Out-Null
$script:stages = [System.Collections.Generic.List[object]]::new()
$script:result = 0
$script:failure = $null
$strict = $Mode -eq 'Release'
if ($PackageVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$') {
    throw 'PackageVersion must be a normal NuGet SemVer (e.g. 0.1.0 or 0.1.0-rc.1).'
}
if ($strict) {
    if ($Configuration -ne 'Release') { throw 'Final release validation must use -Configuration Release.' }
    $RunPackages = $true
}

function Invoke-AcceptanceStage([string]$Name, [scriptblock]$Action) {
    $startedAt = [DateTime]::UtcNow
    Write-Host "[ACCEPTANCE] START: $Name"
    try {
        $global:LASTEXITCODE = 0
        & $Action
        if ($LASTEXITCODE -ne 0) { throw "$Name exited with code $LASTEXITCODE" }
        $script:stages.Add([pscustomobject]@{
            name=$Name; status='PASS'; startedAtUtc=$startedAt.ToString('o');
            finishedAtUtc=[DateTime]::UtcNow.ToString('o')
        })
        Write-Host "[ACCEPTANCE] PASS: $Name"
    }
    catch {
        $script:stages.Add([pscustomobject]@{
            name=$Name; status='FAIL'; startedAtUtc=$startedAt.ToString('o');
            finishedAtUtc=[DateTime]::UtcNow.ToString('o'); message=$_.Exception.Message
        })
        throw
    }
}

try {
    Invoke-AcceptanceStage 'portable source and release-tooling checks' {
        python (Join-Path $root 'scripts/check-source.py')
    }
    if ($strict) {
        Invoke-AcceptanceStage 'approved Windows x64 editor, IME and accessibility evidence' {
            $approval = Join-Path $root "reference/windows-acceptance/approved/$PackageVersion.json"
            python (Join-Path $root 'scripts/windows-acceptance.py') verify `
                --version $PackageVersion --file $approval
        }
        Invoke-AcceptanceStage 'approved Windows renderer goldens exist at 96/120/144/192 DPI' {
            foreach ($dpi in @(96, 120, 144, 192)) {
                $baseline = Join-Path $root "reference/render-goldens/windows-x64-${dpi}dpi.json"
                if (-not (Test-Path -LiteralPath $baseline -PathType Leaf)) {
                    throw "Required Windows $dpi DPI golden baseline is missing: $baseline"
                }
            }
        }
    }
    if ($Mode -eq 'Interactive') {
        Invoke-AcceptanceStage 'unlocked desktop UI Automation' {
            & (Join-Path $root 'scripts/test-ui.ps1') -Configuration $Configuration -SkipBuild:$SkipUiBuild
        }
        Invoke-AcceptanceStage 'generate unapproved manual acceptance template' {
            python (Join-Path $root 'scripts/windows-acceptance.py') template `
                --version $PackageVersion `
                --output (Join-Path $root 'artifacts/release/windows-manual.json')
        }
        Write-Warning 'UIA smoke is not a full IME/Narrator validation. Record actual manual results before release.'
    }
    else {
        Invoke-AcceptanceStage 'layered Windows Build, Core, Services, WinUI, Integration and Packages' {
            & (Join-Path $root 'scripts/test.ps1') -Layer All -Configuration $Configuration `
                -RequireGoldenBaseline:$strict -RunPackages:$RunPackages -PackageVersion $PackageVersion `
                -ReportPath 'artifacts/release/layer-report.json'
        }

    }
}
catch {
    $script:result = 1
    $script:failure = $_.Exception.Message
    Write-Warning "[ACCEPTANCE] FAIL: $($script:failure)"
}
finally {
    $baselinesComplete = @(@(96, 120, 144, 192) | Where-Object {
        -not (Test-Path -LiteralPath (Join-Path $root "reference/render-goldens/windows-x64-${_}dpi.json") -PathType Leaf)
    }).Count -eq 0
    $baselineState = if ($Mode -eq 'Interactive') { 'NOT_RUN' }
                     elseif ($baselinesComplete -and $script:result -eq 0) { 'CHECKED_BY_RENDERER' }
                     else { 'PENDING_APPROVAL' }
    $manualState = if ($strict -and $script:result -eq 0) { 'PASS' } else { 'NOT_APPROVED' }
    $overall = if ($script:result -ne 0) { 'FAIL' }
               elseif ($strict) { 'PASS' } else { 'CONDITIONAL' }
    $report = [ordered]@{
        schemaVersion = 1
        mode = $Mode
        configuration = $Configuration
        packageVersion = $PackageVersion
        generatedAtUtc = [DateTime]::UtcNow.ToString('o')
        status = $overall
        releaseEligible = $overall -eq 'PASS'
        rendererBaseline = $baselineState
        manualWindowsAcceptance = $manualState
        arm64RuntimeExecution = 'NOT_RUN_ON_X64_HOST'
        stages = @($script:stages.ToArray())
        failure = $script:failure
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8
    Write-Host "[ACCEPTANCE] $overall | Report: $reportPath"
}
exit $script:result
