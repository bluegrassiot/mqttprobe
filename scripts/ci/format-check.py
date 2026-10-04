#!/usr/bin/env python3
"""
Checks (or fixes) code formatting and unused using directives.

On Windows, the full solution (MqttProbe.slnx) is used; MAUI workloads must be
installed separately. On non-Windows (CI / Linux / macOS), non-MAUI projects
are checked through MqttProbe.NoMaui.slnf and the MAUI project is checked
best-effort only when its workloads are present.

LucideIcons.cs and SparkplugBProtobuf.cs are always excluded (column-aligned
constants and auto-generated protobuf code respectively).

After formatting, a targeted style pass checks for unused using directives
(IDE0005) via `dotnet format style --diagnostics IDE0005`. Only that single
diagnostic is enabled; import ordering and other style fixes may also run.

ENDOFLINE-only violations are ignored in check mode: .editorconfig mandates LF
but git (text=auto) checks out CRLF on Windows, so every file reports
ENDOFLINE locally. CI normalizes line endings before checking and is
unaffected. The ENDOFLINE bypass applies only to the whitespace pass result and
never suppresses the style or targeted IDE0005 passes.

Usage:
  ./scripts/ci/format-check.py          # check -- exits 1 on violations
  ./scripts/ci/format-check.py --fix    # auto-fix all violations
"""

import re
import sys
import subprocess
from pathlib import Path


def find_repo_root() -> Path:
    here = Path(__file__).resolve().parent
    for d in [here, *here.parents]:
        if (d / "MqttProbe.slnx").is_file():
            return d
    raise SystemExit("Could not find repo root (MqttProbe.slnx)")


ROOT = find_repo_root()
FIX = "--fix" in sys.argv

SUBCOMMANDS = ["whitespace", "style"]

# Targeted style pass: only IDE0005 (remove unnecessary usings).  This runs
# via `dotnet format style --diagnostics IDE0005` so no other style fixes are
# enabled.  Without an .editorconfig entry IDE0005 defaults to `hidden`
# severity; `--severity hidden` catches default hidden diagnostics.
TARGETED_STYLE_DIAGNOSTICS = ["IDE0005"]

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

# A missing MAUI workload surfaces as NETSDK1147 in the error output.
# Only this specific SDK error code triggers the skip; generic messages
# like "Restore operation failed" or the "workload" keyword alone are not
# sufficient because they also appear for transient NuGet failures or
# broken project files.
WORKLOAD_ERROR = re.compile(r"NETSDK1147", re.IGNORECASE)
REAL_ERROR_LINE = re.compile(r"error (?!ENDOFLINE)\w+:")

# Workspace load warnings.  dotnet format may print the prefixed form
# ("warn : ...") or the actual unprefixed message that appears on stdout:
#   "Warnings were encountered while loading the workspace. Set the verbosity
#    option to the diagnostic level to log warnings."
# Both forms indicate the tool could not fully load the workspace and any
# results are unreliable.
WORKSPACE_WARN = re.compile(
    r"(?:warn(?:ing)?\s*:.*(?:workspace|load|MSBuild))"
    r"|Warnings were encountered while loading the workspace",
    re.IGNORECASE,
)


def _run_dotnet(args: list[str]) -> subprocess.CompletedProcess:  # type: ignore[type-arg]
    return subprocess.run(args, capture_output=True, text=True)


def _has_workspace_warning(output: str) -> bool:
    return WORKSPACE_WARN.search(output) is not None


def _is_endofline_only(output: str) -> bool:
    """True when every error line is an ENDOFLINE violation and nothing else
    looks like a real operational failure."""
    if "ENDOFLINE" not in output:
        return False
    for line in output.splitlines():
        if REAL_ERROR_LINE.search(line) and "ENDOFLINE" not in line:
            return False
        lower = line.lower()
        if any(kw in lower for kw in ("exception", "failed", "restore")):
            return False
    return True


def _extract_real_errors(output: str) -> list[str]:
    return [l.strip() for l in output.splitlines() if REAL_ERROR_LINE.search(l)]


def main() -> None:
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

        print(f"\n  {label}...", end="", flush=True)

        # Phase 1: whitespace + style formatting passes.
        # Each invocation is classified independently.  ENDOFLINE-only
        # whitespace noise is ignored in check mode; everything else persists.
        style_failed = False
        for sub in SUBCOMMANDS:
            args = ["dotnet", "format", sub, str(ROOT / target)]
            if not FIX:
                args.append("--verify-no-changes")
            for ex in EXCLUDES:
                args.extend(["--exclude", ex])

            result = _run_dotnet(args)
            output = f"{result.stdout}\n{result.stderr}"

            # Workspace warning with exit 0: fail closed immediately.
            if result.returncode == 0 and _has_workspace_warning(output):
                print(" FAIL")
                for line in output.splitlines():
                    if WORKSPACE_WARN.search(line):
                        print(f"    {line.strip()}")
                failed += 1
                style_failed = True
                break

            if result.returncode != 0:
                # Missing MAUI workload on a best-effort target: skip
                # gracefully.  Only NETSDK1147 triggers this; the invocation
                # must have actually failed.
                if needs_workload and WORKLOAD_ERROR.search(output):
                    print(" SKIP (required MAUI workload not installed)")
                    style_failed = True
                    break

                # ENDOFLINE-only whitespace pass: ignore in check mode and
                # continue to the next pass (style, then targeted IDE0005).
                if sub == "whitespace" and not FIX and _is_endofline_only(output):
                    continue

                # Any other failure is real.
                real = _extract_real_errors(output)
                print(" FAIL")
                for line in (real or [output.strip()]):
                    print(f"    {line}")
                failed += 1
                style_failed = True
                break

        if style_failed:
            continue

        # Phase 2: targeted IDE0005 style pass.
        # This always runs (even after ENDOFLINE-only whitespace noise)
        # so the semantic check is never skipped.
        if TARGETED_STYLE_DIAGNOSTICS:
            args = [
                "dotnet", "format", "style", str(ROOT / target),
                "--diagnostics", *TARGETED_STYLE_DIAGNOSTICS,
                "--severity", "hidden",
            ]
            if not FIX:
                args.append("--verify-no-changes")
            for ex in EXCLUDES:
                args.extend(["--exclude", ex])

            result = _run_dotnet(args)
            output = f"{result.stdout}\n{result.stderr}"

            if result.returncode == 0 and _has_workspace_warning(output):
                print(" FAIL")
                for line in output.splitlines():
                    if WORKSPACE_WARN.search(line):
                        print(f"    {line.strip()}")
                failed += 1
                continue

            if result.returncode != 0:
                if needs_workload and WORKLOAD_ERROR.search(output):
                    print(" SKIP (required MAUI workload not installed)")
                    continue

                real = _extract_real_errors(output)
                print(" FAIL")
                for line in (real or [output.strip()]):
                    print(f"    {line}")
                failed += 1
                continue

        print(" OK")

    print()
    if failed > 0:
        print(f"=== {failed} target(s) have formatting violations ===")
        print("Run: ./scripts/ci/format-check.py --fix")
        sys.exit(1)
    else:
        print("=== All projects formatted correctly ===")


if __name__ == "__main__":
    main()
