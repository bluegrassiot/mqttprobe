"""Tests for manifest ownership: exact precedence, collisions, resources,
alias dedup, nonELF rejection, arch metadata, epochs, and .NET precedence.

Focused on _query_dpkg_owner, _query_dpkg_owner_with_relocation,
_system_path_candidates, and generate_manifest ownership logic.
"""

import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import MagicMock, patch

REPO_ROOT = Path(__file__).resolve().parents[5]
LINUX_DIR = REPO_ROOT / "scripts" / "packaging" / "linux"

if str(LINUX_DIR) not in sys.path:
    sys.path.insert(0, str(LINUX_DIR))

from manifest import (
    BundledFile,
    PackageManifest,
    SystemPackage,
    _classify_file,
    _is_dotnet_runtime_file,
    _query_dpkg_owner,
    _query_dpkg_owner_with_relocation,
    _system_path_candidates,
    generate_manifest,
)


# -- Exact precedence: exact path wins over fallback ---------------------------

class TestExactPrecedence(unittest.TestCase):
    """Exact system path must be tried before any fallback."""

    @patch("manifest.subprocess.run")
    def test_exact_path_tried_first(self, mock_run):
        mock_run.side_effect = [
            MagicMock(returncode=0, stdout="libgtk-3-0:amd64: /usr/lib/x86_64-linux-gnu/libgtk-3.so.0\n"),
            MagicMock(returncode=0, stdout="3.24.33-1ubuntu2\tgtk+3.0\t3.24.33-1ubuntu2\n"),
        ]
        result = _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libgtk-3.so.0")
        self.assertIsNotNone(result)
        # First call must be the exact path query
        first_args = mock_run.call_args_list[0][0][0]
        self.assertEqual(first_args, ["dpkg-query", "-S", "/usr/lib/x86_64-linux-gnu/libgtk-3.so.0"])

    @patch("manifest.subprocess.run")
    def test_different_path_not_accepted(self, mock_run):
        """dpkg returning a different path must not be accepted as ownership."""
        mock_run.return_value = MagicMock(
            returncode=0,
            stdout="libpulse0:amd64: /usr/lib/x86_64-linux-gnu/pulseaudio/libpulsecommon-15.99.so\n",
        )
        result = _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libpulsecommon-15.99.so")
        self.assertIsNone(result)

    def test_candidates_preserved_multiarch_has_lib_counterpart(self):
        """Preserved multiarch paths must include /lib counterpart."""
        candidates = _system_path_candidates(
            "usr/lib/x86_64-linux-gnu/webkit2gtk-4.1/libwebkit2gtk-4.1.so.0"
        )
        self.assertEqual(candidates, [
            "/usr/lib/x86_64-linux-gnu/webkit2gtk-4.1/libwebkit2gtk-4.1.so.0",
            "/lib/x86_64-linux-gnu/webkit2gtk-4.1/libwebkit2gtk-4.1.so.0",
        ])

    def test_candidates_collapsed_has_all_four(self):
        """Collapsed lib paths must try all four variants."""
        candidates = _system_path_candidates("usr/lib/libfoo.so.1")
        self.assertEqual(candidates, [
            "/usr/lib/x86_64-linux-gnu/libfoo.so.1",
            "/lib/x86_64-linux-gnu/libfoo.so.1",
            "/usr/lib/libfoo.so.1",
            "/lib/libfoo.so.1",
        ])


# -- Collisions: managed/resource same-basename elsewhere ----------------------

class TestCollisionsNonclaim(unittest.TestCase):
    """Same basename in different packages must not create false ownership."""

    @patch("manifest.subprocess.run")
    def test_ambiguous_ownership_raises(self, mock_run):
        """Two different packages owning same path must raise ValueError."""
        mock_run.return_value = MagicMock(
            returncode=0,
            stdout=(
                "pkg-a:amd64: /usr/lib/x86_64-linux-gnu/libfoo.so\n"
                "pkg-b:amd64: /usr/lib/x86_64-linux-gnu/libfoo.so\n"
            ),
        )
        with self.assertRaises(ValueError) as ctx:
            _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libfoo.so")
        self.assertIn("Ambiguous", str(ctx.exception))

    @patch("manifest.subprocess.run")
    def test_same_package_multiline_not_ambiguous(self, mock_run):
        """Multiple lines from same package are not ambiguous."""
        mock_run.side_effect = [
            MagicMock(
                returncode=0,
                stdout=(
                    "libgtk-3-0:amd64: /usr/lib/x86_64-linux-gnu/libgtk-3.so.0\n"
                    "libgtk-3-0:amd64: /usr/lib/x86_64-linux-gnu/libgtk-3.so.0.0\n"
                ),
            ),
            MagicMock(returncode=0, stdout="3.24.33-1ubuntu2\tgtk+3.0\t3.24.33-1ubuntu2\n"),
        ]
        result = _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libgtk-3.so.0")
        # Only exact path match counts; second line has different path
        # So owners has one entry -> not ambiguous -> returns result
        self.assertIsNotNone(result)
        self.assertEqual(result.binary_package, "libgtk-3-0")

    @patch("manifest.subprocess.run")
    def test_different_path_lines_ignored(self, mock_run):
        """Lines with different returned paths are ignored."""
        mock_run.return_value = MagicMock(
            returncode=0,
            stdout=(
                "libgtk-3-0:amd64: /usr/lib/x86_64-linux-gnu/libgtk-3.so.0\n"
                "libother: /usr/lib/x86_64-linux-gnu/libother.so\n"
            ),
        )
        result = _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libfoo.so")
        self.assertIsNone(result)


# -- Resources: fonts/schema/typelib retain exact ownership --------------------

class TestResourceOwnership(unittest.TestCase):
    """Resource files (fonts, schemas, typelibs) must retain exact ownership."""

    @patch("manifest.subprocess.run")
    def test_font_file_owned(self, mock_run):
        mock_run.side_effect = [
            MagicMock(returncode=0, stdout="fonts-dejavu-core: /usr/share/fonts/truetype/dejavu/DejaVuSans.ttf\n"),
            MagicMock(returncode=0, stdout="2.37-2build1\tfonts-dejavu\t2.37-2build1\n"),
        ]
        result = _query_dpkg_owner("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf")
        self.assertIsNotNone(result)
        self.assertEqual(result.binary_package, "fonts-dejavu-core")

    @patch("manifest.subprocess.run")
    def test_schema_file_owned(self, mock_run):
        mock_run.side_effect = [
            MagicMock(returncode=0, stdout="libgtk-3-0:amd64: /usr/lib/x86_64-linux-gnu/glib-2.0/schemas/gschemas.compiled\n"),
            MagicMock(returncode=0, stdout="3.24.33-1ubuntu2\tgtk+3.0\t3.24.33-1ubuntu2\n"),
        ]
        result = _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/glib-2.0/schemas/gschemas.compiled")
        self.assertIsNotNone(result)
        self.assertEqual(result.binary_package, "libgtk-3-0")

    def test_classify_file_font(self):
        with tempfile.TemporaryDirectory() as td:
            appdir = Path(td)
            font_dir = appdir / "usr" / "share" / "fonts"
            font_dir.mkdir(parents=True)
            (font_dir / "test.ttf").write_bytes(b"\x00" * 10)
            self.assertEqual(_classify_file("usr/share/fonts/test.ttf", appdir), "font")

    def test_classify_file_config_schema(self):
        with tempfile.TemporaryDirectory() as td:
            appdir = Path(td)
            schema_dir = appdir / "usr" / "lib" / "schemas"
            schema_dir.mkdir(parents=True)
            (schema_dir / "test.gschema.compiled").write_bytes(b"\x00" * 10)
            self.assertEqual(
                _classify_file("usr/lib/schemas/test.gschema.compiled", appdir),
                "config",
            )

    @patch("manifest._query_dpkg_owner")
    def test_staged_nonelf_so_not_claimed_by_host_elf(self, mock_owner):
        """A staged non-ELF .so resource cannot be claimed via relocation.

        The staged file carries .so in its name but is not an ELF, so it is
        classified as a plain resource. A same-named host ELF must not take
        ownership of it even though the relocation search would find that
        file. Exact lookup still runs for the resource (all types), but it
        misses here, so the file ends up attributed to nobody.
        """
        host_pkg = SystemPackage(
            binary_package="libhostonly",
            binary_version="1.0",
            source_package="hostonly",
            source_version="1.0",
            license_hint="",
            bundled_files=[],
        )
        with tempfile.TemporaryDirectory() as td:
            appdir = Path(td)
            lib_dir = appdir / "usr" / "lib"
            lib_dir.mkdir(parents=True)
            # Staged resource: .so name, but plain text, no ELF magic.
            (lib_dir / "libnotreally.so").write_text("not an elf payload")

            # Host has a real native ELF of the same name; if relocation ran,
            # it would find this and attribute the resource to the host.
            host_dir = Path(td) / "host" / "x86_64-linux-gnu"
            host_dir.mkdir(parents=True)
            (host_dir / "libnotreally.so").write_bytes(b"\x7fELF" + b"\x00" * 32)

            def owner_side_effect(path):
                # Exact staged candidates miss; the host alias would match.
                if "libnotreally.so" in str(path) and "host" in str(path):
                    return host_pkg
                return None

            mock_owner.side_effect = owner_side_effect

            with patch("manifest.os.walk") as mock_walk:
                mock_walk.return_value = [
                    (str(host_dir), [], ["libnotreally.so"]),
                ]
                manifest = generate_manifest(appdir, "2026-10-04")

            # The host ELF must never appear as an owner.
            owners = {p.binary_package for p in manifest.build_system_packages}
            self.assertNotIn("libhostonly", owners)
            # The resource is attributed to nobody and is not flagged unknown
            # (it is not an ELF, so it is not an unclaimed native library).
            claimed = [
                f for p in manifest.build_system_packages for f in p.bundled_files
            ]
            self.assertNotIn("usr/lib/libnotreally.so", claimed)
            self.assertNotIn("usr/lib/libnotreally.so", manifest.unknown_files)
            # Relocation must not have been attempted at all.
            mock_walk.assert_not_called()


# -- Alias dedup: usrmerge same real file, reject distinct candidates ----------

class TestAliasDedup(unittest.TestCase):
    """usrmerge aliases must normalize to same identity."""

    def test_usr_lib_and_lib_are_candidates(self):
        """Both /usr/lib and /lib variants must be candidates."""
        candidates = _system_path_candidates("usr/lib/libfoo.so.1")
        self.assertIn("/usr/lib/libfoo.so.1", candidates)
        self.assertIn("/lib/libfoo.so.1", candidates)

    @patch("manifest.subprocess.run")
    def test_first_matching_candidate_wins(self, mock_run):
        """First candidate that matches wins; later candidates not tried."""
        mock_run.side_effect = [
            MagicMock(returncode=0, stdout="libfoo:amd64: /usr/lib/x86_64-linux-gnu/libfoo.so.1\n"),
            MagicMock(returncode=0, stdout="1.0\tfoo\t1.0\n"),
        ]
        result = _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libfoo.so.1")
        self.assertIsNotNone(result)
        # Only one -S call, not four
        self.assertEqual(mock_run.call_count, 2)  # -S + -W


# -- Stage nonELF reject -------------------------------------------------------

class TestNonElfReject(unittest.TestCase):
    """Non-ELF staged files must not get relocation fallback."""

    @patch("manifest._query_dpkg_owner")
    def test_non_so_no_relocation(self, mock_owner):
        """Non-.so files don't get relocation fallback."""
        mock_owner.return_value = None
        result = _query_dpkg_owner_with_relocation("usr/lib/libfoo.txt")
        self.assertIsNone(result)

    @patch("manifest._query_dpkg_owner")
    def test_non_usr_lib_no_relocation(self, mock_owner):
        """Non-usr/lib paths don't get relocation fallback."""
        mock_owner.return_value = None
        result = _query_dpkg_owner_with_relocation("usr/bin/libfoo.so")
        self.assertIsNone(result)
        mock_owner.assert_called_once()

    @patch("manifest._query_dpkg_owner")
    def test_no_host_byte_sniffing_in_relocation(self, mock_owner):
        """Relocation trusts dpkg ownership, not host file bytes.

        Eligibility is decided from the staged file's own classification, so
        the resolver deliberately does not re-read host bytes. Ownership of
        whatever alias dpkg reports decides the result.
        """
        pkg = SystemPackage(
            binary_package="libpulse0",
            binary_version="15.99.1-0ubuntu3",
            source_package="pulseaudio",
            source_version="15.99.1-0ubuntu3",
            license_hint="",
            bundled_files=[],
        )
        mock_owner.side_effect = [None, None, None, None, pkg]
        with tempfile.TemporaryDirectory() as td:
            search_dir = Path(td) / "usr" / "lib" / "x86_64-linux-gnu" / "pulseaudio"
            search_dir.mkdir(parents=True)
            # Host file is deliberately not an ELF; dpkg still owns the path.
            (search_dir / "libpulsecommon-15.99.so").write_text("not elf content")
            with patch("manifest.os.walk") as mock_walk:
                mock_walk.side_effect = [
                    [(str(search_dir), [], ["libpulsecommon-15.99.so"])],
                    [],
                ]
                result = _query_dpkg_owner_with_relocation("usr/lib/libpulsecommon-15.99.so")
        self.assertIsNotNone(result)
        self.assertEqual(result.binary_package, "libpulse0")


# -- Arch metadata + failed query raises --------------------------------------

class TestArchMetadata(unittest.TestCase):
    """Arch-qualified metadata query must succeed; failures raise."""

    @patch("manifest.subprocess.run")
    def test_arch_qualified_name_used(self, mock_run):
        """Metadata query must use arch-qualified package name."""
        mock_run.side_effect = [
            MagicMock(returncode=0, stdout="libgtk-3-0:amd64: /usr/lib/x86_64-linux-gnu/libgtk-3.so.0\n"),
            MagicMock(returncode=0, stdout="3.24.33-1ubuntu2\tgtk+3.0\t3.24.33-1ubuntu2\n"),
        ]
        _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libgtk-3.so.0")
        # Second call must use arch-qualified name
        meta_args = mock_run.call_args_list[1][0][0]
        self.assertIn("libgtk-3-0:amd64", meta_args)

    @patch("manifest.subprocess.run")
    def test_metadata_returncode_nonzero_raises(self, mock_run):
        """Non-zero returncode from metadata query must raise."""
        mock_run.side_effect = [
            MagicMock(returncode=0, stdout="libfoo:amd64: /usr/lib/x86_64-linux-gnu/libfoo.so\n"),
            MagicMock(returncode=1, stdout="", stderr="package not found"),
        ]
        with self.assertRaises(ValueError) as ctx:
            _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libfoo.so")
        self.assertIn("metadata failed", str(ctx.exception))

    @patch("manifest.subprocess.run")
    def test_metadata_empty_version_raises(self, mock_run):
        """Empty Version field must raise."""
        mock_run.side_effect = [
            MagicMock(returncode=0, stdout="libfoo:amd64: /usr/lib/x86_64-linux-gnu/libfoo.so\n"),
            MagicMock(returncode=0, stdout="\tfoo-src\t1.0\n"),  # empty bin_ver
        ]
        with self.assertRaises(ValueError) as ctx:
            _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libfoo.so")
        self.assertIn("empty metadata", str(ctx.exception))

    @patch("manifest.subprocess.run")
    def test_metadata_empty_source_package_raises(self, mock_run):
        """Empty source:Package field must raise."""
        mock_run.side_effect = [
            MagicMock(returncode=0, stdout="libfoo:amd64: /usr/lib/x86_64-linux-gnu/libfoo.so\n"),
            MagicMock(returncode=0, stdout="1.0\t\t1.0\n"),  # empty src_name
        ]
        with self.assertRaises(ValueError) as ctx:
            _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libfoo.so")
        self.assertIn("empty metadata", str(ctx.exception))

    @patch("manifest.subprocess.run")
    def test_metadata_empty_source_version_raises(self, mock_run):
        """Empty source:Version field must raise."""
        mock_run.side_effect = [
            MagicMock(returncode=0, stdout="libfoo:amd64: /usr/lib/x86_64-linux-gnu/libfoo.so\n"),
            MagicMock(returncode=0, stdout="1.0\tfoo-src\t\n"),  # empty src_ver
        ]
        with self.assertRaises(ValueError) as ctx:
            _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libfoo.so")
        self.assertIn("empty metadata", str(ctx.exception))

    @patch("manifest.subprocess.run")
    def test_metadata_unexpected_format_raises(self, mock_run):
        """Unexpected output format must raise."""
        mock_run.side_effect = [
            MagicMock(returncode=0, stdout="libfoo:amd64: /usr/lib/x86_64-linux-gnu/libfoo.so\n"),
            MagicMock(returncode=0, stdout="unexpected\n"),
        ]
        with self.assertRaises(ValueError) as ctx:
            _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libfoo.so")
        self.assertIn("unexpected format", str(ctx.exception))

    @patch("manifest.subprocess.run")
    def test_metadata_timeout_raises(self, mock_run):
        """Timeout on metadata query must raise."""
        import subprocess
        mock_run.side_effect = [
            MagicMock(returncode=0, stdout="libfoo:amd64: /usr/lib/x86_64-linux-gnu/libfoo.so\n"),
            subprocess.TimeoutExpired(cmd="dpkg-query", timeout=5),
        ]
        with self.assertRaises(ValueError) as ctx:
            _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libfoo.so")
        self.assertIn("metadata error", str(ctx.exception))


# -- Epochs/version fields distinct --------------------------------------------

class TestEpochsDistinct(unittest.TestCase):
    """Source versions with epochs must be preserved verbatim."""

    @patch("manifest.subprocess.run")
    def test_epoch_in_source_version(self, mock_run):
        mock_run.side_effect = [
            MagicMock(returncode=0, stdout="libssl3:amd64: /usr/lib/x86_64-linux-gnu/libssl.so.3\n"),
            MagicMock(returncode=0, stdout="3.0.2-0ubuntu1.29\topenssl\t3.0.2-0ubuntu1.29\n"),
        ]
        result = _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libssl.so.3")
        self.assertIsNotNone(result)
        self.assertEqual(result.source_version, "3.0.2-0ubuntu1.29")
        self.assertEqual(result.binary_version, "3.0.2-0ubuntu1.29")

    @patch("manifest.subprocess.run")
    def test_binary_source_versions_differ(self, mock_run):
        mock_run.side_effect = [
            MagicMock(returncode=0, stdout="libfoo:amd64: /usr/lib/x86_64-linux-gnu/libfoo.so\n"),
            MagicMock(returncode=0, stdout="1.2.3-1ubuntu1\tfoo-src\t1.2.3-1ubuntu2\n"),
        ]
        result = _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libfoo.so")
        self.assertIsNotNone(result)
        self.assertEqual(result.binary_version, "1.2.3-1ubuntu1")
        self.assertEqual(result.source_version, "1.2.3-1ubuntu2")

    @patch("manifest.subprocess.run")
    def test_source_package_name_preserved(self, mock_run):
        """Source package name differs from binary package name."""
        mock_run.side_effect = [
            MagicMock(returncode=0, stdout="libnotify4:amd64: /usr/lib/x86_64-linux-gnu/libnotify.so.4\n"),
            MagicMock(returncode=0, stdout="0.7.9-3ubuntu5\tlibnotify\t0.7.9-3ubuntu5\n"),
        ]
        result = _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libnotify.so.4")
        self.assertIsNotNone(result)
        self.assertEqual(result.binary_package, "libnotify4")
        self.assertEqual(result.source_package, "libnotify")


# -- .NET precedence in generation ----------------------------------------------

class TestDotnetPrecedesOwner(unittest.TestCase):
    """Dotnet runtime files must be classified before system owner lookup."""

    def test_dotnet_in_usr_lib_is_dotnet(self):
        self.assertTrue(_is_dotnet_runtime_file("usr/lib/libcoreclr.so"))

    def test_dotnet_in_usr_bin_is_dotnet(self):
        self.assertTrue(_is_dotnet_runtime_file("usr/bin/libcoreclr.so"))

    def test_photino_not_dotnet(self):
        self.assertFalse(_is_dotnet_runtime_file("usr/bin/Photino.Native.so"))

    @patch("manifest._query_dpkg_owner_with_relocation")
    def test_dotnet_skips_dpkg_lookup(self, mock_owner):
        """generate_manifest must not call dpkg for .NET runtime files."""
        with tempfile.TemporaryDirectory() as td:
            appdir = Path(td)
            bin_dir = appdir / "usr" / "bin"
            bin_dir.mkdir(parents=True)
            # Create a .NET runtime file
            (bin_dir / "libcoreclr.so").write_bytes(b"\x7fELF" + b"\x00" * 60)
            (bin_dir / "MqttProbe.Desktop").write_bytes(b"\x7fELF" + b"\x00" * 60)
            lib_dir = appdir / "usr" / "lib"
            lib_dir.mkdir(parents=True)
            manifest = generate_manifest(appdir, "2026-10-04")
            # dpkg should NOT have been called for libcoreclr.so
            for c in mock_owner.call_args_list:
                self.assertNotIn("libcoreclr", str(c))

    @patch("manifest._query_dpkg_owner_with_relocation")
    def test_generate_manifest_includes_dotnet_vendor(self, mock_owner):
        """generate_manifest must include .NET Runtime Native vendor."""
        mock_owner.return_value = None
        with tempfile.TemporaryDirectory() as td:
            appdir = Path(td)
            (appdir / "usr" / "bin").mkdir(parents=True)
            (appdir / "usr" / "bin" / "MqttProbe.Desktop").write_bytes(b"\x7fELF" + b"\x00" * 60)
            (appdir / "usr" / "lib").mkdir(parents=True)
            manifest = generate_manifest(appdir, "2026-10-04")
            vendor_names = [v.name for v in manifest.vendor_ingredients]
            self.assertIn(".NET Runtime Native", vendor_names)


# -- Relocation: ambiguous same-basename rejects --------------------------------

class TestRelocationAmbiguous(unittest.TestCase):
    """Same basename in multiple locations must be rejected."""

    def _pulse_pkg(self):
        return SystemPackage(
            binary_package="libpulse0",
            binary_version="15.99.1-0ubuntu3",
            source_package="pulseaudio",
            source_version="15.99.1-0ubuntu3",
            license_hint="",
            bundled_files=[],
        )

    @patch("manifest._is_elf", return_value=True)
    @patch("manifest._query_dpkg_owner")
    def test_distinct_host_files_ambiguous_rejects(self, mock_owner, mock_elf):
        """Two genuinely distinct host files stay ambiguous."""
        mock_owner.side_effect = [None, None, None, None]
        with tempfile.TemporaryDirectory() as td:
            dir1 = Path(td) / "usr" / "lib" / "x86_64-linux-gnu" / "pulseaudio"
            dir1.mkdir(parents=True)
            (dir1 / "libpulsecommon-15.99.so").write_bytes(b"\x7fELF")
            dir2 = Path(td) / "usr" / "lib" / "x86_64-linux-gnu" / "other"
            dir2.mkdir(parents=True)
            (dir2 / "libpulsecommon-15.99.so").write_bytes(b"\x7fELF")
            real1 = os.path.realpath(dir1 / "libpulsecommon-15.99.so")
            real2 = os.path.realpath(dir2 / "libpulsecommon-15.99.so")
            with patch("manifest.os.walk") as mock_walk, \
                 patch("manifest.os.path.realpath", side_effect=lambda p: {
                     str(dir1 / "libpulsecommon-15.99.so"): real1,
                     str(dir2 / "libpulsecommon-15.99.so"): real2,
                 }.get(p, p)):
                mock_walk.return_value = [
                    (str(dir1), [], ["libpulsecommon-15.99.so"]),
                    (str(dir2), [], ["libpulsecommon-15.99.so"]),
                ]
                result = _query_dpkg_owner_with_relocation("usr/lib/libpulsecommon-15.99.so")
        self.assertIsNone(result)

    @patch("manifest._query_dpkg_owner")
    def test_second_alias_retried_when_first_misses(self, mock_owner):
        """One real file, two spellings: /lib alias must still be tried."""
        pkg = self._pulse_pkg()
        # 4 exact candidates miss, then /usr/lib alias misses, /lib alias owns.
        mock_owner.side_effect = [None, None, None, None, None, pkg]
        with tempfile.TemporaryDirectory() as td:
            usr_dir = Path(td) / "usr" / "lib" / "x86_64-linux-gnu" / "pulseaudio"
            usr_dir.mkdir(parents=True)
            usr_file = usr_dir / "libpulsecommon-15.99.so"
            usr_file.write_bytes(b"\x7fELF")
            lib_dir = Path(td) / "lib" / "x86_64-linux-gnu" / "pulseaudio"
            lib_dir.mkdir(parents=True)
            lib_file = lib_dir / "libpulsecommon-15.99.so"
            lib_file.write_bytes(b"\x7fELF")
            identity = os.path.realpath(usr_file)
            with patch("manifest.os.walk") as mock_walk, \
                 patch("manifest.os.path.realpath", side_effect=lambda p: {
                     str(usr_file): identity,
                     str(lib_file): identity,
                 }.get(p, p)):
                mock_walk.side_effect = [
                    [(str(usr_dir), [], ["libpulsecommon-15.99.so"])],
                    [(str(lib_dir), [], ["libpulsecommon-15.99.so"])],
                ]
                result = _query_dpkg_owner_with_relocation("usr/lib/libpulsecommon-15.99.so")
        self.assertIsNotNone(result)
        self.assertEqual(result.binary_package, "libpulse0")
        # The /lib spelling, not the realpath, produced the owner.
        final_lookup = mock_owner.call_args_list[-1][0][0]
        self.assertEqual(final_lookup, str(lib_file))

    @patch("manifest._query_dpkg_owner")
    def test_both_aliases_miss_returns_none(self, mock_owner):
        """Same real file, neither spelling owned -> no owner."""
        mock_owner.side_effect = [None, None, None, None, None, None]
        with tempfile.TemporaryDirectory() as td:
            usr_dir = Path(td) / "usr" / "lib" / "x86_64-linux-gnu" / "pulseaudio"
            usr_dir.mkdir(parents=True)
            usr_file = usr_dir / "libpulsecommon-15.99.so"
            usr_file.write_bytes(b"\x7fELF")
            lib_dir = Path(td) / "lib" / "x86_64-linux-gnu" / "pulseaudio"
            lib_dir.mkdir(parents=True)
            lib_file = lib_dir / "libpulsecommon-15.99.so"
            lib_file.write_bytes(b"\x7fELF")
            identity = os.path.realpath(usr_file)
            with patch("manifest.os.walk") as mock_walk, \
                 patch("manifest.os.path.realpath", return_value=identity):
                mock_walk.side_effect = [
                    [(str(usr_dir), [], ["libpulsecommon-15.99.so"])],
                    [(str(lib_dir), [], ["libpulsecommon-15.99.so"])],
                ]
                result = _query_dpkg_owner_with_relocation("usr/lib/libpulsecommon-15.99.so")
        self.assertIsNone(result)

    @patch("manifest._query_dpkg_owner")
    def test_staged_non_native_blocks_relocation(self, mock_owner):
        """staged_is_native_lib=False disables relocation entirely."""
        mock_owner.side_effect = [None, None, None, None]
        with patch("manifest.os.walk") as mock_walk:
            result = _query_dpkg_owner_with_relocation(
                "usr/lib/libpulsecommon-15.99.so",
                staged_is_native_lib=False,
            )
        self.assertIsNone(result)
        mock_walk.assert_not_called()

    @patch("manifest._query_dpkg_owner")
    def test_alias_path_preserved_for_dpkg_lookup(self, mock_owner):
        """The surviving alias path (not the realpath) is handed to dpkg."""
        pkg = self._pulse_pkg()
        mock_owner.side_effect = [None, None, None, None, pkg]
        with tempfile.TemporaryDirectory() as td:
            usr_dir = Path(td) / "usr" / "lib" / "x86_64-linux-gnu" / "pulseaudio"
            usr_dir.mkdir(parents=True)
            usr_file = usr_dir / "libpulsecommon-15.99.so"
            usr_file.write_bytes(b"\x7fELF")
            identity = os.path.realpath(usr_file)
            with patch("manifest.os.walk") as mock_walk, \
                 patch("manifest.os.path.realpath", return_value=identity):
                mock_walk.side_effect = [
                    [(str(usr_dir), [], ["libpulsecommon-15.99.so"])],
                    [],
                ]
                result = _query_dpkg_owner_with_relocation("usr/lib/libpulsecommon-15.99.so")
        self.assertIsNotNone(result)
        # Final lookup used the alias path, not the normalized realpath.
        final_lookup = mock_owner.call_args_list[-1][0][0]
        self.assertTrue(final_lookup.startswith(usr_dir.as_posix()))

    @patch("manifest._query_dpkg_owner")
    def test_nested_staged_path_has_no_relocation(self, mock_owner):
        """usr/lib/<dir>/<name> must not use relocation fallback."""
        mock_owner.return_value = None
        with patch("manifest.os.walk") as mock_walk:
            result = _query_dpkg_owner_with_relocation(
                "usr/lib/gtk-3.0/3.0.0/immodules/im-xim.so"
            )
        self.assertIsNone(result)
        mock_walk.assert_not_called()

    @patch("manifest._query_dpkg_owner")
    def test_direct_flattened_lib_uses_relocation(self, mock_owner):
        """usr/lib/<name> is eligible for relocation when exact paths miss."""
        pkg = self._pulse_pkg()
        mock_owner.side_effect = [None, None, None, None, pkg]
        with tempfile.TemporaryDirectory() as td:
            search_dir = Path(td) / "usr" / "lib" / "x86_64-linux-gnu" / "pulseaudio"
            search_dir.mkdir(parents=True)
            (search_dir / "libpulsecommon-15.99.so").write_bytes(b"\x7fELF")
            with patch("manifest.os.walk") as mock_walk, \
                 patch("manifest._is_elf", return_value=True):
                mock_walk.side_effect = [
                    [(str(search_dir), [], ["libpulsecommon-15.99.so"])],
                    [],
                ]
                result = _query_dpkg_owner_with_relocation("usr/lib/libpulsecommon-15.99.so")
        self.assertIsNotNone(result)
        self.assertEqual(result.binary_package, "libpulse0")


if __name__ == "__main__":
    unittest.main()