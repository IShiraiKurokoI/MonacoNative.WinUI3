#!/usr/bin/env python3
"""Guard the JSON serializer and Python budget checker against schema drift."""
from pathlib import Path
import json
import subprocess
import sys
import tempfile

root = Path(__file__).resolve().parents[1]
code = (root / "benchmarks/RendererBenchmarks/Program.cs").read_text(encoding="utf-8")
assert "PropertyNamingPolicy = JsonNamingPolicy.CamelCase" in code, (
    "Renderer benchmark JSON must serialize camelCase properties for the Python checker"
)
runner = (root / "scripts/run-renderer-benchmarks.ps1").read_text(encoding="utf-8")
assert 'IsPathRooted($Output)' in runner and 'GetFullPath($Output)' in runner, (
    "Renderer output must be absolute before passing it to the child EXE"
)
checker = root / "scripts/check-benchmark-results.py"
for kind, source in (
    ("renderer", "reference/performance-budgets-renderer.json"),
    ("core", "reference/performance-budgets-core.json"),
):
    budget = json.loads((root / source).read_text(encoding="utf-8"))
    names = budget.get("budgets", {})
    assert budget.get("suite") and names, f"Missing suite or budget list in {source}"
    entries = []
    for name, limits in names.items():
        counts = dict.fromkeys(limits.get("countsMax", {}), 0)
        counts.update(limits.get("countsMin", {}))
        entries.append({"name": name, "counts": counts})
    with tempfile.TemporaryDirectory() as folder:
        result_path = Path(folder) / "result.json"
        result = {"schemaVersion": 1, "suite": budget["suite"], "profile": "quick", "results": entries}
        result_path.write_text(json.dumps(result), encoding="utf-8")
        cmd = [sys.executable, str(checker), str(result_path), "--budgets", str(root / source)]
        valid = subprocess.run(cmd, capture_output=True, text=True, check=False)
        if valid.returncode != 0:
            raise SystemExit(f"{kind}: correct result rejected: {valid.stderr}")
        result.pop("suite")
        result_path.write_text(json.dumps(result), encoding="utf-8")
        invalid = subprocess.run(cmd, capture_output=True, text=True, check=False)
        if invalid.returncode == 0:
            raise SystemExit(f"{kind}: missing suite was incorrectly accepted")
    print(f"{kind} JSON schema/budget contract PASS ({len(names)} checks)")
