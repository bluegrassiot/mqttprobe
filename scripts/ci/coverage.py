#!/usr/bin/env python3
"""
Runs all test projects with coverage collection and generates an HTML report.

Collects XPlat Code Coverage (OpenCover format) from unit and integration tests,
merges the results, and generates an HTML report via ReportGenerator.
E2E tests are excluded from coverage (Playwright tests exercise the app externally).

Usage:
  ./scripts/ci/coverage.py
  ./scripts/ci/coverage.py --open --threshold 75
"""

import argparse
import re
import shutil
import subprocess
import sys
import webbrowser
from pathlib import Path


def find_repo_root() -> Path:
    here = Path(__file__).resolve().parent
    for d in [here, *here.parents]:
        if (d / "MqttProbe.slnx").is_file():
            return d
    raise SystemExit("Could not find repo root (MqttProbe.slnx)")


ROOT = find_repo_root()
RESULTS_DIR = ROOT / "TestResults"
REPORT_DIR = RESULTS_DIR / "CoverageReport"
SETTINGS = ROOT / "tests" / "coverlet.runsettings"

UNIT_PROJ = ROOT / "tests/MqttProbe.Core.Tests/MqttProbe.Core.Tests.csproj"
UI_PROJ = ROOT / "tests/MqttProbe.UI.Tests/MqttProbe.UI.Tests.csproj"
# INTEGRATION_PROJ = ROOT / "tests/MqttProbe.IntegrationTests/MqttProbe.IntegrationTests.csproj"


def run_tests(project: Path, label: str, results_subdir: str):
    print(f"\n=== Running {label} ===", flush=True)
    result = subprocess.run(
        [
            "dotnet", "test", str(project),
            "--results-directory", str(RESULTS_DIR / results_subdir),
            "--settings", str(SETTINGS),
            "--collect:XPlat Code Coverage",
            "--logger", "console;verbosity=minimal",
        ],
    )
    if result.returncode != 0:
        print(f"{label} failed", file=sys.stderr)
        sys.exit(1)


def main():
    parser = argparse.ArgumentParser(description="Run tests with coverage and generate HTML report.")
    parser.add_argument("--open", action="store_true", help="Open the HTML report in the default browser.")
    parser.add_argument("--threshold", type=int, default=75, help="Minimum line coverage percentage (default: 75).")
    args = parser.parse_args()

    if RESULTS_DIR.exists():
        shutil.rmtree(RESULTS_DIR)
    RESULTS_DIR.mkdir(parents=True)

    run_tests(UNIT_PROJ, "Unit Tests (Core)", "core")
    run_tests(UI_PROJ, "Unit Tests (UI)", "ui")
    # run_tests(INTEGRATION_PROJ, "Integration Tests", "integration")

    print("\n=== Generating Coverage Report ===", flush=True)

    coverage_files = list(RESULTS_DIR.rglob("coverage.opencover.xml"))
    if not coverage_files:
        print("No coverage files found. Ensure coverlet.collector is installed.", file=sys.stderr)
        sys.exit(1)

    reports = ";".join(str(f) for f in coverage_files)
    result = subprocess.run(
        [
            "dotnet", "reportgenerator",
            f"-reports:{reports}",
            f"-targetdir:{REPORT_DIR}",
            "-reporttypes:Html;TextSummary;Badges",
            "-assemblyfilters:+MqttProbe.Core;+MqttProbe.UI;+MqttProbe.Web",
            "-filefilters:-*SparkplugBProtobuf*;-*.g.cs;-*.Designer.cs",
        ],
    )
    if result.returncode != 0:
        print("Report generation failed", file=sys.stderr)
        sys.exit(1)

    summary_path = REPORT_DIR / "Summary.txt"
    summary = ""
    if summary_path.exists():
        summary = summary_path.read_text()
        print("\n=== Coverage Summary ===")
        print(summary)

    match = re.search(r"Line coverage:\s+([\d.]+)%", summary)
    if match:
        line_coverage = float(match.group(1))
        if line_coverage < args.threshold:
            print(f"\nLine coverage {line_coverage}% is below threshold {args.threshold}%")
            sys.exit(1)
        else:
            print(f"\nLine coverage {line_coverage}% meets threshold {args.threshold}%")

    index_html = REPORT_DIR / "index.html"
    print(f"\nReport: {index_html}")

    if args.open and index_html.exists():
        webbrowser.open(f"file:///{index_html}")


if __name__ == "__main__":
    main()
