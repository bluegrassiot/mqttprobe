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
GLOBAL_JSON = REPO_ROOT / "global.json"
XCODE_PIN = REPO_ROOT / ".github/xcode-version"
SETUP_ACTION = REPO_ROOT / ".github/actions/setup-dotnet-workload/action.yml"
XCODE_ACTION = REPO_ROOT / ".github/actions/select-xcode/action.yml"
WORKFLOWS = REPO_ROOT / ".github/workflows"
PACKAGING_SCRIPT = REPO_ROOT / "scripts/packaging/velopack-macos-pack.sh"
CALLERS = {
    "ci.yml": (("build-maui-windows", "maui-windows"), ("build-maui-android", "maui-android")),
    "release.yml": (("build-maui-windows", "maui-windows"), ("build-maui-android", "maui-android")),
    "build-macos.yml": (("build-maui-macos", "maui-maccatalyst"),),
    "build-ios.yml": (("build-ios-archive", "maui-ios"),),
}
APPLE_JOBS = (("build-macos.yml", "build-maui-macos"), ("build-ios.yml", "build-ios-archive"))

POWERSHELL = shutil.which("pwsh") or shutil.which("powershell")


def find_bash():
    if os.name != "nt":
        return shutil.which("bash")
    program_files = Path(os.environ.get("ProgramFiles", r"C:\Program Files"))
    for candidate in (program_files / "Git/bin/bash.exe", program_files / "Git/usr/bin/bash.exe"):
        if candidate.is_file():
            return str(candidate)
    return None


BASH = find_bash()
SDK_VERIFICATION_STEP = "Verify selected SDK matches global.json"
WORKLOAD_SET_STEP = "Restore pinned .NET workload set"
WORKLOAD_INSTALL_STEP = "Install .NET workload"
XCODE_STEP = "Select pinned Xcode"
SHARED_SETUP = "uses: ./.github/actions/setup-dotnet-workload"
DOTNET_COMMAND = re.compile(r"(?m)^\s*(?:-\s*)?(?:run:\s*(?:\||>|-)?\s*)?dotnet\s+\S")
SDK_ONLY_JOBS = (
    ("ci.yml", "test"),
    ("ci.yml", "test-integration"),
    ("ci.yml", "build-web"),
    ("ci.yml", "build-desktop-linux"),
    ("release.yml", "build-web"),
    ("build-linux-desktop.yml", "build-desktop-linux"),
    ("build-macos-desktop.yml", "build-desktop-macos"),
)
SDK_VERSION_PATTERN = re.compile(r"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$")
WORKLOAD_VERSION_PATTERN = re.compile(r"^\d+\.\d+\.\d+$")
XCODE_VERSION_PATTERN = re.compile(r"^\d+(?:\.\d+)+$")


def global_sdk():
    return json.loads(GLOBAL_JSON.read_text(encoding="utf-8"))["sdk"]


def expected_sdk_version():
    version = global_sdk().get("version")
    if not isinstance(version, str) or not SDK_VERSION_PATTERN.match(version):
        raise AssertionError(f"global.json sdk.version is not a valid SDK version: {version!r}")
    return version


def expected_workload_version():
    version = global_sdk().get("workloadVersion")
    if not isinstance(version, str) or not WORKLOAD_VERSION_PATTERN.match(version):
        raise AssertionError(f"global.json sdk.workloadVersion is not a valid workload set version: {version!r}")
    return version


def xcode_pin_text():
    return XCODE_PIN.read_text(encoding="utf-8")


def xcode_version_pin():
    text = xcode_pin_text()
    version = text.strip()
    if len(text.splitlines()) != 1 or not XCODE_VERSION_PATTERN.match(version):
        raise AssertionError(f".github/xcode-version is not a single numeric version: {text!r}")
    return version


def global_config(**sdk):
    return json.dumps({"sdk": sdk})


def run_block(action, step_name):
    text = action.read_text(encoding="utf-8")
    start = text.index(f"- name: {step_name}")
    body = text.index("run: |\n", start) + len("run: |\n")
    lines = []
    for line in text[body:].splitlines():
        if line.strip() and not line.startswith("        "):
            break
        lines.append(line)
    return textwrap.dedent("\n".join(lines)).strip() + "\n"


def workflow_text(name):
    return (WORKFLOWS / name).read_text(encoding="utf-8")


def job_section(text, job):
    match = re.search(rf"(?ms)^  {re.escape(job)}:\n(.*?)(?=^  [\w-]+:\s*$|\Z)", text)
    if match is None:
        raise AssertionError(f"job not found: {job}")
    return match.group(1)


def job_sections(text):
    parts = text.split("\njobs:\n", 1)
    if len(parts) != 2:
        return []
    body = parts[1]
    headers = list(re.finditer(r"(?m)^  ([A-Za-z0-9_-]+):\s*$", body))
    sections = []
    for index, match in enumerate(headers):
        end = headers[index + 1].start() if index + 1 < len(headers) else len(body)
        sections.append((match.group(1), body[match.start():end]))
    return sections


def version_stub(version, exit_code, marker):
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


def recording_stub(exit_code, marker):
    if os.name == "nt":
        return "dotnet.cmd", (
            "@echo off\r\n"
            f'> "{marker}" echo %*\r\n'
            f"exit /b {exit_code}\r\n"
        )
    return "dotnet", (
        "#!/bin/sh\n"
        f'printf "%s" "$*" > "{marker}"\n'
        f"exit {exit_code}\n"
    )


def write_stub(bindir, name, text):
    path = bindir / name
    path.write_text(text, encoding="utf-8", newline="")
    if os.name != "nt":
        path.chmod(0o755)


class ActionContractTests(unittest.TestCase):
    def test_setup_action_installs_global_json_workload_from_pin(self):
        action = SETUP_ACTION.read_text(encoding="utf-8")
        self.assertEqual(action.count("actions/setup-dotnet@v6"), 1)
        setup = action.split("actions/setup-dotnet@v6", 1)[1].split("\n    - name:", 1)[0]
        self.assertIn("global-json-file: global.json", setup)
        self.assertIn('dotnet workload install "$env:WORKLOAD_ID" --source https://api.nuget.org/v3/index.json', action)
        self.assertIn("dotnet workload update --source https://api.nuget.org/v3/index.json", action)
        for step in (WORKLOAD_SET_STEP, WORKLOAD_INSTALL_STEP):
            with self.subTest(step=step):
                self.assertNotIn("--version", run_block(SETUP_ACTION, step))
        self.assertNotIn("workload-version", action)
        self.assertNotIn("actions/cache", action)
        self.assertNotIn("dotnet workload repair", action)

    def test_setup_action_workload_input_is_optional(self):
        action = SETUP_ACTION.read_text(encoding="utf-8")
        inputs = action.split("inputs:", 1)[1].split("runs:", 1)[0]
        self.assertIn("required: false", inputs)
        self.assertIn("default: ''", inputs)
        self.assertNotIn("required: true", inputs)

    def test_setup_action_selects_bootstrap_by_input(self):
        restore = run_block(SETUP_ACTION, WORKLOAD_SET_STEP)
        install = run_block(SETUP_ACTION, WORKLOAD_INSTALL_STEP)
        self.assertIn("workload update", restore)
        self.assertNotIn("workload install", restore)
        self.assertIn("workload install", install)
        self.assertNotIn("workload update", install)
        self.assertNotIn('"$env:WORKLOAD_ID"', restore)

    def test_setup_action_validates_pins_before_install(self):
        action = SETUP_ACTION.read_text(encoding="utf-8")
        pin_check = action.index("sdk.workloadVersion")
        restore = action.index(f"- name: {WORKLOAD_SET_STEP}")
        install = action.index(f"- name: {WORKLOAD_INSTALL_STEP}")
        self.assertLess(action.index("actions/setup-dotnet@v6"), pin_check)
        self.assertLess(pin_check, restore)
        self.assertLess(pin_check, install)
        self.assertIn("dotnet --version", action[pin_check:restore])
        self.assertIn("does not match global.json pin", action[pin_check:restore])
        self.assertIn("if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }", action[pin_check:restore])

    def test_xcode_action_exposes_version_output(self):
        text = XCODE_ACTION.read_text(encoding="utf-8")
        self.assertIn("outputs:", text)
        self.assertIn("${{ steps.select.outputs.version }}", text)


class WorkflowIntegrationTests(unittest.TestCase):
    def test_workflows_never_bootstrap_the_sdk_directly(self):
        for path in sorted(WORKFLOWS.glob("*.yml")):
            with self.subTest(path=path.name):
                text = path.read_text(encoding="utf-8")
                self.assertNotIn("setup-dotnet@", text)
                self.assertNotIn("workload-version", text)

    def test_every_dotnet_job_bootstraps_before_first_dotnet_command(self):
        checked = 0
        for path in sorted(WORKFLOWS.glob("*.yml")):
            for job, section in job_sections(path.read_text(encoding="utf-8")):
                commands = list(DOTNET_COMMAND.finditer(section))
                if not commands:
                    continue
                with self.subTest(path=path.name, job=job):
                    shared = section.find(SHARED_SETUP)
                    self.assertGreaterEqual(shared, 0, "job runs dotnet without the shared setup action")
                    self.assertLess(shared, commands[0].start())
                checked += 1
        self.assertEqual(checked, 13)

    def test_sdk_only_jobs_use_shared_setup_without_a_workload(self):
        for filename, job in SDK_ONLY_JOBS:
            section = job_section(workflow_text(filename), job)
            with self.subTest(file=filename, job=job):
                self.assertIn(SHARED_SETUP, section)
                self.assertIsNone(re.search(r"(?m)^\s+workload:", section))

    def test_every_caller_installs_a_workload_without_version_override(self):
        count = 0
        for filename, jobs in CALLERS.items():
            text = workflow_text(filename)
            for job, workload in jobs:
                section = job_section(text, job)
                with self.subTest(file=filename, job=job):
                    self.assertIn("uses: ./.github/actions/setup-dotnet-workload", section)
                    self.assertIn(f"workload: {workload}", section)
                    self.assertNotIn("workload-version", section)
                    self.assertNotIn("xcode-version:", section)
                    self.assertNotIn("dotnet workload list", section)
                count += 1
        self.assertEqual(count, 6)

    def test_apple_jobs_use_shared_xcode_action(self):
        pin = xcode_version_pin()
        for filename, job in APPLE_JOBS:
            section = job_section(workflow_text(filename), job)
            with self.subTest(file=filename):
                self.assertIn("runs-on: xcode-27", section)
                self.assertIn("uses: ./.github/actions/select-xcode", section)
                self.assertIn("id: xcode", section)
                self.assertNotIn("Xcode_", section)
                self.assertNotIn("xcodebuild", section)
                self.assertNotIn("sudo xcode-select", section)
                self.assertNotIn(pin, section)

    def test_central_pins_are_single_numeric_values(self):
        self.assertRegex(expected_sdk_version(), SDK_VERSION_PATTERN)
        self.assertRegex(expected_workload_version(), WORKLOAD_VERSION_PATTERN)
        text = xcode_pin_text()
        self.assertEqual(len(text.splitlines()), 1)
        self.assertRegex(xcode_version_pin(), XCODE_VERSION_PATTERN)

    def test_xcode_pin_consumers_read_the_central_file(self):
        for path in (XCODE_ACTION, PACKAGING_SCRIPT):
            with self.subTest(path=str(path.relative_to(REPO_ROOT))):
                self.assertIn(".github/xcode-version", path.read_text(encoding="utf-8"))

    def test_production_config_does_not_duplicate_the_xcode_pin(self):
        pin = xcode_version_pin()
        for path in [*WORKFLOWS.glob("*.yml"), XCODE_ACTION]:
            with self.subTest(path=str(path.relative_to(REPO_ROOT))):
                self.assertNotIn(pin, path.read_text(encoding="utf-8"))


class SdkPinBehaviorTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if POWERSHELL is None:
            raise unittest.SkipTest("PowerShell is unavailable on this platform")
        cls.block = run_block(SETUP_ACTION, SDK_VERIFICATION_STEP)

    def invoke(self, config, version, exit_code):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "global.json").write_text(config, encoding="utf-8")
            bindir = root / "bin"
            bindir.mkdir()
            marker = root / "dotnet-invoked"
            name, stub = version_stub(version, exit_code, marker)
            write_stub(bindir, name, stub)
            script = root / "verify.ps1"
            script.write_text(self.block, encoding="utf-8")
            env = dict(os.environ, PATH=str(bindir))
            result = subprocess.run(
                [POWERSHELL, "-NoProfile", "-NonInteractive", "-File", str(script)],
                cwd=str(root), env=env, capture_output=True, text=True,
            )
            return result, marker.exists()

    def test_matching_pins_succeed(self):
        result, invoked = self.invoke(
            global_config(version=expected_sdk_version(), rollForward="disable",
                          workloadVersion=expected_workload_version()),
            expected_sdk_version(), 0)
        self.assertTrue(invoked)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_missing_workload_pin_fails_before_dotnet(self):
        result, invoked = self.invoke(
            global_config(version=expected_sdk_version(), rollForward="disable"),
            expected_sdk_version(), 0)
        self.assertFalse(invoked)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("valid sdk.workloadVersion pin", result.stdout)

    def test_malformed_workload_pin_fails_before_dotnet(self):
        for value in ("", "10.0", "10.0.403-preview", 100403):
            with self.subTest(value=value):
                result, invoked = self.invoke(
                    global_config(version=expected_sdk_version(), rollForward="disable", workloadVersion=value),
                    expected_sdk_version(), 0)
                self.assertFalse(invoked)
                self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
                self.assertIn("valid sdk.workloadVersion pin", result.stdout)

    def test_missing_sdk_pin_fails_before_dotnet(self):
        result, invoked = self.invoke(
            global_config(rollForward="disable", workloadVersion=expected_workload_version()),
            expected_sdk_version(), 0)
        self.assertFalse(invoked)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("does not contain an sdk.version pin", result.stdout)

    def test_sdk_and_workload_pins_are_independent(self):
        for sdk, workload in (("10.0.999", "10.0.403"), ("10.0.999", "10.1.0")):
            with self.subTest(sdk=sdk, workload=workload):
                result, invoked = self.invoke(
                    global_config(version=sdk, rollForward="disable", workloadVersion=workload), sdk, 0)
                self.assertTrue(invoked)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_workload_pin_is_not_accepted_as_sdk_pin(self):
        result, invoked = self.invoke(
            global_config(version="10.0.999", rollForward="disable",
                          workloadVersion=expected_workload_version()),
            expected_workload_version(), 0)
        self.assertTrue(invoked)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("does not match global.json pin", result.stdout)

    def test_mismatched_sdk_fails_with_exit_code_one(self):
        result, invoked = self.invoke(
            global_config(version=expected_sdk_version(), rollForward="disable",
                          workloadVersion=expected_workload_version()),
            "10.0.998", 0)
        self.assertTrue(invoked)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("does not match global.json pin", result.stdout)

    def test_failed_version_command_propagates_its_exit_code(self):
        result, invoked = self.invoke(
            global_config(version=expected_sdk_version(), rollForward="disable",
                          workloadVersion=expected_workload_version()),
            expected_sdk_version(), 3)
        self.assertTrue(invoked)
        self.assertEqual(result.returncode, 3, result.stdout + result.stderr)


class WorkloadSetRestoreBehaviorTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if POWERSHELL is None:
            raise unittest.SkipTest("PowerShell is unavailable on this platform")
        cls.block = run_block(SETUP_ACTION, WORKLOAD_SET_STEP)

    def invoke(self, exit_code):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            bindir = root / "bin"
            bindir.mkdir()
            marker = root / "dotnet-args"
            name, stub = recording_stub(exit_code, marker)
            write_stub(bindir, name, stub)
            script = root / "restore.ps1"
            script.write_text(self.block, encoding="utf-8")
            env = dict(os.environ, PATH=str(bindir))
            result = subprocess.run(
                [POWERSHELL, "-NoProfile", "-NonInteractive", "-File", str(script)],
                cwd=str(root), env=env, capture_output=True, text=True,
            )
            return result, marker.read_text(encoding="utf-8") if marker.exists() else ""

    def test_restore_updates_pinned_workload_set_without_install_or_version(self):
        result, arguments = self.invoke(0)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("workload update", arguments)
        self.assertIn("--source https://api.nuget.org/v3/index.json", arguments)
        self.assertNotIn("workload install", arguments)
        self.assertNotIn("--version", arguments)
        self.assertIn("Workload set restore elapsed", result.stdout)

    def test_restore_failure_propagates_exit_code(self):
        result, _ = self.invoke(3)
        self.assertEqual(result.returncode, 3, result.stdout + result.stderr)


class WorkloadInstallBehaviorTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if POWERSHELL is None:
            raise unittest.SkipTest("PowerShell is unavailable on this platform")
        cls.block = run_block(SETUP_ACTION, WORKLOAD_INSTALL_STEP)

    def invoke(self, exit_code):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            bindir = root / "bin"
            bindir.mkdir()
            marker = root / "dotnet-args"
            name, stub = recording_stub(exit_code, marker)
            write_stub(bindir, name, stub)
            script = root / "install.ps1"
            script.write_text(self.block, encoding="utf-8")
            env = dict(os.environ, PATH=str(bindir), WORKLOAD_ID="maui-ios")
            result = subprocess.run(
                [POWERSHELL, "-NoProfile", "-NonInteractive", "-File", str(script)],
                cwd=str(root), env=env, capture_output=True, text=True,
            )
            return result, marker.read_text(encoding="utf-8") if marker.exists() else ""

    def test_install_uses_pinned_workload_without_version_flag(self):
        result, arguments = self.invoke(0)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("workload install", arguments)
        self.assertIn("maui-ios", arguments)
        self.assertIn("--source https://api.nuget.org/v3/index.json", arguments)
        self.assertNotIn("--version", arguments)
        self.assertIn("Workload install elapsed", result.stdout)

    def test_install_failure_propagates_exit_code(self):
        result, _ = self.invoke(3)
        self.assertEqual(result.returncode, 3, result.stdout + result.stderr)


class SelectXcodeBehaviorTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if BASH is None:
            raise unittest.SkipTest("bash is unavailable")
        cls.block = run_block(XCODE_ACTION, XCODE_STEP)
        cls.pin = xcode_version_pin()

    def invoke(self, pin_content, xcode_output, app_exists=True):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            repo = root / "repo"
            (repo / ".github").mkdir(parents=True)
            pin_file = repo / ".github/xcode-version"
            if pin_content is not None:
                pin_file.write_text(pin_content, encoding="utf-8", newline="")
            apps = root / "Applications"
            apps.mkdir()
            if app_exists and pin_content is not None:
                expected = pin_content.strip()
                if XCODE_VERSION_PATTERN.match(expected):
                    (apps / f"Xcode_{expected}.app").mkdir()

            bindir = root / "bin"
            bindir.mkdir()
            selected = root / "selected"
            write_stub(bindir, "sudo", '#!/bin/sh\nexec "$@"\n')
            write_stub(
                bindir,
                "xcode-select",
                f'#!/bin/sh\nprintf "%s" "$*" > "{selected.as_posix()}"\n',
            )
            xcode_output_file = root / "xcodebuild.out"
            xcode_output_file.write_text(xcode_output, encoding="utf-8", newline="")
            write_stub(
                bindir,
                "xcodebuild",
                f'#!/bin/sh\ncat "{xcode_output_file.as_posix()}"\n',
            )

            script_text = self.block.replace(
                'XCODE_APP="/Applications/Xcode_${pin}.app"',
                f'XCODE_APP="{apps.as_posix()}/Xcode_${{pin}}.app"',
            )
            self.assertIn(apps.as_posix(), script_text)
            script = root / "select.sh"
            script.write_text(script_text, encoding="utf-8", newline="\n")
            github_output = root / "github-output"
            github_output.write_text("", encoding="utf-8")
            env = dict(
                os.environ,
                PATH=f"{bindir}{os.pathsep}{os.environ.get('PATH', '')}",
                GITHUB_WORKSPACE=repo.as_posix(),
                GITHUB_OUTPUT=github_output.as_posix(),
            )
            result = subprocess.run(
                [BASH, script.as_posix()], env=env, capture_output=True, text=True,
            )
            output = github_output.read_text(encoding="utf-8")
            selection = selected.read_text(encoding="utf-8") if selected.exists() else ""
            return result, output, selection

    def test_matching_pin_selects_app_and_emits_version(self):
        for pin_content in (f"{self.pin}\n", f"{self.pin}\r\n"):
            with self.subTest(pin=repr(pin_content)):
                result, output, selection = self.invoke(
                    pin_content, f"Xcode {self.pin}\nBuild version 18A123")
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertIn(f"version=Xcode {self.pin} (18A123)", output)
                self.assertIn("-s", selection)
                self.assertIn(f"Xcode_{self.pin}.app", selection)

    def test_mismatched_xcode_version_fails(self):
        result, output, _ = self.invoke(f"{self.pin}\n", "Xcode 26.6\nBuild version 18A123")
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("Unexpected Xcode version", result.stdout)
        self.assertEqual(output, "")

    def test_missing_or_empty_build_fails(self):
        for xcode_output in (f"Xcode {self.pin}\n", f"Xcode {self.pin}\nBuild version "):
            with self.subTest(output=repr(xcode_output)):
                result, _, _ = self.invoke(f"{self.pin}\n", xcode_output)
                self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
                self.assertIn("Xcode build version unavailable", result.stdout)

    def test_missing_pin_file_fails(self):
        result, _, _ = self.invoke(None, f"Xcode {self.pin}\nBuild version 18A123")
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("Invalid Xcode version pin", result.stdout)

    def test_malformed_pin_fails(self):
        for pin_content in ("", "27\n", "27.x\n", " 27.1\n", f"{self.pin}\n27.2\n"):
            with self.subTest(pin=repr(pin_content)):
                result, _, _ = self.invoke(pin_content, f"Xcode {self.pin}\nBuild version 18A123")
                self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
                self.assertIn("Invalid Xcode version pin", result.stdout)

    def test_missing_xcode_app_fails(self):
        result, _, _ = self.invoke(f"{self.pin}\n", f"Xcode {self.pin}\nBuild version 18A123", app_exists=False)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("Xcode not found at", result.stdout)

    def test_action_is_bash_32_compatible(self):
        text = XCODE_ACTION.read_text(encoding="utf-8")
        self.assertNotIn("mapfile", text)
        self.assertNotRegex(text, r"\bjq\b")


if __name__ == "__main__":
    unittest.main()
