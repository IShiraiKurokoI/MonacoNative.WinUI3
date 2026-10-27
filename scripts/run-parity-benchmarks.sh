#!/usr/bin/env bash
# Portable parity evidence only. This is NOT a Windows release certification.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
bash "$ROOT/scripts/run-benchmarks.sh" quick "$ROOT/artifacts/benchmarks/core-quick.json"
bash "$ROOT/scripts/run-benchmarks.sh" full "$ROOT/artifacts/benchmarks/core-full.json"
python3 "$ROOT/scripts/check-parity-release.py" --allow-missing-windows \
  --output "$ROOT/artifacts/release/parity-report.json"
echo 'Portable parity complete (CONDITIONAL; Windows evidence still required).'
