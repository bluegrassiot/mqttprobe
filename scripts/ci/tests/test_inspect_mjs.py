#!/usr/bin/env python3
"""Regression tests for the ES-module DevSkim pass and its namespace exception.

Run: python -m unittest discover -s scripts/ci/tests
"""

import fnmatch
import hashlib
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

_SPEC = importlib.util.spec_from_file_location(
    "ci_inspect", Path(__file__).resolve().parents[1] / "inspect.py",
)
if _SPEC is None or _SPEC.loader is None:
    raise SystemExit("Cannot load scripts/ci/inspect.py")
inspect_ci = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(inspect_ci)

ENTRY = inspect_ci.VENDOR_NAMESPACE_EXCEPTIONS[0]
UNAPPROVED_URL = "http://unapproved.example.com/"  # DevSkim: ignore DS137138


def finding(line, rule=ENTRY["rule"], file=ENTRY["file"]):
    return {
        "rule": rule,
        "file": file,
        "line": line,
        "level": "warning",
        "message": "",
        "snippet": "",
    }


def in_mjs_scope(path):
    # DevSkim's ** matches zero directories too, as Python's fnmatch does not.
    pattern = inspect_ci.MJS_GLOB
    return (fnmatch.fnmatch(path, pattern)
            or fnmatch.fnmatch(path, pattern.removeprefix("**/")))


class TestDevSkimMjsPass(unittest.TestCase):
    """The second pass must cover every ES module without changing other scans."""

    def test_scan_scope_is_every_es_module(self):
        self.assertEqual(inspect_ci.MJS_GLOB, "**/*.mjs")
        for path in (
            ENTRY["file"],
            "src/MqttProbe.UI/wwwroot/js/chart-renderer.mjs",
            "handwritten.mjs",
        ):
            self.assertTrue(in_mjs_scope(path), path)

    def test_languages_table_maps_only_mjs(self):
        languages = json.loads(inspect_ci.MJS_LANGUAGES.read_text(encoding="utf-8"))
        self.assertEqual(
            languages,
            [{"name": "javascript", "extensions": [".mjs"]}],
            "the custom table replaces DevSkim's defaults, so it must stay scoped to .mjs",
        )

    def test_comments_table_covers_the_language(self):
        comments = json.loads(inspect_ci.MJS_COMMENTS.read_text(encoding="utf-8"))
        covered = {name for entry in comments for name in entry["language"]}
        self.assertIn("javascript", covered)

    def test_two_passes_share_exclusions_and_write_separate_reports(self):
        sarif = Path("artifacts/inspect-devskim.sarif")
        passes = inspect_ci.devskim_commands(sarif)
        self.assertEqual(len(passes), 2)
        (tree_label, tree_cmd, tree_sarif), (mjs_label, mjs_cmd, mjs_sarif) = passes

        self.assertEqual(tree_label, "devskim (security patterns)")
        self.assertEqual(mjs_label, "devskim (ECMAScript modules)")
        self.assertEqual(tree_sarif, sarif)
        self.assertNotEqual(tree_sarif, mjs_sarif)
        self.assertEqual(mjs_sarif.name, "inspect-devskim-mjs.sarif")

        for cmd in (tree_cmd, mjs_cmd):
            self.assertIn("-g", cmd)
            start = cmd.index("-g") + 1
            self.assertEqual(
                cmd[start:start + len(inspect_ci.DEVSKIM_GLOBS)],
                inspect_ci.DEVSKIM_GLOBS,
                "ignore globs must not be dropped from either pass",
            )
            self.assertNotIn("--severity", cmd)
            self.assertNotIn("-s", cmd)

        self.assertNotIn("--include-globs", tree_cmd)
        self.assertNotIn("--languages", tree_cmd)
        index = mjs_cmd.index("--include-globs")
        self.assertEqual(mjs_cmd[index + 1], inspect_ci.MJS_GLOB)
        self.assertEqual(
            mjs_cmd[mjs_cmd.index("--languages") + 1], str(inspect_ci.MJS_LANGUAGES))
        self.assertEqual(
            mjs_cmd[mjs_cmd.index("--comments") + 1], str(inspect_ci.MJS_COMMENTS))


class TestVendorNamespaceRealBundle(unittest.TestCase):
    """The shipped bundle must match the pinned digest and namespace set."""

    def test_vendored_file_is_scanned(self):
        self.assertTrue(in_mjs_scope(ENTRY["file"]))
        for glob in inspect_ci.DEVSKIM_GLOBS:
            self.assertFalse(fnmatch.fnmatch(ENTRY["file"], glob), glob)

    def test_vendored_line_is_accepted(self):
        f = finding(ENTRY["line"])
        self.assertTrue(
            inspect_ci.is_vendor_namespace_exception(f),
            f"vendored line not accepted: {ENTRY['rule']} {ENTRY['file']}:{ENTRY['line']}",
        )

    def test_entry_pins_the_upstream_digest(self):
        self.assertRegex(ENTRY["sha256"], r"^[0-9a-f]{64}$")
        actual = hashlib.sha256(
            (inspect_ci.ROOT / ENTRY["file"]).read_bytes()).hexdigest()
        self.assertEqual(ENTRY["sha256"], actual)

    def test_entry_limits_rule_span_and_urls(self):
        self.assertEqual(ENTRY["rule"], "DS137138")
        self.assertEqual(ENTRY["file"],
                         "src/MqttProbe.UI/wwwroot/js/echarts/6.1.0/echarts.esm.min.mjs")
        self.assertGreaterEqual(ENTRY["line"], 1)
        self.assertEqual(
            ENTRY["urls"],
            {
                "http://www.w3.org/2000/svg",  # DevSkim: ignore DS137138 XML namespace
                "http://www.w3.org/1999/xlink",  # DevSkim: ignore DS137138 XML namespace
                "http://www.w3.org/2000/xmlns/",  # DevSkim: ignore DS137138 XML namespace
                "http://www.w3.org/XML/1998/namespace",  # DevSkim: ignore DS137138 XML namespace
            },
        )


class TestNamespaceUrlTokens(unittest.TestCase):
    """Suffix punctuation stays inside the token, so it can never equal a namespace."""

    SVG = "http://www.w3.org/2000/svg"  # DevSkim: ignore DS137138 XML namespace

    def test_suffixes_are_kept(self):
        for suffix in (";telemetry", ",telemetry", ")"):
            with self.subTest(suffix=suffix):
                self.assertEqual(
                    inspect_ci.namespace_url_tokens(f'"{self.SVG}{suffix}"'),
                    {f"{self.SVG}{suffix}"},
                )

    def test_plain_quoted_namespace_is_extracted(self):
        self.assertEqual(
            inspect_ci.namespace_url_tokens(f'"{self.SVG}"'), {self.SVG})

    def test_real_line_yields_exactly_the_approved_set(self):
        lines = (inspect_ci.ROOT / ENTRY["file"]).read_text(
            encoding="utf-8").splitlines()
        self.assertEqual(
            inspect_ci.namespace_url_tokens(lines[ENTRY["line"] - 1]), ENTRY["urls"])


class TestVendorNamespaceException(unittest.TestCase):
    """Fail closed on any byte change, fetch call, or unapproved URL."""

    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        original_root = inspect_ci.ROOT
        self.addCleanup(setattr, inspect_ci, "ROOT", original_root)
        setattr(inspect_ci, "ROOT", Path(tmp.name))

    def _write(self, line_text, file=ENTRY["file"]):
        lines = ["// minified bundle"] * (ENTRY["line"] - 1) + [line_text]
        path = inspect_ci.ROOT / file
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("\n".join(lines) + "\n", encoding="utf-8")
        return path

    def _pin_fixture_hash(self, path):
        original = ENTRY["sha256"]
        self.addCleanup(ENTRY.__setitem__, "sha256", original)
        ENTRY["sha256"] = hashlib.sha256(path.read_bytes()).hexdigest()

    def _approved_line(self, urls=None):
        urls = sorted(ENTRY["urls"]) if urls is None else urls
        return "var a=" + ",".join(f'"{url}"' for url in urls) + ";"

    def test_namespace_only_line_is_accepted_when_pinned(self):
        self._pin_fixture_hash(self._write(self._approved_line()))
        self.assertTrue(inspect_ci.is_vendor_namespace_exception(finding(ENTRY["line"])))

    def test_modified_bundle_blocks_even_with_approved_line(self):
        self._write(self._approved_line())
        self.assertFalse(inspect_ci.is_vendor_namespace_exception(finding(ENTRY["line"])))

    def test_byte_change_outside_the_line_blocks(self):
        source = Path(__file__).resolve().parents[3] / ENTRY["file"]
        path = inspect_ci.ROOT / ENTRY["file"]
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(source.read_bytes() + b"\n")
        self.assertFalse(inspect_ci.is_vendor_namespace_exception(finding(ENTRY["line"])))

    def test_fetch_call_blocks_when_pinned(self):
        svg = sorted(ENTRY["urls"])[0]
        self._pin_fixture_hash(self._write(f'fetch("{svg}")'))
        self.assertFalse(inspect_ci.is_vendor_namespace_exception(finding(ENTRY["line"])))

    def test_suffix_on_a_namespace_blocks_when_pinned(self):
        svg = sorted(ENTRY["urls"])[0]
        self._pin_fixture_hash(self._write(f'fetch("{svg};telemetry")'))
        self.assertFalse(inspect_ci.is_vendor_namespace_exception(finding(ENTRY["line"])))

    def test_extra_url_blocks_when_pinned(self):
        urls = [*sorted(ENTRY["urls"]), UNAPPROVED_URL]
        self._pin_fixture_hash(self._write(self._approved_line(urls)))
        self.assertFalse(inspect_ci.is_vendor_namespace_exception(finding(ENTRY["line"])))

    def test_missing_namespace_blocks_when_pinned(self):
        urls = sorted(ENTRY["urls"])[1:]
        self._pin_fixture_hash(self._write(self._approved_line(urls)))
        self.assertFalse(inspect_ci.is_vendor_namespace_exception(finding(ENTRY["line"])))

    def test_handwritten_es_module_blocks_when_pinned(self):
        other = "src/MqttProbe.UI/wwwroot/js/chart-renderer.mjs"
        self._pin_fixture_hash(self._write(self._approved_line(), file=other))
        self.assertFalse(
            inspect_ci.is_vendor_namespace_exception(finding(ENTRY["line"], file=other)))

    def test_wrong_rule_line_or_file_blocks(self):
        self._pin_fixture_hash(self._write(self._approved_line()))
        self.assertFalse(inspect_ci.is_vendor_namespace_exception(
            finding(ENTRY["line"], rule="DS999999")))
        self.assertFalse(inspect_ci.is_vendor_namespace_exception(finding(1)))
        self.assertFalse(inspect_ci.is_vendor_namespace_exception(
            finding(ENTRY["line"], file="src/MqttProbe.UI/wwwroot/js/other.mjs")))

    def test_missing_file_fails_closed(self):
        self.assertFalse(inspect_ci.is_vendor_namespace_exception(finding(ENTRY["line"])))


if __name__ == "__main__":
    unittest.main()
