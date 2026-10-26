#!/usr/bin/env python3
"""Pinned GitHub source dependencies. Build-time only; never imported by the editor.

No floating branches, forced resets, shell command interpolation, or silent runtime
server downloads. A ZIP checkout needs no parent .git directory. Fetched repositories
are ordinary independent Git worktrees under external/, not fake ZIP submodules.
"""
from __future__ import annotations
import argparse
import json
import os
from pathlib import Path
import platform
import re
import shlex
import shutil
import stat
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
LOCK = ROOT / "reference/upstream-sources.lock.json"
CONFIG = ROOT / "artifacts/upstream/MonacoNative.Upstream.props"


def run(args: list[str], *, cwd: Path | None = None, capture: bool = False,
        dry_run: bool = False) -> str:
    print("+ " + shlex.join([str(a) for a in args]), flush=True)
    if dry_run:
        return ""
    env = dict(os.environ, GIT_TERMINAL_PROMPT="0")
    result = subprocess.run(args, cwd=cwd, env=env, check=True, text=True,
                            encoding="utf-8", errors="replace",
                            stdout=subprocess.PIPE if capture else None,
                            stderr=subprocess.PIPE if capture else None)
    return result.stdout.strip() if capture else ""


def read_lock(path: Path = LOCK) -> dict:
    data = json.loads(path.read_text(encoding="utf-8"))
    if data.get("format") != 1 or not isinstance(data.get("dependencies"), dict):
        raise ValueError("Unsupported upstream lock format.")
    for name, dep in data["dependencies"].items():
        if not re.fullmatch(r"[a-z][a-z0-9_-]*", name):
            raise ValueError("Invalid dependency name.")
        if not re.fullmatch(r"[0-9a-f]{40}", dep.get("revision", "")):
            raise ValueError(f"{name}: a full immutable Git commit is required.")
        if not re.fullmatch(r"https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\.git", dep.get("repository", "")):
            raise ValueError(f"{name}: only explicit HTTPS GitHub repositories are accepted.")
        relative = Path(dep["directory"])
        if relative.is_absolute() or ".." in relative.parts or relative.parts[0] != "external":
            raise ValueError(f"{name}: checkout must be inside external/.")
    return data["dependencies"]


def checkout_path(dep: dict, root: Path = ROOT) -> Path:
    path = root / dep["directory"]
    if not path.resolve().is_relative_to(root.resolve()):
        raise ValueError("Dependency path escapes the repository through a symlink.")
    return path


def verify_checkout(dep: dict, path: Path, *, clean: bool = True) -> str:
    if not (path / ".git").exists():
        raise RuntimeError(f"Missing source checkout: {path}. Run scripts/upstream.py fetch first.")
    top = Path(run(["git", "-C", str(path), "rev-parse", "--show-toplevel"], capture=True))
    if top.resolve() != path.resolve():
        raise RuntimeError(f"{path} is not an independent dependency worktree.")
    origin = run(["git", "-C", str(path), "remote", "get-url", "origin"], capture=True)
    if origin != dep["repository"]:
        raise RuntimeError(f"Unexpected origin for {path}; refusing to replace it.")
    revision = run(["git", "-C", str(path), "rev-parse", "HEAD"], capture=True)
    if revision != dep["revision"]:
        raise RuntimeError(f"{path}: HEAD is {revision}, expected {dep['revision']}. No reset was performed.")
    if clean and run(["git", "-C", str(path), "status", "--porcelain", "--untracked-files=normal"], capture=True):
        raise RuntimeError(f"{path}: local modifications exist; refusing a pinned build. No files were reset.")
    return revision


def fetch_checkout(dep: dict, path: Path) -> None:
    if path.exists():
        verify_checkout(dep, path)
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    staging = Path(tempfile.mkdtemp(prefix=path.name + ".fetch-", dir=path.parent))
    try:
        run(["git", "init", str(staging)])
        run(["git", "-C", str(staging), "config", "core.autocrlf", "false"])
        run(["git", "-C", str(staging), "config", "core.longpaths", "true"])
        run(["git", "-C", str(staging), "remote", "add", "origin", dep["repository"]])
        run(["git", "-C", str(staging), "fetch", "--depth=1", "--no-tags", "origin", dep["revision"]])
        run(["git", "-C", str(staging), "checkout", "--detach", "FETCH_HEAD"])
        verify_checkout(dep, staging)
        if path.exists():
            raise RuntimeError(f"Dependency destination appeared while fetching: {path}")
        staging.rename(path)
    finally:
        # Only this invocation's fresh staging folder is removed. Never clean a user's tree.
        if staging.exists():
            def remove_readonly(function, path, exc_info):
                # Git objects may be read-only on Windows; only touch our fresh staging directory.
                os.chmod(path, stat.S_IWRITE | stat.S_IREAD)
                function(path)
            shutil.rmtree(staging, onerror=remove_readonly)


def llvm_commands(source: Path, build: Path, install: Path, jobs: int,
                  configuration: str, generator: str = "Ninja") -> list[list[str]]:
    configure = ["cmake", "-S", str(source / "llvm"), "-B", str(build), "-G", generator,
        f"-DCMAKE_BUILD_TYPE={configuration}", f"-DCMAKE_INSTALL_PREFIX={install}",
        "-DLLVM_ENABLE_PROJECTS=clang", "-DLLVM_TARGETS_TO_BUILD=Native",
        "-DLLVM_INCLUDE_TESTS=OFF", "-DLLVM_INCLUDE_EXAMPLES=OFF", "-DLLVM_INCLUDE_BENCHMARKS=OFF",
        "-DLLVM_ENABLE_ZLIB=OFF", "-DLLVM_ENABLE_ZSTD=OFF", "-DLLVM_ENABLE_LIBXML2=OFF",
        "-DLLVM_ENABLE_TERMINFO=OFF", "-DLLVM_ENABLE_LIBEDIT=OFF", "-DLLVM_ENABLE_FFI=OFF",
        "-DLLVM_BUILD_LLVM_DYLIB=OFF", "-DLLVM_LINK_LLVM_DYLIB=OFF",
        "-DLLVM_ENABLE_PIC=ON", "-DLIBCLANG_BUILD_STATIC=OFF", "-DCMAKE_POSITION_INDEPENDENT_CODE=ON"]
    return [configure,
        ["cmake", "--build", str(build), "--config", configuration, "--target", "libclang", "clang-resource-headers", "--parallel", str(jobs)],
        ["cmake", "--install", str(build), "--config", configuration, "--component", "libclang"],
        ["cmake", "--install", str(build), "--config", configuration, "--component", "clang-resource-headers"]]


def verify_native_architecture(target: str) -> None:
    host = {"amd64": "x64", "x86_64": "x64", "arm64": "ARM64", "aarch64": "ARM64"}.get(platform.machine().lower())
    compiler = os.environ.get("VSCMD_ARG_TGT_ARCH", "").lower()
    actual = {"x64": "x64", "amd64": "x64", "arm64": "ARM64"}.get(compiler, host)
    if actual != target:
        raise RuntimeError(f"Requested {target}, current native compiler environment is {actual}. Use the matching VS developer prompt/toolchain; cross-compilation is not guessed.")


def write_llvm_receipt(dep: dict, source: Path, install: Path, arch: str) -> None:
    libraries = [install / "bin/libclang.dll", install / "lib/libclang.dylib", install / "lib/libclang.so"]
    library = next((path for path in libraries if path.exists()), None)
    resources = sorted(path for path in (install / "lib/clang").glob("*") if (path / "include/stddef.h").exists())
    if library is None or len(resources) != 1:
        raise RuntimeError("LLVM install is incomplete or ambiguous: need libclang and one Clang resource directory.")
    # Preserve the upstream licenses in the generated runtime bundle.
    license_dir = install / "share/licenses"
    license_dir.mkdir(parents=True, exist_ok=True)
    for name, relative in (("LLVM", "llvm/LICENSE.TXT"), ("Clang", "clang/LICENSE.TXT")):
        shutil.copyfile(source / relative, license_dir / (name + "-LICENSE.TXT"))
    receipt = {"format": 1, "component": "llvm", "repository": dep["repository"], "revision": dep["revision"],
               "version": dep["version"], "architecture": arch, "library": library.relative_to(install).as_posix(),
               "resourceDirectory": resources[0].relative_to(install).as_posix()}
    (install / "monaco-upstream.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")


def build_llvm(dep: dict, args: argparse.Namespace) -> None:
    source = checkout_path(dep)
    if not args.dry_run:
        verify_checkout(dep, source)
        verify_native_architecture(args.platform)
    build = ROOT / f"artifacts/upstream/llvm/build-{args.platform}-{args.configuration}"
    install = ROOT / f"artifacts/upstream/llvm/install-{args.platform}"
    for command in llvm_commands(source, build, install, args.jobs, args.configuration, args.generator):
        run(command, dry_run=args.dry_run)
    if not args.dry_run:
        write_llvm_receipt(dep, source, install, args.platform)
        print(f"Built pinned in-process Clang runtime: {install}")


def roslyn_commands(dep: dict, source: Path, *, windows: bool, configuration: str) -> list[str]:
    project = source / dep["project"]
    if windows:
        return ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(source / "eng/build.ps1"),
                "-restore", "-build", "-configuration", configuration, "-msbuildEngine", "dotnet", f"/p:Projects={project}"]
    return ["bash", str(source / "build.sh"), "--restore", "--build", "--configuration", configuration,
            "--solution", dep["project"]]


def enable_roslyn(dep: dict, *, dry_run: bool = False) -> None:
    # Source-mode ProjectReferences were removed in the precompiled dependency edition.
    # Fail explicitly instead of emitting an ignored props file and claiming success.
    raise RuntimeError("Roslyn source mode was removed. This project always restores the fixed 4.11.0 NuGet package. No source checkout/build is needed.")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    fetch = sub.add_parser("fetch", help="Fetch exact locked commits (never updates an existing checkout).")
    fetch.add_argument("components", nargs="+", choices=["llvm", "roslyn", "typescript"])
    sub.add_parser("status", help="Inspect lock and local checkouts without network access.")
    llvm = sub.add_parser("build-llvm", help="Compile libclang and resource headers from the LLVM monorepo.")
    llvm.add_argument("--platform", choices=["x64", "ARM64"], default="x64")
    llvm.add_argument("--configuration", choices=["Release", "Debug"], default="Release")
    llvm.add_argument("--generator", choices=["Ninja", "Ninja Multi-Config"], default="Ninja")
    llvm.add_argument("--jobs", type=int, default=2)
    llvm.add_argument("--dry-run", action="store_true")
    roslyn = sub.add_parser("build-roslyn", help="Use Roslyn's pinned build scripts/SDK, not a language server.")
    roslyn.add_argument("--configuration", choices=["Release", "Debug"], default="Release")
    roslyn.add_argument("--dry-run", action="store_true")
    enable = sub.add_parser("enable-roslyn-source", help="Deprecated: explains the fixed precompiled NuGet dependency.")
    enable.add_argument("--dry-run", action="store_true")
    sub.add_parser("disable-roslyn-source", help="Remove only the generated source-mode setting.")
    args = parser.parse_args()
    deps = read_lock()
    if args.command == "fetch":
        for name in dict.fromkeys(args.components):
            fetch_checkout(deps[name], checkout_path(deps[name]))
    elif args.command == "status":
        for name, dep in deps.items():
            path = checkout_path(dep)
            if not path.exists():
                print(f"{name}: NOT FETCHED | {dep['revision']} | {dep['directory']}")
            else:
                try:
                    verify_checkout(dep, path)
                    print(f"{name}: PINNED AND CLEAN | {dep['revision']}")
                except RuntimeError as error:
                    print(f"{name}: MISMATCH | {error}")
                    return 1
    elif args.command == "build-llvm":
        if args.jobs < 1:
            parser.error("--jobs must be positive")
        build_llvm(deps["llvm"], args)
    elif args.command == "build-roslyn":
        source = checkout_path(deps["roslyn"])
        if not args.dry_run:
            verify_checkout(deps["roslyn"], source)
        run(roslyn_commands(deps["roslyn"], source, windows=os.name == "nt", configuration=args.configuration),
            cwd=source if not args.dry_run else None, dry_run=args.dry_run)
    elif args.command == "enable-roslyn-source":
        enable_roslyn(deps["roslyn"], dry_run=args.dry_run)
    elif args.command == "disable-roslyn-source":
        if CONFIG.exists():
            if "Generated by scripts/upstream.py" not in CONFIG.read_text(encoding="utf-8"):
                raise RuntimeError("Refusing to remove an unrecognized configuration file.")
            CONFIG.unlink()
        print("Returned to the unchanged pinned NuGet in-process mode.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, RuntimeError, subprocess.CalledProcessError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        if isinstance(error, subprocess.CalledProcessError) and error.stderr:
            print(error.stderr, file=sys.stderr)
        raise SystemExit(1)
