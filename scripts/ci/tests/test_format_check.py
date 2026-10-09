#!/usr/bin/env python3
"""Regression tests for scripts/ci/format-check.py.

Run: python -m unittest discover -s scripts/ci/tests
"""

import importlib.util
import io
import os
import subprocess
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
        self._orig_targets = format_ci.TARGETS

    def __enter__(self):
        format_ci.ROOT = Path("/tmp/fake")
        format_ci.TARGETS = self._targets
        return self

    def __exit__(self, *exc):
        format_ci.ROOT = self._orig_root
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
                format_ci.main(["--fix"] if ctx._fix else [])
    return buf.getvalue(), calls


class TestInvocationArgs(unittest.TestCase):
    """Verify formatting passes and their shared invocation options."""

    def test_whitespace_general_style_and_targeted_style_passes_run(self):
        _, calls = _run_main(lambda a, **kw: _run())
        self.assertEqual([c[2] for c in calls], ["whitespace", "style", "style"])
        self.assertEqual(sum("--diagnostics" in c for c in calls), 1)

    def test_all_invocations_include_excludes(self):
        _, calls = _run_main(lambda a, **kw: _run())
        for c in calls:
            self.assertIn("--exclude", c, f"Missing --exclude in: {c}")

    def test_check_mode_has_verify_no_changes(self):
        _, calls = _run_main(lambda a, **kw: _run())
        for c in calls:
            self.assertIn("--verify-no-changes", c, f"Missing --verify-no-changes in: {c}")

    def test_fix_mode_runs_both_passes_without_verify_flag(self):
        _, calls = _run_main(lambda a, **kw: _run(), _Fixture(fix=True))
        self.assertEqual([c[2] for c in calls], ["whitespace", "style", "style"])
        for c in calls:
            self.assertNotIn("--verify-no-changes", c)

    def test_ide0005_pass_uses_hidden_severity(self):
        _, calls = _run_main(lambda a, **kw: _run())
        call = next(c for c in calls if "--diagnostics" in c)
        self.assertEqual(call[call.index("--diagnostics") + 1], "IDE0005")
        self.assertEqual(call[call.index("--severity") + 1], "hidden")

    def test_scoped_ide0005_pass_preserves_include_and_fix_options(self):
        path = Path("/tmp/fake/src/changed.cs")
        original_select = format_ci._select_files
        format_ci._select_files = lambda selection: (False, [path])
        try:
            _, calls = _run_main(lambda a, **kw: _run())
            _, fix_calls = _run_main(lambda a, **kw: _run(), _Fixture(fix=True))
        finally:
            format_ci._select_files = original_select
        for call in (next(c for c in calls if "--diagnostics" in c),
                     next(c for c in fix_calls if "--diagnostics" in c)):
            self.assertEqual(call[call.index("--include") + 1], os.path.join("src", "changed.cs"))
        self.assertIn("--verify-no-changes", next(c for c in calls if "--diagnostics" in c))
        self.assertNotIn("--verify-no-changes", next(c for c in fix_calls if "--diagnostics" in c))


class TestCrlfExecutesBothStylePasses(unittest.TestCase):
    """ENDOFLINE-only whitespace failure must not suppress the style pass."""

    def test_crlf_runs_both_formatting_passes(self):
        def fake_run(args, **kw):
            if "whitespace" in args:
                return _run(2, "", "error ENDOFLINE: crlf")
            return _run(0, "", "")

        out, calls = _run_main(fake_run)
        subs = [c[2] for c in calls]
        self.assertEqual(subs, ["whitespace", "style", "style"])

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

    def test_style_pass_workspace_warning_fails_closed(self):
        calls = []

        def fake_run(args, **kw):
            calls.append(list(args))
            if "style" in args:
                return _run(0, "", "warn : Workspace failed to load project")
            return _run(0, "", "")

        with self.assertRaises(SystemExit) as ctx:
            _run_main(fake_run)
        self.assertEqual(ctx.exception.code, 1)
        self.assertEqual([c[2] for c in calls], ["whitespace", "style"])


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


class TestExplicitRestore(unittest.TestCase):
    """Best-effort targets get an explicit restore before format passes."""

    def test_solely_netsdk1147_skips(self):
        """Restore fails with only NETSDK1147 → skip gracefully."""
        def fake_run(args, **kw):
            if "restore" in args:
                return _run(1, "", "error NETSDK1147: workload not installed")
            return _run(0, "", "")

        out, _ = _run_main(fake_run, _Fixture(targets=[("Fake.csproj", True)]))
        self.assertIn("SKIP", out)
        self.assertNotIn("FAIL", out)

    def test_wrapper_only_fails(self):
        """Restore fails with generic wrapper, no NETSDK1147 → fail-closed."""
        def fake_run(args, **kw):
            if "restore" in args:
                return _run(1, "", (
                    "Unhandled exception: System.Exception: Restore operation failed.\n"
                    "   at Microsoft.CodeAnalysis.Tools.CodeFormatter"
                    ".OpenMSBuildWorkspaceAsync(...)"
                ))
            return _run(0, "", "")

        with self.assertRaises(SystemExit) as ctx:
            _run_main(fake_run, _Fixture(targets=[("Fake.csproj", True)]))
        self.assertEqual(ctx.exception.code, 1)

    def test_nuget_error_fails(self):
        """Restore fails with NuGet error → fail-closed."""
        def fake_run(args, **kw):
            if "restore" in args:
                return _run(1, "", "error NU1301: Unable to load package")
            return _run(0, "", "")

        with self.assertRaises(SystemExit) as ctx:
            _run_main(fake_run, _Fixture(targets=[("Fake.csproj", True)]))
        self.assertEqual(ctx.exception.code, 1)

    def test_mixed_netsdk1147_plus_nu_fails(self):
        """Restore fails with NETSDK1147 AND NuGet error → fail-closed."""
        def fake_run(args, **kw):
            if "restore" in args:
                return _run(1, "", "error NETSDK1147: workload\nerror NU1301: package")
            return _run(0, "", "")

        with self.assertRaises(SystemExit) as ctx:
            _run_main(fake_run, _Fixture(targets=[("Fake.csproj", True)]))
        self.assertEqual(ctx.exception.code, 1)

    def test_unclassified_error_fails(self):
        """Restore fails with unclassified error → fail-closed."""
        def fake_run(args, **kw):
            if "restore" in args:
                return _run(1, "", "error: something went wrong")
            return _run(0, "", "")

        with self.assertRaises(SystemExit) as ctx:
            _run_main(fake_run, _Fixture(targets=[("Fake.csproj", True)]))
        self.assertEqual(ctx.exception.code, 1)

    def test_timeout_fails(self):
        """Restore times out → fail-closed."""
        def fake_run(args, **kw):
            if "restore" in args:
                raise subprocess.TimeoutExpired(cmd=args, timeout=120)
            return _run(0, "", "")

        with self.assertRaises(SystemExit) as ctx:
            _run_main(fake_run, _Fixture(targets=[("Fake.csproj", True)]))
        self.assertEqual(ctx.exception.code, 1)

    def test_restored_success_all_passes_ok(self):
        """Restore succeeds → both format passes use --no-restore and succeed."""
        def fake_run(args, **kw):
            if "restore" in args:
                return _run(0, "", "")
            return _run(0, "", "")

        out, calls = _run_main(fake_run, _Fixture(targets=[("Fake.csproj", True)]))
        restore_calls = [c for c in calls if "restore" in c and "format" not in c]
        format_calls = [c for c in calls if "format" in c]
        self.assertTrue(restore_calls, "No explicit restore call found")
        for c in format_calls:
            self.assertIn("--no-restore", c, f"Format call missing --no-restore: {c}")
        self.assertIn("OK", out)
        self.assertNotIn("FAIL", out)

    def test_restored_success_style_fail(self):
        """Restore succeeds but style pass has formatting violation → fail."""
        def fake_run(args, **kw):
            if "restore" in args:
                return _run(0, "", "")
            if "style" in args and "--diagnostics" not in args:
                return _run(2, "", "error IDE0005: Remove unused using directive")
            return _run(0, "", "")

        with self.assertRaises(SystemExit) as ctx:
            _run_main(fake_run, _Fixture(targets=[("Fake.csproj", True)]))
        self.assertEqual(ctx.exception.code, 1)

    def test_mandatory_target_no_explicit_restore(self):
        """Mandatory targets skip the explicit restore step entirely."""
        def fake_run(args, **kw):
            return _run(0, "", "")

        out, calls = _run_main(fake_run, _Fixture(targets=[("Fake.slnf", False)]))
        restore_calls = [c for c in calls if "restore" in c and "format" not in c]
        self.assertFalse(restore_calls, "Mandatory target should not have explicit restore")
        format_calls = [c for c in calls if "format" in c]
        for c in format_calls:
            self.assertNotIn("--no-restore", c, f"Mandatory target should not use --no-restore: {c}")
        self.assertIn("OK", out)

    def test_mandatory_workload_error_fails(self):
        """Mandatory target with NETSDK1147 fails (no skip)."""
        def fake_run(args, **kw):
            return _run(2, "", "error NETSDK1147: workload not installed")

        with self.assertRaises(SystemExit) as ctx:
            _run_main(fake_run, _Fixture(targets=[("Fake.slnf", False)]))
        self.assertEqual(ctx.exception.code, 1)

    def test_netsdk1147_plus_bare_error_fatal(self):
        """NETSDK1147 + bare 'error:' → fail-closed (bare has no code)."""
        def fake_run(args, **kw):
            if "restore" in args:
                return _run(1, "", "error NETSDK1147: workload\nerror: something else")
            return _run(0, "", "")

        with self.assertRaises(SystemExit) as ctx:
            _run_main(fake_run, _Fixture(targets=[("Fake.csproj", True)]))
        self.assertEqual(ctx.exception.code, 1)

    def test_netsdk1147_plus_spaced_error_fatal(self):
        """NETSDK1147 + 'error :' (space before colon) → fail-closed."""
        def fake_run(args, **kw):
            if "restore" in args:
                return _run(1, "", "error NETSDK1147: workload\nerror : something else")
            return _run(0, "", "")

        with self.assertRaises(SystemExit) as ctx:
            _run_main(fake_run, _Fixture(targets=[("Fake.csproj", True)]))
        self.assertEqual(ctx.exception.code, 1)

    def test_nu1301_mentioning_netsdk1147_url_fatal(self):
        """NU1301 with NETSDK1147 in URL → fail-closed (code is NU1301, not NETSDK1147)."""
        def fake_run(args, **kw):
            if "restore" in args:
                return _run(1, "", "error NU1301: See https://aka.ms/netSDK1147")
            return _run(0, "", "")

        with self.assertRaises(SystemExit) as ctx:
            _run_main(fake_run, _Fixture(targets=[("Fake.csproj", True)]))
        self.assertEqual(ctx.exception.code, 1)

    def test_netsdk11470_wrong_code_fatal(self):
        """NETSDK11470 is not NETSDK1147 → fail-closed."""
        def fake_run(args, **kw):
            if "restore" in args:
                return _run(1, "", "error NETSDK11470: wrong code")
            return _run(0, "", "")

        with self.assertRaises(SystemExit) as ctx:
            _run_main(fake_run, _Fixture(targets=[("Fake.csproj", True)]))
        self.assertEqual(ctx.exception.code, 1)

    def test_realistic_path_prefixed_netsdk1147_skip(self):
        """Path-prefixed 'error NETSDK1147:' → skip."""
        def fake_run(args, **kw):
            if "restore" in args:
                return _run(1, "", "/home/runner/work/p/src/P.csproj : error NETSDK1147: workload")
            return _run(0, "", "")

        out, _ = _run_main(fake_run, _Fixture(targets=[("Fake.csproj", True)]))
        self.assertIn("SKIP", out)
        self.assertNotIn("FAIL", out)


class TestCleanPassExitZero(unittest.TestCase):
    """All passes clean must exit 0."""

    def test_clean_pass(self):
        out, _ = _run_main(lambda a, **kw: _run())
        self.assertIn("All projects formatted correctly", out)


if __name__ == "__main__":
    unittest.main()
