"""Shared helpers for the Linux AppImage verifier.

Squashfs superblock parsing/validation, baseline resolution, readelf/objdump
wrappers, version parsing, RPATH classification, and provider resolution.
Imported by the verifier CLI and its tests.

Not a standalone script; no CLI, no side effects on import.
"""

import os
import re
import struct
import subprocess
from dataclasses import dataclass
from pathlib import Path

ELF_MAGIC = b"\x7fELF"

# Sentinel: readelf/objdump returned no output. Distinguish from "no dynamic
# section" (legitimate static ELF) vs tool failure (must fail closed).
TOOL_FAILURE: str = "__TOOL_FAILURE__"

# -- Version parsing ----------------------------------------------------------


def parse_version(s: str) -> tuple[int, ...]:
    return tuple(int(p) for p in s.split("."))


def format_version(t: tuple[int, ...]) -> str:
    return ".".join(str(p) for p in t)


# -- Squashfs superblock ------------------------------------------------------

SQFS_MAGIC_LE = 0x73717368  # hsqs
SQFS_MAGIC_BE = 0x73687173  # sqsh
SQFS_SUPERBLOCK_SIZE = 96
SQFS_VALID_BLOCK_SIZES = {4096, 8192, 16384, 32768, 65536, 131072}


@dataclass(frozen=True)
class SquashfsSuperblock:
    magic: int
    inode_count: int
    block_size: int
    major_version: int
    minor_version: int
    bytes_used: int


def parse_squashfs_superblock(path: Path, offset: int) -> SquashfsSuperblock:
    with open(path, "rb") as f:
        f.seek(offset)
        data = f.read(SQFS_SUPERBLOCK_SIZE)
    if len(data) < SQFS_SUPERBLOCK_SIZE:
        raise ValueError(
            f"Truncated superblock: got {len(data)} bytes, need {SQFS_SUPERBLOCK_SIZE}"
        )
    magic = struct.unpack_from("<I", data, 0)[0]
    if magic == SQFS_MAGIC_LE:
        endian = "<"
    elif magic == SQFS_MAGIC_BE:
        endian = ">"
    else:
        raise ValueError(f"Bad squashfs magic: 0x{magic:08x}")
    return SquashfsSuperblock(
        magic=magic,
        inode_count=struct.unpack_from(f"{endian}I", data, 4)[0],
        block_size=struct.unpack_from(f"{endian}I", data, 12)[0],
        major_version=struct.unpack_from(f"{endian}H", data, 28)[0],
        minor_version=struct.unpack_from(f"{endian}H", data, 30)[0],
        bytes_used=struct.unpack_from(f"{endian}Q", data, 40)[0],
    )


def validate_squashfs_superblock(
    sb: SquashfsSuperblock, file_size: int, offset: int
) -> list[str]:
    issues = []
    if sb.magic not in (SQFS_MAGIC_LE, SQFS_MAGIC_BE):
        issues.append(f"bad magic 0x{sb.magic:08x}")
    if sb.major_version != 4:
        issues.append(f"unsupported major version {sb.major_version} (need 4)")
    if sb.block_size not in SQFS_VALID_BLOCK_SIZES:
        if sb.block_size == 0 or (sb.block_size & (sb.block_size - 1)) != 0:
            issues.append(f"invalid block size {sb.block_size}")
    if sb.inode_count == 0:
        issues.append("inode count is 0")
    if sb.bytes_used <= 0:
        issues.append(f"bytes_used {sb.bytes_used} is not positive")
    elif offset + sb.bytes_used > file_size:
        issues.append(
            f"offset {offset} + bytes_used {sb.bytes_used} = "
            f"{offset + sb.bytes_used} exceeds file size {file_size}"
        )
    return issues


def find_squashfs_offsets(path: Path) -> list[tuple[int, SquashfsSuperblock]]:
    """Enumerate ALL squashfs magic candidates, validate each superblock."""
    chunk_size = 64 * 1024
    file_size = path.stat().st_size
    results: list[tuple[int, SquashfsSuperblock]] = []
    with open(path, "rb") as f:
        prev_tail = b""
        while True:
            chunk = f.read(chunk_size)
            if not chunk:
                break
            data = prev_tail + chunk
            for magic_bytes in (b"hsqs", b"sqsh"):
                search_from = 0
                while True:
                    idx = data.find(magic_bytes, search_from)
                    if idx == -1:
                        break
                    offset = f.tell() - len(chunk) - len(prev_tail) + idx
                    search_from = idx + 1
                    if offset + SQFS_SUPERBLOCK_SIZE > file_size:
                        continue
                    try:
                        sb = parse_squashfs_superblock(path, offset)
                        if not validate_squashfs_superblock(sb, file_size, offset):
                            results.append((offset, sb))
                    except ValueError:
                        continue
            prev_tail = data[-3:] if len(data) >= 3 else data
    return results


# -- Baseline resolution (explicit root required) -----------------------------


def parse_ldconfig(output: str) -> set[str]:
    """Parse ldconfig -p output, return set of library sonames available."""
    libs: set[str] = set()
    for line in output.splitlines():
        m = re.match(r"\s+(\S+)\s+\(.*\)\s+=>\s+", line)
        if m:
            libs.add(m.group(1))
    return libs


def verify_baseline_identity(baseline_root: Path) -> str | None:
    """Check /etc/os-release for Ubuntu 22.04 identity.

    Returns None if verified, error message if not.
    """
    os_release = baseline_root / "etc" / "os-release"
    if not os_release.is_file():
        return f"/etc/os-release not found in {baseline_root}"
    try:
        text = os_release.read_text(encoding="utf-8")
    except OSError as e:
        return f"Cannot read {os_release}: {e}"
    id_match = re.search(r'^ID=(.+)$', text, re.MULTILINE)
    ver_match = re.search(r'^VERSION_ID=(.+)$', text, re.MULTILINE)
    if not id_match or not ver_match:
        return "/etc/os-release missing ID or VERSION_ID"
    bid = id_match.group(1).strip('"').lower()
    bver = ver_match.group(1).strip('"')
    if bid != "ubuntu":
        return f"Baseline is not Ubuntu (ID={bid!r})"
    if bver != "22.04":
        return f"Baseline is not Ubuntu 22.04 (VERSION_ID={bver!r})"
    return None


def scan_baseline_libs(baseline_root: Path) -> set[str]:
    """Scan baseline root for available library sonames.

    Handles absolute symlinks by resolving within baseline root.
    """
    names: set[str] = set()
    search_dirs = [
        baseline_root / "usr" / "lib",
        baseline_root / "usr" / "lib" / "x86_64-linux-gnu",
        baseline_root / "lib" / "x86_64-linux-gnu",
    ]
    for d in search_dirs:
        if not d.is_dir():
            continue
        for f in d.iterdir():
            if f.is_symlink():
                target = f.readlink()
                if target.is_absolute():
                    resolved = baseline_root / str(target).lstrip("/")
                    if resolved.is_file():
                        names.add(f.name)
                elif f.exists():
                    names.add(f.name)
            elif f.is_file() and ".so" in f.name:
                names.add(f.name)
    return names


# -- RPATH classification ----------------------------------------------------

# $ORIGIN-relative RPATH is standard in linuxdeploy bundles. The ld.so loader
# expands $ORIGIN to the directory containing the ELF binary.
_ORIGIN_RE = re.compile(r"^\$\{?ORIGIN\}?(/.*)?$")
_ORIGIN_TOKEN_RE = re.compile(r'\$\{?ORIGIN\}?')


@dataclass(frozen=True)
class RpathInfo:
    raw: str
    entries: list[str]
    supported: list[str]   # $ORIGIN-relative entries
    unsupported: list[str]  # absolute, empty, or unresolved


def classify_rpath(raw: str) -> RpathInfo:
    """Classify RPATH/RUNPATH entries into supported and unsupported.

    Supported: $ORIGIN-relative paths (e.g. $ORIGIN/../lib, ${ORIGIN}/lib).
    Unsupported: absolute paths, empty entries, unresolved expansions.
    """
    entries = [e for e in raw.split(":") if e]  # skip empty entries
    supported: list[str] = []
    unsupported: list[str] = []
    for entry in entries:
        if _ORIGIN_RE.match(entry):
            supported.append(entry)
        elif entry.startswith("/"):
            unsupported.append(f"absolute:{entry}")
        else:
            unsupported.append(f"unresolved:{entry}")
    return RpathInfo(raw=raw, entries=entries, supported=supported, unsupported=unsupported)


def has_rpath(elf_path: Path) -> str | None:
    """Check if an ELF has RPATH/RUNPATH. Returns the raw path string or None."""
    out = run_readelf(elf_path, "-d")
    if out is None:
        return None
    for line in out.splitlines():
        m = re.search(r"\((?:RPATH|RUNPATH)\)\s+.*\[(.+?)\]", line)
        if m:
            return m.group(1)
    return None


def get_rpath_runpath(elf_path: Path) -> tuple[str | None, str | None]:
    """Get RPATH and RUNPATH separately. Returns (rpath, runpath)."""
    out = run_readelf(elf_path, "-d")
    if out is None:
        return None, None
    rpath = runpath = None
    for line in out.splitlines():
        mr = re.search(r"\(RPATH\)\s+.*\[(.+?)\]", line)
        if mr:
            rpath = mr.group(1)
        mru = re.search(r"\(RUNPATH\)\s+.*\[(.+?)\]", line)
        if mru:
            runpath = mru.group(1)
    return rpath, runpath


# -- Provider resolution (search order) ---------------------------------------

PROVIDER_SEARCH_DIRS = [
    "lib",
    "lib/x86_64-linux-gnu",
    "lib/x86_64-linux-gnu/webkit2gtk-4.1",
]

def _expand_origin(rpath_entry: str, elf_path: Path, appdir: Path | None = None) -> Path | None:
    """Expand $ORIGIN in an RPATH entry, replacing only the token.

    Preserves suffix. If appdir given, rejects resolved paths that escape it.
    """
    origin_dir = elf_path.parent
    expanded = _ORIGIN_TOKEN_RE.sub(lambda _: str(origin_dir), rpath_entry)
    result = Path(expanded)
    if appdir is not None:
        try:
            if not result.resolve().is_relative_to(appdir.resolve()):
                return None
        except (OSError, ValueError):
            return None
    return result


def resolve_provider(
    appdir: Path, lib_name: str, baseline_root: Path | None = None,
    rpath_dirs: list[Path] | None = None,
) -> Path | None:
    """Find a library provider through the declared search order.

    Search order (matching ld.so precedence):
    1. RPATH directories (from $ORIGIN-expanded entries) if provided
    2. appdir/usr subdirectories (bundled)
    3. baseline root if provided

    Returns resolved path or None.
    """
    search_roots: list[tuple[Path, str]] = []
    # RPATH first (ld.so precedence: RPATH before RUNPATH, before system)
    if rpath_dirs:
        for rd in rpath_dirs:
            search_roots.append((rd, "rpath"))
    # Bundled
    for rel_dir in PROVIDER_SEARCH_DIRS:
        search_roots.append((appdir / "usr" / rel_dir, "bundled"))
    # Baseline
    if baseline_root:
        for rel_dir in PROVIDER_SEARCH_DIRS:
            search_roots.append((baseline_root / "usr" / rel_dir, "baseline"))

    for root, _source in search_roots:
        candidate = root / lib_name
        # Baseline absolute symlinks: resolve within baseline_root, not the host.
        if _source == "baseline" and baseline_root and candidate.is_symlink():
            target = candidate.readlink()
            if target.is_absolute():
                within = baseline_root / str(target).lstrip("/")
                if within.is_file():
                    return within
        if candidate.exists():
            try:
                resolved = candidate.resolve()
                # For bundled/rpath, ensure contained within appdir
                if _source != "baseline":
                    if not resolved.is_relative_to(appdir.resolve()):
                        continue
                return resolved
            except (OSError, ValueError):
                continue
    return None


def find_libstdcxx(
    appdir: Path, baseline_root: Path | None = None
) -> tuple[Path, str] | None:
    """Find libstdc++.so.6 through the provider search order."""
    found = resolve_provider(appdir, "libstdc++.so.6")
    if found:
        return found, "bundled"
    if baseline_root:
        found = resolve_provider(appdir, "libstdc++.so.6", baseline_root=baseline_root)
        if found:
            return found, "baseline"
    return None


# -- readelf/objdump wrappers -------------------------------------------------

_READELF_TIMEOUT = 10


def run_readelf(elf_path: Path, *args: str) -> str | None:
    """Run readelf with LC_ALL=C and bounded timeout.

    Returns stdout on success, None on failure (non-zero exit, OSError, timeout).
    """
    try:
        result = subprocess.run(
            ["readelf", *args, str(elf_path)],
            capture_output=True, text=True,
            timeout=_READELF_TIMEOUT,
            env={**os.environ, "LC_ALL": "C"},
        )
        if result.returncode != 0:
            return None
        return result.stdout
    except (OSError, subprocess.TimeoutExpired):
        return None


def run_objdump(elf_path: Path) -> str | None:
    """Fallback: objdump -p for version refs. Returns None on failure."""
    try:
        result = subprocess.run(
            ["objdump", "-p", str(elf_path)],
            capture_output=True, text=True,
            timeout=_READELF_TIMEOUT,
            env={**os.environ, "LC_ALL": "C"},
        )
        if result.returncode != 0:
            return None
        return result.stdout
    except (OSError, subprocess.TimeoutExpired):
        return None


def inspect_elf_dynamic(elf_path: Path) -> str | None:
    """Read ELF dynamic section. Returns output, None for static, TOOL_FAILURE."""
    out = run_readelf(elf_path, "-d")
    if out is not None:
        return out
    # readelf failed; try objdump as fallback
    obj_out = run_objdump(elf_path)
    if obj_out is not None:
        return obj_out
    # Both failed: distinguish static ELF from tool failure.
    # Try readelf -h (basic header) to see if the file is even readable.
    hdr = run_readelf(elf_path, "-h")
    if hdr is None:
        return TOOL_FAILURE  # file not readable as ELF at all
    return None  # readable ELF with no .dynamic section (static)


# -- Version requirement parsing ----------------------------------------------


def parse_version_needs(text: str) -> tuple[set[str], set[str], set[str]]:
    """Parse .gnu.version_r section. Returns (glibc, glibcxx, cxxabi)."""
    glibc: set[str] = set()
    glibcxx: set[str] = set()
    cxxabi: set[str] = set()
    in_needs = False
    for line in text.splitlines():
        if "Version needs" in line and ".gnu.version_r" in line:
            in_needs = True
            continue
        if in_needs and ("Version definition" in line or ".gnu.version_d" in line):
            break
        if in_needs:
            for m in re.finditer(r"GLIBC_(\S+)", line):
                glibc.add(m.group(1))
            for m in re.finditer(r"GLIBCXX_(\S+)", line):
                glibcxx.add(m.group(1))
            for m in re.finditer(r"CXXABI_(\S+)", line):
                cxxabi.add(m.group(1))
    return glibc, glibcxx, cxxabi


def parse_version_defs(text: str) -> tuple[set[str], set[str]]:
    """Parse .gnu.version_d section. Returns (glibcxx, cxxabi)."""
    glibcxx: set[str] = set()
    cxxabi: set[str] = set()
    in_defs = False
    for line in text.splitlines():
        if "Version definition" in line and ".gnu.version_d" in line:
            in_defs = True
            continue
        if in_defs and line.strip() == "":
            continue
        if in_defs and ("Version needs" in line or ".gnu.version_r" in line):
            break
        if in_defs:
            for m in re.finditer(r"GLIBCXX_(\S+)", line):
                glibcxx.add(m.group(1))
            for m in re.finditer(r"CXXABI_(\S+)", line):
                cxxabi.add(m.group(1))
    return glibcxx, cxxabi


def read_elf_requirements(elf_path: Path) -> tuple[set[str], set[str], set[str]]:
    """Read GLIBC, GLIBCXX, CXXABI version requirements from an ELF.

    Returns (glibc, glibcxx, cxxabi). Empty sets for static ELFs.
    Returns TOOL_FAILURE marker if both readelf and objdump fail.
    """
    out = inspect_elf_dynamic(elf_path)
    if out is TOOL_FAILURE:
        return TOOL_FAILURE, set(), set()  # type: ignore[return-value]
    if out is None:
        return set(), set(), set()  # static ELF
    # Try readelf -V for precise section parsing
    v_out = run_readelf(elf_path, "-V")
    if v_out is not None:
        return parse_version_needs(v_out)
    # Fallback: objdump -p for version references (not -d which is unreliable)
    obj_out = run_objdump(elf_path)
    if obj_out is not None:
        glibc, glibcxx, cxxabi = set(), set(), set()
        for line in obj_out.splitlines():
            for m in re.finditer(r"GLIBC_(\S+)", line):
                glibc.add(m.group(1))
            for m in re.finditer(r"GLIBCXX_(\S+)", line):
                glibcxx.add(m.group(1))
            for m in re.finditer(r"CXXABI_(\S+)", line):
                cxxabi.add(m.group(1))
        return glibc, glibcxx, cxxabi
    # Both readelf -V and objdump failed -> fail closed
    return TOOL_FAILURE, set(), set()  # type: ignore[return-value]


def read_elf_provided_versions(elf_path: Path) -> tuple[set[str], set[str]]:
    """Read GLIBCXX and CXXABI versions DEFINED by an ELF (e.g. libstdc++)."""
    out = run_readelf(elf_path, "-V")
    if out is None:
        return set(), set()
    return parse_version_defs(out)


def read_needed_libs(elf_path: Path) -> list[str] | str:
    """Read DT_NEEDED entries from an ELF. Returns TOOL_FAILURE on failure."""
    out = run_readelf(elf_path, "-d")
    if out is None:
        return TOOL_FAILURE
    return [m.group(1) for m in re.finditer(r"\(NEEDED\)\s+.*\[(.+?)\]", out)]

def is_dynamic_elf(elf_path: Path) -> bool:
    """Check if an ELF has a .dynamic section (is dynamically linked)."""
    out = run_readelf(elf_path, "-d")
    return out is not None and "NEEDED" in out