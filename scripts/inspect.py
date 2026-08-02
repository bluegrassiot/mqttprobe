#!/usr/bin/env python3
"""
Runs the static-analysis tools that live outside the build and writes a single
Markdown report for an AI agent (or a human) to read.

  jb inspectcode  ReSharper's inspections -- code smells, redundancies, and
                  nullability findings the Roslyn analyzers do not cover.
  devskim         Microsoft's security-pattern scanner -- hardcoded secrets,
                  weak crypto, unsafe API use. Scans every file type, not just C#.

Both emit SARIF, so both are parsed by the same code and merged into one file
grouped by tool, severity, and rule. Raw SARIF is kept with --keep-sarif.

jb builds the solution first (it needs the assemblies), so a full run takes a
few minutes. Pass --no-build if you have already built.

The exclusion globs matter more than they look: without them devskim scans
.claude/worktrees (a full repo copy per agent worktree), node_modules, and
publish output, which took one run from 144 findings to over 1400.

Usage:
  ./scripts/inspect.py                      # both tools
  ./scripts/inspect.py --tool devskim       # just devskim (seconds, no build)
  ./scripts/inspect.py --tool jb --no-build
  ./scripts/inspect.py -o artifacts/today.md --keep-sarif
"""

import argparse
import collections
import json
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ARTIFACTS = ROOT / "artifacts"
SOLUTION = "MqttProbe.slnx"

# Anything that is not our source: build output, vendored code, agent worktrees
# (each a full copy of the repo), and generated report trees.
SKIP_DIRS = [
    ".git", ".vs", ".claude", ".opencode", "bin", "obj", "node_modules",
    "external", "artifacts", "publish", "TestResults",
    # CI points NUGET_PACKAGES at the workspace, so the restored package cache
    # lands in the tree; scanning it added ~2900 findings from third-party XML.
    ".nuget",
]

# Column-aligned icon constants and generated protobuf; neither is hand-maintained.
SKIP_FILES = ["LucideIcons.cs", "SparkplugBProtobuf.cs"]

# devskim only: this workflow heredocs an Apple plist whose DOCTYPE trips the insecure-URL
# rule, and a suppression comment would land inside the generated file rather than be read
# as one. Scoped to the file so the other workflows are still scanned for secrets.
DEVSKIM_SKIP_FILES = SKIP_FILES + ["build-macos-desktop.yml"]

# devskim needs the leading **/ or it only matches at the repo root -- a plain
# "TestResults/**" silently missed TestResults\CoverageReport\main.js.
DEVSKIM_GLOBS = [f"**/{d}/**" for d in SKIP_DIRS] + [f"**/{f}" for f in DEVSKIM_SKIP_FILES]
JB_EXCLUDE = ";".join([f"**/{d}/**/*" for d in SKIP_DIRS] + [f"**/{f}" for f in SKIP_FILES])

# SARIF levels, worst first. Anything unrecognised sorts last under "other".
LEVEL_ORDER = ["error", "warning", "note", "none", "other"]


def run_tool(name, cmd, sarif_path):
    """Run one analyzer. Returns the SARIF path, or None if it produced nothing."""
    print(f"\n  {name}...", end="", flush=True)
    sarif_path.unlink(missing_ok=True)

    result = subprocess.run(cmd, cwd=ROOT, capture_output=True, text=True,
                            encoding="utf-8", errors="replace")

    if not sarif_path.exists():
        print(" FAILED")
        # A missing local tool is the common case and has a one-line fix.
        output = f"{result.stdout}\n{result.stderr}"
        if "is not a recognized" in output or "was not found" in output:
            print("    Tool not available. Run: dotnet tool restore")
        else:
            print(f"    exit {result.returncode}")
            for line in output.strip().splitlines()[-15:]:
                print(f"    {line}")
        return None

    print(" done")
    return sarif_path


def parse_sarif(path):
    """Flatten SARIF into (findings, rule_metadata). Both tools share this shape."""
    # Explicit UTF-8: these files carry non-ASCII snippets and Windows would
    # otherwise decode them as cp1252 and throw.
    data = json.loads(path.read_text(encoding="utf-8"))

    findings = []
    rules = {}

    for run in data.get("runs", []):
        for rule in run.get("tool", {}).get("driver", {}).get("rules", []):
            rules[rule.get("id")] = {
                "name": rule.get("name") or rule.get("id"),
                "description": (rule.get("shortDescription", {}).get("text")
                                or rule.get("fullDescription", {}).get("text")
                                or "").strip(),
            }

        for result in run.get("results", []):
            location = (result.get("locations") or [{}])[0]
            physical = location.get("physicalLocation", {})
            region = physical.get("region", {})
            uri = physical.get("artifactLocation", {}).get("uri", "")

            findings.append({
                "rule": result.get("ruleId") or "(no rule id)",
                "level": result.get("level", "other"),
                "message": (result.get("message", {}).get("text") or "").strip(),
                # removeprefix, not lstrip: lstrip takes a character set and would
                # eat the leading dot of a path like ./.config/x.
                "file": uri.replace("\\", "/").removeprefix("./"),
                "line": region.get("startLine", 0),
                "snippet": (region.get("snippet", {}).get("text") or "").strip(),
            })

    return findings, rules


def level_key(level):
    return LEVEL_ORDER.index(level) if level in LEVEL_ORDER else len(LEVEL_ORDER)


def render(tool, findings, rules, max_per_rule):
    """One tool's section: a rule index, then each rule's sites."""
    lines = [f"## {tool}", ""]

    if not findings:
        lines += ["No findings.", ""]
        return lines

    by_level = collections.Counter(f["level"] for f in findings)
    summary = ", ".join(f"{by_level[lv]} {lv}" for lv in LEVEL_ORDER if by_level[lv])
    lines += [f"{len(findings)} findings ({summary})", ""]

    # Group by rule, ordered by severity then by how often it fires.
    groups = collections.defaultdict(list)
    for finding in findings:
        groups[finding["rule"]].append(finding)

    ordered = sorted(
        groups.items(),
        key=lambda kv: (level_key(kv[1][0]["level"]), -len(kv[1]), kv[0]),
    )

    lines += ["| Rule | Level | Count | What it flags |", "| --- | --- | --- | --- |"]
    for rule_id, items in ordered:
        meta = rules.get(rule_id, {})
        name = meta.get("name", rule_id)
        description = (meta.get("description") or items[0]["message"]).split(". ")[0]
        lines.append(
            f"| `{rule_id}` | {items[0]['level']} | {len(items)} | {name} — {description} |"
        )
    lines.append("")

    for rule_id, items in ordered:
        meta = rules.get(rule_id, {})
        lines += [f"### `{rule_id}` — {meta.get('name', rule_id)} ({items[0]['level']})", ""]
        if meta.get("description"):
            lines += [meta["description"], ""]

        # devskim reports once per column match, so one line can yield the same
        # site several times over. Collapse those, but say how many there were.
        sites = collections.Counter(
            (f["file"], f["line"], f["snippet"] or f["message"]) for f in items
        )

        for (file, line, detail), count in sorted(sites.items())[:max_per_rule]:
            suffix = f" (×{count})" if count > 1 else ""
            lines.append(f"- `{file}:{line}`" + (f" — {detail}" if detail else "") + suffix)

        # Never truncate silently: a capped list reads as "that's all of them".
        if len(sites) > max_per_rule:
            lines.append(
                f"- _...{len(sites) - max_per_rule} more sites omitted "
                f"(raise --max-per-rule to see them all)_"
            )
        lines.append("")

    return lines


def main():
    parser = argparse.ArgumentParser(
        description="Run jb inspectcode and/or devskim, writing one Markdown report.")
    parser.add_argument("--tool", choices=["all", "jb", "devskim"], default="all",
                        help="which analyzer to run (default: all)")
    parser.add_argument("-o", "--output", default=str(ARTIFACTS / "inspect-report.md"),
                        help="report path (default: artifacts/inspect-report.md)")
    parser.add_argument("--no-build", action="store_true",
                        help="skip jb's solution build; only valid if already built")
    parser.add_argument("--severity", default="WARNING",
                        choices=["INFO", "HINT", "SUGGESTION", "WARNING", "ERROR"],
                        help="jb minimum severity (default: WARNING)")
    parser.add_argument("--max-per-rule", type=int, default=25,
                        help="sites listed per rule before summarising (default: 25)")
    parser.add_argument("--keep-sarif", action="store_true",
                        help="keep the raw SARIF files next to the report")
    parser.add_argument("--fail-on", default="never",
                        choices=["error", "warning", "note", "never"],
                        help="exit non-zero if any finding is at this level or worse "
                             "(default: never; the pre-commit hook uses warning)")
    args = parser.parse_args()

    ARTIFACTS.mkdir(exist_ok=True)
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)

    jb_sarif = ARTIFACTS / "inspect-jb.sarif"
    devskim_sarif = ARTIFACTS / "inspect-devskim.sarif"

    print("\n=== Static analysis ===")

    runs = []
    failures = 0

    if args.tool in ("all", "jb"):
        cmd = [
            "dotnet", "jb", "inspectcode", SOLUTION,
            f"--output={jb_sarif}",
            "--format=Sarif",
            f"--severity={args.severity}",
            f"--exclude={JB_EXCLUDE}",
            "--no-updates",
            "--verbosity=WARN",
        ]
        if args.no_build:
            cmd.append("--no-build")
        if run_tool("jb inspectcode", cmd, jb_sarif):
            runs.append(("jb inspectcode (ReSharper)", jb_sarif))
        else:
            failures += 1

    if args.tool in ("all", "devskim"):
        cmd = ["dotnet", "devskim", "analyze",
               "-I", ".", "-O", str(devskim_sarif), "-f", "sarif",
               # DS162092 flags every localhost/127.0.0.1 as possible debug code. This is an
               # MQTT client whose users connect to local brokers, so all 72 hits were dev
               # config, test fixtures, and loopback proxy allowlists -- enough noise to bury
               # a real one. Drop the flag to see them again.
               "--ignore-rule-ids", "DS162092",
               "-g", *DEVSKIM_GLOBS]
        if run_tool("devskim", cmd, devskim_sarif):
            runs.append(("devskim (security patterns)", devskim_sarif))
        else:
            failures += 1

    if not runs:
        print("\n=== No analyzer produced results ===")
        return 1

    sections = []
    totals = []
    gated = []
    for label, sarif in runs:
        findings, rules = parse_sarif(sarif)
        totals.append(f"{len(findings)} from {label}")
        sections += render(label, findings, rules, args.max_per_rule)
        if args.fail_on != "never":
            gated += [f for f in findings
                      if level_key(f["level"]) <= level_key(args.fail_on)]

    report = [
        "# Static analysis report",
        "",
        f"Solution: `{SOLUTION}` · jb severity: `{args.severity}` and above",
        f"Totals: {', '.join(totals)}.",
        "",
        "Generated by `scripts/inspect.py`. Findings are unverified tool output:",
        "confirm each one against the source before acting on it.",
        "",
        *sections,
    ]

    output.write_text("\n".join(report), encoding="utf-8")

    if not args.keep_sarif:
        for _, sarif in runs:
            sarif.unlink(missing_ok=True)

    print(f"\n=== Report: {output.relative_to(ROOT) if output.is_relative_to(ROOT) else output} ===")
    for total in totals:
        print(f"  {total}")

    if gated:
        print(f"\n=== {len(gated)} finding(s) at or above {args.fail_on} ===")
        # Same collapse as the report: one line can match a rule several times over.
        for file, line, rule, level in sorted({
            (f["file"], f["line"], f["rule"], f["level"]) for f in gated
        })[:25]:
            print(f"  {level:8} {rule:10} {file}:{line}")
        return 1

    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
