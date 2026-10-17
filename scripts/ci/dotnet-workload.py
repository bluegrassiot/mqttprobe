#!/usr/bin/env python3
"""Resolve the active .NET SDK and workload cache identity for GitHub Actions."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import stat
import subprocess
import sys
import time
from pathlib import Path
from typing import Mapping


class WorkloadError(ValueError):
    pass


class InvalidSnapshot(WorkloadError):
    pass


ASSET_DIRS = ("packs", "sdk-manifests", "metadata/workloads", "template-packs", "library-packs")
INVENTORY_NAME = "inventory.json"
SNAPSHOT_NAME = "dotnet-workload-snapshot"


def run_dotnet(*arguments: str) -> str:
    try:
        result = subprocess.run(
            ["dotnet", *arguments], check=True, capture_output=True, text=True
        )
    except (OSError, subprocess.CalledProcessError) as error:
        raise WorkloadError(f"dotnet {' '.join(arguments)} failed") from error
    return result.stdout.strip()


def discover_sdk_root() -> Path:
    active = run_dotnet("--version")
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:[-+][\w.-]+)?", active):
        raise WorkloadError("dotnet --version returned an invalid SDK version")
    listed = run_dotnet("--list-sdks")
    for line in listed.splitlines():
        match = re.fullmatch(r"\s*(\S+)\s+\[(.+)]\s*", line)
        if match and match.group(1) == active:
            sdk_directory = Path(match.group(2)) / active
            if not sdk_directory.is_dir():
                raise WorkloadError(f"Active SDK directory does not exist: {sdk_directory}")
            root = sdk_directory.parent.parent
            if not root.is_dir():
                raise WorkloadError(f"SDK install root does not exist: {root}")
            return root
    raise WorkloadError(f"Active SDK {active} was not found in dotnet --list-sdks")


def cache_key(
    repo_root: Path,
    *,
    runner_os: str,
    runner_arch: str,
    sdk: str,
    workload_version: str,
    workload: str,
    xcode: str,
) -> str:
    files = (
        repo_root / "global.json",
        repo_root / ".github/actions/setup-dotnet-workload/action.yml",
        repo_root / "scripts/ci/dotnet-workload.py",
    )
    digest = hashlib.sha256()
    for path in files:
        if not path.is_file():
            raise WorkloadError(f"Cache identity input is missing: {path}")
        digest.update(path.relative_to(repo_root).as_posix().encode("utf-8"))
        digest.update(b"\0")
        digest.update(path.read_bytes())
        digest.update(b"\0")
    identity = "\0".join(
        (runner_os, runner_arch, sdk, workload_version, workload, xcode, digest.hexdigest())
    )
    return "dotnet-workload-v2-" + hashlib.sha256(identity.encode("utf-8")).hexdigest()


def _inventory_entries(root: Path) -> list[dict[str, object]]:
    entries: list[dict[str, object]] = []
    for asset in ASSET_DIRS:
        base = root / asset
        if base.is_symlink():
            raise WorkloadError(f"Workload asset directory is a symlink: {asset}")
        if not base.is_dir():
            raise WorkloadError(f"Snapshot asset directory is missing: {asset}")
    for current, directories, files in os.walk(root, followlinks=False):
        current_path = Path(current)
        for name in sorted(directories + files):
            path = current_path / name
            relative = path.relative_to(root).as_posix()
            if name == INVENTORY_NAME and path.parent == root:
                continue
            info = path.lstat()
            if stat.S_ISLNK(info.st_mode):
                entries.append({"path": relative, "type": "symlink", "target": os.readlink(path)})
            elif stat.S_ISDIR(info.st_mode):
                entries.append({"path": relative, "type": "directory"})
            elif stat.S_ISREG(info.st_mode):
                entries.append({"path": relative, "type": "file", "size": info.st_size})
            else:
                raise WorkloadError(f"Unsupported workload snapshot entry: {relative}")
    return sorted(entries, key=lambda item: str(item["path"]))


def capture_snapshot(install_root: Path, snapshot_root: Path) -> None:
    _ensure_disjoint(install_root, snapshot_root)
    if snapshot_root.exists() or snapshot_root.is_symlink():
        if snapshot_root.is_symlink() or not snapshot_root.is_dir():
            raise WorkloadError("Snapshot path is not a safe directory")
        shutil.rmtree(snapshot_root)
    snapshot_root.mkdir(parents=True)
    for asset in ASSET_DIRS:
        source = install_root / asset
        destination = snapshot_root / asset
        if source.is_symlink():
            raise WorkloadError(f"Workload asset directory is a symlink: {asset}")
        if source.exists():
            shutil.copytree(source, destination, symlinks=True)
        else:
            destination.mkdir(parents=True)
    entries = _inventory_entries(snapshot_root)
    (snapshot_root / INVENTORY_NAME).write_text(
        json.dumps({"version": 1, "entries": entries}, sort_keys=True) + "\n", encoding="utf-8"
    )


def _validate_inventory(snapshot_root: Path) -> None:
    inventory_path = snapshot_root / INVENTORY_NAME
    if snapshot_root.is_symlink() or inventory_path.is_symlink() or not snapshot_root.is_dir():
        raise InvalidSnapshot("Snapshot root or inventory is not a safe directory/file")
    try:
        data = json.loads(inventory_path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise InvalidSnapshot("Snapshot inventory is missing or unreadable") from error
    if not isinstance(data, dict) or set(data) != {"version", "entries"} or data["version"] != 1 or not isinstance(data["entries"], list):
        raise InvalidSnapshot("Snapshot inventory has an invalid format")
    expected: dict[str, dict[str, object]] = {}
    allowed = set(ASSET_DIRS)
    for entry in data["entries"]:
        if not isinstance(entry, dict) or not isinstance(entry.get("path"), str):
            raise InvalidSnapshot("Snapshot inventory contains an invalid entry")
        path = entry["path"]
        candidate = Path(path)
        allowed_path = any(path == name or path.startswith(name + "/") for name in ASSET_DIRS)
        allowed_parent = path in {"metadata"}
        if candidate.is_absolute() or "\\" in path or any(part in ("", ".", "..") for part in path.split("/")) or not (allowed_path or allowed_parent):
            raise InvalidSnapshot("Snapshot inventory contains an unsafe path")
        if path in expected or path not in allowed and not allowed_parent and not any(path.startswith(name + "/") for name in ASSET_DIRS):
            raise InvalidSnapshot("Snapshot inventory contains a duplicate or unexpected path")
        kind = entry.get("type")
        if kind == "file":
            valid = set(entry) == {"path", "type", "size"} and type(entry.get("size")) is int and entry["size"] >= 0
        elif kind == "directory":
            valid = set(entry) == {"path", "type"}
        elif kind == "symlink":
            valid = set(entry) == {"path", "type", "target"} and isinstance(entry.get("target"), str)
        else:
            valid = False
        if not valid:
            raise InvalidSnapshot("Snapshot inventory contains an invalid entry")
        expected[path] = entry
    try:
        actual = {str(entry["path"]): entry for entry in _inventory_entries(snapshot_root)}
    except (OSError, WorkloadError) as error:
        raise InvalidSnapshot("Snapshot tree is incomplete or unreadable") from error
    if actual != expected:
        raise InvalidSnapshot("Snapshot contents do not match the inventory")
    for path, entry in expected.items():
        if entry["type"] == "file":
            try:
                with (snapshot_root / path).open("rb") as handle:
                    size = 0
                    while chunk := handle.read(1024 * 1024):
                        size += len(chunk)
            except OSError as error:
                raise InvalidSnapshot(f"Snapshot file is unreadable: {path}") from error
            if size != entry["size"]:
                raise InvalidSnapshot(f"Snapshot file size changed: {path}")
    try:
        top = {item.name for item in snapshot_root.iterdir()}
    except OSError as error:
        raise InvalidSnapshot("Snapshot directory is unreadable") from error
    if top != {INVENTORY_NAME, *(Path(name).parts[0] for name in ASSET_DIRS)}:
        raise InvalidSnapshot("Snapshot contains unexpected top-level entries")


def restore_snapshot(snapshot_root: Path, install_root: Path) -> None:
    _ensure_disjoint(install_root, snapshot_root)
    _validate_inventory(snapshot_root)
    try:
        for asset in ASSET_DIRS:
            source = snapshot_root / asset
            destination = install_root / asset
            destination.parent.mkdir(parents=True, exist_ok=True)

            def ignore_identical_symlinks(source_dir: str, names: list[str]) -> set[str]:
                source_path = Path(source_dir)
                destination_dir = destination / source_path.relative_to(source)
                return {
                    name
                    for name in names
                    if (source_path / name).is_symlink()
                    and (destination_dir / name).is_symlink()
                    and os.readlink(source_path / name) == os.readlink(destination_dir / name)
                }

            shutil.copytree(
                source,
                destination,
                symlinks=True,
                dirs_exist_ok=True,
                ignore=ignore_identical_symlinks,
            )
    except OSError as error:
        raise WorkloadError("Unable to merge validated workload snapshot into SDK") from error


def _ensure_disjoint(first: Path, second: Path) -> None:
    first_resolved = first.resolve()
    second_resolved = second.resolve()
    if first_resolved == second_resolved or first_resolved in second_resolved.parents or second_resolved in first_resolved.parents:
        raise WorkloadError("Snapshot and SDK paths must not overlap")


def discard_snapshot(snapshot_root: Path, runner_temp: Path) -> None:
    expected = runner_temp.resolve() / SNAPSHOT_NAME
    if snapshot_root.resolve() != expected or snapshot_root.is_symlink():
        raise WorkloadError("Refusing to discard a directory not owned by this action")
    if snapshot_root.exists():
        if not snapshot_root.is_dir():
            raise WorkloadError("Refusing to discard a non-directory snapshot")
        shutil.rmtree(snapshot_root)


def outputs(repo_root: Path, workload_version: str, workload: str, xcode: str, env: Mapping[str, str]) -> dict[str, str]:
    root = discover_sdk_root()
    sdk = run_dotnet("--version")
    try:
        pinned = json.loads((repo_root / "global.json").read_text(encoding="utf-8"))["sdk"]["version"]
    except (OSError, UnicodeError, json.JSONDecodeError, KeyError, TypeError) as error:
        raise WorkloadError("global.json does not contain a valid SDK version pin") from error
    if sdk != pinned:
        raise WorkloadError(f"Selected .NET SDK {sdk} does not match global.json pin {pinned}")
    key = cache_key(
        repo_root,
        runner_os=env.get("RUNNER_OS", ""),
        runner_arch=env.get("RUNNER_ARCH", ""),
        sdk=sdk,
        workload_version=workload_version,
        workload=workload,
        xcode=xcode,
    )
    runner_temp = Path(env.get("RUNNER_TEMP", os.environ.get("RUNNER_TEMP", ".")))
    snapshot = runner_temp / SNAPSHOT_NAME
    return {
        "cache-key": key,
        "install-root": str(root),
        "cache-path": str(snapshot),
    }


def main(argv: list[str] | None = None, env: Mapping[str, str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--workload-version")
    parser.add_argument("--workload")
    parser.add_argument("--xcode-version", default="none")
    parser.add_argument("--github-output", default=os.environ.get("GITHUB_OUTPUT"))
    parser.add_argument("--snapshot-operation", choices=("restore", "capture", "discard"))
    parser.add_argument("--snapshot-root", type=Path)
    parser.add_argument("--install-root", type=Path)
    args = parser.parse_args(argv)
    environment = env or os.environ
    try:
        if args.snapshot_operation:
            if args.snapshot_root is None:
                raise WorkloadError("Snapshot operation requires --snapshot-root")
            runner_temp = Path(environment.get("RUNNER_TEMP", ".")).resolve()
            owned_snapshot = runner_temp / SNAPSHOT_NAME
            if args.snapshot_root.resolve() != owned_snapshot or args.snapshot_root.is_symlink():
                raise WorkloadError("Snapshot operations require the action-owned RUNNER_TEMP directory")
            if args.snapshot_operation == "restore":
                started = time.perf_counter()
                if args.install_root is None:
                    raise WorkloadError("Restore requires --install-root")
                restore_snapshot(args.snapshot_root, args.install_root)
                print(f"Workload snapshot validation and merge elapsed: {time.perf_counter() - started:.2f}s")
            elif args.snapshot_operation == "capture":
                started = time.perf_counter()
                if args.install_root is None:
                    raise WorkloadError("Capture requires --install-root")
                capture_snapshot(args.install_root, args.snapshot_root)
                print(f"Workload snapshot capture elapsed: {time.perf_counter() - started:.2f}s")
            else:
                discard_snapshot(args.snapshot_root, runner_temp)
            return 0
        if not args.workload_version or not args.workload:
            raise WorkloadError("--workload-version and --workload are required")
        values = outputs(args.repo_root.resolve(), args.workload_version, args.workload, args.xcode_version, environment)
        if args.github_output:
            with Path(args.github_output).open("a", encoding="utf-8", newline="\n") as handle:
                handle.write(f"cache-key={values['cache-key']}\n")
                handle.write(f"install-root={values['install-root']}\n")
                handle.write(f"cache-path<<WORKLOAD_PATHS\n{values['cache-path']}\nWORKLOAD_PATHS\n")
        else:
            print(json.dumps(values))
    except WorkloadError as error:
        level = "warning" if isinstance(error, InvalidSnapshot) else "error"
        print(f"::{level}::{error}", file=sys.stderr)
        return 2 if isinstance(error, InvalidSnapshot) else 1
    except OSError as error:
        print(f"::error::Snapshot operation failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
