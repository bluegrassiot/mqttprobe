#!/usr/bin/env python3
"""Tests for scripts/ci/ios-profile.py.

Run: python -m unittest discover -s scripts/ci/tests
"""

import hashlib
import importlib.util
import plistlib
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path

_SPEC = importlib.util.spec_from_file_location(
    "ci_ios_profile",
    Path(__file__).resolve().parents[1] / "ios-profile.py",
)
if _SPEC is None or _SPEC.loader is None:
    raise SystemExit("Cannot load scripts/ci/ios-profile.py")
ios_profile = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(ios_profile)

TEAM = "XVKMB6CQ72"
BUNDLE = "com.bluegrassiot.mqttprobe"
APP_ID = f"{TEAM}.{BUNDLE}"
NOW = datetime(2026, 10, 11, tzinfo=timezone.utc)

CERT_DER = b"synthetic-certificate-der-bytes"
CERT_SHA256 = hashlib.sha256(CERT_DER).hexdigest()
OTHER_DER = b"a-different-certificate-der"
OTHER_SHA256 = hashlib.sha256(OTHER_DER).hexdigest()


def make_profile(**overrides) -> dict:
    profile = {
        "Name": "MQTTProbe App Store",
        "UUID": "1a2b3c4d-0000-0000-0000-000000000000",
        "TeamIdentifier": [TEAM],
        "Platform": ["iOS"],
        "ExpirationDate": NOW + timedelta(days=90),
        "DeveloperCertificates": [CERT_DER],
        "Entitlements": {"application-identifier": APP_ID, "get-task-allow": False},
    }
    for key, value in overrides.items():
        if value is None:
            profile.pop(key, None)
        elif key == "Entitlements":
            # None means "remove this key"; plistlib cannot serialize None.
            merged = {**profile["Entitlements"], **value}
            profile["Entitlements"] = {
                existing: item for existing, item in merged.items() if item is not None
            }
        elif key == "TeamIdentifier":
            profile["TeamIdentifier"] = value
        elif key == "DeveloperCertificates":
            profile["DeveloperCertificates"] = value
        else:
            profile[key] = value
    return profile


class ProfileFixture(unittest.TestCase):
    def write(self, profile) -> Path:
        directory = tempfile.mkdtemp()
        path = Path(directory) / "profile.plist"
        with path.open("wb") as handle:
            plistlib.dump(profile, handle)
        self.addCleanup(lambda: path.unlink(missing_ok=True))
        return path

    def check(self, profile, **kwargs):
        params = {"team": TEAM, "bundle_id": BUNDLE, "now": NOW}
        params.update(kwargs)
        return ios_profile.check(self.write(profile), **params)


class HappyPathTests(ProfileFixture):
    def test_returns_profile_identity(self):
        values = self.check(make_profile())
        self.assertEqual(values["name"], "MQTTProbe App Store")
        self.assertEqual(values["team"], TEAM)
        self.assertEqual(values["app_id"], APP_ID)
        self.assertEqual(values["uuid"], "1a2b3c4d-0000-0000-0000-000000000000")

    def test_accepts_matching_certificate(self):
        values = self.check(make_profile(), certificate_sha256=CERT_SHA256)
        self.assertEqual(values["profile_certificates"], 1)

    def test_accepts_matching_certificate_among_several(self):
        values = self.check(
            make_profile(DeveloperCertificates=[OTHER_DER, CERT_DER]),
            certificate_sha256=CERT_SHA256,
        )
        self.assertEqual(values["profile_certificates"], 2)

    def test_accepts_uppercase_digest_from_the_caller(self):
        self.assertEqual(
            self.check(make_profile(), certificate_sha256=CERT_SHA256.upper())[
                "profile_certificates"
            ],
            1,
        )

    def test_accepts_digest_with_surrounding_whitespace(self):
        self.assertEqual(
            self.check(make_profile(), certificate_sha256=f"  {CERT_SHA256}\n")[
                "profile_certificates"
            ],
            1,
        )

    def test_rejects_a_40_character_digest(self):
        with self.assertRaises(ios_profile.ProfileError) as raised:
            self.check(make_profile(), certificate_sha256=CERT_SHA256[:40])
        self.assertIn("SHA-256", str(raised.exception))

    def test_rejects_non_hex_digest(self):
        with self.assertRaises(ios_profile.ProfileError):
            self.check(make_profile(), certificate_sha256="z" * 64)

    def test_accepts_absent_get_task_allow(self):
        values = self.check(make_profile(Entitlements={"get-task-allow": None}))
        self.assertEqual(values["app_id"], APP_ID)

    def test_accepts_expiry_one_second_in_the_future(self):
        profile = make_profile(ExpirationDate=NOW + timedelta(seconds=1))
        self.assertIn("expires", self.check(profile))

    def test_accepts_naive_expiry_date(self):
        naive = (NOW + timedelta(days=10)).replace(tzinfo=None)
        self.assertIn("expires", self.check(make_profile(ExpirationDate=naive)))


class DistributionTypeTests(ProfileFixture):
    def test_rejects_ad_hoc_profile(self):
        profile = make_profile(ProvisionedDevices=["aaaa", "bbbb", "cccc"])
        with self.assertRaises(ios_profile.ProfileError) as raised:
            self.check(profile)
        self.assertIn("Ad Hoc", str(raised.exception))

    def test_rejects_empty_device_list(self):
        with self.assertRaises(ios_profile.ProfileError) as raised:
            self.check(make_profile(ProvisionedDevices=[]))
        self.assertIn("ProvisionedDevices", str(raised.exception))

    def test_rejects_malformed_device_list(self):
        with self.assertRaises(ios_profile.ProfileError) as raised:
            self.check(make_profile(ProvisionedDevices="not-a-list"))
        self.assertIn("ProvisionedDevices", str(raised.exception))

    def test_rejects_zero_device_count(self):
        with self.assertRaises(ios_profile.ProfileError):
            self.check(make_profile(ProvisionedDevices=0))

    def test_rejects_development_profile(self):
        profile = make_profile(Entitlements={"get-task-allow": True})
        with self.assertRaises(ios_profile.ProfileError) as raised:
            self.check(profile)
        self.assertIn("Development", str(raised.exception))

    def test_rejects_enterprise_profile(self):
        with self.assertRaises(ios_profile.ProfileError) as raised:
            self.check(make_profile(ProvisionsAllDevices=True))
        self.assertIn("Enterprise", str(raised.exception))

    def test_accepts_explicit_false_provisions_all_devices(self):
        self.assertIn("expires", self.check(make_profile(ProvisionsAllDevices=False)))


class IdentityTests(ProfileFixture):
    def test_rejects_wrong_team(self):
        with self.assertRaises(ios_profile.ProfileError):
            self.check(make_profile(), team="OTHERTEAM1")

    def test_rejects_wrong_bundle_id(self):
        with self.assertRaises(ios_profile.ProfileError) as raised:
            self.check(make_profile(), bundle_id="com.example.other")
        self.assertIn("com.example.other", str(raised.exception))

    def test_rejects_wildcard_profile(self):
        profile = make_profile(Entitlements={"application-identifier": f"{TEAM}.com.bluegrassiot.*"})
        with self.assertRaises(ios_profile.ProfileError) as raised:
            self.check(profile)
        self.assertIn("wildcard", str(raised.exception))

    def test_rejects_missing_application_identifier(self):
        with self.assertRaises(ios_profile.ProfileError):
            self.check(make_profile(Entitlements={"application-identifier": None}))

    def test_rejects_name_mismatch(self):
        with self.assertRaises(ios_profile.ProfileError) as raised:
            self.check(make_profile(), expected_name="Some Other Profile")
        self.assertIn("Some Other Profile", str(raised.exception))

    def test_accepts_name_match(self):
        values = self.check(make_profile(), expected_name="MQTTProbe App Store")
        self.assertEqual(values["name"], "MQTTProbe App Store")

    def test_rejects_missing_name(self):
        with self.assertRaises(ios_profile.ProfileError):
            self.check(make_profile(Name=None), expected_name="MQTTProbe App Store")

    def test_rejects_missing_uuid(self):
        with self.assertRaises(ios_profile.ProfileError):
            self.check(make_profile(UUID=None))

    def test_rejects_missing_team_identifier(self):
        with self.assertRaises(ios_profile.ProfileError):
            self.check(make_profile(TeamIdentifier=None))

    def test_rejects_empty_team_identifier(self):
        with self.assertRaises(ios_profile.ProfileError):
            self.check(make_profile(TeamIdentifier=[]))


class PlatformTests(ProfileFixture):
    def test_accepts_ios_platform(self):
        values = self.check(make_profile(Platform=["iOS"]))
        self.assertEqual(values["app_id"], APP_ID)

    def test_accepts_ios_among_additional_platforms(self):
        values = self.check(make_profile(Platform=["iOS", "xrOS"]))
        self.assertEqual(values["team"], TEAM)

    def test_rejects_missing_platform(self):
        with self.assertRaises(ios_profile.ProfileError) as raised:
            self.check(make_profile(Platform=None))
        self.assertIn("Platform", str(raised.exception))

    def test_rejects_string_platform(self):
        with self.assertRaises(ios_profile.ProfileError) as raised:
            self.check(make_profile(Platform="iOS"))
        self.assertIn("Platform", str(raised.exception))

    def test_rejects_empty_platform_list(self):
        with self.assertRaises(ios_profile.ProfileError) as raised:
            self.check(make_profile(Platform=[]))
        self.assertIn("Platform", str(raised.exception))

    def test_rejects_non_string_platform_entry(self):
        with self.assertRaises(ios_profile.ProfileError) as raised:
            self.check(make_profile(Platform=["iOS", 7]))
        self.assertIn("Platform", str(raised.exception))

    def test_rejects_empty_platform_entry(self):
        with self.assertRaises(ios_profile.ProfileError) as raised:
            self.check(make_profile(Platform=["iOS", ""]))
        self.assertIn("Platform", str(raised.exception))

    def test_rejects_tvos_platform(self):
        with self.assertRaises(ios_profile.ProfileError) as raised:
            self.check(make_profile(Platform=["tvOS"]))
        self.assertIn("tvOS", str(raised.exception))

    def test_rejects_macos_platform(self):
        with self.assertRaises(ios_profile.ProfileError) as raised:
            self.check(make_profile(Platform=["macOS"]))
        self.assertIn("macOS", str(raised.exception))


class ExpiryTests(ProfileFixture):
    def test_rejects_expired_profile(self):
        with self.assertRaises(ios_profile.ProfileError) as raised:
            self.check(make_profile(ExpirationDate=NOW - timedelta(days=1)))
        self.assertIn("expired", str(raised.exception))

    def test_rejects_expiry_exactly_now(self):
        with self.assertRaises(ios_profile.ProfileError):
            self.check(make_profile(ExpirationDate=NOW))

    def test_rejects_missing_expiry(self):
        with self.assertRaises(ios_profile.ProfileError) as raised:
            self.check(make_profile(ExpirationDate=None))
        self.assertIn("ExpirationDate", str(raised.exception))

    def test_rejects_non_datetime_expiry(self):
        with self.assertRaises(ios_profile.ProfileError):
            self.check(make_profile(ExpirationDate="2027-01-01T00:00:00Z"))


class CertificateTests(ProfileFixture):
    def test_rejects_certificate_not_in_profile(self):
        with self.assertRaises(ios_profile.ProfileError) as raised:
            self.check(make_profile(), certificate_sha256=OTHER_SHA256)
        self.assertIn("configured to use", str(raised.exception))

    def test_rejects_profile_with_no_certificates(self):
        with self.assertRaises(ios_profile.ProfileError) as raised:
            self.check(make_profile(DeveloperCertificates=[]))
        self.assertIn("DeveloperCertificates", str(raised.exception))

    def test_rejects_profile_whose_only_certificate_is_another_one(self):
        profile = make_profile(DeveloperCertificates=[OTHER_DER])
        with self.assertRaises(ios_profile.ProfileError):
            self.check(profile, certificate_sha256=CERT_SHA256)

    def test_skips_certificate_check_when_none_supplied(self):
        self.assertIn("expires", self.check(make_profile(), certificate_sha256=None))

    def test_ignores_non_binary_certificate_entries(self):
        profile = make_profile(DeveloperCertificates=["not-bytes", CERT_DER])
        self.assertEqual(
            self.check(profile, certificate_sha256=CERT_SHA256)["profile_certificates"], 1
        )


class MalformedInputTests(ProfileFixture):
    def test_rejects_missing_file(self):
        missing = Path(tempfile.mkdtemp()) / "nope.plist"
        with self.assertRaises(ios_profile.ProfileError) as raised:
            ios_profile.check(missing, team=TEAM, bundle_id=BUNDLE)
        self.assertIn("Cannot read", str(raised.exception))

    def test_rejects_non_plist_content(self):
        path = Path(tempfile.mkdtemp()) / "bad.plist"
        path.write_bytes(b"this is not a plist at all")
        self.addCleanup(lambda: path.unlink(missing_ok=True))
        with self.assertRaises(ios_profile.ProfileError) as raised:
            ios_profile.check(path, team=TEAM, bundle_id=BUNDLE)
        self.assertIn("not a readable plist", str(raised.exception))

    def test_rejects_non_dict_plist(self):
        path = Path(tempfile.mkdtemp()) / "list.plist"
        with path.open("wb") as handle:
            plistlib.dump(["not", "a", "profile"], handle)
        self.addCleanup(lambda: path.unlink(missing_ok=True))
        with self.assertRaises(ios_profile.ProfileError) as raised:
            ios_profile.check(path, team=TEAM, bundle_id=BUNDLE)
        self.assertIn("not a provisioning profile", str(raised.exception))

    def test_rejects_missing_entitlements(self):
        path = Path(tempfile.mkdtemp()) / "noent.plist"
        profile = make_profile()
        del profile["Entitlements"]
        with path.open("wb") as handle:
            plistlib.dump(profile, handle)
        self.addCleanup(lambda: path.unlink(missing_ok=True))
        with self.assertRaises(ios_profile.ProfileError) as raised:
            ios_profile.check(path, team=TEAM, bundle_id=BUNDLE)
        self.assertIn("Entitlements", str(raised.exception))


class CliTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.mkdtemp()
        self.path = Path(directory) / "profile.plist"
        with self.path.open("wb") as handle:
            plistlib.dump(make_profile(), handle)
        self.addCleanup(lambda: self.path.unlink(missing_ok=True))

    def base_args(self):
        return [str(self.path), "--team", TEAM, "--bundle-id", BUNDLE]

    def test_prints_uuid_and_writes_outputs(self):
        output = Path(tempfile.mkdtemp()) / "gh_output.txt"
        code = ios_profile.main(self.base_args() + ["--github-output", str(output)])
        self.assertEqual(code, 0)
        self.assertEqual(output.read_text(encoding="utf-8").splitlines()[0].split("=")[0], "uuid")

    def test_reports_failure_for_ad_hoc(self):
        directory = tempfile.mkdtemp()
        path = Path(directory) / "adhoc.plist"
        with path.open("wb") as handle:
            plistlib.dump(make_profile(ProvisionedDevices=["a"]), handle)
        self.addCleanup(lambda: path.unlink(missing_ok=True))
        code = ios_profile.main([str(path), "--team", TEAM, "--bundle-id", BUNDLE])
        self.assertEqual(code, 1)


if __name__ == "__main__":
    unittest.main()