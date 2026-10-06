"""Tests for resolve_provider and find_libstdcxx: baseline absolute symlink
resolution, correct source labeling (baseline vs bundled), version defs
inspected at the baseline's own file (not the host's).

Run:
  python -m pytest scripts/packaging/linux/tests/verifier/test_verify_provider.py -v
"""

import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from _fixtures import lib, mod, readelf_defs, readelf_needs, make_elf


@unittest.skipIf(sys.platform == "win32", "Unix symlinks required")
class TestResolveProviderBaselineSymlinks(unittest.TestCase):
    """resolve_provider must resolve baseline absolute symlinks within
    baseline_root, not follow them to the host filesystem."""

    def test_absolute_symlink_resolved_within_baseline(self):
        """Baseline libstdc++.so.6 -> /usr/lib/.../libstdc++.so.6.0.30 must
        resolve to baseline_root/usr/lib/.../libstdc++.so.6.0.30."""
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            appdir = root / "appdir"
            baseline = root / "baseline"
            lib_dir = baseline / "usr" / "lib" / "x86_64-linux-gnu"
            lib_dir.mkdir(parents=True)
            real = lib_dir / "libstdc++.so.6.0.30"
            real.write_bytes(b"\x7fELF" + b"\x00" * 60)
            link = lib_dir / "libstdc++.so.6"
            link.symlink_to("/usr/lib/x86_64-linux-gnu/libstdc++.so.6.0.30")
            result = lib.resolve_provider(
                appdir, "libstdc++.so.6", baseline_root=baseline,
            )
        self.assertIsNotNone(result)
        self.assertEqual(result, real)

    def test_bundled_takes_precedence_over_baseline(self):
        """When lib is both bundled and baseline, bundled wins."""
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            appdir = root / "appdir"
            baseline = root / "baseline"
            b_dir = appdir / "usr" / "lib" / "x86_64-linux-gnu"
            b_dir.mkdir(parents=True)
            bundled = b_dir / "libstdc++.so.6"
            bundled.write_bytes(b"\x7fELF" + b"\x00" * 60)
            bl_dir = baseline / "usr" / "lib" / "x86_64-linux-gnu"
            bl_dir.mkdir(parents=True)
            (bl_dir / "libstdc++.so.6.0.30").write_bytes(b"\x7fELF" + b"\x00" * 60)
            (bl_dir / "libstdc++.so.6").symlink_to(
                "/usr/lib/x86_64-linux-gnu/libstdc++.so.6.0.30"
            )
            result = lib.resolve_provider(
                appdir, "libstdc++.so.6", baseline_root=baseline,
            )
        self.assertIsNotNone(result)
        self.assertTrue(str(result).startswith(str(appdir)))

    def test_relative_symlink_in_baseline_resolves_normally(self):
        """Relative symlinks in baseline resolve within the baseline dir."""
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            appdir = root / "appdir"
            baseline = root / "baseline"
            lib_dir = baseline / "usr" / "lib" / "x86_64-linux-gnu"
            lib_dir.mkdir(parents=True)
            real = lib_dir / "libfoo.so.1.2.3"
            real.write_bytes(b"\x7fELF" + b"\x00" * 60)
            link = lib_dir / "libfoo.so.1"
            link.symlink_to("libfoo.so.1.2.3")
            result = lib.resolve_provider(
                appdir, "libfoo.so.1", baseline_root=baseline,
            )
        self.assertIsNotNone(result)
        self.assertEqual(result, real)

    def test_baseline_regular_file_found(self):
        """Regular file in baseline (no symlink) is found directly."""
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            appdir = root / "appdir"
            baseline = root / "baseline"
            lib_dir = baseline / "usr" / "lib" / "x86_64-linux-gnu"
            lib_dir.mkdir(parents=True)
            real = lib_dir / "libfoo.so.1"
            real.write_bytes(b"\x7fELF" + b"\x00" * 60)
            result = lib.resolve_provider(
                appdir, "libfoo.so.1", baseline_root=baseline,
            )
        self.assertIsNotNone(result)
        self.assertEqual(result, real)

    def test_broken_absolute_symlink_in_baseline_returns_none(self):
        """Absolute symlink whose target doesn't exist in baseline returns None."""
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            appdir = root / "appdir"
            baseline = root / "baseline"
            lib_dir = baseline / "usr" / "lib" / "x86_64-linux-gnu"
            lib_dir.mkdir(parents=True)
            link = lib_dir / "libmissing.so.1"
            link.symlink_to("/usr/lib/x86_64-linux-gnu/libmissing-9.9.so")
            result = lib.resolve_provider(
                appdir, "libmissing.so.1", baseline_root=baseline,
            )
        self.assertIsNone(result)

    def test_not_found_returns_none(self):
        """Library not in bundled or baseline returns None."""
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            appdir = root / "appdir"
            baseline = root / "baseline"
            (baseline / "usr" / "lib" / "x86_64-linux-gnu").mkdir(parents=True)
            result = lib.resolve_provider(
                appdir, "libnothere.so.1", baseline_root=baseline,
            )
        self.assertIsNone(result)


@unittest.skipIf(sys.platform == "win32", "Unix symlinks required")
class TestFindLibstdcxxBaseline(unittest.TestCase):
    """find_libstdcxx must label baseline provider as 'baseline', not 'bundled'."""

    def test_baseline_absolute_symlink_returns_baseline_label(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            appdir = root / "appdir"
            baseline = root / "baseline"
            lib_dir = baseline / "usr" / "lib" / "x86_64-linux-gnu"
            lib_dir.mkdir(parents=True)
            real = lib_dir / "libstdc++.so.6.0.30"
            real.write_bytes(b"\x7fELF" + b"\x00" * 60)
            link = lib_dir / "libstdc++.so.6"
            link.symlink_to("/usr/lib/x86_64-linux-gnu/libstdc++.so.6.0.30")
            result = lib.find_libstdcxx(appdir, baseline_root=baseline)
        self.assertIsNotNone(result)
        path, source = result
        self.assertEqual(source, "baseline")
        self.assertEqual(path, real)

    def test_bundled_label_when_bundled(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            appdir = root / "appdir"
            lib_dir = appdir / "usr" / "lib" / "x86_64-linux-gnu"
            lib_dir.mkdir(parents=True)
            real = lib_dir / "libstdc++.so.6"
            real.write_bytes(b"\x7fELF" + b"\x00" * 60)
            result = lib.find_libstdcxx(appdir, baseline_root=None)
        self.assertIsNotNone(result)
        path, source = result
        self.assertEqual(source, "bundled")

    def test_none_when_not_found(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            appdir = root / "appdir"
            baseline = root / "baseline"
            (baseline / "usr" / "lib" / "x86_64-linux-gnu").mkdir(parents=True)
            result = lib.find_libstdcxx(appdir, baseline_root=baseline)
        self.assertIsNone(result)


@unittest.skipIf(sys.platform == "win32", "Unix symlinks required")
class TestGlibcxxSatisfactionBaseline(unittest.TestCase):
    """check_glibcxx_satisfaction must read version defs from baseline's
    libstdc++ (resolved within baseline_root), not the host's."""

    def _setup_baseline_stdcxx(self, td):
        """Create baseline with libstdc++.so.6 absolute symlink and appdir."""
        root = Path(td)
        appdir = root / "appdir"
        baseline = root / "baseline"
        # Baseline lib with absolute symlink
        lib_dir = baseline / "usr" / "lib" / "x86_64-linux-gnu"
        lib_dir.mkdir(parents=True)
        real = lib_dir / "libstdc++.so.6.0.30"
        real.write_bytes(b"\x7fELF" + b"\x00" * 60)
        link = lib_dir / "libstdc++.so.6"
        link.symlink_to("/usr/lib/x86_64-linux-gnu/libstdc++.so.6.0.30")
        # Appdir with an ELF
        bin_dir = appdir / "usr" / "bin"
        bin_dir.mkdir(parents=True)
        elf = make_elf(bin_dir / "app")
        return appdir, baseline, [elf], real

    def test_baseline_provides_required_versions(self):
        with tempfile.TemporaryDirectory() as td:
            appdir, baseline, elves, real = self._setup_baseline_stdcxx(td)
            # Mock readelf: first call reads defs, second reads needs
            call_count = {"n": 0}

            def mock_readelf(path, *args):
                if args == ("-V",):
                    call_count["n"] += 1
                    if call_count["n"] == 1:
                        return readelf_defs("3.4", "3.4.1", "3.4.30")
                    return readelf_needs(glibcxx=["3.4.30"])
                return ""

            with patch.object(lib, "run_readelf", side_effect=mock_readelf):
                issues = mod.check_glibcxx_satisfaction(appdir, elves, baseline)
        self.assertEqual(issues, [])

    def test_baseline_missing_version_fails(self):
        with tempfile.TemporaryDirectory() as td:
            appdir, baseline, elves, real = self._setup_baseline_stdcxx(td)
            call_count = {"n": 0}

            def mock_readelf(path, *args):
                if args == ("-V",):
                    call_count["n"] += 1
                    if call_count["n"] == 1:
                        return readelf_defs("3.4", "3.4.1")
                    return readelf_needs(glibcxx=["3.4.30"])
                return ""

            with patch.object(lib, "run_readelf", side_effect=mock_readelf):
                issues = mod.check_glibcxx_satisfaction(appdir, elves, baseline)
        self.assertTrue(any("GLIBCXX_3.4.30" in i for i in issues))
        self.assertTrue(any("baseline" in i for i in issues))

    def test_inspects_baseline_file_not_host(self):
        """The resolved libstdc++ path must be inside baseline_root."""
        with tempfile.TemporaryDirectory() as td:
            appdir, baseline, elves, real = self._setup_baseline_stdcxx(td)
            captured_paths = []

            def mock_readelf(path, *args):
                captured_paths.append(str(path))
                if args == ("-V",):
                    return readelf_defs("3.4.30")
                return ""

            with patch.object(lib, "run_readelf", side_effect=mock_readelf):
                lib.find_libstdcxx(appdir, baseline_root=baseline)
        if captured_paths:
            self.assertIn(str(baseline), captured_paths[0])


if __name__ == "__main__":
    unittest.main()