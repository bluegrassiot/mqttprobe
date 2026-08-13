#!/usr/bin/env python3
"""Pack a plugin folder into an MQTT Probe plugin package (.zip)."""

import argparse
import json
import pathlib
import sys
import zipfile

MANIFEST_NAME = "mqttprobe-plugin.json"
REQUIRED_FIELDS = ("id", "name", "version", "kind")
KINDS = ("protobuf-schemas", "assembly")

# Mirrors PluginArchiveValidator's allowlist, so a package this script produces cannot be
# rejected on file type. Build output is skipped outright: sweeping bin/ would bundle the
# host's own MqttProbe.UI.dll alongside the plugin.
SHARED_SUFFIXES = (".proto", ".json", ".md", ".txt")
ASSEMBLY_SUFFIXES = (".dll", ".pdb")
SKIP_DIRS = frozenset({"bin", "obj"})


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=pathlib.Path, help=f"folder containing {MANIFEST_NAME}")
    parser.add_argument("-o", "--output", type=pathlib.Path, help="output .zip path")
    parser.add_argument(
        "--dll",
        type=pathlib.Path,
        action="append",
        default=[],
        help="assembly to include; the first is renamed to <id>.dll (repeatable)",
    )
    args = parser.parse_args()

    manifest_path = args.source / MANIFEST_NAME
    if not manifest_path.is_file():
        print(f"error: {manifest_path} not found", file=sys.stderr)
        return 1

    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))

    missing = [field for field in REQUIRED_FIELDS if not manifest.get(field)]
    if missing:
        print(f"error: manifest is missing {', '.join(missing)}", file=sys.stderr)
        return 1

    if manifest["kind"] not in KINDS:
        print(f"error: kind must be one of {', '.join(KINDS)}", file=sys.stderr)
        return 1

    for dll in args.dll:
        if not dll.is_file():
            print(f"error: {dll} not found", file=sys.stderr)
            return 1

    if manifest["kind"] == "assembly" and not args.dll:
        print(f"error: kind '{manifest['kind']}' needs at least one --dll", file=sys.stderr)
        return 1

    if args.dll and manifest["kind"] != "assembly":
        print(f"error: --dll requires kind 'assembly'", file=sys.stderr)
        return 1

    output = args.output or pathlib.Path(f"{manifest['id']}-{manifest['version']}.zip")

    allowed = SHARED_SUFFIXES
    if manifest["kind"] == "assembly":
        allowed += ASSEMBLY_SUFFIXES

    with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(args.source.rglob("*")):
            relative = path.relative_to(args.source)
            if path.is_dir() or path.suffix.lower() not in allowed:
                continue
            if SKIP_DIRS.intersection(relative.parts):
                continue
            archive.write(path, relative.as_posix())

        # The loader resolves the primary assembly by the manifest id, and ids are
        # lowercase-only, so a build output like CustomDemoPlugin.dll would never be
        # found on a case-sensitive filesystem unless it is renamed here.
        for index, dll in enumerate(args.dll):
            name = f"{manifest['id']}.dll" if index == 0 else dll.name
            archive.write(dll, name)
            if index == 0 and dll.name != name:
                print(f"renamed {dll.name} -> {name}")

    print(f"wrote {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
