[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$RequireBaseline
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dpis = @(96, 120, 144, 192)
foreach ($dpi in $dpis) {
    $renderArtifact = Join-Path $root "artifacts/render-goldens/renderer-current-${dpi}dpi.json"
    $pngArtifact = Join-Path $root "artifacts/render-goldens/renderer-current-${dpi}dpi.png"
    $baseline = Join-Path $root "reference/render-goldens/windows-x64-${dpi}dpi.json"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $renderArtifact) | Out-Null
    Remove-Item -LiteralPath $renderArtifact, $pngArtifact -ErrorAction SilentlyContinue
    $argsForProgram = @('--output', $renderArtifact, '--png', $pngArtifact, '--dpi', "$dpi")
    if ($RequireBaseline -and -not (Test-Path -LiteralPath $baseline -PathType Leaf)) {
        throw "Approved renderer baseline missing: $baseline. Review the actual Windows PNG and commit the matching JSON first."
    }
    if (Test-Path -LiteralPath $baseline -PathType Leaf) {
        $argsForProgram += @('--baseline', $baseline)
    } else {
        Write-Warning "No approved $dpi DPI golden. Only repeatability is checked; the release gate remains pending."
    }
    Write-Host "[Renderer golden] Running Win2D probe at $dpi DPI."
    & (Join-Path $root 'scripts/invoke-headless-renderer.ps1') `
        -Project 'tests/RendererGoldenTests/MonacoNative.RendererGolden.Tests.csproj' `
        -ExecutableName 'MonacoNative.RendererGolden.Tests' `
        -Configuration $Configuration -TimeoutSeconds 90 -LogName "renderer-golden-${dpi}dpi" `
        -ProgramArguments $argsForProgram
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host "Golden evidence: $renderArtifact and $pngArtifact"
}
