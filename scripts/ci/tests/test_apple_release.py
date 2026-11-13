#!/usr/bin/env python3
"""Tests for scripts/ci/apple-build-number.py and the release.yml Apple lanes.

The workflow tests read .github/workflows/release.yml as text: no YAML library is
available on the runners, and the assertions are about job wiring rather than
document structure.

Run: python -m unittest discover -s scripts/ci/tests
"""

import contextlib
import importlib.util
import io
import os
import re
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
RELEASE_YML = ROOT / ".github/workflows/release.yml"
RELEASE_ASSETS = ROOT / ".github/release-assets.txt"

_SPEC = importlib.util.spec_from_file_location(
    "ci_apple_build_number",
    Path(__file__).resolve().parents[1] / "apple-build-number.py",
)
if _SPEC is None or _SPEC.loader is None:
    raise SystemExit("Cannot load scripts/ci/apple-build-number.py")
apple_build_number = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(apple_build_number)

RELEASE = RELEASE_YML.read_text(encoding="utf-8")

EXISTING_BUILD_JOBS = (
    "build-web",
    "build-docker",
    "build-maui-windows",
    "build-maui-android",
    "build-macos-desktop",
    "build-desktop-linux",
)
APPLE_CALLERS = {
    "build-ios-testflight": "./.github/workflows/build-ios.yml",
    "build-macos-testflight": "./.github/workflows/build-macos-app-store.yml",
}
EXISTING_DOWNLOADS = {
    "web-linux-x64",
    "web-linux-arm64",
    "web-win-x64",
    "maui-windows",
    "maui-windows-velopack",
    "desktop-macos-velopack",
    "maui-android",
    "desktop-linux-x64",
    "desktop-linux-velopack",
    "desktop-linux-third-party-sources",
}
TAG_PATTERN = re.compile(r'\[\[\s*!?\s*"\$RAW_TAG"\s*=~\s*(\S+?)\s*\]\]')
STEP_RUN = re.compile(r"^(?P<indent> +)run:\s*\|\s*$")


def job_bodies(text):
    jobs = {}
    current = None
    inside = False
    for line in text.splitlines():
        if line.rstrip() == "jobs:":
            inside = True
            continue
        if not inside:
            continue
        if line and not line.startswith(" "):
            break
        match = re.match(r"^  ([A-Za-z0-9_-]+):\s*$", line)
        if match:
            current = match.group(1)
            jobs[current] = []
        elif current is not None:
            jobs[current].append(line)
    return {name: "\n".join(lines) for name, lines in jobs.items()}


JOBS = job_bodies(RELEASE)


def _block(lines, key):
    header = f"    {key}:"
    try:
        start = lines.index(header)
    except ValueError:
        return []
    collected = []
    for line in lines[start + 1:]:
        if line.strip() and not line.startswith("      "):
            break
        collected.append(line)
    return collected


def needs_of(body):
    lines = body.splitlines()
    match = re.search(r"^    needs:(.*)$", body, re.MULTILINE)
    if match is None:
        return []
    inline = match.group(1).strip()
    if not inline:
        return [
            line.strip()[2:].strip()
            for line in _block(lines, "needs")
            if line.strip().startswith("- ")
        ]
    if inline.startswith("[") and inline.endswith("]"):
        return [name.strip() for name in inline[1:-1].split(",") if name.strip()]
    return [inline]


def with_inputs(body):
    inputs = {}
    for line in _block(body.splitlines(), "with"):
        if not line.strip():
            continue
        name, _, value = line.strip().partition(":")
        inputs[name] = value.strip()
    return inputs


def download_names(body):
    return set(re.findall(r"^          name: (\S+)$", body, re.MULTILINE))


def run_bodies(body):
    lines = body.splitlines()
    bodies = []
    index = 0
    while index < len(lines):
        match = STEP_RUN.match(lines[index])
        if match is None:
            index += 1
            continue
        indent = len(match.group("indent"))
        index += 1
        collected = []
        while index < len(lines):
            line = lines[index]
            if line.strip() and len(line) - len(line.lstrip()) <= indent:
                break
            collected.append(line)
            index += 1
        bodies.append("\n".join(collected))
    return bodies


def step_run(body, step_name):
    lines = body.splitlines()
    marker = f"      - name: {step_name}"
    if marker not in lines:
        return None
    index = lines.index(marker) + 1
    while index < len(lines):
        line = lines[index]
        stripped = line.strip()
        if stripped.startswith("- ") and len(line) - len(line.lstrip()) <= 6:
            return None
        if stripped == "run: |":
            indent = len(line) - len(line.lstrip())
            collected = []
            index += 1
            while index < len(lines):
                inner = lines[index]
                if inner.strip() and len(inner) - len(inner.lstrip()) <= indent:
                    break
                collected.append(inner)
                index += 1
            return "\n".join(collected)
        index += 1
    return None


def find_bash():
    if os.name != "nt":
        return shutil.which("bash")
    program_files = Path(os.environ.get("ProgramFiles", r"C:\Program Files"))
    for candidate in (program_files / "Git/bin/bash.exe", program_files / "Git/usr/bin/bash.exe"):
        if candidate.is_file():
            return str(candidate)
    return None


BASH = find_bash()


class BuildNumberTests(unittest.TestCase):
    def test_derives_run_and_attempt(self):
        self.assertEqual(apple_build_number.derive(1, 1), 11)
        self.assertEqual(apple_build_number.derive(1, 2), 12)
        self.assertEqual(apple_build_number.derive(2, 1), 21)
        self.assertEqual(apple_build_number.derive(99, 5), 995)

    def test_accepts_highest_encodable_pair(self):
        self.assertEqual(apple_build_number.derive(999, 9), 9999)

    def test_attempt_is_the_last_digit(self):
        for run, attempt in ((1, 3), (17, 9), (400, 1)):
            with self.subTest(run=run, attempt=attempt):
                build = apple_build_number.derive(run, attempt)
                self.assertEqual(build // 10, run)
                self.assertEqual(build % 10, attempt)

    def test_retries_never_reuse_an_earlier_number(self):
        seen = [
            apple_build_number.derive(run, attempt)
            for run in range(1, 200)
            for attempt in range(1, 10)
        ]
        self.assertEqual(len(seen), len(set(seen)))
        self.assertEqual(seen, sorted(seen))

    def test_attempt_nine_precedes_the_next_run(self):
        self.assertLess(
            apple_build_number.derive(7, 9),
            apple_build_number.derive(8, 1),
        )

    def test_rejects_non_positive_run_number(self):
        for run in (0, -1, -100):
            with self.subTest(run=run):
                with self.assertRaises(apple_build_number.BuildNumberError):
                    apple_build_number.derive(run, 1)

    def test_rejects_attempt_outside_one_to_nine(self):
        for attempt in (0, -1, 10, 99):
            with self.subTest(attempt=attempt):
                with self.assertRaises(apple_build_number.BuildNumberError):
                    apple_build_number.derive(1, attempt)

    def test_attempt_beyond_nine_explains_the_collision(self):
        with self.assertRaises(apple_build_number.BuildNumberError) as raised:
            apple_build_number.derive(5, 10)
        message = str(raised.exception)
        self.assertIn("boundary", message)
        self.assertIn("collides", message)

    def test_rejects_numbers_over_the_apple_ceiling(self):
        with self.assertRaises(apple_build_number.BuildNumberError) as raised:
            apple_build_number.derive(1000, 1)
        message = str(raised.exception)
        self.assertIn("10001", message)
        self.assertIn("9999", message)

    def test_ceiling_is_not_wrapped(self):
        for run, attempt in ((1000, 9), (5000, 1), (100000, 4)):
            with self.subTest(run=run, attempt=attempt):
                with self.assertRaises(apple_build_number.BuildNumberError):
                    apple_build_number.derive(run, attempt)

    def test_ceiling_message_names_the_last_usable_run_number(self):
        with self.assertRaises(apple_build_number.BuildNumberError) as raised:
            apple_build_number.derive(1000, 5)
        self.assertIn("run 999 attempt 9", str(raised.exception))

    def test_errors_stop_the_release_instead_of_offering_a_manual_upload(self):
        for run, attempt in ((1000, 1), (5, 10)):
            with self.subTest(run=run, attempt=attempt):
                with self.assertRaises(apple_build_number.BuildNumberError) as raised:
                    apple_build_number.derive(run, attempt)
                message = str(raised.exception).lower()
                self.assertIn("build-number policy", message)
                self.assertIn("does not unblock the release", message)
                self.assertNotIn("manually", message)
                self.assertNotIn("dispatch", message)

    def test_cli_prints_only_the_build_number(self):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = apple_build_number.main(["--run-number", "42", "--run-attempt", "3"])
        self.assertEqual(code, 0)
        self.assertEqual(out.getvalue(), "423\n")

    def test_cli_failure_writes_no_build_number(self):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = apple_build_number.main(["--run-number", "1000", "--run-attempt", "1"])
        self.assertEqual(code, 1)
        self.assertEqual(out.getvalue(), "")
        self.assertIn("::error::", err.getvalue())
        self.assertIn("9999", err.getvalue())

    def test_cli_rejects_non_numeric_counters(self):
        for bad in (("abc", "1"), ("1", "1.5"), ("1e3", "1"), ("v1", "1")):
            with self.subTest(bad=bad):
                out = io.StringIO()
                with contextlib.redirect_stdout(out), contextlib.redirect_stderr(io.StringIO()):
                    with self.assertRaises(SystemExit):
                        apple_build_number.main(
                            ["--run-number", bad[0], "--run-attempt", bad[1]]
                        )
                self.assertEqual(out.getvalue(), "")


class ReleaseTagGateTests(unittest.TestCase):
    def setUp(self):
        match = TAG_PATTERN.search(RELEASE)
        self.assertIsNotNone(match, "release.yml has no RAW_TAG pattern match")
        self.pattern = re.compile(match.group(1))

    def test_accepts_releasable_tags(self):
        for tag in ("v1.2.3", "v0.0.1", "v10.20.30", "v9999.9999.9999", "v0.0.9999"):
            with self.subTest(tag=tag):
                self.assertTrue(self.pattern.fullmatch(tag), tag)

    def test_rejects_anything_but_a_releasable_tag(self):
        for tag in (
            "1.2.3",
            "V1.2.3",
            "v1.2",
            "v1",
            "v1.2.3.4",
            "v1.2.3-rc1",
            "v1.2.3+build.5",
            "v1.2.3 ",
            " v1.2.3",
            "v1.2.3; echo",
            "v1.2.3$(id)",
            "release-v1.2.3",
            "vx.y.z",
            "v1..3",
            "latest",
            "",
        ):
            with self.subTest(tag=tag):
                self.assertFalse(self.pattern.fullmatch(tag), tag)

    def test_rejects_components_apple_would_refuse(self):
        for tag in (
            "v10000.0.0",
            "v0.10000.0",
            "v1.2.10000",
            "v01.2.3",
            "v1.02.3",
            "v1.2.03",
            "v-1.2.3",
            "v1.2.-3",
        ):
            with self.subTest(tag=tag):
                self.assertFalse(self.pattern.fullmatch(tag), tag)

    def test_gate_validates_with_inline_shell_only(self):
        body = JOBS["verify-release"]
        self.assertNotIn("scripts/", body)
        self.assertNotIn("./.github/actions", body)
        self.assertNotIn("secrets.", body)

    def test_tag_name_is_passed_through_the_environment(self):
        body = JOBS["verify-release"]
        self.assertIn("RAW_TAG: ${{ github.ref_name }}", body)
        for run in run_bodies(body):
            with self.subTest(run=run.splitlines()[:1]):
                self.assertNotIn("${{", run)

    def test_triggering_sha_is_peeled_and_compared(self):
        body = JOBS["verify-release"]
        self.assertIn('git rev-parse --verify "${EVENT_SHA}^{commit}"', body)
        self.assertIn('git rev-parse --verify "refs/tags/${RAW_TAG}^{commit}"', body)
        self.assertIn("EVENT_SHA: ${{ github.sha }}", body)

    def test_main_is_fetched_before_ancestry_is_checked(self):
        body = JOBS["verify-release"]
        fetch = body.find("+refs/heads/main:refs/remotes/origin/main")
        ancestry = body.find("git merge-base --is-ancestor")
        self.assertGreaterEqual(fetch, 0, "release.yml does not fetch main")
        self.assertGreaterEqual(ancestry, 0, "release.yml never checks ancestry")
        self.assertLess(fetch, ancestry)
        self.assertIn('"$TAG_COMMIT" origin/main', body)

    def test_checkout_keeps_full_history_without_credentials_or_submodules(self):
        body = JOBS["verify-release"]
        self.assertIn("fetch-depth: 0", body)
        self.assertIn("persist-credentials: false", body)
        self.assertNotIn("submodules:", body)

    def test_ancestry_gate_runs_for_a_git_only_checkout(self):
        body = JOBS["verify-release"]
        self.assertIn("--is-shallow-repository", body)
        self.assertIn("contents: read", body)

    def test_main_fetch_failure_blocks_the_release(self):
        body = JOBS["verify-release"]
        self.assertNotIn("::warning::", body)
        script = step_run(body, "Require the tag commit on main")
        self.assertIsNotNone(script)
        lines = script.splitlines()
        fetch = next(
            (index for index, line in enumerate(lines) if "if ! git fetch" in line), None
        )
        self.assertIsNotNone(fetch, "release.yml does not guard the main fetch")
        window = lines[fetch:fetch + 4]
        self.assertTrue(any("::error::" in line for line in window), window)
        self.assertTrue(any(line.strip() == "exit 1" for line in window), window)

    def test_outputs_carry_the_tag_and_commit(self):
        body = JOBS["verify-release"]
        self.assertIn("version: ${{ steps.tag.outputs.version }}", body)
        self.assertIn("commit: ${{ steps.resolve.outputs.commit }}", body)


@unittest.skipUnless(BASH, "bash is required to execute the workflow shell")
class TagGateShellTests(unittest.TestCase):
    def run_gate(self, tag):
        script = step_run(JOBS["verify-release"], "Validate tag name")
        self.assertIsNotNone(script)
        with tempfile.TemporaryDirectory() as tmp:
            output = Path(tmp) / "output"
            output.write_text("", encoding="utf-8")
            env = {**os.environ, "RAW_TAG": tag, "GITHUB_OUTPUT": output.as_posix()}
            result = subprocess.run(
                [BASH, "-eo", "pipefail", "-c", script],
                env=env,
                capture_output=True,
                text=True,
            )
            return result, output.read_text(encoding="utf-8")

    def test_accepts_releasable_tags(self):
        for tag in ("v1.2.3", "v0.0.1", "v9999.9999.9999"):
            with self.subTest(tag=tag):
                result, output = self.run_gate(tag)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertIn(f"version={tag}", output)

    def test_rejects_the_placeholder_version(self):
        result, output = self.run_gate("v0.0.0")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("placeholder", result.stdout)
        self.assertNotIn("version=", output)

    def test_rejects_tags_apple_or_the_release_rules_refuse(self):
        for tag in (
            "v10000.0.0",
            "v01.2.3",
            "v1.2.3-rc1",
            "1.2.3",
            "v1.2.3; echo pwned",
            "v1.2.3$(id)",
        ):
            with self.subTest(tag=tag):
                result, output = self.run_gate(tag)
                self.assertNotEqual(result.returncode, 0)
                self.assertNotIn("version=", output)


class ReleaseGraphTests(unittest.TestCase):
    def test_every_build_job_waits_for_the_tag_gate(self):
        for job in EXISTING_BUILD_JOBS:
            with self.subTest(job=job):
                self.assertIn(job, JOBS, f"release.yml lost the {job} job")
                self.assertIn("verify-release", needs_of(JOBS[job]))

    def test_apple_lanes_call_the_reusable_workflows(self):
        for job, workflow in APPLE_CALLERS.items():
            with self.subTest(job=job):
                body = JOBS[job]
                self.assertIn(f"uses: {workflow}", body)
                self.assertIn("secrets: inherit", body)
                self.assertIn("contents: read", body)
                self.assertEqual(needs_of(body), ["verify-release"])

    def test_apple_lanes_receive_only_the_marketing_version(self):
        for job in APPLE_CALLERS:
            with self.subTest(job=job):
                self.assertEqual(
                    with_inputs(JOBS[job]),
                    {"version": "${{ needs.verify-release.outputs.version }}"},
                )

    def test_apple_lanes_upload_nothing_to_the_run(self):
        for job in APPLE_CALLERS:
            with self.subTest(job=job):
                body = JOBS[job]
                self.assertNotIn("upload-artifact", body)
                self.assertNotIn("download-artifact", body)

    def test_github_release_waits_for_both_apple_uploads(self):
        needs = needs_of(JOBS["create-release"])
        self.assertEqual(
            set(needs),
            set(EXISTING_BUILD_JOBS) | {"verify-release"} | set(APPLE_CALLERS),
        )
        self.assertEqual(len(needs), len(set(needs)), "create-release needs duplicates")
        self.assertIn("verify-release", needs)

    def test_existing_release_downloads_are_untouched(self):
        self.assertEqual(download_names(JOBS["create-release"]), EXISTING_DOWNLOADS)

    def test_create_release_downloads_no_apple_binary(self):
        body = JOBS["create-release"]
        self.assertNotIn("upload-artifact", body)
        for name in download_names(body):
            lowered = name.lower()
            for token in ("ios", "testflight", "maccatalyst", "app-store", "ipa"):
                with self.subTest(name=name):
                    self.assertNotIn(token, lowered)
        self.assertIn("files: ${{ steps.assets.outputs.list }}", body)

    def test_release_runs_are_serialised_per_tag(self):
        self.assertIn("concurrency:\n  group: release-${{ github.ref }}", RELEASE)
        self.assertIn("cancel-in-progress: false", RELEASE)

    def test_release_assets_manifest_has_no_apple_binary(self):
        entries = [
            line.strip()
            for line in RELEASE_ASSETS.read_text(encoding="utf-8").splitlines()
            if line.strip() and not line.strip().startswith("#")
        ]
        for entry in entries:
            lowered = entry.lower()
            for token in ("ipa", "testflight", "maccatalyst", "app-store", "appstore"):
                with self.subTest(entry=entry):
                    self.assertNotIn(token, lowered)


if __name__ == "__main__":
    unittest.main()
