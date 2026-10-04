#!/usr/bin/env python3
"""Resolves release assets and checks that the release notes document them.

The release body used to live inline in release.yml and was corrected by hand
after every tag, which is how the AppImage, the third-party source archive and
the Docker tag drifted out of the downloads table. This script makes the notes
and the uploaded files check each other, so that class of bug fails the release
instead of shipping.

Three subcommands, all keyed off the tag name so a local dry run and the
create-release job see identical input:

  render   substitute {{VERSION}} and {{VERSION_NUM}} into a notes template
  resolve  expand an asset manifest into concrete, existing file paths
  check    fail when a shipped artifact is undocumented, or a documented
           artifact was never shipped

Run the tests with: python -m unittest discover -s scripts/ci/tests
"""

from __future__ import annotations

import argparse
import fnmatch
import re
import sys
from pathlib import Path, PurePosixPath

DEFAULT_MANIFEST = Path(".github/release-assets.txt")

# Velopack update metadata and package containers. They are part of every
# release and are not downloads a person chooses from, so they stay undocumented.
METADATA_PATTERNS = (
    "*.nupkg",
    "releases.*.json",
    "assets.*.json",
    "RELEASES-*",
)

# Extensions that make a backticked span in the downloads table look like an
# artifact name. Deliberately narrow: it keeps prose such as
# `libwebkit2gtk-4.1-0` or `docker pull bluegrassiot/mqttprobe:1.2.3` from being
# mistaken for an asset reference.
ARTIFACT_SUFFIXES = (".zip", ".apk", ".exe", ".pkg", ".AppImage", ".tar.gz")


def substitute(text: str, version: str) -> str:
    return text.replace("{{VERSION_NUM}}", version.lstrip("v")).replace(
        "{{VERSION}}", version
    )


def read_patterns(manifest: Path, version: str) -> tuple[list[str], list[str]]:
    includes: list[str] = []
    excludes: list[str] = []
    for raw in substitute(manifest.read_text(encoding="utf-8"), version).splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        if line.startswith("!"):
            excludes.append(line[1:].strip())
        else:
            includes.append(line)
    return includes, excludes


def _has_magic(pattern: str) -> bool:
    return any(ch in pattern for ch in "*?[")


def _matches(root: Path, pattern: str) -> list[Path]:
    if not _has_magic(pattern):
        candidate = root / pattern
        return [candidate] if candidate.is_file() else []
    return sorted(p for p in root.glob(pattern) if p.is_file())


def resolve(manifest: Path, version: str, root: Path) -> tuple[list[str], list[str]]:
    """Returns (asset paths relative to root, include patterns that matched nothing)."""
    includes, excludes = read_patterns(manifest, version)
    found: dict[str, None] = {}
    unmatched: list[str] = []

    for pattern in includes:
        hits = _matches(root, pattern)
        if not hits:
            unmatched.append(pattern)
        for hit in hits:
            found[hit.relative_to(root).as_posix()] = None

    # Exclusions apply after every include so their position in the file is
    # irrelevant and two include blocks cannot leak past one.
    for rel in [r for r in found if any(fnmatch.fnmatch(r, e) for e in excludes)]:
        del found[rel]

    return sorted(found), unmatched


def table_tokens(notes: str) -> list[str]:
    """Backticked spans from the downloads table that look like artifact names."""
    tokens: list[str] = []
    for line in notes.splitlines():
        if not line.lstrip().startswith("|"):
            continue
        for chunk in line.split("`")[1::2]:
            token = chunk.strip()
            if token.endswith(ARTIFACT_SUFFIXES):
                tokens.append(token)
    return tokens


def find_leaked_expressions(text: str) -> list[str]:
    """GitHub expressions left in a rendered file.

    The notes are substituted before the release body is built, so an expression
    here means the template used ${{ github.ref_name }} where it should have used
    {{VERSION}}. Left alone, that surfaces as a baffling "no uploaded asset
    matches" error from the table check instead of naming the real mistake.
    """
    return sorted(set(re.findall(r"\$\{\{[^}]*\}\}", text)))


def check_coverage(notes: str, assets: list[str]) -> list[str]:
    problems: list[str] = []
    names = [PurePosixPath(a).name for a in assets]

    for rel, name in zip(assets, names):
        if any(fnmatch.fnmatch(name, pattern) for pattern in METADATA_PATTERNS):
            continue
        if name not in notes:
            problems.append(
                f"{rel} is uploaded but never mentioned in the release notes"
            )

    for token in table_tokens(notes):
        if not any(fnmatch.fnmatch(name, token) for name in names):
            problems.append(
                f"the downloads table lists {token}, which no uploaded asset matches"
            )

    return problems


def cmd_render(args: argparse.Namespace) -> int:
    text = substitute(
        Path(args.notes).read_text(encoding="utf-8"), args.version
    )
    Path(args.out).write_text(text, encoding="utf-8")
    return 0


def cmd_resolve(args: argparse.Namespace) -> int:
    assets, unmatched = resolve(
        Path(args.manifest), args.version, Path(args.root)
    )
    for pattern in unmatched:
        print(f"::error::No file matches release asset pattern: {pattern}", file=sys.stderr)
    if unmatched:
        return 1
    print("\n".join(assets))
    return 0


def cmd_check(args: argparse.Namespace) -> int:
    notes = Path(args.notes).read_text(encoding="utf-8")
    assets, unmatched = resolve(
        Path(args.manifest), args.version, Path(args.root)
    )
    problems = [f"no file matches release asset pattern: {p}" for p in unmatched]
    for expression in find_leaked_expressions(notes):
        problems.append(
            f"the rendered notes still contain {expression}; use the "
            "{{VERSION}} placeholder there, not a GitHub expression"
        )
    problems += check_coverage(notes, assets)

    if problems:
        for problem in problems:
            print(f"::error::{problem}", file=sys.stderr)
        print(
            "\nFix by editing .github/release-notes.md (add the row) or "
            ".github/release-assets.txt (stop shipping it).",
            file=sys.stderr,
        )
        return 1
    print(f"  OK  {len(assets)} asset(s) shipped and documented")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)

    render = sub.add_parser("render", help="write the notes with placeholders filled in")
    render.add_argument("--notes", required=True)
    render.add_argument("--version", required=True)
    render.add_argument("--out", required=True)
    render.set_defaults(func=cmd_render)

    for name, handler in (("resolve", cmd_resolve), ("check", cmd_check)):
        p = sub.add_parser(name, help=handler.__doc__)
        p.add_argument("--manifest", default=str(DEFAULT_MANIFEST))
        p.add_argument("--version", required=True)
        p.add_argument("--root", default=".")
        p.set_defaults(func=handler)
        if name == "check":
            p.add_argument("--notes", required=True)

    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
