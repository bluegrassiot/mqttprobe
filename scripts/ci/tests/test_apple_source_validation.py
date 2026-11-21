#!/usr/bin/env python3
"""Executable tests for the Apple source-validation and signing shells.

These run the real shell extracted from the workflows against a throwaway git
remote. They do not run GitHub Actions or any Apple signing tool.
"""

import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
IOS_WORKFLOW = ROOT / ".github" / "workflows" / "build-ios.yml"
MAC_WORKFLOW = ROOT / ".github" / "workflows" / "build-macos-app-store.yml"
VALIDATION_JOB = "validate-source"
IOS_JOB = "build-ios-archive"
MAC_JOB = "build-macos-app-store"
VALIDATION_STEP = "Validate tag and source"
VERSION_STEP = "Derive and validate version and build number"


def find_bash():
    if os.name != "nt":
        return shutil.which("bash")
    program_files = Path(os.environ.get("ProgramFiles", r"C:\Program Files"))
    for candidate in (program_files / "Git/bin/bash.exe", program_files / "Git/usr/bin/bash.exe"):
        if candidate.is_file():
            return str(candidate)
    return None


BASH = find_bash()


def load_job(name, text):
    lines = text.splitlines()
    start = lines.index(f"  {name}:")
    block = [lines[start]]
    for line in lines[start + 1:]:
        if line.startswith("  ") and not line.startswith("   "):
            break
        block.append(line)
    return "\n".join(block)


def load_step(name, job):
    lines = job.splitlines()
    start = lines.index(f"      - name: {name}")
    block = [lines[start]]
    for line in lines[start + 1:]:
        if line.startswith("      - "):
            break
        block.append(line)
    return "\n".join(block)


def step_run(step):
    lines = step.splitlines()
    start = lines.index("        run: |")
    return "\n".join(lines[start + 1:])


@unittest.skipUnless(BASH, "bash is required")
class AppleSourceValidationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory()
        cls.root = Path(cls.temp.name)
        cls.python_shim = cls.root / "python-shim.sh"
        cls.python_shim.write_text(
            "python3() {\n"
            f'  exec "{sys.executable.replace(chr(92), "/")}" "$@"\n'
            "}\n",
            encoding="utf-8",
            newline="\n",
        )
        cls.origin = cls.root / "origin.git"
        cls.seed = cls.root / "seed"
        cls.repo = cls.root / "repo"
        cls.git_env = {
            **os.environ,
            "GIT_AUTHOR_NAME": "Test",
            "GIT_AUTHOR_EMAIL": "test@example.invalid",
            "GIT_COMMITTER_NAME": "Test",
            "GIT_COMMITTER_EMAIL": "test@example.invalid",
        }
        cls._git(cls.root, "init", "--bare", str(cls.origin))
        cls._git(cls.root, "init", str(cls.seed))
        cls._git(cls.seed, "checkout", "-b", "main")
        cls._write(cls.seed / "file.txt", "one")
        cls._commit(cls.seed, "one")
        cls._git(cls.seed, "tag", "v1.0.0")
        cls._git(cls.seed, "tag", "-a", "v1.1.0", "-m", "annotated")
        cls._git(cls.seed, "remote", "add", "origin", str(cls.origin))
        cls._git(cls.seed, "push", "-u", "origin", "main")
        cls._git(cls.seed, "push", "origin", "--tags")
        cls._git(cls.seed, "checkout", "-b", "side")
        cls._write(cls.seed / "file.txt", "side")
        cls._commit(cls.seed, "side")
        cls._git(cls.seed, "tag", "v2.0.0")
        cls._git(cls.seed, "branch", "release/1.2")
        cls._git(cls.seed, "branch", "release/hotfix/1.2")
        cls._git(cls.seed, "branch", "release/2.0")
        cls._git(
            cls.seed,
            "push",
            "origin",
            "side",
            "release/1.2",
            "release/hotfix/1.2",
            "release/2.0",
        )
        cls._git(cls.seed, "push", "origin", "v2.0.0")
        cls._git(cls.seed, "checkout", "main")
        cls._write(cls.seed / "file.txt", "three")
        cls._commit(cls.seed, "three")
        cls._git(cls.seed, "push", "origin", "main")
        cls._git(cls.origin, "symbolic-ref", "HEAD", "refs/heads/main")
        cls._git(cls.root, "clone", "--branch", "main", str(cls.origin), str(cls.repo))
        cls.c1 = cls._git(cls.repo, "rev-parse", "v1.0.0^{commit}")
        cls.c2 = cls._git(cls.repo, "rev-parse", "origin/side^{commit}")
        cls.c3 = cls._git(cls.repo, "rev-parse", "main^{commit}")
        # Deleted on the origin after the clone so a stale remote-tracking ref
        # still exists locally when the run fetches the branch again.
        cls._git(cls.origin, "update-ref", "-d", "refs/heads/release/2.0")
        scripts = cls.repo / "scripts" / "ci"
        scripts.mkdir(parents=True)
        cls._write(
            scripts / "apple-build-number.py",
            "\n".join(
                (
                    "import sys",
                    "from pathlib import Path",
                    'Path(__file__).with_name("build-helper-called").write_text("called", encoding="utf-8")',
                    "args = sys.argv[1:]",
                    'run = int(args[args.index("--run-number") + 1])',
                    'attempt = int(args[args.index("--run-attempt") + 1])',
                    "print(run * 10 + attempt)",
                    "",
                )
            ),
        )
        cls._write(
            scripts / "ios-version.py",
            "\n".join(
                (
                    "import sys",
                    "from pathlib import Path",
                    "args = sys.argv[1:]",
                    'version = args[args.index("--version") + 1]',
                    'build = args[args.index("--build-number") + 1]',
                    'out = Path(args[args.index("--github-output") + 1])',
                    'marketing = version[1:] if version.startswith("v") else version',
                    'with out.open("a", encoding="utf-8") as handle:',
                    '    handle.write(f"marketing={marketing}\\nbuild={build}\\n")',
                    "",
                )
            ),
        )
        cls.marker = scripts / "build-helper-called"
        cls.counter = 0
        cls.shells = {}
        for workflow, job in ((IOS_WORKFLOW, IOS_JOB), (MAC_WORKFLOW, MAC_JOB)):
            text = workflow.read_text(encoding="utf-8")
            validation = step_run(load_step(VALIDATION_STEP, load_job(VALIDATION_JOB, text)))
            versioning = step_run(load_step(VERSION_STEP, load_job(job, text)))
            cls.shells[workflow.name] = (validation, versioning)

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    @classmethod
    def _git(cls, cwd, *args):
        result = subprocess.run(
            ["git", *args],
            cwd=cwd,
            env=cls.git_env,
            capture_output=True,
            text=True,
        )
        if result.returncode != 0:
            raise AssertionError(
                f"git {' '.join(args)} failed in {cwd}:\n{result.stdout}{result.stderr}"
            )
        return result.stdout.strip()

    @classmethod
    def _write(cls, path, content):
        path.write_text(content, encoding="utf-8", newline="\n")

    @classmethod
    def _commit(cls, cwd, message):
        cls._git(cwd, "add", "-A")
        cls._git(cwd, "commit", "-m", message)

    def setUp(self):
        self.marker.unlink(missing_ok=True)

    def _run_script(self, script, environment):
        type(self).counter += 1
        script_path = self.root / f"step-{self.counter}.sh"
        script_path.write_text(script, encoding="utf-8", newline="\n")
        output_path = self.root / f"output-{self.counter}"
        output_path.write_text("", encoding="utf-8")
        env = {
            **os.environ,
            **environment,
            "GITHUB_OUTPUT": str(output_path),
            "GIT_TERMINAL_PROMPT": "0",
            "BASH_ENV": str(self.python_shim),
        }
        result = subprocess.run(
            [BASH, "-e", "-o", "pipefail", str(script_path)],
            cwd=self.repo,
            env=env,
            capture_output=True,
            text=True,
        )
        output = {}
        for line in output_path.read_text(encoding="utf-8").splitlines():
            key, _, value = line.partition("=")
            output[key] = value
        return result, output, self.marker.exists()

    def _run_validation(self, workflow_name, *, event, ref, ref_name, sha, version):
        script, _ = self.shells[workflow_name]
        return self._run_script(
            script,
            {
                "EVENT_NAME": event,
                "REF": ref,
                "REF_NAME": ref_name,
                "SHA": sha,
                "INPUT_VERSION": version,
            },
        )

    def test_push_tags_accept_lightweight_and_annotated(self):
        for workflow_name in self.shells:
            for version in ("v1.0.0", "v1.1.0"):
                with self.subTest(workflow=workflow_name, version=version):
                    result, output, helper = self._run_validation(
                        workflow_name,
                        event="push",
                        ref=f"refs/tags/{version}",
                        ref_name=version,
                        sha=self.c1,
                        version=version,
                    )
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertEqual(output["source_sha"], self.c1)
                    self.assertEqual(output["tag"], version)
                    self.assertEqual(output["marketing"], version[1:])
                    self.assertFalse(helper)

    def test_push_branch_is_rejected(self):
        for workflow_name in self.shells:
            with self.subTest(workflow=workflow_name):
                result, _, helper = self._run_validation(
                    workflow_name,
                    event="push",
                    ref="refs/heads/main",
                    ref_name="main",
                    sha=self.c3,
                    version="v1.0.0",
                )
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(helper)

    def test_push_wrong_tag_ref_is_rejected(self):
        for workflow_name in self.shells:
            with self.subTest(workflow=workflow_name):
                result, _, helper = self._run_validation(
                    workflow_name,
                    event="push",
                    ref="refs/tags/v1.1.0",
                    ref_name="v1.1.0",
                    sha=self.c1,
                    version="v1.0.0",
                )
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(helper)

    def test_push_wrong_event_sha_is_rejected(self):
        for workflow_name in self.shells:
            with self.subTest(workflow=workflow_name):
                result, _, helper = self._run_validation(
                    workflow_name,
                    event="push",
                    ref="refs/tags/v1.0.0",
                    ref_name="v1.0.0",
                    sha=self.c3,
                    version="v1.0.0",
                )
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(helper)

    def test_push_tag_not_on_main_is_rejected(self):
        for workflow_name in self.shells:
            with self.subTest(workflow=workflow_name):
                result, _, helper = self._run_validation(
                    workflow_name,
                    event="push",
                    ref="refs/tags/v2.0.0",
                    ref_name="v2.0.0",
                    sha=self.c2,
                    version="v2.0.0",
                )
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(helper)

    def test_synthetic_workflow_call_event_is_rejected(self):
        for workflow_name in self.shells:
            with self.subTest(workflow=workflow_name):
                result, _, helper = self._run_validation(
                    workflow_name,
                    event="workflow_call",
                    ref="refs/tags/v1.0.0",
                    ref_name="v1.0.0",
                    sha=self.c1,
                    version="v1.0.0",
                )
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(helper)

    def test_manual_dispatch_builds_dispatched_sha_without_a_tag(self):
        for workflow_name in self.shells:
            with self.subTest(workflow=workflow_name):
                version = "v9.9.9"
                result, output, helper = self._run_validation(
                    workflow_name,
                    event="workflow_dispatch",
                    ref="refs/heads/main",
                    ref_name="main",
                    sha=self.c3,
                    version=version,
                )
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(output["source_sha"], self.c3)
                self.assertEqual(output["tag"], version)
                self.assertEqual(output["marketing"], version[1:])
                self.assertFalse(helper)

    def test_manual_dispatch_from_commit_not_on_main_is_rejected(self):
        for workflow_name in self.shells:
            with self.subTest(workflow=workflow_name):
                result, _, helper = self._run_validation(
                    workflow_name,
                    event="workflow_dispatch",
                    ref="refs/heads/main",
                    ref_name="main",
                    sha=self.c2,
                    version="v1.0.0",
                )
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(helper)

    def test_manual_dispatch_from_branch_is_rejected(self):
        for workflow_name in self.shells:
            with self.subTest(workflow=workflow_name):
                result, _, helper = self._run_validation(
                    workflow_name,
                    event="workflow_dispatch",
                    ref="refs/heads/side",
                    ref_name="side",
                    sha=self.c2,
                    version="v1.0.0",
                )
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(helper)

    def test_manual_dispatch_from_release_branch_builds_the_dispatched_sha(self):
        for workflow_name in self.shells:
            with self.subTest(workflow=workflow_name):
                version = "v9.9.9"
                result, output, helper = self._run_validation(
                    workflow_name,
                    event="workflow_dispatch",
                    ref="refs/heads/release/1.2",
                    ref_name="release/1.2",
                    sha=self.c2,
                    version=version,
                )
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(output["source_sha"], self.c2)
                self.assertEqual(output["tag"], version)
                self.assertEqual(output["marketing"], version[1:])
                self.assertFalse(helper)

    def test_manual_dispatch_from_release_branch_keeps_the_dispatched_commit(self):
        for workflow_name in self.shells:
            with self.subTest(workflow=workflow_name):
                result, output, helper = self._run_validation(
                    workflow_name,
                    event="workflow_dispatch",
                    ref="refs/heads/release/1.2",
                    ref_name="release/1.2",
                    sha=self.c1,
                    version="v1.0.0",
                )
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(output["source_sha"], self.c1)
                self.assertFalse(helper)

    def test_manual_dispatch_from_release_branch_rejects_main_only_commit(self):
        for workflow_name in self.shells:
            with self.subTest(workflow=workflow_name):
                result, _, helper = self._run_validation(
                    workflow_name,
                    event="workflow_dispatch",
                    ref="refs/heads/release/1.2",
                    ref_name="release/1.2",
                    sha=self.c3,
                    version="v1.0.0",
                )
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(helper)

    def test_manual_dispatch_from_deleted_release_branch_is_rejected(self):
        for workflow_name in self.shells:
            with self.subTest(workflow=workflow_name):
                result, _, helper = self._run_validation(
                    workflow_name,
                    event="workflow_dispatch",
                    ref="refs/heads/release/2.0",
                    ref_name="release/2.0",
                    sha=self.c2,
                    version="v1.0.0",
                )
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(helper)

    def test_manual_dispatch_from_nested_release_branch_is_rejected(self):
        for workflow_name in self.shells:
            with self.subTest(workflow=workflow_name):
                result, _, helper = self._run_validation(
                    workflow_name,
                    event="workflow_dispatch",
                    ref="refs/heads/release/1.2/hotfix",
                    ref_name="release/1.2/hotfix",
                    sha=self.c2,
                    version="v1.0.0",
                )
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(helper)

    def test_manual_dispatch_rejects_an_existing_two_level_release_branch(self):
        for workflow_name in self.shells:
            with self.subTest(workflow=workflow_name):
                result, _, helper = self._run_validation(
                    workflow_name,
                    event="workflow_dispatch",
                    ref="refs/heads/release/hotfix/1.2",
                    ref_name="release/hotfix/1.2",
                    sha=self.c2,
                    version="v1.0.0",
                )
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(helper)

    def test_manual_dispatch_from_tag_ref_is_rejected(self):
        for workflow_name in self.shells:
            with self.subTest(workflow=workflow_name):
                result, _, helper = self._run_validation(
                    workflow_name,
                    event="workflow_dispatch",
                    ref="refs/tags/v2.0.0",
                    ref_name="v2.0.0",
                    sha=self.c2,
                    version="v2.0.0",
                )
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(helper)

    def test_signing_recomputes_automatic_build_for_a_new_attempt(self):
        for workflow_name in self.shells:
            with self.subTest(workflow=workflow_name):
                _, version_script = self.shells[workflow_name]
                attempts = []
                for attempt in (1, 2):
                    result, output, helper = self._run_script(
                        version_script,
                        {
                            "EVENT_NAME": "push",
                            "RAW_VERSION": "v1.0.0",
                            "INPUT_BUILD_NUMBER": "",
                            "RUN_NUMBER": "50",
                            "RUN_ATTEMPT": str(attempt),
                        },
                    )
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertTrue(helper)
                    attempts.append(output)
                self.assertEqual(attempts[0]["build"], "501")
                self.assertEqual(attempts[1]["build"], "502")
                self.assertEqual(attempts[1]["marketing"], "1.0.0")

    def test_signing_uses_manual_build_without_deriving(self):
        for workflow_name in self.shells:
            with self.subTest(workflow=workflow_name):
                _, version_script = self.shells[workflow_name]
                result, output, helper = self._run_script(
                    version_script,
                    {
                        "EVENT_NAME": "workflow_dispatch",
                        "RAW_VERSION": "v1.0.0",
                        "INPUT_BUILD_NUMBER": "321",
                        "RUN_NUMBER": "50",
                        "RUN_ATTEMPT": "2",
                    },
                )
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(output["build"], "321")
                self.assertFalse(helper)


if __name__ == "__main__":
    unittest.main()
