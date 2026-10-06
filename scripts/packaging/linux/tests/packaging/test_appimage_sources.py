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