#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROFILE="${1:-quick}"
OUT="${2:-$ROOT/artifacts/benchmarks/core-${PROFILE}.json}"
BUILD="${MONACONATIVE_BENCH_BUILD_DIR:-$ROOT/build-benchmarks}"

mkdir -p "$(dirname "$OUT")"
cmake -S "$ROOT" -B "$BUILD" -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DMONACONATIVE_BUILD_TESTS=OFF \
  -DMONACONATIVE_BUILD_NATIVE_ABI=OFF \
  -DMONACONATIVE_BUILD_BENCHMARKS=ON \
  -DMONACONATIVE_WARNINGS_AS_ERRORS=ON
cmake --build "$BUILD" --target MonacoNative.Core.Benchmarks --parallel
"$BUILD/benchmarks/CoreBenchmarks/MonacoNative.Core.Benchmarks" --profile "$PROFILE" --output "$OUT"
python3 "$ROOT/scripts/check-benchmark-results.py" "$OUT" --budgets "$ROOT/reference/performance-budgets-core.json"
echo "Benchmark result: $OUT"
