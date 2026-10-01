param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# Software-rendered DirectWrite/Win2D golden probe.
$renderArtifact = Join-Path $root 'artifacts\render-goldens\renderer-current.json'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $renderArtifact) | Out-Null
Write-Host '[Renderer golden] Starting headless Win2D image probe.'
& (Join-Path $root 'scripts\invoke-headless-renderer.ps1') `
    -Project 'tests\RendererGoldenTests\MonacoNative.RendererGolden.Tests.csproj' `
    -ExecutableName 'MonacoNative.RendererGolden.Tests' `
    -Configuration $Configuration -TimeoutSeconds 90 -LogName 'renderer-golden' `
    -ProgramArguments @('--output', $renderArtifact)
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

