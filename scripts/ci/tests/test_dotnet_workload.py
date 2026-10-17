import base64
import importlib.util
import json
import os
import shutil
import re
import subprocess
import tempfile
import textwrap
import unittest
from pathlib import Path
from unittest.mock import patch

_SCRIPT = Path(__file__).resolve().parents[1] / "dotnet-workload.py"
_SPEC = importlib.util.spec_from_file_location("ci_dotnet_workload", _SCRIPT)
if _SPEC is None or _SPEC.loader is None:
    raise SystemExit(f"Cannot load {_SCRIPT}")
dotnet_workload = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(dotnet_workload)


class SdkDiscoveryTests(unittest.TestCase):
    def test_uses_active_sdk_and_matching_sdk_location_with_spaces(self):
        with tempfile.TemporaryDirectory(prefix="dotnet sdk ") as temp:
            root = Path(temp) / "dotnet install"
            sdk = root / "sdk" / "10.0.402"
            sdk.mkdir(parents=True)
            with patch.object(dotnet_workload.subprocess, "run", side_effect=[
                subprocess.CompletedProcess(["dotnet"], 0, "10.0.402\n", ""),
                subprocess.CompletedProcess(["dotnet"], 0, f"10.0.300 [{root / 'sdk'}]\n10.0.402 [{root / 'sdk'}]\n", ""),
            ]) as run:
                self.assertEqual(dotnet_workload.discover_sdk_root(), root)
            self.assertEqual(run.call_args_list[0].args[0], ["dotnet", "--version"])

    def test_rejects_missing_active_sdk_location(self):
        with patch.object(dotnet_workload, "run_dotnet", side_effect=["10.0.402", "10.0.300 [C:\\dotnet\\sdk]"]):
            with self.assertRaises(dotnet_workload.WorkloadError):
                dotnet_workload.discover_sdk_root()

    def test_rejects_listed_sdk_with_missing_directory(self):
        with tempfile.TemporaryDirectory() as temp:
            with patch.object(dotnet_workload, "run_dotnet", side_effect=["10.0.402", f"10.0.402 [{temp}/sdk]"]):
                with self.assertRaises(dotnet_workload.WorkloadError):
                    dotnet_workload.discover_sdk_root()

    def test_rejects_invalid_dotnet_version_output(self):
        with patch.object(dotnet_workload, "run_dotnet", return_value="not-an-sdk"):
            with self.assertRaises(dotnet_workload.WorkloadError):
                dotnet_workload.discover_sdk_root()


class CacheIdentityTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / "global.json").write_text('{"sdk":{"version":"10.0.402","rollForward":"disable"}}', encoding="utf-8")
        action = self.root / ".github/actions/setup-dotnet-workload"
        action.mkdir(parents=True)
        (action / "action.yml").write_text("name: workload setup\n", encoding="utf-8")
        script = self.root / "scripts/ci/dotnet-workload.py"
        script.parent.mkdir(parents=True)
        script.write_text("helper\n", encoding="utf-8")

    def key(self, **overrides):
        values = {"runner_os": "Windows", "runner_arch": "X64", "sdk": "10.0.402", "workload_version": "9.0.100", "workload": "maui-windows", "xcode": "none"}
        values.update(overrides)
        return dotnet_workload.cache_key(self.root, **values)

    def test_identity_changes_with_platform_sdk_workloads_and_xcode(self):
        base = self.key()
        for change in ({"runner_os": "macOS"}, {"runner_arch": "ARM64"}, {"sdk": "10.0.403"}, {"workload_version": "9.0.101"}, {"workload": "maui-ios"}, {"xcode": "16.4"}):
            with self.subTest(change=change):
                self.assertNotEqual(base, self.key(**change))

    def test_identity_changes_with_configuration_files(self):
        before = self.key()
        (self.root / "global.json").write_text('{"sdk":{"version":"10.0.403"}}', encoding="utf-8")
        self.assertNotEqual(before, self.key())
        before = self.key()
        (self.root / ".github/actions/setup-dotnet-workload/action.yml").write_text("name: changed\n", encoding="utf-8")
        self.assertNotEqual(before, self.key())
        before = self.key()
        (self.root / "scripts/ci/dotnet-workload.py").write_text("helper changed\n", encoding="utf-8")
        self.assertNotEqual(before, self.key())


class CliTests(unittest.TestCase):
    def test_appends_cache_outputs(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "global.json").write_text('{"sdk":{"version":"10.0.402","rollForward":"disable"}}', encoding="utf-8")
            action = root / ".github/actions/setup-dotnet-workload"
            action.mkdir(parents=True)
            (action / "action.yml").write_text("action", encoding="utf-8")
            script = root / "scripts/ci/dotnet-workload.py"
            script.parent.mkdir(parents=True)
            script.write_text("helper", encoding="utf-8")
            install = root / "dotnet install"
            (install / "sdk" / "10.0.402").mkdir(parents=True)
            output = root / "output.txt"
            with patch.object(dotnet_workload, "run_dotnet", side_effect=["10.0.402", f"10.0.402 [{install / 'sdk'}]", "10.0.402"]):
                result = dotnet_workload.main(["--repo-root", str(root), "--workload-version", "9.0.100", "--workload", "maui-ios", "--github-output", str(output)], env={"RUNNER_OS": "macOS", "RUNNER_ARCH": "ARM64", "RUNNER_TEMP": str(root / "runner-temp")})
            self.assertEqual(result, 0)
            contents = output.read_text(encoding="utf-8")
            self.assertIn("cache-key=dotnet-workload-v2-", contents)
            self.assertIn(f"install-root={install}", contents)
            self.assertIn("cache-path<<", contents)
            cache_paths = contents.split("cache-path<<WORKLOAD_PATHS\n", 1)[1].split("\nWORKLOAD_PATHS", 1)[0]
            self.assertEqual(Path(cache_paths), root / "runner-temp" / "dotnet-workload-snapshot")
            self.assertNotIn(str(install), cache_paths)
            self.assertNotIn("credentials", cache_paths.lower())

    def test_dotnet_failure_returns_nonzero(self):
        with patch.object(dotnet_workload.subprocess, "run", side_effect=OSError("missing dotnet")):
            self.assertEqual(dotnet_workload.main(["--workload-version", "9.0.100", "--workload", "maui-ios"], env={"RUNNER_OS": "macOS", "RUNNER_ARCH": "ARM64"}), 1)

    def test_install_is_always_after_cache_restore(self):
        action = (Path(__file__).resolve().parents[3] / ".github/actions/setup-dotnet-workload/action.yml").read_text(encoding="utf-8")
        self.assertIn("actions/cache@v6", action)
        self.assertIn("dotnet workload install", action)
        self.assertLess(action.index("actions/cache@v6"), action.index("dotnet workload install"))
        self.assertIn("--source https://api.nuget.org/v3/index.json", action)
        self.assertNotIn("if:", action[action.index("- name: Install .NET workload"):])
        self.assertIn("--snapshot-operation restore", action)
        self.assertIn("--snapshot-operation capture", action)
        self.assertIn("--snapshot-operation discard", action)
        self.assertNotIn("dotnet workload repair", action)
        self.assertEqual(action.count("required: true"), 2)
        self.assertNotIn("workload-set", action)
        self.assertIn("--workload-version", action)
        self.assertEqual(action.count("WORKLOAD_VERSION: ${{ inputs.workload-version }}"), 2)
        self.assertIn("WORKLOAD_VERSION: ${{ inputs.workload-version }}", action[action.index("- name: Resolve workload cache"):action.index("- name: Restore workload assets")])
        self.assertIn("WORKLOAD_VERSION: ${{ inputs.workload-version }}", action[action.index("- name: Install .NET workload"):])
        self.assertIn("--workload-version \"$env:WORKLOAD_VERSION\"", action)


class WorkflowIntegrationTests(unittest.TestCase):
    @staticmethod
    def job_section(text, job):
        match = re.search(rf"(?ms)^  {re.escape(job)}:\n(.*?)(?=^  [\w-]+:\s*$|\Z)", text)
        if match is None:
            raise AssertionError(f"job not found: {job}")
        return match.group(1)

    def test_workflows_use_global_sdk_and_pinned_workload_inputs(self):
        root = Path(__file__).resolve().parents[3]
        workflows = root / ".github/workflows"
        for path in workflows.glob("*.yml"):
            text = path.read_text(encoding="utf-8")
            for block in text.split("- uses: actions/setup-dotnet@v6")[1:]:
                with self.subTest(path=path.name):
                    self.assertIn("global-json-file: global.json", block.split("\n      - ", 1)[0])
        for filename, jobs in {
            "ci.yml": ("build-maui-windows", "build-maui-android"),
            "release.yml": ("build-maui-windows", "build-maui-android"),
        }.items():
            text = (workflows / filename).read_text(encoding="utf-8")
            for job in jobs:
                section = self.job_section(text, job)
                with self.subTest(file=filename, job=job):
                    self.assertIn("uses: ./.github/actions/setup-dotnet-workload", section)
                    self.assertIn("workload-version: '10.0.402'", section)
                    self.assertNotIn("dotnet workload list", section)

    def test_apple_jobs_select_and_identify_xcode_27(self):
        root = Path(__file__).resolve().parents[3]
        for filename, job, workload in (("build-macos.yml", "build-maui-macos", "maui-maccatalyst"), ("build-ios.yml", "build-ios-archive", "maui-ios")):
            text = (root / ".github/workflows" / filename).read_text(encoding="utf-8")
            section = self.job_section(text, job)
            with self.subTest(file=filename):
                self.assertIn("runs-on: xcode-27", section)
                self.assertIn("/Applications/Xcode_27.0.app", section)
                self.assertIn("xcode-version: ${{ steps.xcode.outputs.version }}", section)
                self.assertIn("xcodebuild -version", section)
                self.assertIn(f"workload: {workload}", section)
                self.assertIn("workload-version: '10.0.402'", section)
                self.assertIn("version=$xcode_version ($xcode_build)", section)

    def test_global_sdk_pin_disables_roll_forward(self):
        root = Path(__file__).resolve().parents[3]
        sdk = json.loads((root / "global.json").read_text(encoding="utf-8"))["sdk"]
        self.assertEqual(sdk["version"], "10.0.402")
        self.assertEqual(sdk["rollForward"], "disable")

    def test_apple_xcode_parser_runs_under_bash_and_rejects_bad_output(self):
        if os.name == "nt":
            self.skipTest("Windows bash launcher does not preserve script arguments")
        bash = shutil.which("bash")
        if not bash:
            self.skipTest("bash is unavailable")
        root = Path(__file__).resolve().parents[3]
        script = (root / ".github/workflows/build-ios.yml").read_text(encoding="utf-8")
        section = self.job_section(script, "build-ios-archive")
        self.assertNotIn("mapfile", section)
        parser_lines = textwrap.dedent(section.split('xcode_info="$(xcodebuild -version)"\n', 1)[1].split("\n\n      - uses:", 1)[0])
        for value, expected, success in (
            ("Xcode 27.0\nBuild version 18A123", "version=Xcode 27.0 (18A123)", True),
            ("Xcode 26.6\nBuild version 18A123", "", False),
            ("Xcode 27.0\n", "", False),
            ("Xcode 27.0\nBuild version ", "", False),
        ):
            with self.subTest(value=value):
                encoded = base64.b64encode(value.encode("utf-8")).decode("ascii")
                parser = f'xcode_info="$(printf %s {encoded} | base64 --decode)"\n{parser_lines}'
                parser = parser.replace('>> "$GITHUB_OUTPUT"', ">> /dev/stdout") + "\n"
                result = subprocess.run([bash, "-c", parser], capture_output=True, text=True)
                self.assertEqual(result.returncode == 0, success, result.stdout + result.stderr)
                if expected:
                    self.assertIn(expected, result.stdout)


if __name__ == "__main__":
    unittest.main()
