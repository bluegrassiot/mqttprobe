"""Integration tests: detection, offset enumeration, container, valid layout, wrong helpers.

Run:
  python -m pytest scripts/packaging/linux/tests/verifier/test_verify_integration.py -v
"""

import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from _fixtures import lib, mod, sqfs_data, readelf_needs, readelf_needed, make_elf


class TestDetectInputType(unittest.TestCase):
    def test_type2(self):
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "a.AppImage"
            p.write_bytes(b"\x7fELF" + b"\x00" * 4 + b"AI\x02" + b"\x00" * 10)
            self.assertEqual(mod.detect_input_type(p), "appimage-type2")

    def test_plain_elf(self):
        with tempfile.TemporaryDirectory() as td:
            self.assertEqual(mod.detect_input_type(make_elf(Path(td) / "bin")), "appimage-elf")

    def test_unknown(self):
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "f.txt"
            p.write_text("hi")
            self.assertEqual(mod.detect_input_type(p), "appimage-unknown")


class TestSquashfsExtractionRegression(unittest.TestCase):
    """Regression: v1.0.5 false magic at ~194183, real at ~944632."""

    def test_false_early_magic_skipped_real_used(self):
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "img"
            # False magic at 1000 with invalid superblock (bad version).
            data = bytearray(b"\x00" * 9000)
            false_sb = sqfs_data(magic=0x73717368, inodes=10, block=4096, maj=5, used=100)
            data[1000:1000+96] = false_sb
            # Real magic at 5000 with valid superblock.
            real_sb = sqfs_data(magic=0x73717368, inodes=42, block=4096, maj=4, used=500)
            data[5000:5000+96] = real_sb
            p.write_bytes(bytes(data))
            results = lib.find_squashfs_offsets(p)
        # Only the real one at 5000 should be valid.
        self.assertEqual(len(results), 1)
        self.assertEqual(results[0][0], 5000)

    def test_no_valid_magic_raises_in_extract(self):
        """extract_appimage should raise when no valid squashfs found."""
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "img"
            data = bytearray(b"\x00" * 1000)
            bad_sb = sqfs_data(magic=0x73717368, inodes=0, block=4096, maj=5, used=0)
            data[0:96] = bad_sb
            p.write_bytes(bytes(data))
            with self.assertRaises(ValueError):
                with patch("shutil.which", return_value="/usr/bin/unsquashfs"):
                    mod.extract_appimage(p, Path(td))


@unittest.skipIf(not shutil.which("docker"), "Docker not available")
class TestContainerPermissions(unittest.TestCase):
    """Verify stat-based permission and symlink detection on real Linux."""

    def test_executable_detection_in_container(self):
        script = (
            "import stat, tempfile, os, sys\n"
            "from pathlib import Path\n"
            "td = tempfile.mkdtemp()\n"
            "root = Path(td)\n"
            "f1 = root / 'no_exec.txt'\n"
            "f1.write_text('test')\n"
            "os.chmod(f1, 0o644)\n"
            "f2 = root / 'exec.sh'\n"
            "f2.write_text('#!/bin/sh')\n"
            "os.chmod(f2, 0o755)\n"
            "for p, exp in [(f1, False), (f2, True)]:\n"
            "    x = bool(p.stat().st_mode & (stat.S_IXUSR|stat.S_IXGRP|stat.S_IXOTH))\n"
            "    if x != exp:\n"
            "        print(f'FAIL: {p.name} expected={exp} got={x}', file=sys.stderr)\n"
            "        sys.exit(1)\n"
            "print('OK')\n"
        )
        result = subprocess.run(
            ["docker", "run", "--rm", "python:3.11-slim", "python3", "-c", script],
            capture_output=True, text=True, timeout=30,
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout.strip(), "OK")

    def test_symlink_escape_in_container(self):
        script = (
            "import tempfile\n"
            "from pathlib import Path\n"
            "td = tempfile.mkdtemp()\n"
            "root = Path(td)\n"
            "link = root / 'escape.txt'\n"
            "link.symlink_to('/etc/passwd')\n"
            "resolved = link.resolve()\n"
            "root_resolved = root.resolve()\n"
            "if resolved.is_relative_to(root_resolved):\n"
            "    print('FAIL: escape not detected')\n"
            "else:\n"
            "    print('OK')\n"
        )
        result = subprocess.run(
            ["docker", "run", "--rm", "python:3.11-slim", "python3", "-c", script],
            capture_output=True, text=True, timeout=30,
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout.strip(), "OK")


@unittest.skipIf(not shutil.which("docker"), "Docker not available")
class TestDockerFullSuite(unittest.TestCase):
    """Run the full verifier test suite inside a Linux container."""

    def test_full_suite_in_container(self):
        project_root = Path(__file__).resolve().parents[5]
        result = subprocess.run(
            ["docker", "run", "--rm",
             "-v", f"{project_root}:/project",
             "-w", "/project",
             "python:3.11-slim",
             "sh", "-c",
             "pip install pytest -q 2>/dev/null && "
             "python -m pytest scripts/packaging/linux/tests/verifier/test_verify_lib.py "
             "scripts/packaging/linux/tests/verifier/test_verify_checks.py "
             "scripts/packaging/linux/tests/verifier/test_verify_integration.py "
             "-v --tb=short -k 'not TestDockerFullSuite'"],
            capture_output=True, text=True, timeout=120,
        )
        self.assertEqual(
            result.returncode, 0,
            f"Full suite failed in container:\n"
            f"stdout (last 3000):\n{result.stdout[-3000:]}\n"
            f"stderr (last 1000):\n{result.stderr[-1000:]}"
        )


class TestValidLayout(unittest.TestCase):
    @unittest.skipIf(sys.platform == "win32", "chmod does not set Unix bits on Windows")
    def test_valid_appdir_passes_all_checks(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            ar = root / "AppRun"
            ar.write_text("#!/bin/sh\ncd usr\n")
            ar.chmod(0o755)

            main = root / "usr" / "bin" / "MqttProbe.Desktop"
            main.parent.mkdir(parents=True)
            main.write_bytes(b"\x7fELF" + b"\x00" * 60)
            main.chmod(0o755)

            wk = root / "usr" / "lib" / "x86_64-linux-gnu" / "webkit2gtk-4.1"
            wk.mkdir(parents=True)
            for n in ("WebKitWebProcess", "WebKitNetworkProcess", "WebKitGPUProcess"):
                f = wk / n
                f.write_bytes(b"\x7fELF" + mod.WEBKIT_RELATIVE_PREFIX.encode() + b"\x00" * 60)
                f.chmod(0o755)
            # libwebkit2gtk-4.1.so.0: linuxdeploy moves it to usr/lib/
            lib_dir = root / "usr" / "lib"
            lib_dir.mkdir(parents=True, exist_ok=True)
            (lib_dir / "libwebkit2gtk-4.1.so.0").write_bytes(
                b"\x7fELF" + mod.WEBKIT_RELATIVE_PREFIX.encode() + b"\x00" * 60
            )
            bundle = wk / "injected-bundle"
            bundle.mkdir()
            (bundle / mod.INJECTED_BUNDLE_NAME).write_bytes(b"\x7fELF")

            elf_files = [main, wk / "WebKitWebProcess"]
            baseline = {"libwebkit2gtk-4.1.so.0"}
            baseline_root = Path(td) / "baseline"
            (baseline_root / "usr" / "lib" / "x86_64-linux-gnu").mkdir(parents=True)
            with patch.object(lib, "run_readelf") as mre, \
                 patch.object(mod, "get_rpath_runpath", return_value=(None, None)):
                mre.side_effect = lambda p, *a: (
                    readelf_needs("2.31") if a == ("-V",)
                    else readelf_needed("libwebkit2gtk-4.1.so.0")
                )
                issues = []
                issues.extend(mod.check_glibc_versions(elf_files, (2, 35)))
                issues.extend(mod.check_symlinks(root))
                issues.extend(mod.check_permissions(root))
                issues.extend(mod.check_resources(root))
                issues.extend(mod.check_webkit_paths(root, baseline_root))
                issues.extend(mod.check_dependencies(root, elf_files, baseline))
        self.assertEqual(issues, [], f"Unexpected: {issues}")


class TestWrongHelpers(unittest.TestCase):
    def test_not_executable(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            wk = root / "usr" / "lib" / "x86_64-linux-gnu" / "webkit2gtk-4.1"
            wk.mkdir(parents=True)
            for n in ("WebKitWebProcess", "WebKitNetworkProcess"):
                (wk / n).write_bytes(b"\x7fELF" + b"\x00" * 60)
            issues = mod.check_permissions(root)
        # 2 WebKit not-executable + AppRun missing + Desktop missing = 4
        self.assertEqual(len(issues), 4)
        self.assertTrue(any("WebKitWebProcess" in i and "not executable" in i for i in issues))
        self.assertTrue(any("WebKitNetworkProcess" in i and "not executable" in i for i in issues))


class TestMalformedElf(unittest.TestCase):
    def test_malformed_elf_fails_closed_in_glibc_check(self):
        """ELF with magic but garbage content must fail closed, not silently pass."""
        with tempfile.TemporaryDirectory() as td:
            elf = Path(td) / "garbage.so"
            elf.write_bytes(b"\x7fELF" + b"\xff" * 200)
            issues = mod.check_glibc_versions([elf], (2, 35))
        self.assertTrue(len(issues) > 0, "Malformed ELF should produce issues")
        self.assertTrue(any("failed" in i.lower() for i in issues))

    def test_malformed_elf_fails_closed_in_dependency_check(self):
        """Malformed ELF in dependency check should report TOOL_FAILURE."""
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            lib_dir = root / "usr" / "lib" / "x86_64-linux-gnu"
            lib_dir.mkdir(parents=True)
            elf = Path(td) / "garbage.so"
            elf.write_bytes(b"\x7fELF" + b"\xff" * 200)
            with patch.object(mod, "get_rpath_runpath", return_value=(None, None)):
                with patch.object(lib, "read_needed_libs", return_value=lib.TOOL_FAILURE):
                    issues = mod.check_dependencies(root, [elf], set())
        self.assertTrue(any("readelf -d failed" in i for i in issues))


if __name__ == "__main__":
    unittest.main()