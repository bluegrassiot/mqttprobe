#!/usr/bin/env python3
"""Regression tests for scripts/ci/format-check.py.

Run: python -m unittest discover -s scripts/ci/tests
"""

import importlib.util
import io
import sys
import unittest
from pathlib import Path
from unittest.mock import patch, MagicMock

_SPEC = importlib.util.spec_from_file_location(
    "ci_format_check", Path(__file__).resolve().parents[1] / "format-check.py",
)
if _SPEC is None or _SPEC.loader is None:
    raise SystemExit("Cannot load scripts/ci/format-check.py")
format_ci = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(format_ci)


def _run(returncode=0, stdout="", stderr=""):
    r = MagicMock()
    r.returncode = returncode
    r.stdout = stdout
    r.stderr = stderr
    return r


class _Fixture:
    """Context manager that patches format_ci globals and restores them."""

    def __init__(self, targets=None, fix=False):
        self._targets = targets or [("Fake.slnf", False)]
        self._fix = fix
        self._orig_root = format_ci.ROOT
        self._orig_fix = format_ci.FIX
        self._orig_targets = format_ci.TARGETS

    def __enter__(self):
        format_ci.ROOT = Path("/tmp/fake")
        format_ci.FIX = self._fix
        format_ci.TARGETS = self._targets
        return self

    def __exit__(self, *exc):
        format_ci.ROOT = self._orig_root
        format_ci.FIX = self._orig_fix
        format_ci.TARGETS = self._orig_targets
        return False


def _run_main(fake_run, fixture=None):
    """Run format_ci.main() with mocked subprocess.run, capturing stdout."""
    ctx = fixture or _Fixture()
    calls = []
    def tracking_run(args, **kw):
        calls.append(list(args))
        return fake_run(args, **kw)
    buf = io.StringIO()
    with ctx:
        with patch("subprocess.run", side_effect=tracking_run):
            with patch("sys.stdout", buf):
                format_ci.main()
    return buf.getvalue(), calls


class TestInvocationArgs(unittest.TestCase):
    """Verify IDE0005, excludes, --verify-no-changes, --severity info in args."""

    def test_ide0005_uses_style_subcommand_with_excludes_and_severity(self):
        _, calls = _run_main(lambda a, **kw: _run())
        ide_calls = [c for c in calls if "--diagnostics" in c]
        self.assertTrue(ide_calls, "No IDE0005 pass found")
        c = ide_calls[0]
        self.assertEqual(c[2], "style", "Must use 'style' subcommand")
        self.assertNotIn("analyzers", c)
        self.assertIn("--diagnostics", c)
        self.assertIn("IDE0005", c)
        idx = c.index("--severity")
        self.assertEqual(c[idx + 1], "hidden",
                         "IDE0005 defaults to hidden without .editorconfig entry")
        self.assertIn("--verify-no-changes", c)
        self.assertIn("--exclude", c)

    def test_all_invocations_include_excludes(self):
        _, calls = _run_main(lambda a, **kw: _run())
        for c in calls:
            self.assertIn("--exclude", c, f"Missing --exclude in: {c}")

    def test_check_mode_has_verify_no_changes(self):
        _, calls = _run_main(lambda a, **kw: _run())
        for c in calls:
            self.assertIn("--verify-no-changes", c, f"Missing --verify-no-changes in: {c}")


class TestCrlfExecutesBothStylePasses(unittest.TestCase):
    """ENDOFLINE-only whitespace failure must not suppress the style or IDE0005 passes."""

    def test_crlf_runs_whitespace_style_and_ide0005(self):
        def fake_run(args, **kw):
            if "whitespace" in args:
                return _run(2, "", "error ENDOFLINE: crlf")
            return _run(0, "", "")

        out, calls = _run_main(fake_run)
        subs = [c[2] for c in calls]
        self.assertIn("whitespace", subs)
        self.assertIn("style", subs)
        ide_calls = [c for c in calls if "--diagnostics" in c]
        self.assertTrue(ide_calls, "IDE0005 pass was skipped despite ENDOFLINE-only noise")

    def test_endofline_only_reports_ok(self):
        def fake_run(args, **kw):
            if "whitespace" in args:
                return _run(2, "", "error ENDOFLINE: crlf")
            return _run(0, "", "")

        out, _ = _run_main(fake_run)
        self.assertIn("OK", out)
        self.assertNotIn("FAIL", out)


class TestStyleViolationAfterCrlfFails(unittest.TestCase):
    """If whitespace is ENDOFLINE-only but style has a real error, it must fail."""

    def test_style_error_after_endofline_whitespace_fails(self):
        def fake_run(args, **kw):
            if "whitespace" in args:
                return _run(2, "", "error ENDOFLINE: crlf")
            if "style" in args and "--diagnostics" not in args:
                return _run(2, "", "error IDE0005: Remove unused using directive")
            return _run(0, "", "")

        with self.assertRaises(SystemExit) as ctx:
            _run_main(fake_run)
        self.assertEqual(ctx.exception.code, 1)

    def test_targeted_pass_after_endofline_whitespace_fails(self):
        """Even if the style pass is clean, targeted IDE0005 failure persists."""
        def fake_run(args, **kw):
            if "whitespace" in args:
                return _run(2, "", "error ENDOFLINE: crlf")
            if "--diagnostics" in args:
                return _run(2, "", "error IDE0005: unused using")
            return _run(0, "", "")

        with self.assertRaises(SystemExit) as ctx:
            _run_main(fake_run)
        self.assertEqual(ctx.exception.code, 1)


class TestEndoflineWithRestoreExceptionFails(unittest.TestCase):
    """ENDOFLINE + restore/exception evidence must not be treated as ENDOFLINE-only."""

    def test_endofline_plus_restore_fails(self):
        def fake_run(args, **kw):
            if "whitespace" in args:
                return _run(2, "", "error ENDOFLINE: crlf\nRestore operation failed")
            return _run(0, "", "")

        with self.assertRaises(SystemExit) as ctx:
            _run_main(fake_run)
        self.assertEqual(ctx.exception.code, 1)

    def test_endofline_plus_exception_fails(self):
        def fake_run(args, **kw):
            if "whitespace" in args:
                return _run(2, "", "error ENDOFLINE: crlf\nSystem.Exception: boom")
            return _run(0, "", "")

        with self.assertRaises(SystemExit) as ctx:
            _run_main(fake_run)
        self.assertEqual(ctx.exception.code, 1)


class TestWhitespaceFixtureNotIde0005(unittest.TestCase):
    """Fixture whitespace violation must use WHITESPACE subcommand, not IDE0005."""

    def test_whitespace_error_in_whitespeace_pass(self):
        def fake_run(args, **kw):
            if "whitespace" in args:
                return _run(2, "", "error WHITESPACE: trailing whitespace")
            return _run(0, "", "")

        with self.assertRaises(SystemExit) as ctx:
            _run_main(fake_run)
        self.assertEqual(ctx.exception.code, 1)


class TestTrueFailurePropagates(unittest.TestCase):
    """A real formatting violation must cause exit 1."""

    def test_real_style_error_fails(self):
        def fake_run(args, **kw):
            if "style" in args and "--diagnostics" not in args:
                return _run(2, "", "error IDE0005: Remove unused using")
            return _run(0, "", "")

        with self.assertRaises(SystemExit) as ctx:
            _run_main(fake_run)
        self.assertEqual(ctx.exception.code, 1)

    def test_targeted_ide0005_failure_fails(self):
        def fake_run(args, **kw):
            if "--diagnostics" in args:
                return _run(2, "", "error IDE0005: unused using")
            return _run(0, "", "")

        with self.assertRaises(SystemExit) as ctx:
            _run_main(fake_run)
        self.assertEqual(ctx.exception.code, 1)


class TestWorkspaceWarningExitZeroFails(unittest.TestCase):
    """A workspace load warning with exit 0 must fail closed."""

    def test_prefixed_warn_fails(self):
        def fake_run(args, **kw):
            return _run(0, "", "warn : Workspace failed to load project")

        with self.assertRaises(SystemExit) as ctx:
            _run_main(fake_run)
        self.assertEqual(ctx.exception.code, 1)

    def test_unprefixed_workspace_message_fails(self):
        """The actual dotnet format message has no warn: prefix."""
        msg = ("Warnings were encountered while loading the workspace. "
               "Set the verbosity option to the diagnostic level to log warnings.")
        def fake_run(args, **kw):
            return _run(0, msg, "")

        with self.assertRaises(SystemExit) as ctx:
            _run_main(fake_run)
        self.assertEqual(ctx.exception.code, 1)

    def test_msbuild_warn_fails(self):
        def fake_run(args, **kw):
            return _run(0, "", "warn : MSBuild could not load project")

        with self.assertRaises(SystemExit) as ctx:
            _run_main(fake_run)
        self.assertEqual(ctx.exception.code, 1)


class TestWorkloadSkip(unittest.TestCase):
    """NETSDK1147 skip rules: only failed invocation + needs_workload."""

    def test_netsdk1147_failed_skips(self):
        def fake_run(args, **kw):
            return _run(2, "", "error NETSDK1147: workload not installed")

        out, _ = _run_main(fake_run, _Fixture(targets=[("Fake.csproj", True)]))
        self.assertIn("SKIP", out)

    def test_netsdk1147_exit0_does_not_skip(self):
        """Exit 0 with NETSDK1147 means the invocation succeeded; no skip."""
        def fake_run(args, **kw):
            return _run(0, "", "warning NETSDK1147: workload message")

        out, _ = _run_main(fake_run, _Fixture(targets=[("Fake.csproj", True)]))
        self.assertIn("OK", out)
        self.assertNotIn("SKIP", out)

    def test_workload_keyword_does_not_skip(self):
        """Generic 'workload' keyword alone must not trigger skip."""
        def fake_run(args, **kw):
            return _run(2, "", "error : workload 'maui' is not installed")

        with self.assertRaises(SystemExit) as ctx:
            _run_main(fake_run, _Fixture(targets=[("Fake.csproj", True)]))
        self.assertEqual(ctx.exception.code, 1)

    def test_generic_restore_does_not_skip(self):
        def fake_run(args, **kw):
            return _run(2, "", "error : Restore operation failed")

        with self.assertRaises(SystemExit) as ctx:
            _run_main(fake_run, _Fixture(targets=[("Fake.csproj", True)]))
        self.assertEqual(ctx.exception.code, 1)

    def test_workload_skip_only_on_best_effort_target(self):
        def fake_run(args, **kw):
            return _run(2, "", "error NETSDK1147: workload not installed")

        with self.assertRaises(SystemExit) as ctx:
            _run_main(fake_run, _Fixture(targets=[("Fake.slnf", False)]))
        self.assertEqual(ctx.exception.code, 1)


class TestCleanPassExitZero(unittest.TestCase):
    """All passes clean must exit 0."""

    def test_clean_pass(self):
        out, _ = _run_main(lambda a, **kw: _run())
        self.assertIn("All projects formatted correctly", out)


if __name__ == "__main__":
    unittest.main()