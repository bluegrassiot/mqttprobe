#!/usr/bin/env python3
"""Focused tests for scenario-declared MSBuild properties in the legal pipeline."""

import json
import os
import shutil
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "scripts" / "legal"))

import common  # noqa: E402
import extract  # noqa: E402
import profiles  # noqa: E402
import refresh  # noqa: E402

CATALYST_KEYS = ["net10.0-maccatalyst/maccatalyst-x64", "net10.0-maccatalyst/maccatalyst-arm64"]


def fixture(test):
    root = Path(tempfile.mkdtemp())
    test.addCleanup(shutil.rmtree, root, True)
    return root


def catalyst_scenario(properties=None):
    scenario = {
        "name": "maui-maccatalyst-app-store",
        "project": "src/MqttProbe.Maui/MqttProbe.Maui.csproj",
        "framework": "net10.0-maccatalyst",
        "runtimeIdentifiers": ["maccatalyst-x64", "maccatalyst-arm64"],
        "selfContained": False,
        "host": "macos",
    }
    if properties is not None:
        scenario["msbuildProperties"] = properties
    return scenario


def fake_assets(packages, keys=CATALYST_KEYS):
    targets = {
        key: {f"{package}/{version}": {"type": "package"} for package, version in packages}
        for key in keys
    }
    libraries = {
        f"{package}/{version}": {"sha512": f"{package}-{version}"}
        for package, version in packages
    }
    return {"targets": targets, "libraries": libraries}


def inventory(scenario, packages, keys=CATALYST_KEYS):
    with mock.patch.object(profiles, "read_assets", return_value=fake_assets(packages, keys)), \
         mock.patch.object(profiles, "input_fingerprint", return_value={"files": {}, "submodules": {}, "sdk": None}):
        return profiles.inventory(scenario)


class ScenarioPropertyValidationTests(unittest.TestCase):
    def test_absent_map_defaults_to_empty(self):
        self.assertEqual(profiles.scenario_properties({"name": "direct"}), {})

    def test_values_normalize_to_msbuild_strings(self):
        scenario = {"name": "s", "msbuildProperties": {"Flag": True, "Count": 3, "Name": "a b"}}
        self.assertEqual(
            profiles.scenario_properties(scenario),
            {"Flag": "true", "Count": "3", "Name": "a b"},
        )

    def test_invalid_map_types_fail_loudly(self):
        for raw in ([], "true", {"Bad Name": "x"}, {"Name": ["x"]}, {1: "x"}):
            with self.assertRaises(common.LegalError):
                profiles.scenario_properties({"name": "s", "msbuildProperties": raw})


class StoreScenarioDeclarationTests(unittest.TestCase):
    def scenario_entries(self):
        data = json.loads((ROOT / "legal" / "scenarios.json").read_text(encoding="utf-8"))
        return {entry["name"]: entry for entry in data["scenarios"]}

    def test_store_scenario_mirrors_direct_catalyst_with_property(self):
        entries = self.scenario_entries()
        direct = entries["maui-maccatalyst"]
        store = entries["maui-maccatalyst-app-store"]
        self.assertNotIn("pending", store)
        for field in ("project", "framework", "runtimeIdentifiers", "selfContained", "host"):
            self.assertEqual(store[field], direct[field], field)
        self.assertEqual(store["msbuildProperties"], {"MqttProbeMacAppStore": "true"})

    def test_direct_catalyst_scenario_is_unchanged(self):
        direct = self.scenario_entries()["maui-maccatalyst"]
        self.assertNotIn("msbuildProperties", direct)
        self.assertEqual(direct["runtimeIdentifiers"], ["maccatalyst-x64", "maccatalyst-arm64"])


class RestoreCommandTests(unittest.TestCase):
    def test_declared_properties_are_appended_as_msbuild_arguments(self):
        command = refresh.restore_command(catalyst_scenario({"MqttProbeMacAppStore": "true"}))
        self.assertIn("-p:MqttProbeMacAppStore=true", command)
        self.assertEqual(command[-1], "-p:MqttProbeMacAppStore=true")

    def test_direct_catalyst_command_has_no_property_arguments(self):
        command = refresh.restore_command(catalyst_scenario())
        self.assertFalse(any(argument.startswith("-p:MqttProbeMacAppStore=") for argument in command))

    def test_restore_environment_carries_properties_without_mutating_process(self):
        before = dict(os.environ)
        environment = extract.scenario_environment(catalyst_scenario({"MqttProbeMacAppStore": "true"}))
        self.assertEqual(environment["MqttProbeMacAppStore"], "true")
        self.assertEqual(dict(os.environ), before)

    def test_sequential_store_then_direct_does_not_leak_properties(self):
        store_environment = extract.scenario_environment(
            catalyst_scenario({"MqttProbeMacAppStore": "true"})
        )
        direct_environment = extract.scenario_environment(catalyst_scenario())
        self.assertIn("MqttProbeMacAppStore", store_environment)
        self.assertNotIn("MqttProbeMacAppStore", direct_environment)
        self.assertNotIn("MqttProbeMacAppStore", os.environ)


class RefreshFlowTests(unittest.TestCase):
    def test_refresh_scenario_passes_property_environment_to_restore(self):
        environments = []
        scenario = catalyst_scenario({"MqttProbeMacAppStore": "true"})
        with mock.patch.object(
            refresh, "run", side_effect=lambda command, environment=None: environments.append(environment)
        ), \
             mock.patch.object(refresh.profiles, "inventory", return_value={"packages": []}), \
             mock.patch.object(refresh, "write_json"), \
             mock.patch.object(refresh.extract, "extract_scenario", return_value={}):
            refresh.refresh_scenario(scenario, {})
        self.assertEqual(environments[0]["MqttProbeMacAppStore"], "true")

    def test_extraction_environment_carries_properties(self):
        root = fixture(self)
        scenario = catalyst_scenario({"MqttProbeMacAppStore": "true"})
        scenario["framework"] = "net10.0"

        def fake_run(command, **kwargs):
            Path(command[command.index("-fo") + 1]).write_text("[]", encoding="utf-8")
            return mock.Mock(returncode=0, stdout="", stderr="")

        with mock.patch.object(extract.tempfile, "mkdtemp", return_value=str(root)), \
             mock.patch.object(extract.subprocess, "run", side_effect=fake_run) as run:
            entries, _, _ = extract.run_nuget_license(scenario)
        self.assertEqual(entries, [])
        self.assertEqual(run.call_args.kwargs["env"]["MqttProbeMacAppStore"], "true")
        self.assertNotIn("MqttProbeMacAppStore", os.environ)


class InventoryMetadataTests(unittest.TestCase):
    def test_direct_profile_omits_empty_property_map(self):
        profile = inventory(catalyst_scenario(), [("Test.Pkg", "1.0.0")])
        self.assertNotIn("msbuildProperties", profile)
        self.assertEqual(profile["targets"], CATALYST_KEYS)

    def test_store_profile_records_exact_property_map_and_both_rids(self):
        profile = inventory(
            catalyst_scenario({"MqttProbeMacAppStore": "true"}), [("Test.Pkg", "1.0.0")]
        )
        self.assertEqual(profile["msbuildProperties"], {"MqttProbeMacAppStore": "true"})
        self.assertEqual(profile["targets"], CATALYST_KEYS)

    def test_store_assets_without_velopack_are_recorded_verbatim(self):
        profile = inventory(catalyst_scenario({"MqttProbeMacAppStore": "true"}), [("Test.Pkg", "1.0.0")])
        self.assertNotIn("Velopack", [item["id"] for item in profile["packages"]])


class ProfileComparisonTests(unittest.TestCase):
    def test_property_drift_fails_even_when_packages_match(self):
        committed = inventory(catalyst_scenario({"MqttProbeMacAppStore": "true"}), [("Test.Pkg", "1.0.0")])
        current = inventory(catalyst_scenario({"MqttProbeMacAppStore": "false"}), [("Test.Pkg", "1.0.0")])
        problems = profiles.compare(committed, current)
        self.assertTrue(any("MSBuild properties changed" in item for item in problems))

    def test_absent_map_matches_empty_map_for_old_profiles(self):
        committed = inventory(catalyst_scenario(), [("Test.Pkg", "1.0.0")])
        current = inventory(catalyst_scenario(), [("Test.Pkg", "1.0.0")])
        self.assertEqual(profiles.compare(committed, current), [])

    def test_store_assets_reject_direct_profile(self):
        committed = inventory(catalyst_scenario(), [("Test.Pkg", "1.0.0"), ("Velopack", "1.2.161")])
        current = inventory(catalyst_scenario({"MqttProbeMacAppStore": "true"}), [("Test.Pkg", "1.0.0")])
        problems = profiles.compare(committed, current)
        self.assertTrue(any("missing resolved package: Velopack 1.2.161" in item for item in problems))

    def test_direct_assets_reject_store_profile(self):
        committed = inventory(catalyst_scenario({"MqttProbeMacAppStore": "true"}), [("Test.Pkg", "1.0.0")])
        current = inventory(catalyst_scenario(), [("Test.Pkg", "1.0.0"), ("Velopack", "1.2.161")])
        problems = profiles.compare(committed, current)
        self.assertTrue(any("unreviewed resolved package: Velopack 1.2.161" in item for item in problems))


if __name__ == "__main__":
    unittest.main()
