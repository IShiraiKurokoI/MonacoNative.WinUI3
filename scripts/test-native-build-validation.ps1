[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateSet('x64', 'ARM64')][string]$Platform = 'x64',
    [switch]$CoreOnly
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'tests/NativeValidationMsbuild/Validation.proj'
$dll = Join-Path $root "artifacts/native/$Platform/$Configuration/MonacoNative.Core.Native.dll"
if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
    throw "Build Core.Native before running MSBuild validation: $dll"
}

function Invoke-Validation([string]$Case, [string]$File, [string]$Architecture,
                            [string]$Export, [bool]$ExpectSuccess) {
    # The real RoslynCodeTaskFactory target is executed for positive and negative cases.
    $params = @('msbuild', $project, '-nologo', '-t:ValidateFixture', '-v:quiet',
        "-p:BinaryPath=$File", "-p:TargetPlatform=$Architecture", "-p:RequiredExport=$Export")
    & dotnet @params
    $passed = $LASTEXITCODE -eq 0
    if ($passed -ne $ExpectSuccess) {
        throw "MSBuild validation case '$Case' unexpected result (success=$passed; expected=$ExpectSuccess)."
    }
    Write-Host "MSBuild native validation PASS: $Case (expectedSuccess=$ExpectSuccess)"
}
$otherArch = if ($Platform -eq 'x64') { 'ARM64' } else { 'x64' }
Invoke-Validation 'real linked DLL and required export' $dll $Platform 'mn_abi_version' $true
Invoke-Validation 'wrong PE machine type' $dll $otherArch 'mn_abi_version' $false
Invoke-Validation 'missing export' $dll $Platform 'mn_export_that_does_not_exist' $false
Invoke-Validation 'missing DLL' (Join-Path $root 'artifacts/nonexistent/missing.dll') $Platform 'mn_abi_version' $false

if ($CoreOnly) {
    Write-Host "Native MSBuild linked-DLL validation PASS ($Platform, no runtime or publish claim)."
    # Negative cases intentionally leave a nonzero native exit code. Clear it
    # only after every expected result was checked so the parent acceptance stage passes.
    $global:LASTEXITCODE = 0
    return
}

# Verify the real AfterTargets=Publish checks also run for a target that performs
# NO build. Stage the real compiled DLL and the real NuGet-deployed libclang in a
# separate directory, then intentionally remove/mismatch assets.
$sampleOut = Join-Path $root "src/MonacoNative.SampleApp/bin/$Platform/$Configuration"
$clangMatches = @(Get-ChildItem -Path $sampleOut -Recurse -Filter 'libclang.dll' -File -ErrorAction SilentlyContinue)
if ($clangMatches.Count -lt 1) { throw "Sample build has no deployed libclang.dll below $sampleOut" }
$staged = Join-Path $root 'artifacts/native-validation/publish-fixture'
if (Test-Path -LiteralPath $staged) { Remove-Item -LiteralPath $staged -Recurse -Force }
New-Item -Path $staged -ItemType Directory -Force | Out-Null
$stagedCore = Join-Path $staged 'MonacoNative.Core.Native.dll'
$stagedClang = Join-Path $staged 'libclang.dll'
Copy-Item -LiteralPath $dll -Destination $stagedCore
Copy-Item -LiteralPath $clangMatches[0].FullName -Destination $stagedClang

function Invoke-PublishFixture([string]$Case, [string]$Architecture,
                                [bool]$ExpectSuccess, [bool]$SingleFile = $false) {
    $rid = if ($Architecture -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
    & dotnet msbuild $project -nologo -t:Publish -v:quiet `
        "-p:PublishDir=$staged/" "-p:Platform=$Architecture" "-p:RuntimeIdentifier=$rid" `
        "-p:PublishSingleFile=$($SingleFile.ToString().ToLowerInvariant())"
    $passed = $LASTEXITCODE -eq 0
    if ($passed -ne $ExpectSuccess) {
        throw "No-build publish case '$Case' unexpected result (success=$passed; expected=$ExpectSuccess)."
    }
    Write-Host "MSBuild publish validation PASS: $Case (expectedSuccess=$ExpectSuccess)"
}
try {
    Invoke-PublishFixture 'no-build publish with real DLLs' $Platform $true
    Invoke-PublishFixture 'no-build publish rejects wrong architecture' $otherArch $false
    Invoke-PublishFixture 'single-file publish explicitly unsupported' $Platform $false $true
    Remove-Item -LiteralPath $stagedCore -Force
    Invoke-PublishFixture 'no-build publish rejects missing Core.Native' $Platform $false
    Copy-Item -LiteralPath $dll -Destination $stagedCore
    Remove-Item -LiteralPath $stagedClang -Force
    Invoke-PublishFixture 'no-build publish rejects missing libclang' $Platform $false
}
finally {
    Remove-Item -LiteralPath $staged -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host 'Native MSBuild build/publish validation PASS (real DLLs; negative cases must fail).'
# The final fixture intentionally fails. Its exit code is not the suite result.
$global:LASTEXITCODE = 0
