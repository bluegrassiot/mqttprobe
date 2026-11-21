#!/usr/bin/env python3
"""Source-contract tests for the Apple platform packaging files.

These parse the plists and the privacy manifest as XML and assert the invariants the
App Store submission depends on, so a well-meaning edit cannot silently reintroduce a
review blocker. They check structure and values, not formatting.

Run: python -m unittest discover -s scripts/ci/tests
"""

import importlib.util
import plistlib
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
PLATFORMS = ROOT / "src" / "MqttProbe.Maui" / "Platforms"
IOS_PLATFORM = PLATFORMS / "iOS"
IOS_PLIST = IOS_PLATFORM / "Info.plist"
CATALYST_PLIST = PLATFORMS / "MacCatalyst" / "Info.plist"
PRIVACY_MANIFEST = IOS_PLATFORM / "Resources" / "PrivacyInfo.xcprivacy"

IOS_ORIENTATION_POLICY = IOS_PLATFORM / "IosOrientationPolicy.cs"
IOS_APP_DELEGATE = IOS_PLATFORM / "AppDelegate.cs"
IOS_SCENE_HANDLER = IOS_PLATFORM / "IosSceneHandler.cs"
MAIN_PAGE = ROOT / "src" / "MqttProbe.Maui" / "MainPage.xaml.cs"
MAIN_PAGE_XAML = ROOT / "src" / "MqttProbe.Maui" / "MainPage.xaml"

IOS_PHONE_ORIENTATIONS = [
    "UIInterfaceOrientationPortrait",
    "UIInterfaceOrientationLandscapeLeft",
    "UIInterfaceOrientationLandscapeRight",
]
IOS_IPAD_ORIENTATIONS = IOS_PHONE_ORIENTATIONS + ["UIInterfaceOrientationPortraitUpsideDown"]

APP_WINDOW_ORIENTATION_SELECTOR = "application:supportedInterfaceOrientationsForWindow:"
WINDOW_SCENE_ORIENTATION_SELECTOR = "supportedInterfaceOrientationsForWindowScene:"
EXPORT_SELECTOR = re.compile(
    r'\[\s*(?:Foundation\.|ObjCRuntime\.)?Export\s*\(\s*"([^"]+)"\s*\)\s*\]'
)


def load_plist(path):
    with path.open("rb") as handle:
        return plistlib.load(handle)


def compact_source(path):
    return re.sub(r"\s+", "", path.read_text(encoding="utf-8"))


def source_block(source, marker):
    start = source.find(marker)
    if start < 0:
        return None
    opening = source.find("{", start)
    if opening < 0:
        return None
    depth = 0
    for index in range(opening, len(source)):
        if source[index] == "{":
            depth += 1
        elif source[index] == "}":
            depth -= 1
            if depth == 0:
                return source[start:index + 1]
    return None


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

    def test_targets_iphone_and_ipad(self):
        self.assertCountEqual(self.plist["UIDeviceFamily"], [1, 2])

    def test_supports_portrait_and_both_landscapes(self):
        self.assertCountEqual(
            self.plist["UISupportedInterfaceOrientations"],
            IOS_PHONE_ORIENTATIONS,
        )

    def test_ipad_declares_all_four_orientations(self):
        self.assertCountEqual(
            self.plist["UISupportedInterfaceOrientations~ipad"],
            IOS_IPAD_ORIENTATIONS,
        )

    def test_does_not_require_full_screen(self):
        self.assertNotIn("UIRequiresFullScreen", self.plist)

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


class IosOrientationSourceContractTests(unittest.TestCase):
    """Pin the shared iOS orientation policy and its two public selectors.

    Source-level only: finding an export or a guard does not prove the selector
    runs, nor that orientation behavior is correct on a device or simulator.
    """

    def setUp(self):
        self.policy = compact_source(IOS_ORIENTATION_POLICY)
        self.app_delegate = compact_source(IOS_APP_DELEGATE)
        self.scene_handler = compact_source(IOS_SCENE_HANDLER)

    def _exported_selectors(self):
        selectors = set()
        for path in sorted(IOS_PLATFORM.rglob("*.cs")):
            selectors.update(EXPORT_SELECTOR.findall(path.read_text(encoding="utf-8")))
        return selectors

    def test_app_delegate_exports_window_orientation_selector(self):
        self.assertIn(APP_WINDOW_ORIENTATION_SELECTOR, self._exported_selectors())

    def test_scene_handler_exports_window_scene_orientation_selector(self):
        self.assertIn(WINDOW_SCENE_ORIENTATION_SELECTOR, self._exported_selectors())

    def test_both_selectors_delegate_to_the_shared_policy(self):
        for source in (self.app_delegate, self.scene_handler):
            self.assertIn("IosOrientationPolicy.ForWindow(", source)

    def test_policy_helper_exposes_window_and_refresh_entry_points(self):
        self.assertTrue(IOS_ORIENTATION_POLICY.is_file())
        self.assertIn("classIosOrientationPolicy", self.policy)
        self.assertIn("ForWindow(", self.policy)
        self.assertIn("Refresh(", self.policy)

    def test_ipad_uses_all_orientations(self):
        self.assertIn("UIUserInterfaceIdiom.Pad", self.policy)
        self.assertIn("UIInterfaceOrientationMask.All;", self.policy)

    def test_phone_and_unknown_idioms_fall_back_to_portrait(self):
        self.assertIn("UIUserInterfaceIdiom.Phone", self.policy)
        self.assertGreaterEqual(
            self.policy.count("UIInterfaceOrientationMask.Portrait"), 2
        )
        self.assertIn("UIInterfaceOrientationMask.AllButUpsideDown", self.policy)

    def test_fold_detection_uses_uiwindow_division_regions(self):
        self.assertIn("UIViewReservedRegionKind.CreateDivision()", self.policy)
        self.assertIn("UIViewReservedRegionQueryOptions.IncludeInactive", self.policy)
        self.assertIn("window.GetReservedRegions(", self.policy)
        self.assertNotIn("WKWebView", self.policy)

    def test_fold_detection_requires_ios_27_1(self):
        self.assertIn("OperatingSystem.IsIOSVersionAtLeast(27,1)", self.policy)

    def test_refresh_requires_ios_16_and_updates_supported_orientations(self):
        self.assertIn("OperatingSystem.IsIOSVersionAtLeast(16)", self.policy)
        self.assertIn(
            "SetNeedsUpdateOfSupportedInterfaceOrientations()", self.policy
        )

    def test_no_device_model_or_width_heuristics(self):
        for forbidden in ("Duo", "Bounds.Width", "Frame.Width", "UIScreen.MainScreen"):
            self.assertNotIn(forbidden, self.policy)


class IosOrientationAttachmentSourceContractTests(unittest.TestCase):
    """Pin the MainPage hooks that reapply the orientation policy.

    Source-level only: hook presence and call sites do not prove the app refreshes
    correctly after load, foreground, rotation, or a fold change on a device.
    """

    def setUp(self):
        self.page = MAIN_PAGE.read_text(encoding="utf-8")
        self.compact_page = compact_source(MAIN_PAGE)

    def _assert_hook_refreshes(self, marker, label):
        block = source_block(self.page, marker)
        self.assertIsNotNone(block, f"{label} hook is missing")
        self.assertIn(
            "RefreshOrientationPolicy",
            block,
            f"{label} hook does not refresh the orientation policy",
        )

    def test_page_refreshes_the_attached_window_through_the_policy(self):
        self.assertIn("IosOrientationPolicy.Refresh(", self.compact_page)
        self.assertTrue(
            "_webView?.Window" in self.compact_page
            or "Window?.Handler?.PlatformView" in self.compact_page
        )

    def test_page_attaches_loaded_and_appearing_hooks(self):
        self.assertRegex(self.page, r"Loaded\s*\+=\s*OnPageLoaded")
        self._assert_hook_refreshes("protected override void OnAppearing()", "OnAppearing")
        self._assert_hook_refreshes("private void OnPageLoaded(", "Loaded")

    def test_page_refreshes_on_foreground(self):
        foreground = re.search(
            r"ObserveWillEnterForeground\([^)]*\)\s*=>\s*\{[\s\S]{0,300}?\}\);",
            self.page,
        )
        self.assertIsNotNone(foreground, "WillEnterForeground hook is missing")
        self.assertIn("RefreshOrientationPolicy", foreground.group(0))


class IosKeyboardBridgeSourceContractTests(unittest.TestCase):
    """Pin the native half of the iOS keyboard bridge.

    Source-level only: an attribute name or a classification branch does not prove
    WebKit paints correctly, that a device reports usable keyboard geometry, or
    that the JavaScript owner consumes the published state.
    """

    def setUp(self):
        self.page = MAIN_PAGE.read_text(encoding="utf-8")
        self.compact_page = compact_source(MAIN_PAGE)

    def test_xaml_keeps_all_safe_area_edges_for_other_platforms(self):
        self.assertRegex(
            MAIN_PAGE_XAML.read_text(encoding="utf-8"),
            r'x:Class="MqttProbe\.MainPage"[\s\S]*?SafeAreaEdges="All"',
        )

    def test_ios_constructor_keeps_hardware_insets_only(self):
        self.assertRegex(
            self.page,
            r"public MainPage\(\)\s*\{[\s\S]*?#if IOS[\s\S]*?"
            r"SafeAreaEdges\s*=\s*(?:Microsoft\.Maui\.SafeAreaEdges\.Container"
            r"|new\s+Microsoft\.Maui\.SafeAreaEdges\("
            r"\s*Microsoft\.Maui\.SafeAreaRegions\.Container\s*\))"
            r";[\s\S]*?#endif",
        )

    def test_native_publishes_the_retained_attribute_and_change_event(self):
        self.assertIn("data-app-native-keyboard-state", self.page)
        self.assertIn("app-native-keyboard-change", self.page)
        self.assertIn("document.documentElement.setAttribute(", self.compact_page)
        self.assertIn("window.dispatchEvent(newEvent(", self.compact_page)

    def test_native_does_not_write_the_effective_keyboard_class_or_height(self):
        for forbidden in (
            "app-keyboard-open",
            "--app-keyboard-height",
            "--app-keyboard-top-inset",
            "classList",
        ):
            self.assertNotIn(forbidden, self.page)

    def test_native_publishes_all_three_states(self):
        for token in ('"open"', '"closed"', '"unknown"'):
            self.assertIn(token, self.page)

    def test_frame_evidence_classifies_unknown_but_never_closed(self):
        block = source_block(self.page, "private IosKeyboardState ClassifyKeyboardFrame(")
        self.assertIsNotNone(block, "keyboard frame classifier is missing")
        self.assertIn("IosKeyboardState.Unknown", block)
        self.assertIn("IosKeyboardState.Open", block)
        self.assertNotIn("IosKeyboardState.Closed", block)
        self.assertTrue(
            "_webView?.Window" in block
            and "ConvertRectFromCoordinateSpace" in block
            and "UIScreen.MainScreen" not in block,
        )

    def test_hide_notification_is_the_closed_evidence(self):
        self.assertRegex(
            self.page,
            r"ObserveWillHide\([^)]*\)\s*=>\s*SetKeyboardState\(IosKeyboardState\.Closed\)",
        )

    def test_applied_state_only_advances_after_a_successful_publish(self):
        block = source_block(self.page, "private async Task PushKeyboardStateAsync(")
        self.assertIsNotNone(block, "keyboard publisher is missing")
        self.assertIn("_appliedKeyboardState", block)


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

    def test_csproj_does_not_override_device_family_or_orientation(self):
        targets = ROOT / "src" / "MqttProbe.Maui" / "MqttProbe.Maui.csproj"
        contents = targets.read_text(encoding="utf-8")
        for forbidden in (
            "UIDeviceFamily",
            "UISupportedInterfaceOrientations",
            "UIRequiresFullScreen",
        ):
            self.assertNotIn(forbidden, contents)

    def test_ios_minimum_os_is_unchanged(self):
        targets = ROOT / "src" / "MqttProbe.Maui" / "MqttProbe.Maui.csproj"
        contents = targets.read_text(encoding="utf-8")
        self.assertRegex(
            contents,
            r"<SupportedOSPlatformVersion[^>]*==\s*'ios'[^>]*>\s*15\.0\s*</SupportedOSPlatformVersion>",
        )


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

if __name__ == "__main__":
    unittest.main()
