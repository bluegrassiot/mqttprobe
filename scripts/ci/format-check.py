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

# A missing MAUI workload surfaces as NETSDK1147 in the restore output.
# Only this exact SDK error code triggers the skip for best-effort targets.
# Generic messages like "Restore operation failed" also appear for transient
# NuGet failures, broken project files, or dotnet format's own wrapper
# exception, so they must not trigger a skip.
WORKLOAD_ERROR = re.compile(r"NETSDK1147", re.IGNORECASE)
REAL_ERROR_LINE = re.compile(r"error (?!ENDOFLINE)\w+:")

# Restore-specific classifier: matches "error CODE:" or "error :" (bare).
# Captures the diagnostic code (letters+digits) when present; bare "error:"
# or "error :" yields an empty capture.  Used only by _is_netsdk1147_only;
# REAL_ERROR_LINE and ENDOFLINE handling are untouched.
RESTORE_ERROR_LINE = re.compile(r"\berror(?:\s+([A-Za-z]+\d+))?\s*:", re.IGNORECASE)

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


def _run_dotnet(args: list[str], timeout: int | None = None) -> subprocess.CompletedProcess:  # type: ignore[type-arg]
    return subprocess.run(args, capture_output=True, text=True, timeout=timeout)


def _try_restore(target: str, timeout: int = 120) -> tuple[bool, str]:
    """Explicitly restore a target before format passes.

    Returns (success, combined_output).  On timeout the output is "timeout".
    """
    args = ["dotnet", "restore", str(ROOT / target), "--verbosity", "minimal"]
    try:
        result = _run_dotnet(args, timeout=timeout)
        output = f"{result.stdout}\n{result.stderr}"
        return result.returncode == 0, output
    except subprocess.TimeoutExpired:
        return False, "timeout"


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


def _is_netsdk1147_only(output: str) -> bool:
    """True when NETSDK1147 is present and every error diagnostic is NETSDK1147.

    Uses RESTORE_ERROR_LINE to classify each "error" line.  A bare "error:"
    or "error :" (no diagnostic code) alongside NETSDK1147 is fatal: the
    output is not solely a workload-missing signal.
    """
    matches = RESTORE_ERROR_LINE.findall(output)
    if not matches:
        return False
    # Every match must have a non-empty code that equals NETSDK1147.
    return all(code and code.upper() == "NETSDK1147" for code in matches)


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

        # For best-effort targets, restore explicitly once so we can inspect
        # the real error output instead of dotnet format's wrapper exception.
        skip_restore = False
        if needs_workload:
            restored, restore_output = _try_restore(target)
            if not restored:
                if _is_netsdk1147_only(restore_output):
                    print(" SKIP (required MAUI workload not installed)")
                    continue

                # Fail-closed: restore failed with unclassified error
                # (NuGet, MSBuild, timeout, or generic wrapper).
                real = _extract_real_errors(restore_output)
                print(" FAIL (restore)")
                for line in (real or [restore_output.strip()]):
                    print(f"    {line}")
                failed += 1
                continue

            skip_restore = True

        # Phase 1: whitespace + style formatting passes.
        # Each invocation is classified independently.  ENDOFLINE-only
        # whitespace noise is ignored in check mode; everything else persists.
        style_failed = False
        for sub in SUBCOMMANDS:
            args = ["dotnet", "format", sub, str(ROOT / target)]
            if not FIX:
                args.append("--verify-no-changes")
            if skip_restore:
                args.append("--no-restore")
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
            if skip_restore:
                args.append("--no-restore")
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
