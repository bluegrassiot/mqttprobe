#!/usr/bin/env python3
"""Source-contract tests for the Apple platform packaging files.

These parse the plists and the privacy manifest as XML and assert the invariants the
App Store submission depends on, so a well-meaning edit cannot silently reintroduce a
review blocker. They check structure and values, not formatting.

Run: python -m unittest discover -s scripts/ci/tests
"""

import importlib.util
import plistlib
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
PLATFORMS = ROOT / "src" / "MqttProbe.Maui" / "Platforms"
IOS_PLIST = PLATFORMS / "iOS" / "Info.plist"
CATALYST_PLIST = PLATFORMS / "MacCatalyst" / "Info.plist"
PRIVACY_MANIFEST = PLATFORMS / "iOS" / "Resources" / "PrivacyInfo.xcprivacy"

WORKFLOW = ROOT / ".github" / "workflows" / "build-ios.yml"
SIGNING_JOB = "build-ios-archive"
TRUSTED_REF = "refs/heads/release/v1.0.8"


def load_plist(path):
    with path.open("rb") as handle:
        return plistlib.load(handle)


def load_job(name):
    """Return one top-level job block from the workflow as text.

    Read as text, not YAML, and never through the GitHub API: the script tests
    must not depend on a YAML parser the runner does not guarantee, and these
    assert source structure rather than exercise a workflow.
    """
    lines = WORKFLOW.read_text(encoding="utf-8").splitlines()
    start = lines.index(f"  {name}:")
    block = [lines[start]]
    for line in lines[start + 1:]:
        if line.startswith("  ") and not line.startswith("   "):
            break
        block.append(line)
    return "\n".join(block)


def load_privacy_validator():
    spec = importlib.util.spec_from_file_location(
        "ci_ios_privacy_manifest",
        Path(__file__).resolve().parents[1] / "ios-privacy-manifest.py",
    )
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


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

    def test_local_network_description_matches_ios(self):
        description = self.plist["NSLocalNetworkUsageDescription"]
        self.assertTrue(description.strip())
        self.assertEqual(description, load_plist(IOS_PLIST)["NSLocalNetworkUsageDescription"])

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
    def test_committed_manifest_passes_the_bundle_validator(self):
        # The bundle copy is this file, so it must satisfy the validator that runs
        # against the built app; drift here would fail the release lane.
        counts = load_privacy_validator().validate(PRIVACY_MANIFEST)
        self.assertEqual(counts["declared_api_types"], 3)

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


MAUI_APP_INFO_SERVICE = ROOT / "src" / "MqttProbe.Maui" / "Services" / "AppInfoService.cs"
PLUGIN_ASSEMBLY_CACHE = (
    ROOT / "src" / "MqttProbe.Core" / "Services" / "Plugins" / "Loading" / "PluginAssemblyCache.cs"
)

MAUI_CAPABILITY = "public bool SupportsAssemblyPlugins"
LOADER_GUARD = "private static bool AssemblyPluginLoadingAllowed"
IOS_ONLY_GUARD = "!OperatingSystem.IsIOS();"
INSPECTOR_SUBSTITUTE = "PluginAssemblyInspector.AssemblyPluginsSupported;"
ANDROID_EXCLUDING_GUARD = "!OperatingSystem.IsIOS() && !OperatingSystem.IsAndroid();"


def guard_expression(source, member):
    """Return what an expression-bodied `member => ...;` property is initialized from.

    Text, not reflection: these checks run without a build, and the point is to pin
    the declaration that ships rather than a compiled copy of it.
    """
    marker = f"{member} =>"
    for line in source.splitlines():
        stripped = line.strip()
        if stripped.startswith(marker):
            return stripped.split("=>", 1)[1].strip()
    raise AssertionError(f"{member} is not declared as an expression-bodied property")


def is_ios_only_guard(expression):
    """True only for the iOS guard. The inspector constant also refuses Android, and any
    expression naming Android is an exclusion this app never wanted: Android keeps its
    configured and bundled DLL loading."""
    return expression == IOS_ONLY_GUARD


class AssemblyPluginGuardSourceContractTests(unittest.TestCase):
    """Pin the shipped iOS-only assembly-plugin guards in both hosts.

    Source-level contract on the real declarations, not runtime proof: no Android,
    iOS or device execution is claimed here.
    """

    def setUp(self):
        self.maui = MAUI_APP_INFO_SERVICE.read_text(encoding="utf-8")
        self.loader = PLUGIN_ASSEMBLY_CACHE.read_text(encoding="utf-8")

    def test_maui_capability_is_ios_only(self):
        self.assertTrue(MAUI_APP_INFO_SERVICE.is_file())
        self.assertTrue(is_ios_only_guard(guard_expression(self.maui, MAUI_CAPABILITY)))

    def test_loader_guard_is_ios_only(self):
        self.assertTrue(PLUGIN_ASSEMBLY_CACHE.is_file())
        self.assertTrue(is_ios_only_guard(guard_expression(self.loader, LOADER_GUARD)))

    def test_inspector_substitution_breaks_the_maui_contract(self):
        mutated = self.maui.replace(IOS_ONLY_GUARD, INSPECTOR_SUBSTITUTE, 1)
        self.assertNotEqual(mutated, self.maui)
        self.assertFalse(is_ios_only_guard(guard_expression(mutated, MAUI_CAPABILITY)))

    def test_android_exclusion_breaks_the_loader_contract(self):
        mutated = self.loader.replace(IOS_ONLY_GUARD, ANDROID_EXCLUDING_GUARD, 1)
        self.assertNotEqual(mutated, self.loader)
        self.assertFalse(is_ios_only_guard(guard_expression(mutated, LOADER_GUARD)))


class IosSigningWorkflowTests(unittest.TestCase):
    """Source contracts for the signing gate in build-ios.yml.

    A workflow file cannot configure an environment, so these check only that the
    job references the right environment and carries the defense-in-depth ref
    test. The environment itself is a repository setting; see the archive build
    guide for the manual setup, which is where the real protection lives.
    """

    def setUp(self):
        self.text = WORKFLOW.read_text(encoding="utf-8")
        self.job = load_job(SIGNING_JOB)

    def test_signing_job_references_the_ios_signing_environment(self):
        self.assertIn("    environment: ios-signing", self.job)

    def test_signing_job_is_guarded_to_the_trusted_release_branch(self):
        # github.ref is fully qualified, so a tag named v1.0.8 or any other branch
        # does not satisfy the guard; this is defense in depth, not the boundary.
        self.assertIn(f"    if: github.ref == '{TRUSTED_REF}'", self.job)

    def test_guard_only_targets_the_branch_not_a_tag(self):
        # ref_name would also match a v1.0.8 tag; the guard must use github.ref.
        self.assertIn(TRUSTED_REF, self.job)
        self.assertNotIn("github.ref_name ==", self.job)

    def test_protection_is_not_pretended_in_yaml(self):
        # Reviewers and bypass are repository settings, not workflow keys, and
        # deployment: false is unnecessary syntax this workflow does not need.
        for forbidden in ("reviewers:", "required_reviewers", "deployment:"):
            self.assertNotIn(forbidden, self.text)

    def test_no_workflow_api_or_secret_management_steps(self):
        for forbidden in ("api.github.com", "gh api", "gh secret", "gh variable"):
            self.assertNotIn(forbidden, self.text)

    def test_manual_artifact_upload_is_the_only_publish_step(self):
        self.assertIn("uses: actions/upload-artifact@", self.job)
        self.assertIn("name: maui-ios-archive", self.job)
        for forbidden in ("action-gh-release", "upload-release-asset", "gh release", "altool"):
            self.assertNotIn(forbidden, self.text)


if __name__ == "__main__":
    unittest.main()