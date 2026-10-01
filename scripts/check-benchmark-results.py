#!/usr/bin/env python3
import argparse
import json
import sys
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser(description="Check deterministic MonacoNative benchmark work-count budgets.")
    parser.add_argument("result", type=Path)
    parser.add_argument("--budgets", type=Path, default=Path("reference/performance-budgets-core.json"))
    args = parser.parse_args()

    result = json.loads(args.result.read_text(encoding="utf-8"))
    budgets = json.loads(args.budgets.read_text(encoding="utf-8"))
    if result.get("suite") != budgets.get("suite"):
        raise SystemExit(f"suite mismatch: result={result.get('suite')!r} budgets={budgets.get('suite')!r}")

    by_name = {entry["name"]: entry for entry in result.get("results", [])}
    failures: list[str] = []
    for name, rule in budgets.get("budgets", {}).items():
        entry = by_name.get(name)
        if entry is None:
            failures.append(f"missing benchmark result: {name}")
            continue
        counts = entry.get("counts", {})
        for metric, maximum in rule.get("countsMax", {}).items():
            actual = counts.get(metric)
            if actual is None:
                failures.append(f"{name}: missing count {metric}")
            elif actual > maximum:
                failures.append(f"{name}: {metric}={actual} exceeds {maximum}")
        for metric, minimum in rule.get("countsMin", {}).items():
            actual = counts.get(metric)
            if actual is None:
                failures.append(f"{name}: missing count {metric}")
            elif actual < minimum:
                failures.append(f"{name}: {metric}={actual} is below {minimum}")

    if failures:
        print("Benchmark budget check FAILED", file=sys.stderr)
        for failure in failures:
            print(f" - {failure}", file=sys.stderr)
        return 1

    print("Benchmark budget check PASS")
    for name, rule in budgets.get("budgets", {}).items():
        counts = by_name[name]["counts"]
        parts = [f"{metric}={counts[metric]} <= {maximum}" for metric, maximum in rule.get("countsMax", {}).items()]
        parts.extend(f"{metric}={counts[metric]} >= {minimum}" for metric, minimum in rule.get("countsMin", {}).items())
        print(f" - {name}: {', '.join(parts)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
