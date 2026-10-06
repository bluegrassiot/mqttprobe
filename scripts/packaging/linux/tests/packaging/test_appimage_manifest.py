"""Behavioral tests for MQTTProbe package manifest generation,
NOTICE file builder, path mapping, dpkg parsing, and vendor definitions.

Split from test_appimage_sources.py to keep each module under 500 lines.
"""

import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import MagicMock, patch

REPO_ROOT = Path(__file__).resolve().parents[5]
PACKAGING_DIR = REPO_ROOT / "scripts" / "packaging"
LINUX_DIR = PACKAGING_DIR / "linux"

if str(LINUX_DIR) not in sys.path:
    sys.path.insert(0, str(LINUX_DIR))

from manifest import (
    BundledFile,
    PackageManifest,
    SystemPackage,
    VendorIngredient,
    _map_staged_to_system,
    _system_path_candidates,
    _is_dotnet_runtime_file,
    _query_dpkg_owner,
    format_manifest_text,
    generate_manifest,
)

NOTICE_AVAILABLE = False
try:
    from notices import build_notice as _nb, _get_build_date as _gbd
    NOTICE_AVAILABLE = True
except ImportError:
    _nb = None
    _gbd = None


class TestPackageManifest(unittest.TestCase):
    def _make_manifest(self):
        return PackageManifest(
            build_date="2026-10-04T00:00:00Z",
            build_system_packages=[
                SystemPackage(
                    binary_package="libwebkit2gtk-4.1-0",
                    binary_version="2.44.0-0ubuntu0.22.04.1",
                    source_package="webkit2gtk",
                    source_version="2.44.0-0ubuntu0.22.04.1",
                    license_hint="LGPL-2.1+",
                    bundled_files=["usr/lib/x86_64-linux-gnu/webkit2gtk-4.1/libwebkit2gtk-4.1.so.0"],
                ),
                SystemPackage(
                    binary_package="libgtk-3-0",
                    binary_version="3.24.33-1ubuntu2",
                    source_package="gtk+3.0",
                    source_version="3.24.33-1ubuntu2",
                    license_hint="LGPL-2.1+",
                    bundled_files=["usr/lib/x86_64-linux-gnu/libgtk-3.so.0"],
                ),
            ],
            vendor_ingredients=[
                VendorIngredient(
                    name="Photino.Native",
                    version="4.0.22",
                    source_url="https://github.com/tryphotino/photino.Native",
                    license_id="Apache-2.0",
                    description="Native WebKit wrapper",
                    bundled_paths=["usr/bin/Photino.Native.so"],
                ),
            ],
            all_bundled_files=[
                BundledFile("usr/lib/x86_64-linux-gnu/webkit2gtk-4.1/libwebkit2gtk-4.1.so.0",
                            "/usr/lib/x86_64-linux-gnu/webkit2gtk-4.1/libwebkit2gtk-4.1.so.0",
                            "shared-lib"),
                BundledFile("usr/bin/Photino.Native.so", "", "shared-lib"),
            ],
            unknown_files=[],
        )

    def test_manifest_total_counts(self):
        manifest = self._make_manifest()
        self.assertEqual(manifest.total_package_count(), 3)
        self.assertEqual(manifest.total_file_count(), 2)
        self.assertEqual(manifest.unknown_count(), 0)

    def test_format_manifest_text_contains_packages(self):
        manifest = self._make_manifest()
        text = format_manifest_text(manifest)
        self.assertIn("libwebkit2gtk-4.1-0", text)
        self.assertIn("libgtk-3-0", text)
        self.assertIn("Photino.Native", text)

    def test_format_manifest_text_has_header(self):
        manifest = self._make_manifest()
        text = format_manifest_text(manifest)
        self.assertIn("MQTTProbe AppImage Package Manifest", text)
        self.assertIn("System Packages", text)
        self.assertIn("Vendor Ingredients", text)

    def test_format_manifest_text_has_column_headers(self):
        manifest = self._make_manifest()
        text = format_manifest_text(manifest)
        self.assertIn("binary-package", text)
        self.assertIn("source-package", text)

    def test_manifest_with_unknown_files(self):
        manifest = self._make_manifest()
        manifest.unknown_files.append("usr/lib/something.so")
        self.assertEqual(manifest.unknown_count(), 1)
        text = format_manifest_text(manifest)
        self.assertIn("Unknown/Unclaimed ELFs", text)
        self.assertIn("usr/lib/something.so", text)

    def test_system_package_dataclass(self):
        pkg = SystemPackage(
            binary_package="libnotify4",
            binary_version="0.7.9-3ubuntu5",
            source_package="libnotify",
            source_version="0.7.9-3ubuntu5",
            license_hint="LGPL-2.1+",
            bundled_files=["usr/lib/x86_64-linux-gnu/libnotify.so.4"],
        )
        self.assertEqual(pkg.binary_package, "libnotify4")
        self.assertEqual(pkg.source_package, "libnotify")
        self.assertEqual(len(pkg.bundled_files), 1)

    def test_vendor_ingredient_dataclass(self):
        v = VendorIngredient(
            name="Photino.Native",
            version="4.0.22",
            source_url="https://github.com/tryphotino/photino.Native",
            license_id="Apache-2.0",
            description="Native WebKit wrapper",
            bundled_paths=["usr/bin/Photino.Native.so"],
        )
        self.assertEqual(v.name, "Photino.Native")
        self.assertEqual(v.version, "4.0.22")


class TestNoticeBuilder(unittest.TestCase):
    @unittest.skipUnless(NOTICE_AVAILABLE, "notices not importable")
    def _make_manifest(self):
        return PackageManifest(
            build_date="2026-10-04",
            build_system_packages=[
                SystemPackage("libwebkit2gtk-4.1-0", "2.44.0-0ubuntu0.22.04.1",
                              "webkit2gtk", "2.44.0-0ubuntu0.22.04.1", "LGPL-2.1+",
                              ["usr/lib/x86_64-linux-gnu/webkit2gtk-4.1/libwebkit2gtk-4.1.so.0"]),
            ],
            vendor_ingredients=[
                VendorIngredient("Photino.Native", "4.0.22",
                                 "https://github.com/tryphotino/photino.Native",
                                 "Apache-2.0", "Native wrapper", []),
            ],
            all_bundled_files=[],
            unknown_files=[],
        )

    @unittest.skipUnless(NOTICE_AVAILABLE, "notices not importable")
    def test_notice_contains_version(self):
        manifest = self._make_manifest()
        notice = _nb(manifest, "1.0.6", "mqttprobe-third-party-sources-v1.0.6.tar.gz")
        self.assertIn("v1.0.6", notice)

    @unittest.skipUnless(NOTICE_AVAILABLE, "notices not importable")
    def test_notice_contains_webkit_modification_record(self):
        manifest = self._make_manifest()
        notice = _nb(manifest, "1.0.6", "test.tar.gz")
        self.assertIn("WebKit Binary Modification Record", notice)
        self.assertIn("/usr/lib/x86_64-linux-gnu/webkit2gtk-4.1", notice)
        self.assertIn("././/lib/x86_64-linux-gnu/webkit2gtk-4.1", notice)
        self.assertIn("40 bytes", notice)

    @unittest.skipUnless(NOTICE_AVAILABLE, "notices not importable")
    def test_notice_contains_package_table(self):
        manifest = self._make_manifest()
        notice = _nb(manifest, "1.0.6", "test.tar.gz")
        self.assertIn("libwebkit2gtk-4.1-0", notice)
        self.assertIn("2.44.0-0ubuntu0.22.04.1", notice)

    @unittest.skipUnless(NOTICE_AVAILABLE, "notices not importable")
    def test_notice_contains_vendor_ingredients(self):
        manifest = self._make_manifest()
        notice = _nb(manifest, "1.0.6", "test.tar.gz")
        self.assertIn("Photino.Native", notice)
        self.assertIn("4.0.22", notice)

    @unittest.skipUnless(NOTICE_AVAILABLE, "notices not importable")
    def test_notice_contains_extraction_instructions(self):
        manifest = self._make_manifest()
        notice = _nb(manifest, "1.0.6", "test.tar.gz")
        self.assertIn("--appimage-extract", notice)
        self.assertIn("squashfs-root", notice)
        self.assertIn("AppRun", notice)

    @unittest.skipUnless(NOTICE_AVAILABLE, "notices not importable")
    def test_notice_contains_source_archive_name(self):
        manifest = self._make_manifest()
        archive_name = "mqttprobe-third-party-sources-v1.0.6.tar.gz"
        notice = _nb(manifest, "1.0.6", archive_name)
        self.assertIn(archive_name, notice)

    @unittest.skipUnless(NOTICE_AVAILABLE, "notices not importable")
    def test_notice_contains_build_date(self):
        manifest = PackageManifest(
            build_date="2026-10-04",
            build_system_packages=[
                SystemPackage("libwebkit2gtk-4.1-0", "2.44.0-0ubuntu0.22.04.1",
                              "webkit2gtk", "2.44.0-0ubuntu0.22.04.1", "LGPL-2.1+",
                              ["usr/lib/x86_64-linux-gnu/webkit2gtk-4.1/libwebkit2gtk-4.1.so.0"]),
            ],
            vendor_ingredients=[
                VendorIngredient("Photino.Native", "4.0.22",
                                 "https://github.com/tryphotino/photino.Native",
                                 "Apache-2.0", "Native wrapper", []),
            ],
            all_bundled_files=[],
            unknown_files=[],
        )
        notice = _nb(manifest, "1.0.6", "test.tar.gz", source_date_epoch="2026-10-04")
        self.assertIn("2026-10-04", notice)

    @unittest.skipUnless(NOTICE_AVAILABLE, "notices not importable")
    def test_notice_affected_binaries(self):
        manifest = self._make_manifest()
        notice = _nb(manifest, "1.0.6", "test.tar.gz")
        self.assertIn("libwebkit2gtk-4.1.so.0", notice)
        self.assertIn("WebKitWebProcess", notice)

    @unittest.skipUnless(NOTICE_AVAILABLE, "notices not importable")
    def test_get_build_date_uses_source_date_epoch(self):
        date = _gbd("1728000000")
        self.assertIn("2024", date)

    @unittest.skipUnless(NOTICE_AVAILABLE, "notices not importable")
    def test_get_build_date_fallback_to_utc(self):
        date = _gbd(None)
        self.assertRegex(date, r"^\d{4}-\d{2}-\d{2}$")


class TestPathMapping(unittest.TestCase):
    def test_collapsed_multiarch_lib(self):
        result = _map_staged_to_system("usr/lib/libwebkit2gtk-4.1.so.0")
        self.assertEqual(result, "/usr/lib/x86_64-linux-gnu/libwebkit2gtk-4.1.so.0")

    def test_collapsed_multiarch_gstreamer(self):
        result = _map_staged_to_system("usr/lib/gstreamer-1.0/libgstcoreelements.so")
        self.assertEqual(result, "/usr/lib/x86_64-linux-gnu/gstreamer-1.0/libgstcoreelements.so")

    def test_collapsed_multiarch_gtk(self):
        result = _map_staged_to_system("usr/lib/gtk-3.0/3.0.0/immodules/im-xim.so")
        self.assertEqual(result, "/usr/lib/x86_64-linux-gnu/gtk-3.0/3.0.0/immodules/im-xim.so")

    def test_collapsed_multiarch_gio(self):
        result = _map_staged_to_system("usr/lib/gio/modules/libdconfsettings.so")
        self.assertEqual(result, "/usr/lib/x86_64-linux-gnu/gio/modules/libdconfsettings.so")

    def test_preserved_multiarch_path(self):
        result = _map_staged_to_system("usr/lib/x86_64-linux-gnu/webkit2gtk-4.1/libwebkit2gtk-4.1.so.0")
        self.assertEqual(result, "/usr/lib/x86_64-linux-gnu/webkit2gtk-4.1/libwebkit2gtk-4.1.so.0")

    def test_usr_bin_path(self):
        result = _map_staged_to_system("usr/bin/MqttProbe.Desktop")
        self.assertEqual(result, "/usr/bin/MqttProbe.Desktop")

    def test_non_usr_path_returns_empty(self):
        self.assertEqual(_map_staged_to_system("AppRun"), "")
        self.assertEqual(_map_staged_to_system("icon.png"), "")


class TestSystemPathCandidates(unittest.TestCase):
    def test_multiarch_first_for_collapsed_lib(self):
        candidates = _system_path_candidates("usr/lib/libgtk-3.so.0")
        self.assertEqual(candidates, [
            "/usr/lib/x86_64-linux-gnu/libgtk-3.so.0",
            "/lib/x86_64-linux-gnu/libgtk-3.so.0",
            "/usr/lib/libgtk-3.so.0",
            "/lib/libgtk-3.so.0",
        ])

    def test_single_candidate_for_preserved_path(self):
        candidates = _system_path_candidates("usr/lib/x86_64-linux-gnu/webkit2gtk-4.1/libwebkit2gtk-4.1.so.0")
        self.assertEqual(candidates, [
            "/usr/lib/x86_64-linux-gnu/webkit2gtk-4.1/libwebkit2gtk-4.1.so.0",
            "/lib/x86_64-linux-gnu/webkit2gtk-4.1/libwebkit2gtk-4.1.so.0",
        ])

    def test_single_candidate_for_usr_bin(self):
        candidates = _system_path_candidates("usr/bin/Photino.Native.so")
        self.assertEqual(candidates, ["/usr/bin/Photino.Native.so"])

    def test_empty_for_non_system(self):
        self.assertEqual(_system_path_candidates("AppRun"), [])
        self.assertEqual(_system_path_candidates("icon.png"), [])


class TestDpkgParsing(unittest.TestCase):
    @patch("manifest.subprocess.run")
    def test_simple_package_format(self, mock_run):
        mock_run.side_effect = [
            MagicMock(returncode=0, stdout="libgtk-3-0: /usr/lib/x86_64-linux-gnu/libgtk-3.so.0\n"),
            MagicMock(returncode=0, stdout="3.24.33-1ubuntu2\tgtk+3.0\t3.24.33-1ubuntu2\n"),
        ]
        result = _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libgtk-3.so.0")
        self.assertIsNotNone(result)
        self.assertEqual(result.binary_package, "libgtk-3-0")
        self.assertEqual(result.binary_version, "3.24.33-1ubuntu2")
        self.assertEqual(result.source_package, "gtk+3.0")
        self.assertEqual(result.source_version, "3.24.33-1ubuntu2")

    @patch("manifest.subprocess.run")
    def test_multiarch_package_format(self, mock_run):
        mock_run.side_effect = [
            MagicMock(returncode=0, stdout="libgtk-3-0:amd64: /usr/lib/x86_64-linux-gnu/libgtk-3.so.0\n"),
            MagicMock(returncode=0, stdout="3.24.33-1ubuntu2\tgtk+3.0\t3.24.33-1ubuntu2\n"),
        ]
        result = _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libgtk-3.so.0")
        self.assertIsNotNone(result)
        self.assertEqual(result.binary_package, "libgtk-3-0")

    @patch("manifest.subprocess.run")
    def test_diverted_entry_returns_none(self, mock_run):
        mock_run.return_value = MagicMock(
            returncode=0,
            stdout="diverted to libgtk-3-0 by libgtk-3-0:amd64: /usr/lib/x86_64-linux-gnu/libgtk-3.so.0\n",
        )
        result = _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libgtk-3.so.0")
        self.assertIsNone(result)

    @patch("manifest.subprocess.run")
    def test_not_found_returns_none(self, mock_run):
        mock_run.return_value = MagicMock(returncode=1, stdout="")
        result = _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libnonexistent.so.0")
        self.assertIsNone(result)

    @patch("manifest.subprocess.run")
    def test_consolidates_source_version(self, mock_run):
        mock_run.side_effect = [
            MagicMock(returncode=0, stdout="libnotify4:amd64: /usr/lib/x86_64-linux-gnu/libnotify.so.4\n"),
            MagicMock(returncode=0, stdout="0.7.9-3ubuntu5\tlibnotify\t0.7.9-3ubuntu5\n"),
        ]
        result = _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libnotify.so.4")
        self.assertIsNotNone(result)
        self.assertEqual(result.source_package, "libnotify")
        self.assertEqual(result.source_version, "0.7.9-3ubuntu5")

    @patch("manifest.subprocess.run")
    def test_ambiguous_ownership_raises(self, mock_run):
        """Multiple different packages owning same path must raise."""
        mock_run.return_value = MagicMock(
            returncode=0,
            stdout="pkg-a:amd64: /usr/lib/x86_64-linux-gnu/libfoo.so\npkg-b:amd64: /usr/lib/x86_64-linux-gnu/libfoo.so\n",
        )
        with self.assertRaises(ValueError) as ctx:
            _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libfoo.so")
        self.assertIn("Ambiguous", str(ctx.exception))

    @patch("manifest.subprocess.run")
    def test_metadata_failure_raises(self, mock_run):
        """dpkg-query metadata failure must raise, not silently substitute."""
        mock_run.side_effect = [
            MagicMock(returncode=0, stdout="libfoo:amd64: /usr/lib/x86_64-linux-gnu/libfoo.so\n"),
            MagicMock(returncode=1, stdout="", stderr="error"),
        ]
        with self.assertRaises(ValueError) as ctx:
            _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libfoo.so")
        self.assertIn("metadata failed", str(ctx.exception))

    @patch("manifest.subprocess.run")
    def test_metadata_empty_field_raises(self, mock_run):
        """Empty metadata fields must raise, not substitute defaults."""
        mock_run.side_effect = [
            MagicMock(returncode=0, stdout="libfoo:amd64: /usr/lib/x86_64-linux-gnu/libfoo.so\n"),
            MagicMock(returncode=0, stdout="1.0\t\t1.0\n"),  # empty source:Package
        ]
        with self.assertRaises(ValueError) as ctx:
            _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libfoo.so")
        self.assertIn("empty metadata", str(ctx.exception))

    @patch("manifest.subprocess.run")
    def test_epoch_preserved_in_source_version(self, mock_run):
        """Epochs must be preserved verbatim."""
        mock_run.side_effect = [
            MagicMock(returncode=0, stdout="libssl3:amd64: /usr/lib/x86_64-linux-gnu/libssl.so.3\n"),
            MagicMock(returncode=0, stdout="3.0.2-0ubuntu1.29\topenssl\t3.0.2-0ubuntu1.29\n"),
        ]
        result = _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libssl.so.3")
        self.assertIsNotNone(result)
        self.assertEqual(result.source_version, "3.0.2-0ubuntu1.29")

    @patch("manifest.subprocess.run")
    def test_binary_source_version_distinct(self, mock_run):
        """Binary and source versions can differ."""
        mock_run.side_effect = [
            MagicMock(returncode=0, stdout="libfoo:amd64: /usr/lib/x86_64-linux-gnu/libfoo.so\n"),
            MagicMock(returncode=0, stdout="1.2.3-1ubuntu1\tfoo-src\t1.2.3-1ubuntu2\n"),
        ]
        result = _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libfoo.so")
        self.assertIsNotNone(result)
        self.assertEqual(result.binary_version, "1.2.3-1ubuntu1")
        self.assertEqual(result.source_version, "1.2.3-1ubuntu2")

    @patch("manifest._is_elf", return_value=True)
    @patch("manifest._query_dpkg_owner")
    def test_relocation_finds_subdirectory_lib(self, mock_owner, mock_elf):
        """Native .so in subdirectory found via relocation fallback."""
        pulseaudio_pkg = SystemPackage(
            binary_package="libpulse0",
            binary_version="15.99.1-0ubuntu3",
            source_package="pulseaudio",
            source_version="15.99.1-0ubuntu3",
            license_hint="",
            bundled_files=[],
        )
        # All exact path queries fail, then relocation succeeds
        mock_owner.side_effect = [
            None,  # /usr/lib/x86_64-linux-gnu/libpulsecommon-15.99.so
            None,  # /lib/x86_64-linux-gnu/libpulsecommon-15.99.so
            None,  # /usr/lib/libpulsecommon-15.99.so
            None,  # /lib/libpulsecommon-15.99.so
            pulseaudio_pkg,  # relocated path
        ]
        with tempfile.TemporaryDirectory() as td:
            real_path = Path(td) / "usr" / "lib" / "x86_64-linux-gnu" / "pulseaudio"
            real_path.mkdir(parents=True)
            (real_path / "libpulsecommon-15.99.so").write_bytes(b"\x7fELF" + b"\x00" * 60)
            with patch("manifest.os.walk") as mock_walk:
                mock_walk.side_effect = [
                    [(str(real_path), [], ["libpulsecommon-15.99.so"])],
                    [],
                ]
                from manifest import _query_dpkg_owner_with_relocation
                result = _query_dpkg_owner_with_relocation("usr/lib/libpulsecommon-15.99.so")
        self.assertIsNotNone(result)
        self.assertEqual(result.binary_package, "libpulse0")


class TestDotnetClassification(unittest.TestCase):
    def test_coreclr_is_dotnet(self):
        self.assertTrue(_is_dotnet_runtime_file("usr/bin/libcoreclr.so"))

    def test_hostpolicy_is_dotnet(self):
        self.assertTrue(_is_dotnet_runtime_file("usr/bin/libhostpolicy.so"))

    def test_system_native_is_dotnet(self):
        self.assertTrue(_is_dotnet_runtime_file("usr/bin/libSystem.Native.so"))

    def test_versioned_so_is_dotnet(self):
        self.assertTrue(_is_dotnet_runtime_file("usr/bin/libclrgc.so.1"))

    def test_photino_is_not_dotnet(self):
        self.assertFalse(_is_dotnet_runtime_file("usr/bin/Photino.Native.so"))

    def test_main_app_is_not_dotnet(self):
        self.assertFalse(_is_dotnet_runtime_file("usr/bin/MqttProbe.Desktop"))

    def test_non_bin_path_is_not_dotnet(self):
        self.assertFalse(_is_dotnet_runtime_file("opt/libcoreclr.so"))

    def test_usr_lib_path_is_dotnet(self):
        self.assertTrue(_is_dotnet_runtime_file("usr/lib/libmscordaccore.so"))

    def test_non_so_is_not_dotnet(self):
        self.assertFalse(_is_dotnet_runtime_file("usr/bin/libcoreclr.config"))


class TestVelopackVendorPaths(unittest.TestCase):
    def test_no_releases_in_velopack_paths(self):
        from manifest import VELOPACK_LINUX
        for p in VELOPACK_LINUX.bundled_paths:
            self.assertNotIn("RELEASES", p)

    def test_no_nupkg_in_velopack_paths(self):
        from manifest import VELOPACK_LINUX
        for p in VELOPACK_LINUX.bundled_paths:
            self.assertNotIn(".nupkg", p)

    def test_updatenix_in_velopack_paths(self):
        from manifest import VELOPACK_LINUX
        self.assertIn("usr/bin/UpdateNix", VELOPACK_LINUX.bundled_paths)


class TestDotnetRuntimeVendor(unittest.TestCase):
    def test_dotnet_vendor_exists(self):
        from manifest import DOTNET_RUNTIME_NATIVE
        self.assertEqual(DOTNET_RUNTIME_NATIVE.name, ".NET Runtime Native")

    def test_dotnet_vendor_has_coreclr(self):
        from manifest import DOTNET_RUNTIME_NATIVE
        self.assertIn("usr/bin/libcoreclr.so", DOTNET_RUNTIME_NATIVE.bundled_paths)

    def test_dotnet_vendor_has_system_native(self):
        from manifest import DOTNET_RUNTIME_NATIVE
        self.assertIn("usr/bin/libSystem.Native.so", DOTNET_RUNTIME_NATIVE.bundled_paths)

    def test_dotnet_vendor_excludes_createdump(self):
        from manifest import DOTNET_RUNTIME_NATIVE
        self.assertNotIn("usr/bin/createdump", DOTNET_RUNTIME_NATIVE.bundled_paths)

    def test_generate_manifest_includes_dotnet_vendor(self):
        from manifest import DOTNET_RUNTIME_NATIVE
        with tempfile.TemporaryDirectory() as td:
            appdir = Path(td)
            (appdir / "usr" / "bin").mkdir(parents=True)
            (appdir / "usr" / "bin" / "MqttProbe.Desktop").write_bytes(b"\x7fELF" + b"\x00" * 60)
            (appdir / "usr" / "lib").mkdir(parents=True)
            manifest = generate_manifest(appdir, "2026-10-04")
            vendor_names = [v.name for v in manifest.vendor_ingredients]
            self.assertIn(".NET Runtime Native", vendor_names)


if __name__ == "__main__":
    unittest.main()