#!/usr/bin/env python3
"""Tests for scripts/ci/ios-certificate.py.

Run: python -m unittest discover -s scripts/ci/tests
"""

import importlib.util
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

_SPEC = importlib.util.spec_from_file_location(
    "ci_ios_certificate",
    Path(__file__).resolve().parents[1] / "ios-certificate.py",
)
if _SPEC is None or _SPEC.loader is None:
    raise SystemExit("Cannot load scripts/ci/ios-certificate.py")
ios_certificate = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(ios_certificate)

OPENSSL = shutil.which("openssl")

# Verbatim `openssl x509 -noout -subject -nameopt sep_multiline,utf8` output.
CN_FIRST = "subject=\n    CN=Apple Distribution: Bluegrass IOT LLC (XVKMB6CQ72)\n    O=Bluegrass IOT LLC\n    C=US\n"
CN_LAST = "subject=\n    C=US\n    O=Bluegrass IOT LLC\n    OU=Engineering\n    CN=Apple Distribution: Bluegrass IOT LLC (XVKMB6CQ72)\n"
CN_COMMA = "subject=\n    CN=Acme, Widgets Ltd\n    C=US\n"
CN_SPECIAL = 'subject=\n    CN=Test "Quoted" + Plus, Comma and Slash Back\n    C=US\n'
CN_ALONE = "subject=\n    CN=Only Attribute Here\n"
NO_CN = "subject=\n    O=Some Organisation\n    C=US\n"
TWO_CN = "subject=\n    CN=First Name\n    CN=Second Name\n"
EMPTY = ""


class ParseCommonNameTests(unittest.TestCase):
    def test_reads_cn_when_first(self):
        self.assertEqual(
            ios_certificate.parse_common_name(CN_FIRST),
            "Apple Distribution: Bluegrass IOT LLC (XVKMB6CQ72)",
        )

    def test_reads_cn_when_last(self):
        self.assertEqual(
            ios_certificate.parse_common_name(CN_LAST),
            "Apple Distribution: Bluegrass IOT LLC (XVKMB6CQ72)",
        )

    def test_reads_cn_when_alone(self):
        self.assertEqual(ios_certificate.parse_common_name(CN_ALONE), "Only Attribute Here")

    def test_attribute_order_does_not_change_the_result(self):
        self.assertEqual(
            ios_certificate.parse_common_name(CN_FIRST).split(":")[0],
            ios_certificate.parse_common_name(CN_LAST).split(":")[0],
        )

    def test_preserves_comma_in_common_name(self):
        self.assertEqual(ios_certificate.parse_common_name(CN_COMMA), "Acme, Widgets Ltd")

    def test_preserves_quotes_and_plus(self):
        self.assertEqual(
            ios_certificate.parse_common_name(CN_SPECIAL),
            'Test "Quoted" + Plus, Comma and Slash Back',
        )

    def test_preserves_trailing_whitespace_inside_value(self):
        subject = "subject=\n    CN=Name With Trailing Space \n    C=US\n"
        self.assertEqual(ios_certificate.parse_common_name(subject), "Name With Trailing Space")

    def test_ignores_attributes_that_merely_contain_cn(self):
        subject = "subject=\n    OU=CN=not the subject\n    CNX=nope\n    CN=Real Name\n"
        self.assertEqual(ios_certificate.parse_common_name(subject), "Real Name")

    def test_rejects_subject_without_cn(self):
        with self.assertRaises(ios_certificate.CertificateError) as raised:
            ios_certificate.parse_common_name(NO_CN)
        self.assertIn("no CN attribute", str(raised.exception))

    def test_rejects_ambiguous_duplicate_cn(self):
        with self.assertRaises(ios_certificate.CertificateError) as raised:
            ios_certificate.parse_common_name(TWO_CN)
        self.assertIn("2 CN attributes", str(raised.exception))

    def test_rejects_empty_output(self):
        with self.assertRaises(ios_certificate.CertificateError):
            ios_certificate.parse_common_name(EMPTY)

    def test_rfc2253_style_input_does_not_silently_match(self):
        rfc = "subject=C=US,O=Bluegrass IOT LLC,CN=Apple Distribution: Bluegrass IOT LLC (XVKMB6CQ72)"
        with self.assertRaises(ios_certificate.CertificateError):
            ios_certificate.parse_common_name(rfc)


@unittest.skipUnless(OPENSSL, "openssl is not available on this machine")
class RealCertificateTests(unittest.TestCase):
    def make_certificate(self, subject_lines: str) -> Path:
        directory = Path(tempfile.mkdtemp())
        config = directory / "openssl.cnf"
        config.write_text(
            "[req]\ndistinguished_name=dn\nx509_extensions=v3\nprompt=no\n"
            f"[dn]\n{subject_lines}"
            "[v3]\nbasicConstraints=critical,CA:FALSE\nkeyUsage=digitalSignature\n",
            encoding="utf-8",
        )
        certificate = directory / "cert.pem"
        subprocess.run(
            [
                OPENSSL, "req", "-x509", "-newkey", "rsa:2048", "-nodes",
                "-keyout", str(directory / "key.pem"), "-out", str(certificate),
                "-days", "3650", "-config", str(config),
            ],
            capture_output=True,
            check=True,
        )
        self.addCleanup(lambda: shutil.rmtree(directory, ignore_errors=True))
        return certificate

    def test_accepts_matching_certificate_and_returns_digest(self):
        certificate = self.make_certificate("CN=Bluegrass Signing Certificate\nO=Bluegrass IOT LLC\nC=US\n")
        values = ios_certificate.check(certificate, "Bluegrass Signing Certificate")
        self.assertEqual(values["common_name"], "Bluegrass Signing Certificate")
        self.assertEqual(len(values["sha256"]), 64)

    def test_rejects_mismatched_common_name(self):
        certificate = self.make_certificate("CN=Bluegrass Signing Certificate\nC=US\n")
        with self.assertRaises(ios_certificate.CertificateError) as raised:
            ios_certificate.check(certificate, "Some Other Identity")
        self.assertIn("Some Other Identity", str(raised.exception))

    def test_handles_common_name_with_comma_and_plus(self):
        # Quotes are covered in ParseCommonNameTests: openssl's config parser
        # strips them at creation, so a generated certificate never carries them.
        certificate = self.make_certificate("CN=Acme, Widgets + Co\nC=US\n")
        values = ios_certificate.check(certificate, "Acme, Widgets + Co")
        self.assertEqual(values["common_name"], "Acme, Widgets + Co")

    def test_rejects_common_name_differing_only_by_a_comma_position(self):
        certificate = self.make_certificate("CN=Acme Widgets, Co\nC=US\n")
        with self.assertRaises(ios_certificate.CertificateError):
            ios_certificate.check(certificate, "Acme, Widgets Co")

    def test_rejects_prefix_of_actual_common_name(self):
        certificate = self.make_certificate("CN=Bluegrass Signing Certificate Extra\nC=US\n")
        with self.assertRaises(ios_certificate.CertificateError):
            ios_certificate.check(certificate, "Bluegrass Signing Certificate")

    def test_digest_is_stable_across_calls(self):
        certificate = self.make_certificate("CN=Stable Digest\nC=US\n")
        first = ios_certificate.check(certificate, "Stable Digest")["sha256"]
        second = ios_certificate.check(certificate, "Stable Digest")["sha256"]
        self.assertEqual(first, second)

    def test_digest_differs_between_certificates(self):
        first = self.make_certificate("CN=Digest Subject\nC=US\n")
        second = self.make_certificate("CN=Digest Subject\nC=US\n")
        self.assertNotEqual(
            ios_certificate.check(first, "Digest Subject")["sha256"],
            ios_certificate.check(second, "Digest Subject")["sha256"],
        )


class CliTests(unittest.TestCase):
    def test_rejects_missing_file(self):
        missing = Path(tempfile.mkdtemp()) / "cert.pem"
        self.assertEqual(
            ios_certificate.main([str(missing), "--common-name", "Anything"]), 1
        )

    def test_rejects_unreadable_file(self):
        path = Path(tempfile.mkdtemp()) / "cert.pem"
        path.write_text("not a certificate", encoding="utf-8")
        self.addCleanup(lambda: path.unlink(missing_ok=True))
        self.assertEqual(ios_certificate.main([str(path), "--common-name", "Anything"]), 1)


if __name__ == "__main__":
    unittest.main()