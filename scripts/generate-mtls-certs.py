#!/usr/bin/env python3
"""
Generate lab CA, server, and client certificates for mTLS Mosquitto testing.

Requires openssl on PATH. No third-party Python packages.

Usage:
  python scripts/generate-mtls-certs.py
  python scripts/generate-mtls-certs.py --force
  python scripts/generate-mtls-certs.py --out deploy/mtls/certs --pfx-password changeme --days 365
"""

from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import NoReturn

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_OUT = ROOT / "deploy" / "mtls" / "certs"

EXPECTED = (
    "ca.key",
    "ca.crt",
    "server.key",
    "server.crt",
    "client.key",
    "client.crt",
    "client.pfx",
)


def die(message: str, code: int = 1) -> NoReturn:
    print(message, file=sys.stderr)
    sys.exit(code)


def require_openssl() -> str:
    path = shutil.which("openssl")
    if not path:
        die(
            "openssl not found on PATH.\n"
            "Install OpenSSL, then re-run this script.\n"
            "  Windows: install Git for Windows (includes openssl) or use WSL\n"
            "  macOS:   brew install openssl\n"
            "  Linux:   install the openssl package for your distro"
        )
    return path


def resolve_openssl_conf(openssl_path: str) -> str | None:
    env_conf = os.environ.get("OPENSSL_CONF")
    if env_conf and Path(env_conf).is_file():
        return env_conf

    openssl_dir = Path(openssl_path).resolve().parent
    for candidate in (
        openssl_dir / ".." / "ssl" / "openssl.cnf",
        openssl_dir / "openssl.cnf",
        openssl_dir / ".." / "openssl.cnf",
    ):
        resolved = candidate.resolve()
        if resolved.is_file():
            return str(resolved)

    return None


def run_openssl(
    openssl: str, args: list[str], cwd: Path, conf: str | None = None
) -> None:
    cmd = [openssl, *args]
    env = {**os.environ, "OPENSSL_CONF": conf} if conf else None
    try:
        subprocess.run(
            cmd, cwd=cwd, check=True, capture_output=True, text=True, env=env
        )
    except subprocess.CalledProcessError as exc:
        detail = (exc.stderr or exc.stdout or "").strip()
        if "openssl.cnf" in detail:
            die(
                f"openssl failed ({' '.join(cmd)}):\n{detail}\n\n"
                "Hint: set the OPENSSL_CONF environment variable to the path of openssl.cnf.\n"
                "  Example: $env:OPENSSL_CONF = 'C:\\path\\to\\openssl.cnf'"
            )
        die(f"openssl failed ({' '.join(cmd)}):\n{detail}")


def all_present(out: Path) -> bool:
    return all((out / name).is_file() for name in EXPECTED)


def generate(
    out: Path, days: int, pfx_password: str, openssl: str, conf: str | None
) -> None:
    out.mkdir(parents=True, exist_ok=True)

    run_openssl(
        openssl,
        [
            "req",
            "-x509",
            "-newkey",
            "rsa:2048",
            "-keyout",
            "ca.key",
            "-out",
            "ca.crt",
            "-days",
            str(days),
            "-nodes",
            "-subj",
            "/CN=LabCA",
        ],
        out,
        conf,
    )

    run_openssl(
        openssl,
        [
            "req",
            "-newkey",
            "rsa:2048",
            "-keyout",
            "server.key",
            "-out",
            "server.csr",
            "-nodes",
            "-subj",
            "/CN=localhost",
        ],
        out,
        conf,
    )

    # Portable SAN extfile (no bash process substitution).
    with tempfile.NamedTemporaryFile(
        mode="w",
        suffix=".cnf",
        delete=False,
        encoding="utf-8",
    ) as tmp:
        tmp.write("subjectAltName=DNS:localhost,DNS:mosquitto,IP:127.0.0.1\n")
        san_path = Path(tmp.name)

    try:
        run_openssl(
            openssl,
            [
                "x509",
                "-req",
                "-in",
                "server.csr",
                "-CA",
                "ca.crt",
                "-CAkey",
                "ca.key",
                "-CAcreateserial",
                "-out",
                "server.crt",
                "-days",
                str(days),
                "-extfile",
                str(san_path),
            ],
            out,
            conf,
        )
    finally:
        san_path.unlink(missing_ok=True)

    run_openssl(
        openssl,
        [
            "req",
            "-newkey",
            "rsa:2048",
            "-keyout",
            "client.key",
            "-out",
            "client.csr",
            "-nodes",
            "-subj",
            "/CN=mqttprobe-client",
        ],
        out,
        conf,
    )

    run_openssl(
        openssl,
        [
            "x509",
            "-req",
            "-in",
            "client.csr",
            "-CA",
            "ca.crt",
            "-CAkey",
            "ca.key",
            "-CAcreateserial",
            "-out",
            "client.crt",
            "-days",
            str(days),
        ],
        out,
        conf,
    )

    run_openssl(
        openssl,
        [
            "pkcs12",
            "-export",
            "-out",
            "client.pfx",
            "-inkey",
            "client.key",
            "-in",
            "client.crt",
            "-password",
            f"pass:{pfx_password}",
        ],
        out,
        conf,
    )

    # Intermediate CSR/serial files are not required by Mosquitto or MQTTProbe.
    for name in ("server.csr", "client.csr", "ca.srl"):
        path = out / name
        if path.is_file():
            path.unlink()


def print_next_steps(out: Path, pfx_password: str) -> None:
    rel = out
    try:
        rel = out.resolve().relative_to(ROOT)
    except ValueError:
        pass
    print(f"Wrote mTLS lab certificates to: {rel}")
    print()
    print("Next steps:")
    print("  1. docker compose -f docker-compose.mtls.yml up -d")
    print(f"  2. Import {rel / 'client.pfx'} in MQTTProbe (password: {pfx_password})")
    print("     or use client.crt + client.key for PEM mode")
    print("  3. Connect to 127.0.0.1:8883 with Use TLS and Allow untrusted certificate")


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Generate lab CA/server/client certificates for mTLS Mosquitto testing."
    )
    parser.add_argument(
        "--out",
        type=Path,
        default=DEFAULT_OUT,
        help=f"Output directory (default: {DEFAULT_OUT})",
    )
    parser.add_argument(
        "--force",
        action="store_true",
        help="Overwrite existing certificate files",
    )
    parser.add_argument(
        "--pfx-password",
        default="changeme",
        help="Password for client.pfx (default: changeme)",
    )
    parser.add_argument(
        "--days",
        type=int,
        default=365,
        help="Certificate validity in days (default: 365)",
    )
    args = parser.parse_args()

    if args.days < 1:
        die("--days must be >= 1")

    out = args.out
    if not out.is_absolute():
        out = (Path.cwd() / out).resolve()
    else:
        out = out.resolve()

    openssl = require_openssl()
    conf = resolve_openssl_conf(openssl)

    if all_present(out) and not args.force:
        print(f"Certificates already present in {out} (use --force to regenerate).")
        print_next_steps(out, args.pfx_password)
        sys.exit(0)

    if out.exists() and any(out.iterdir()) and not args.force:
        # Partial dir: only skip when complete set exists (handled above).
        # If incomplete, regenerate missing by forcing full generate after cleanup of known names.
        pass

    if args.force:
        for name in EXPECTED:
            path = out / name
            if path.is_file():
                path.unlink()

    generate(out, args.days, args.pfx_password, openssl, conf)

    if not all_present(out):
        missing = [n for n in EXPECTED if not (out / n).is_file()]
        die(f"Generation finished but missing files: {', '.join(missing)}")

    print_next_steps(out, args.pfx_password)


if __name__ == "__main__":
    main()
