#!/usr/bin/env python3
"""Tests for scripts/ci/ios-version.py.

Run: python -m unittest discover -s scripts/ci/tests
"""

import importlib.util
import tempfile
import unittest
from pathlib import Path

_SPEC = importlib.util.spec_from_file_location(
    "ci_ios_version",
    Path(__file__).resolve().parents[1] / "ios-version.py",
)
if _SPEC is None or _SPEC.loader is None:
    raise SystemExit("Cannot load scripts/ci/ios-version.py")
ios_version = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(ios_version)


class NormalizeTests(unittest.TestCase):
    def test_strips_leading_v(self):
        self.assertEqual(ios_version.normalize("v1.0.9"), (1, 0, 9))

    def test_accepts_bare_version(self):
        self.assertEqual(ios_version.normalize("1.0.9"), (1, 0, 9))

    def test_tolerates_surrounding_whitespace(self):
        self.assertEqual(ios_version.normalize("  v12.34.56 "), (12, 34, 56))

    def test_truncates_leading_zeros_like_apple(self):
        # Apple ignores leading zeros in each part, so normalize rather than stamp.
        self.assertEqual(ios_version.normalize("v01.02.03"), (1, 2, 3))

    def test_accepts_four_digit_parts(self):
        self.assertEqual(ios_version.normalize("v9999.9999.9999"), (9999, 9999, 9999))

    def test_rejects_five_digit_part(self):
        with self.assertRaises(ios_version.VersionError):
            ios_version.normalize("v10000.0.0")

    def test_rejects_two_part_version(self):
        with self.assertRaises(ios_version.VersionError):
            ios_version.normalize("v1.0")

    def test_rejects_one_part_version(self):
        with self.assertRaises(ios_version.VersionError):
            ios_version.normalize("v1")

    def test_rejects_four_part_version(self):
        with self.assertRaises(ios_version.VersionError):
            ios_version.normalize("v1.0.9.1")

    def test_rejects_prerelease_suffix(self):
        with self.assertRaises(ios_version.VersionError):
            ios_version.normalize("v1.0.9-rc1")

    def test_rejects_apple_development_suffix_style(self):
        with self.assertRaises(ios_version.VersionError):
            ios_version.normalize("v1.0.9a1")

    def test_rejects_placeholder_zero(self):
        with self.assertRaises(ios_version.VersionError):
            ios_version.normalize("v0.0.0")

    def test_rejects_empty(self):
        with self.assertRaises(ios_version.VersionError):
            ios_version.normalize("")

    def test_rejects_none(self):
        with self.assertRaises(ios_version.VersionError):
            ios_version.normalize(None)

    def test_rejects_placeholder_word(self):
        for candidate in ("latest", "dev", "0.0.0", "1.0", "v", "1.0.9.0", "1.0.x"):
            with self.subTest(candidate=candidate):
                with self.assertRaises(ios_version.VersionError):
                    ios_version.normalize(candidate)


class BuildNumberTests(unittest.TestCase):
    def test_accepts_lowest_valid_value(self):
        self.assertEqual(ios_version.validate_build_number("1"), 1)

    def test_accepts_highest_valid_value(self):
        self.assertEqual(ios_version.validate_build_number("9999"), 9999)

    def test_strips_surrounding_whitespace(self):
        self.assertEqual(ios_version.validate_build_number(" 42 "), 42)

    def test_drops_leading_zeros_like_apple(self):
        self.assertEqual(ios_version.validate_build_number("0042"), 42)

    def test_rejects_zero(self):
        with self.assertRaises(ios_version.VersionError):
            ios_version.validate_build_number("0")

    def test_rejects_oversized_run_number(self):
        with self.assertRaises(ios_version.VersionError):
            ios_version.validate_build_number("1000800001")

    def test_rejects_ten_thousand(self):
        with self.assertRaises(ios_version.VersionError):
            ios_version.validate_build_number("10000")

    def test_rejects_negative(self):
        with self.assertRaises(ios_version.VersionError):
            ios_version.validate_build_number("-1")

    def test_rejects_decimal(self):
        with self.assertRaises(ios_version.VersionError):
            ios_version.validate_build_number("1.5")

    def test_rejects_non_numeric(self):
        with self.assertRaises(ios_version.VersionError):
            ios_version.validate_build_number("abc")

    def test_rejects_empty(self):
        with self.assertRaises(ios_version.VersionError):
            ios_version.validate_build_number("")

    def test_rejects_whitespace_only(self):
        with self.assertRaises(ios_version.VersionError):
            ios_version.validate_build_number("   ")

    def test_every_accepted_value_fits_apple_limit(self):
        for value in ("1", "9", "99", "999", "9999"):
            with self.subTest(value=value):
                accepted = ios_version.validate_build_number(value)
                self.assertLessEqual(len(str(accepted)), 4)
                self.assertGreater(accepted, 0)

    def test_every_accepted_build_number_increases_monotonically(self):
        accepted = [ios_version.validate_build_number(str(n)) for n in range(1, 10000, 97)]
        self.assertEqual(accepted, sorted(accepted))
        self.assertEqual(len(set(accepted)), len(accepted))


class DeriveTests(unittest.TestCase):
    def test_returns_marketing_and_build(self):
        self.assertEqual(
            ios_version.derive("v1.0.9", "42"), {"marketing": "1.0.9", "build": "42"}
        )

    def test_normalizes_leading_zeros_in_both_fields(self):
        self.assertEqual(
            ios_version.derive("v01.02.03", "0042"),
            {"marketing": "1.2.3", "build": "42"},
        )

    def test_build_is_independent_of_tag(self):
        self.assertEqual(
            ios_version.derive("v2.0.0", "42")["build"],
            ios_version.derive("v1.0.9", "42")["build"],
        )
        self.assertNotEqual(
            ios_version.derive("v2.0.0", "42")["marketing"],
            ios_version.derive("v1.0.9", "42")["marketing"],
        )

    def test_invalid_version_propagates(self):
        with self.assertRaises(ios_version.VersionError):
            ios_version.derive("nope", "42")

    def test_invalid_build_number_propagates(self):
        with self.assertRaises(ios_version.VersionError):
            ios_version.derive("v1.0.9", "10000")


class CliTests(unittest.TestCase):
    def test_writes_github_output_lines(self):
        with tempfile.TemporaryDirectory() as tmp:
            output = Path(tmp) / "output.txt"
            output.write_text("existing=1\n", encoding="utf-8")
            code = ios_version.main(
                ["--version", "v1.0.9", "--build-number", "42", "--github-output", str(output)]
            )
            self.assertEqual(code, 0)
            self.assertEqual(
                output.read_text(encoding="utf-8"),
                "existing=1\nmarketing=1.0.9\nbuild=42\n",
            )

    def test_prints_to_stdout_without_github_output(self):
        self.assertEqual(
            ios_version.main(["--version", "v1.0.9", "--build-number", "42"]),
            0,
        )

    def test_invalid_version_exits_nonzero(self):
        self.assertEqual(
            ios_version.main(["--version", "nope", "--build-number", "42"]), 1
        )

    def test_invalid_build_number_exits_nonzero(self):
        self.assertEqual(
            ios_version.main(["--version", "v1.0.9", "--build-number", "1000800001"]), 1
        )

    def test_invalid_input_writes_nothing_to_github_output(self):
        with tempfile.TemporaryDirectory() as tmp:
            output = Path(tmp) / "output.txt"
            code = ios_version.main(
                ["--version", "v1.0.9", "--build-number", "0", "--github-output", str(output)]
            )
            self.assertEqual(code, 1)
            self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()