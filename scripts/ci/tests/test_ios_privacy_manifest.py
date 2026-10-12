#!/usr/bin/env python3
"""Tests for scripts/ci/ios-privacy-manifest.py.

Run: python -m unittest discover -s scripts/ci/tests
"""

import importlib.util
import plistlib
import tempfile
import unittest
from pathlib import Path

_SPEC = importlib.util.spec_from_file_location(
    "ci_ios_privacy_manifest",
    Path(__file__).resolve().parents[1] / "ios-privacy-manifest.py",
)
if _SPEC is None or _SPEC.loader is None:
    raise SystemExit("Cannot load scripts/ci/ios-privacy-manifest.py")
ios_privacy = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(ios_privacy)

# The baseline Microsoft ships for .NET MAUI, per the requirements verification.
MAUI_TEMPLATE = {
    "NSPrivacyTracking": False,
    "NSPrivacyTrackingDomains": [],
    "NSPrivacyCollectedDataTypes": [],
    "NSPrivacyAccessedAPITypes": [
        {
            "NSPrivacyAccessedAPIType": "NSPrivacyAccessedAPICategoryFileTimestamp",
            "NSPrivacyAccessedAPITypeReasons": ["C617.1"],
        },
        {
            "NSPrivacyAccessedAPIType": "NSPrivacyAccessedAPICategorySystemBootTime",
            "NSPrivacyAccessedAPITypeReasons": ["35F9.1"],
        },
        {
            "NSPrivacyAccessedAPIType": "NSPrivacyAccessedAPICategoryDiskSpace",
            "NSPrivacyAccessedAPITypeReasons": ["E174.1"],
        },
    ],
}


class ManifestFixture(unittest.TestCase):
    def write(self, content=None, raw: bytes | None = None) -> Path:
        directory = tempfile.mkdtemp()
        path = Path(directory) / "PrivacyInfo.xcprivacy"
        if raw is not None:
            path.write_bytes(raw)
        else:
            with path.open("wb") as handle:
                plistlib.dump(MAUI_TEMPLATE if content is None else content, handle)
        self.addCleanup(lambda: path.unlink(missing_ok=True))
        return path


class ValidateTests(ManifestFixture):
    def test_accepts_the_maui_template(self):
        counts = ios_privacy.validate(self.write())
        self.assertEqual(counts["declared_api_types"], 3)

    def test_accepts_empty_api_types_list(self):
        manifest = {**MAUI_TEMPLATE, "NSPrivacyAccessedAPITypes": []}
        self.assertEqual(ios_privacy.validate(self.write(manifest))["declared_api_types"], 0)

    def test_accepts_tracking_declared_true(self):
        manifest = {**MAUI_TEMPLATE, "NSPrivacyTracking": True}
        self.assertIn("declared_api_types", ios_privacy.validate(self.write(manifest)))

    def test_rejects_missing_file(self):
        missing = Path(tempfile.mkdtemp()) / "PrivacyInfo.xcprivacy"
        with self.assertRaises(ios_privacy.ManifestError) as raised:
            ios_privacy.validate(missing)
        self.assertIn("ITMS-91053", str(raised.exception))

    def test_rejects_unparseable_plist(self):
        with self.assertRaises(ios_privacy.ManifestError) as raised:
            ios_privacy.validate(self.write(raw=b"not a plist"))
        self.assertIn("not a readable privacy manifest", str(raised.exception))

    def test_rejects_non_dict_plist(self):
        with self.assertRaises(ios_privacy.ManifestError) as raised:
            ios_privacy.validate(self.write(raw=plistlib.dumps(["a", "b"])))
        self.assertIn("not a privacy manifest dictionary", str(raised.exception))

    def test_rejects_each_missing_top_level_key(self):
        for key in ios_privacy.REQUIRED_KEYS:
            with self.subTest(key=key):
                manifest = {name: value for name, value in MAUI_TEMPLATE.items() if name != key}
                with self.assertRaises(ios_privacy.ManifestError) as raised:
                    ios_privacy.validate(self.write(manifest))
                self.assertIn(key, str(raised.exception))

    def test_rejects_all_three_missing(self):
        with self.assertRaises(ios_privacy.ManifestError):
            ios_privacy.validate(self.write({}))

    def test_rejects_non_array_api_types(self):
        manifest = {**MAUI_TEMPLATE, "NSPrivacyAccessedAPITypes": "not-an-array"}
        with self.assertRaises(ios_privacy.ManifestError) as raised:
            ios_privacy.validate(self.write(manifest))
        self.assertIn("must be an array", str(raised.exception))

    def test_rejects_empty_manifest(self):
        with self.assertRaises(ios_privacy.ManifestError):
            ios_privacy.validate(self.write({}))


class CliTests(ManifestFixture):
    def test_valid_manifest_exits_zero(self):
        self.assertEqual(ios_privacy.main([str(self.write())]), 0)

    def test_missing_manifest_exits_nonzero(self):
        missing = Path(tempfile.mkdtemp()) / "PrivacyInfo.xcprivacy"
        self.assertEqual(ios_privacy.main([str(missing)]), 1)

    def test_incomplete_manifest_exits_nonzero(self):
        incomplete = {name: value for name, value in MAUI_TEMPLATE.items() if name != "NSPrivacyTracking"}
        self.assertEqual(ios_privacy.main([str(self.write(incomplete))]), 1)


if __name__ == "__main__":
    unittest.main()