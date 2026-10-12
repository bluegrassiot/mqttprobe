#!/usr/bin/env python3
"""Source-contract tests for the Apple platform packaging files.

These parse the plists and the privacy manifest as XML and assert the invariants the
App Store submission depends on, so a well-meaning edit cannot silently reintroduce a
review blocker. They check structure and values, not formatting.

Run: python -m unittest discover -s scripts/ci/tests
"""

import plistlib
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
PLATFORMS = ROOT / "src" / "MqttProbe.Maui" / "Platforms"
IOS_PLIST = PLATFORMS / "iOS" / "Info.plist"
CATALYST_PLIST = PLATFORMS / "MacCatalyst" / "Info.plist"
PRIVACY_MANIFEST = PLATFORMS / "iOS" / "Resources" / "PrivacyInfo.xcprivacy"


def load_plist(path):
    with path.open("rb") as handle:
        return plistlib.load(handle)


class IosInfoPlistTests(unittest.TestCase):
    def setUp(self):
        self.plist = load_plist(IOS_PLIST)

    def test_targets_iphone_only(self):
        self.assertEqual(self.plist["UIDeviceFamily"], [1])

    def test_drops_ipad_only_orientation_key(self):
        self.assertNotIn("UISupportedInterfaceOrientations~ipad", self.plist)

    def test_declares_local_network_usage_description(self):
        description = self.plist["NSLocalNetworkUsageDescription"]
        self.assertTrue(description.strip())
        self.assertIn("broker", description.lower())

    def test_omits_bonjour_services(self):
        # No Bonjour/DNS-SD discovery exists in the app, so declaring service types
        # would be inaccurate and invites review questions.
        self.assertNotIn("NSBonjourServices", self.plist)

    def test_ats_allows_local_networking_only(self):
        ats = self.plist["NSAppTransportSecurity"]
        self.assertIs(ats["NSAllowsLocalNetworking"], True)
        self.assertNotIn("NSAllowsArbitraryLoads", ats)

    def test_no_inert_root_exception_domains(self):
        # NSExceptionDomains must be nested inside NSAppTransportSecurity to have any
        # effect; at the plist root it is ignored by the system.
        self.assertNotIn("NSExceptionDomains", self.plist)
        self.assertNotIn("NSExceptionDomains", self.plist["NSAppTransportSecurity"])

    def test_version_keys_remain_build_supplied(self):
        self.assertNotIn("CFBundleShortVersionString", self.plist)
        self.assertNotIn("CFBundleVersion", self.plist)

    def test_declares_exempt_encryption_only(self):
        self.assertIs(self.plist["ITSAppUsesNonExemptEncryption"], False)


class MacCatalystInfoPlistTests(unittest.TestCase):
    def setUp(self):
        self.plist = load_plist(CATALYST_PLIST)

    def test_ats_allows_local_networking_only(self):
        ats = self.plist["NSAppTransportSecurity"]
        self.assertIs(ats["NSAllowsLocalNetworking"], True)
        self.assertNotIn("NSAllowsArbitraryLoads", ats)

    def test_no_inert_root_exception_domains(self):
        self.assertNotIn("NSExceptionDomains", self.plist)
        self.assertNotIn("NSExceptionDomains", self.plist["NSAppTransportSecurity"])

    def test_declares_exempt_encryption_only(self):
        self.assertIs(self.plist["ITSAppUsesNonExemptEncryption"], False)


class PrivacyManifestTests(unittest.TestCase):
    def setUp(self):
        self.manifest = load_plist(PRIVACY_MANIFEST)

    def _reasons(self):
        return {
            entry["NSPrivacyAccessedAPIType"]: set(entry["NSPrivacyAccessedAPITypeReasons"])
            for entry in self.manifest["NSPrivacyAccessedAPITypes"]
        }

    def test_declares_the_dotnet_bcl_baseline(self):
        reasons = self._reasons()
        self.assertEqual(
            reasons["NSPrivacyAccessedAPICategoryFileTimestamp"], {"C617.1"}
        )
        self.assertEqual(
            reasons["NSPrivacyAccessedAPICategorySystemBootTime"], {"35F9.1"}
        )
        self.assertEqual(reasons["NSPrivacyAccessedAPICategoryDiskSpace"], {"E174.1"})

    def test_omits_user_defaults(self):
        # SecureStorage and the certificate store both use Keychain Services on iOS,
        # which is not a required-reason category. Declaring it would be inaccurate.
        self.assertNotIn("NSPrivacyAccessedAPICategoryUserDefaults", self._reasons())

    def test_omits_sdk_only_reason_codes(self):
        # SDK-only codes must not appear in a first-party app manifest.
        for code in ("0A2A.1", "C56D.1", "BD.1", "3EC4.1"):
            for reasons in self._reasons().values():
                self.assertNotIn(code, reasons)

    def test_declares_no_tracking(self):
        self.assertIs(self.manifest["NSPrivacyTracking"], False)
        self.assertEqual(self.manifest["NSPrivacyTrackingDomains"], [])

    def test_collects_no_developer_data(self):
        # The app stores broker configuration and certificates locally on device and
        # sends nothing to the developer.
        self.assertEqual(self.manifest["NSPrivacyCollectedDataTypes"], [])


class BundleSourceContractTests(unittest.TestCase):
    def test_manifest_lives_under_ios_resources_folder(self):
        # Platforms/iOS/Resources is the single-project IPhoneResourcePrefix folder the
        # SDK glob turns into BundleResource items, which is how the file reaches the
        # bundle root where Apple requires it.
        self.assertTrue(PRIVACY_MANIFEST.is_file())
        self.assertEqual(PRIVACY_MANIFEST.parent.name, "Resources")
        self.assertEqual(PRIVACY_MANIFEST.parent.parent.name, "iOS")

    def test_maui_single_project_sets_iphone_resource_prefix(self):
        targets = ROOT / "src" / "MqttProbe.Maui" / "MqttProbe.Maui.csproj"
        self.assertTrue(targets.is_file())
        # The MAUI build tasks own IPhoneResourcePrefix; the csproj only needs to keep
        # the Platforms/iOS layout. A stray csproj override would break bundling.
        contents = targets.read_text(encoding="utf-8")
        self.assertNotIn("IPhoneResourcePrefix", contents)
        self.assertNotIn("xcprivacy", contents)


if __name__ == "__main__":
    unittest.main()