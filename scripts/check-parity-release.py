#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def load_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8"))


def require_text(path: Path, needles: list[str], failures: list[str], evidence: list[dict]):
    if not path.exists():
        failures.append(f"missing source: {path.relative_to(ROOT)}")
        return
    text = path.read_text(encoding="utf-8", errors="replace")
    missing = [n for n in needles if n not in text]
    evidence.append({
        "kind": "source_guard",
        "path": str(path.relative_to(ROOT)),
        "required": needles,
        "missing": missing,
        "pass": not missing,
    })
    for n in missing:
        failures.append(f"{path.relative_to(ROOT)}: missing invariant token {n!r}")


def check_budget(result_path: Path, budget_path: Path, failures: list[str], evidence: list[dict]):
    if not result_path.exists():
        failures.append(f"missing benchmark artifact: {result_path.relative_to(ROOT)}")
        evidence.append({"kind": "benchmark", "path": str(result_path.relative_to(ROOT)), "pass": False, "reason": "missing"})
        return
    result = load_json(result_path)
    budget = load_json(budget_path)
    local_failures: list[str] = []
    if result.get("suite") != budget.get("suite"):
        local_failures.append(f"suite mismatch {result.get('suite')!r} != {budget.get('suite')!r}")
    by_name = {entry["name"]: entry for entry in result.get("results", [])}
    for name, rule in budget.get("budgets", {}).items():
        entry = by_name.get(name)
        if entry is None:
            local_failures.append(f"missing result {name}")
            continue
        counts = entry.get("counts", {})
        for metric, maximum in rule.get("countsMax", {}).items():
            actual = counts.get(metric)
            if actual is None:
                local_failures.append(f"{name}: missing {metric}")
            elif actual > maximum:
                local_failures.append(f"{name}: {metric}={actual} > {maximum}")
        for metric, minimum in rule.get("countsMin", {}).items():
            actual = counts.get(metric)
            if actual is None:
                local_failures.append(f"{name}: missing {metric}")
            elif actual < minimum:
                local_failures.append(f"{name}: {metric}={actual} < {minimum}")
    evidence.append({
        "kind": "benchmark",
        "path": str(result_path.relative_to(ROOT)),
        "budget": str(budget_path.relative_to(ROOT)),
        "profile": result.get("profile"),
        "pass": not local_failures,
        "failures": local_failures,
    })
    failures.extend(f"{result_path.name}: {f}" for f in local_failures)


def run_abi_check(failures: list[str], evidence: list[dict]):
    script = ROOT / "scripts" / "check-abi-contract.py"
    proc = subprocess.run([sys.executable, str(script)], cwd=ROOT, capture_output=True, text=True)
    evidence.append({
        "kind": "abi",
        "pass": proc.returncode == 0,
        "stdout": proc.stdout.strip(),
        "stderr": proc.stderr.strip(),
    })
    if proc.returncode != 0:
        failures.append("ABI contract check failed")


def main() -> int:
    parser = argparse.ArgumentParser(description="Monaco 0.57 source, ABI and benchmark release validation")
    parser.add_argument("--windows-renderer", type=Path, default=ROOT / "artifacts/benchmarks/renderer-quick.json")
    parser.add_argument("--allow-missing-windows", action="store_true", help="Allow local/non-Windows runs to emit a conditional report")
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/release/parity-report.json")
    args = parser.parse_args()

    comparison = load_json(ROOT / "reference/monaco-0.57.0-parity-gate.json")
    failures: list[str] = []
    warnings: list[str] = []
    evidence: list[dict] = []

    # Pinned baseline itself is part of the gate.
    if comparison.get("monacoEditorVersion") != "0.57.0":
        failures.append("reference baseline is not Monaco 0.57.0")
    if comparison.get("vscodeRef") != "6a598d4a13031703d483d103c1d934a36ad27971":
        failures.append("reference baseline VS Code commit drifted")

    # Architecture/source invariants corresponding to the pinned upstream anchors.
    require_text(ROOT / "src/MonacoNative.Core/include/MonacoNative/Core/ViewModel.h", [
        "modelLineViewLines_", "SpliceablePrefixSum", "suffixViewLineMetadataRewrites", "projectionSlotMoves"
    ], failures, evidence)
    require_text(ROOT / "src/MonacoNative.Core/src/ViewModel.cpp", [
        "modelLineViewCounts_.Splice", "lastProjectionUpdateStats_", "projectionSlotMoves"
    ], failures, evidence)
    require_text(ROOT / "src/MonacoNative.Core/include/MonacoNative/Core/Commands.h", [
        "firstChordIndex_"
    ], failures, evidence)
    require_text(ROOT / "src/MonacoNative.Core/src/Commands.cpp", [
        "firstChordIndex_.find(sequence.front())"
    ], failures, evidence)
    require_text(ROOT / "src/MonacoNative.Renderer.WinUI3/LineRangeIndex.cs", [
        "LineRangeIndex<T>"
    ], failures, evidence)
    require_text(ROOT / "src/MonacoNative.Renderer.WinUI3/MinimapRowCache.cs", [
        "MinimapRowCache"
    ], failures, evidence)
    require_text(ROOT / "src/MonacoNative.Renderer.WinUI3/MonacoEditor.xaml.cs", [
        "NativeTsfInputController"
    ], failures, evidence)

    # The removed TextBox proxy must not return as a production runtime path.
    renderer = (ROOT / "src/MonacoNative.Renderer.WinUI3/MonacoEditor.xaml.cs").read_text(encoding="utf-8")
    if "CreateTextBoxInputController" in renderer or "ImeProxyTextBox" in renderer:
        failures.append("legacy TextBox proxy runtime path reappeared")

    # The Windows connection-point HRESULTs must use portable SDK-compatible values.
    # Keep only the numeric CONNECT_E fallbacks in the final source.
    tsf_store = (ROOT / "src/MonacoNative.Input.Native/src/TsfTextStore.cpp").read_text(encoding="utf-8")
    if "#include <olectl.h>" in tsf_store:
        failures.append("TsfTextStore.cpp still includes <olectl.h> (known IsEqualGUID duplicate warning source)")
    if "0x80040200L" not in tsf_store or "0x80040201L" not in tsf_store:
        failures.append("CONNECT_E compatibility fallbacks are missing")

    # Core benchmark evidence: current quick/full artifacts are required.
    check_budget(ROOT / "artifacts/benchmarks/core-quick.json", ROOT / "reference/performance-budgets-core.json", failures, evidence)
    check_budget(ROOT / "artifacts/benchmarks/core-full.json", ROOT / "reference/performance-budgets-core.json", failures, evidence)

    # Windows renderer evidence is strict on release runners but conditional locally.
    windows_path = args.windows_renderer if args.windows_renderer.is_absolute() else ROOT / args.windows_renderer
    if windows_path.exists():
        check_budget(windows_path, ROOT / "reference/performance-budgets-renderer.json", failures, evidence)
        windows_status = "pass" if not any(e.get("path") == str(windows_path.relative_to(ROOT)) and not e.get("pass") for e in evidence) else "fail"
    elif args.allow_missing_windows:
        windows_status = "pending"
        warnings.append("Windows renderer benchmark artifact is not available in this environment; final certification remains conditional until the Windows gate runs.")
        evidence.append({"kind": "benchmark", "path": str(windows_path.relative_to(ROOT)), "pass": None, "reason": "missing-windows-allowed"})
    else:
        windows_status = "missing"
        failures.append(f"missing Windows renderer evidence: {windows_path.relative_to(ROOT)}")

    run_abi_check(failures, evidence)

    status = "FAIL" if failures else ("CONDITIONAL" if warnings else "PASS")
    report = {
        "schemaVersion": 1,
        "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "status": status,
        "monacoEditorVersion": comparison["monacoEditorVersion"],
        "vscodeRef": comparison["vscodeRef"],
        "windowsRendererEvidence": windows_status,
        "failures": failures,
        "warnings": warnings,
        "acceptedPlatformDifferences": comparison.get("acceptedPlatformDifferences", []),
        "evidence": evidence,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    print(f"Monaco parity release validation: {status}")
    print(f"Report: {args.output}")
    for warning in warnings:
        print(f"INFO: {warning}")
    for failure in failures:
        print(f"FAIL: {failure}", file=sys.stderr)
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
