"""Package manifest generator for MQTTProbe AppImage.

Enumerates actual staged files in an AppDir, maps linuxdeploy-collapsed
paths back to original system paths, queries dpkg for package ownership
and source versions, and produces a manifest covering all redistributable
ELFs, shared libraries, and bundled resources.

Non-Ubuntu ingredients (Photino.Native, NuGet packages, Velopack, pinned
vendors) are tracked separately with their own provenance.

Not a standalone script; no CLI, no side effects on import.
"""

import fnmatch
import subprocess
from dataclasses import dataclass, field
from pathlib import Path


@dataclass(frozen=True)
class BundledFile:
    """A single file bundled into the AppImage."""
    staged_path: str          # Path relative to AppDir root
    original_system_path: str  # Mapped original path on build system (empty if not from system)
    file_type: str            # "ELF", "shared-lib", "resource", "font", "config", "other"


@dataclass(frozen=True)
class SystemPackage:
    """An Ubuntu system package whose files are bundled."""
    binary_package: str
    binary_version: str
    source_package: str
    source_version: str
    license_hint: str  # Short license identifier from dpkg or guess
    bundled_files: list[str]  # staged paths of files from this package


@dataclass(frozen=True)
class VendorIngredient:
    """A non-Ubuntu bundled component with its own provenance."""
    name: str
    version: str
    source_url: str
    license_id: str
    description: str
    bundled_paths: list[str]


@dataclass
class PackageManifest:
    """Complete manifest of everything bundled in the AppImage."""
    build_date: str
    build_system_packages: list[SystemPackage] = field(default_factory=list)
    vendor_ingredients: list[VendorIngredient] = field(default_factory=list)
    all_bundled_files: list[BundledFile] = field(default_factory=list)
    unknown_files: list[str] = field(default_factory=list)

    def total_package_count(self) -> int:
        return len(self.build_system_packages) + len(self.vendor_ingredients)

    def total_file_count(self) -> int:
        return len(self.all_bundled_files)

    def unknown_count(self) -> int:
        return len(self.unknown_files)


# -- Path mapping helpers ------------------------------------------------------

# linuxdeploy collapses /usr/lib/x86_64-linux-gnu/... into AppDir/usr/lib/...
# We need to reverse this to find the original system path for dpkg-query.

_SYSTEM_LIB_DIRS = [
    "/usr/lib/x86_64-linux-gnu",
    "/usr/lib",
    "/lib/x86_64-linux-gnu",
    "/lib",
]


def _is_elf(path: Path) -> bool:
    """Check if file starts with ELF magic."""
    try:
        with open(path, "rb") as f:
            return f.read(4) == b"\x7fELF"
    except (OSError, PermissionError):
        return False


def _classify_file(staged_path: str, appdir: Path) -> str:
    """Classify a bundled file by its type."""
    full = appdir / staged_path
    if not full.exists():
        return "other"
    if full.is_dir():
        return "other"
    if _is_elf(full):
        # Check if it's a shared lib or executable
        name = full.name
        if ".so" in name or name.endswith(".so"):
            return "shared-lib"
        return "ELF"
    if full.suffix in (".woff2", ".ttf", ".otf", ".woff"):
        return "font"
    if full.suffix in (".xml", ".gschema.compiled", ".gresource"):
        return "config"
    if full.suffix in (".png", ".svg", ".ico", ".desktop"):
        return "resource"
    return "other"


def _map_staged_to_system(staged_rel: str) -> str:
    """Map a staged AppDir path to the most likely original system path.

    linuxdeploy collapses /usr/lib/x86_64-linux-gnu/... into AppDir/usr/lib/...
    For paths already containing x86_64-linux-gnu (explicitly staged like WebKit),
    the path is preserved as-is. Otherwise the multiarch path is returned as
    the primary candidate since that is where Ubuntu 22.04 places them.
    """
    if staged_rel.startswith("usr/lib/"):
        if "x86_64-linux-gnu" in staged_rel:
            return "/" + staged_rel
        rest = staged_rel[len("usr/lib/"):]
        return f"/usr/lib/x86_64-linux-gnu/{rest}"
    if staged_rel.startswith("usr/"):
        return "/" + staged_rel
    return ""


def _system_path_candidates(staged_rel: str) -> list[str]:
    """Return ordered candidate system paths for dpkg lookup.

    Tries the multiarch path first (most common on Ubuntu), then the plain
    path as fallback. On Ubuntu 22.04, /lib is a symlink to /usr/lib but
    dpkg tracks the canonical /lib/ path, so both are tried.
    """
    if staged_rel.startswith("usr/lib/"):
        if "x86_64-linux-gnu" in staged_rel:
            return ["/" + staged_rel]
        rest = staged_rel[len("usr/lib/"):]
        return [
            f"/usr/lib/x86_64-linux-gnu/{rest}",
            f"/lib/x86_64-linux-gnu/{rest}",
            f"/usr/lib/{rest}",
        ]
    if staged_rel.startswith("usr/"):
        return ["/" + staged_rel]
    return []


def _query_dpkg_owner(system_path: str) -> SystemPackage | None:
    """Query dpkg for the package that owns a given system path.

    Returns SystemPackage with binary+source version info, or None if
    not owned by any package.  Uses rsplit(': ', 1) to handle multiarch
    package names (e.g., "libfoo:amd64: /path").  Falls back to basename
    search when the exact path is not tracked by dpkg (e.g., libs in
    version-specific subdirectories like pulseaudio/).
    """
    # Try exact path first, then basename fallback
    paths_to_try = [system_path]
    basename = system_path.rsplit("/", 1)[-1] if "/" in system_path else ""
    if basename:
        paths_to_try.append(f"*/{basename}")

    for try_path in paths_to_try:
        try:
            result = subprocess.run(
                ["dpkg-query", "-S", try_path],
                capture_output=True, text=True, timeout=5,
            )
            if result.returncode != 0:
                continue
            # Output format: "package: /path/to/file" or "package:amd64: /path"
            # Use rsplit to correctly handle multiarch package names
            line = result.stdout.strip().split("\n")[0]
            if ": " not in line:
                continue
            pkg_part, _ = line.rsplit(": ", 1)
            pkg_name = pkg_part.strip()
            # Strip architecture suffix (e.g., "libfoo:amd64" -> "libfoo")
            if ":" in pkg_name:
                pkg_name = pkg_name.rsplit(":", 1)[0].strip()
            if not pkg_name or "diverted" in pkg_name:
                continue
            break  # Found a valid package
        except (OSError, subprocess.TimeoutExpired):
            continue
    else:
        return None

    # Get binary version
    try:
        bin_ver = subprocess.run(
            ["dpkg-query", "-W", "-f=${Version}", pkg_name],
            capture_output=True, text=True, timeout=5,
        ).stdout.strip()
    except (OSError, subprocess.TimeoutExpired):
        bin_ver = "unknown"

    # Get source package name
    try:
        src_name = subprocess.run(
            ["dpkg-query", "-W", "-f=${source:Package}", pkg_name],
            capture_output=True, text=True, timeout=5,
        ).stdout.strip()
    except (OSError, subprocess.TimeoutExpired):
        src_name = pkg_name

    # Get source version
    try:
        src_ver = subprocess.run(
            ["dpkg-query", "-W", "-f=${source:Version}", pkg_name],
            capture_output=True, text=True, timeout=5,
        ).stdout.strip()
    except (OSError, subprocess.TimeoutExpired):
        src_ver = bin_ver

    return SystemPackage(
        binary_package=pkg_name,
        binary_version=bin_ver,
        source_package=src_name or pkg_name,
        source_version=src_ver or bin_ver,
        license_hint="",
        bundled_files=[],
    )


# -- .NET runtime identification -----------------------------------------------

_DOTNET_LIB_PREFIXES = (
    "libcoreclr", "libhostpolicy", "libhostfxr", "libclrjit",
    "libclrgc", "libclrgcexp", "libmscordaccore", "libmscordbi",
    "libdbgshim", "libSystem.Globalization.Native",
    "libSystem.Net.Security.Native", "libSystem.IO.Compression.Native",
    "libSystem.Security.Cryptography.Native.OpenSsl",
    "libSystem.Native",
)

_APP_BINARIES = {"MqttProbe.Desktop"}


def _is_dotnet_runtime_file(staged_path: str) -> bool:
    """Check if a staged file is a .NET runtime native library.

    Checks both usr/bin/ (NuGet publish output) and usr/lib/ (linuxdeploy
    copies deployed as transitive dependencies).
    """
    if not (staged_path.startswith("usr/bin/") or staged_path.startswith("usr/lib/")):
        return False
    name = staged_path.rsplit("/", 1)[-1]
    if not (name.endswith(".so") or ".so." in name):
        return False
    return any(name.startswith(p) for p in _DOTNET_LIB_PREFIXES)


# -- Known vendor ingredients --------------------------------------------------

PHOTINO_NATIVE = VendorIngredient(
    name="Photino.Native",
    version="4.0.22",
    source_url="https://github.com/tryphotino/photino.Native",
    license_id="Apache-2.0",
    description="Native WebKit wrapper for .NET desktop apps (rebuilt from source on ubuntu-22.04)",
    bundled_paths=["usr/bin/Photino.Native.so"],
)

VELOPACK_LINUX = VendorIngredient(
    name="Velopack",
    version="1.2.0",
    source_url="https://github.com/velopack/velopack",
    license_id="MIT",
    description="Cross-platform installer/updater framework",
    bundled_paths=["usr/bin/UpdateNix"],
)

DOTNET_RUNTIME_NATIVE = VendorIngredient(
    name=".NET Runtime Native",
    version="10.0.401",
    source_url="https://github.com/dotnet/runtime",
    license_id="MIT",
    description=".NET runtime native libraries (from dotnet publish NuGet restore)",
    bundled_paths=[
        "usr/bin/libcoreclr.so",
        "usr/bin/libhostpolicy.so",
        "usr/bin/libhostfxr.so",
        "usr/bin/libclrjit.so",
        "usr/bin/libclrgc.so",
        "usr/bin/libclrgcexp.so",
        "usr/bin/libmscordaccore.so",
        "usr/bin/libmscordbi.so",
        "usr/bin/libSystem.Globalization.Native.so",
        "usr/bin/libSystem.Net.Security.Native.so",
        "usr/bin/libSystem.IO.Compression.Native.so",
        "usr/bin/libSystem.Security.Cryptography.Native.OpenSsl.so",
        "usr/bin/libSystem.Native.so",
    ],
)

LINUXDEPLOY_BINARY = VendorIngredient(
    name="linuxdeploy",
    version="07333c6",
    source_url="https://github.com/tauri-apps/binary-releases",
    license_id="MIT",
    description="AppImage packaging tool (used during build, not bundled)",
    bundled_paths=[],
)


# -- Manifest generation -------------------------------------------------------

def enumerate_staged_files(appdir: Path) -> list[BundledFile]:
    """Enumerate all files in a staged AppDir, classify and map to system paths."""
    files: list[BundledFile] = []
    for path in sorted(appdir.rglob("*")):
        if path.is_dir() or path.is_symlink():
            continue
        rel = str(path.relative_to(appdir)).replace("\\", "/")
        file_type = _classify_file(rel, appdir)
        system_path = _map_staged_to_system(rel)
        files.append(BundledFile(
            staged_path=rel,
            original_system_path=system_path,
            file_type=file_type,
        ))
    return files


def generate_manifest(appdir: Path, build_date: str = "") -> PackageManifest:
    """Generate a complete package manifest from a staged AppDir.

    Args:
        appdir: Path to the staged AppDir (after linuxdeploy, before vpk)
        build_date: ISO date string or UTC timestamp for the build

    Returns:
        PackageManifest with all system packages, vendor ingredients,
        and unknown files identified.
    """
    manifest = PackageManifest(build_date=build_date)

    # Enumerate all staged files
    manifest.all_bundled_files = enumerate_staged_files(appdir)

    _VENDOR_LIST = [PHOTINO_NATIVE, VELOPACK_LINUX, DOTNET_RUNTIME_NATIVE]

    # Group files by their system package owner
    package_files: dict[str, list[str]] = {}  # binary_package -> [staged_paths]
    package_cache: dict[str, SystemPackage] = {}

    for bf in manifest.all_bundled_files:
        # Skip application binaries
        if bf.staged_path.startswith("usr/bin/"):
            basename = bf.staged_path.rsplit("/", 1)[-1]
            if basename in _APP_BINARIES:
                continue

        # Check vendor ingredients for ALL files, not just empty-system_path
        is_vendor = False
        for vendor in _VENDOR_LIST:
            for vp in vendor.bundled_paths:
                if "*" in vp:
                    if fnmatch.fnmatch(bf.staged_path, vp):
                        is_vendor = True
                        break
                elif bf.staged_path == vp:
                    is_vendor = True
                    break
            if is_vendor:
                break
        if is_vendor:
            continue

        # Try candidate system paths for dpkg lookup
        candidates = _system_path_candidates(bf.staged_path)
        pkg = None
        for candidate in candidates:
            pkg = _query_dpkg_owner(candidate)
            if pkg is not None:
                break

        if pkg is None:
            # .NET runtime libs not in the static vendor list
            if _is_dotnet_runtime_file(bf.staged_path):
                continue
            if bf.file_type in ("ELF", "shared-lib"):
                manifest.unknown_files.append(bf.staged_path)
            continue

        key = pkg.binary_package
        if key not in package_cache:
            package_cache[key] = pkg
        if key not in package_files:
            package_files[key] = []
        package_files[key].append(bf.staged_path)

    # Build SystemPackage entries with file lists
    for pkg_name, staged_paths in package_files.items():
        pkg = package_cache[pkg_name]
        manifest.build_system_packages.append(SystemPackage(
            binary_package=pkg.binary_package,
            binary_version=pkg.binary_version,
            source_package=pkg.source_package,
            source_version=pkg.source_version,
            license_hint=pkg.license_hint,
            bundled_files=staged_paths,
        ))

    # Always include known vendor ingredients
    manifest.vendor_ingredients = [PHOTINO_NATIVE, VELOPACK_LINUX, DOTNET_RUNTIME_NATIVE, LINUXDEPLOY_BINARY]

    return manifest


def format_manifest_text(manifest: PackageManifest) -> str:
    """Format manifest as human-readable text for the source archive."""
    lines = [
        f"# MQTTProbe AppImage Package Manifest",
        f"# Build date: {manifest.build_date}",
        f"# Total system packages: {len(manifest.build_system_packages)}",
        f"# Total vendor ingredients: {len(manifest.vendor_ingredients)}",
        f"# Total bundled files: {manifest.total_file_count()}",
        f"# Unknown/unclaimed ELFs: {manifest.unknown_count()}",
        "",
        "## System Packages",
        "",
        "# binary-package | binary-version | source-package | source-version | file-count",
    ]

    for pkg in sorted(manifest.build_system_packages, key=lambda p: p.binary_package):
        lines.append(
            f"{pkg.binary_package} | {pkg.binary_version} | "
            f"{pkg.source_package} | {pkg.source_version} | "
            f"{len(pkg.bundled_files)}"
        )

    lines.extend(["", "## Vendor Ingredients", ""])
    for v in manifest.vendor_ingredients:
        lines.append(f"{v.name} {v.version} ({v.license_id}) - {v.description}")
        if v.bundled_paths:
            for p in v.bundled_paths:
                lines.append(f"  {p}")

    if manifest.unknown_files:
        lines.extend(["", "## Unknown/Unclaimed ELFs", ""])
        for f in manifest.unknown_files:
            lines.append(f"  {f}")

    return "\n".join(lines) + "\n"


# -- CLI entry point -----------------------------------------------------------

if __name__ == "__main__":
    import argparse
    import sys

    parser = argparse.ArgumentParser(
        description="Generate package manifest from a staged AppDir."
    )
    parser.add_argument("--appdir", required=True, help="Path to staged AppDir")
    parser.add_argument("--build-date", default="", help="Build date (ISO or epoch)")
    parser.add_argument("--output", required=True, help="Output manifest file path")
    parser.add_argument("--strict", action="store_true",
                        help="Exit non-zero if unknown ELFs are found")
    args = parser.parse_args()

    appdir = Path(args.appdir)
    if not appdir.is_dir():
        print(f"ERROR: AppDir not found: {appdir}", file=sys.stderr)
        sys.exit(1)

    manifest = generate_manifest(appdir, args.build_date)
    text = format_manifest_text(manifest)
    Path(args.output).write_text(text, encoding="utf-8")

    print(f"Manifest: {len(manifest.build_system_packages)} system packages, "
          f"{len(manifest.vendor_ingredients)} vendor ingredients, "
          f"{manifest.total_file_count()} files, "
          f"{manifest.unknown_count()} unknown ELFs")

    if args.strict and manifest.unknown_count() > 0:
        print(f"ERROR: {manifest.unknown_count()} unknown ELF(s) found in strict mode",
              file=sys.stderr)
        for f in manifest.unknown_files:
            print(f"  UNKNOWN: {f}", file=sys.stderr)
        sys.exit(1)