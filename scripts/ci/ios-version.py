#!/usr/bin/env python3
"""Validates the marketing version and build number stamped into the iOS bundle.

Apple limits the first integer of CFBundleVersion to four digits, so the whole
number space is 1..9999 and cannot encode a tag plus a run number. Both values
are therefore caller-supplied, and this script only validates them.
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

# Apple requires exactly three numeric parts; App Store Connect rejects fewer.
_MARKETING_VERSION = re.compile(r"^(\d{1,4})\.(\d{1,4})\.(\d{1,4})$")

_BUILD_NUMBER = re.compile(r"^\d{1,4}$")
_MIN_BUILD_NUMBER = 1
_MAX_BUILD_NUMBER = 9999

_PLACEHOLDER_MARKETING_VERSION = "0.0.0"


class VersionError(ValueError):
    pass


def normalize(raw: str) -> tuple[int, int, int]:
    candidate = (raw or "").strip()
    if candidate.startswith("v"):
        candidate = candidate[1:]
    match = _MARKETING_VERSION.match(candidate)
    if not match:
        raise VersionError(
            f"Version '{raw}' is not a valid App Store marketing version. "
            "Use three numeric parts of at most four digits each, e.g. 1.0.9 or v1.0.9."
        )
    # Apple ignores leading zeros in each part, so normalize rather than stamp them.
    parts = tuple(int(part) for part in match.groups())
    if parts == (0, 0, 0):
        raise VersionError(
            f"Version '{raw}' is the csproj placeholder and cannot be released."
        )
    return parts


def validate_build_number(raw: str) -> int:
    candidate = (raw or "").strip()
    if not _BUILD_NUMBER.match(candidate):
        raise VersionError(
            f"Build number '{raw}' must be a whole number of one to four digits "
            "(Apple limits the first integer of CFBundleVersion to four digits)."
        )
    value = int(candidate)
    if value < _MIN_BUILD_NUMBER:
        raise VersionError(
            f"Build number {value} is not valid; the first integer must be greater than zero."
        )
    if value > _MAX_BUILD_NUMBER:
        raise VersionError(
            f"Build number {value} exceeds the four-digit maximum Apple allows for "
            f"CFBundleVersion (max {_MAX_BUILD_NUMBER})."
        )
    return value


def derive(version: str, build_number: str) -> dict[str, str]:
    major, minor, patch = normalize(version)
    return {
        "marketing": f"{major}.{minor}.{patch}",
        "build": str(validate_build_number(build_number)),
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument(
        "--version", required=True, help="Release tag or bare version, e.g. v1.0.9"
    )
    parser.add_argument(
        "--build-number",
        required=True,
        help="CFBundleVersion, a whole number 1..9999, higher than any build already "
        "uploaded for this marketing version",
    )
    parser.add_argument(
        "--github-output",
        help="Append 'marketing=' and 'build=' lines to this file (defaults to stdout)",
    )
    args = parser.parse_args(argv)

    try:
        values = derive(args.version, args.build_number)
    except VersionError as error:
        print(f"::error::{error}", file=sys.stderr)
        return 1

    if args.github_output:
        with Path(args.github_output).open("a", encoding="utf-8") as handle:
            for key, value in values.items():
                handle.write(f"{key}={value}\n")
    else:
        for key, value in values.items():
            print(f"{key}={value}")
    return 0


if __name__ == "__main__":
    sys.exit(main())