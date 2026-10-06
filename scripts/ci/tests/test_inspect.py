#!/usr/bin/env python3
"""Regression tests for the gated DevSkim exception in scripts/ci/inspect.py.

Run: python -m unittest discover -s scripts/ci/tests
"""

import importlib.util
import json
import subprocess
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

EXCEPTION = inspect_ci.GATED_EXCEPTIONS[0]
RULE = EXCEPTION["rule"]
REALM = EXCEPTION["file"]
APPROVED = EXCEPTION["value"]
OIDC_BACKCHANNEL_SUFFIX = "/oidc/backchannel-logout"
UNAPPROVED_URL = "http://unapproved.example.com/"  # DevSkim: ignore DS137138


def finding(line, rule=RULE, file=REALM):
    return {
        "rule": rule,
        "file": file,
        "line": line,
        "level": "warning",
        "message": "",
        "snippet": "",
    }


class TestParsePropertyLine(unittest.TestCase):
    """The line check is exact but for whitespace-level JSON normalisation."""

    def test_normalises_indent_colon_spacing_and_trailing_comma(self):
        expected = inspect_ci.parse_property_line(APPROVED)
        self.assertIsNotNone(expected)
        self.assertEqual(inspect_ci.parse_property_line("    " + APPROVED), expected)
        self.assertEqual(inspect_ci.parse_property_line(APPROVED + ","), expected)
        self.assertEqual(inspect_ci.parse_property_line(APPROVED.replace(": ", ":")), expected)

    def test_rejects_anything_that_is_not_exactly_one_property(self):
        approved = inspect_ci.parse_property_line(APPROVED)
        self.assertIsNone(inspect_ci.parse_property_line(""))
        self.assertIsNone(inspect_ci.parse_property_line(APPROVED + ", " + APPROVED))
        self.assertIsNone(inspect_ci.parse_property_line(APPROVED + ', "flag": "true"'))
        self.assertIsNone(inspect_ci.parse_property_line('{"attributes": ' + APPROVED + "}"))
        # Key and value split across lines: the URL line alone is not a property.
        self.assertIsNone(inspect_ci.parse_property_line(json.dumps(approved[1])))
        self.assertNotEqual(inspect_ci.parse_property_line('"flag": "true"'), approved)


class TestCurrentRealm(unittest.TestCase):
    """The shipped realm.json must match the value the gate approves."""

    def test_approved_property_is_accepted(self):
        text = (inspect_ci.ROOT / REALM).read_text(encoding="utf-8")
        approved = inspect_ci.parse_property_line(APPROVED)
        self.assertIsNotNone(approved)
        matches = [
            number for number, line in enumerate(text.splitlines(), 1)
            if inspect_ci.parse_property_line(line) == approved
        ]
        self.assertEqual(
            len(matches), 1,
            f"realm.json must carry the approved property exactly once; "
            f"GATED_EXCEPTIONS and {REALM} move together ({approved[1]})",
        )
        self.assertTrue(inspect_ci.is_gated_exception(finding(matches[0])))

        occurrences, redefined = inspect_ci.json_property_occurrences(text, approved[0])
        self.assertEqual(occurrences, [approved[1]])
        self.assertFalse(redefined)


class TestGatedExceptionValue(unittest.TestCase):
    """The approved value targets the shared backchannel path."""

    def test_approved_value_targets_oidc_backchannel_path(self):
        approved = inspect_ci.parse_property_line(APPROVED)
        self.assertIsNotNone(approved)
        self.assertTrue(
            approved[1].endswith(OIDC_BACKCHANNEL_SUFFIX),
            f"approved value should end with {OIDC_BACKCHANNEL_SUFFIX}: {approved[1]}",
        )


class TestGatedException(unittest.TestCase):
    """Fixture realms under a temporary root, so each case controls the file."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        # setattr: ROOT lives on a dynamically loaded module, so point it at a
        # temporary repo root for each case and put it back afterwards.
        original_root = inspect_ci.ROOT
        self.addCleanup(setattr, inspect_ci, "ROOT", original_root)
        setattr(inspect_ci, "ROOT", Path(self.tmp.name))

    def write_realm(self, lines):
        path = inspect_ci.ROOT / REALM
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("\n".join(lines) + "\n", encoding="utf-8")

    def test_approved_line_is_accepted(self):
        self.write_realm(["{", '  "attributes": {', f"    {APPROVED}", "  }", "}"])
        self.assertTrue(inspect_ci.is_gated_exception(finding(3)))

    def test_formatting_variants_are_accepted(self):
        # No space after the colon, deeper indentation, trailing comma.
        compact = APPROVED.replace(": ", ":")
        self.write_realm([
            "{",
            '  "attributes": {',
            f"        {compact},",
            '    "backchannel.logout.session.required": "true"',
            "  }",
            "}",
        ])
        self.assertTrue(inspect_ci.is_gated_exception(finding(3)))

    def test_unapproved_url_sharing_the_line_is_rejected(self):
        # Valid JSON whose effective value is still the approved one: only the
        # exact-line check stops an approved property riding along with another URL.
        self.write_realm([
            "{",
            '  "attributes": {',
            f'    {APPROVED}, "homepage": "{UNAPPROVED_URL}"',
            "  }",
            "}",
        ])
        realm = json.loads((inspect_ci.ROOT / REALM).read_text(encoding="utf-8"))
        effective = realm["attributes"]["backchannel.logout.url"]
        self.assertEqual(effective, inspect_ci.parse_property_line(APPROVED)[1])
        self.assertFalse(inspect_ci.is_gated_exception(finding(3)))

    def test_duplicate_property_on_one_line_is_rejected(self):
        # Effective value is the approved one (JSON keeps the last entry), and
        # the approved text is present, so only the new checks catch this.
        self.write_realm([
            "{",
            '  "attributes": {',
            f'    "backchannel.logout.url": "{UNAPPROVED_URL}", {APPROVED}',
            "  }",
            "}",
        ])
        self.assertFalse(inspect_ci.is_gated_exception(finding(3)))

    def test_duplicate_property_on_another_line_is_rejected(self):
        # The reported line is byte-for-byte the approved property, but the
        # file defines the key twice, so there is no unique approved value.
        self.write_realm([
            "{",
            '  "attributes": {',
            f"    {APPROVED},",
            f'    "backchannel.logout.url": "{UNAPPROVED_URL}"',
            "  }",
            "}",
        ])
        self.assertFalse(inspect_ci.is_gated_exception(finding(3)))

    def test_malformed_json_fails_closed(self):
        self.write_realm(["{", '  "attributes": {', f"    {APPROVED}", "  }"])
        self.assertFalse(inspect_ci.is_gated_exception(finding(3)))

    def test_missing_file_fails_closed(self):
        self.assertFalse(inspect_ci.is_gated_exception(finding(1)))

    def test_other_rule_or_file_still_blocks(self):
        self.write_realm(["{", '  "attributes": {', f"    {APPROVED}", "  }", "}"])
        self.assertFalse(inspect_ci.is_gated_exception(finding(3, rule="DS999999")))
        self.assertFalse(inspect_ci.is_gated_exception(finding(3, file="deploy/other.json")))

    def test_line_beyond_the_file_blocks(self):
        self.write_realm(["{", '  "attributes": {', f"    {APPROVED}", "  }", "}"])
        self.assertFalse(inspect_ci.is_gated_exception(finding(99)))
        self.assertFalse(inspect_ci.is_gated_exception(finding(0)))


class TestSpecIdentifierException(unittest.TestCase):
    """Fixture sources under a temporary root, so each case controls the line."""

    SPEC_FILE = "src/MqttProbe.Web/Authentication/Oidc/BackChannelLogoutValidator.cs"
    QUOTED = inspect_ci.SPEC_IDENTIFIER_QUOTED
    ESCAPED = inspect_ci.SPEC_IDENTIFIER_ESCAPED
    XHTTP_QUOTED = QUOTED.replace("http", "xhttp", 1)
    SUFFIX_QUOTED = QUOTED.replace('logout"', 'logout-x"')

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        original_root = inspect_ci.ROOT
        self.addCleanup(setattr, inspect_ci, "ROOT", original_root)
        setattr(inspect_ci, "ROOT", Path(self.tmp.name))

    def write_source(self, lines, file=SPEC_FILE):
        path = inspect_ci.ROOT / file
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("\n".join(lines) + "\n", encoding="utf-8")

    def test_exact_quoted_identifier_is_accepted(self):
        self.write_source(["class C {", f"    const string T = {self.QUOTED};", "}"])
        self.assertTrue(
            inspect_ci.is_spec_identifier_exception(finding(2, file=self.SPEC_FILE)))

    def test_escaped_quotes_are_accepted(self):
        self.write_source([f'    parts.Add("{self.ESCAPED}");'])
        self.assertTrue(
            inspect_ci.is_spec_identifier_exception(finding(1, file=self.SPEC_FILE)))

    def test_wrong_rule_blocks(self):
        self.write_source([f"    const string T = {self.QUOTED};"])
        self.assertFalse(
            inspect_ci.is_spec_identifier_exception(
                finding(1, rule="DS999999", file=self.SPEC_FILE)))

    def test_wrong_file_blocks(self):
        other = "src/MqttProbe.Web/Authentication/Oidc/Other.cs"
        self.write_source([f"    const string T = {self.QUOTED};"], file=other)
        self.assertFalse(inspect_ci.is_spec_identifier_exception(finding(1, file=other)))

    def test_wrong_line_blocks(self):
        self.write_source(["class C {", f"    const string T = {self.QUOTED};", "}"])
        self.assertFalse(
            inspect_ci.is_spec_identifier_exception(finding(1, file=self.SPEC_FILE)))
        self.assertFalse(
            inspect_ci.is_spec_identifier_exception(finding(3, file=self.SPEC_FILE)))

    def test_xhttp_prefix_blocks(self):
        self.write_source([f"    const string T = {self.XHTTP_QUOTED};"])
        self.assertFalse(
            inspect_ci.is_spec_identifier_exception(finding(1, file=self.SPEC_FILE)))

    def test_uri_suffix_blocks(self):
        self.write_source([f"    const string T = {self.SUFFIX_QUOTED};"])
        self.assertFalse(
            inspect_ci.is_spec_identifier_exception(finding(1, file=self.SPEC_FILE)))

    def test_second_http_url_on_line_blocks(self):
        self.write_source([f"    const string T = {self.QUOTED}; // {UNAPPROVED_URL}"])
        self.assertFalse(
            inspect_ci.is_spec_identifier_exception(finding(1, file=self.SPEC_FILE)))


class TestSourceLineExceptionSchema(unittest.TestCase):
    """Every SOURCE_LINE_EXCEPTIONS entry has the required keys and a rationale."""

    REQUIRED_KEYS = {"rule", "file", "line", "content", "rationale"}

    def test_all_entries_have_required_keys(self):
        for i, entry in enumerate(inspect_ci.SOURCE_LINE_EXCEPTIONS):
            missing = self.REQUIRED_KEYS - entry.keys()
            self.assertFalse(
                missing,
                f"SOURCE_LINE_EXCEPTIONS[{i}] missing keys: {missing}",
            )

    def test_all_entries_have_non_empty_rationale(self):
        for i, entry in enumerate(inspect_ci.SOURCE_LINE_EXCEPTIONS):
            self.assertTrue(
                entry.get("rationale", "").strip(),
                f"SOURCE_LINE_EXCEPTIONS[{i}] has empty rationale",
            )

    def test_all_entries_have_positive_line_number(self):
        for i, entry in enumerate(inspect_ci.SOURCE_LINE_EXCEPTIONS):
            self.assertGreaterEqual(
                entry["line"], 1,
                f"SOURCE_LINE_EXCEPTIONS[{i}] line must be >= 1",
            )

    def test_no_entry_looks_like_secret(self):
        """Content must not be a bare hex blob; it must be a full source statement."""
        for i, entry in enumerate(inspect_ci.SOURCE_LINE_EXCEPTIONS):
            content = entry["content"]
            # A bare hex string (no letters other than a-f) is suspicious.
            import re
            self.assertIsNotNone(
                re.search(r'[g-zG-Z_=:."()\[\]{} ]', content),
                f"SOURCE_LINE_EXCEPTIONS[{i}] content looks like a bare hex blob: {content!r}",
            )


class TestSourceLineExceptionPositive(unittest.TestCase):
    """Each SOURCE_LINE_EXCEPTIONS entry is accepted against the real repo file."""

    def test_all_entries_are_accepted(self):
        for i, entry in enumerate(inspect_ci.SOURCE_LINE_EXCEPTIONS):
            with self.subTest(i=i, rule=entry["rule"], file=entry["file"], line=entry["line"]):
                f = finding(entry["line"], rule=entry["rule"], file=entry["file"])
                self.assertTrue(
                    inspect_ci.is_source_line_exception(f),
                    f"SOURCE_LINE_EXCEPTIONS[{i}] not accepted: "
                    f"{entry['rule']} {entry['file']}:{entry['line']} "
                    f"({entry['rationale']})",
                )


class TestSourceLineExceptionNegative(unittest.TestCase):
    """Fail-closed behaviour: wrong rule, file, line, content, or missing file."""

    SAMPLE_FILE = "scripts/packaging/linux/appdir.sh"
    SAMPLE_LINE = 14
    SAMPLE_RULE = "DS173237"

    def test_exact_match_is_accepted(self):
        f = finding(self.SAMPLE_LINE, rule=self.SAMPLE_RULE, file=self.SAMPLE_FILE)
        self.assertTrue(inspect_ci.is_source_line_exception(f))

    def test_wrong_rule_blocks(self):
        f = finding(self.SAMPLE_LINE, rule="DS999999", file=self.SAMPLE_FILE)
        self.assertFalse(inspect_ci.is_source_line_exception(f))

    def test_wrong_file_blocks(self):
        f = finding(self.SAMPLE_LINE, rule=self.SAMPLE_RULE, file="nonexistent/file.sh")
        self.assertFalse(inspect_ci.is_source_line_exception(f))

    def test_wrong_line_blocks(self):
        f = finding(999, rule=self.SAMPLE_RULE, file=self.SAMPLE_FILE)
        self.assertFalse(inspect_ci.is_source_line_exception(f))

    def test_line_zero_blocks(self):
        f = finding(0, rule=self.SAMPLE_RULE, file=self.SAMPLE_FILE)
        self.assertFalse(inspect_ci.is_source_line_exception(f))

    def test_content_change_blocks(self):
        """If the source line changes, the exception stops matching."""
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        original_root = inspect_ci.ROOT
        self.addCleanup(setattr, inspect_ci, "ROOT", original_root)
        setattr(inspect_ci, "ROOT", Path(tmp.name))

        # Write a file at the expected path with modified content
        path = inspect_ci.ROOT / self.SAMPLE_FILE
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text('LINUXDEPLOY_SHA256="deadbeef"\n', encoding="utf-8")
        f = finding(1, rule=self.SAMPLE_RULE, file=self.SAMPLE_FILE)
        self.assertFalse(inspect_ci.is_source_line_exception(f))

    def test_missing_file_fails_closed(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        original_root = inspect_ci.ROOT
        self.addCleanup(setattr, inspect_ci, "ROOT", original_root)
        setattr(inspect_ci, "ROOT", Path(tmp.name))

        f = finding(self.SAMPLE_LINE, rule=self.SAMPLE_RULE, file=self.SAMPLE_FILE)
        self.assertFalse(inspect_ci.is_source_line_exception(f))

    def test_extra_content_on_line_blocks(self):
        """A line with extra material beyond the approved statement blocks."""
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        original_root = inspect_ci.ROOT
        self.addCleanup(setattr, inspect_ci, "ROOT", original_root)
        setattr(inspect_ci, "ROOT", Path(tmp.name))

        path = inspect_ci.ROOT / self.SAMPLE_FILE
        path.parent.mkdir(parents=True, exist_ok=True)
        # Same hash value but with a trailing comment
        path.write_text(
            'LINUXDEPLOY_SHA256="36a2d7e274d12e1050d0e9ecfe11d339ed54720b2bec464c286d53f8b07f5c62" # extra\n',  # DevSkim: ignore DS173237 test fixture for fail-closed test
            encoding="utf-8",
        )
        f = finding(1, rule=self.SAMPLE_RULE, file=self.SAMPLE_FILE)
        self.assertFalse(inspect_ci.is_source_line_exception(f))

    def test_no_match_for_unlisted_rule(self):
        """A finding with a rule not in SOURCE_LINE_EXCEPTIONS is not accepted."""
        f = finding(self.SAMPLE_LINE, rule="DS000000", file=self.SAMPLE_FILE)
        self.assertFalse(inspect_ci.is_source_line_exception(f))

    def test_no_match_for_unlisted_file(self):
        """A finding with a file not in SOURCE_LINE_EXCEPTIONS is not accepted."""
        f = finding(self.SAMPLE_LINE, rule=self.SAMPLE_RULE,
                    file="src/MqttProbe.Web/Program.cs")
        self.assertFalse(inspect_ci.is_source_line_exception(f))


class TestGitIgnoredPaths(unittest.TestCase):
    """git_ignored_paths correctly identifies .gitignored files.

    Uses a temporary git repo so each case controls the ignore rules and
    tracked/untracked state without touching the real repo.
    """

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.repo = Path(self.tmp.name)
        subprocess.run(["git", "init"], cwd=self.repo, capture_output=True,
                        check=True)
        # Configure git user for commits (required on some systems)
        subprocess.run(["git", "config", "user.email", "test@test"],
                        cwd=self.repo, capture_output=True)
        subprocess.run(["git", "config", "user.name", "Test"],
                        cwd=self.repo, capture_output=True)

    def _write(self, name, content="dummy"):
        path = self.repo / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")
        return path

    def test_ignored_untracked_file_detected(self):
        """An untracked file matching .gitignore is reported as ignored."""
        self._write(".gitignore", "*.key\n")
        self._write("deploy/certs/ca.key", "not-a-real-key")
        result = inspect_ci.git_ignored_paths(
            ["deploy/certs/ca.key"], self.repo)
        self.assertIn("deploy/certs/ca.key", result)

    def test_non_ignored_file_not_detected(self):
        """A file not matching any .gitignore pattern is not reported."""
        self._write(".gitignore", "*.key\n")
        self._write("src/Program.cs", "class P {}")
        subprocess.run(["git", "add", "."], cwd=self.repo, capture_output=True)
        subprocess.run(["git", "commit", "-m", "init"], cwd=self.repo,
                        capture_output=True)
        result = inspect_ci.git_ignored_paths(
            ["src/Program.cs"], self.repo)
        self.assertNotIn("src/Program.cs", result)

    def test_tracked_file_matching_ignore_not_ignored(self):
        """A tracked file is never reported as ignored, even if .gitignore
        matches.  This is the key safety property: committed secrets still gate.
        """
        self._write(".gitignore", "*.key\n")
        self._write("server.key", "tracked-key-content")
        # Force-add to override the ignore pattern (same as deploy/mtls/certs
        # where the README.md is tracked but .key files are not).
        subprocess.run(["git", "add", "-f", "server.key"], cwd=self.repo,
                        capture_output=True, check=True)
        subprocess.run(["git", "commit", "-m", "add tracked key"],
                        cwd=self.repo, capture_output=True, check=True)
        result = inspect_ci.git_ignored_paths(["server.key"], self.repo)
        self.assertNotIn("server.key", result)

    def test_empty_input_returns_empty_set(self):
        """Empty path set is a no-op."""
        self.assertEqual(inspect_ci.git_ignored_paths([], self.repo), set())

    def test_non_git_dir_returns_empty_set(self):
        """Outside a git repo, no paths are excluded (fail-open on discovery)."""
        outside = tempfile.TemporaryDirectory()
        self.addCleanup(outside.cleanup)
        result = inspect_ci.git_ignored_paths(["some/file.key"],
                                               Path(outside.name))
        self.assertEqual(result, set())

    def test_mixed_batch(self):
        """Multiple files in one call: only ignored ones are returned."""
        self._write(".gitignore", "*.key\n*.pfx\n")
        self._write("ca.key", "k")
        self._write("client.pfx", "p")
        self._write("README.md", "readme")
        subprocess.run(["git", "add", "README.md"], cwd=self.repo,
                        capture_output=True)
        subprocess.run(["git", "commit", "-m", "init"], cwd=self.repo,
                        capture_output=True)
        result = inspect_ci.git_ignored_paths(
            ["ca.key", "client.pfx", "README.md"], self.repo)
        self.assertIn("ca.key", result)
        self.assertIn("client.pfx", result)
        self.assertNotIn("README.md", result)

    def test_backslash_paths_normalised(self):
        """Windows backslash paths are normalised for git check-ignore."""
        self._write(".gitignore", "*.key\n")
        self._write("deploy/mtls/certs/ca.key", "k")
        result = inspect_ci.git_ignored_paths(
            ["deploy\\mtls\\certs\\ca.key"], self.repo)
        # git reports with forward slashes; the function normalises input.
        self.assertTrue(any("ca.key" in p for p in result))


if __name__ == "__main__":
    unittest.main()
