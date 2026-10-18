import base64
import json
import os
import re
import shutil
import subprocess
import tempfile
import textwrap
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[3]
ACTION = REPO_ROOT / ".github/actions/setup-dotnet-workload/action.yml"
WORKFLOWS = REPO_ROOT / ".github/workflows"
CALLERS = {
    "ci.yml": (("build-maui-windows", "maui-windows"), ("build-maui-android", "maui-android")),
    "release.yml": (("build-maui-windows", "maui-windows"), ("build-maui-android", "maui-android")),
    "build-macos.yml": (("build-maui-macos", "maui-maccatalyst"),),
    "build-ios.yml": (("build-ios-archive", "maui-ios"),),
}


POWERSHELL = shutil.which("pwsh") or shutil.which("powershell")
SDK_VERIFICATION_STEP = "Verify selected SDK matches global.json"
PINNED_SDK = "10.0.402"


def action_text():
    return ACTION.read_text(encoding="utf-8")


def sdk_verification_block():
    action = action_text()
    start = action.index(f"- name: {SDK_VERIFICATION_STEP}")
    body = action.index("run: |\n", start) + len("run: |\n")
    lines = []
    for line in action[body:].splitlines():
        if line.strip() and not line.startswith("        "):
            break
        lines.append(line)
    return textwrap.dedent("\n".join(lines)).strip() + "\n"


def dotnet_stub(version, exit_code, marker):
    if os.name == "nt":
        return "dotnet.cmd", (
            "@echo off\r\n"
            f'> "{marker}" echo called\r\n'
            f"echo {version}\r\n"
            f"exit /b {exit_code}\r\n"
        )
    return "dotnet", (
        "#!/bin/sh\n"
        f'printf called > "{marker}"\n'
        f"printf '%s\\n' '{version}'\n"
        f"exit {exit_code}\n"
    )


def job_section(text, job):
    match = re.search(rf"(?ms)^  {re.escape(job)}:\n(.*?)(?=^  [\w-]+:\s*$|\Z)", text)
    if match is None:
        raise AssertionError(f"job not found: {job}")
    return match.group(1)


class ActionTests(unittest.TestCase):
    def test_installs_global_json_sdk_and_pinned_workload(self):
        action = action_text()
        setup = action.split("actions/setup-dotnet@v6", 1)[1].split("\n    - name:", 1)[0]
        self.assertIn("global-json-file: global.json", setup)
        self.assertIn('dotnet workload install "$env:WORKLOAD_ID"', action)
        self.assertIn('--version "$env:WORKLOAD_VERSION"', action)
        self.assertIn("--source https://api.nuget.org/v3/index.json", action)
        self.assertEqual(action.count("required: true"), 2)
        self.assertNotIn("actions/cache", action)
        self.assertNotIn("dotnet workload repair", action)

    def test_sdk_pin_check_precedes_install_and_fails_the_step(self):
        action = action_text()
        pin_check = action.index("sdk.version")
        install = action.index("- name: Install .NET workload")
        self.assertLess(action.index("actions/setup-dotnet@v6"), pin_check)
        self.assertLess(pin_check, install)
        self.assertIn("dotnet --version", action[pin_check:install])
        self.assertIn("does not match global.json pin", action[pin_check:install])
        self.assertIn("if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }", action[pin_check:install])

    def test_install_is_unconditional_propagates_failure_and_reports_timing(self):
        install = action_text()[action_text().index("- name: Install .NET workload"):]
        self.assertNotIn("if:", install)
        command = install.index("dotnet workload install")
        self.assertLess(command, install.index("if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }"))
        self.assertIn("Workload install elapsed", install)


class WorkflowIntegrationTests(unittest.TestCase):
    def test_workflows_use_global_sdk_and_pinned_workload_inputs(self):
        for path in WORKFLOWS.glob("*.yml"):
            text = path.read_text(encoding="utf-8")
            for block in text.split("- uses: actions/setup-dotnet@v6")[1:]:
                with self.subTest(path=path.name):
                    self.assertIn("global-json-file: global.json", block.split("\n      - ", 1)[0])

    def test_every_caller_pins_the_workload_and_omits_the_removed_xcode_input(self):
        count = 0
        for filename, jobs in CALLERS.items():
            text = (WORKFLOWS / filename).read_text(encoding="utf-8")
            for job, workload in jobs:
                section = job_section(text, job)
                with self.subTest(file=filename, job=job):
                    self.assertIn("uses: ./.github/actions/setup-dotnet-workload", section)
                    self.assertIn(f"workload: {workload}", section)
                    self.assertIn("workload-version: '10.0.402'", section)
                    self.assertNotIn("xcode-version:", section)
                    self.assertNotIn("dotnet workload list", section)
                count += 1
        self.assertEqual(count, 6)

    def test_apple_jobs_select_and_identify_xcode_27(self):
        for filename, job in (("build-macos.yml", "build-maui-macos"), ("build-ios.yml", "build-ios-archive")):
            section = job_section((WORKFLOWS / filename).read_text(encoding="utf-8"), job)
            with self.subTest(file=filename):
                self.assertIn("runs-on: xcode-27", section)
                self.assertIn("/Applications/Xcode_27.0.app", section)
                self.assertIn("sudo xcode-select -s", section)
                self.assertIn("xcodebuild -version", section)
                self.assertIn('== "Xcode 27.0"', section)
                self.assertIn("version=$xcode_version ($xcode_build)", section)

    def test_global_sdk_pin_disables_roll_forward(self):
        sdk = json.loads((REPO_ROOT / "global.json").read_text(encoding="utf-8"))["sdk"]
        self.assertEqual(sdk["version"], "10.0.402")
        self.assertEqual(sdk["rollForward"], "disable")

    def test_apple_xcode_parser_runs_under_bash_and_rejects_bad_output(self):
        if os.name == "nt":
            self.skipTest("Windows bash launcher does not preserve script arguments")
        bash = shutil.which("bash")
        if not bash:
            self.skipTest("bash is unavailable")
        section = job_section((WORKFLOWS / "build-ios.yml").read_text(encoding="utf-8"), "build-ios-archive")
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


class SdkVerificationBehaviorTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if POWERSHELL is None:
            raise unittest.SkipTest("PowerShell is unavailable on this platform")

    def invoke(self, block, global_json, version, exit_code):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "global.json").write_text(global_json, encoding="utf-8")
            bindir = root / "bin"
            bindir.mkdir()
            marker = root / "dotnet-invoked"
            name, stub = dotnet_stub(version, exit_code, marker)
            stub_path = bindir / name
            stub_path.write_text(stub, encoding="utf-8", newline="")
            if os.name != "nt":
                stub_path.chmod(0o755)
            script = root / "verify.ps1"
            script.write_text(block, encoding="utf-8")
            # PATH holds only the stub directory so a real SDK can never answer.
            env = dict(os.environ, PATH=str(bindir))
            result = subprocess.run(
                [POWERSHELL, "-NoProfile", "-NonInteractive", "-File", str(script)],
                cwd=str(root), env=env, capture_output=True, text=True,
            )
            return result, marker.exists()

    @staticmethod
    def pinned_global_json():
        return f'{{"sdk": {{"version": "{PINNED_SDK}", "rollForward": "disable"}}}}'

    def test_matching_sdk_pin_succeeds(self):
        result, invoked = self.invoke(
            sdk_verification_block(), self.pinned_global_json(), PINNED_SDK, 0)
        self.assertTrue(invoked)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_mismatched_sdk_fails_with_exit_code_one(self):
        result, invoked = self.invoke(
            sdk_verification_block(), self.pinned_global_json(), "10.0.401", 0)
        self.assertTrue(invoked)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("does not match global.json pin", result.stdout)

    def test_failed_dotnet_version_propagates_its_exit_code(self):
        result, invoked = self.invoke(
            sdk_verification_block(), self.pinned_global_json(), PINNED_SDK, 3)
        self.assertTrue(invoked)
        self.assertEqual(result.returncode, 3, result.stdout + result.stderr)

    def test_missing_sdk_pin_fails_without_invoking_dotnet(self):
        result, invoked = self.invoke(
            sdk_verification_block(), '{"sdk": {"rollForward": "disable"}}', PINNED_SDK, 0)
        self.assertFalse(invoked)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("does not contain an sdk.version pin", result.stdout)


if __name__ == "__main__":
    unittest.main()
