#!/usr/bin/env python3
"""Tests for scripts/ci/release-notes.py.

Run: python -m unittest discover -s scripts/ci/tests
"""

import importlib.util
import shutil
import tempfile
import unittest
from pathlib import Path

_SPEC = importlib.util.spec_from_file_location(
    "ci_release_notes",
    Path(__file__).resolve().parents[1] / "release-notes.py",
)
if _SPEC is None or _SPEC.loader is None:
    raise SystemExit("Cannot load scripts/ci/release-notes.py")
release_notes = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(release_notes)

VERSION = "v1.2.3"
BARE = "1.2.3"

MANIFEST = f"""
# a comment, and a blank line follow

mqttprobe-web-linux-x64-{{{{VERSION}}}}.zip
mqttprobe-android-{{{{VERSION}}}}.apk
docker-note-{{{{VERSION_NUM}}}}.txt
velopack-linux/*
!*/package-manifest.txt
"""

NOTES = """
## MQTT Probe v1.2.3

| Artifact | Description |
|----------|-------------|
| `mqttprobe-web-linux-x64-v1.2.3.zip` | Web app. |
| `mqttprobe-android-v1.2.3.apk` | Android app. |
| `MQTTProbe.AppImage` | Linux desktop app. |
| `docker pull bluegrassiot/mqttprobe:1.2.3` | Docker image. |
| `libwebkit2gtk-4.1-0` | Needed by the portable zip. |

Ship the docker-note-1.2.3.txt licence bundle alongside them.

Needs `libwebkit2gtk-4.1-0` on Linux.
"""


class Fixture:
    """A temp root holding the manifest, a notes file, and fake built assets."""

    def __init__(self, test: unittest.TestCase, manifest: str = MANIFEST,
                 notes: str = NOTES, assets: dict[str, str] | None = None) -> None:
        self.root = Path(tempfile.mkdtemp())
        test.addCleanup(shutil.rmtree, self.root, True)
        (self.root / "manifest.txt").write_text(manifest, encoding="utf-8")
        (self.root / "notes.md").write_text(notes, encoding="utf-8")
        for rel in (assets if assets is not None else {}):
            self.write(rel)

    def write(self, rel: str, content: str = "x") -> Path:
        path = self.root / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")
        return path

    @property
    def manifest(self) -> Path:
        return self.root / "manifest.txt"

    @property
    def notes(self) -> Path:
        return self.root / "notes.md"

    def resolve(self):
        return release_notes.resolve(self.manifest, VERSION, self.root)


DEFAULT_ASSETS = {
    "mqttprobe-web-linux-x64-v1.2.3.zip": "",
    "mqttprobe-android-v1.2.3.apk": "",
    "docker-note-1.2.3.txt": "",
    "velopack-linux/MQTTProbe.AppImage": "",
    "velopack-linux/releases.linux.json": "",
    "velopack-linux/package-manifest.txt": "",
}


class TestSubstitute(unittest.TestCase):
    def test_strips_v_only_from_the_bare_placeholder(self) -> None:
        out = release_notes.substitute("{{VERSION}} {{VERSION_NUM}}", VERSION)
        self.assertEqual(out, "v1.2.3 1.2.3")

    def test_accepts_a_tag_given_without_the_v(self) -> None:
        out = release_notes.substitute("{{VERSION}} {{VERSION_NUM}}", BARE)
        self.assertEqual(out, "1.2.3 1.2.3")


class TestResolve(unittest.TestCase):
    def test_expands_globs_and_substitutes_both_placeholders(self) -> None:
        found, unmatched = Fixture(self, assets=DEFAULT_ASSETS).resolve()
        self.assertEqual(unmatched, [])
        self.assertIn("mqttprobe-web-linux-x64-v1.2.3.zip", found)
        self.assertIn("docker-note-1.2.3.txt", found)

    def test_exclusions_apply_after_every_include(self) -> None:
        found, _ = Fixture(self, assets=DEFAULT_ASSETS).resolve()
        self.assertIn("velopack-linux/MQTTProbe.AppImage", found)
        self.assertNotIn("velopack-linux/package-manifest.txt", found)

    def test_reports_patterns_that_match_nothing(self) -> None:
        _, unmatched = Fixture(self, assets={}).resolve()
        self.assertIn(f"mqttprobe-web-linux-x64-{VERSION}.zip", unmatched)
        self.assertIn("velopack-linux/*", unmatched)

    def test_ignores_comments_and_blank_lines(self) -> None:
        found, unmatched = Fixture(self, assets=DEFAULT_ASSETS).resolve()
        self.assertEqual(unmatched, [])
        self.assertFalse(any("comment" in f for f in found))


class TestTableTokens(unittest.TestCase):
    def test_collects_artifact_suffixes_only(self) -> None:
        tokens = release_notes.table_tokens(NOTES)
        self.assertEqual(tokens, [
            "mqttprobe-web-linux-x64-v1.2.3.zip",
            "mqttprobe-android-v1.2.3.apk",
            "MQTTProbe.AppImage",
        ])

    def test_ignores_backticked_text_outside_the_table(self) -> None:
        notes = "Install `mqttprobe-desktop-linux-x64-v1.2.3.zip` first.\n"
        self.assertEqual(release_notes.table_tokens(notes), [])

    def test_matches_a_documented_glob(self) -> None:
        notes = "| `MQTTProbe-osx-*.pkg` | macOS. |\n"
        self.assertEqual(release_notes.table_tokens(notes), ["MQTTProbe-osx-*.pkg"])


class TestCheckCoverage(unittest.TestCase):
    def test_passes_when_every_shipped_artifact_is_documented(self) -> None:
        found, _ = Fixture(self, assets=DEFAULT_ASSETS).resolve()
        self.assertEqual(release_notes.check_coverage(NOTES, found), [])

    def test_flags_an_uploaded_artifact_missing_from_the_notes(self) -> None:
        manifest = MANIFEST + "\nextra/*\n"
        fixture = Fixture(
            self,
            manifest=manifest,
            assets={**DEFAULT_ASSETS, "extra/undocumented-tool.zip": ""},
        )
        found, _ = fixture.resolve()
        problems = release_notes.check_coverage(NOTES, found)
        self.assertTrue(
            any("undocumented-tool.zip" in p and "never mentioned" in p
                for p in problems),
            problems,
        )

    def test_does_not_require_metadata_to_be_documented(self) -> None:
        found, _ = Fixture(self, assets=DEFAULT_ASSETS).resolve()
        problems = release_notes.check_coverage(NOTES, found)
        self.assertFalse(any("releases.linux.json" in p for p in problems), problems)

    def test_flags_a_table_row_that_was_never_shipped(self) -> None:
        notes = NOTES + "\n| `mqttprobe-desktop-linux-x64-v1.2.3.zip` | Linux. |\n"
        found, _ = Fixture(self, assets=DEFAULT_ASSETS).resolve()
        problems = release_notes.check_coverage(notes, found)
        self.assertTrue(
            any("no uploaded asset matches" in p for p in problems), problems
        )


class TestLeakedExpressions(unittest.TestCase):
    def test_finds_a_github_expression_left_in_the_notes(self) -> None:
        text = "| `mqttprobe-web-linux-x64-${{ github.ref_name }}.zip` | Web. |\n"
        self.assertEqual(
            release_notes.find_leaked_expressions(text),
            ["${{ github.ref_name }}"],
        )

    def test_ignores_correct_placeholders_and_dollar_amounts(self) -> None:
        text = "Costs $5. {{VERSION}} and {{VERSION_NUM}}.\n"
        self.assertEqual(release_notes.find_leaked_expressions(text), [])


if __name__ == "__main__":
    unittest.main()
