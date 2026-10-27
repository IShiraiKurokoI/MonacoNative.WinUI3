#!/usr/bin/env python3
"""Generate and verify human Windows desktop acceptance evidence.

No test is auto-marked PASS. A release only trusts the acceptance record when its
source-tree digest matches, every required case was recorded, and the reviewer and
Windows environment are identified. This script cannot conduct a real IME test.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MATRICES = (
    ("editor", "reference/windows-editor-interactive-matrix.json"),
    ("ime", "reference/ime-interactive-matrix.json"),
    ("accessibility", "reference/accessibility-interactive-matrix.json"),
)
SOURCE_DIRS = ("src", "tests", "scripts", "packaging", "reference", ".github")
EXCLUDED_PARTS = {"__pycache__", "obj", "bin", "approved"}


def source_fingerprint(root: Path = ROOT) -> str:
    """Stable digest of versioned code, tooling, and acceptance definitions.

    Excludes approval records to avoid a self-referential commit. Excludes
    generated output and editor image assets (the renderer baseline is included).
    """
    result = hashlib.sha256()
    # Prefer *tracked* source in a git checkout. Generated WinUI files, NuGet
    # restore outputs and scratch artifacts must not invalidate human review.
    tracked = subprocess.run(
        ["git", "-C", str(root), "ls-files", "-z", "--", *SOURCE_DIRS],
        capture_output=True, check=False,
    ) if (root / ".git").exists() else None
    if tracked is not None and tracked.returncode == 0 and tracked.stdout:
        paths = [root / Path(raw.decode("utf-8")) for raw in tracked.stdout.split(b"\0") if raw]
    else:
        paths: list[Path] = []
        for name in SOURCE_DIRS:
            directory = root / name
            if directory.is_dir():
                paths.extend(directory.rglob("*"))
    for path in sorted((p for p in paths if p.is_file()), key=lambda p: p.as_posix()):
        relative = path.relative_to(root)
        if any(part in EXCLUDED_PARTS or part.startswith("build-") for part in relative.parts[:-1]):
            continue
        if path.suffix in {".pyc", ".exe", ".dll", ".obj", ".pdb"}:
            continue
        result.update(relative.as_posix().encode("utf-8") + b"\0")
        result.update(hashlib.sha256(path.read_bytes()).digest())
    return result.hexdigest()


def required_cases(root: Path = ROOT) -> dict[str, str]:
    cases: dict[str, str] = {}
    for group, relpath in MATRICES:
        matrix = json.loads((root / relpath).read_text(encoding="utf-8"))
        for case in matrix["cases"]:
            identifier = f"{group}/{case['id']}"
            if identifier in cases:
                raise ValueError(f"duplicate Windows acceptance case: {identifier}")
            cases[identifier] = case.get("scenario", case.get("expect", ""))
    return cases


def template(version: str, root: Path = ROOT) -> dict:
    cases = required_cases(root)
    return {
        "schemaVersion": 1,
        "releaseVersion": version,
        "sourceSha256": source_fingerprint(root),
        "tester": "",
        "reviewedAtUtc": "",
        "environment": {
            "os": "Windows",
            "osBuild": "",
            "architecture": "x64",
            "displayDpi": "",
            "inputMethods": "",
            "notes": "",
        },
        "cases": [{"id": key, "status": "NOT_RUN", "notes": ""} for key in cases],
    }


def verify(data: dict, version: str, root: Path = ROOT) -> list[str]:
    problems = []
    if data.get("schemaVersion") != 1:
        problems.append("unknown acceptance evidence schema")
    if data.get("releaseVersion") != version:
        problems.append("evidence version does not match the release")
    if data.get("sourceSha256") != source_fingerprint(root):
        problems.append("source digest mismatch: source changed after manual acceptance")
    if not (isinstance(data.get("tester"), str) and data["tester"].strip()):
        problems.append("tester name is required")
    reviewed = data.get("reviewedAtUtc", "")
    try:
        parsed = datetime.fromisoformat(reviewed.replace("Z", "+00:00"))
        if parsed.tzinfo is None or parsed.astimezone(timezone.utc) > datetime.now(timezone.utc):
            raise ValueError()
    except (ValueError, AttributeError, TypeError):
        problems.append("reviewedAtUtc must be a real, timezone-aware past timestamp")
    env = data.get("environment") or {}
    if env.get("os") != "Windows" or env.get("architecture") != "x64":
        problems.append("manual acceptance must come from Windows x64")
    for field in ("osBuild", "displayDpi", "inputMethods"):
        if not isinstance(env.get(field), str) or not env[field].strip():
            problems.append(f"missing environment.{field}")
    required = required_cases(root)
    entries = data.get("cases", [])
    if not isinstance(entries, list):
        return problems + ["cases must be a list"]
    seen: set[str] = set()
    for item in entries:
        if not isinstance(item, dict):
            problems.append("invalid case entry")
            continue
        identifier = item.get("id")
        if not isinstance(identifier, str):
            problems.append("case ID must be a string")
            continue
        if identifier in seen:
            problems.append(f"duplicate case: {identifier}")
        seen.add(identifier)
        if identifier not in required:
            problems.append(f"unknown/retired case: {identifier}")
        if item.get("status") != "PASS":
            problems.append(f"case {identifier} not passed (status={item.get('status')!r})")
    for identifier in required:
        if identifier not in seen:
            problems.append(f"missing case: {identifier}")
    return problems


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    actions = parser.add_subparsers(dest="action", required=True)
    generate = actions.add_parser("template", help="Emit NOT_RUN evidence for a human tester to fill in")
    generate.add_argument("--version", required=True)
    generate.add_argument("--output", type=Path, required=True)
    check = actions.add_parser("verify", help="Fail unless all manual cases and source fingerprint are approved")
    check.add_argument("--version", required=True)
    check.add_argument("--file", type=Path, required=True)
    args = parser.parse_args(argv)
    try:
        if args.action == "template":
            report = template(args.version)
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
            print(f"NOT_RUN acceptance template: {args.output} ({len(report['cases'])} cases)")
            return 0
        report = json.loads(args.file.read_text(encoding="utf-8"))
        failures = verify(report, args.version)
    except (OSError, ValueError, KeyError, TypeError) as exc:
        print(f"Windows acceptance FAIL: {exc}", file=sys.stderr)
        return 1
    if failures:
        for failure in failures:
            print(f"Windows acceptance FAIL: {failure}", file=sys.stderr)
        return 1
    print(f"Windows acceptance PASS: {len(report['cases'])} real-manual cases, matching source fingerprint")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
