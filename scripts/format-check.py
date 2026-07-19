#!/usr/bin/env python3
"""
Checks (or fixes) code formatting.

On Windows, the full solution (MqttProbe.slnx) is used because Visual Studio
installs MAUI workloads automatically, so all projects can be checked together.

On non-Windows (CI / Linux / macOS), non-MAUI projects are checked through
MqttProbe.NoMaui.slnf — the same filter CI uses, so local and CI coverage
cannot drift. The MAUI project only loads when its platform workloads are
installed, so it is checked best-effort and skipped otherwise.

LucideIcons.cs and SparkplugBProtobuf.cs are always excluded (column-aligned
constants and auto-generated protobuf code respectively).

ENDOFLINE-only violations are ignored in check mode: .editorconfig mandates LF
but git (text=auto) checks out CRLF on Windows, so every file reports
ENDOFLINE locally. CI normalizes line endings before checking and is
unaffected.

Usage:
  ./scripts/format-check.py          # check — exits 1 on violations
  ./scripts/format-check.py --fix    # auto-fix all violations
"""

import re
import sys
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
FIX = "--fix" in sys.argv

# external/ holds vendored git submodules (e.g. SparkplugNet fork); they keep
# their own upstream code style and must not be reformatted by mqttprobe rules.
EXCLUDES = ["**/LucideIcons.cs", "**/SparkplugBProtobuf.cs", "external/**"]

if sys.platform == "win32":
    TARGETS = [
        ("MqttProbe.slnx", False),
    ]
else:
    TARGETS = [
        ("MqttProbe.NoMaui.slnf", False),
        ("src/MqttProbe.Maui/MqttProbe.Maui.csproj", True),
    ]

WORKLOAD_ERROR = re.compile(r"NETSDK1147|workload", re.IGNORECASE)
REAL_ERROR_LINE = re.compile(r"error (?!ENDOFLINE)\w+:")

failed = 0

print()
if FIX:
    print("=== Format Fix ===")
    print("Applying formatting changes...")
else:
    print("=== Format Check ===")
    print("Run with --fix to apply changes.")

for target, needs_workload in TARGETS:
    label = Path(target).stem
    args = ["dotnet", "format", str(ROOT / target)]
    if not FIX:
        args.append("--verify-no-changes")
    for ex in EXCLUDES:
        args.extend(["--exclude", ex])

    print(f"\n  {label}...", end="", flush=True)

    result = subprocess.run(args, capture_output=True, text=True)
    output = f"{result.stdout}\n{result.stderr}"

    if result.returncode == 0:
        print(" OK")
        continue

    if needs_workload and WORKLOAD_ERROR.search(output):
        print(" SKIP (required MAUI workload not installed)")
        continue

    real_errors = [line.strip() for line in output.splitlines() if REAL_ERROR_LINE.search(line)]

    if not FIX and not real_errors and "ENDOFLINE" in output:
        print(" OK (ENDOFLINE-only noise ignored)")
        continue

    print(" FAIL")
    if real_errors:
        for line in real_errors:
            print(f"    {line}")
    else:
        print(output.strip())
    failed += 1

print()
if failed > 0:
    print(f"=== {failed} target(s) have formatting violations ===")
    print("Run: ./scripts/format-check.py --fix")
    sys.exit(1)
else:
    print("=== All projects formatted correctly ===")
