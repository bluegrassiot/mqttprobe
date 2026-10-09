#!/usr/bin/env python3
"""
Checks (or fixes) code formatting.

On Windows, the full solution (MqttProbe.slnx) is used; MAUI workloads must be
installed separately. On non-Windows (CI / Linux / macOS), non-MAUI projects
are checked through MqttProbe.NoMaui.slnf and the MAUI project is checked
best-effort only when its workloads are present.

LucideIcons.cs and SparkplugBProtobuf.cs are always excluded (column-aligned
constants and auto-generated protobuf code respectively).

ENDOFLINE-only violations are ignored in check mode: .editorconfig mandates LF
but git (text=auto) checks out CRLF on Windows, so every file reports
ENDOFLINE locally. CI normalizes line endings before checking and is
unaffected. The ENDOFLINE bypass applies only to the whitespace pass result and
never suppresses the style or targeted IDE0005 passes.

Usage:
  ./scripts/ci/format-check.py                  # full-tree check
  ./scripts/ci/format-check.py --changed        # changed source files
  ./scripts/ci/format-check.py --staged         # staged source paths, current disk contents
  ./scripts/ci/format-check.py --fix            # auto-fix selected scope
"""

import argparse
import os
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

SUBCOMMANDS = ["whitespace", "style"]

# IDE0005 defaults to hidden without an explicit .editorconfig severity. Keep
# this focused pass separate so ordinary style fixes retain their existing scope.
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

SOURCE_SUFFIXES = {".cs", ".razor", ".cshtml"}
PROJECT_SUFFIXES = {".csproj", ".fsproj", ".vbproj", ".proj"}
WORKSPACE_FILENAMES = {
    ".editorconfig",
    "directory.build.props",
    "directory.build.targets",
    "directory.build.rsp",
    "directory.packages.props",
    "directory.solution.props",
    "directory.solution.targets",
    "dotnet-tools.json",
    "global.json",
    "msbuild.rsp",
    "nuget.config",
    "packages.lock.json",
}


def _git(*args: str) -> bytes:
    result = subprocess.run(
        ["git", "-C", str(ROOT), *args], capture_output=True, check=False,
    )
    if result.returncode != 0:
        error = os.fsdecode(result.stderr).strip() or f"git {' '.join(args)} failed"
        raise RuntimeError(error)
    return result.stdout


def _parse_git_paths(data: bytes) -> list[tuple[str, bool]]:
    """Parse `git diff --name-status -z`; bool marks the current-side path."""
    fields = [os.fsdecode(field) for field in data.split(b"\0") if field]
    paths: list[tuple[str, bool]] = []
    index = 0
    while index < len(fields):
        status = fields[index]
        index += 1
        path_count = 2 if status.startswith(("R", "C")) else 1
        if len(fields) - index < path_count:
            raise RuntimeError("git returned an incomplete NUL-delimited path list")
        changed = fields[index:index + path_count]
        index += path_count
        if path_count == 2:
            paths.extend(((changed[0], False), (changed[1], True)))
        else:
            paths.append((changed[0], status[0] != "D"))
    return paths


def _selected_git_paths(selection: str) -> list[tuple[str, bool]]:
    if not _git("rev-parse", "--show-toplevel"):
        raise RuntimeError("git did not return a repository root")

    if selection == "staged":
        return _parse_git_paths(_git(
            "diff", "--cached", "--name-status", "-z", "--find-renames",
        ))

    head = subprocess.run(
        ["git", "-C", str(ROOT), "rev-parse", "--verify", "HEAD"],
        capture_output=True, check=False,
    )
    if head.returncode == 0:
        records = _parse_git_paths(_git(
            "diff", "HEAD", "--name-status", "-z", "--find-renames",
        ))
    else:
        # A symbolic HEAD with no commit is an unborn branch. Other failures
        # are not interpreted as an empty change set.
        symbolic_head = subprocess.run(
            ["git", "-C", str(ROOT), "symbolic-ref", "-q", "HEAD"],
            capture_output=True, check=False,
        )
        if symbolic_head.returncode != 0:
            error = os.fsdecode(head.stderr).strip() or "could not resolve HEAD"
            raise RuntimeError(error)
        records = _parse_git_paths(_git(
            "diff", "--cached", "--name-status", "-z", "--find-renames",
        ))
        records.extend(_parse_git_paths(_git(
            "diff", "--name-status", "-z", "--find-renames",
        )))

    untracked = _git("ls-files", "--others", "--exclude-standard", "-z")
    records.extend((os.fsdecode(path), True) for path in untracked.split(b"\0") if path)
    return records


def _is_workspace_input(path: str) -> bool:
    normalized = path.replace("\\", "/")
    name = normalized.rsplit("/", 1)[-1].lower()
    suffix = Path(name).suffix
    return (
        name in WORKSPACE_FILENAMES
        or name.endswith(".sln")
        or name.endswith(".slnx")
        or name.endswith(".slnf")
        or suffix in PROJECT_SUFFIXES
        or suffix in {".props", ".targets"}
        or name.startswith("directory.build.")
        or name.startswith("directory.solution.")
    )


def _select_files(selection: str | None) -> tuple[bool, list[Path]]:
    """Return (full_tree, current source paths) for the requested scope."""
    if selection is None:
        return True, []

    records = _selected_git_paths(selection)
    if any(_is_workspace_input(path) for path, _ in records):
        return True, []

    includes = {
        (ROOT / path).resolve()
        for path, current in records
        if current and Path(path).suffix.lower() in SOURCE_SUFFIXES
        and (ROOT / path).is_file()
    }
    return False, sorted(includes, key=lambda path: os.fspath(path).casefold())


def _parse_args(argv: list[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Check or fix repository formatting.")
    scope = parser.add_mutually_exclusive_group()
    scope.add_argument("--changed", action="store_true", help="format changed and untracked source files")
    scope.add_argument("--staged", action="store_true", help="format staged source filenames using current disk content")
    parser.add_argument("--fix", action="store_true", help="apply formatting changes")
    return parser.parse_args(argv)


def _run_dotnet(
    args: list[str], timeout: int | None = None, cwd: Path | None = None,
) -> subprocess.CompletedProcess:  # type: ignore[type-arg]
    return subprocess.run(args, capture_output=True, text=True, timeout=timeout, cwd=cwd)


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


def main(argv: list[str] | None = None) -> None:
    options = _parse_args(argv)
    selection = "changed" if options.changed else "staged" if options.staged else None
    try:
        full_tree, includes = _select_files(selection)
    except RuntimeError as exc:
        print(f"Git change discovery failed: {exc}", file=sys.stderr)
        sys.exit(1)

    failed = 0

    print()
    if options.fix:
        print("=== Format Fix ===")
        print("Applying formatting changes...")
    else:
        print("=== Format Check ===")
        print("Run with --fix to apply changes.")

    if selection is None:
        print("Scope: full tree")
    elif full_tree:
        print("Scope: full tree (workspace or policy input changed)")
    else:
        print(f"Scope: {selection} source files ({len(includes)} selected)")

    if not full_tree and not includes:
        print("\nNo format-relevant files selected.")
        print("Nothing to format.")
        return

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

        # Whitespace + style formatting passes.
        # Each invocation is classified independently.  ENDOFLINE-only
        # whitespace noise is ignored in check mode; everything else persists.
        style_failed = False
        for sub in SUBCOMMANDS:
            args = ["dotnet", "format", sub, str(ROOT / target)]
            if not full_tree:
                # dotnet format resolves --include paths relative to its process
                # working directory, even when the workspace argument is absolute.
                args.extend(["--include", *(str(path.relative_to(ROOT)) for path in includes)])
            if not options.fix:
                args.append("--verify-no-changes")
            if skip_restore:
                args.append("--no-restore")
            for ex in EXCLUDES:
                args.extend(["--exclude", ex])

            result = _run_dotnet(args, cwd=ROOT if not full_tree else None)
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
                # continue to the style pass.
                if sub == "whitespace" and not options.fix and _is_endofline_only(output):
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

        # IDE0005 is hidden by default, so the normal style pass does not
        # enforce unused using directives. Preserve the selected file scope.
        args = [
            "dotnet", "format", "style", str(ROOT / target),
            "--diagnostics", *TARGETED_STYLE_DIAGNOSTICS,
            "--severity", "hidden",
        ]
        if not full_tree:
            args.extend(["--include", *(str(path.relative_to(ROOT)) for path in includes)])
        if not options.fix:
            args.append("--verify-no-changes")
        if skip_restore:
            args.append("--no-restore")
        for ex in EXCLUDES:
            args.extend(["--exclude", ex])

        result = _run_dotnet(args, cwd=ROOT if not full_tree else None)
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
