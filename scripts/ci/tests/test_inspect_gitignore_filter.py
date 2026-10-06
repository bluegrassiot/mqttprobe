#!/usr/bin/env python3
"""Integration tests for gitignored-finding filtering in scripts/ci/inspect.py.

Split from test_inspect.py to keep each module under 500 lines.

Run: python -m unittest discover -s scripts/ci/tests
"""

import importlib.util
import subprocess
import tempfile
import unittest
from pathlib import Path

_SPEC = importlib.util.spec_from_file_location(
    "ci_inspect", Path(__file__).resolve().parents[1] / "inspect.py",
)
if _SPEC is None or _SPEC.loader is None:
    raise SystemExit("Cannot load scripts/ci/inspect.py")
inspect_ci = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(inspect_ci)

RULE = inspect_ci.GATED_EXCEPTIONS[0]["rule"]
REALM = inspect_ci.GATED_EXCEPTIONS[0]["file"]


def finding(line, rule=RULE, file=REALM):
    return {
        "rule": rule,
        "file": file,
        "line": line,
        "level": "warning",
        "message": "",
        "snippet": "",
    }


class TestGitIgnoredFindingFiltering(unittest.TestCase):
    """Integration: the main flow filters findings on gitignored files.

    Uses a temporary git repo with synthetic findings (no real secrets).
    """

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.repo = Path(self.tmp.name)
        subprocess.run(["git", "init"], cwd=self.repo, capture_output=True,
                        check=True)
        subprocess.run(["git", "config", "user.email", "test@test"],
                        cwd=self.repo, capture_output=True)
        subprocess.run(["git", "config", "user.name", "Test"],
                        cwd=self.repo, capture_output=True)

    def _write(self, name, content="dummy"):
        path = self.repo / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")
        return path

    def test_ignored_key_finding_is_filtered(self):
        """Findings on gitignored .key files are excluded from gating."""
        self._write(".gitignore", "deploy/mtls/certs/**\n")
        self._write("deploy/mtls/certs/ca.key", "synthetic-test-content")
        paths = {"deploy/mtls/certs/ca.key", "src/Program.cs"}
        ignored = inspect_ci.git_ignored_paths(paths, self.repo)
        self.assertIn("deploy/mtls/certs/ca.key", ignored)

    def test_tracked_key_finding_is_not_filtered(self):
        """Findings on tracked .key files are NOT excluded (fail closed)."""
        self._write(".gitignore", "*.key\n")
        self._write("tracked.key", "tracked-content")
        subprocess.run(["git", "add", "-f", "tracked.key"], cwd=self.repo,
                        capture_output=True, check=True)
        subprocess.run(["git", "commit", "-m", "track key"], cwd=self.repo,
                        capture_output=True, check=True)
        paths = {"tracked.key"}
        ignored = inspect_ci.git_ignored_paths(paths, self.repo)
        self.assertNotIn("tracked.key", ignored)

    def test_non_ignored_untracked_source_not_filtered(self):
        """Untracked source files that don't match .gitignore are scanned."""
        self._write(".gitignore", "*.key\n")
        self._write("scripts/build.sh", "LINUXDEPLOY_SHA256='abc123'")
        paths = {"scripts/build.sh"}
        ignored = inspect_ci.git_ignored_paths(paths, self.repo)
        self.assertNotIn("scripts/build.sh", ignored)


if __name__ == "__main__":
    unittest.main()