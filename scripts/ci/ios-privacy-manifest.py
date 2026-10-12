#!/usr/bin/env python3
"""Validates a PrivacyInfo.xcprivacy inside a built iOS app bundle.

App Store Connect rejects an upload without one (ITMS-91053 for a missing
manifest, ITMS-91061 for missing required-reason declarations), so this fails
closed on absence, on an unparseable plist, and on a missing top-level key.
"""

from __future__ import annotations

import argparse
import plistlib
import sys
from pathlib import Path

# Apple requires all three top-level keys to be present in a privacy manifest.
REQUIRED_KEYS = (
    "NSPrivacyTracking",
    "NSPrivacyTrackingDomains",
    "NSPrivacyCollectedDataTypes",
)

# Apple's five required-reason API categories.
KNOWN_API_TYPES = frozenset(
    {
        "NSPrivacyAccessedAPICategoryFileTimestamp",
        "NSPrivacyAccessedAPICategorySystemBootTime",
        "NSPrivacyAccessedAPICategoryDiskSpace",
        "NSPrivacyAccessedAPICategoryActiveKeyboards",
        "NSPrivacyAccessedAPICategoryUserDefaults",
    }
)


class ManifestError(ValueError):
    pass


def validate(path: Path) -> dict[str, int]:
    if not path.is_file():
        raise ManifestError(
            f"No PrivacyInfo.xcprivacy at {path}. App Store Connect rejects the upload with "
            "ITMS-91053. The manifest belongs at Platforms/iOS/Resources in this repo."
        )
    try:
        with path.open("rb") as handle:
            manifest = plistlib.load(handle)
    except Exception as error:  # plistlib raises several unrelated types
        raise ManifestError(f"{path} is not a readable privacy manifest plist: {error}") from error
    if not isinstance(manifest, dict):
        raise ManifestError(f"{path} is not a privacy manifest dictionary.")

    missing = [key for key in REQUIRED_KEYS if key not in manifest]
    if missing:
        raise ManifestError(
            f"{path.name} is missing required key(s): {', '.join(missing)}. Apple rejects a "
            "manifest that omits any of the three top-level keys."
        )

    declared = manifest.get("NSPrivacyAccessedAPITypes", [])
    if not isinstance(declared, list):
        raise ManifestError("NSPrivacyAccessedAPITypes must be an array.")

    unknown = [
        entry.get("NSPrivacyAccessedAPIType", "?")
        for entry in declared
        if isinstance(entry, dict)
        and entry.get("NSPrivacyAccessedAPIType") not in KNOWN_API_TYPES
    ]
    if unknown:
        print(f"::warning::{path.name} declares unrecognised API types: {', '.join(unknown)}")

    return {"declared_api_types": len(declared)}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("manifest", type=Path, help="Path to PrivacyInfo.xcprivacy")
    args = parser.parse_args(argv)

    try:
        counts = validate(args.manifest)
    except ManifestError as error:
        print(f"::error::{error}", file=sys.stderr)
        return 1

    print(
        f"Privacy manifest OK: {args.manifest.name}, "
        f"{counts['declared_api_types']} required-reason API type(s) declared."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())