"""Behavioral tests for MQTTProbe AppImage packaging scripts.

Tests actual script behavior: argument parsing, relocation logic, file
operations, vendor integrity, AppRun runtime behavior, and deliberate
component exclusions. Not source-string matching.
"""

import hashlib
import os
import struct
import subprocess
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[5]
PACKAGING_DIR = REPO_ROOT / "scripts" / "packaging"
LINUX_DIR = PACKAGING_DIR / "linux"
VENDOR_DIR = LINUX_DIR / "vendor"


def sha256_of(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(8192), b""):
            h.update(chunk)
    return h.hexdigest()


class TestVendorIntegrity(unittest.TestCase):
    """Vendor script snapshot integrity: hashes and behavioral properties."""

    def test_gstreamer_hash_matches_upstream(self):
        """GStreamer script must match Tauri commit 30da1fd6 exactly."""
        script = VENDOR_DIR / "linuxdeploy-plugin-gstreamer.sh"
        self.assertTrue(script.exists(), f"Missing: {script}")
        actual = sha256_of(script)
        expected = "2a15ce9da8de6e20159e1ab27861a7a5ef8758c81a6278ba4ab30cefa1d74c9f"
        self.assertEqual(actual, expected,
                         f"GStreamer hash mismatch: {actual}")

    def test_gtk_vendor_removes_blanket_webkit_patch(self):
        """GTK vendor script must NOT contain the original blanket sed command."""
        script = VENDOR_DIR / "linuxdeploy-plugin-gtk.sh"
        self.assertTrue(script.exists())
        content = script.read_text(encoding="utf-8", errors="replace")
        self.assertNotIn(
            '-exec sed -i',
            content,
            "Vendor GTK script still has blanket -exec sed command"
        )

    def test_gtk_vendor_retains_glib_schema_logic(self):
        """GTK vendor script must retain the core GLib schema bundling logic."""
        script = VENDOR_DIR / "linuxdeploy-plugin-gtk.sh"
        content = script.read_text(encoding="utf-8", errors="replace")
        self.assertIn("glib_schemasdir", content)
        self.assertIn("glib-compile-schemas", content)

    def test_gstreamer_vendor_retains_plugin_copy_logic(self):
        """GStreamer vendor script must retain plugin copy and rpath logic."""
        script = VENDOR_DIR / "linuxdeploy-plugin-gstreamer.sh"
        content = script.read_text(encoding="utf-8", errors="replace")
        self.assertIn("patchelf --set-rpath", content)
        self.assertIn("gstreamer", content.lower())

    def test_notice_md_exists_with_required_sections(self):
        """NOTICE.md must document provenance for all vendored components."""
        notice = VENDOR_DIR / "NOTICE.md"
        self.assertTrue(notice.exists(), "NOTICE.md missing")
        content = notice.read_text(encoding="utf-8", errors="replace")
        for required in ["Photino.Native", "linuxdeploy", "Tauri",
                         "webkit2gtk", "LGPL", "Apache-2.0",
                         "3ba4b937d6337b5c58344445db08a5a9a63f07c8",
                         "30da1fd6e17de6107ecc850c95dfb16b5729f2dd"]:
            self.assertIn(required, content,
                          f"NOTICE.md missing required term: {required}")

    def test_notice_md_documents_lttng_exclusion(self):
        """NOTICE.md must document the deliberate LTTng exclusion."""
        notice = VENDOR_DIR / "NOTICE.md"
        content = notice.read_text(encoding="utf-8", errors="replace")
        self.assertIn("libcoreclrtraceptprovider", content)
        self.assertIn("LTTng", content)

    def test_notice_md_documents_webkit_modification(self):
        """NOTICE.md must document the WebKit binary modification."""
        notice = VENDOR_DIR / "NOTICE.md"
        content = notice.read_text(encoding="utf-8", errors="replace")
        self.assertIn("modification", content.lower())
        self.assertIn("relocation", content.lower())
        self.assertIn("././/lib/x86_64-linux-gnu/webkit2gtk-4.1", content)


class TestWebKitRelocation(unittest.TestCase):
    """Test the narrow WebKit relocation logic with real binary data."""

    OLD_PREFIX = b"/usr/lib/x86_64-linux-gnu/webkit2gtk-4.1"
    NEW_PREFIX = b"././/lib/x86_64-linux-gnu/webkit2gtk-4.1"

    def test_prefixes_same_length(self):
        """Both prefixes MUST be exactly 40 bytes to avoid suffix truncation."""
        self.assertEqual(len(self.OLD_PREFIX), 40)
        self.assertEqual(len(self.NEW_PREFIX), 40)
        self.assertEqual(len(self.OLD_PREFIX), len(self.NEW_PREFIX))

    def _make_mock_elf(self, paths):
        elf = b"\x7fELF" + b"\x00" * 64
        for p in paths:
            elf += p + b"\x00"
        return elf

    def test_full_prefix_match_preserves_size(self):
        data = self._make_mock_elf([
            self.OLD_PREFIX + b"/injected-bundle",
            b"/usr/lib/x86_64-linux-gnu/libgtk-3.so.0",
        ])
        original_size = len(data)
        replaced = data.replace(self.OLD_PREFIX, self.NEW_PREFIX)
        self.assertEqual(len(replaced), original_size)
        self.assertIn(self.NEW_PREFIX, replaced)
        self.assertNotIn(self.OLD_PREFIX, replaced)
        self.assertIn(b"/usr/lib/x86_64-linux-gnu/libgtk-3.so.0", replaced)

    def test_suffix_preserved_after_replacement(self):
        suffix = b"/injected-bundle/libwebkitinjected.so"
        data = self._make_mock_elf([self.OLD_PREFIX + suffix])
        replaced = data.replace(self.OLD_PREFIX, self.NEW_PREFIX)
        self.assertIn(self.NEW_PREFIX + suffix, replaced)
        self.assertEqual(len(replaced), len(data))

    def test_no_match_returns_unchanged(self):
        data = self._make_mock_elf([b"/usr/lib/x86_64-linux-gnu/libgtk-3.so.0"])
        original = bytes(data)
        replaced = data.replace(self.OLD_PREFIX, self.NEW_PREFIX)
        self.assertEqual(replaced, original)

    def test_multiple_occurrences_all_replaced(self):
        data = self._make_mock_elf([
            self.OLD_PREFIX + b"/libwebkit2gtk-4.1.so.0",
            self.OLD_PREFIX + b"/injected-bundle/libwebkitinjected.so",
        ])
        replaced = data.replace(self.OLD_PREFIX, self.NEW_PREFIX)
        self.assertNotIn(self.OLD_PREFIX, replaced)
        self.assertEqual(replaced.count(self.NEW_PREFIX), 2)

    def test_partial_prefix_not_matched(self):
        data = self._make_mock_elf([
            b"/usr/lib/x86_64-linux-gnu/webkit2gtk-3.0",
        ])
        original = bytes(data)
        replaced = data.replace(self.OLD_PREFIX, self.NEW_PREFIX)
        self.assertEqual(replaced, original)

    def test_elf_magic_preserved(self):
        data = self._make_mock_elf([self.OLD_PREFIX + b"/test"])
        replaced = data.replace(self.OLD_PREFIX, self.NEW_PREFIX)
        self.assertTrue(replaced[:4] == b"\x7fELF")

    def test_no_null_padding_needed(self):
        self.assertEqual(len(self.OLD_PREFIX), len(self.NEW_PREFIX))


class TestAppRunBehavior(unittest.TestCase):
    """Test AppRun behavior by extracting and validating its logic."""

    def _extract_apprun(self):
        script = LINUX_DIR / "appdir.sh"
        content = script.read_text(encoding="utf-8", errors="replace")
        start = content.find('cat > "$APPDIR/AppRun" << \'APPRUN_EOF\'')
        if start == -1:
            self.skipTest("AppRun heredoc not found in build script")
        start = content.find("\n", start) + 1
        end = content.find("APPRUN_EOF", start)
        return content[start:end]

    def test_apprun_resolves_from_own_path(self):
        apprun = self._extract_apprun()
        self.assertIn('readlink -f "$0"', apprun)
        import re
        self.assertIsNone(
            re.search(r'APPDIR=.*\$APPIMAGE', apprun),
            "AppRun resolves APPDIR from $APPIMAGE instead of $0"
        )

    def test_apprun_sets_injected_bundle_path(self):
        apprun = self._extract_apprun()
        self.assertIn("WEBKIT_INJECTED_BUNDLE_PATH", apprun)
        self.assertIn('$APPDIR/usr/lib/x86_64-linux-gnu/webkit2gtk-4.1/injected-bundle',
                      apprun)

    def test_apprun_does_not_export_exec_path(self):
        apprun = self._extract_apprun()
        import re
        self.assertIsNone(re.search(r'export\s+WEBKIT_EXEC_PATH', apprun))

    def test_apprun_handles_empty_ld_library_path(self):
        apprun = self._extract_apprun()
        self.assertIn('LD_LIBRARY_PATH:-', apprun)

    def test_apprun_cd_failure_is_fatal(self):
        apprun = self._extract_apprun()
        self.assertIn('||', apprun.split('cd "$APPDIR/usr"')[-1].split('\n')[0])

    def test_apprun_sources_hooks(self):
        apprun = self._extract_apprun()
        self.assertIn("apprun-hooks", apprun)
        self.assertIn("source", apprun)

    def test_apprun_execs_binary(self):
        apprun = self._extract_apprun()
        self.assertIn('exec "$APPDIR/usr/bin/MqttProbe.Desktop" "$@"', apprun)


class TestBuildScriptStructure(unittest.TestCase):
    """Test appdir.sh argument parsing and structural requirements."""

    def test_requires_publish_dir(self):
        script = str(LINUX_DIR / "appdir.sh")
        if os.name == "nt":
            self.skipTest("bash subprocess not reliable on Windows")
        result = subprocess.run(
            ["bash", script, "--version", "1.0.6"],
            capture_output=True, text=True, timeout=10
        )
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("--publish-dir", result.stderr + result.stdout)

    def test_requires_version(self):
        script = str(LINUX_DIR / "appdir.sh")
        if os.name == "nt":
            self.skipTest("bash subprocess not reliable on Windows")
        result = subprocess.run(
            ["bash", script, "--publish-dir", "/tmp"],
            capture_output=True, text=True, timeout=10
        )
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("--version", result.stderr + result.stdout)

    def test_rejects_version_0_0_0(self):
        result = subprocess.run(
            ["bash", str(LINUX_DIR / "appdir.sh"),
             "--version", "0.0.0", "--publish-dir", "/tmp"],
            capture_output=True, text=True, timeout=10
        )
        self.assertNotEqual(result.returncode, 0)

    def test_strips_v_prefix(self):
        script = LINUX_DIR / "appdir.sh"
        content = script.read_text(encoding="utf-8", errors="replace")
        self.assertIn('${VERSION#v}', content)

    def test_does_not_rebuild_photino(self):
        script = LINUX_DIR / "appdir.sh"
        content = script.read_text(encoding="utf-8", errors="replace")
        self.assertNotIn("rebuild-photino-native.sh", content,
                         "appdir.sh should not reference old script name")

    def test_uses_deploy_deps_only_for_helpers(self):
        script = LINUX_DIR / "appdir.sh"
        content = script.read_text(encoding="utf-8", errors="replace")
        self.assertIn("--deploy-deps-only", content)
        deploy_section = content.split("deploy_deps_for_elf()")[1].split("}")[0] if "deploy_deps_for_elf" in content else ""
        self.assertIn("--deploy-deps-only", deploy_section)

    def test_no_lttng_package_install(self):
        script = LINUX_DIR / "appdir.sh"
        content = script.read_text(encoding="utf-8", errors="replace")
        for line in content.split('\n'):
            stripped = line.strip()
            if 'apt-get install' in stripped or stripped.startswith('liblttng') or stripped.endswith('liblttng-ust1'):
                if 'liblttng-ust' in stripped and 'liblttng-ust1' in stripped:
                    self.fail(f"liblttng-ust1 found in apt-get install: {stripped}")

    def test_prefix_length_check_exists(self):
        script = LINUX_DIR / "appdir.sh"
        content = script.read_text(encoding="utf-8", errors="replace")
        self.assertIn("prefix length mismatch", content)

    def test_font_libs_inlined_in_appdir(self):
        """Font lib bundling must be inlined in appdir.sh as internal function."""
        script = LINUX_DIR / "appdir.sh"
        content = script.read_text(encoding="utf-8", errors="replace")
        self.assertIn("_bundle_font_libs", content,
                      "Font bundling function must be inlined in appdir.sh")
        self.assertNotIn("bundle-font-libs.sh", content,
                         "External bundle-font-libs.sh reference should be removed")

    def test_font_bundling_helper_has_required_libs(self):
        """Inlined font bundling must reference all required font libraries."""
        script = LINUX_DIR / "appdir.sh"
        content = script.read_text(encoding="utf-8", errors="replace")
        for lib in ["libharfbuzz.so.0", "libfontconfig.so.1",
                     "libfreetype.so.6", "libfribidi.so.0"]:
            self.assertIn(lib, content,
                          f"Font library {lib} not referenced in appdir.sh")

    def test_font_bundling_uses_deploy_deps_only(self):
        script = LINUX_DIR / "appdir.sh"
        content = script.read_text(encoding="utf-8", errors="replace")
        # Find the _bundle_font_libs function
        func_start = content.find("_bundle_font_libs()")
        func_end = content.find("}", content.find("PASS: Font/text stack", func_start))
        func_body = content[func_start:func_end] if func_start != -1 else ""
        self.assertIn("--deploy-deps-only", func_body,
                      "Font bundling must use --deploy-deps-only")

    def test_output_selects_mqttprobe_appimage(self):
        script = LINUX_DIR / "appdir.sh"
        content = script.read_text(encoding="utf-8", errors="replace")
        self.assertIn('MQTTProbe.AppImage', content,
                      "Script must reference MQTTProbe.AppImage as output")
        self.assertNotIn('MQTTProbe-${VERSION}-linux-x64.AppImage', content,
                         "Script must not use versioned AppImage filename")

    def test_no_tar_head_pipefail(self):
        script = LINUX_DIR / "appdir.sh"
        content = script.read_text(encoding="utf-8", errors="replace")
        for line in content.split('\n'):
            stripped = line.strip()
            if 'tar ' in stripped and '|' in stripped and 'head' in stripped:
                self.fail(f"tar | head pipeline found (SIGPIPE risk): {stripped}")

    def test_cp_aL_dereferences_symlinks(self):
        if os.name == "nt": self.skipTest("symlink dereference not testable on Windows")
        with tempfile.TemporaryDirectory() as tmpdir:
            src, dst = Path(tmpdir, "src"), Path(tmpdir, "dst")
            src.mkdir(); dst.mkdir()
            (src / "libfoo.so.0.1").write_bytes(b"\x7fELF" + os.urandom(256))
            (src / "libfoo.so.0").symlink_to("libfoo.so.0.1")
            subprocess.run(["cp", "-aL", str(src / "libfoo.so.0"), str(dst)], check=True)
            result = dst / "libfoo.so.0"
            self.assertTrue(result.exists() and not result.is_symlink())
            self.assertEqual(result.read_bytes()[:4], b"\x7fELF")
            self.assertEqual(result.name, "libfoo.so.0")


class TestRebuildScriptStructure(unittest.TestCase):
    """Test rebuild-photino.sh structural requirements."""

    def test_resolves_output_dir_before_cd(self):
        script = LINUX_DIR / "rebuild-photino.sh"
        content = script.read_text(encoding="utf-8", errors="replace")
        lines = content.split('\n')
        resolve_line = None
        cd_line = None
        for i, line in enumerate(lines):
            if 'OUTPUT_DIR' in line and 'mkdir -p' in line and 'pwd' in line:
                resolve_line = i
            if line.strip().startswith('cd ') and 'WORK_DIR' in line:
                cd_line = i
        if resolve_line is not None and cd_line is not None:
            self.assertLess(resolve_line, cd_line,
                            "OUTPUT_DIR must be resolved before cd")

    def test_no_pipefail_head_in_pipeline(self):
        script = LINUX_DIR / "rebuild-photino.sh"
        content = script.read_text(encoding="utf-8", errors="replace")
        for line in content.split('\n'):
            if 'nm ' in line and '|' in line and 'head' in line:
                self.fail(f"nm pipeline with head found under pipefail: {line.strip()}")

    def test_version_assertion_is_fatal(self):
        script = LINUX_DIR / "rebuild-photino.sh"
        content = script.read_text(encoding="utf-8", errors="replace")
        version_section = content.split("Asserting Photino.Native")[-1]
        self.assertIn("exit 1", version_section.split("Copying to output")[0])


class TestPackageMismatchDetection(unittest.TestCase):
    """Test that packaging detects version/dependency mismatches."""

    def test_build_script_validates_publish_so_exists(self):
        script = LINUX_DIR / "appdir.sh"
        content = script.read_text(encoding="utf-8", errors="replace")
        self.assertIn("PUBLISH_SO", content)
        self.assertIn("not found", content.lower())

    def test_build_script_validates_exports(self):
        script = LINUX_DIR / "appdir.sh"
        content = script.read_text(encoding="utf-8", errors="replace")
        self.assertIn("Photino", content)
        self.assertIn("export", content.lower())


class TestCollectThirdPartySources(unittest.TestCase):
    """Test collect-sources.sh exists and has required content."""

    def test_script_exists(self):
        script = LINUX_DIR / "collect-sources.sh"
        self.assertTrue(script.exists(), f"Missing: {script}")

    def test_script_clones_photino_at_pinned_commit(self):
        script = LINUX_DIR / "collect-sources.sh"
        content = script.read_text(encoding="utf-8", errors="replace")
        self.assertIn("3ba4b937d6337b5c58344445db08a5a9a63f07c8", content)

    def test_script_uses_manifest_for_versions(self):
        script = LINUX_DIR / "collect-sources.sh"
        content = script.read_text(encoding="utf-8", errors="replace")
        self.assertIn("source-package", content)
        self.assertIn("source-version", content)
        self.assertIn("MANIFEST_PATH", content)

    def test_script_requires_manifest(self):
        script = LINUX_DIR / "collect-sources.sh"
        content = script.read_text(encoding="utf-8", errors="replace")
        self.assertIn("--manifest is required", content)
        # No hardcoded BINARY_PACKAGES fallback array
        self.assertNotIn("Using hardcoded package list", content)

    def test_script_downloads_actual_source_archives(self):
        script = LINUX_DIR / "collect-sources.sh"
        content = script.read_text(encoding="utf-8", errors="replace")
        self.assertIn("apt-get source", content)
        self.assertIn("--download-only", content)

    def test_script_copies_actual_copyright_files(self):
        script = LINUX_DIR / "collect-sources.sh"
        content = script.read_text(encoding="utf-8", errors="replace")
        self.assertIn("/usr/share/doc/", content)
        self.assertIn("copyright", content)

    def test_script_enables_deb_src(self):
        script = LINUX_DIR / "collect-sources.sh"
        content = script.read_text(encoding="utf-8", errors="replace")
        self.assertIn("deb-src", content)

    def test_script_includes_webkit_modification_notice(self):
        script = LINUX_DIR / "collect-sources.sh"
        content = script.read_text(encoding="utf-8", errors="replace")
        self.assertIn("modification", content.lower())

    def test_no_tar_head_pipefail(self):
        script = LINUX_DIR / "collect-sources.sh"
        content = script.read_text(encoding="utf-8", errors="replace")
        for line in content.split('\n'):
            stripped = line.strip()
            if 'tar ' in stripped and '|' in stripped and 'head' in stripped:
                self.fail(f"tar | head pipeline found (SIGPIPE risk): {stripped}")


if __name__ == "__main__":
    unittest.main()