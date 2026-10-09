#!/usr/bin/env python3
"""Checks that source files do not exceed a line-count limit."""

import os
import subprocess
import sys
from pathlib import Path, PurePosixPath


def find_repo_root() -> Path:
    here = Path(__file__).resolve().parent
    for d in [here, *here.parents]:
        if (d / "MqttProbe.slnx").is_file():
            return d
    raise SystemExit("Could not find repo root (MqttProbe.slnx)")


ROOT = find_repo_root()
DEFAULT_LIMIT = 500

# Ceilings freeze current size: may stay over DEFAULT_LIMIT but cannot grow.
GRANDFATHER = {
}

SKIP_BASENAMES = {"SparkplugBProtobuf.cs"}
SKIP_DIR_NAMES = {"bin", "obj", "external"}
RELEVANT_EXTENSIONS = {".cs", ".razor"}

DEFAULT_ROOTS = ["src"]


def is_relevant(path_str: str) -> bool:
    p = PurePosixPath(path_str.replace("\\", "/"))
    if p.name in SKIP_BASENAMES:
        return False
    if not p.parts or p.parts[0] != "src":
        return False
    if SKIP_DIR_NAMES & set(p.parts):
        return False
    return p.suffix.lower() in RELEVANT_EXTENSIONS


def limit_for(path_str: str) -> int:
    norm = path_str.replace("\\", "/")
    return GRANDFATHER.get(norm, DEFAULT_LIMIT)


def count_lines(text: str) -> int:
    return len(text.splitlines())


def collect_tree_paths() -> list[str]:
    found: list[str] = []
    for root_name in DEFAULT_ROOTS:
        root = ROOT / root_name
        if not root.is_dir():
            continue
        for path in sorted(root.rglob("*")):
            if not path.is_file():
                continue
            rel = path.relative_to(ROOT).as_posix()
            if is_relevant(rel):
                found.append(rel)
    return found


def get_staged_paths() -> list[str]:
    # -z keeps paths NUL-delimited and unquoted: a filename may contain a newline,
    # and without it git C-quotes such names, so both splitlines() and the staged
    # lookup below would miss the real file.
    result = subprocess.run(
        ["git", "diff", "--cached", "--name-only", "-z", "--diff-filter=ACMR"],
        capture_output=True, cwd=ROOT,
    )
    return [os.fsdecode(p) for p in result.stdout.split(b"\0") if p]


def staged_blob(path_str: str) -> str:
    result = subprocess.run(
        ["git", "show", f":{path_str}"],
        capture_output=True, cwd=ROOT,
    )
    return result.stdout.decode("utf-8-sig", errors="replace")


def disk_text(path_str: str) -> str:
    return (ROOT / path_str).read_text(encoding="utf-8-sig")


def main(argv: list[str] | None = None) -> int:
    raw = list(sys.argv[1:] if argv is None else argv)
    args = [a for a in raw if not a.startswith("-")]
    use_staged = "--staged" in raw

    print("\n=== File length ===")

    if use_staged:
        paths = [p for p in get_staged_paths() if is_relevant(p)]
        read = staged_blob
    elif args:
        paths = [p.replace("\\", "/") for p in args if is_relevant(p.replace("\\", "/"))]
        read = disk_text
    else:
        paths = collect_tree_paths()
        read = disk_text

    violations = []
    for p in paths:
        n = count_lines(read(p))
        lim = limit_for(p)
        if n > lim:
            violations.append((p, n, lim))

    if violations:
        for path, actual, lim in violations:
            print(f"  FAIL  {path}: {actual} lines (limit {lim})")
        return 1

    print(f"  OK  {len(paths)} file(s) within limits")
    return 0


if __name__ == "__main__":
    sys.exit(main())
