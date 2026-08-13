#!/usr/bin/env python3
"""Ranks src/ C# and Razor files by line count using cloc."""

import argparse
import csv
import io
import shutil
import subprocess
import sys
from pathlib import Path

EXCLUDE_DIRS = ["bin", "obj", "Generated", "TestResults", "node_modules", "packages", ".vs"]

# protoc output; counting it would swamp the ranking with generated code.
SKIP_BASENAMES = {"SparkplugBProtobuf.cs"}

MATCH_FILES = r"\.(cs|razor)$"


def find_repo_root() -> Path:
    here = Path(__file__).resolve().parent
    for d in [here, *here.parents]:
        if (d / "MqttProbe.slnx").is_file():
            return d
    raise SystemExit("Could not find repo root (MqttProbe.slnx)")


ROOT = find_repo_root()


def run_cloc(include_generated: bool) -> str:
    cloc = shutil.which("cloc")
    if cloc is None:
        raise SystemExit("cloc not found on PATH. Install it: winget install AlDanial.Cloc")

    cmd = [
        cloc, "src",
        "--by-file",
        "--csv",
        "--quiet",
        f"--match-f={MATCH_FILES}",
        f"--exclude-dir={','.join(EXCLUDE_DIRS)}",
    ]
    if not include_generated:
        skip = "|".join(name.replace(".", r"\.") for name in sorted(SKIP_BASENAMES))
        cmd.append(f"--not-match-f=({skip})$")

    result = subprocess.run(cmd, capture_output=True, text=True, cwd=ROOT)
    if result.returncode != 0:
        raise SystemExit(f"cloc failed ({result.returncode}):\n{result.stderr.strip()}")
    return result.stdout


def parse_rows(csv_text: str) -> list[dict]:
    rows = []
    for row in csv.reader(io.StringIO(csv_text)):
        # Header carries a trailing timing column; SUM is cloc's own footer.
        if len(row) < 5 or row[0] in ("language", "SUM"):
            continue
        blank, comment, code = (int(row[2]), int(row[3]), int(row[4]))
        rows.append({
            "language": row[0],
            "path": row[1].replace("\\", "/"),
            "blank": blank,
            "comment": comment,
            "code": code,
            "total": blank + comment + code,
        })
    return rows


def print_table(rows: list[dict], top: int | None) -> None:
    shown = rows[:top] if top else rows
    width = max((len(r["path"]) for r in shown), default=4)

    print(f"{'TOTAL':>7}{'CODE':>8}{'COMMENT':>9}{'BLANK':>7}  FILE")
    print(f"{'-----':>7}{'----':>8}{'-------':>9}{'-----':>7}  {'-' * width}")
    for r in shown:
        print(f"{r['total']:>7}{r['code']:>8}{r['comment']:>9}{r['blank']:>7}  {r['path']}")

    if top and len(rows) > top:
        print(f"\n... {len(rows) - top} more file(s) not shown (--top {top})")


def print_summary(rows: list[dict]) -> None:
    def totals(subset: list[dict]) -> str:
        return (
            f"{sum(r['total'] for r in subset):>7}"
            f"{sum(r['code'] for r in subset):>8}"
            f"{sum(r['comment'] for r in subset):>9}"
            f"{sum(r['blank'] for r in subset):>7}"
        )

    print()
    for language in sorted({r["language"] for r in rows}):
        subset = [r for r in rows if r["language"] == language]
        print(f"{totals(subset)}  {language} ({len(subset)} files)")
    print(f"{totals(rows)}  ALL ({len(rows)} files)")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--top", type=int, metavar="N", help="show only the N largest files")
    parser.add_argument(
        "--include-generated",
        action="store_true",
        help=f"include generated sources ({', '.join(sorted(SKIP_BASENAMES))})",
    )
    args = parser.parse_args()

    rows = parse_rows(run_cloc(args.include_generated))
    if not rows:
        print("No matching files found under src/.")
        return 0

    rows.sort(key=lambda r: (-r["total"], r["path"]))
    print_table(rows, args.top)
    print_summary(rows)
    return 0


if __name__ == "__main__":
    sys.exit(main())
