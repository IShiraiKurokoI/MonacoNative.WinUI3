#!/usr/bin/env python3
"""Offline tests for pinned source restoration/configuration (not LLVM/Roslyn build claims)."""
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("upstream", Path(__file__).with_name("upstream.py"))
upstream = importlib.util.module_from_spec(spec)
spec.loader.exec_module(upstream)


def git(path, *args):
    return subprocess.run(["git", "-C", str(path), *args], check=True, text=True,
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE).stdout.strip()


class UpstreamTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="monaco source test ")
        self.root = Path(self.temporary.name)
        self.origin = self.root / "origin repo"
        self.origin.mkdir()
        git(self.origin, "init")
        git(self.origin, "config", "user.email", "tests@example.invalid")
        git(self.origin, "config", "user.name", "Monaco test fixture")
        (self.origin / "source.cpp").write_text("int fixture;\n")
        (self.origin / ".gitignore").write_text("build/\n")
        git(self.origin, "add", ".")
        git(self.origin, "commit", "-m", "fixture")
        self.revision = git(self.origin, "rev-parse", "HEAD")
        self.dep = {"repository": str(self.origin), "revision": self.revision,
                    "directory": "external/dependency", "version": "test"}
        self.destination = self.root / self.dep["directory"]

    def tearDown(self):
        self.temporary.cleanup()

    def fetch(self):
        with contextlib.redirect_stdout(io.StringIO()):
            upstream.fetch_checkout(self.dep, self.destination)

    def test_fetch_exact_commit_without_parent_repository(self):
        self.fetch()
        self.assertEqual(git(self.destination, "rev-parse", "HEAD"), self.revision)
        self.assertTrue((self.destination / "source.cpp").is_file())
        self.assertFalse((self.root / ".git").exists())

    def test_idempotent_fetch_preserves_ignored_build_cache(self):
        self.fetch()
        (self.destination / "build").mkdir()
        (self.destination / "build/cache").write_text("keep")
        self.fetch()
        self.assertEqual((self.destination / "build/cache").read_text(encoding="utf-8"), "keep")

    def test_modified_source_is_never_reset(self):
        self.fetch()
        (self.destination / "source.cpp").write_text("keep user edit")
        with self.assertRaisesRegex(RuntimeError, "local modifications"):
            self.fetch()
        self.assertEqual((self.destination / "source.cpp").read_text(encoding="utf-8"), "keep user edit")

    def test_untracked_source_is_not_deleted(self):
        self.fetch()
        (self.destination / "mine.cpp").write_text("keep")
        with self.assertRaisesRegex(RuntimeError, "local modifications"):
            self.fetch()
        self.assertTrue((self.destination / "mine.cpp").exists())

    def test_wrong_revision_is_not_replaced(self):
        self.fetch()
        self.dep["revision"] = "a" * 40
        with self.assertRaisesRegex(RuntimeError, "No reset"):
            self.fetch()
        self.assertEqual(git(self.destination, "rev-parse", "HEAD"), self.revision)

    def test_wrong_remote_is_rejected(self):
        self.fetch()
        git(self.destination, "remote", "set-url", "origin", "https://example.invalid/wrong.git")
        with self.assertRaisesRegex(RuntimeError, "Unexpected origin"):
            self.fetch()

    def test_failed_fetch_leaves_no_partial_checkout(self):
        self.dep["revision"] = "f" * 40
        with self.assertRaises(subprocess.CalledProcessError), contextlib.redirect_stdout(io.StringIO()):
            upstream.fetch_checkout(self.dep, self.destination)
        self.assertFalse(self.destination.exists())
        self.assertEqual(list(self.destination.parent.glob("dependency.fetch-*")), [])

    def test_lock_requires_full_revision_and_safe_relative_directory(self):
        lock = json.loads(upstream.LOCK.read_text(encoding="utf-8"))
        path = self.root / "lock.json"
        for key, value in (("revision", "main"), ("directory", "external/../../escape"),
                           ("repository", "ssh://example.invalid/repo")):
            bad = json.loads(json.dumps(lock))
            bad["dependencies"]["llvm"][key] = value
            path.write_text(json.dumps(bad))
            with self.assertRaises(ValueError):
                upstream.read_lock(path)

    @unittest.skipIf(os.name == "nt", "symlink creation may need privileges on Windows")
    def test_symlink_escape_rejected(self):
        outside = self.root / "outside"; outside.mkdir()
        repo = self.root / "repo"; repo.mkdir()
        (repo / "external").symlink_to(outside, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, "symlink"):
            upstream.checkout_path(self.dep, repo)

    def test_llvm_plan_builds_library_and_headers_not_servers(self):
        plan = upstream.llvm_commands(Path("source with spaces"), Path("build"), Path("install"), 2, "Release")
        self.assertIn(str(Path("source with spaces") / "llvm"), plan[0])
        self.assertIn("-DLLVM_ENABLE_PROJECTS=clang", plan[0])
        self.assertIn("libclang", plan[1])
        self.assertIn("clang-resource-headers", plan[1])
        self.assertIn("--component", plan[-1])
        self.assertNotIn("clangd", " ".join(word for command in plan for word in command))

    def test_removed_source_mode_cannot_silently_reenable(self):
        for dry_run in (True, False):
            with self.assertRaisesRegex(RuntimeError, "source mode was removed"):
                upstream.enable_roslyn({}, dry_run=dry_run)

    def test_roslyn_plan_uses_upstream_build_and_source_project(self):
        dep = upstream.read_lock()["roslyn"]
        windows = upstream.roslyn_commands(dep, Path("source"), windows=True, configuration="Release")
        unix = upstream.roslyn_commands(dep, Path("source"), windows=False, configuration="Release")
        self.assertIn("-msbuildEngine", windows)
        self.assertTrue(any(arg.startswith("/p:Projects=") for arg in windows))
        self.assertIn("--solution", unix)
        self.assertFalse(any("LanguageServer" in arg or "--stdio" in arg for arg in windows + unix))

    def test_receipt_contains_build_pin_resources_and_licenses(self):
        source = self.root / "llvm-source"
        install = self.root / "install"
        for name in ("llvm", "clang"):
            (source / name).mkdir(parents=True)
            (source / name / "LICENSE.TXT").write_text("fixture license")
        (install / "bin").mkdir(parents=True)
        (install / "bin/libclang.dll").write_bytes(b"fixture, not executable")
        (install / "lib/clang/17/include").mkdir(parents=True)
        (install / "lib/clang/17/include/stddef.h").write_text("fixture header")
        upstream.write_llvm_receipt(self.dep, source, install, "x64")
        receipt = json.loads((install / "monaco-upstream.json").read_text(encoding="utf-8"))
        self.assertEqual(receipt["revision"], self.revision)
        self.assertEqual(receipt["resourceDirectory"], "lib/clang/17")
        self.assertEqual(len(list((install / "share/licenses").glob("*"))), 2)


if __name__ == "__main__":
    unittest.main(verbosity=2)
