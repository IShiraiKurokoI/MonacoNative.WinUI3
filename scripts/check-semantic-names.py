#!/usr/bin/env python3
"""Keep CI and code labeled by tested behavior rather than historical release numbers."""
from pathlib import Path
import re
import sys

root = Path(__file__).resolve().parents[1]
# Reject numbered development eras and obsolete version-specific ABI names.
numbered_development = re.compile(r"\b(?:stage|batch)[\s_-]*\d", flags=re.IGNORECASE)
retired_abi_names = re.compile(r"(?:\bfrozen[\s_-]+abi\b|\blegacy[\s_-]+abi\b|\babi[-_]v\d+\b)", flags=re.IGNORECASE)
numbered_guard_path = re.compile(r"(?:^|/)check-(?:v\d+[a-z]?|native-input\d+[a-z]?)[\w-]*\.py$", flags=re.IGNORECASE)
ignore_dirs = {".git", "bin", "obj", "__pycache__", ".vs", ".pytest_cache"}
ignore_prefixes = ("build", "out")
files = 0
errors = []
for path in root.rglob("*"):
    rel = path.relative_to(root)
    if any(part in ignore_dirs or part.startswith(ignore_prefixes) for part in rel.parts[:-1]):
        continue
    rel_text = rel.as_posix()
    if numbered_development.search(rel_text):
        errors.append(f"numbered development label in path: {rel}")
    if retired_abi_names.search(rel_text):
        errors.append(f"obsolete ABI label in path: {rel}")
    if numbered_guard_path.search(rel_text):
        errors.append(f"numbered guard script in path: {rel}")
    if not path.is_file():
        continue
    if path.suffix.lower() not in {
        ".cs", ".cpp", ".h", ".hpp", ".ps1", ".py", ".sh", ".yml", ".yaml",
        ".json", ".md", ".xml", ".csproj", ".vcxproj", ".targets", ".nuspec", ".txt",
    }:
        continue
    try:
        content = path.read_text(encoding="utf-8")
    except UnicodeError:
        continue
    files += 1
    for line, text in enumerate(content.splitlines(), 1):
        if numbered_development.search(text):
            errors.append(f"{rel}:{line}: numbered development label")
        if retired_abi_names.search(text):
            errors.append(f"{rel}:{line}: obsolete ABI label")

if errors:
    for item in errors[:30]:
        print(item, file=sys.stderr)
    if len(errors) > 30:
        print(f"...and {len(errors) - 30} more", file=sys.stderr)
    raise SystemExit(1)
print(f"Semantic naming check PASS: {files} source, test, CI and reference files")
