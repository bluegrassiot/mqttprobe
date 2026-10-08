#!/usr/bin/env python3
"""Regression tests for scripts/ci/check-file-length.py.

Run: python -m unittest discover -s scripts/ci/tests
"""

import importlib.util
import io
import tempfile
import unittest
from contextlib import contextmanager
from pathlib import Path
from unittest.mock import patch

_SPEC = importlib.util.spec_from_file_location(
    "ci_check_file_length", Path(__file__).resolve().parents[1] / "check-file-length.py",
)
if _SPEC is None or _SPEC.loader is None:
    raise SystemExit("Cannot load scripts/ci/check-file-length.py")
length_ci = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(length_ci)


@contextmanager
def _run(files, argv=None, grandfather=None):
    """Point the module at a throwaway tree, run main(argv), capture stdout.

    `files` maps repo-relative paths to their content.
    """
    orig_root = length_ci.ROOT
    orig_grandfather = length_ci.GRANDFATHER
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        for rel, content in files.items():
            target = root / rel
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(content, encoding="utf-8")
        length_ci.ROOT = root
        length_ci.GRANDFATHER = dict(grandfather or {})
        buf = io.StringIO()
        try:
            with patch("sys.stdout", buf):
                code = length_ci.main(list(argv or []))
        finally:
            length_ci.ROOT = orig_root
            length_ci.GRANDFATHER = orig_grandfather
    yield code, buf.getvalue()


def _lines(n):
    return "".join(f"line {i}\n" for i in range(n))


class TestBareInvocation(unittest.TestCase):
    """A bare run must scan the tree, not silently report nothing to check."""

    def test_bare_run_flags_oversized_source_file(self):
        with _run({"src/App/Widget.cs": _lines(600)}) as (code, out):
            self.assertEqual(code, 1, out)
            self.assertIn("src/App/Widget.cs", out)

    def test_bare_run_passes_and_reports_the_file_count(self):
        with _run({"src/App/Widget.cs": _lines(10)}) as (code, out):
            self.assertEqual(code, 0, out)
            self.assertIn("1 file(s) within limits", out)

    def test_bare_run_does_not_report_no_files_to_check(self):
        """The original defect: bare main() printed this and still exited 0."""
        with _run({"src/App/Widget.cs": _lines(10)}) as (code, out):
            self.assertNotIn("No files to check", out)

    def test_bare_run_reports_zero_files_when_tree_is_empty(self):
        with _run({"README.md": "hello"}) as (code, out):
            self.assertEqual(code, 0, out)
            self.assertIn("0 file(s) within limits", out)


class TestRelevance(unittest.TestCase):
    """Only hand-written src counts; generated and test trees never gate."""

    def test_bare_run_ignores_tests_root(self):
        files = {"tests/App/Big.cs": _lines(900), "src/App/Ok.cs": _lines(5)}
        with _run(files) as (code, out):
            self.assertEqual(code, 0, out)

    def test_bare_run_ignores_bin_and_obj_output(self):
        files = {
            "src/App/obj/Debug/net10.0/Generated.cs": _lines(900),
            "src/App/bin/Release/Generated.cs": _lines(900),
            "src/App/Ok.cs": _lines(5),
        }
        with _run(files) as (code, out):
            self.assertEqual(code, 0, out)

    def test_bare_run_ignores_generated_protobuf(self):
        with _run({"src/Models/SparkplugBProtobuf.cs": _lines(4000)}) as (code, out):
            self.assertEqual(code, 0, out)

    def test_bare_run_ignores_non_code_files(self):
        with _run({"src/App/style.css": _lines(2000)}) as (code, out):
            self.assertEqual(code, 0, out)


class TestGrandfatherCeiling(unittest.TestCase):
    """A frozen ceiling lets an oversized file shrink but never grow."""

    def test_grandfathered_file_within_ceiling_passes(self):
        files = {"src/App/Legacy.cs": _lines(600)}
        with _run(files, grandfather={"src/App/Legacy.cs": 600}) as (code, out):
            self.assertEqual(code, 0, out)

    def test_grandfathered_file_that_grew_fails(self):
        files = {"src/App/Legacy.cs": _lines(601)}
        with _run(files, grandfather={"src/App/Legacy.cs": 600}) as (code, out):
            self.assertEqual(code, 1, out)
            self.assertIn("src/App/Legacy.cs", out)


class TestExplicitPaths(unittest.TestCase):
    """Passing paths stays available and keeps the same exit codes."""

    def test_explicit_oversized_path_fails(self):
        files = {"src/App/Big.cs": _lines(600), "src/App/Ok.cs": _lines(5)}
        with _run(files, argv=["src/App/Big.cs"]) as (code, out):
            self.assertEqual(code, 1, out)
            self.assertIn("src/App/Big.cs", out)
            self.assertNotIn("src/App/Ok.cs", out)

    def test_explicit_path_at_limit_passes(self):
        with _run({"src/App/Ok.cs": _lines(500)}, argv=["src/App/Ok.cs"]) as (code, out):
            self.assertEqual(code, 0, out)

    def test_explicit_out_of_scope_path_is_ignored(self):
        with _run({"tests/App/Big.cs": _lines(900)}, argv=["tests/App/Big.cs"]) as (code, out):
            self.assertEqual(code, 0, out)


if __name__ == "__main__":
    unittest.main()