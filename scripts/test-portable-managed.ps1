param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$SkipLanguageTests
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

dotnet run --project (Join-Path $root 'tests\DocumentTests\MonacoNative.Document.Tests.csproj') -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project (Join-Path $root 'tests\RenameLogicTests\MonacoNative.RenameLogic.Tests.csproj') -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Portable IME/i18n state tests do not require a live input method.
dotnet run --project (Join-Path $root 'tests\InputTests\MonacoNative.Input.Tests.csproj') -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project (Join-Path $root 'tests\AccessibilityTests\MonacoNative.Accessibility.Tests.csproj') -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (-not $SkipLanguageTests) {
    dotnet run --project (Join-Path $root 'tests\LanguageTests\MonacoNative.Language.Tests.csproj') -c $Configuration
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

dotnet run --project (Join-Path $root 'tests\LspTests\MonacoNative.Lsp.Tests.csproj') -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project (Join-Path $root 'tests\RendererLogicTests\MonacoNative.RendererLogic.Tests.csproj') -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host 'Portable managed tests PASS'
