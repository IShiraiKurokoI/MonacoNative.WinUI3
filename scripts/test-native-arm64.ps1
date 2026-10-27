# Execute native CTest, the managed ABI tests and real PE validation on an
# ARM64 Windows runner. Cross-compiling ARM64 binaries is not a substitute.
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$ReportPath = 'artifacts/tests/arm64-native.json'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$architecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
if ($env:OS -ne 'Windows_NT' -or $architecture -ne 'Arm64') {
    throw "Native ARM64 tests must execute on Windows ARM64, not via emulation (current: $env:OS/$architecture)."
}
if (-not [IO.Path]::IsPathRooted($ReportPath)) { $ReportPath = Join-Path $root $ReportPath }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $ReportPath) | Out-Null
$build = Join-Path $root 'build-arm64-native'
$started = [DateTime]::UtcNow
$status = 'FAIL'
$failure = $null
$tests = 'NOT_RUN'
$managed = 'NOT_RUN'
$language = 'NOT_RUN'
$binary = 'NOT_RUN'
try {
    & cmake -S $root -B $build -G 'Visual Studio 18 2026' -A ARM64 -DMONACONATIVE_WARNINGS_AS_ERRORS=ON
    if ($LASTEXITCODE -ne 0) { throw 'ARM64 CMake configuration failed.' }
    & cmake --build $build --config $Configuration --parallel
    if ($LASTEXITCODE -ne 0) { throw 'ARM64 CMake compilation failed.' }
    & ctest --test-dir $build --output-on-failure -C $Configuration --output-junit (Join-Path $build 'ctest-arm64.xml')
    if ($LASTEXITCODE -ne 0) { throw 'Native ARM64 CTest failed.' }
    $tests = 'PASS'

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path $vswhere)) { throw 'vswhere.exe is required for the native ARM64 MSBuild artifact.' }
    $msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if (-not $msbuild) { throw 'MSBuild.exe not found.' }
    & $msbuild (Join-Path $root 'src/MonacoNative.Core.Native/MonacoNative.Core.Native.vcxproj') /restore /m `
        "/p:Configuration=$Configuration" /p:Platform=ARM64 /v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Native ARM64 MSBuild compilation failed.' }
    & (Join-Path $root 'scripts/test-native-build-validation.ps1') -Configuration $Configuration -Platform ARM64 -CoreOnly
    if ($LASTEXITCODE -ne 0) { throw 'ARM64 PE validation failed.' }
    $binary = 'PASS'

    & dotnet run --project (Join-Path $root 'tests/ManagedTests/MonacoNative.Managed.Tests.csproj') `
        -c $Configuration -p:Platform=ARM64
    if ($LASTEXITCODE -ne 0) { throw 'Managed ARM64 ABI execution failed.' }
    $managed = 'PASS'
    $dll = Join-Path $root "artifacts/native/ARM64/$Configuration/MonacoNative.Core.Native.dll"
    & (Join-Path $root 'scripts/test-language-integration.ps1') `
        -Configuration $Configuration -Platform ARM64 -NativeLibrary $dll
    if ($LASTEXITCODE -ne 0) { throw 'Strict ARM64 Core.Native/libclang language integration failed.' }
    $language = 'PASS'
    $status = 'PASS'
    Write-Host 'ARM64 native CTest, managed ABI, language integration, and PE validation PASS.'
}
catch {
    $failure = $_.Exception.Message
    Write-Warning $failure
}
finally {
    [ordered]@{
        schemaVersion = 1; status = $status; architecture = $architecture;
        configuration = $Configuration; startedAtUtc = $started.ToString('o');
        finishedAtUtc = [DateTime]::UtcNow.ToString('o'); ctest = $tests;
        managedAbi = $managed; languageIntegration = $language; nativeBinaryValidation = $binary;
        interactiveWinUI = 'NOT_RUN'; failure = $failure
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ReportPath -Encoding utf8
}
if ($status -ne 'PASS') { exit 1 }
