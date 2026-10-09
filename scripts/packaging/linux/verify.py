#!/usr/bin/env python3
"""
Static gate for Linux AppImage/AppDir portability.

Verifies bundled ELF binaries against a GLIBC baseline (default 2.35),
checks dependency resolution against an explicit baseline runtime root,
validates WebKit path relocation in the runtime library, confirms executable
permissions, requires a root .DirIcon resolving to a PNG, and catches
escaping, broken, or absolute symlinks.

Does NOT execute any inspected ELF binaries. Uses readelf/objdump for static
analysis only.

Usage:
  python3 scripts/packaging/linux/verify.py AppImage --baseline-root /path/to/ubuntu22.04-root
  python3 scripts/packaging/linux/verify.py squashfs-root/ --appdir --baseline-root /path/to/root
"""

import argparse
import shutil
import stat
import subprocess
import sys
import tempfile
from pathlib import Path

from _elf import (
    ELF_MAGIC,
    PROVIDER_SEARCH_DIRS,
    TOOL_FAILURE,
    RpathInfo,
    classify_rpath,
    find_libstdcxx,
    find_squashfs_offsets,
    format_version,
    get_rpath_runpath,
    is_dynamic_elf,
    parse_squashfs_superblock,
    parse_version,
    read_elf_provided_versions,
    read_elf_requirements,
    read_needed_libs,
    resolve_provider,
    run_readelf,
    scan_baseline_libs,
    validate_squashfs_superblock,
    verify_baseline_identity,
    _expand_origin,
)

APPIMAGE_TYPE2_MAGIC = b"AI\x02"
PNG_MAGIC = b"\x89PNG\r\n\x1a\n"
DEFAULT_MAX_GLIBC = (2, 35)

WEBKIT_HOST_PREFIX = "/usr/lib/x86_64-linux-gnu/webkit2gtk-4.1"
# Both prefixes MUST be exactly 40 bytes. The double-slash preserves the
# full prefix length so no suffix is truncated in the ELF binary.
WEBKIT_RELATIVE_PREFIX = "././/lib/x86_64-linux-gnu/webkit2gtk-4.1"
# Old truncated prefix (39 bytes + NUL = 40 but truncates suffix). Reject if found.
OLD_WEBKIT_RELATIVE_PREFIX = "././lib/x86_64-linux-gnu/webkit2gtk-4.1"
WEBKIT_DIR_REL = "lib/x86_64-linux-gnu/webkit2gtk-4.1"

REQUIRED_WEBKIT_BINARIES = [
    f"{WEBKIT_DIR_REL}/WebKitWebProcess",
    f"{WEBKIT_DIR_REL}/WebKitNetworkProcess",
    # libwebkit2gtk-4.1.so.0 may be in the webkit subdir OR moved to usr/lib/
    # by linuxdeploy. We check for it separately in check_resources.
]
# The main webkit library can live in either location after linuxdeploy runs
WEBKIT_LIB_LOCATIONS = [
    f"{WEBKIT_DIR_REL}/libwebkit2gtk-4.1.so.0",
    "lib/libwebkit2gtk-4.1.so.0",
]
INJECTED_BUNDLE_NAME = "libwebkit2gtkinjectedbundle.so"
INJECTED_BUNDLE_DIR = f"{WEBKIT_DIR_REL}/injected-bundle"


# -- AppImage extraction ------------------------------------------------------


def detect_input_type(path: Path) -> str:
    with open(path, "rb") as f:
        header = f.read(16)
    if header[:4] != b"\x7fELF":
        return "appimage-unknown"
    if len(header) >= 11 and header[8:11] == APPIMAGE_TYPE2_MAGIC:
        return "appimage-type2"
    return "appimage-elf"


def extract_appimage(appimage_path: Path, dest_dir: Path) -> Path:
    """Extract AppImage, enumerating all squashfs candidates and validating each."""
    if not shutil.which("unsquashfs"):
        raise RuntimeError(
            "unsquashfs not found. Install: sudo apt install squashfs-tools"
        )
    candidates = find_squashfs_offsets(appimage_path)
    if not candidates:
        raise ValueError(
            "No valid squashfs superblock found in file "
            "(scanned all magic candidates, none passed validation)"
        )
    offset, sb = candidates[-1]
    root = dest_dir / "squashfs-root"
    result = subprocess.run(
        ["unsquashfs", "-f", "-d", str(root), "-o", str(offset),
         str(appimage_path)],
        capture_output=True, text=True, timeout=60,
    )
    if result.returncode != 0:
        raise RuntimeError(
            f"unsquashfs failed (exit {result.returncode}): "
            f"{result.stderr.strip()[:200]}"
        )
    return root


# -- ELF discovery ------------------------------------------------------------


def find_elf_files(root: Path) -> list[Path]:
    found = []
    for path in root.rglob("*"):
        if not path.is_file() or path.is_symlink():
            continue
        try:
            with open(path, "rb") as f:
                if f.read(4) == ELF_MAGIC:
                    found.append(path)
        except (OSError, PermissionError):
            continue
    return found


# -- Checks -------------------------------------------------------------------


def check_glibc_versions(
    elf_files: list[Path], max_glibc: tuple[int, ...]
) -> list[str]:
    issues = []
    max_str = format_version(max_glibc)
    for elf in elf_files:
        result = read_elf_requirements(elf)
        if result[0] is TOOL_FAILURE:
            issues.append(f"GLIBC: {elf.name}: readelf/objdump failed (fail-closed)")
            continue
        glibc_reqs = result[0]
        for v in sorted(glibc_reqs, key=parse_version):
            if parse_version(v) > max_glibc:
                issues.append(
                    f"GLIBC: {elf.name} requires GLIBC_{v} (max {max_str})"
                )
    return issues


def check_symlinks(root: Path) -> list[str]:
    """Detect escaping, broken, or absolute symlinks."""
    issues = []
    root_resolved = root.resolve()
    for path in root.rglob("*"):
        if not path.is_symlink():
            continue
        target = path.readlink()
        if target.is_absolute():
            issues.append(
                f"SYMLINK: absolute: {path.relative_to(root)} -> {target}"
            )
            continue
        try:
            resolved = path.resolve()
        except (OSError, ValueError):
            issues.append(f"SYMLINK: broken: {path.relative_to(root)} -> {target}")
            continue
        if not resolved.exists():
            issues.append(f"SYMLINK: broken: {path.relative_to(root)} -> {target}")
            continue
        if not resolved.is_relative_to(root_resolved):
            issues.append(
                f"SYMLINK: escapes root: {path.relative_to(root)} -> {target}"
            )
    return issues


def check_permissions(root: Path) -> list[str]:
    issues = []

    def _is_executable(p: Path) -> bool:
        try:
            return bool(p.stat().st_mode & (stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH))
        except OSError:
            return False

    apprun = root / "AppRun"
    if not apprun.exists():
        issues.append("PERM: AppRun missing")
    elif not _is_executable(apprun):
        issues.append("PERM: AppRun is not executable")

    main_bin = root / "usr" / "bin" / "MqttProbe.Desktop"
    if not main_bin.exists():
        issues.append("PERM: usr/bin/MqttProbe.Desktop missing")
    elif not _is_executable(main_bin):
        issues.append("PERM: usr/bin/MqttProbe.Desktop is not executable")

    webkit_dir = root / "usr" / WEBKIT_DIR_REL
    if webkit_dir.is_dir():
        for name in ("WebKitWebProcess", "WebKitNetworkProcess", "WebKitGPUProcess"):
            p = webkit_dir / name
            if p.exists() and not _is_executable(p):
                issues.append(f"PERM: {WEBKIT_DIR_REL}/{name} is not executable")
    return issues


def check_resources(root: Path) -> list[str]:
    usr = root / "usr"
    issues = []
    for rel in REQUIRED_WEBKIT_BINARIES:
        if not (usr / rel).exists():
            issues.append(f"RESOURCE: missing: usr/{rel}")

    # libwebkit2gtk-4.1.so.0: linuxdeploy may move it from the webkit subdir
    # to usr/lib/. Check either location.
    webkit_lib_found = any((usr / loc).exists() for loc in WEBKIT_LIB_LOCATIONS)
    if not webkit_lib_found:
        checked = ", ".join(f"usr/{loc}" for loc in WEBKIT_LIB_LOCATIONS)
        issues.append(f"RESOURCE: libwebkit2gtk-4.1.so.0 not found (checked: {checked})")

    bundle_dir = usr / INJECTED_BUNDLE_DIR
    if not bundle_dir.is_dir():
        issues.append(f"RESOURCE: missing injected-bundle dir: usr/{INJECTED_BUNDLE_DIR}")
    else:
        exact = bundle_dir / INJECTED_BUNDLE_NAME
        if not exact.exists():
            issues.append(
                f"RESOURCE: missing {INJECTED_BUNDLE_NAME} in "
                f"usr/{INJECTED_BUNDLE_DIR}"
            )

    # Deliberate exclusion: libcoreclrtraceptprovider.so is an optional .NET
    # LTTng tracing provider that links liblttng-ust.so.0 (not on 22.04).
    trace_provider = usr / "bin" / "libcoreclrtraceptprovider.so"
    if trace_provider.exists():
        issues.append(
            "RESOURCE: libcoreclrtraceptprovider.so should be removed "
            "(LTTng provider, links liblttng-ust.so.0 unavailable on 22.04)"
        )

    return issues


def check_diricon(root: Path) -> list[str]:
    """AppImageHub appdir-lint requires a root .DirIcon resolving to a PNG."""
    diricon = root / ".DirIcon"
    if not diricon.is_symlink() and not diricon.exists():
        return ["DIRICON: missing .DirIcon in AppDir root"]

    try:
        resolved = diricon.resolve(strict=True)
    except (OSError, ValueError):
        target = diricon.readlink() if diricon.is_symlink() else diricon.name
        return [f"DIRICON: broken .DirIcon -> {target}"]

    if not resolved.is_file():
        return [f"DIRICON: .DirIcon does not resolve to a regular file: {resolved}"]

    try:
        with open(resolved, "rb") as f:
            header = f.read(8)
    except OSError as e:
        return [f"DIRICON: cannot read {resolved.name}: {e}"]

    if header != PNG_MAGIC:
        return [f"DIRICON: .DirIcon target is not a PNG: {resolved.name}"]
    return []


def check_webkit_paths(root: Path, baseline_root: Path | None) -> list[str]:
    """Check the resolved WebKit runtime library for prefix correctness."""
    issues = []
    host_bytes = WEBKIT_HOST_PREFIX.encode("utf-8")
    relative_bytes = WEBKIT_RELATIVE_PREFIX.encode("utf-8")
    old_relative_bytes = OLD_WEBKIT_RELATIVE_PREFIX.encode("utf-8")

    lib_path = resolve_provider(root, "libwebkit2gtk-4.1.so.0", baseline_root)
    if lib_path is None:
        issues.append("WEBKIT: libwebkit2gtk-4.1.so.0 not found in any search location")
        return issues

    try:
        size = lib_path.stat().st_size
        if size > 200 * 1024 * 1024:
            issues.append(f"WEBKIT: {lib_path.name} too large ({size} bytes), refusing to read")
            return issues
        data = lib_path.read_bytes()
    except (OSError, PermissionError) as e:
        issues.append(f"WEBKIT: cannot read {lib_path}: {e}")
        return issues

    has_host = host_bytes in data
    has_relative = relative_bytes in data
    has_old_relative = old_relative_bytes in data
    if has_host:
        also = " (relative prefix also present)" if has_relative else ""
        issues.append(
            f"WEBKIT: unrelocated host prefix in {lib_path}: "
            f"{WEBKIT_HOST_PREFIX}{also}"
        )
    if has_old_relative:
        issues.append(
            f"WEBKIT: old truncated relative prefix in {lib_path}: "
            f"{OLD_WEBKIT_RELATIVE_PREFIX} (expected {WEBKIT_RELATIVE_PREFIX})"
        )
    if not has_relative:
        issues.append(
            f"WEBKIT: relative prefix missing in {lib_path}: "
            f"expected {WEBKIT_RELATIVE_PREFIX}"
        )
    return issues


def check_dependencies(
    root: Path, elf_files: list[Path], baseline_libs: set[str]
) -> list[str]:
    """Check that DT_NEEDED libs are bundled, on baseline, or resolvable.

    Search order (matching ld.so precedence):
    - No RUNPATH: RPATH dirs -> bundle lib dirs -> baseline
    - RUNPATH present: bundle lib dirs -> RUNPATH dirs -> baseline
    """
    bundled: set[str] = set()
    for rel in ("lib", "lib/x86_64-linux-gnu", "lib/x86_64-linux-gnu/webkit2gtk-4.1"):
        lib_dir = root / "usr" / rel
        if lib_dir.is_dir():
            for p in lib_dir.iterdir():
                if p.is_file() and ".so" in p.name:
                    bundled.add(p.name)
    issues: list[str] = []
    for elf in elf_files:
        raw_rpath, raw_runpath = get_rpath_runpath(elf)
        rpath_dirs: list[Path] = []
        runpath_dirs: list[Path] = []

        def _process_path(raw: str, label: str) -> list[Path]:
            info = classify_rpath(raw)
            dirs: list[Path] = []
            if info.unsupported:
                for u in info.unsupported:
                    issues.append(
                        f"DEP: {elf.name} has unsupported {label} entry [{u}] "
                        f"(full: {raw!r})"
                    )
            for entry in info.supported:
                expanded = _expand_origin(entry, elf, root)
                if expanded is not None and expanded.is_dir():
                    dirs.append(expanded)
            return dirs

        if raw_rpath and not raw_runpath:
            rpath_dirs = _process_path(raw_rpath, "RPATH")
        elif raw_rpath and raw_runpath:
            issues.append(
                f"DEP: {elf.name} has RPATH [{raw_rpath}] overridden by "
                f"RUNPATH [{raw_runpath}]"
            )
        if raw_runpath:
            runpath_dirs = _process_path(raw_runpath, "RUNPATH")

        needed = read_needed_libs(elf)
        if needed is TOOL_FAILURE:
            issues.append(f"DEP: {elf.name}: readelf -d failed (cannot verify)")
            continue
        for lib in needed:
            if lib in bundled or lib in baseline_libs:
                continue
            found = any((rd / lib).exists() for rd in rpath_dirs)
            if not found:
                found = any((rd / lib).exists() for rd in runpath_dirs)
            if found:
                continue
            issues.append(
                f"DEP: {elf.name} needs {lib} "
                f"(not bundled, not on baseline, not in RPATH/RUNPATH)"
            )
    return issues


def check_glibcxx_satisfaction(
    root: Path,
    elf_files: list[Path],
    baseline_root: Path | None,
) -> list[str]:
    """Verify GLIBCXX and CXXABI requirements via exact set membership."""
    result = find_libstdcxx(root, baseline_root)
    if result is None:
        return [
            "GLIBCXX/CXXABI: libstdc++.so.6 not found "
            "(not bundled, not on baseline); cannot verify"
        ]
    libstdcxx, source = result
    provided_glibcxx, provided_cxxabi = read_elf_provided_versions(libstdcxx)
    if not provided_glibcxx and not provided_cxxabi:
        return [
            f"GLIBCXX/CXXABI: could not read definitions from {source} "
            f"libstdc++ ({libstdcxx})"
        ]
    issues = []
    for elf in elf_files:
        req_result = read_elf_requirements(elf)
        if req_result[0] is TOOL_FAILURE:
            issues.append(f"GLIBCXX/CXXABI: {elf.name}: readelf/objdump failed (fail-closed)")
            continue
        _, req_glibcxx, req_cxxabi = req_result
        for v in sorted(req_glibcxx):
            if v not in provided_glibcxx:
                issues.append(
                    f"GLIBCXX: {elf.name} requires GLIBCXX_{v} "
                    f"({source} libstdc++ does not provide it)"
                )
        for v in sorted(req_cxxabi):
            if v not in provided_cxxabi:
                issues.append(
                    f"CXXABI: {elf.name} requires CXXABI_{v} "
                    f"({source} libstdc++ does not provide it)"
                )
    return issues


def main() -> int:
    p = argparse.ArgumentParser(
        description="Static gate for Linux AppImage/AppDir portability.",
    )
    p.add_argument("path", help="Path to AppImage file or extracted AppDir")
    p.add_argument(
        "--max-glibc", default=format_version(DEFAULT_MAX_GLIBC),
        help=f"Max allowed GLIBC version (default: {format_version(DEFAULT_MAX_GLIBC)})",
    )
    p.add_argument("--appdir", action="store_true", help="Treat path as AppDir (skip extraction)")
    p.add_argument(
        "--baseline-root", required=True,
        help="Path to clean Ubuntu 22.04 runtime root (e.g. extracted docker image)",
    )
    args = p.parse_args()

    try:
        max_glibc = parse_version(args.max_glibc)
    except ValueError:
        print(f"ERROR: invalid --max-glibc: {args.max_glibc!r}")
        return 1

    input_path = Path(args.path).resolve()
    if not input_path.exists():
        print(f"ERROR: path not found: {input_path}")
        return 1

    baseline_root = Path(args.baseline_root).resolve()
    if not baseline_root.is_dir():
        print(f"ERROR: baseline-root not found: {baseline_root}")
        return 1

    # Verify baseline identity (Ubuntu 22.04).
    identity_err = verify_baseline_identity(baseline_root)
    if identity_err:
        print(f"ERROR: baseline identity: {identity_err}")
        print("  Provide --baseline-root pointing to a clean Ubuntu 22.04 runtime root")
        return 1

    baseline_libs = scan_baseline_libs(baseline_root)
    if not baseline_libs:
        print(f"ERROR: no libraries found in baseline root: {baseline_root}")
        return 1

    print(f"\n=== Verify Linux AppImage/AppDir ===")
    print(f"  Input: {input_path}")
    print(f"  Max GLIBC: {format_version(max_glibc)}")
    print(f"  Baseline root: {baseline_root} ({len(baseline_libs)} libs)")
    all_issues: list[str] = []
    tmpdir: str | None = None

    try:
        if args.appdir or input_path.is_dir():
            appdir = input_path
            print("  Mode: AppDir (no extraction)")
        else:
            input_type = detect_input_type(input_path)
            print(f"  Type: {input_type}")
            if input_type == "appimage-unknown":
                print("  WARNING: not a recognized AppImage; attempting extraction")
            tmpdir = tempfile.mkdtemp(prefix="appimage-verify-")
            appdir = extract_appimage(input_path, Path(tmpdir))
            print(f"  Extracted to: {appdir}")

        elf_files = find_elf_files(appdir)
        print(f"  ELF files found: {len(elf_files)}")
        for ef in elf_files:
            print(f"    - {ef.relative_to(appdir)}")
        print("\n--- Checks ---")
        checks = [
            ("GLIBC", lambda: check_glibc_versions(elf_files, max_glibc)),
            ("Symlinks", lambda: check_symlinks(appdir)),
            ("Permissions", lambda: check_permissions(appdir)),
            ("Resources", lambda: check_resources(appdir)),
            ("DirIcon", lambda: check_diricon(appdir)),
            ("WebKit paths", lambda: check_webkit_paths(appdir, baseline_root)),
            ("Dependencies", lambda: check_dependencies(appdir, elf_files, baseline_libs)),
            ("GLIBCXX/CXXABI", lambda: check_glibcxx_satisfaction(appdir, elf_files, baseline_root)),
        ]
        for name, check_fn in checks:
            print(f"  {name}...", end=" ", flush=True)
            r = check_fn()
            print(f"{len(r)} issue(s)")
            all_issues.extend(r)

    finally:
        if tmpdir:
            shutil.rmtree(tmpdir, ignore_errors=True)

    print()
    if not all_issues:
        print("=== All checks passed ===")
        return 0
    for issue in all_issues:
        print(f"  [FAIL] {issue}")
    print(f"\n=== {len(all_issues)} check(s) FAILED ===")
    return 1


if __name__ == "__main__":
    sys.exit(main())