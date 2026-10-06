"""Tests for createdump pruning in appdir.sh.

Validates that the .NET createdump binary is removed during AppDir
assembly, and that the prune happens at the right point in the build
pipeline (before manifest generation and dependency scanning).
"""

import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[5]
PACKAGING_DIR = REPO_ROOT / "scripts" / "packaging"
LINUX_DIR = PACKAGING_DIR / "linux"


class TestCreatedumpPruning(unittest.TestCase):
    """createdump must be pruned from the AppDir before manifest/dependency steps."""

    def test_createdump_prune_present_and_idempotent(self):
        """appdir.sh must prune createdump with a guard so missing file is harmless."""
        script = LINUX_DIR / "appdir.sh"
        content = script.read_text(encoding="utf-8", errors="replace")
        self.assertIn("createdump", content, "createdump prune missing from appdir.sh")
        # Must guard with -f so re-running on an already-pruned dir is safe
        self.assertIn('if [[ -f "$CREATEDUMP" ]]', content,
                       "createdump prune must be guarded by -f test")

    def test_createdump_prune_before_manifest_step(self):
        """createdump must be removed BEFORE Step 9 manifest generation."""
        script = LINUX_DIR / "appdir.sh"
        content = script.read_text(encoding="utf-8", errors="replace")
        lines = content.split('\n')
        prune_line = None
        manifest_line = None
        for i, line in enumerate(lines):
            if 'CREATEDUMP' in line and 'rm -f' in line:
                prune_line = i
            if 'Step 9' in line and 'manifest' in line.lower():
                manifest_line = i
        self.assertIsNotNone(prune_line, "createdump rm not found")
        self.assertIsNotNone(manifest_line, "Step 9 manifest line not found")
        self.assertLess(prune_line, manifest_line,
                        "createdump prune must precede Step 9 manifest")

    def test_createdump_prune_before_deploy_deps(self):
        """createdump must be removed BEFORE dependency scanning starts."""
        script = LINUX_DIR / "appdir.sh"
        lines = script.read_text(encoding="utf-8", errors="replace").split('\n')
        prune_line = None
        deploy_line = None
        for i, line in enumerate(lines):
            if 'CREATEDUMP' in line and 'rm -f' in line:
                prune_line = i
            if 'deploy_deps_for_elf' in line and 'function' not in line and '#' not in line.split('deploy_deps_for_elf')[0]:
                if deploy_line is None:
                    deploy_line = i
        self.assertIsNotNone(prune_line, "createdump rm not found")
        self.assertIsNotNone(deploy_line, "deploy_deps_for_elf call not found")
        self.assertLess(prune_line, deploy_line,
                        "createdump prune must precede dependency scanning")


if __name__ == "__main__":
    unittest.main()