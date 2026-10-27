#!/usr/bin/env python3
"""Single portable preflight for source/tooling and release evidence rules."""
from __future__ import annotations
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CHECKS = (
    "check-semantic-names.py",
    "check-sample-xaml-resources.py",
    "check-demo-ui.py",
    "check-abi-contract.py",
    "check-tsf-input-guards.py",
    "check-tsf-source-guards.py",
    "check-tsf-connection-guards.py",
    "check-winrt-package-metadata.py",
    "check-packaging-platforms.py",
    "check-benchmark-schema.py",
    "check-windows-smoke-guards.py",
    "check-editor-interaction-regressions.py",
    "check-framework-and-test-layers.py",
    "test-upstream.py",
)


def main() -> int:
    failures = []
    for script in CHECKS:
        result = subprocess.run([sys.executable, str(ROOT / "scripts" / script)], cwd=ROOT)
        if result.returncode:
            failures.append(script)
    tooling = subprocess.run([sys.executable, "-m", "unittest", "discover", "-s",
                              "tests/ToolingTests", "-v"], cwd=ROOT)
    if tooling.returncode:
        failures.append("ToolingTests")
    if failures:
        print("source preflight FAIL: " + ", ".join(failures), file=sys.stderr)
        return 1
    print(f"source preflight PASS: {len(CHECKS)} source checks + tooling tests")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
