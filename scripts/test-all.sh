#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if command -v dotnet >/dev/null 2>&1; then
  dotnet run --project "$ROOT/tests/DocumentTests/MonacoNative.Document.Tests.csproj" -c Release
  dotnet run --project "$ROOT/tests/RenameLogicTests/MonacoNative.RenameLogic.Tests.csproj" -c Release
  dotnet run --project "$ROOT/tests/InputTests/MonacoNative.Input.Tests.csproj" -c Release
  dotnet run --project "$ROOT/tests/AccessibilityTests/MonacoNative.Accessibility.Tests.csproj" -c Release
  dotnet run --project "$ROOT/tests/LanguageTests/MonacoNative.Language.Tests.csproj" -c Release
  dotnet run --project "$ROOT/tests/LspTests/MonacoNative.Lsp.Tests.csproj" -c Release
  dotnet run --project "$ROOT/tests/RendererLogicTests/MonacoNative.RendererLogic.Tests.csproj" -c Release
else
  echo "dotnet not found; portable managed tests are covered by CI and Windows test-all." >&2
fi


rm -rf "$ROOT/build-release"
cmake -S "$ROOT" -B "$ROOT/build-release" -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DMONACONATIVE_WARNINGS_AS_ERRORS=ON
cmake --build "$ROOT/build-release" --parallel
ctest --test-dir "$ROOT/build-release" --output-on-failure --output-junit "$ROOT/build-release/ctest-results.xml"

if command -v dotnet >/dev/null 2>&1; then
  MONACONATIVE_NATIVE_LIBRARY="$ROOT/build-release/src/MonacoNative.Core.Native/libMonacoNative.Core.Native.so" \
    dotnet run --project "$ROOT/tests/DocumentModelTests/MonacoNative.DocumentModel.Tests.csproj" -c Release
fi

if command -v clang++ >/dev/null 2>&1; then
  rm -rf "$ROOT/build-sanitize"
  CC="${CC:-clang}" CXX="${CXX:-clang++}" cmake -S "$ROOT" -B "$ROOT/build-sanitize" -G Ninja \
    -DCMAKE_BUILD_TYPE=Debug \
    -DMONACONATIVE_ENABLE_SANITIZERS=ON \
    -DMONACONATIVE_WARNINGS_AS_ERRORS=ON
  cmake --build "$ROOT/build-sanitize" --parallel
  ASAN_OPTIONS=detect_leaks=1:strict_string_checks=1 \
  UBSAN_OPTIONS=print_stacktrace=1:halt_on_error=1 \
    ctest --test-dir "$ROOT/build-sanitize" --output-on-failure --output-junit "$ROOT/build-sanitize/ctest-results.xml"
fi
