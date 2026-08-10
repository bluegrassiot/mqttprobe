#!/usr/bin/env python3
"""Flags routine XML doc comments in hand-written C#/Razor.

AGENTS.md bans routine XML doc comments (`/// <summary>`, `/// <param`,
`/// <returns>`, etc.) in hand-written code. This gate catches them so
agents and humans cannot land them without CI/pre-commit failing.

Only automatable: any `///` line that opens an XML tag is flagged.
Continuation lines (`///` text without `<`) are fine.
"""

import re
import sys
from pathlib import Path, PurePosixPath

def find_repo_root() -> Path:
    here = Path(__file__).resolve().parent
    for d in [here, *here.parents]:
        if (d / "MqttProbe.slnx").is_file():
            return d
    raise SystemExit("Could not find repo root (MqttProbe.slnx)")


ROOT = find_repo_root()

RELEVANT_EXTENSIONS = {".cs", ".razor"}
SKIP_BASENAMES = {"SparkplugBProtobuf.cs"}

# Match any `///` line that contains an opening XML doc tag.
# Covers: <summary, <param, <returns, <remarks, <exception, <seealso,
# <typeparam, <value, <example, <inheritdoc, <see, and any other `/// <`.
XML_DOC_LINE = re.compile(r"^\s*///\s*<", re.IGNORECASE)

DEFAULT_ROOTS = ["src", "tests"]


def should_skip(path: PurePosixPath) -> bool:
    if path.name in SKIP_BASENAMES:
        return True
    parts = path.parts
    if "external" in parts:
        return True
    if "bin" in parts or "obj" in parts:
        return True
    return False


def scan_file(path: Path, rel: str) -> list[tuple[int, str]]:
    hits = []
    try:
        text = path.read_text(encoding="utf-8-sig")
    except (UnicodeDecodeError, OSError):
        return hits
    for n, line in enumerate(text.splitlines(), 1):
        if XML_DOC_LINE.search(line):
            hits.append((n, line.rstrip()))
    return hits


def main() -> int:
    args = [a for a in sys.argv[1:] if not a.startswith("-")]

    print("\n=== Comments ===")

    scan_roots = args if args else DEFAULT_ROOTS
    found: dict[str, list[tuple[int, str]]] = {}

    for root_name in scan_roots:
        root = ROOT / root_name
        if not root.is_dir():
            continue
        for path in sorted(root.rglob("*")):
            if not path.is_file():
                continue
            if path.suffix.lower() not in RELEVANT_EXTENSIONS:
                continue
            rel = PurePosixPath(path.relative_to(ROOT).as_posix())
            if should_skip(rel):
                continue
            hits = scan_file(path, str(rel))
            if hits:
                found[str(rel)] = hits

    if found:
        for rel, hits in sorted(found.items()):
            for n, snippet in hits:
                print(f"  FAIL  {rel}:{n}: {snippet}")
        print(
            "\nRoutine XML doc comments are banned by AGENTS.md.\n"
            "Delete the `/// <...>` block. Inline `//` comments for non-obvious why are fine."
        )
        return 1

    print("  OK  no routine XML doc comments found")
    return 0


if __name__ == "__main__":
    sys.exit(main())
