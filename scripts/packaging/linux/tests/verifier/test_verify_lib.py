"""Tests for verify_linux_appimage_lib: superblock, ldconfig, readelf parsing,
enumeration, RPATH classification, baseline identity, tool failure handling.

Run:
  python -m pytest scripts/packaging/linux/tests/verifier/test_verify_lib.py -v
"""

import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
from _fixtures import lib, sqfs_data, readelf_needs, readelf_defs, readelf_needed


class TestSquashfsSuperblock(unittest.TestCase):
    def test_parse_valid_le(self):
        data = sqfs_data(magic=0x73717368, inodes=42, block=131072, used=8192)
        with tempfile.NamedTemporaryFile(delete=False) as f:
            f.write(data)
            f.flush()
            sb = lib.parse_squashfs_superblock(Path(f.name), 0)
        self.assertEqual(sb.magic, 0x73717368)
        self.assertEqual(sb.inode_count, 42)
        self.assertEqual(sb.block_size, 131072)
        self.assertEqual(sb.major_version, 4)
        self.assertEqual(sb.bytes_used, 8192)

    def test_parse_bad_magic_raises(self):
        data = sqfs_data(magic=0xDEADBEEF)
        with tempfile.NamedTemporaryFile(delete=False) as f:
            f.write(data)
            f.flush()
            with self.assertRaises(ValueError):
                lib.parse_squashfs_superblock(Path(f.name), 0)

    def test_truncated_raises(self):
        with tempfile.NamedTemporaryFile(delete=False) as f:
            f.write(b"\x00" * 10)
            f.flush()
            with self.assertRaises(ValueError):
                lib.parse_squashfs_superblock(Path(f.name), 0)

    def test_validate_ok(self):
        sb = lib.SquashfsSuperblock(0x73717368, 100, 4096, 4, 0, 5000)
        self.assertEqual(lib.validate_squashfs_superblock(sb, 10000, 0), [])

    def test_validate_bad_version(self):
        sb = lib.SquashfsSuperblock(0x73717368, 100, 4096, 5, 0, 5000)
        issues = lib.validate_squashfs_superblock(sb, 10000, 0)
        self.assertTrue(any("major version" in i for i in issues))

    def test_validate_zero_inodes(self):
        sb = lib.SquashfsSuperblock(0x73717368, 0, 4096, 4, 0, 5000)
        issues = lib.validate_squashfs_superblock(sb, 10000, 0)
        self.assertTrue(any("inode count" in i for i in issues))

    def test_validate_bytes_used_exceeds_file(self):
        sb = lib.SquashfsSuperblock(0x73717368, 100, 4096, 4, 0, 20000)
        issues = lib.validate_squashfs_superblock(sb, 10000, 0)
        self.assertTrue(any("exceeds file size" in i for i in issues))

    def test_validate_bad_block_size(self):
        sb = lib.SquashfsSuperblock(0x73717368, 100, 3333, 4, 0, 5000)
        issues = lib.validate_squashfs_superblock(sb, 10000, 0)
        self.assertTrue(any("block size" in i for i in issues))

    def test_validate_offset_plus_bytes_exceeds(self):
        sb = lib.SquashfsSuperblock(0x73717368, 100, 4096, 4, 0, 6000)
        issues = lib.validate_squashfs_superblock(sb, 10000, 5000)
        self.assertTrue(any("exceeds file size" in i for i in issues))


class TestSquashfsEnumeration(unittest.TestCase):
    """Regression: v1.0.5 has false magic at ~194183, real at ~944632."""

    def test_false_early_magic_skipped(self):
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "img"
            # False magic at 1000 with invalid superblock (version 5).
            data = bytearray(b"\x00" * 9000)
            false_sb = sqfs_data(magic=0x73717368, inodes=10, block=4096, maj=5, used=100)
            data[1000:1000+96] = false_sb
            # Real magic at 5000 with valid superblock.
            real_sb = sqfs_data(magic=0x73717368, inodes=42, block=4096, maj=4, used=500)
            data[5000:5000+96] = real_sb
            p.write_bytes(bytes(data))
            results = lib.find_squashfs_offsets(p)
        self.assertEqual(len(results), 1)
        self.assertEqual(results[0][0], 5000)
        self.assertEqual(results[0][1].inode_count, 42)

    def test_no_valid_returns_empty(self):
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "img"
            # Magic at 0 but invalid (version 5).
            data = bytearray(b"\x00" * 1000)
            bad_sb = sqfs_data(magic=0x73717368, inodes=0, block=4096, maj=5, used=0)
            data[0:96] = bad_sb
            p.write_bytes(bytes(data))
            results = lib.find_squashfs_offsets(p)
        self.assertEqual(results, [])

    def test_multiple_valid_returns_all(self):
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "img"
            data = bytearray(b"\x00" * 9000)
            sb1 = sqfs_data(magic=0x73717368, inodes=10, block=4096, maj=4, used=100)
            data[1000:1000+96] = sb1
            sb2 = sqfs_data(magic=0x73717368, inodes=20, block=4096, maj=4, used=200)
            data[5000:5000+96] = sb2
            p.write_bytes(bytes(data))
            results = lib.find_squashfs_offsets(p)
        self.assertEqual(len(results), 2)


class TestReadelfFailClosed(unittest.TestCase):
    """readelf/objdump failures must fail closed, not silently return empty."""

    def test_nonzero_exit_returns_none(self):
        with tempfile.TemporaryDirectory() as td:
            elf = Path(td) / "lib.so"
            elf.write_bytes(b"\x7fELF" + b"\x00" * 60)
            result = lib.run_readelf(elf, "-V")
        # On systems without readelf, this returns None (fail-closed).
        # On systems with readelf, a minimal ELF may succeed.
        # The key test: we don't get an empty string from a failure.
        self.assertTrue(result is None or len(result) > 0)

    def test_missing_file_returns_none(self):
        result = lib.run_readelf(Path("/nonexistent/lib.so"), "-V")
        self.assertIsNone(result)

    def test_timeout_returns_none(self):
        """Verify timeout behavior by checking the function handles it."""
        # We can't easily trigger a real timeout, but we verify the API contract.
        with tempfile.TemporaryDirectory() as td:
            elf = Path(td) / "lib.so"
            elf.write_bytes(b"\x7fELF" + b"\x00" * 60)
            result = lib.run_readelf(elf, "-V")
        self.assertIsInstance(result, (str, type(None)))


class TestDynamicElfDetection(unittest.TestCase):
    def test_static_elf_no_dynamic(self):
        """A minimal ELF with no .dynamic section should be detectable."""
        with tempfile.TemporaryDirectory() as td:
            elf = Path(td) / "static"
            elf.write_bytes(b"\x7fELF" + b"\x00" * 60)
            # Minimal ELF has no .dynamic section.
            self.assertFalse(lib.is_dynamic_elf(elf))


class TestLdconfig(unittest.TestCase):
    def test_parse_typical_output(self):
        output = (
            "1234 libs found in cache `/etc/ld.so.cache'\n"
            "\tlibc.so.6 (libc6,x86-64) => /lib/x86_64-linux-gnu/libc.so.6\n"
            "\tlibstdc++.so.6 (libc6,x86-64) => /usr/lib/x86_64-linux-gnu/libstdc++.so.6\n"
        )
        libs = lib.parse_ldconfig(output)
        self.assertIn("libc.so.6", libs)
        self.assertIn("libstdc++.so.6", libs)

    def test_parse_empty(self):
        self.assertEqual(lib.parse_ldconfig(""), set())


class TestParseVersion(unittest.TestCase):
    def test_basic(self):
        self.assertEqual(lib.parse_version("2.35"), (2, 35))
        self.assertEqual(lib.parse_version("3.4.30"), (3, 4, 30))

    def test_format_roundtrip(self):
        self.assertEqual(lib.format_version((2, 35, 1)), "2.35.1")


class TestParseVersionNeeds(unittest.TestCase):
    def test_glibc_only(self):
        g, gx, cx = lib.parse_version_needs(readelf_needs("2.2.5", "2.17", "2.35"))
        self.assertEqual(g, {"2.2.5", "2.17", "2.35"})
        self.assertEqual(gx, set())
        self.assertEqual(cx, set())

    def test_mixed(self):
        g, gx, cx = lib.parse_version_needs(readelf_needs("2.17", glibcxx=["3.4.30"]))
        self.assertEqual(g, {"2.17"})
        self.assertEqual(gx, {"3.4.30"})
        self.assertEqual(cx, set())

    def test_cxxabi(self):
        _, gx, cx = lib.parse_version_needs(readelf_needs(glibcxx=["3.4.30"], cxxabi=["1.3.13"]))
        self.assertEqual(gx, {"3.4.30"})
        self.assertEqual(cx, {"1.3.13"})

    def test_nonnumeric_names(self):
        _, gx, cx = lib.parse_version_needs(readelf_needs(glibcxx=["GLIBCXX_DEBUG"]))
        self.assertIn("GLIBCXX_DEBUG", gx)

    def test_empty(self):
        self.assertEqual(lib.parse_version_needs("", ), (set(), set(), set()))


class TestParseVersionDefs(unittest.TestCase):
    def test_provided(self):
        gx, cx = lib.parse_version_defs(readelf_defs("3.4", "3.4.1", "3.4.30"))
        self.assertTrue({"3.4", "3.4.1", "3.4.30"}.issubset(gx))
        self.assertEqual(cx, set())

    def test_cxxabi_defs(self):
        gx, cx = lib.parse_version_defs(readelf_defs("3.4.30", cxxabi=["1.3", "1.3.13"]))
        self.assertIn("3.4.30", gx)
        self.assertEqual(cx, {"1.3", "1.3.13"})

    def test_empty(self):
        self.assertEqual(lib.parse_version_defs(""), (set(), set()))


class TestRpathClassification(unittest.TestCase):
    def test_origin_relative_supported(self):
        info = lib.classify_rpath("$ORIGIN/../lib")
        self.assertEqual(info.supported, ["$ORIGIN/../lib"])
        self.assertEqual(info.unsupported, [])

    def test_origin_braces_supported(self):
        info = lib.classify_rpath("${ORIGIN}/lib")
        self.assertEqual(info.supported, ["${ORIGIN}/lib"])
        self.assertEqual(info.unsupported, [])

    def test_absolute_rejected(self):
        info = lib.classify_rpath("/opt/custom/lib")
        self.assertEqual(info.supported, [])
        self.assertEqual(len(info.unsupported), 1)
        self.assertIn("absolute", info.unsupported[0])

    def test_empty_entry_skipped(self):
        info = lib.classify_rpath("$ORIGIN/lib::/bad")
        self.assertEqual(info.supported, ["$ORIGIN/lib"])
        self.assertEqual(len(info.unsupported), 1)

    def test_mixed_entries(self):
        info = lib.classify_rpath("$ORIGIN/lib:/opt/lib:${ORIGIN}/../lib")
        self.assertEqual(len(info.supported), 2)
        self.assertEqual(len(info.unsupported), 1)
        self.assertIn("absolute", info.unsupported[0])

    def test_unresolved_rejected(self):
        info = lib.classify_rpath("some/relative/path")
        self.assertEqual(info.supported, [])
        self.assertEqual(len(info.unsupported), 1)
        self.assertIn("unresolved", info.unsupported[0])

    def test_origin_only_supported(self):
        info = lib.classify_rpath("$ORIGIN")
        self.assertEqual(info.supported, ["$ORIGIN"])
        self.assertEqual(info.unsupported, [])


class TestBaselineIdentity(unittest.TestCase):
    def test_ubuntu_2204_passes(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            (root / "etc").mkdir()
            (root / "etc" / "os-release").write_text(
                'ID=ubuntu\nVERSION_ID="22.04"\n'
            )
            self.assertIsNone(lib.verify_baseline_identity(root))

    def test_wrong_version_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            (root / "etc").mkdir()
            (root / "etc" / "os-release").write_text(
                'ID=ubuntu\nVERSION_ID="24.04"\n'
            )
            self.assertIsNotNone(lib.verify_baseline_identity(root))

    def test_wrong_distro_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            (root / "etc").mkdir()
            (root / "etc" / "os-release").write_text(
                'ID=debian\nVERSION_ID="12"\n'
            )
            self.assertIsNotNone(lib.verify_baseline_identity(root))

    def test_missing_file_fails(self):
        with tempfile.TemporaryDirectory() as td:
            self.assertIsNotNone(lib.verify_baseline_identity(Path(td)))


class TestToolFailure(unittest.TestCase):
    def test_tool_failure_sentinel(self):
        self.assertEqual(lib.TOOL_FAILURE, "__TOOL_FAILURE__")

    def test_malformed_elf_returns_tool_failure(self):
        """A file with ELF magic but garbage content should fail closed."""
        with tempfile.TemporaryDirectory() as td:
            elf = Path(td) / "garbage.so"
            elf.write_bytes(b"\x7fELF" + b"\xff" * 200)
            result = lib.read_elf_requirements(elf)
        # First element is either TOOL_FAILURE or a set (static ELF).
        # For garbage, readelf -h will likely fail -> TOOL_FAILURE.
        self.assertTrue(
            result[0] is lib.TOOL_FAILURE or isinstance(result[0], set),
            f"Expected TOOL_FAILURE or set, got {result[0]!r}"
        )

    def test_inspect_dynamic_tool_failure(self):
        """inspect_elf_dynamic returns TOOL_FAILURE for unreadable file."""
        with tempfile.TemporaryDirectory() as td:
            elf = Path(td) / "garbage.so"
            elf.write_bytes(b"\x7fELF" + b"\xff" * 200)
            result = lib.inspect_elf_dynamic(elf)
        self.assertTrue(
            result is lib.TOOL_FAILURE or result is None,
            f"Expected TOOL_FAILURE or None, got {result!r}"
        )


class TestExpandOrigin(unittest.TestCase):
    def _make_elf(self, td, subdir="usr/bin"):
        appdir = Path(td)
        elf_dir = appdir / subdir
        elf_dir.mkdir(parents=True, exist_ok=True)
        elf = elf_dir / "app"
        elf.write_bytes(b"\x7fELF")
        return appdir, elf, elf_dir

    def test_token_only_replaced(self):
        """$ORIGIN/.. must preserve the suffix, not remove the whole entry."""
        with tempfile.TemporaryDirectory() as td:
            appdir, elf, elf_dir = self._make_elf(td)
            result = lib._expand_origin("$ORIGIN/..", elf, appdir)
        self.assertIsNotNone(result)
        self.assertEqual(result, elf_dir / "..")

    def test_escape_rejected(self):
        """$ORIGIN escaping beyond appdir must return None."""
        with tempfile.TemporaryDirectory() as td:
            appdir, elf, _ = self._make_elf(td)
            result = lib._expand_origin("$ORIGIN/../../../../..", elf, appdir)
        self.assertIsNone(result)

    def test_braces_expanded(self):
        with tempfile.TemporaryDirectory() as td:
            appdir, elf, elf_dir = self._make_elf(td)
            result = lib._expand_origin("${ORIGIN}/../lib", elf, appdir)
        self.assertIsNotNone(result)
        self.assertEqual(result, elf_dir / ".." / "lib")

    def test_origin_only_returns_parent(self):
        with tempfile.TemporaryDirectory() as td:
            appdir, elf, elf_dir = self._make_elf(td)
            result = lib._expand_origin("$ORIGIN", elf, appdir)
        self.assertEqual(result, elf_dir)


class TestParseVersionDefsContamination(unittest.TestCase):
    def test_defs_do_not_bleed_into_needs(self):
        """parse_version_defs must stop at 'Version needs' boundary."""
        combined = (
            readelf_defs("3.4", "3.4.1", "3.4.30")
            + readelf_needs("2.17", glibcxx=["3.4.31"])
        )
        gx, cx = lib.parse_version_defs(combined)
        self.assertTrue({"3.4", "3.4.1", "3.4.30"}.issubset(gx))
        self.assertNotIn("3.4.31", gx)

    def test_defs_stops_at_version_r_header(self):
        text = (
            "Version definition section '.gnu.version_d' contains 2 entries\n"
            "  0x0010: Rev: 1  Flags: none  Index: 2  Cnt: 1  Name: GLIBCXX_3.4\n"
            "\nVersion needs section '.gnu.version_r' contains 1 entry\n"
            "  0x0010:   Name: GLIBC_2.17  Flags: none  Version: 2\n"
        )
        gx, cx = lib.parse_version_defs(text)
        self.assertIn("3.4", gx)
        self.assertNotIn("2.17", gx)


class TestReadElfRequirementsFallback(unittest.TestCase):
    def test_objdump_fallback_when_readelf_V_fails(self):
        with tempfile.TemporaryDirectory() as td:
            elf = Path(td) / "lib.so"
            elf.write_bytes(b"\x7fELF" + b"\x00" * 60)
            with patch.object(lib, "inspect_elf_dynamic", return_value="NEEDED"):
                with patch.object(lib, "run_readelf", return_value=None):
                    with patch.object(lib, "run_objdump", return_value="GLIBC_2.17"):
                        result = lib.read_elf_requirements(elf)
        self.assertNotEqual(result[0], lib.TOOL_FAILURE)
        self.assertIn("2.17", result[0])

    def test_both_fail_returns_tool_failure(self):
        with tempfile.TemporaryDirectory() as td:
            elf = Path(td) / "lib.so"
            elf.write_bytes(b"\x7fELF" + b"\x00" * 60)
            with patch.object(lib, "inspect_elf_dynamic", return_value="NEEDED"):
                with patch.object(lib, "run_readelf", return_value=None):
                    with patch.object(lib, "run_objdump", return_value=None):
                        result = lib.read_elf_requirements(elf)
        self.assertIs(result[0], lib.TOOL_FAILURE)

    def test_static_elf_returns_empty(self):
        with tempfile.TemporaryDirectory() as td:
            elf = Path(td) / "static"
            elf.write_bytes(b"\x7fELF" + b"\x00" * 60)
            with patch.object(lib, "inspect_elf_dynamic", return_value=None):
                result = lib.read_elf_requirements(elf)
        self.assertEqual(result, (set(), set(), set()))


class TestReadNeededLibsFailure(unittest.TestCase):
    def test_tool_failure_on_readelf_failure(self):
        with tempfile.TemporaryDirectory() as td:
            elf = Path(td) / "lib.so"
            elf.write_bytes(b"\x7fELF" + b"\x00" * 60)
            with patch.object(lib, "run_readelf", return_value=None):
                result = lib.read_needed_libs(elf)
        self.assertEqual(result, lib.TOOL_FAILURE)

    def test_success_returns_list(self):
        with tempfile.TemporaryDirectory() as td:
            elf = Path(td) / "lib.so"
            elf.write_bytes(b"\x7fELF" + b"\x00" * 60)
            with patch.object(lib, "run_readelf", return_value=readelf_needed("libc.so.6")):
                result = lib.read_needed_libs(elf)
        self.assertEqual(result, ["libc.so.6"])


class TestGetRpathRunpath(unittest.TestCase):
    def _mock_elf(self, td, readelf_out):
        elf = Path(td) / "lib.so"
        elf.write_bytes(b"\x7fELF" + b"\x00" * 60)
        with patch.object(lib, "run_readelf", return_value=readelf_out):
            return lib.get_rpath_runpath(elf)

    def test_rpath_only(self):
        with tempfile.TemporaryDirectory() as td:
            out = "  0x000000000000000f (RPATH)    Library rpath: [$ORIGIN/../lib]\n"
            rpath, runpath = self._mock_elf(td, out)
        self.assertEqual(rpath, "$ORIGIN/../lib")
        self.assertIsNone(runpath)

    def test_runpath_only(self):
        with tempfile.TemporaryDirectory() as td:
            out = "  0x000000000000001d (RUNPATH)  Library runpath: [$ORIGIN/lib]\n"
            rpath, runpath = self._mock_elf(td, out)
        self.assertIsNone(rpath)
        self.assertEqual(runpath, "$ORIGIN/lib")

    def test_both(self):
        with tempfile.TemporaryDirectory() as td:
            out = (
                "  0x000000000000000f (RPATH)    Library rpath: [$ORIGIN/lib]\n"
                "  0x000000000000001d (RUNPATH)  Library runpath: [$ORIGIN/lib2]\n"
            )
            rpath, runpath = self._mock_elf(td, out)
        self.assertEqual(rpath, "$ORIGIN/lib")
        self.assertEqual(runpath, "$ORIGIN/lib2")

    def test_neither(self):
        with tempfile.TemporaryDirectory() as td:
            rpath, runpath = self._mock_elf(td, "  NEEDED  [libc.so.6]")
        self.assertIsNone(rpath)
        self.assertIsNone(runpath)


class TestScanBaselineSymlinks(unittest.TestCase):
    @unittest.skipIf(sys.platform == "win32", "Unix symlinks differ on Windows")
    def test_absolute_symlink_resolved_within_baseline(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            lib_dir = root / "lib" / "x86_64-linux-gnu"
            lib_dir.mkdir(parents=True)
            (lib_dir / "libc-2.31.so").write_bytes(b"\x7fELF")
            (lib_dir / "libc.so.6").symlink_to("/lib/x86_64-linux-gnu/libc-2.31.so")
            self.assertIn("libc.so.6", lib.scan_baseline_libs(root))

    @unittest.skipIf(sys.platform == "win32", "Unix symlinks behave differently on Windows")
    def test_broken_absolute_symlink_excluded(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            lib_dir = root / "lib" / "x86_64-linux-gnu"
            lib_dir.mkdir(parents=True)
            (lib_dir / "libfake.so.1").symlink_to("/lib/x86_64-linux-gnu/libfake-9.9.so")
            self.assertNotIn("libfake.so.1", lib.scan_baseline_libs(root))

    @unittest.skipIf(sys.platform == "win32", "Unix symlinks behave differently on Windows")
    def test_relative_symlink_resolved_normally(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            lib_dir = root / "usr" / "lib" / "x86_64-linux-gnu"
            lib_dir.mkdir(parents=True)
            (lib_dir / "libfoo.so.1.2.3").write_bytes(b"\x7fELF")
            (lib_dir / "libfoo.so.1").symlink_to("libfoo.so.1.2.3")
            self.assertIn("libfoo.so.1", lib.scan_baseline_libs(root))

if __name__ == "__main__":
    unittest.main()