#!/usr/bin/env python3
"""Regression tests for .githooks staged-path classification.

A staged filename may contain a newline. The pre-commit hook reads staged paths
NUL-separated into an array, so every gate classifier must take that array
positionally. Feeding a newline-delimited string to a stdin classifier splits the
path into fragments and can silently skip the gate.

Run: python -m unittest discover -s scripts/ci/tests
"""

import os
import shlex
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
LIB = ROOT / ".githooks" / "lib.sh"
HOOK = ROOT / ".githooks" / "pre-commit"

NL = "$'\n'"

# (positional classifier, gate name, newline-containing staged path)
GATES = [
    ("any_workflow_relevant_paths", "Workflow lint",
     ".github/workflows/bad" + NL + "name.yml"),
    ("any_format_relevant_paths", "Format",
     "src/Broken" + NL + "Name.cs"),
    ("any_format_relevant_paths", "Format (config basename)",
     "Directory" + NL + ".Build.props"),
    ("any_file_length_relevant_paths", "File length",
     "src/Big" + NL + "File.cs"),
    ("any_file_length_relevant_paths", "Blocking awaits",
     "src/Big" + NL + "Page.razor"),
    ("any_comment_check_relevant_paths", "Comments",
     "tests/T/Bad" + NL + "Test.cs"),
]

# stdin classifier, gate name, newline-containing staged path.
# Only gates that lose the path when flattened are listed. Format is deliberately
# absent: its extension match survives on the final fragment, so flattening
# over-triggers it rather than skipping it.
FRAGMENTED = [
    ("any_workflow_relevant", "Workflow lint",
     ".github/workflows/bad" + NL + "name.yml"),
    ("any_file_length_relevant", "File length",
     "src/Big" + NL + "File.cs"),
    ("any_comment_check_relevant", "Comments",
     "tests/T/Bad" + NL + "Test.cs"),
]


def _bash_path(path: Path) -> str:
    """Path usable from inside the bash that runs these tests (Git Bash or WSL)."""
    text = str(path)
    if os.name != "nt":
        return text
    drive, rest = text.split(":", 1)
    wsl = f"/mnt/{drive.lower()}/{rest.lstrip('/').replace(chr(92), '/')}"
    probe = subprocess.run(
        ["bash", "-c", f'test -f "{wsl}" && echo ok || echo no'],
        check=False, capture_output=True, text=True,
    )
    return wsl if probe.stdout.strip() == "ok" else text


def _run_bash(script: str, cwd: Path | None = None) -> subprocess.CompletedProcess:
    return subprocess.run(
        ["bash", "-c", f"source {shlex.quote(_bash_path(LIB))}; {script}"],
        check=False, capture_output=True, text=True, cwd=cwd,
    )


@unittest.skipUnless(shutil.which("bash"), "bash is unavailable")
class TestNewlineFilenamesReachEveryGate(unittest.TestCase):
    """A newline inside a staged filename must not skip any gate."""

    def test_positional_classifiers_keep_newline_path_intact(self):
        for classifier, gate, path in GATES:
            with self.subTest(gate=gate, classifier=classifier):
                result = _run_bash(f"{classifier} {path}")
                self.assertEqual(
                    result.returncode, 0,
                    f"{gate}: {path!r} was not classified as relevant\n{result.stderr}",
                )

    def test_stdin_classifiers_lose_the_newline_path(self):
        """Pins the defect the positional form exists to avoid.

        A `read -r` line loop sees only the fragments, so these report
        irrelevant. If one ever returns 0 the loop changed shape; either way the
        hook must not depend on these for staged paths.
        """
        for classifier, gate, path in FRAGMENTED:
            with self.subTest(gate=gate, classifier=classifier):
                result = _run_bash(f"printf '%s\\n' {path} | {classifier}")
                self.assertEqual(
                    result.returncode, 1,
                    f"{gate}: stdin classifier kept the flattened path; re-check "
                    f"whether the hook still needs the positional form\n{result.stderr}",
                )


@unittest.skipIf(
    os.name == "nt", "Windows filesystems cannot hold a newline in a filename",
)
class TestNulReadKeepsPathsIntact(unittest.TestCase):
    """End-to-end: the hook's own read loop keeps one path per array element."""

    def test_hook_read_loop_yields_one_intact_path(self):
        with tempfile.TemporaryDirectory() as tmp:
            repo = Path(tmp)
            self.git(repo, "init", "-q")
            self.git(repo, "config", "user.email", "hooks@example.invalid")
            self.git(repo, "config", "user.name", "Hook Tests")
            (repo / "src").mkdir()
            (repo / "src" / "Odd\nName.cs").write_text("class A {}\n", encoding="utf-8")
            self.git(repo, "add", "-A")

            dump = repo / "paths.z"
            with dump.open("wb") as handle:
                self.git(repo, "diff", "--cached", "--name-only", "-z", stdout=handle)

            # The read loop and the classifier must share one shell: STAGED_PATHS is a
            # local of the hook process, so a second bash would see an empty array.
            # Output is compared raw, so the embedded newline in the path and the
            # final record terminator are both asserted rather than stripped.
            script = (
                f"source {shlex.quote(_bash_path(LIB))}; "
                'STAGED_PATHS=(); while IFS= read -r -d \'\' p; do '
                'STAGED_PATHS+=("$p"); done < "$1"; '
                'printf "%s\\n" "${#STAGED_PATHS[@]}"; '
                'printf "%s\\n" "${STAGED_PATHS[0]}"; '
                'if any_file_length_relevant_paths "${STAGED_PATHS[@]}"; then '
                'printf "%s\\n" RELEVANT; else printf "%s\\n" SKIPPED; fi'
            )
            result = subprocess.run(
                ["bash", "-c", script, "hook-probe", str(dump)],
                check=False, capture_output=True, text=True, cwd=repo,
            )
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(
                result.stdout, "1\nsrc/Odd\nName.cs\nRELEVANT\n",
                f"unexpected probe output: {result.stdout!r}",
            )

    @staticmethod
    def git(repo: Path, *args, stdout=None):
        # capture_output and an explicit stdout are mutually exclusive.
        if stdout is None:
            subprocess.run(["git", *args], cwd=repo, check=True, capture_output=True)
        else:
            subprocess.run(["git", *args], cwd=repo, check=True, stdout=stdout)


@unittest.skipUnless(shutil.which("bash"), "bash is unavailable")
class TestPreCommitUsesPositionalClassifiers(unittest.TestCase):
    def test_hook_passes_the_array_to_every_gate(self):
        content = HOOK.read_text(encoding="utf-8")
        for fragment in (
            'any_workflow_relevant_paths "${STAGED_PATHS[@]}"',
            'any_format_relevant_paths "${STAGED_PATHS[@]}"',
            'any_file_length_relevant_paths "${STAGED_PATHS[@]}"',
            'any_comment_check_relevant_paths "${STAGED_PATHS[@]}"',
        ):
            self.assertIn(fragment, content)

        for stdin_classifier in (
            "any_workflow_relevant", "any_format_relevant",
            "any_file_length_relevant", "any_comment_check_relevant",
        ):
            self.assertNotIn(
                f"| {stdin_classifier};", content,
                f"{stdin_classifier} must not gate on a newline-flattened path list",
            )

    def test_hook_does_not_flatten_staged_paths(self):
        content = HOOK.read_text(encoding="utf-8")
        self.assertNotIn("STAGED=$(printf", content)
        self.assertNotIn("mapfile", content)
        self.assertIn("while IFS= read -r -d '' staged_path; do", content)
        self.assertIn('STAGED_PATHS+=("$staged_path")', content)

    def test_classifiers_survive_set_u_and_empty_input(self):
        """macOS ships Bash 3.2: no mapfile, and `"$@"` on empty input under `set -u`."""
        # A newline-containing path each classifier must accept, and an empty call
        # it must reject without tripping `set -u`.
        cases = [
            ("any_workflow_relevant_paths", ".github/workflows/bad" + NL + "name.yml"),
            ("any_format_relevant_paths", "src/Big" + NL + "File.cs"),
            ("any_file_length_relevant_paths", "src/Big" + NL + "File.cs"),
            ("any_comment_check_relevant_paths", "tests/T/Bad" + NL + "Test.cs"),
        ]
        for name, path in cases:
            relevant = _run_bash(f"set -u; {name} {path}")
            self.assertEqual(relevant.returncode, 0, f"{name}: {relevant.stderr}")

            empty = _run_bash(f"set -u; {name}")
            self.assertEqual(empty.returncode, 1, f"{name}: {empty.stderr}")


if __name__ == "__main__":
    unittest.main()