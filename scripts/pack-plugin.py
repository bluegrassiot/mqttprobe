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
SKIP_SUFFIXES = (".b64", ".zip")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=pathlib.Path, help=f"folder containing {MANIFEST_NAME}")
    parser.add_argument("-o", "--output", type=pathlib.Path, help="output .zip path")
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

    output = args.output or pathlib.Path(f"{manifest['id']}-{manifest['version']}.zip")

    with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(args.source.rglob("*")):
            if path.is_dir() or path.suffix.lower() in SKIP_SUFFIXES:
                continue
            archive.write(path, path.relative_to(args.source).as_posix())

    print(f"wrote {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
