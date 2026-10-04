"""Shared fixtures and module loading for verifier tests.

Imported by all test modules. Registers _elf in
sys.modules so the main script's `from _elf import ...`
resolves. Provides synthetic readelf/squashfs builders and ELF helpers.
"""

import importlib.util
import struct
import sys
from pathlib import Path

_LINUX = Path(__file__).resolve().parents[2]

_LIB_SPEC = importlib.util.spec_from_file_location(
    "_elf", _LINUX / "_elf.py",
)
if _LIB_SPEC is None or _LIB_SPEC.loader is None:
    raise SystemExit("Cannot load _elf.py")
lib = importlib.util.module_from_spec(_LIB_SPEC)
sys.modules["_elf"] = lib
_LIB_SPEC.loader.exec_module(lib)

_MOD_SPEC = importlib.util.spec_from_file_location(
    "verify", _LINUX / "verify.py",
)
if _MOD_SPEC is None or _MOD_SPEC.loader is None:
    raise SystemExit("Cannot load verify.py")
mod = importlib.util.module_from_spec(_MOD_SPEC)
_MOD_SPEC.loader.exec_module(mod)


def sqfs_data(magic=0x73717368, inodes=100, block=4096, maj=4, min_=0, used=1000):
    """Build a 96-byte squashfs superblock with given fields."""
    d = bytearray(96)
    struct.pack_into("<I", d, 0, magic)
    struct.pack_into("<I", d, 4, inodes)
    struct.pack_into("<I", d, 12, block)
    struct.pack_into("<H", d, 28, maj)
    struct.pack_into("<H", d, 30, min_)
    struct.pack_into("<Q", d, 40, used)
    return bytes(d)


def readelf_needs(*glibc_v, glibcxx=None, cxxabi=None):
    glibcxx = glibcxx or []
    cxxabi = cxxabi or []
    lines = [
        "Version needs section '.gnu.version_r' contains 1 entry",
        "  000000: Version: 1  File: libc.so.6  Cnt: 1",
    ]
    i = 2
    for v in glibc_v:
        lines.append(f"  0x0010:   Name: GLIBC_{v}  Flags: none  Version: {i}")
        i += 1
    if glibcxx or cxxabi:
        lines.append(f"  000020: Version: 1  File: libstdc++.so.6  Cnt: {len(glibcxx) + len(cxxabi)}")
        for v in glibcxx:
            lines.append(f"  0x0010:   Name: GLIBCXX_{v}  Flags: none  Version: {i}")
            i += 1
        for v in cxxabi:
            lines.append(f"  0x0010:   Name: CXXABI_{v}  Flags: none  Version: {i}")
            i += 1
    return "\n".join(lines) + "\n"


def readelf_defs(*glibcxx_v, cxxabi=None):
    cxxabi = cxxabi or []
    lines = [
        "Version definition section '.gnu.version_d' contains 3 entries",
        "  000000: Rev: 1  Flags: BASE   Index: 1  Cnt: 1  Name: libstdc++.so.6",
    ]
    i = 2
    for v in glibcxx_v:
        lines.append(
            f"  0x00{10 + i*8:02x}: Rev: 1  Flags: none  Index: {i}  "
            f"Cnt: 1  Name: GLIBCXX_{v}"
        )
        i += 1
    for v in cxxabi:
        lines.append(
            f"  0x00{10 + i*8:02x}: Rev: 1  Flags: none  Index: {i}  "
            f"Cnt: 1  Name: CXXABI_{v}"
        )
        i += 1
    return "\n".join(lines) + "\n"


def readelf_needed(*libs):
    return "\n".join(
        f"  0x0000000000000001 (NEEDED)  Shared library: [{l}]" for l in libs
    ) + "\n"


def readelf_rpath(raw: str) -> str:
    """Build fake readelf -d output with an RPATH entry."""
    return f"  0x000000000000000f (RPATH)    Library rpath: [{raw}]\n"


def readelf_runpath(raw: str) -> str:
    """Build fake readelf -d output with a RUNPATH entry."""
    return f"  0x000000000000001d (RUNPATH)  Library runpath: [{raw}]\n"


def make_elf(path):
    path.write_bytes(b"\x7fELF" + b"\x00" * 60)
    return path