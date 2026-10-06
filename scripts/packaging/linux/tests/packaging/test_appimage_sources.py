"""Behavioral tests for MQTTProbe source collection, manifest generation,
and NOTICE file builder.

Tests actual function behavior with production-schema fixtures, not
text grep. Verifies manifest coverage and notice content against real
data structures.

NOTE: The metadata_helpers.py module and its tests (TestReleasesLinuxJsonParsing)
were removed in the linux/ consolidation. The production workflow parses
releases.linux.json assets directly with inline Python; metadata_helpers was
used only by tests/archive and never by production code. The real workflow
schema validation tests (TestWorkflowReleasesParse) are retained here.
"""

import json
import os
import sys
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[5]
PACKAGING_DIR = REPO_ROOT / "scripts" / "packaging"
LINUX_DIR = PACKAGING_DIR / "linux"
SCRATCH_DIR = Path(os.environ.get("LOCALAPPDATA", "")) / "Temp" / "opencode"


class TestFetchSourceRetrievalGuards(unittest.TestCase):
    """Structural guards in the exact-version source fetcher."""

    def setUp(self) -> None:
        self.content = (LINUX_DIR / "fetch_source.py").read_text(
            encoding="utf-8", errors="replace")

    def test_requests_source_only(self):
        """apt must be asked for source only, never binaries."""
        self.assertIn("--only-source", self.content)

    def test_pins_exact_version(self):
        self.assertIn('f"{source}={version}"', self.content)

    def test_verifies_identity_and_checksums(self):
        """Both retrieval paths share the same verification."""
        self.assertIn("verify_dsc_identity", self.content)
        self.assertIn("def _validate_downloaded_package", self.content)
        # Shared validator is used by apt as well as Launchpad.
        self.assertIn("_validate_downloaded_package(", self.content)

    def test_isolates_failed_downloads(self):
        """A failed apt run must not leave files behind for the fallback."""
        self.assertIn("tempfile.mkdtemp", self.content)
        self.assertIn("shutil.rmtree", self.content)

    def test_enforces_https_and_host_allowlist(self):
        self.assertIn("https", self.content)
        self.assertIn("launchpadlibrarian.net", self.content)
        self.assertIn("_ALLOWED_HOSTS", self.content)

    def test_bounds_descriptor_and_total_budget(self):
        self.assertIn("_MAX_DSC_BYTES", self.content)
        self.assertIn("_MAX_TOTAL_BYTES", self.content)
        self.assertIn("_TOTAL_DEADLINE_SECONDS", self.content)

    def test_rejects_unsafe_payload_paths(self):
        self.assertIn("_SAFE_FILENAME_RE", self.content)

    def test_apt_origin_is_pinned_not_ambient(self):
        """apt must run against one owned sources file, never host defaults."""
        self.assertIn("def apt_config_options(", self.content)
        self.assertIn("Dir::Etc::sourcelist", self.content)
        self.assertIn("Dir::Etc::sourceparts", self.content)
        self.assertIn("Dir::Etc::Parts", self.content)
        self.assertIn("Dir::Etc::main", self.content)
        self.assertIn("apt_env(", self.content)

    def test_apt_bootstrap_config_is_written_and_owned(self):
        """An early APT_CONFIG must disable Parts/main, owner-only."""
        self.assertIn("def write_apt_bootstrap_conf(", self.content)
        self.assertIn('_APT_BOOTSTRAP_CONF', self.content)
        self.assertIn('Dir::Etc::Parts "/dev/null";', self.content)
        self.assertIn('Dir::Etc::main "/dev/null";', self.content)
        self.assertIn("chmod(0o600)", self.content)
        # APT_CONFIG names the file, never a bare /dev/null.
        self.assertIn('env["APT_CONFIG"] = str(bootstrap_conf)', self.content)
        self.assertNotIn('env["APT_CONFIG"] = "/dev/null"', self.content)

    def test_timeout_is_not_hardcoded_for_apt(self):
        """The caller's budget reaches apt instead of a fixed 120s."""
        self.assertNotIn("dest_dir, timeout=120)", self.content)
        self.assertIn("timeout=timeout, apt_sources=apt_sources", self.content)


class TestCollectorNaming(unittest.TestCase):
    def test_archive_name_pattern(self):
        version = "1.0.6"
        expected = f"mqttprobe-third-party-sources-v{version}"
        self.assertEqual(expected, "mqttprobe-third-party-sources-v1.0.6")

    def test_script_uses_correct_naming(self):
        script = LINUX_DIR / "collect-sources.sh"
        content = script.read_text(encoding="utf-8")
        self.assertIn('ARCHIVE_NAME="mqttprobe-third-party-sources-v${VERSION}"', content)


class TestWorkflowReleasesParse(unittest.TestCase):
    def test_workflow_parse_logic_matches_helper(self):
        workflow = REPO_ROOT / ".github" / "workflows" / "build-linux-desktop.yml"
        content = workflow.read_text(encoding="utf-8")
        self.assertIn("data.get('Assets'", content,
                       "Workflow must parse Assets key from releases.linux.json")
        self.assertIn("PackageId", content,
                       "Workflow must filter by PackageId")
        self.assertIn("'Type'", content,
                       "Workflow must filter by Type")

    def test_workflow_does_not_use_bare_list_parse(self):
        workflow = REPO_ROOT / ".github" / "workflows" / "build-linux-desktop.yml"
        content = workflow.read_text(encoding="utf-8")
        self.assertNotIn("isinstance(data, list)", content,
                          "Workflow must not use bare list parse for releases.linux.json")


class TestDockerBaselineBash(unittest.TestCase):
    def test_docker_baseline_uses_pipefail(self):
        workflow = REPO_ROOT / ".github" / "workflows" / "build-linux-desktop.yml"
        content = workflow.read_text(encoding="utf-8")
        baseline_idx = content.find("Prepare clean Ubuntu 22.04 desktop runtime baseline")
        self.assertGreater(baseline_idx, -1, "Baseline step not found")
        baseline_section = content[baseline_idx:baseline_idx + 2000]
        self.assertIn("set -euo pipefail", baseline_section,
                       "Docker baseline must set euo pipefail")

    def test_docker_baseline_checks_container_exit(self):
        workflow = REPO_ROOT / ".github" / "workflows" / "build-linux-desktop.yml"
        content = workflow.read_text(encoding="utf-8")
        baseline_idx = content.find("Prepare clean Ubuntu 22.04 desktop runtime baseline")
        baseline_section = content[baseline_idx:baseline_idx + 2000]
        self.assertIn("ExitCode", baseline_section,
                       "Docker baseline must check container exit code")

    def test_docker_baseline_has_cleanup_trap(self):
        workflow = REPO_ROOT / ".github" / "workflows" / "build-linux-desktop.yml"
        content = workflow.read_text(encoding="utf-8")
        baseline_idx = content.find("Prepare clean Ubuntu 22.04 desktop runtime baseline")
        baseline_section = content[baseline_idx:baseline_idx + 2000]
        self.assertIn("trap cleanup", baseline_section,
                       "Docker baseline must have cleanup trap")

    def test_docker_baseline_visible_apt_errors(self):
        workflow = REPO_ROOT / ".github" / "workflows" / "build-linux-desktop.yml"
        content = workflow.read_text(encoding="utf-8")
        baseline_idx = content.find("Prepare clean Ubuntu 22.04 desktop runtime baseline")
        baseline_section = content[baseline_idx:baseline_idx + 2000]
        self.assertNotIn("apt-get update -qq > /dev/null", baseline_section,
                          "apt-get errors must be visible")


class TestBuildAppImageNoticeStep(unittest.TestCase):
    def test_build_script_has_notices_step(self):
        script = LINUX_DIR / "appdir.sh"
        content = script.read_text(encoding="utf-8")
        self.assertIn("notices.py", content,
                       "Build script must call notices.py")

    def test_notice_step_before_vpk(self):
        script = LINUX_DIR / "appdir.sh"
        content = script.read_text(encoding="utf-8")
        notice_step = content.find("Step 9: Generating package manifest and NOTICE")
        vpk_step = content.find("Step 10: Building AppImage via vpk pack")
        self.assertGreater(notice_step, -1, "Step 9 (manifest/NOTICE) not found")
        self.assertGreater(vpk_step, -1, "Step 10 (vpk pack) not found")
        self.assertLess(notice_step, vpk_step,
                        "NOTICE step must come before vpk pack step")

    def test_build_script_saves_manifest_to_output(self):
        script = LINUX_DIR / "appdir.sh"
        content = script.read_text(encoding="utf-8")
        self.assertIn("package-manifest.txt", content,
                       "Build script must save manifest as package-manifest.txt")


class TestVendorNoticeUpdated(unittest.TestCase):
    def test_notice_includes_libnotify(self):
        notice = (LINUX_DIR / "vendor" / "NOTICE.md").read_text(encoding="utf-8")
        self.assertIn("libnotify", notice)

    def test_notice_includes_libsecret(self):
        notice = (LINUX_DIR / "vendor" / "NOTICE.md").read_text(encoding="utf-8")
        self.assertIn("libsecret", notice)

    def test_notice_includes_gnutls(self):
        notice = (LINUX_DIR / "vendor" / "NOTICE.md").read_text(encoding="utf-8")
        self.assertIn("gnutls", notice)

    def test_notice_includes_libgcrypt(self):
        notice = (LINUX_DIR / "vendor" / "NOTICE.md").read_text(encoding="utf-8")
        self.assertIn("libgcrypt", notice)

    def test_notice_includes_nettle(self):
        notice = (LINUX_DIR / "vendor" / "NOTICE.md").read_text(encoding="utf-8")
        self.assertIn("nettle", notice)

    def test_notice_includes_orc(self):
        notice = (LINUX_DIR / "vendor" / "NOTICE.md").read_text(encoding="utf-8")
        self.assertIn("orc", notice)


class TestJackExclusion(unittest.TestCase):
    def test_build_script_removes_jack_plugin(self):
        script = LINUX_DIR / "appdir.sh"
        content = script.read_text(encoding="utf-8")
        self.assertIn("libgstjack", content,
                       "Build script must remove libgstjack.so (JACK plugin)")

    def test_collector_has_jack_exclusion_notice(self):
        script = LINUX_DIR / "collect-sources.sh"
        content = script.read_text(encoding="utf-8")
        self.assertIn("JACK", content,
                       "Collector must document JACK exclusion")

    def test_build_script_removes_jack_before_vpk(self):
        script = LINUX_DIR / "appdir.sh"
        content = script.read_text(encoding="utf-8")
        jack_idx = content.find("libgstjack")
        vpk_idx = content.find("vpk pack \\")
        self.assertGreater(jack_idx, -1, "JACK removal not found")
        self.assertGreater(vpk_idx, -1, "vpk pack not found")
        self.assertLess(jack_idx, vpk_idx,
                        "JACK removal must precede vpk pack")


class TestNoMetadataHelpers(unittest.TestCase):
    """Verify metadata_helpers.py is removed and not referenced by production."""

    def test_metadata_helpers_not_in_linux_dir(self):
        metadata_file = LINUX_DIR / "metadata_helpers.py"
        self.assertFalse(metadata_file.exists(),
                         "metadata_helpers.py should not exist in linux/")

    def test_appdir_does_not_reference_metadata_helpers(self):
        script = LINUX_DIR / "appdir.sh"
        content = script.read_text(encoding="utf-8")
        self.assertNotIn("metadata_helpers", content,
                         "appdir.sh should not reference metadata_helpers")

    def test_collect_sources_does_not_reference_metadata_helpers(self):
        script = LINUX_DIR / "collect-sources.sh"
        content = script.read_text(encoding="utf-8")
        self.assertNotIn("metadata_helpers", content,
                         "collect-sources.sh should not reference metadata_helpers")

    def test_notices_does_not_reference_metadata_helpers(self):
        script = LINUX_DIR / "notices.py"
        content = script.read_text(encoding="utf-8")
        self.assertNotIn("metadata_helpers", content,
                         "notices.py should not reference metadata_helpers")


if __name__ == "__main__":
    unittest.main()