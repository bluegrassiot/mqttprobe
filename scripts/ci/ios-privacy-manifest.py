#!/usr/bin/env python3
"""Validates a PrivacyInfo.xcprivacy inside a built iOS app bundle.

Checks the bundled declarations this app needs; passing is not App Store Connect approval.
"""

from __future__ import annotations

import argparse
import plistlib
import sys
from pathlib import Path

# Bundle gate requires explicit declarations.
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

# The .NET BCL required-reason baseline: every .NET iOS app links the runtime that
# reads these, so the bundle manifest must declare each category's reason code.
REQUIRED_REASON_PAIRS = {
    "NSPrivacyAccessedAPICategoryFileTimestamp": "C617.1",
    "NSPrivacyAccessedAPICategorySystemBootTime": "35F9.1",
    "NSPrivacyAccessedAPICategoryDiskSpace": "E174.1",
}

COLLECTED_DATA_KEYS = frozenset(
    {
        "NSPrivacyCollectedDataType",
        "NSPrivacyCollectedDataTypeLinked",
        "NSPrivacyCollectedDataTypeTracking",
        "NSPrivacyCollectedDataTypePurposes",
    }
)


class ManifestError(ValueError):
    pass


def _check_tracking(manifest: dict) -> None:
    tracking = manifest["NSPrivacyTracking"]
    if not isinstance(tracking, bool):
        raise ManifestError(
            "NSPrivacyTracking must be a boolean true or false, not a number or string."
        )

    domains = manifest["NSPrivacyTrackingDomains"]
    if not isinstance(domains, list):
        raise ManifestError("NSPrivacyTrackingDomains must be an array.")
    for domain in domains:
        if not isinstance(domain, str) or not domain:
            raise ManifestError("NSPrivacyTrackingDomains entries must be non-empty strings.")
        if any(character in domain for character in "/?#:@") or any(
            character.isspace() for character in domain
        ):
            raise ManifestError(
                f"Tracking domain '{domain}' must be a bare host: no scheme, userinfo, port, "
                "path, query or trailing slash."
            )

    if tracking and not domains:
        raise ManifestError(
            "NSPrivacyTracking is true but NSPrivacyTrackingDomains is empty; a tracking app "
            "must name at least one domain."
        )
    if not tracking and domains:
        raise ManifestError(
            "NSPrivacyTracking is false but NSPrivacyTrackingDomains is not empty; TN3181 "
            "treats that combination as invalid."
        )


def _check_collected_data(manifest: dict) -> None:
    collected = manifest["NSPrivacyCollectedDataTypes"]
    if not isinstance(collected, list):
        raise ManifestError("NSPrivacyCollectedDataTypes must be an array.")

    for entry in collected:
        if not isinstance(entry, dict):
            raise ManifestError("Every NSPrivacyCollectedDataTypes entry must be a dictionary.")
        if set(entry) != COLLECTED_DATA_KEYS:
            raise ManifestError(
                "A NSPrivacyCollectedDataTypes entry must carry exactly these keys: "
                f"{', '.join(sorted(COLLECTED_DATA_KEYS))}."
            )
        data_type = entry["NSPrivacyCollectedDataType"]
        if not isinstance(data_type, str) or not data_type:
            raise ManifestError("NSPrivacyCollectedDataType must be a non-empty string.")
        for flag in ("NSPrivacyCollectedDataTypeLinked", "NSPrivacyCollectedDataTypeTracking"):
            if not isinstance(entry[flag], bool):
                raise ManifestError(f"{flag} must be a boolean true or false.")
        purposes = entry["NSPrivacyCollectedDataTypePurposes"]
        if not isinstance(purposes, list):
            raise ManifestError("NSPrivacyCollectedDataTypePurposes must be an array.")
        if any(not isinstance(purpose, str) or not purpose for purpose in purposes):
            raise ManifestError(
                "NSPrivacyCollectedDataTypePurposes entries must be non-empty strings."
            )


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
            f"{path.name} is missing required key(s): {', '.join(missing)}. This bundle gate "
            "requires all three top-level keys."
        )

    _check_tracking(manifest)
    _check_collected_data(manifest)

    if "NSPrivacyAccessedAPITypes" not in manifest:
        raise ManifestError(
            f"{path.name} declares no NSPrivacyAccessedAPITypes. The .NET runtime uses "
            "required-reason APIs, so the bundle manifest must at least carry the BCL baseline."
        )
    declared = manifest["NSPrivacyAccessedAPITypes"]
    if not isinstance(declared, list):
        raise ManifestError("NSPrivacyAccessedAPITypes must be an array.")

    reasons_by_category: dict[str, set[str]] = {}
    for entry in declared:
        if not isinstance(entry, dict):
            raise ManifestError(
                "Every NSPrivacyAccessedAPITypes entry must be a dictionary with "
                "NSPrivacyAccessedAPIType and NSPrivacyAccessedAPITypeReasons."
            )
        category = entry.get("NSPrivacyAccessedAPIType")
        if not isinstance(category, str) or not category:
            raise ManifestError(
                "An NSPrivacyAccessedAPITypes entry has no NSPrivacyAccessedAPIType string."
            )
        reasons = entry.get("NSPrivacyAccessedAPITypeReasons")
        if not isinstance(reasons, list) or not reasons:
            raise ManifestError(
                f"{category} has no NSPrivacyAccessedAPITypeReasons array; every declared "
                "category needs at least one reason code."
            )
        if any(not isinstance(reason, str) or not reason for reason in reasons):
            raise ManifestError(f"{category} has a non-string or empty reason code.")
        reasons_by_category.setdefault(category, set()).update(reasons)

    missing_pairs = [
        f"{category} ({reason})"
        for category, reason in REQUIRED_REASON_PAIRS.items()
        if reason not in reasons_by_category.get(category, set())
    ]
    if missing_pairs:
        raise ManifestError(
            f"{path.name} does not declare the .NET BCL required-reason baseline: "
            f"{', '.join(missing_pairs)}. App Store Connect rejects the upload with ITMS-91061."
        )

    unknown = sorted(set(reasons_by_category) - KNOWN_API_TYPES)
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