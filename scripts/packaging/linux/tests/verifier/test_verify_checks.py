"""Tests for verifier check functions: GLIBC, GLIBCXX/CXXABI, deps, WebKit, resources, symlinks, perms, no-execute.

Run:
  python -m pytest scripts/packaging/linux/tests/verifier/test_verify_checks.py -v
"""

import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch, MagicMock

from _fixtures import lib, mod, readelf_needs, readelf_defs, readelf_needed, readelf_rpath, make_elf


class TestGlibcCheck(unittest.TestCase):
    def _check(self, versions, max_v=(2, 35)):
        with tempfile.TemporaryDirectory() as td:
            elf = make_elf(Path(td) / "lib.so")
            with patch.object(lib, "run_readelf", return_value=readelf_needs(*versions)):
                return mod.check_glibc_versions([elf], max_v)

    def test_pass(self):
        self.assertEqual(self._check(["2.17", "2.31"]), [])

    def test_fail_238(self):
        issues = self._check(["2.17", "2.38"])
        self.assertEqual(len(issues), 1)
        self.assertIn("GLIBC_2.38", issues[0])
        self.assertIn("2.35", issues[0])

    def test_exact_boundary(self):
        self.assertEqual(self._check(["2.35"]), [])

    def test_multiple_elves(self):
        with tempfile.TemporaryDirectory() as td:
            good = make_elf(Path(td) / "good.so")
            bad = make_elf(Path(td) / "bad.so")
            # read_elf_requirements calls inspect_elf_dynamic (run_readelf -d)
            # then run_readelf -V. Each ELF needs 2 mock returns.
            with patch.object(lib, "run_readelf") as m:
                m.side_effect = [
                    "  NEEDED  [libc.so.6]", readelf_needs("2.17"),       # good: -d, -V
                    "  NEEDED  [libc.so.6]", readelf_needs("2.31", "2.38"),  # bad: -d, -V
                ]
                issues = mod.check_glibc_versions([good, bad], (2, 35))
        self.assertEqual(len(issues), 1)
        self.assertIn("bad.so", issues[0])


class TestGlibcxxSatisfaction(unittest.TestCase):
    def _make_stdcxx_app(self, td):
        root = Path(td)
        lib_dir = root / "usr" / "lib" / "x86_64-linux-gnu"
        lib_dir.mkdir(parents=True)
        make_elf(lib_dir / "libstdc++.so.6")
        elf = make_elf(root / "app")
        return root, [elf]

    def _mock_readelf_seq(self, defs_output, needs_output):
        """Return a side_effect that returns defs first, then needs for -V calls."""
        call_count = {"n": 0}

        def mock_run_readelf(path, *args):
            if args == ("-V",):
                call_count["n"] += 1
                if call_count["n"] == 1:
                    return defs_output
                return needs_output
            return ""

        return mock_run_readelf

    def test_bundled_satisfied(self):
        with tempfile.TemporaryDirectory() as td:
            root, elves = self._make_stdcxx_app(td)
            mock = self._mock_readelf_seq(
                readelf_defs("3.4", "3.4.1", "3.4.30"),
                readelf_needs(glibcxx=["3.4.1"]),
            )
            with patch.object(lib, "run_readelf", side_effect=mock):
                self.assertEqual(mod.check_glibcxx_satisfaction(root, elves, None), [])

    def test_bundled_missing_glibcxx_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root, elves = self._make_stdcxx_app(td)
            mock = self._mock_readelf_seq(
                readelf_defs("3.4", "3.4.1"),
                readelf_needs(glibcxx=["3.4.31"]),
            )
            with patch.object(lib, "run_readelf", side_effect=mock):
                issues = mod.check_glibcxx_satisfaction(root, elves, None)
        self.assertTrue(any("GLIBCXX_3.4.31" in i for i in issues))

    def test_cxxabi_missing_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root, elves = self._make_stdcxx_app(td)
            mock = self._mock_readelf_seq(
                readelf_defs("3.4.30", cxxabi=["1.3", "1.3.1"]),
                readelf_needs(cxxabi=["1.3.13"]),
            )
            with patch.object(lib, "run_readelf", side_effect=mock):
                issues = mod.check_glibcxx_satisfaction(root, elves, None)
        self.assertTrue(any("CXXABI_1.3.13" in i for i in issues))

    def test_exact_set_membership_not_max(self):
        """Gap in versions: provider has 3.4.1 and 3.4.30 but not 3.4.2."""
        with tempfile.TemporaryDirectory() as td:
            root, elves = self._make_stdcxx_app(td)
            mock = self._mock_readelf_seq(
                readelf_defs("3.4.1", "3.4.30"),
                readelf_needs(glibcxx=["3.4.2"]),
            )
            with patch.object(lib, "run_readelf", side_effect=mock):
                issues = mod.check_glibcxx_satisfaction(root, elves, None)
        self.assertTrue(any("GLIBCXX_3.4.2" in i for i in issues))

    def test_no_libstdcxx_fails_closed(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            (root / "usr" / "lib").mkdir(parents=True)
            with patch.object(mod, "find_libstdcxx", return_value=None):
                issues = mod.check_glibcxx_satisfaction(root, [], None)
        self.assertTrue(any("cannot verify" in i.lower() or "not found" in i.lower() for i in issues))


class TestDependencies(unittest.TestCase):
    def _check(self, needed, bundled=None, baseline=None):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            lib_dir = root / "usr" / "lib" / "x86_64-linux-gnu"
            lib_dir.mkdir(parents=True)
            for b in (bundled or []):
                (lib_dir / b).write_bytes(b"\x7fELF")
            elf = make_elf(root / "app")
            baseline_libs = set(baseline or ["libc.so.6", "libm.so.6", "libstdc++.so.6", "libgcc_s.so.1"])
            with patch.object(lib, "run_readelf", return_value=readelf_needed(*needed)):
                with patch.object(mod, "get_rpath_runpath", return_value=(None, None)):
                    return mod.check_dependencies(root, [elf], baseline_libs)

    def test_bundled_ok(self):
        self.assertEqual(self._check(["libfoo.so.1"], ["libfoo.so.1"]), [])

    def test_baseline_ok(self):
        self.assertEqual(self._check(["libc.so.6"], baseline=["libc.so.6"]), [])

    def test_missing_is_fail(self):
        issues = self._check(["libmissing.so.1"], baseline=[])
        self.assertEqual(len(issues), 1)
        self.assertIn("libmissing.so.1", issues[0])
        self.assertIn("not bundled", issues[0])

    def test_not_on_baseline_fails(self):
        issues = self._check(["libfoo.so.1"], baseline=["libc.so.6"])
        self.assertEqual(len(issues), 1)
        self.assertIn("libfoo.so.1", issues[0])

    def test_issue_is_fail_not_warn(self):
        issues = self._check(["libmissing.so.1"], baseline=[])
        for issue in issues:
            self.assertTrue(issue.startswith("DEP:"), f"Expected DEP: prefix, got: {issue}")

    def test_rpath_origin_relative_accepted(self):
        """$ORIGIN-relative RPATH (linuxdeploy standard) should be accepted."""
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            lib_dir = root / "usr" / "lib" / "x86_64-linux-gnu"
            lib_dir.mkdir(parents=True)
            bin_dir = root / "usr" / "bin"
            bin_dir.mkdir(parents=True)
            elf = make_elf(bin_dir / "app")
            with patch.object(lib, "run_readelf", return_value=readelf_needed("libc.so.6")):
                with patch.object(mod, "get_rpath_runpath", return_value=("$ORIGIN/../lib", None)):
                    issues = mod.check_dependencies(root, [elf], {"libc.so.6"})
        # No RPATH issue should be raised for $ORIGIN-relative
        self.assertFalse(any("RPATH" in i for i in issues))

    def test_rpath_absolute_rejected(self):
        """Absolute RPATH should be rejected."""
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            lib_dir = root / "usr" / "lib" / "x86_64-linux-gnu"
            lib_dir.mkdir(parents=True)
            elf = make_elf(root / "app")
            with patch.object(lib, "run_readelf", return_value=readelf_needed("libcustom.so.1")):
                with patch.object(mod, "get_rpath_runpath", return_value=("/opt/custom/lib", None)):
                    issues = mod.check_dependencies(root, [elf], set())
        self.assertTrue(any("unsupported" in i and "absolute" in i for i in issues))

    def test_tool_failure_fails_closed(self):
        """ELF that readelf/objdump can't inspect must fail closed."""
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            (root / "usr" / "lib").mkdir(parents=True)
            elf = make_elf(root / "app")
            with patch.object(lib, "read_elf_requirements", return_value=(lib.TOOL_FAILURE, set(), set())):
                issues = mod.check_glibc_versions([elf], (2, 35))
        self.assertTrue(any("failed" in i.lower() for i in issues))

    def test_needed_libs_tool_failure_fails_closed(self):
        """When read_needed_libs returns TOOL_FAILURE, report explicit fail."""
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            lib_dir = root / "usr" / "lib" / "x86_64-linux-gnu"
            lib_dir.mkdir(parents=True)
            elf = make_elf(root / "app")
            with patch.object(mod, "get_rpath_runpath", return_value=(None, None)):
                with patch.object(lib, "read_needed_libs", return_value=lib.TOOL_FAILURE):
                    issues = mod.check_dependencies(root, [elf], set())
        self.assertTrue(any("readelf -d failed" in i for i in issues))

    def test_rpath_overridden_by_runpath_reported(self):
        """When both RPATH and RUNPATH present, report RPATH as overridden."""
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            lib_dir = root / "usr" / "lib" / "x86_64-linux-gnu"
            lib_dir.mkdir(parents=True)
            elf = make_elf(root / "app")
            with patch.object(mod, "get_rpath_runpath", return_value=("$ORIGIN/lib", "$ORIGIN/lib2")):
                with patch.object(lib, "read_needed_libs", return_value=["libcustom.so.1"]):
                    issues = mod.check_dependencies(root, [elf], set())
        self.assertTrue(any("overridden" in i and "RUNPATH" in i for i in issues))


class TestWebKitPaths(unittest.TestCase):
    def test_resolved_lib_with_relative_ok(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            lib_dir = root / "usr" / "lib" / "x86_64-linux-gnu"
            lib_dir.mkdir(parents=True)
            libf = lib_dir / "libwebkit2gtk-4.1.so.0"
            libf.write_bytes(b"\x7fELF" + mod.WEBKIT_RELATIVE_PREFIX.encode() + b"\x00" * 60)
            self.assertEqual(mod.check_webkit_paths(root, None), [])

    def test_resolved_lib_missing_relative_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            lib_dir = root / "usr" / "lib" / "x86_64-linux-gnu"
            lib_dir.mkdir(parents=True)
            libf = lib_dir / "libwebkit2gtk-4.1.so.0"
            libf.write_bytes(b"\x7fELF" + b"\x00" * 100)
            issues = mod.check_webkit_paths(root, None)
        self.assertTrue(any("relative prefix missing" in i for i in issues))

    def test_host_prefix_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            lib_dir = root / "usr" / "lib" / "x86_64-linux-gnu"
            lib_dir.mkdir(parents=True)
            libf = lib_dir / "libwebkit2gtk-4.1.so.0"
            libf.write_bytes(
                b"\x7fELF" + mod.WEBKIT_HOST_PREFIX.encode()
                + mod.WEBKIT_RELATIVE_PREFIX.encode() + b"\x00" * 60
            )
            issues = mod.check_webkit_paths(root, None)
        self.assertTrue(any("unrelocated host prefix" in i for i in issues))

    def test_not_found_fails(self):
        with tempfile.TemporaryDirectory() as td:
            issues = mod.check_webkit_paths(Path(td), None)
        self.assertTrue(any("not found" in i for i in issues))

    def test_old_truncated_prefix_rejected(self):
        """Old 39-byte truncated prefix must be detected and rejected."""
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            lib_dir = root / "usr" / "lib" / "x86_64-linux-gnu"
            lib_dir.mkdir(parents=True)
            libf = lib_dir / "libwebkit2gtk-4.1.so.0"
            old_prefix = "././lib/x86_64-linux-gnu/webkit2gtk-4.1"
            libf.write_bytes(b"\x7fELF" + old_prefix.encode() + b"\x00" * 60)
            issues = mod.check_webkit_paths(root, None)
        self.assertTrue(any("old truncated" in i for i in issues))

    def test_new_prefix_length_is_40(self):
        """WEBKIT_RELATIVE_PREFIX must be exactly 40 bytes (same as host prefix)."""
        self.assertEqual(len(mod.WEBKIT_RELATIVE_PREFIX), 40)
        self.assertEqual(len(mod.WEBKIT_RELATIVE_PREFIX), len(mod.WEBKIT_HOST_PREFIX))


class TestResources(unittest.TestCase):
    def _full_webkit(self, td):
        root = Path(td)
        usr = root / "usr"
        for rel in mod.REQUIRED_WEBKIT_BINARIES:
            p = usr / rel
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_bytes(b"\x7fELF")
        # libwebkit2gtk-4.1.so.0 goes in usr/lib/ (where linuxdeploy puts it)
        lib_dir = usr / "lib"
        lib_dir.mkdir(parents=True, exist_ok=True)
        (lib_dir / "libwebkit2gtk-4.1.so.0").write_bytes(b"\x7fELF")
        bundle = usr / mod.INJECTED_BUNDLE_DIR
        bundle.mkdir(parents=True, exist_ok=True)
        (bundle / mod.INJECTED_BUNDLE_NAME).write_bytes(b"\x7fELF")
        return root

    def test_all_present_passes(self):
        with tempfile.TemporaryDirectory() as td:
            self.assertEqual(mod.check_resources(self._full_webkit(td)), [])

    def test_missing_binary_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root = self._full_webkit(td)
            (root / "usr" / mod.REQUIRED_WEBKIT_BINARIES[0]).unlink()
            issues = mod.check_resources(root)
        self.assertTrue(any("missing" in i for i in issues))

    def test_missing_injected_bundle_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root = self._full_webkit(td)
            shutil.rmtree(root / "usr" / mod.INJECTED_BUNDLE_DIR)
            issues = mod.check_resources(root)
        self.assertTrue(any("injected-bundle" in i for i in issues))

    def test_missing_exact_bundle_name_fails(self):
        """Bundle dir exists but without the exact libwebkit2gtkinjectedbundle.so."""
        with tempfile.TemporaryDirectory() as td:
            root = self._full_webkit(td)
            (root / "usr" / mod.INJECTED_BUNDLE_DIR / mod.INJECTED_BUNDLE_NAME).unlink()
            (root / "usr" / mod.INJECTED_BUNDLE_DIR / "other.so").write_bytes(b"\x7fELF")
            issues = mod.check_resources(root)
        self.assertTrue(any(mod.INJECTED_BUNDLE_NAME in i for i in issues))

    def test_webkit_lib_in_usr_lib_passes(self):
        """libwebkit2gtk-4.1.so.0 in usr/lib/ (linuxdeploy location) must pass."""
        with tempfile.TemporaryDirectory() as td:
            root = self._full_webkit(td)
            # It's already in usr/lib/ from _full_webkit
            self.assertEqual(mod.check_resources(root), [])

    def test_webkit_lib_in_subdir_passes(self):
        """libwebkit2gtk-4.1.so.0 in the webkit subdir must also pass."""
        with tempfile.TemporaryDirectory() as td:
            root = self._full_webkit(td)
            # Remove from usr/lib/ and put in webkit subdir
            (root / "usr" / "lib" / "libwebkit2gtk-4.1.so.0").unlink()
            wk_dir = root / "usr" / "lib" / "x86_64-linux-gnu" / "webkit2gtk-4.1"
            wk_dir.mkdir(parents=True, exist_ok=True)
            (wk_dir / "libwebkit2gtk-4.1.so.0").write_bytes(b"\x7fELF")
            self.assertEqual(mod.check_resources(root), [])

    def test_webkit_lib_nowhere_fails(self):
        """Missing libwebkit2gtk-4.1.so.0 in both locations must fail."""
        with tempfile.TemporaryDirectory() as td:
            root = self._full_webkit(td)
            # Remove from usr/lib/
            (root / "usr" / "lib" / "libwebkit2gtk-4.1.so.0").unlink()
            issues = mod.check_resources(root)
        self.assertTrue(any("libwebkit2gtk-4.1.so.0" in i for i in issues))

    def test_trace_provider_present_fails(self):
        """libcoreclrtraceptprovider.so must be removed (deliberate exclusion)."""
        with tempfile.TemporaryDirectory() as td:
            root = self._full_webkit(td)
            bin_dir = root / "usr" / "bin"
            bin_dir.mkdir(parents=True, exist_ok=True)
            (bin_dir / "libcoreclrtraceptprovider.so").write_bytes(b"\x7fELF")
            issues = mod.check_resources(root)
        self.assertTrue(any("libcoreclrtraceptprovider" in i for i in issues))

    def test_trace_provider_absent_passes(self):
        """Resources check must pass when trace provider is correctly removed."""
        with tempfile.TemporaryDirectory() as td:
            root = self._full_webkit(td)
            # No trace provider present — should pass
            self.assertEqual(mod.check_resources(root), [])


class TestSymlinks(unittest.TestCase):
    def test_safe(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            t = root / "real.txt"
            t.write_text("ok")
            (root / "link.txt").symlink_to("real.txt")  # relative symlink
            self.assertEqual(mod.check_symlinks(root), [])

    def test_escape(self):
        with tempfile.TemporaryDirectory() as td:
            (Path(td) / "escape.txt").symlink_to("/etc/passwd")
            issues = mod.check_symlinks(Path(td))
        self.assertTrue(any(("escapes root" in i or "broken" in i or "absolute" in i) for i in issues))

    def test_broken(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            (root / "broken.txt").symlink_to(root / "nope.txt")
            issues = mod.check_symlinks(root)
        self.assertTrue(any("broken" in i for i in issues))

    def test_absolute_symlink_rejected(self):
        """Absolute symlinks must be rejected even if target is within temp."""
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            target = root / "target.txt"
            target.write_text("ok")
            (root / "abs_link.txt").symlink_to(target)  # absolute path
            issues = mod.check_symlinks(root)
        self.assertTrue(any("absolute" in i for i in issues))


class TestPermissions(unittest.TestCase):
    def test_no_appimage_reports_missing(self):
        with tempfile.TemporaryDirectory() as td:
            issues = mod.check_permissions(Path(td))
        self.assertTrue(any("AppRun missing" in i for i in issues))
        self.assertTrue(any("MqttProbe.Desktop missing" in i for i in issues))

    def test_apprun_not_exec(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            (root / "AppRun").write_text("#!/bin/sh\n")
            issues = mod.check_permissions(root)
        self.assertTrue(any("AppRun" in i and "not executable" in i for i in issues))

    def test_apprun_missing(self):
        with tempfile.TemporaryDirectory() as td:
            issues = mod.check_permissions(Path(td))
        self.assertTrue(any("AppRun missing" in i for i in issues))

    @unittest.skipIf(sys.platform == "win32", "chmod does not set Unix bits on Windows")
    def test_apprun_exec(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            ar = root / "AppRun"
            ar.write_text("#!/bin/sh\n")
            ar.chmod(0o755)
            main = root / "usr" / "bin" / "MqttProbe.Desktop"
            main.parent.mkdir(parents=True)
            main.write_bytes(b"\x7fELF" + b"\x00" * 60)
            main.chmod(0o755)
            self.assertEqual(mod.check_permissions(root), [])


class TestNoExecute(unittest.TestCase):
    def test_find_elf_never_calls_subprocess(self):
        with tempfile.TemporaryDirectory() as td:
            make_elf(Path(td) / "lib.so")
            with patch("subprocess.run") as m:
                found = mod.find_elf_files(Path(td))
                m.assert_not_called()
        self.assertEqual(len(found), 1)

    def test_check_glibc_calls_readelf_not_exec(self):
        with tempfile.TemporaryDirectory() as td:
            elf = make_elf(Path(td) / "lib.so")
            calls = []

            def track(args, **kw):
                calls.append(list(args))
                return MagicMock(returncode=1, stdout="", stderr="")

            with patch.object(lib.subprocess, "run", side_effect=track):
                mod.check_glibc_versions([elf], (2, 35))
            for c in calls:
                self.assertNotEqual(c[0], str(elf))


if __name__ == "__main__":
    unittest.main()