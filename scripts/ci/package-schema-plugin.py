#!/usr/bin/env python3
"""Build the ChirpStack schema plugin package attached to a release.

The package name and version come from the sample manifest, so the archive
name follows a version bump without a second version constant. The zip itself
is produced by scripts/packaging/pack-plugin.py, which is also the command
users run locally and which mirrors the installer's allowlist.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PACKER = ROOT / "scripts" / "packaging" / "pack-plugin.py"
DEFAULT_SOURCE = ROOT / "samples" / "protobuf" / "chirpstack"
MANIFEST_NAME = "mqttprobe-plugin.json"
SCHEMA_MANIFEST_NAME = "protobuf-schemas.json"
ALLOWED_SUFFIXES = (".proto", ".json", ".md", ".txt")


def read_manifest(source: Path) -> dict:
    manifest_path = source / MANIFEST_NAME
    if not manifest_path.is_file():
        raise ValueError(f"{manifest_path} not found")

    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    missing = [field for field in ("id", "version", "kind") if not manifest.get(field)]
    if missing:
        raise ValueError(f"{MANIFEST_NAME} is missing {', '.join(missing)}")
    if manifest["kind"] != "protobuf-schemas":
        raise ValueError(f"{MANIFEST_NAME} kind must be protobuf-schemas")

    return manifest


def package_name(manifest: dict) -> str:
    return f"{manifest['id']}-{manifest['version']}.zip"


def validate_archive(path: Path) -> list[str]:
    problems: list[str] = []
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        if MANIFEST_NAME not in names:
            problems.append(f"{path.name} does not contain {MANIFEST_NAME} at its root")
        if SCHEMA_MANIFEST_NAME not in names:
            problems.append(
                f"{path.name} does not contain {SCHEMA_MANIFEST_NAME} at its root"
            )
        for name in names:
            if Path(name).suffix.lower() not in ALLOWED_SUFFIXES:
                problems.append(
                    f"{path.name} contains a file type schema packages do not allow: "
                    f"{name}"
                )
    return problems


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=DEFAULT_SOURCE)
    parser.add_argument("--output-dir", type=Path, default=ROOT)
    args = parser.parse_args(argv)

    try:
        manifest = read_manifest(args.source)
    except (OSError, ValueError, json.JSONDecodeError) as ex:
        print(f"error: {ex}", file=sys.stderr)
        return 1

    args.output_dir.mkdir(parents=True, exist_ok=True)
    output = args.output_dir / package_name(manifest)

    try:
        result = subprocess.run(
            [sys.executable, str(PACKER), str(args.source), "-o", str(output)],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
        )
    except OSError as ex:
        print(f"error: could not run {PACKER.name}: {ex}", file=sys.stderr)
        return 1

    if result.returncode != 0:
        if result.stdout:
            print(result.stdout, end="", file=sys.stderr)
        if result.stderr:
            print(result.stderr, end="", file=sys.stderr)
        print(
            f"error: {PACKER.name} failed with exit code {result.returncode}",
            file=sys.stderr,
        )
        return 1

    problems = validate_archive(output)
    if problems:
        for problem in problems:
            print(f"error: {problem}", file=sys.stderr)
        return 1

    print(f"wrote {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
