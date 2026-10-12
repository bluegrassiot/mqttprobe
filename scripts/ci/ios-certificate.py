#!/usr/bin/env python3
"""Checks a signing certificate's common name and prints its DER SHA-256 digest.

The common name is read from openssl's `sep_multiline,utf8` subject output, which
puts one attribute per line with escapes already decoded. RFC2253 cannot be
pattern-matched instead: it emits attributes in the certificate's own order, so
CN is not necessarily first, and it escapes commas, quotes, and plus signs.
"""

from __future__ import annotations

import argparse
import hashlib
import re
import shutil
import subprocess
import sys
from pathlib import Path

# sep_multiline output has a leading "subject=" line, then indented
# "TYPE=value" lines.
_COMMON_NAME_PREFIX = "CN="


class CertificateError(ValueError):
    pass


def parse_common_name(subject_output: str) -> str:
    found: list[str] = []
    for raw_line in subject_output.splitlines():
        line = raw_line.strip()
        if line.startswith(_COMMON_NAME_PREFIX):
            found.append(line[len(_COMMON_NAME_PREFIX) :])
    if not found:
        raise CertificateError(
            "Certificate subject has no CN attribute:\n"
            f"{subject_output.strip() or '(openssl printed nothing)'}"
        )
    if len(found) > 1:
        # CN is single-valued in X.509, so two of them means this is not the
        # expected subject output.
        raise CertificateError(
            f"Certificate subject has {len(found)} CN attributes; expected exactly one."
        )
    return found[0]


def _run_openssl(openssl: str, args: list[str]) -> str:
    try:
        result = subprocess.run(
            [openssl, *args],
            capture_output=True,
            text=True,
            check=False,
        )
    except OSError as error:
        raise CertificateError(f"Could not run '{openssl}': {error}") from error
    if result.returncode != 0:
        raise CertificateError(f"'{openssl} {' '.join(args)}' failed: {result.stderr.strip()}")
    return result.stdout


def _der_bytes(path: Path, openssl: str) -> bytes:
    try:
        result = subprocess.run(
            [openssl, "x509", "-in", str(path), "-outform", "DER"],
            capture_output=True,
            check=False,
        )
    except OSError as error:
        raise CertificateError(f"Could not run '{openssl}': {error}") from error
    if result.returncode != 0 or not result.stdout:
        detail = result.stderr.decode("utf-8", "replace").strip()
        raise CertificateError(f"Could not read a certificate from {path}: {detail}")
    return result.stdout


def check(path: Path, expected_common_name: str, openssl: str = "openssl") -> dict[str, str]:
    if not path.is_file():
        raise CertificateError(f"No certificate file at {path}.")

    subject = _run_openssl(
        openssl,
        ["x509", "-in", str(path), "-noout", "-subject", "-nameopt", "sep_multiline,utf8"],
    )
    actual = parse_common_name(subject)
    if actual != expected_common_name:
        raise CertificateError(
            f"Certificate common name is '{actual}', but the build is configured to sign "
            f"with '{expected_common_name}'."
        )

    digest = hashlib.sha256(_der_bytes(path, openssl)).hexdigest()
    if not re.fullmatch(r"[0-9a-f]{64}", digest):
        raise CertificateError(f"Unexpected digest format for {path}: {digest!r}")

    return {"common_name": actual, "sha256": digest}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("certificate", type=Path, help="PEM file from 'security find-certificate -p'")
    parser.add_argument("--common-name", required=True, help="Exact CN the build must sign with")
    parser.add_argument("--openssl", default="openssl", help="openssl executable (default: openssl)")
    args = parser.parse_args(argv)

    try:
        values = check(args.certificate, args.common_name, args.openssl)
    except CertificateError as error:
        print(f"::error::{error}", file=sys.stderr)
        return 1

    print(values["sha256"])
    return 0


if __name__ == "__main__":
    sys.exit(main())