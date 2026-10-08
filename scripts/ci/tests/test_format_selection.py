#!/usr/bin/env python3
"""Tests for scoped source selection in format-check.py."""

import contextlib
import importlib.util
import io
import os
import shlex
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

SCRIPT = Path(__file__).resolve().parents[1] / "format-check.py"
SPEC = importlib.util.spec_from_file_location("ci_format_selection", SCRIPT)
if SPEC is None or SPEC.loader is None:
    raise SystemExit("Cannot load scripts/ci/format-check.py")
format_ci = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(format_ci)


def _run_main_quietly(args):
    output = io.StringIO()
    with contextlib.redirect_stdout(output), contextlib.redirect_stderr(io.StringIO()):
        format_ci.main(args)
    return output.getvalue()


def _expected_call_sequence():
    """The args[2] of every _run_dotnet call a full-tree run makes.

    TARGETS is platform-dependent by design (see format-check.py): Windows formats the
    whole solution, while other platforms add the MAUI project behind an explicit
    restore. Deriving the expectation keeps these tests about invocation shape instead
    of hardcoding one platform's target set.
    """
    expected = []
    for target, needs_workload in format_ci.TARGETS:
        if needs_workload:
            expected.append(str(format_ci.ROOT / target))
        expected.extend(["whitespace", "style"])
    return expected


class GitFixture(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.git("init", "-q")
        self.git("config", "user.email", "format-tests@example.invalid")
        self.git("config", "user.name", "Format Tests")
        self._old_root = format_ci.ROOT
        format_ci.ROOT = self.root

    def tearDown(self):
        format_ci.ROOT = self._old_root
        self.temp.cleanup()

    def git(self, *args):
        return subprocess.run(
            ["git", "-C", str(getattr(self, "root", self.temp.name)), *args],
            check=True, capture_output=True,
        )

    def put(self, relative, content="class C {}\n"):
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")
        return path

    def commit_all(self):
        self.git("add", "-A")
        self.git("commit", "-qm", "fixture")


class TestScopedSelection(GitFixture):
    def test_untracked_and_unicode_paths_are_selected(self):
        path = self.put("src/space and café.cshtml")
        full_tree, includes = format_ci._select_files("changed")
        self.assertFalse(full_tree)
        self.assertEqual(includes, [path.resolve()])

    def test_changed_combines_staged_unstaged_and_untracked_sources(self):
        staged = self.put("src/staged.cs")
        unstaged = self.put("src/unstaged.cs")
        self.commit_all()

        staged.write_text("class Staged { int Changed; }\n", encoding="utf-8")
        self.git("add", "src/staged.cs")
        unstaged.write_text("class Unstaged { int Changed; }\n", encoding="utf-8")
        untracked = self.put("src/untracked.cshtml", "<p>new</p>\n")

        full_tree, includes = format_ci._select_files("changed")
        self.assertFalse(full_tree)
        self.assertEqual(set(includes), {staged.resolve(), unstaged.resolve(), untracked.resolve()})

    def test_deleted_source_is_not_included(self):
        path = self.put("src/deleted.cs")
        self.commit_all()
        path.unlink()
        full_tree, includes = format_ci._select_files("changed")
        self.assertFalse(full_tree)
        self.assertEqual(includes, [])

    def test_rename_selects_destination_only(self):
        old_path = self.put("src/old name.cs", "class RenameMe { int Value; }\n")
        self.commit_all()
        new_path = self.root / "src" / "new café name.cs"
        old_path.rename(new_path)
        full_tree, includes = format_ci._select_files("changed")
        self.assertFalse(full_tree)
        self.assertEqual(includes, [new_path.resolve()])

    def test_staged_path_uses_current_disk_file(self):
        path = self.put("src/current.cs", "class C { int Staged; }\n")
        self.commit_all()
        path.write_text("class C { int Staged; int CurrentDisk; }\n", encoding="utf-8")
        self.git("add", "src/current.cs")
        path.write_text("class C { int Staged; int CurrentDisk; int Latest; }\n", encoding="utf-8")

        full_tree, includes = format_ci._select_files("staged")
        self.assertFalse(full_tree)
        self.assertEqual(includes, [path.resolve()])
        self.assertIn("Latest", includes[0].read_text(encoding="utf-8"))

    def test_staged_rename_includes_destination_only(self):
        old_path = self.put("src/old-name.cs", "class RenameMe { int Value; }\n")
        self.commit_all()
        new_path = self.root / "src" / "new-name.cs"
        self.git("mv", "src/old-name.cs", "src/new-name.cs")

        full_tree, includes = format_ci._select_files("staged")
        self.assertFalse(full_tree)
        self.assertEqual(includes, [new_path.resolve()])
        self.assertNotIn(old_path.resolve(), includes)

    def test_staged_excludes_unstaged_and_untracked_sources(self):
        staged = self.put("src/staged-only.cs")
        unstaged = self.put("src/unstaged-only.cs")
        self.commit_all()
        staged.write_text("class Staged { int Changed; }\n", encoding="utf-8")
        self.git("add", "src/staged-only.cs")
        unstaged.write_text("class Unstaged { int Changed; }\n", encoding="utf-8")
        self.put("src/untracked-only.cs")

        full_tree, includes = format_ci._select_files("staged")
        self.assertFalse(full_tree)
        self.assertEqual(includes, [staged.resolve()])

    def test_unborn_head_includes_staged_and_untracked_sources(self):
        staged = self.put("src/staged.cs")
        self.git("add", "src/staged.cs")
        untracked = self.put("src/untracked.cshtml")
        full_tree, includes = format_ci._select_files("changed")
        self.assertFalse(full_tree)
        self.assertEqual(includes, sorted([staged.resolve(), untracked.resolve()], key=lambda p: os.fspath(p).casefold()))

    def test_workspace_configuration_change_forces_full_tree(self):
        self.put("src/change.cs")
        self.put("Directory.Build.targets", "<Project />\n")
        full_tree, includes = format_ci._select_files("changed")
        self.assertTrue(full_tree)
        self.assertEqual(includes, [])

    def test_workspace_input_suffixes_force_full_tree(self):
        for path in (
            ".editorconfig", "sample.csproj", "sample.fsproj", "sample.vbproj",
            "sample.props", "sample.targets", "sample.sln", "sample.slnx",
            "sample.slnf", "global.json", "NuGet.Config",
            "Directory.Packages.props", "Directory.Build.rsp", "dotnet-tools.json",
        ):
            with self.subTest(path=path):
                self.assertTrue(format_ci._is_workspace_input(path))

    def test_deleted_workspace_configuration_forces_full_tree(self):
        config = self.put("NuGet.Config", "<configuration />\n")
        self.put("src/change.cs")
        self.commit_all()
        config.unlink()
        full_tree, includes = format_ci._select_files("changed")
        self.assertTrue(full_tree)
        self.assertEqual(includes, [])

    def test_staged_deleted_configuration_forces_full_tree(self):
        self.put(".editorconfig", "root = true\n")
        self.commit_all()
        self.git("rm", ".editorconfig")

        full_tree, includes = format_ci._select_files("staged")
        self.assertTrue(full_tree)
        self.assertEqual(includes, [])

    def test_config_only_selection_runs_full_passes_not_noop(self):
        self.put("NuGet.Config", "<configuration />\n")
        self.commit_all()
        self.git("rm", "NuGet.Config")
        calls = []

        def fake_run(args, **kwargs):
            calls.append(args)
            return type("Result", (), {"returncode": 0, "stdout": "", "stderr": ""})()

        with patch.object(format_ci, "_run_dotnet", side_effect=fake_run):
            output = _run_main_quietly(["--staged"])

        self.assertIn("workspace or policy input changed", output)
        self.assertNotIn("Nothing to format", output)
        self.assertEqual([call[2] for call in calls], _expected_call_sequence())
        self.assertTrue(all("--include" not in call for call in calls))

    def test_expected_sequence_restores_best_effort_target_first(self):
        # The multi-target shape is the Linux one, so cover it on every platform.
        with patch.object(format_ci, "TARGETS", [("A.slnf", False), ("B.csproj", True)]):
            self.assertEqual(
                _expected_call_sequence(),
                ["whitespace", "style", str(format_ci.ROOT / "B.csproj"), "whitespace", "style"],
            )

    def test_deleted_workspace_config_on_rename_forces_full_tree(self):
        config = self.put(".editorconfig", "root = true\n")
        self.commit_all()
        config.rename(self.root / "editorconfig.backup")
        full_tree, _ = format_ci._select_files("changed")
        self.assertTrue(full_tree)

    def test_git_errors_fail_closed(self):
        with tempfile.TemporaryDirectory() as other:
            with patch.object(format_ci, "ROOT", Path(other)):
                with self.assertRaises(RuntimeError):
                    format_ci._select_files("changed")

    def test_git_error_exits_nonzero_without_running_dotnet(self):
        with tempfile.TemporaryDirectory() as other:
            with patch.object(format_ci, "ROOT", Path(other)):
                with patch.object(format_ci, "_run_dotnet") as run_dotnet:
                    with self.assertRaises(SystemExit) as ctx:
                        _run_main_quietly(["--changed"])
        self.assertEqual(ctx.exception.code, 1)
        run_dotnet.assert_not_called()

    def test_empty_selection_is_successful_noop_without_dotnet(self):
        with patch.object(format_ci, "_run_dotnet") as run_dotnet:
            output = _run_main_quietly(["--changed"])
        run_dotnet.assert_not_called()
        self.assertIn("Nothing to format", output)
        self.assertNotIn("All projects formatted correctly", output)

    def test_scoped_invocations_use_separate_repo_relative_include_arguments(self):
        path = self.put("src/space café.cs")
        calls = []

        def fake_run(args, **kwargs):
            calls.append((args, kwargs))
            return type("Result", (), {"returncode": 0, "stdout": "", "stderr": ""})()

        with patch.object(format_ci, "_run_dotnet", side_effect=fake_run):
            format_ci.main(["--changed"])

        self.assertEqual([args[2] for args, _ in calls], _expected_call_sequence())
        for call, kwargs in calls:
            if "--include" not in call:
                continue
            include_index = call.index("--include")
            self.assertEqual(call[include_index + 1], os.path.relpath(path, self.root))
            self.assertEqual(call[include_index + 2], "--verify-no-changes")
            self.assertEqual(kwargs["cwd"], self.root)

    def test_selectors_are_mutually_exclusive(self):
        with self.assertRaises(SystemExit) as ctx:
            with contextlib.redirect_stderr(io.StringIO()):
                format_ci._parse_args(["--changed", "--staged"])
        self.assertEqual(ctx.exception.code, 2)

    def test_fix_combines_with_each_scope(self):
        self.assertTrue(format_ci._parse_args(["--changed", "--fix"]).fix)
        self.assertTrue(format_ci._parse_args(["--staged", "--fix"]).fix)

    def test_irrelevant_untracked_file_is_noop(self):
        self.put("docs/guide.md", "notes\n")
        with patch.object(format_ci, "_run_dotnet") as run_dotnet:
            _run_main_quietly(["--changed"])
        run_dotnet.assert_not_called()

    def test_fix_mode_keeps_scope_and_skips_verify_flag(self):
        path = self.put("src/fix-me.cs")
        calls = []

        def fake_run(args, **kwargs):
            calls.append((args, kwargs))
            return type("Result", (), {"returncode": 0, "stdout": "", "stderr": ""})()

        with patch.object(format_ci, "_run_dotnet", side_effect=fake_run):
            _run_main_quietly(["--changed", "--fix"])

        self.assertEqual([args[2] for args, _ in calls], _expected_call_sequence())
        for call, kwargs in calls:
            if "--include" not in call:
                continue
            self.assertEqual(call[call.index("--include") + 1], os.path.relpath(path, self.root))
            self.assertNotIn("--verify-no-changes", call)
            self.assertEqual(kwargs["cwd"], self.root)


@unittest.skipUnless(shutil.which("bash"), "bash is unavailable")
class TestPreCommitFormatSelection(unittest.TestCase):
    def test_hook_helper_recognizes_sources_and_full_fallback_inputs(self):
        lib = Path(__file__).resolve().parents[3] / ".githooks" / "lib.sh"
        lib_path = str(lib)
        if os.name == "nt":
            lib_path = f"/mnt/{lib.drive[0].lower()}/{lib.as_posix().split(':', 1)[1].lstrip('/')}"
        paths = [
            "src/Page.cshtml", "src/App.fsproj", "Directory.Build.props",
            "MqttProbe.slnx", "global.json", "config/NuGeT.CoNfIg",
            "Directory.Packages.props", "Directory.Solution.targets",
        ]
        for path in paths:
            result = subprocess.run(
                ["bash", "-c", (
                    f"source {shlex.quote(lib_path)}; "
                    f"is_format_relevant_path {shlex.quote(path)}"
                )],
                check=False, capture_output=True, text=True,
            )
            self.assertEqual(result.returncode, 0, f"{path}: {result.stderr}")

        newline_path = "src/newline\nname.cshtml"
        array_check = subprocess.run(
            ["bash", "-c", (
                f"source {shlex.quote(lib_path)}; "
                f"any_format_relevant_paths {shlex.quote(newline_path)}"
            )],
            check=False, capture_output=True, text=True,
        )
        self.assertEqual(array_check.returncode, 0, array_check.stderr)

    def test_pre_commit_calls_staged_scope(self):
        hook = Path(__file__).resolve().parents[3] / ".githooks" / "pre-commit"
        content = hook.read_text(encoding="utf-8")
        self.assertIn('format-check.py" --staged', content)
        self.assertIn("--name-only -z", content)
        self.assertIn('any_format_relevant_paths "${STAGED_PATHS[@]}"', content)


if __name__ == "__main__":
    unittest.main()
