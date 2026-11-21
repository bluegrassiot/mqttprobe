#!/usr/bin/env python3
"""Source-contract tests for the manual TestFlight coordinator workflow.

Run: python -m unittest discover -s scripts/ci/tests
"""

import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
WORKFLOW = ROOT / ".github" / "workflows" / "testflight.yml"
IOS_WORKFLOW = ROOT / ".github" / "workflows" / "build-ios.yml"
MAC_WORKFLOW = ROOT / ".github" / "workflows" / "build-macos-app-store.yml"

IOS_CALL = "uses: ./.github/workflows/build-ios.yml"
MAC_CALL = "uses: ./.github/workflows/build-macos-app-store.yml"
WITH_BLOCK = (
    "    with:\n"
    "      version: ${{ inputs.version }}\n"
    "      build_number: ${{ inputs.build_number }}"
)


class TestFlightCoordinatorTests(unittest.TestCase):
    def setUp(self):
        self.text = WORKFLOW.read_text(encoding="utf-8")

    def load_job(self, name):
        lines = self.text.splitlines()
        start = lines.index(f"  {name}:")
        block = [lines[start]]
        for line in lines[start + 1:]:
            if line.startswith("  ") and not line.startswith("   "):
                break
            block.append(line)
        return "\n".join(block)

    def test_dispatch_only_with_required_version_and_build_number(self):
        self.assertIn("  workflow_dispatch:", self.text)
        for trigger in (
            "push:",
            "pull_request:",
            "pull_request_target:",
            "schedule:",
            "release:",
            "workflow_run:",
            "workflow_call:",
        ):
            self.assertNotIn(f"\n  {trigger}", self.text)
        dispatch = self.text.split("  workflow_dispatch:", 1)[1].split("\njobs:", 1)[0]
        self.assertIn("      version:", dispatch)
        self.assertIn("      build_number:", dispatch)
        self.assertEqual(dispatch.count("required: true"), 2)
        self.assertIn("no tag is required", dispatch)

    def test_read_only_permissions_and_non_cancelling_concurrency(self):
        self.assertIn("permissions:\n  contents: read", self.text)
        self.assertIn("concurrency:", self.text)
        self.assertIn("cancel-in-progress: false", self.text)
        self.assertNotIn("contents: write", self.text)

    def test_calls_both_apple_workflows_with_the_same_explicit_build(self):
        jobs = {name: self.load_job(name) for name in ("ios", "macos")}
        self.assertIn(IOS_CALL, jobs["ios"])
        self.assertIn(MAC_CALL, jobs["macos"])
        for name, job in jobs.items():
            with self.subTest(job=name):
                self.assertIn(WITH_BLOCK, job)
                self.assertIn("      contents: read", job)
                self.assertIn("    secrets: inherit", job)
        self.assertNotIn("${{ secrets.", self.text)
        self.assertNotIn("build_number: ''", self.text)

    def test_publishes_no_artifact_or_release(self):
        for forbidden in (
            "actions/upload-artifact",
            "action-gh-release",
            "upload-release-asset",
            "gh release",
            "git tag",
            "git push",
            "create-release",
            "actions/create-release",
            "peter-evans/create-pull-request",
            "pull-requests:",
        ):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, self.text)

    def test_called_workflows_still_validate_source_themselves(self):
        for path in (IOS_WORKFLOW, MAC_WORKFLOW):
            text = path.read_text(encoding="utf-8")
            with self.subTest(workflow=path.name):
                self.assertIn("  workflow_call:", text)
                self.assertIn("needs: validate-source", text)
                self.assertIn("environment: ios-signing", text)


if __name__ == "__main__":
    unittest.main()
