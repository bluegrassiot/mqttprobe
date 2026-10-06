"""NOTICE file builder for MQTTProbe AppImage.

Generates a prominent NOTICE file to be placed inside the AppDir (before
vpk stage) containing:
- Dated WebKit binary modification record with affected binaries,
  old/new prefix, exact 40-byte details
- Package version manifest referencing actual dpkg-query results
- Full license texts copied from /usr/share/common-licenses
- Extraction and shared-lib replacement instructions
- Exact companion source archive filename

Can be imported as a module or run as a CLI script.

Consolidates manifest + NOTICE generation: a single generate_manifest()
call produces the inventory used for both outputs, eliminating the
previous double-generation in build-appimage.sh + notice_builder.py.
"""

import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

from manifest import (
    PackageManifest,
    SystemPackage,
    VendorIngredient,
    format_manifest_text,
    generate_manifest,
)


# WebKit modification constants
WEBKIT_OLD_PREFIX = "/usr/lib/x86_64-linux-gnu/webkit2gtk-4.1"
WEBKIT_NEW_PREFIX = "././/lib/x86_64-linux-gnu/webkit2gtk-4.1"
WEBKIT_PREFIX_LEN = 40  # both prefixes are exactly 40 bytes

# Common licenses to copy from the build system
COMMON_LICENSES = [
    "Apache-2.0",
    "LGPL-2.1",
    "GPL-2",
    "MIT",
    "BSD-3-Clause",
    "MPL-2.0",
    "Artistic",
]


def _get_build_date(source_date_epoch: str | None = None) -> str:
    """Get the build date from SOURCE_DATE_EPOCH or current UTC."""
    if source_date_epoch:
        try:
            return datetime.fromisoformat(
                source_date_epoch.replace("Z", "+00:00")
            ).strftime("%Y-%m-%d")
        except (ValueError, OSError):
            pass
        try:
            ts = int(source_date_epoch)
            return datetime.fromtimestamp(ts, tz=timezone.utc).strftime("%Y-%m-%d")
        except (ValueError, OSError):
            pass
    return datetime.now(timezone.utc).strftime("%Y-%m-%d")


def _get_common_license_text(license_name: str) -> str | None:
    """Read a common license text from /usr/share/common-licenses/."""
    path = Path(f"/usr/share/common-licenses/{license_name}")
    if path.is_file():
        return path.read_text(encoding="utf-8", errors="replace")
    return None


def _format_package_table(packages: list[SystemPackage]) -> str:
    """Format the package version table."""
    lines = [
        "| Binary Package | Version | Source Package | Source Version |",
        "|----------------|---------|----------------|----------------|",
    ]
    for pkg in sorted(packages, key=lambda p: p.binary_package):
        lines.append(
            f"| {pkg.binary_package} | {pkg.binary_version} | "
            f"{pkg.source_package} | {pkg.source_version} |"
        )
    return "\n".join(lines)


def _format_vendor_table(vendors: list[VendorIngredient]) -> str:
    """Format the vendor ingredients table."""
    lines = [
        "| Component | Version | License | Source |",
        "|-----------|---------|---------|--------|",
    ]
    for v in vendors:
        lines.append(f"| {v.name} | {v.version} | {v.license_id} | {v.source_url} |")
    return "\n".join(lines)


def build_notice(
    manifest: PackageManifest,
    version: str,
    source_archive_filename: str,
    source_date_epoch: str | None = None,
    affected_binaries: list[str] | None = None,
) -> str:
    """Build the full NOTICE text for inclusion in the AppDir."""
    build_date = _get_build_date(source_date_epoch)

    if affected_binaries is None:
        affected_binaries = [
            "usr/lib/x86_64-linux-gnu/webkit2gtk-4.1/libwebkit2gtk-4.1.so.0",
            "usr/lib/x86_64-linux-gnu/webkit2gtk-4.1/WebKitWebProcess",
            "usr/lib/x86_64-linux-gnu/webkit2gtk-4.1/WebKitNetworkProcess",
            "usr/lib/x86_64-linux-gnu/webkit2gtk-4.1/WebKitGPUProcess",
        ]

    sections = []

    sections.append(f"""MQTTProbe AppImage v{version} — Third-Party Notice
{'=' * 60}
Build date: {build_date}
Generated from actual staged files and dpkg-query results.

This notice documents all third-party components bundled in this
AppImage, including Ubuntu 22.04 system packages and vendor
ingredients with their exact source versions and licenses.""")

    binary_lines = "\n".join(f"  - {b}" for b in affected_binaries)
    sections.append(f"""
WebKit Binary Modification Record
{'-' * 40}
Date: {build_date}
Affected binaries:
{binary_lines}

The following 40-byte prefix was replaced in each affected binary:

  Old: {WEBKIT_OLD_PREFIX}
       ({WEBKIT_PREFIX_LEN} bytes, absolute host path)
  New: {WEBKIT_NEW_PREFIX}
       ({WEBKIT_PREFIX_LEN} bytes, relative AppImage path)

Both prefixes are exactly {WEBKIT_PREFIX_LEN} bytes. No padding or
truncation occurs. File size and ELF header integrity are validated
post-patch. This is the minimum modification required for the
AppImage runtime to locate bundled WebKit helpers and the injected
bundle.

Corresponding source: Ubuntu 22.04 webkit2gtk source package at the
version recorded in the package table below.""")

    sections.append(f"""
Bundled Ubuntu 22.04 Packages
{'-' * 40}
{_format_package_table(manifest.build_system_packages)}

Total: {len(manifest.build_system_packages)} system packages""")

    sections.append(f"""
Vendor Ingredients
{'-' * 40}
{_format_vendor_table(manifest.vendor_ingredients)}""")

    sections.append(f"""
License Texts
{'-' * 40}
Full license texts for common licenses are included below.
For per-package copyright details, see the companion source archive
({source_archive_filename}) which includes:
  - Per-package copyright files from /usr/share/doc/<pkg>/copyright
  - Source archives (.dsc, .orig.tar.*, .debian.tar.*)
  - Photino.Native source at the pinned commit""")

    for lic_name in COMMON_LICENSES:
        text = _get_common_license_text(lic_name)
        if text:
            sections.append(f"""
--- {lic_name} ---
{text}""")

    sections.append(f"""
How to Extract and Inspect This AppImage
{'-' * 40}
1. Extract the AppImage:
     chmod +x MQTTProbe.AppImage
     ./MQTTProbe.AppImage --appimage-extract

2. The extracted filesystem is in squashfs-root/.

3. To replace a bundled shared library with a compatible version:
   a. Locate the library in squashfs-root/usr/lib/
   b. Replace the file with your compatible version
   c. Ensure the replacement has the same SONAME
   d. Run: ./squashfs-root/AppRun

4. To launch without extracting:
     chmod +x MQTTProbe.AppImage
     ./MQTTProbe.AppImage

Companion source archive: {source_archive_filename}
""")

    return "\n".join(sections)


def write_notice_to_appdir(
    appdir: Path,
    manifest: PackageManifest,
    version: str,
    source_archive_filename: str,
    source_date_epoch: str | None = None,
) -> Path:
    """Write the NOTICE file into the AppDir at a prominent location."""
    notice_dir = appdir / "usr" / "share" / "doc" / "mqttprobe"
    notice_dir.mkdir(parents=True, exist_ok=True)
    notice_path = notice_dir / "NOTICE"

    content = build_notice(
        manifest=manifest,
        version=version,
        source_archive_filename=source_archive_filename,
        source_date_epoch=source_date_epoch,
    )
    notice_path.write_text(content, encoding="utf-8")
    return notice_path


def generate_and_write(
    appdir: Path,
    version: str,
    source_archive_filename: str,
    output_dir: Path,
    build_date: str = "",
    source_date_epoch: str | None = None,
    *,
    strict_unknown: bool = False,
) -> tuple[Path, Path]:
    """Generate manifest once, write both manifest text and NOTICE.

    Eliminates the double-generation that previously occurred in
    build-appimage.sh (package_manifest.py) then notice_builder.py.

    Args:
        strict_unknown: If True, raise SystemExit when unknown ELFs are found.

    Returns (manifest_path, notice_path).
    """
    manifest = generate_manifest(appdir, build_date)

    if strict_unknown and manifest.unknown_count() > 0:
        print(f"ERROR: {manifest.unknown_count()} unknown ELF(s) found:", file=sys.stderr)
        for f in manifest.unknown_files:
            print(f"  UNKNOWN: {f}", file=sys.stderr)
        sys.exit(1)

    manifest_path = output_dir / "package-manifest.txt"
    manifest_text = format_manifest_text(manifest)
    manifest_path.write_text(manifest_text, encoding="utf-8")

    notice_path = write_notice_to_appdir(
        appdir=appdir,
        manifest=manifest,
        version=version,
        source_archive_filename=source_archive_filename,
        source_date_epoch=source_date_epoch,
    )

    return manifest_path, notice_path


# -- CLI entry point -----------------------------------------------------------

if __name__ == "__main__":
    import argparse

    parser = argparse.ArgumentParser(
        description="Generate manifest and NOTICE file for AppImage AppDir."
    )
    parser.add_argument("--appdir", required=True, help="Path to staged AppDir")
    parser.add_argument("--version", required=True, help="MQTTProbe version")
    parser.add_argument("--source-archive", required=True,
                        help="Companion source archive filename")
    parser.add_argument("--build-date", default=None,
                        help="Build date (ISO or epoch)")
    parser.add_argument("--output-dir", required=True,
                        help="Output directory for manifest file")
    parser.add_argument("--strict-unknown", action="store_true",
                        help="Exit non-zero if unknown ELFs are found")
    args = parser.parse_args()

    appdir = Path(args.appdir)
    if not appdir.is_dir():
        print(f"ERROR: AppDir not found: {appdir}", file=sys.stderr)
        sys.exit(1)

    output_dir = Path(args.output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)

    manifest_path, notice_path = generate_and_write(
        appdir=appdir,
        version=args.version,
        source_archive_filename=args.source_archive,
        output_dir=output_dir,
        build_date=args.build_date or "",
        source_date_epoch=args.build_date,
        strict_unknown=args.strict_unknown,
    )

    print(f"Manifest written to: {manifest_path}")
    print(f"NOTICE written to: {notice_path}")
    print(f"NOTICE size: {notice_path.stat().st_size} bytes")