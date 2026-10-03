#!/usr/bin/env python3
"""Regression tests for the gated DevSkim exception in scripts/ci/inspect.py.

Run: python -m unittest discover -s scripts/ci/tests
"""

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


if __name__ == "__main__":
    unittest.main()
