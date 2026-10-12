#!/usr/bin/env python3
"""Checks that a provisioning profile is an App Store profile for this bundle.

Apple distinguishes the distribution channels by profile keys: App Store has no
ProvisionedDevices, no ProvisionsAllDevices, and get-task-allow false; Ad Hoc has
ProvisionedDevices; Development has get-task-allow true; Enterprise has
ProvisionsAllDevices. Only App Store is accepted here, and every check fails
closed, since a wrong profile surfaces as an upload rejection rather than a
build failure.

Read with plistlib rather than a shell reader: the expiry is a plist <date> and
the device list is a real array, so comparing them as text is how an expired
profile slips past.
"""

from __future__ import annotations

import argparse
import hashlib
import plistlib
import re
import sys
from datetime import datetime, timezone
from pathlib import Path


class ProfileError(ValueError):
    pass


def _load(path: Path) -> dict:
    try:
        with path.open("rb") as handle:
            parsed = plistlib.load(handle)
    except OSError as error:
        raise ProfileError(f"Cannot read provisioning profile {path}: {error}") from error
    except Exception as error:  # plistlib raises several unrelated types
        raise ProfileError(f"{path} is not a readable plist: {error}") from error
    if not isinstance(parsed, dict):
        raise ProfileError(f"{path} is not a provisioning profile dictionary.")
    return parsed


def _entitlements(profile: dict) -> dict:
    entitlements = profile.get("Entitlements")
    if not isinstance(entitlements, dict):
        raise ProfileError("Profile has no Entitlements dictionary.")
    return entitlements


def _is_disabled(value: object) -> bool:
    return value is None or value is False


def check(
    path: Path,
    team: str,
    bundle_id: str,
    expected_name: str | None = None,
    certificate_sha256: str | None = None,
    now: datetime | None = None,
) -> dict[str, str]:
    profile = _load(path)
    entitlements = _entitlements(profile)

    name = profile.get("Name")
    if not isinstance(name, str) or not name:
        raise ProfileError("Profile has no Name.")
    if expected_name is not None and name != expected_name:
        raise ProfileError(
            f"Profile name is '{name}', but the workflow expects '{expected_name}'. "
            "Correct IOS_PROVISION_PROFILE_NAME in the workflow, or supply the "
            "profile you actually intend to sign with."
        )

    teams = profile.get("TeamIdentifier")
    if not isinstance(teams, list) or not teams:
        raise ProfileError("Profile has no TeamIdentifier.")
    if teams[0] != team:
        raise ProfileError(f"Profile team '{teams[0]}' does not match the expected team '{team}'.")

    expected_app_id = f"{team}.{bundle_id}"
    app_id = entitlements.get("application-identifier")
    if app_id != expected_app_id:
        raise ProfileError(
            f"Profile covers '{app_id}', not '{expected_app_id}'. An App Store profile must "
            "name this bundle id exactly; a wildcard profile is rejected."
        )

    if not _is_disabled(entitlements.get("get-task-allow")):
        raise ProfileError(
            "Profile allows debugging (get-task-allow is true), so it is a Development profile."
        )
    if not _is_disabled(profile.get("ProvisionsAllDevices")):
        raise ProfileError(
            "Profile sets ProvisionsAllDevices, so it is an Enterprise in-house profile. "
            "App Store Connect does not accept one."
        )
    devices = profile.get("ProvisionedDevices")
    if devices:
        raise ProfileError(
            f"Profile lists {len(devices)} ProvisionedDevices, so it is an Ad Hoc profile. "
            "App Store Connect does not accept one."
        )

    expiry = profile.get("ExpirationDate")
    if not isinstance(expiry, datetime):
        raise ProfileError("Profile has no readable ExpirationDate.")
    reference = now or datetime.now(timezone.utc)
    if expiry.tzinfo is None:
        expiry = expiry.replace(tzinfo=timezone.utc)
    if expiry <= reference:
        raise ProfileError(f"Profile expired on {expiry.isoformat()}.")

    # Digest the stored DER bytes so a digest taken by the caller from the
    # same certificate compares equal.
    certificate_digests = [
        hashlib.sha256(bytes(entry)).hexdigest()
        for entry in profile.get("DeveloperCertificates", [])
        if isinstance(entry, (bytes, bytearray))
    ]
    if not certificate_digests:
        raise ProfileError("Profile embeds no DeveloperCertificates.")
    if certificate_sha256 is not None:
        expected = certificate_sha256.strip().lower()
        if not re.fullmatch(r"[0-9a-f]{64}", expected):
            raise ProfileError(
                "The expected certificate digest is not a 64-character SHA-256 hex value."
            )
        if expected not in certificate_digests:
            raise ProfileError(
                "The signing certificate the build was configured to use is not one of the "
                "certificates in this profile. codesign would fail, or produce an IPA the "
                "profile does not cover."
            )

    uuid = profile.get("UUID")
    if not isinstance(uuid, str) or not uuid:
        raise ProfileError("Profile has no UUID, so it cannot be installed for codesign.")

    return {
        "uuid": uuid,
        "name": name,
        "team": teams[0],
        "app_id": app_id,
        "expires": expiry.isoformat(),
        "profile_certificates": len(certificate_digests),
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("profile", type=Path, help="Decoded profile plist, from 'security cms -D'")
    parser.add_argument("--team", required=True, help="Expected Apple team ID")
    parser.add_argument("--bundle-id", required=True, help="Expected bundle identifier")
    parser.add_argument("--name", help="Expected profile display name")
    parser.add_argument(
        "--certificate-sha256",
        help="SHA-256 hex digest of the DER bytes of the signing certificate the build "
        "was configured to use",
    )
    parser.add_argument("--github-output", help="Append the profile identity to this file")
    args = parser.parse_args(argv)

    try:
        values = check(
            args.profile,
            team=args.team,
            bundle_id=args.bundle_id,
            expected_name=args.name,
            certificate_sha256=args.certificate_sha256,
        )
    except ProfileError as error:
        print(f"::error::{error}", file=sys.stderr)
        return 1

    print(values["uuid"])
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